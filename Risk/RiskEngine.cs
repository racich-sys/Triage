using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

internal static class RiskEngine
{
    public static RiskEngineConfig GetConfig(string dbPath)
    {
        return LoadConfiguration(dbPath, _ => { });
    }

    public static RiskRunResult Run(string dbPath, HashSet<string>? selectedRuleCodes, Action<string> log)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("SQLite database not found.", dbPath);

        DatabaseCore.InitializeDatabase(dbPath);
        var config = LoadConfiguration(dbPath, log);

        if (selectedRuleCodes != null && selectedRuleCodes.Count > 0)
        {
            foreach (var kvp in config.Rules)
            {
                if (!selectedRuleCodes.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    kvp.Value.Enabled = false;
                }
            }
        }

        SqliteConnection.ClearAllPools();
        using var conn = ExecuteWithSqliteRetry(() => DatabaseCore.Open(dbPath), log, "opening risk database");
        ExecuteWithSqliteRetry(() => ClearRiskState(conn), log, "clearing prior risk state");

        var events = ExecuteWithSqliteRetry(() => LoadEvents(conn), log, "loading events for risk analysis");
        var hits = new List<RiskHit>();

        log($"Loaded {events.Count:N0} events for risk analysis");
        log($"Risk profile: {config.Name} (v{config.Version})");

        var userIpBaselines = BuildUserIpBaselines(events, config);
        var userDownloadBursts = BuildOperationBursts(events, new[] { "FileDownloaded" });
        var userMailboxBursts = BuildOperationBursts(events, new[] { "MailItemsAccessed" });
        var userDeleteBursts = BuildOperationBursts(events, new[] { "MoveToDeletedItems", "HardDelete", "FileDeleted", "File_Delete" });

        foreach (var ev in events)
        {
            var recipients = FirstNonBlank(ev.EmailTo, ev.Recipients);
            var behaviorBucket = ev.IsBehavioralTimestamp ? Bucket30(ev.CreationDateUtc) : string.Empty;
            var downloadBucket = !string.IsNullOrEmpty(behaviorBucket) && userDownloadBursts.TryGetValue((ev.UserId, behaviorBucket), out var dlCount) ? dlCount : 0;
            var mailboxBucket = !string.IsNullOrEmpty(behaviorBucket) && userMailboxBursts.TryGetValue((ev.UserId, behaviorBucket), out var mailCount) ? mailCount : 0;
            var deleteBucket = !string.IsNullOrEmpty(behaviorBucket) && userDeleteBursts.TryGetValue((ev.UserId, behaviorBucket), out var delCount) ? delCount : 0;

            AddIfRule(hits, config, "EXF-001", ev, ContainsPersonalDomain(config, recipients), "Recipient list contains a personal email domain", recipients);
            AddIfRule(hits, config, "EXF-002", ev, ContainsPersonalDomain(config, recipients) && !string.IsNullOrWhiteSpace(FirstNonBlank(ev.AttachmentDetails, ev.AttachmentsExpanded)), "Personal email recipient with attachment-related fields populated", FirstNonBlank(ev.AttachmentDetails, ev.AttachmentsExpanded));
            AddIfRule(hits, config, "EXF-010", ev, ev.Operation.Equals("FileDownloaded", StringComparison.OrdinalIgnoreCase), "FileDownloaded event observed", BestFileIndicator(ev));
            AddIfRule(hits, config, "EXF-012", ev, downloadBucket >= config.Thresholds.MassDownloadBurst30Min, $"{config.Thresholds.MassDownloadBurst30Min}+ FileDownloaded events in 30-minute window", downloadBucket.ToString());
            AddIfRule(hits, config, "EXF-011", ev, downloadBucket >= config.Thresholds.DownloadBurst30Min && downloadBucket < config.Thresholds.MassDownloadBurst30Min, $"{config.Thresholds.DownloadBurst30Min}+ FileDownloaded events in 30-minute window", downloadBucket.ToString());
            AddIfRule(hits, config, "EXF-013", ev, !string.IsNullOrWhiteSpace(ev.ZipFileName) && LooksLikeDownloadOrExportOperation(ev.Operation), "ZipFileName present on a download/export event; likely bundled export target", ev.ZipFileName);
            AddIfRule(hits, config, "EXF-014", ev, ev.IsBehavioralTimestamp && ev.Operation.Equals("FileDownloaded", StringComparison.OrdinalIgnoreCase) && IsAfterHours(ev.CreationDateLocal, config) && downloadBucket >= config.Thresholds.AfterHoursDownloadBurst30Min, $"{config.Thresholds.AfterHoursDownloadBurst30Min}+ downloads outside normal business hours", downloadBucket.ToString());
            AddIfRule(hits, config, "EXF-020", ev, ev.Operation.Equals("MailItemsAccessed", StringComparison.OrdinalIgnoreCase), "MailItemsAccessed event observed", ev.ObjectId);
            AddIfRule(hits, config, "EXF-021", ev, mailboxBucket >= config.Thresholds.MailboxBurst30Min, $"{config.Thresholds.MailboxBurst30Min}+ MailItemsAccessed events in 30-minute window", mailboxBucket.ToString());
            AddIfRule(hits, config, "EXF-022", ev, ev.Operation.Equals("MailItemsAccessed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ev.ClientIp) && userIpBaselines.TryGetValue(ev.UserId, out var knownIps) && knownIps.Count > 0 && !knownIps.Contains(ev.ClientIp), "Mailbox access from non-baseline IP", ev.ClientIp);
            AddIfRule(hits, config, "EXF-030", ev, IsExternalSharingOperation(ev.Operation), "Sharing or link-creation operation detected", ev.Operation);
            AddIfRule(hits, config, "EXF-040", ev, ev.Operation.Contains("Print", StringComparison.OrdinalIgnoreCase), "Print-like operation detected", ev.Operation);
            var sensitiveIndicator = BestSensitiveIndicator(config, ev);
            AddIfRule(hits, config, "EXF-052", ev,
                !IsDeletionLikeOperation(ev.Operation) &&
                LooksLikeAccessOrMovementOperation(ev.Operation) &&
                !string.IsNullOrWhiteSpace(sensitiveIndicator),
                "Filename/path/object/subject contains sensitive keyword on an access, movement, or sharing event",
                sensitiveIndicator);

            var crossUser = LooksLikeCrossUserPersonalSiteAccess(ev, out var crossUserTarget);
            AddIfRule(hits, config, "ACC-010", ev, crossUser, "User accessed a personal OneDrive / SharePoint location that does not appear to belong to the acting user", crossUserTarget);

            AddIfRule(hits, config, "CON-060", ev, ev.Operation.Equals("HardDelete", StringComparison.OrdinalIgnoreCase), "HardDelete operation observed", ev.ObjectId);
            AddIfRule(hits, config, "CON-061", ev, deleteBucket >= config.Thresholds.DeletionBurst30Min, $"{config.Thresholds.DeletionBurst30Min}+ delete-type operations in 30-minute window", deleteBucket.ToString());
            AddIfRule(hits, config, "CON-062", ev, ev.DataSource == "RecycleBin", "Local file deleted (Sent to Recycle Bin)", ev.ObjectId);
            AddIfRule(hits, config, "CON-063", ev, ev.DataSource == "USN_Journal" && ev.Operation.Equals("File_Delete", StringComparison.OrdinalIgnoreCase), "USN Journal records a file delete operation", BestFileIndicator(ev));

            bool isAntiForensic = (ev.DataSource == "Prefetch" || ev.DataSource == "Registry_UserAssist") && ContainsKeyword(config.AntiForensicTools, ev.ObjectId);
            AddIfRule(hits, config, "CON-070", ev, isAntiForensic, "Execution of known anti-forensic or wiping tool detected", ev.ObjectId);

            var driveType = FirstNonBlank(ev.DriveType, ExtractFieldFromJson(ev.RawJson, "DriveType"));
            AddIfRule(hits, config, "EXF-060", ev, (ev.DataSource == "LNK_File" || ev.DataSource == "JumpList" || ev.DataSource == "RecycleBin" || ev.DataSource == "ShellBags") && driveType.Equals("Removable/Secondary", StringComparison.OrdinalIgnoreCase), "Artifact indicates file interaction on a removable or secondary drive.", ev.ObjectId);
            AddIfRule(hits, config, "EXF-061", ev, (ev.DataSource == "LNK_File" || ev.DataSource == "JumpList" || ev.DataSource == "RecycleBin" || ev.DataSource == "ShellBags") && driveType.Equals("Network", StringComparison.OrdinalIgnoreCase), "Artifact indicates file interaction on a network drive.", ev.ObjectId);
            AddIfRule(hits, config, "EXF-070", ev, ev.Operation == "USB_Device_Connected" || ev.Operation == "Drive_Mounted_By_User", "USB mass storage device was plugged into the system.", ev.ObjectId);
            AddIfRule(hits, config, "EXF-071", ev, ev.DataSource == "SetupAPI_DeviceLog" && ev.Operation == "USB_Device_FirstInstall", "SetupAPI device log records USB storage first-install/driver-install evidence.", FirstNonBlank(ev.ObjectId, ev.RawJson));
            var setupApiRiskRelevance = FirstNonBlank(ExtractFieldFromJson(ev.RawJson, "SetupApiRiskRelevance"), ExtractFieldFromJson(ev.RawJson, "SetupApiDeviceCategory"), ev.Operation);
            var setupApiRiskText = FirstNonBlank(ev.ObjectId, ExtractFieldFromJson(ev.RawJson, "DeviceDescription"), ExtractFieldFromJson(ev.RawJson, "SetupApiRiskReason"), ev.RawJson);
            AddIfRule(hits, config, "EXF-072", ev, (ev.DataSource == "SetupAPI_DeviceLog" || ev.DataSource == "SetupAPI_AppLog") && setupApiRiskRelevance.Contains("Transfer", StringComparison.OrdinalIgnoreCase), "SetupAPI records transfer-capable device, interface, driver, or device-associated application installation evidence.", setupApiRiskText);
            AddIfRule(hits, config, "CON-071", ev, (ev.DataSource == "SetupAPI_DeviceLog" || ev.DataSource == "SetupAPI_AppLog") && setupApiRiskRelevance.Contains("Destruction", StringComparison.OrdinalIgnoreCase), "SetupAPI records possible destructive, wiping, formatting, or anti-forensic driver/application installation evidence.", setupApiRiskText);

            bool isCloudVisit = ev.DataSource == "Browser_History" && (ContainsKeyword(config.CloudStorageDomains, ev.ObjectId) || ExtractFieldFromJson(ev.RawJson, "IsCloudStorageUrl").Equals("Yes", StringComparison.OrdinalIgnoreCase));
            AddIfRule(hits, config, "EXF-080", ev, isCloudVisit, "Browser history indicates a visit to a personal cloud storage provider.", ev.ObjectId);

            bool isPersonalEmailWebmail = ev.DataSource == "Browser_History" && (ev.Operation.Contains("Personal_Email", StringComparison.OrdinalIgnoreCase) || ExtractFieldFromJson(ev.RawJson, "IsPersonalEmailUrl").Equals("Yes", StringComparison.OrdinalIgnoreCase) || ContainsPersonalDomain(config, ev.ObjectId));
            AddIfRule(hits, config, "EXF-085", ev, isPersonalEmailWebmail, "Browser history indicates access to personal webmail.", ev.ObjectId);

            bool isLocalBrowserFileAccess = ev.DataSource == "Browser_History" && (ev.Operation.Contains("Local_File", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("Localhost", StringComparison.OrdinalIgnoreCase) || ExtractFieldFromJson(ev.RawJson, "IsFileExplorerLocalAccess").Equals("Yes", StringComparison.OrdinalIgnoreCase));
            AddIfRule(hits, config, "ACC-020", ev, isLocalBrowserFileAccess, "Browser/history artifact indicates local file or localhost access, commonly seen when files are opened through Explorer or a browser control.", ev.ObjectId);

            var browserDownloadSource = FirstNonBlank(ExtractFieldFromJson(ev.RawJson, "SourceUrl"), ExtractFieldFromJson(ev.RawJson, "SourceUrlChain"), ev.ObjectId);
            AddIfRule(hits, config, "EXF-081", ev, ev.DataSource == "Browser_Downloads" && ContainsKeyword(config.CloudStorageDomains, browserDownloadSource), "Browser download source references a personal cloud storage provider.", browserDownloadSource);

            AddIfRule(hits, config, "EXF-082", ev, ev.DataSource == "SRUM" && ContainsKeyword(config.CloudStorageDomains, ev.ObjectId), "SRUM best-effort network indicator references a personal cloud storage provider.", ev.ObjectId);

            var cloudSyncText = FirstNonBlank(ev.ObjectId, ev.PathHint, ev.RawJson, ev.FileName);
            bool isDedicatedCloudSyncArtifact = ev.DataSource == "OneDrive" || ev.DataSource == "Dropbox" || ev.DataSource == "GoogleDrive" || ev.DataSource == "Registry_OneDrive" || ev.Operation.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase);
            AddIfRule(hits, config, "EXF-083", ev, isDedicatedCloudSyncArtifact, "Cloud sync account, root, metadata, or log artifact observed.", cloudSyncText);

            AddIfRule(hits, config, "EXF-084", ev, ev.DataSource == "Office_Activity" && (ev.Operation.Contains("Cloud", StringComparison.OrdinalIgnoreCase) || ContainsKeyword(config.CloudStorageDomains, FirstNonBlank(ev.ObjectId, ev.PathHint, ev.RawJson))), "Office MRU/Backstage item references a cloud document or cloud URL.", FirstNonBlank(ev.ObjectId, ev.PathHint));

            AddIfRule(hits, config, "EXF-086", ev, ev.DataSource == "Office_OAlerts" && (ev.Operation.Contains("Saved", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("Edited", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("Cloud", StringComparison.OrdinalIgnoreCase)), "OAlerts event log indicates Office file save/edit/cloud activity.", FirstNonBlank(ev.ObjectId, ev.FileName, ev.RawJson));

            var executionText = FirstNonBlank(ev.ObjectId, ev.FileName, ev.PathHint, ev.RawJson);
            var executionOrCommand = (ev.DataSource == "Prefetch" || ev.DataSource == "Registry_UserAssist" || ev.DataSource == "Registry_BAM" || ev.DataSource == "Registry_DAM" || ev.DataSource == "AmCache" || ev.DataSource == "PowerShell_History" || ev.DataSource == "PowerShell_Transcript" || (ev.DataSource == "WinEventLog" && ev.Operation.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))) && ContainsKeyword(config.TransferTools, executionText);
            AddIfRule(hits, config, "EXF-090", ev, executionOrCommand, "Transfer, sync, or command-line data movement tool observed.", executionText);

            var googleText = FirstNonBlank(ev.ObjectId, ev.SourceRelativeUrl, ev.PathHint, ev.FileName, ev.EmailSubject, ev.RawJson, ev.Operation);
            var isGoogleSource = ev.DataSource.StartsWith("Google", StringComparison.OrdinalIgnoreCase) || ev.DataSource.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
            var isGoogleDrive = isGoogleSource && (ev.DataSource.Contains("Drive", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase));
            var isGoogleGmail = isGoogleSource && (ev.DataSource.Contains("Gmail", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleGmail", StringComparison.OrdinalIgnoreCase) || ev.Operation.Equals("MailItemsAccessed", StringComparison.OrdinalIgnoreCase));
            var isGoogleOAuth = isGoogleSource && (ev.DataSource.Contains("OAuth", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleOAuth", StringComparison.OrdinalIgnoreCase));
            var isGoogleTakeout = isGoogleSource && (ev.DataSource.Contains("Takeout", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleTakeout", StringComparison.OrdinalIgnoreCase));
            var isGoogleGemini = isGoogleSource && (ev.DataSource.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleGemini", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GeminiSession", StringComparison.OrdinalIgnoreCase));
            var isGoogleVaultOrAdmin = isGoogleSource && (ev.DataSource.Contains("Vault", StringComparison.OrdinalIgnoreCase) || ev.DataSource.Contains("Admin", StringComparison.OrdinalIgnoreCase) || ev.Operation.Contains("GoogleVault", StringComparison.OrdinalIgnoreCase));
            AddIfRule(hits, config, "EXF-091", ev, isGoogleTakeout && ContainsAny(googleText, "request", "download", "created", "completed", "export", "destination", "takeout"), "Google Takeout request/export/download-related evidence observed.", googleText);
            AddIfRule(hits, config, "EXF-092", ev, isGoogleDrive && ContainsAny(googleText, "download", "export", "share", "visibility", "external", "link", "copy"), "Google Drive download/export/share/visibility/copy event observed.", googleText);
            AddIfRule(hits, config, "EXF-093", ev, isGoogleGmail && ContainsAny(googleText, "attachment", "forward", "delegate", "routing", "send", "download", "access"), "Google Gmail access/attachment/forwarding/delegation/routing event observed.", googleText);
            AddIfRule(hits, config, "EXF-094", ev, isGoogleOAuth && ContainsAny(googleText, "drive", "gmail", "calendar", "contacts", "scope", "api", "token"), "Google OAuth/API grant or activity involving data-bearing services observed.", googleText);
            AddIfRule(hits, config, "ACC-031", ev, isGoogleSource && (ev.DataSource.Contains("User", StringComparison.OrdinalIgnoreCase) || ev.DataSource.Contains("Device", StringComparison.OrdinalIgnoreCase)) && ContainsAny(googleText, "suspicious", "failed", "new device", "challenge", "login", "compromised"), "Google login/device/user event relevant to access review observed.", googleText);
            AddIfRule(hits, config, "CON-082", ev, isGoogleVaultOrAdmin && ContainsAny(googleText, "vault", "hold", "retention", "delete", "remove", "purge", "suspend", "reset", "wipe", "rule", "setting"), "Google Vault/Admin control event relevant to preservation, deletion, or account-control review observed.", googleText);
            AddIfRule(hits, config, "AI-011", ev, isGoogleGemini && (ContainsSensitiveKeyword(config, googleText) || ContainsAny(googleText, "code", "generate", "summarize", "file", "document", "prompt", "transcript")), "Google Gemini/AI artifact or audit event requiring sensitive-use review.", googleText);

        }

        hits.AddRange(DetectSequences(events, config));
        hits = DeduplicateHits(hits);

        ExecuteWithSqliteRetry(() => PersistHits(conn, hits), log, "persisting risk hits");
        ExecuteWithSqliteRetry(() => UpdateEventScores(conn, config), log, "updating event risk scores");

        var summary = ExecuteWithSqliteRetry(() => GetRiskSummary(conn), log, "summarizing risk hits");
        log($"Risk hits written: {summary.TotalHits:N0}");
        return summary;
    }

    private static T ExecuteWithSqliteRetry<T>(Func<T> operation, Action<string> log, string label)
    {
        const int maxAttempts = 30;
        var delayMs = 250;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (SqliteException ex) when (IsSqliteBusyOrLocked(ex) && attempt < maxAttempts)
            {
                log($"Risk engine waiting for SQLite lock while {label} (attempt {attempt:N0}/{maxAttempts:N0}).");
                System.Threading.Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs + 250, 2000);
                SqliteConnection.ClearAllPools();
            }
        }
    }

    private static void ExecuteWithSqliteRetry(Action operation, Action<string> log, string label)
    {
        ExecuteWithSqliteRetry(() =>
        {
            operation();
            return true;
        }, log, label);
    }

    private static bool IsSqliteBusyOrLocked(SqliteException ex)
    {
        return ex.SqliteErrorCode == 5 ||
               ex.SqliteErrorCode == 6 ||
               ex.SqliteExtendedErrorCode == 5 ||
               ex.SqliteExtendedErrorCode == 6;
    }

    private static string ExtractFieldFromJson(string rawJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "";
        var match = Regex.Match(rawJson, $"\"{fieldName}\":\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static RiskEngineConfig LoadConfiguration(string dbPath, Action<string> log)
    {
        var config = CreateDefaultConfiguration();
        var candidates = new[]
        {
            Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "risk_rules.json"),
            Path.Combine(AppContext.BaseDirectory, "risk_rules.json")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<RiskEngineConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (loaded != null) { MergeConfiguration(config, loaded); log($"Loaded risk_rules.json: {path}"); return config; }
            }
            catch (Exception ex) { log($"Warning: failed to load risk_rules.json from {path}: {ex.Message}"); }
        }
        return config;
    }

    private static RiskEngineConfig CreateDefaultConfiguration()
    {
        var config = new RiskEngineConfig
        {
            Version = 2,
            Name = "Default Risk Profile (Endpoint Enabled)",
            PersonalDomains = new() { "gmail.com", "mail.google.com", "yahoo.com", "mail.yahoo.com", "outlook.com", "outlook.live.com", "hotmail.com", "icloud.com", "mail.icloud.com", "aol.com", "mail.aol.com", "proton.me", "mail.proton.me", "protonmail.com", "fastmail.com" },
            SensitiveKeywords = new() { "confidential", "trade secret", "proprietary", "source code", "pricing", "customer", "client list", "roadmap", "patent", "formula", "design", "finance", "acquisition", "strategy", "board", "payroll", "hr", "intellectual property" },
            AntiForensicTools = new() { "ccleaner", "sdelete", "eraser", "cipher.exe", "vssadmin", "bleachbit", "wipe" },
            TransferTools = new() { "rclone", "winscp", "filezilla", "psftp", "putty", "scp", "sftp", "ftp.exe", "azcopy", "megasync", "dropbox", "googledrivesync", "onedrive", "curl.exe", "wget.exe" },
            CloudStorageDomains = new() { "dropbox.com", "dropboxusercontent.com", "mega.nz", "mega.co.nz", "drive.google.com", "docs.google.com", "wetransfer.com", "box.com", "app.box.com", "onedrive.live.com", "1drv.ms", "sharepoint.com", "icloud.com", "pcloud.com", "mediafire.com" }
        };

        config.Rules["EXF-001"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 90, Description = "Personal email recipient present." };
        config.Rules["EXF-002"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 95, Description = "Personal email recipient plus attachment indicators." };
        config.Rules["EXF-010"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 20, Description = "Single file download observed." };
        config.Rules["EXF-011"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 60, Description = "Download burst in 30 minutes." };
        config.Rules["EXF-012"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 80, Description = "Mass download burst in 30 minutes." };
        config.Rules["EXF-013"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 85, Description = "ZIP package name present on a download/export event." };
        config.Rules["EXF-014"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 75, Description = "After-hours download burst." };
        config.Rules["EXF-020"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 20, Description = "Mailbox item access observed." };
        config.Rules["EXF-021"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 65, Description = "Bulk mailbox item access in 30 minutes." };
        config.Rules["EXF-022"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 80, Description = "Mailbox access from unusual IP." };
        config.Rules["EXF-030"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 80, Description = "External or anonymous sharing activity." };
        config.Rules["EXF-040"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 35, Description = "Print-like activity." };
        config.Rules["EXF-052"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 75, Description = "Sensitive keyword on access/movement/sharing event." };
        config.Rules["ACC-010"] = new() { Enabled = true, RiskDomain = "UNAUTHORIZED_ACCESS", Score = 80, Description = "Cross-user personal OneDrive / SharePoint access." };
        config.Rules["CON-060"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 95, Description = "Hard delete." };
        config.Rules["CON-061"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 70, Description = "Deletion burst in 30 minutes." };
        config.Rules["CON-062"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 15, Description = "Recycle Bin file deleted." };
        config.Rules["CON-063"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 35, Description = "USN Journal delete operation." };
        config.Rules["CON-070"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 100, Description = "Anti-forensic or wiping tool executed." };
        config.Rules["SEQ-001"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 95, Description = "Mailbox access followed by personal-email send within sequence window." };
        config.Rules["SEQ-002"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 100, Description = "Download followed by external sharing within sequence window." };
        config.Rules["SEQ-003"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 95, Description = "Download followed by deletion within sequence window." };
        config.Rules["SEQ-004"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 100, Description = "File access/download followed by cloud storage web visit." };
        config.Rules["EXF-060"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 85, Description = "LNK/JumpList/RecycleBin points to Removable Media." };
        config.Rules["EXF-061"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 45, Description = "LNK/JumpList/RecycleBin points to Network Drive." };
        config.Rules["EXF-070"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 75, Description = "USB Device Connected." };
        config.Rules["EXF-071"] = new() { Enabled = true, RiskDomain = "USB_ACTIVITY", Score = 65, Description = "SetupAPI USB storage first-install evidence." };
        config.Rules["EXF-072"] = new() { Enabled = true, RiskDomain = "USB_ACTIVITY", Score = 55, Description = "SetupAPI transfer-capable device/interface/driver evidence." };
        config.Rules["CON-071"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 70, Description = "SetupAPI possible destructive/wiping/formatting application or driver evidence." };
        config.Rules["EXF-080"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 70, Description = "Browser visit to personal cloud storage." };
        config.Rules["EXF-081"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 75, Description = "Browser download source indicates personal cloud storage." };
        config.Rules["EXF-082"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 55, Description = "SRUM network indicator references personal cloud storage." };
        config.Rules["EXF-083"] = new() { Enabled = true, RiskDomain = "CLOUD_SYNC_ACTIVITY", Score = 45, Description = "Cloud sync account/root/metadata/log artifact observed." };
        config.Rules["EXF-084"] = new() { Enabled = true, RiskDomain = "CLOUD_SYNC_ACTIVITY", Score = 55, Description = "Office MRU/Backstage item references a cloud document or cloud URL." };
        config.Rules["EXF-085"] = new() { Enabled = true, RiskDomain = "PERSONAL_EMAIL_ACTIVITY", Score = 55, Description = "Browser visit to personal webmail." };
        config.Rules["EXF-086"] = new() { Enabled = true, RiskDomain = "OFFICE_FILE_ACTIVITY", Score = 45, Description = "OAlerts Office file save/edit/cloud activity." };
        config.Rules["EXF-090"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 85, Description = "Transfer or sync tool execution/command observed." };
        config.Rules["EXF-091"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 85, Description = "Google Takeout request/export/download evidence." };
        config.Rules["EXF-092"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 75, Description = "Google Drive download/export/share/external visibility evidence." };
        config.Rules["EXF-093"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 60, Description = "Google Gmail access/attachment/forwarding/delegation evidence." };
        config.Rules["EXF-094"] = new() { Enabled = true, RiskDomain = "EXFILTRATION", Score = 70, Description = "Google OAuth/API access to data-bearing services." };
        config.Rules["ACC-031"] = new() { Enabled = true, RiskDomain = "UNAUTHORIZED_ACCESS", Score = 65, Description = "Google login/device/user access anomaly evidence." };
        config.Rules["CON-082"] = new() { Enabled = true, RiskDomain = "CONCEALMENT", Score = 70, Description = "Google Vault/Admin preservation, deletion, or account-control evidence." };
        config.Rules["AI-011"] = new() { Enabled = true, RiskDomain = "AI_ACTIVITY", Score = 45, Description = "Google Gemini/AI use involving sensitive terms, code, or document output." };
        config.Rules["ACC-020"] = new() { Enabled = true, RiskDomain = "LOCAL_FILE_ACCESS", Score = 20, Description = "Browser/history artifact indicates local file or localhost access." };

        return config;
    }

    private static void MergeConfiguration(RiskEngineConfig target, RiskEngineConfig loaded)
    {
        if (loaded.Version > 0) target.Version = loaded.Version;
        if (!string.IsNullOrWhiteSpace(loaded.Name)) target.Name = loaded.Name;
        if (loaded.ScoreThresholds is not null)
        {
            if (loaded.ScoreThresholds.CriticalMin > 0) target.ScoreThresholds.CriticalMin = loaded.ScoreThresholds.CriticalMin;
            if (loaded.ScoreThresholds.HighMin > 0) target.ScoreThresholds.HighMin = loaded.ScoreThresholds.HighMin;
            if (loaded.ScoreThresholds.MediumMin > 0) target.ScoreThresholds.MediumMin = loaded.ScoreThresholds.MediumMin;
        }
        if (loaded.BusinessHours is not null)
        {
            target.BusinessHours.StartHourLocal = loaded.BusinessHours.StartHourLocal;
            target.BusinessHours.EndHourLocal = loaded.BusinessHours.EndHourLocal;
        }
        if (loaded.Thresholds is not null)
        {
            if (loaded.Thresholds.DownloadBurst30Min > 0) target.Thresholds.DownloadBurst30Min = loaded.Thresholds.DownloadBurst30Min;
            if (loaded.Thresholds.MassDownloadBurst30Min > 0) target.Thresholds.MassDownloadBurst30Min = loaded.Thresholds.MassDownloadBurst30Min;
            if (loaded.Thresholds.MailboxBurst30Min > 0) target.Thresholds.MailboxBurst30Min = loaded.Thresholds.MailboxBurst30Min;
            if (loaded.Thresholds.DeletionBurst30Min > 0) target.Thresholds.DeletionBurst30Min = loaded.Thresholds.DeletionBurst30Min;
            if (loaded.Thresholds.AfterHoursDownloadBurst30Min > 0) target.Thresholds.AfterHoursDownloadBurst30Min = loaded.Thresholds.AfterHoursDownloadBurst30Min;
            if (loaded.Thresholds.SequenceWindowMinutes > 0) target.Thresholds.SequenceWindowMinutes = loaded.Thresholds.SequenceWindowMinutes;
            if (loaded.Thresholds.UserIpBaselineTopCount > 0) target.Thresholds.UserIpBaselineTopCount = loaded.Thresholds.UserIpBaselineTopCount;
        }
        if (loaded.PersonalDomains is { Count: > 0 }) target.PersonalDomains = loaded.PersonalDomains.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (loaded.SensitiveKeywords is { Count: > 0 }) target.SensitiveKeywords = loaded.SensitiveKeywords.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        
        if (loaded.AntiForensicTools is { Count: > 0 }) target.AntiForensicTools = loaded.AntiForensicTools.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (loaded.TransferTools is { Count: > 0 }) target.TransferTools = loaded.TransferTools.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (loaded.CloudStorageDomains is { Count: > 0 }) target.CloudStorageDomains = loaded.CloudStorageDomains.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (loaded.Rules is { Count: > 0 })
        {
            foreach (var kvp in loaded.Rules)
            {
                if (!target.Rules.TryGetValue(kvp.Key, out var existing)) { target.Rules[kvp.Key] = kvp.Value; continue; }
                existing.Enabled = kvp.Value.Enabled;
                if (!string.IsNullOrWhiteSpace(kvp.Value.RiskDomain)) existing.RiskDomain = kvp.Value.RiskDomain;
                if (kvp.Value.Score > 0) existing.Score = kvp.Value.Score;
                if (!string.IsNullOrWhiteSpace(kvp.Value.Description)) existing.Description = kvp.Value.Description;
            }
        }
    }

    private static void ClearRiskState(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM risk_hits; UPDATE events SET risk_score = 0, risk_level = 'Low';";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static List<EventRecord> LoadEvents(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.event_id, e.record_id, e.creation_date_utc, e.creation_date_local, e.event_time_basis, e.event_time_confidence, e.is_behavioral_timestamp, e.timestamp_warning, e.user_id, e.operation, e.workload, e.category, e.client_ip, e.client_ip_alt, e.user_agent, e.object_id, e.site_url, e.source_relative_url, e.file_name, e.file_size_bytes, e.recipients, e.attachment_details, e.result_status, e.raw_json, e.source_file, e.source_row_number,
COALESCE((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name = 'ZipFileName' LIMIT 1), '') AS zip_file_name,
COALESCE((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name IN ('EmailInfo_From', 'ExchangeMetaData_From', 'Sender') LIMIT 1), '') AS email_from,
COALESCE((SELECT group_concat(ef.field_value, '; ') FROM event_fields ef WHERE ef.event_id = e.event_id AND (ef.field_name LIKE 'EmailInfo_To_%' OR ef.field_name LIKE 'ExchangeMetaData_To_%' OR ef.field_name LIKE 'EmailInfo_Cc_%' OR ef.field_name LIKE 'ExchangeMetaData_CC_%' OR ef.field_name LIKE 'EmailInfo_Bcc_%' OR ef.field_name LIKE 'ExchangeMetaData_BCC_%')), '') AS email_to,
COALESCE((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name IN ('EmailInfo_Subject', 'ExchangeMetaData_Subject', 'Item_Subject', 'AffectedItems_0_Subject') LIMIT 1), '') AS email_subject,
COALESCE((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name IN ('DisplayTarget','TargetPath','OriginalSourcePath','Original Path','FolderPath','SourceRelativeUrl','DestinationRelativeUrl','Folder_Path','Item_ParentFolder_Path','AffectedItems_0_ParentFolder_Path','SourceUrl','SourceUrlChain','Url','CloudAccount','OneDriveUserFolder','Dropbox_personal_path','GoogleDriveAccountFromPath') AND IFNULL(ef.field_value,'') NOT LIKE '%WorkingEvidence%' LIMIT 1), '') AS path_hint,
COALESCE((SELECT group_concat(ef.field_value, '; ') FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name LIKE '%Attachment%'), '') AS attachments_expanded,
e.data_source,
COALESCE((SELECT ef.field_value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name IN ('DriveType','Drive Type') LIMIT 1), '') AS drive_type
FROM events e ORDER BY e.creation_date_utc, e.event_id;";

        using var reader = cmd.ExecuteReader();
        var list = new List<EventRecord>();
        while (reader.Read())
        {
            list.Add(new EventRecord
            {
                EventId = reader.GetInt64(0),
                RecordId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                CreationDateUtc = reader.IsDBNull(2) ? "" : reader.GetString(2),
                CreationDateLocal = reader.IsDBNull(3) ? "" : reader.GetString(3),
                EventTimeBasis = reader.IsDBNull(4) ? "" : reader.GetString(4),
                EventTimeConfidence = reader.IsDBNull(5) ? "" : reader.GetString(5),
                IsBehavioralTimestamp = !reader.IsDBNull(6) && Convert.ToInt32(reader.GetValue(6)) == 1,
                TimestampWarning = reader.IsDBNull(7) ? "" : reader.GetString(7),
                UserId = reader.IsDBNull(8) ? "" : reader.GetString(8),
                Operation = reader.IsDBNull(9) ? "" : reader.GetString(9),
                Workload = reader.IsDBNull(10) ? "" : reader.GetString(10),
                Category = reader.IsDBNull(11) ? "" : reader.GetString(11),
                ClientIp = reader.IsDBNull(12) ? "" : reader.GetString(12),
                ClientIpAlt = reader.IsDBNull(13) ? "" : reader.GetString(13),
                UserAgent = reader.IsDBNull(14) ? "" : reader.GetString(14),
                ObjectId = NormalizeRiskTarget(reader.IsDBNull(15) ? "" : reader.GetString(15), reader.IsDBNull(30) ? "" : reader.GetString(30), reader.IsDBNull(17) ? "" : reader.GetString(17), reader.IsDBNull(18) ? "" : reader.GetString(18), reader.IsDBNull(24) ? "" : reader.GetString(24)),
                SiteUrl = reader.IsDBNull(16) ? "" : reader.GetString(16),
                SourceRelativeUrl = NormalizeRiskTarget(reader.IsDBNull(17) ? "" : reader.GetString(17), reader.IsDBNull(30) ? "" : reader.GetString(30), "", reader.IsDBNull(18) ? "" : reader.GetString(18), reader.IsDBNull(24) ? "" : reader.GetString(24)),
                FileName = reader.IsDBNull(18) ? "" : reader.GetString(18),
                FileSizeBytes = reader.IsDBNull(19) ? null : reader.GetInt64(19),
                Recipients = reader.IsDBNull(20) ? "" : reader.GetString(20),
                AttachmentDetails = reader.IsDBNull(21) ? "" : reader.GetString(21),
                ResultStatus = reader.IsDBNull(22) ? "" : reader.GetString(22),
                RawJson = reader.IsDBNull(23) ? "" : reader.GetString(23),
                SourceFile = reader.IsDBNull(24) ? "" : reader.GetString(24),
                SourceRowNumber = reader.IsDBNull(25) ? 0 : reader.GetInt32(25),
                ZipFileName = reader.IsDBNull(26) ? "" : reader.GetString(26),
                EmailFrom = reader.IsDBNull(27) ? "" : reader.GetString(27),
                EmailTo = reader.IsDBNull(28) ? "" : reader.GetString(28),
                EmailSubject = reader.IsDBNull(29) ? "" : reader.GetString(29),
                PathHint = reader.IsDBNull(30) ? "" : reader.GetString(30),
                AttachmentsExpanded = reader.IsDBNull(31) ? "" : reader.GetString(31),
                DataSource = reader.IsDBNull(32) ? "O365_UAL" : reader.GetString(32),
                DriveType = reader.IsDBNull(33) ? "" : reader.GetString(33)
            });
        }
        return list;
    }

    private static Dictionary<string, HashSet<string>> BuildUserIpBaselines(List<EventRecord> events, RiskEngineConfig config)
    {
        var baseline = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in events.Where(e => e.IsBehavioralTimestamp && !string.IsNullOrWhiteSpace(e.CreationDateUtc)).GroupBy(e => e.UserId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            baseline[group.Key] = group.Select(e => e.ClientIp).Where(ip => !string.IsNullOrWhiteSpace(ip)).GroupBy(ip => ip, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(config.Thresholds.UserIpBaselineTopCount).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return baseline;
    }

    private static Dictionary<(string UserId, string Bucket), int> BuildOperationBursts(List<EventRecord> events, string[] operations)
    {
        var wanted = operations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return events.Where(e => e.IsBehavioralTimestamp && wanted.Contains(e.Operation ?? string.Empty) && !string.IsNullOrWhiteSpace(e.CreationDateUtc)).GroupBy(e => (e.UserId ?? string.Empty, Bucket30(e.CreationDateUtc))).ToDictionary(g => g.Key, g => g.Count());
    }

    private static List<RiskHit> DetectSequences(List<EventRecord> events, RiskEngineConfig config)
    {
        var hits = new List<RiskHit>();
        foreach (var group in events.Where(e => e.IsBehavioralTimestamp && !string.IsNullOrWhiteSpace(e.CreationDateUtc)).GroupBy(e => e.UserId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(e => e.CreationDateUtc, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.EventId).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var currentTime = ParseLoose(ordered[i].CreationDateUtc);
                if (currentTime is null) continue;
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    var next = ordered[j];
                    var nextTime = ParseLoose(next.CreationDateUtc);
                    if (nextTime is null) continue;
                    if ((nextTime.Value - currentTime.Value).TotalMinutes > config.Thresholds.SequenceWindowMinutes) break;

                    AddIfRule(hits, config, "SEQ-001", next, ordered[i].Operation.Equals("MailItemsAccessed", StringComparison.OrdinalIgnoreCase) && ContainsPersonalDomain(config, FirstNonBlank(next.EmailTo, next.Recipients)), $"MailItemsAccessed followed by personal recipient within {config.Thresholds.SequenceWindowMinutes} minutes", FirstNonBlank(next.EmailTo, next.Recipients));
                    AddIfRule(hits, config, "SEQ-002", next, ordered[i].Operation.Equals("FileDownloaded", StringComparison.OrdinalIgnoreCase) && IsExternalSharingOperation(next.Operation), $"FileDownloaded followed by external sharing/link activity within {config.Thresholds.SequenceWindowMinutes} minutes", next.Operation);
                    AddIfRule(hits, config, "SEQ-003", next, ordered[i].Operation.Equals("FileDownloaded", StringComparison.OrdinalIgnoreCase) && IsDeletionLikeOperation(next.Operation), $"FileDownloaded followed by deletion-related activity within {config.Thresholds.SequenceWindowMinutes} minutes", next.Operation);
                    
                    bool isFileAccess = LooksLikeAccessOrMovementOperation(ordered[i].Operation) || ordered[i].Operation == "FileDownloaded";
                    bool isCloudVisit = next.DataSource == "Browser_History" && ContainsKeyword(config.CloudStorageDomains, next.ObjectId);
                    AddIfRule(hits, config, "SEQ-004", next, isFileAccess && isCloudVisit, $"File access/download followed by visit to cloud storage within {config.Thresholds.SequenceWindowMinutes} minutes", next.ObjectId);
                }
            }
        }
        return hits;
    }

    private static List<RiskHit> DeduplicateHits(List<RiskHit> hits) => hits.GroupBy(h => $"{h.EventId}\u001F{h.RuleCode}\u001F{h.SupportingValue}", StringComparer.Ordinal).Select(g => g.OrderByDescending(x => x.RiskScore).First()).OrderByDescending(h => h.RiskScore).ThenBy(h => h.EventId).ToList();

    private static void PersistHits(SqliteConnection conn, List<RiskHit> hits)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO risk_hits (event_id, rule_code, rule_name, risk_domain, risk_score, risk_level, reason, supporting_value, created_utc) VALUES ($event_id, $rule_code, $rule_name, $risk_domain, $risk_score, $risk_level, $reason, $supporting_value, $created_utc);";
        foreach (var hit in hits)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$event_id", hit.EventId); cmd.Parameters.AddWithValue("$rule_code", hit.RuleCode); cmd.Parameters.AddWithValue("$rule_name", hit.RuleName); cmd.Parameters.AddWithValue("$risk_domain", hit.RiskDomain); cmd.Parameters.AddWithValue("$risk_score", hit.RiskScore); cmd.Parameters.AddWithValue("$risk_level", hit.RiskLevel); cmd.Parameters.AddWithValue("$reason", hit.Reason); cmd.Parameters.AddWithValue("$supporting_value", hit.SupportingValue); cmd.Parameters.AddWithValue("$created_utc", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void UpdateEventScores(SqliteConnection conn, RiskEngineConfig config)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"UPDATE events SET risk_score = COALESCE((SELECT MAX(rh.risk_score) FROM risk_hits rh WHERE rh.event_id = events.event_id), 0), risk_level = CASE WHEN COALESCE((SELECT MAX(rh.risk_score) FROM risk_hits rh WHERE rh.event_id = events.event_id), 0) >= {config.ScoreThresholds.CriticalMin} THEN 'Critical' WHEN COALESCE((SELECT MAX(rh.risk_score) FROM risk_hits rh WHERE rh.event_id = events.event_id), 0) >= {config.ScoreThresholds.HighMin} THEN 'High' WHEN COALESCE((SELECT MAX(rh.risk_score) FROM risk_hits rh WHERE rh.event_id = events.event_id), 0) >= {config.ScoreThresholds.MediumMin} THEN 'Medium' ELSE 'Low' END;";
        cmd.ExecuteNonQuery();
    }

    private static RiskRunResult GetRiskSummary(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM risk_hits), (SELECT COUNT(*) FROM events WHERE risk_level = 'Critical'), (SELECT COUNT(*) FROM events WHERE risk_level = 'High'), (SELECT COUNT(*) FROM events WHERE risk_level = 'Medium');";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new RiskRunResult { TotalHits = reader.GetInt32(0), CriticalEvents = reader.GetInt32(1), HighEvents = reader.GetInt32(2), MediumEvents = reader.GetInt32(3) };
    }

    private static void AddIfRule(List<RiskHit> hits, RiskEngineConfig config, string code, EventRecord ev, bool condition, string reason, string supporting)
    {
        if (!condition) return;
        if (!config.Rules.TryGetValue(code, out var rule) || !rule.Enabled) return;
        hits.Add(new RiskHit { EventId = ev.EventId, RuleCode = code, RuleName = RuleNameFromCode(code), RiskDomain = rule.RiskDomain, RiskScore = rule.Score, RiskLevel = ScoreToLevel(rule.Score, config), Reason = reason, SupportingValue = supporting ?? string.Empty });
    }

    private static string RuleNameFromCode(string code) => code switch { "EXF-001" => "Personal email recipient", "EXF-002" => "Personal email with attachment", "EXF-010" => "Single file download", "EXF-011" => "Download burst", "EXF-012" => "Mass download burst", "EXF-013" => "ZIP package name present", "EXF-014" => "After-hours download burst", "EXF-020" => "Mailbox item access", "EXF-021" => "Bulk mailbox access", "EXF-022" => "Mailbox access from unusual IP", "EXF-030" => "External/anonymous sharing event", "EXF-040" => "Print activity", "EXF-052" => "Sensitive-name access", "EXF-060" => "LNK/JumpList/RecycleBin Removable Drive", "EXF-061" => "LNK/JumpList/RecycleBin Network Drive", "EXF-070" => "USB Device Connected", "EXF-071" => "SetupAPI USB device first install", "EXF-072" => "SetupAPI transfer-capable device/interface install", "CON-071" => "SetupAPI destructive tool/driver install", "EXF-080" => "Browser visit to cloud storage", "EXF-081" => "Browser download from cloud storage", "EXF-082" => "SRUM cloud network indicator", "EXF-083" => "Cloud sync artifact observed", "EXF-084" => "Office cloud MRU item", "EXF-085" => "Personal webmail visit", "EXF-086" => "Office OAlerts file activity", "EXF-090" => "Transfer or sync tool observed", "EXF-091" => "Google Takeout export activity", "EXF-092" => "Google Drive exfiltration-relevant activity", "EXF-093" => "Google Gmail transfer-relevant activity", "EXF-094" => "Google OAuth/API data access", "ACC-031" => "Google access anomaly", "CON-082" => "Google Vault/Admin control event", "AI-011" => "Google Gemini AI activity", "ACC-010" => "Cross-user personal site access", "ACC-020" => "Local file or localhost browser access", "CON-060" => "Hard delete", "CON-061" => "Deletion burst", "CON-062" => "Recycle Bin file deleted", "CON-063" => "USN file delete", "CON-070" => "Anti-forensic tool executed", "SEQ-001" => "Mailbox access followed by personal-email send", "SEQ-002" => "Download followed by sharing", "SEQ-003" => "Download followed by deletion", "SEQ-004" => "File access followed by cloud storage visit", _ => code };
    private static string ScoreToLevel(int score, RiskEngineConfig config) => score >= config.ScoreThresholds.CriticalMin ? "Critical" : score >= config.ScoreThresholds.HighMin ? "High" : score >= config.ScoreThresholds.MediumMin ? "Medium" : "Low";
    
    private static bool ContainsPersonalDomain(RiskEngineConfig config, string text) => !string.IsNullOrWhiteSpace(text) && config.PersonalDomains.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsSensitiveKeyword(RiskEngineConfig config, string text) => !string.IsNullOrWhiteSpace(text) && config.SensitiveKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsKeyword(List<string> keywords, string text) => !string.IsNullOrWhiteSpace(text) && keywords != null && keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsAny(string text, params string[] needles) => !string.IsNullOrWhiteSpace(text) && needles != null && needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
    
    private static bool IsExternalSharingOperation(string operation) => !string.IsNullOrWhiteSpace(operation) && (operation.Contains("AnonymousLink", StringComparison.OrdinalIgnoreCase) || operation.Contains("SharingInvitation", StringComparison.OrdinalIgnoreCase) || operation.Contains("SecureLink", StringComparison.OrdinalIgnoreCase) || operation.Contains("AddedToSecureLink", StringComparison.OrdinalIgnoreCase));
    private static bool IsDeletionLikeOperation(string operation) => !string.IsNullOrWhiteSpace(operation) && (operation.Contains("Delete", StringComparison.OrdinalIgnoreCase) || operation.Contains("Removed", StringComparison.OrdinalIgnoreCase) || operation.Contains("Recycle", StringComparison.OrdinalIgnoreCase));
    private static bool LooksLikeDownloadOrExportOperation(string operation) => !string.IsNullOrWhiteSpace(operation) && (operation.Contains("Download", StringComparison.OrdinalIgnoreCase) || operation.Contains("Export", StringComparison.OrdinalIgnoreCase) || operation.Contains("Sync", StringComparison.OrdinalIgnoreCase));
    private static bool LooksLikeAccessOrMovementOperation(string operation) => !string.IsNullOrWhiteSpace(operation) && (operation.Contains("Access", StringComparison.OrdinalIgnoreCase) || operation.Contains("Download", StringComparison.OrdinalIgnoreCase) || operation.Contains("Preview", StringComparison.OrdinalIgnoreCase) || operation.Contains("View", StringComparison.OrdinalIgnoreCase) || operation.Contains("Open", StringComparison.OrdinalIgnoreCase) || operation.Contains("Sync", StringComparison.OrdinalIgnoreCase) || operation.Contains("File", StringComparison.OrdinalIgnoreCase) || operation.Contains("Move", StringComparison.OrdinalIgnoreCase) || operation.Contains("Copy", StringComparison.OrdinalIgnoreCase) || operation.Contains("Share", StringComparison.OrdinalIgnoreCase) || operation.Contains("Link", StringComparison.OrdinalIgnoreCase) || operation.Contains("MailItemsAccessed", StringComparison.OrdinalIgnoreCase) || operation.Contains("Shortcut", StringComparison.OrdinalIgnoreCase) || operation.Contains("JumpList", StringComparison.OrdinalIgnoreCase) || operation.Contains("Folder_Navigated", StringComparison.OrdinalIgnoreCase) || operation.Contains("Web_Visit", StringComparison.OrdinalIgnoreCase));

    private static string BestSensitiveIndicator(RiskEngineConfig config, EventRecord ev)
    {
        foreach (var candidate in new[] { ev.ZipFileName, ev.SourceRelativeUrl, ev.PathHint, ev.FileName, ev.EmailSubject, ev.ObjectId })
        {
            if (ContainsSensitiveKeyword(config, candidate) && ForensicText.LooksLikeActionableUserDataPathOrName(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string NormalizeRiskTarget(string primary, string pathHint, string sourceRelativeUrl, string fileName, string sourceFile)
    {
        var candidate = FirstNonBlank(primary, sourceRelativeUrl, pathHint, fileName, sourceFile);
        if (ForensicText.IsLocalWorkingEvidencePath(candidate))
            candidate = FirstNonBlank(pathHint, sourceRelativeUrl, fileName, sourceFile, primary);

        if (ForensicText.IsLocalWorkingEvidencePath(candidate))
            candidate = FirstNonBlank(fileName, sourceFile, primary);

        return ForensicText.CleanDisplayValue(candidate);
    }

    private static bool LooksLikeCrossUserPersonalSiteAccess(EventRecord ev, out string supporting)
    {
        supporting = string.Empty;
        if (string.IsNullOrWhiteSpace(ev.UserId) || !LooksLikeAccessOrMovementOperation(ev.Operation)) return false;
        foreach (var candidate in new[] { ev.SiteUrl, ev.SourceRelativeUrl, ev.PathHint, ev.ObjectId })
        {
            if (TryExtractPersonalSiteOwnerToken(candidate, out var ownerToken) && !OwnerTokenMatchesUser(ownerToken, ev.UserId)) { supporting = FirstNonBlank(candidate, ownerToken); return true; }
        }
        return false;
    }

    private static bool TryExtractPersonalSiteOwnerToken(string value, out string ownerToken)
    {
        ownerToken = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(value.Replace('\\', '/').Trim(), @"/(personal|users)/([^/]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        ownerToken = match.Groups[2].Value.Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(ownerToken);
    }

    private static bool OwnerTokenMatchesUser(string ownerToken, string userId)
    {
        var token = NormalizeIdentityToken(ownerToken);
        if (string.IsNullOrWhiteSpace(token)) return false;
        var user = (userId ?? string.Empty).Trim().ToLowerInvariant();
        var local = user.Split('@')[0];
        var full = user.Replace("@", "_").Replace(".", "_").Replace("-", "_");
        var localNorm = NormalizeIdentityToken(local);
        var fullNorm = NormalizeIdentityToken(full);
        var userNorm = NormalizeIdentityToken(user);
        return token == localNorm || token == fullNorm || token == userNorm || (!string.IsNullOrWhiteSpace(fullNorm) && token.Contains(fullNorm, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(localNorm) && token.StartsWith(localNorm, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeIdentityToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var normalized = new string(chars);
        while (normalized.Contains("__", StringComparison.Ordinal)) normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        return normalized.Trim('_');
    }

    private static bool IsAfterHours(string localString, RiskEngineConfig config)
    {
        var dt = ParseLoose(localString);
        return dt is not null && (dt.Value.Hour < config.BusinessHours.StartHourLocal || dt.Value.Hour >= config.BusinessHours.EndHourLocal);
    }

    private static string Bucket30(string utcString)
    {
        var dt = ParseLoose(utcString) ?? DateTime.MinValue;
        return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute < 30 ? 0 : 30, 0).ToString("yyyy-MM-dd HH:mm");
    }

    private static DateTime? ParseLoose(string s) => DateTime.TryParse(s, out var dt) ? dt : null;
    private static string BestFileIndicator(EventRecord ev) => FirstNonBlank(ev.SourceRelativeUrl, ev.PathHint, ev.ObjectId, ev.ZipFileName, ev.FileName, ev.EmailSubject);
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
