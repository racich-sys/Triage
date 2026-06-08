using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Registry;

namespace VestigantTriage;

public sealed class OneDriveParser : IArtifactParser
{
    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UrlRegex = new("https?://[^\\s\\\"'<>|{}]{4,1000}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxDatabaseRowsPerTable = 500;

    public string ParserName => "OneDrive Artifact Parser";

    public bool CanParse(string filePath)
    {
        var rawName = Path.GetFileName(filePath) ?? string.Empty;
        var upperName = rawName.ToUpperInvariant();
        var lowerName = rawName.ToLowerInvariant();

        if (upperName.Equals("NTUSER.DAT", StringComparison.Ordinal) || upperName.EndsWith("_NTUSER.DAT", StringComparison.Ordinal))
            return true;

        if (lowerName.EndsWith(".pf", StringComparison.Ordinal) || lowerName.EndsWith(".exe", StringComparison.Ordinal))
            return false;

        if (IsSupportedOneDriveStateFile(lowerName))
            return true;

        if (!IsInOneDriveApplicationTree(filePath))
            return false;

        return IsSupportedOneDriveConfigFile(lowerName);
    }

    private static bool IsInOneDriveApplicationTree(string filePath)
    {
        return ParserSupport.HasPathSequence(filePath, "Microsoft", "OneDrive") || ParserSupport.HasPathSegment(filePath, "OneDrive");
    }

    private static bool IsSupportedOneDriveStateFile(string lowerName)
    {
        return lowerName is "syncenginedatabase.db" or "settingsdatabase.db" or "safedelete.db" ||
               lowerName.EndsWith("_syncenginedatabase.db", StringComparison.Ordinal) ||
               lowerName.EndsWith("_settingsdatabase.db", StringComparison.Ordinal) ||
               lowerName.EndsWith("_safedelete.db", StringComparison.Ordinal);
    }

    private static bool IsSupportedOneDriveConfigFile(string lowerName)
    {
        if (IsSupportedOneDriveStateFile(lowerName)) return true;
        if (lowerName is "global.ini" or "clientpolicy.ini" or "business1.ini" or "personal.ini") return true;
        if (lowerName.EndsWith(".json", StringComparison.Ordinal) || lowerName.EndsWith(".ini", StringComparison.Ordinal)) return true;
        return false;
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var lowerName = (Path.GetFileName(filePath) ?? string.Empty).ToLowerInvariant();
            var upperName = lowerName.ToUpperInvariant();
            if (upperName.Equals("NTUSER.DAT", StringComparison.Ordinal) || upperName.EndsWith("_NTUSER.DAT", StringComparison.Ordinal))
            {
                events.AddRange(ParseRegistry(filePath));
            }
            else if (lowerName.EndsWith(".db", StringComparison.Ordinal))
            {
                events.AddRange(ParseOneDriveDatabase(filePath, lowerName));
            }
            else
            {
                events.AddRange(ParseLooseOneDriveConfig(filePath));
            }
        }
        catch (Exception ex)
        {
            log($"OneDrive parser failed for {Path.GetFileName(filePath)}: {ex.Message}");
            events.Add(MakeError(filePath, ex.Message));
        }

        return events;
    }

    private IEnumerable<NormalizedEvent> ParseRegistry(string filePath)
    {
        var hive = new RegistryHive(filePath);
        hive.ParseHive();
        var user = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");
        foreach (var basePath in new[] { @"Software\Microsoft\OneDrive\Accounts", @"Software\Microsoft\OneDrive", @"Software\SyncEngines\Providers\OneDrive" })
        {
            var key = hive.GetKey(basePath);
            if (key == null) continue;
            foreach (var ev in ParseRegistryTree(key, user, filePath)) yield return ev;
        }
    }

    private IEnumerable<NormalizedEvent> ParseRegistryTree(object root, string user, string sourceFile)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        var emitted = 0;
        while (stack.Count > 0 && emitted < 5000)
        {
            var key = stack.Pop();
            var keyPath = RegistryParseSupport.SafeKeyPath(key);
            var keyName = RegistryParseSupport.SafeKeyName(key);
            var folder = FirstNonBlank(
                RegistryParseSupport.GetValueString(key, "UserFolder"),
                RegistryParseSupport.GetValueString(key, "MountPoint"),
                RegistryParseSupport.GetValueString(key, "LibraryFolder"),
                RegistryParseSupport.GetValueString(key, "SyncRoot"),
                RegistryParseSupport.GetValueString(key, "UrlNamespace"));
            var email = FirstNonBlank(
                RegistryParseSupport.GetValueString(key, "UserEmail"),
                RegistryParseSupport.GetValueString(key, "Email"),
                ExtractEmail(RegistryParseSupport.GetValueString(key, "UserFolder")));
            var cid = FirstNonBlank(RegistryParseSupport.GetValueString(key, "cid"), RegistryParseSupport.GetValueString(key, "CID"));
            var tenant = FirstNonBlank(RegistryParseSupport.GetValueString(key, "ConfiguredTenantId"), RegistryParseSupport.GetValueString(key, "TenantId"), RegistryParseSupport.GetValueString(key, "Business"));
            var hasUseful = !string.IsNullOrWhiteSpace(folder) || !string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(cid) || keyPath.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
            if (hasUseful)
            {
                var target = FirstNonBlank(folder, email, cid, keyName);
                var ev = new NormalizedEvent
                {
                    DataSource = "OneDrive",
                    Operation = "OneDrive_Account_Or_SyncRoot",
                    ObjectPath = target,
                    UserId = user,
                    TimestampUtc = RegistryParseSupport.LastWriteUtc(key)
                };
                ev.AdditionalFields["EventCategory"] = "CloudSync";
                ev.AdditionalFields["ArtifactType"] = "OneDriveRegistry";
                ev.AdditionalFields["RegistryKeyPath"] = keyPath;
                ev.AdditionalFields["RegistryTimestampBasis"] = "KeyLastWrite";
                ev.AdditionalFields["CloudProvider"] = "OneDrive";
                ev.AdditionalFields["CloudAccount"] = email;
                ev.AdditionalFields["OneDriveUserEmail"] = email;
                ev.AdditionalFields["OneDriveUserFolder"] = folder;
                ev.AdditionalFields["OneDriveCID"] = cid;
                ev.AdditionalFields["OneDriveTenantId"] = tenant;
                ev.AdditionalFields["OneDriveAccountType"] = InferAccountType(keyPath, tenant, email);
                ev.AdditionalFields["SourceRegistryHive"] = sourceFile;
                if (!string.IsNullOrWhiteSpace(folder)) ParserSupport.AddTargetFields(ev, folder, "OneDriveSyncRoot");
                ParserSupport.AddParseQuality(ev, ParserName, "High", "OneDrive registry account/sync-root value extracted.");
                emitted++;
                yield return ev;
            }

            foreach (var sub in SafeSubKeys(key)) stack.Push(sub);
        }
    }

    private IEnumerable<NormalizedEvent> ParseOneDriveDatabase(string filePath, string lowerName)
    {
        var user = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");
        var artifactType = lowerName.Contains("syncenginedatabase", StringComparison.OrdinalIgnoreCase) ? "OneDriveSyncEngineDatabase" : "OneDriveDatabase";
        var emitted = 0;
        var schemaSummary = new List<string>();

        var csb = new SqliteConnectionStringBuilder { DataSource = filePath, Mode = SqliteOpenMode.ReadOnly };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();

        foreach (var table in ReadTables(conn))
        {
            var columns = ReadColumns(conn, table);
            if (columns.Count == 0) continue;
            schemaSummary.Add($"{table}({string.Join(",", columns.Take(24))})");
            var selectedColumns = SelectInterestingColumns(columns).Take(24).ToList();
            if (selectedColumns.Count == 0) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", selectedColumns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(table)} LIMIT {MaxDatabaseRowsPerTable}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < selectedColumns.Count; i++)
                {
                    var value = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i)) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value)) values[selectedColumns[i]] = ParserSupport.Clean(value);
                }

                if (values.Count == 0) continue;
                var target = FirstUsefulDatabaseTarget(values);
                if (string.IsNullOrWhiteSpace(target)) continue;

                var operation = DetermineOneDriveDatabaseOperation(table, values, artifactType);
                var decodedTimestamp = BestOneDriveDatabaseTimestamp(table, operation, values, out var timestampBasis, out var timestampField);
                var ev = new NormalizedEvent
                {
                    DataSource = "OneDrive",
                    Operation = operation,
                    ObjectPath = target,
                    UserId = user,
                    TimestampUtc = decodedTimestamp ?? DateTime.MinValue,
                    EventTimeBasis = decodedTimestamp.HasValue ? timestampBasis : "OneDriveDatabaseRecordNoDecodedTimestamp",
                    EventTimeConfidence = decodedTimestamp.HasValue ? "Medium" : "MetadataOnly",
                    IsBehavioralTimestamp = decodedTimestamp.HasValue,
                    TimestampWarning = decodedTimestamp.HasValue ? string.Empty : "OneDrive SQLite row extracted for catalog/source validation; no behavioral timestamp was decoded in this generic parser path."
                };
                ev.AdditionalFields["EventCategory"] = "CloudSync";
                ev.AdditionalFields["ArtifactType"] = artifactType;
                ev.AdditionalFields["CloudProvider"] = "OneDrive";
                ev.AdditionalFields["SourceDatabase"] = Path.GetFileName(filePath);
                ev.AdditionalFields["SourceTable"] = table;
                foreach (var kv in values.Take(64)) ev.AdditionalFields[$"OneDriveDb_{kv.Key}"] = kv.Value.Length > 1000 ? kv.Value[..1000] : kv.Value;
                AddDecodedOneDriveTimestampFields(ev, values);
                if (!string.IsNullOrWhiteSpace(timestampField)) ev.AdditionalFields["OneDriveDecodedTimestampField"] = timestampField;
                if (ForensicText.IsLikelyPath(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ParserSupport.AddTargetFields(ev, target, "OneDriveDatabaseTarget");
                ParserSupport.AddParseQuality(ev, ParserName, decodedTimestamp.HasValue ? "Medium" : "Medium", decodedTimestamp.HasValue ? "OneDrive SQLite row extracted with decoded artifact-native sync/catalog timestamp." : "Generic OneDrive SQLite database row extracted for catalog validation; schema-specific interpretation may require follow-up.");
                emitted++;
                yield return ev;
                if (emitted >= 5000) yield break;
            }
        }

        if (emitted == 0)
        {
            var ev = new NormalizedEvent
            {
                DataSource = "OneDrive",
                Operation = artifactType == "OneDriveSyncEngineDatabase" ? "OneDrive_SyncEngine_Schema_Inventory" : "OneDrive_Database_Schema_Inventory",
                ObjectPath = filePath,
                UserId = user,
                TimestampUtc = DateTime.MinValue,
                EventTimeBasis = "DatabaseSchemaInventory",
                EventTimeConfidence = "MetadataOnly",
                IsBehavioralTimestamp = false,
                TimestampWarning = "OneDrive database was collected and opened, but no catalog rows were emitted by the generic parser."
            };
            ev.AdditionalFields["EventCategory"] = "CloudSync";
            ev.AdditionalFields["ArtifactType"] = artifactType;
            ev.AdditionalFields["CloudProvider"] = "OneDrive";
            ev.AdditionalFields["SourceDatabase"] = Path.GetFileName(filePath);
            ev.AdditionalFields["OneDriveDbSchemaSummary"] = string.Join(" | ", schemaSummary).Trim();
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "OneDrive SQLite database schema inventory only; no file catalog rows were emitted.");
            yield return ev;
        }
    }

    private static string DetermineOneDriveDatabaseOperation(string table, Dictionary<string, string> values, string artifactType)
    {
        var lowerTable = (table ?? string.Empty).ToLowerInvariant();
        if (lowerTable.Contains("serviceoperationhistory")) return "OneDrive_Service_Operation";
        if (lowerTable.Contains("safedelete") || lowerTable.Contains("delete")) return "OneDrive_SafeDelete_Record";
        if (lowerTable.Contains("clientfile") || values.ContainsKey("fileName")) return "OneDrive_SyncEngine_File_Record";
        if (lowerTable.Contains("clientfolder") || values.ContainsKey("folderName")) return "OneDrive_SyncEngine_Folder_Record";
        if (lowerTable.Contains("hydration")) return "OneDrive_Hydration_Record";
        if (lowerTable.Contains("scopeinfo")) return "OneDrive_Scope_Record";
        if (lowerTable.Contains("graphmetadata")) return "OneDrive_GraphMetadata_Record";
        return artifactType == "OneDriveSyncEngineDatabase" ? "OneDrive_SyncEngine_Record" : "OneDrive_Database_Record";
    }

    private static DateTime? BestOneDriveDatabaseTimestamp(string table, string operation, Dictionary<string, string> values, out string basis, out string sourceField)
    {
        sourceField = string.Empty;
        basis = string.Empty;
        var lowerTable = (table ?? string.Empty).ToLowerInvariant();
        var preferred = new List<string>();

        if (operation.Equals("OneDrive_SafeDelete_Record", StringComparison.OrdinalIgnoreCase))
            preferred.AddRange(new[] { "notificationTime", "deleteTime", "deletedTime", "lastTouchedTime", "timestamp" });
        else if (operation.Equals("OneDrive_Service_Operation", StringComparison.OrdinalIgnoreCase))
            preferred.AddRange(new[] { "timestamp", "operationTime", "lastWriteTime" });
        else if (operation.Equals("OneDrive_Hydration_Record", StringComparison.OrdinalIgnoreCase))
            preferred.AddRange(new[] { "lastHydrationTime", "firstHydrationTime", "timestamp" });
        else if (lowerTable.Contains("clientfile") || lowerTable.Contains("clientfolder"))
            preferred.AddRange(new[] { "diskLastAccessTime", "diskLastWriteTime", "diskCreationTime", "lastModifiedTime", "modifiedTime", "createdTime", "timestamp" });

        preferred.AddRange(new[] { "lastWriteTime", "lastModifiedTime", "modifiedTime", "creationTime", "createdTime", "timestamp", "date" });

        foreach (var name in preferred.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryGetValue(values, name, out var raw) && TryParseOneDriveUnixTimestamp(raw, out var dt))
            {
                sourceField = name;
                basis = $"OneDriveDb_{name}";
                return dt;
            }
        }

        foreach (var kv in values)
        {
            if (!LooksLikeTimestampColumn(kv.Key)) continue;
            if (TryParseOneDriveUnixTimestamp(kv.Value, out var dt))
            {
                sourceField = kv.Key;
                basis = $"OneDriveDb_{kv.Key}";
                return dt;
            }
        }

        return null;
    }

    private static bool TryGetValue(Dictionary<string, string> values, string key, out string value)
    {
        foreach (var kv in values)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static void AddDecodedOneDriveTimestampFields(NormalizedEvent ev, Dictionary<string, string> values)
    {
        foreach (var kv in values)
        {
            if (!LooksLikeTimestampColumn(kv.Key)) continue;
            if (TryParseOneDriveUnixTimestamp(kv.Value, out var dt))
                ev.AdditionalFields[$"OneDriveDb_{kv.Key}Utc"] = dt.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private static bool LooksLikeTimestampColumn(string column)
    {
        var lower = (column ?? string.Empty).ToLowerInvariant();
        return lower.Contains("time") || lower.Contains("date") || lower.Contains("created") || lower.Contains("modified") || lower.Contains("access") || lower.Contains("hydration") || lower.Contains("notification") || lower.Contains("lastwrite");
    }

    private static bool TryParseOneDriveUnixTimestamp(string value, out DateTime timestampUtc)
    {
        timestampUtc = DateTime.MinValue;
        value = ParserSupport.Clean(value);
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)) return false;

        try
        {
            DateTimeOffset dto;
            if (raw > 100000000000000000L)
                dto = DateTimeOffset.FromFileTime(raw);
            else if (raw > 1000000000000L)
                dto = DateTimeOffset.FromUnixTimeMilliseconds(raw);
            else if (raw > 1000000000L)
                dto = DateTimeOffset.FromUnixTimeSeconds(raw);
            else
                return false;

            var utc = dto.UtcDateTime;
            if (utc.Year < 2000 || utc.Year > DateTime.UtcNow.Year + 2) return false;
            timestampUtc = utc;
            return true;
        }
        catch { return false; }
    }

    private IEnumerable<NormalizedEvent> ParseLooseOneDriveConfig(string filePath)
    {
        var user = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");
        var fileName = Path.GetFileName(filePath) ?? string.Empty;
        var lines = ReadReasonableLines(filePath, 20000);
        var emitted = 0;
        foreach (var line in lines)
        {
            if (!line.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) && !line.Contains("sharepoint", StringComparison.OrdinalIgnoreCase) && !line.Contains("d.docs.live.net", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = FirstNonBlank(FirstUrl(line), FirstPath(line), ExtractEmail(line), line.Length > 400 ? line[..400] : line);
            var ev = new NormalizedEvent
            {
                DataSource = "OneDrive",
                Operation = "OneDrive_Config_Indicator",
                ObjectPath = target,
                UserId = user,
                TimestampUtc = DateTime.MinValue,
                EventTimeBasis = "OneDriveConfigLineNoDecodedTimestamp",
                EventTimeConfidence = "MetadataOnly",
                IsBehavioralTimestamp = false,
                TimestampWarning = "OneDrive config/settings line extracted; no behavioral timestamp decoded."
            };
            ev.AdditionalFields["EventCategory"] = "CloudSync";
            ev.AdditionalFields["ArtifactType"] = "OneDriveConfigOrSettings";
            ev.AdditionalFields["CloudProvider"] = "OneDrive";
            ev.AdditionalFields["CloudAccount"] = ExtractEmail(line);
            ev.AdditionalFields["Domain"] = ParserSupport.InferCloudOrWebDomain(FirstUrl(line));
            ev.AdditionalFields["SourceFile"] = fileName;
            ev.AdditionalFields["SourceLine"] = line.Length > 4000 ? line[..4000] : line;
            if (ForensicText.IsLikelyPath(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ParserSupport.AddTargetFields(ev, target, "OneDriveConfigTarget");
            ParserSupport.AddParseQuality(ev, ParserName, "Medium", "OneDrive settings/config line contained cloud-sync path, URL, or account indicator.");
            yield return ev;
            emitted++;
            if (emitted >= 2000) yield break;
        }

        if (emitted == 0)
        {
            yield return MakeOneDriveConfigFileObservedEvent(filePath, user, fileName);
        }
    }

    private NormalizedEvent MakeOneDriveConfigFileObservedEvent(string filePath, string user, string fileName)
    {
        var info = new FileInfo(filePath);
        var target = FirstNonBlank(fileName, filePath);
        var ev = new NormalizedEvent
        {
            DataSource = "OneDrive",
            Operation = "OneDrive_Config_File_Observed",
            ObjectPath = target,
            UserId = user,
            TimestampUtc = DateTime.MinValue,
            EventTimeBasis = "OneDriveConfigFileObservedNoDecodedTimestamp",
            EventTimeConfidence = "MetadataOnly",
            IsBehavioralTimestamp = false,
            TimestampWarning = "OneDrive config/state file was collected but no account/path/URL line with an artifact-native timestamp was decoded; source/staged timestamps are suppressed."
        };
        ev.AdditionalFields["EventCategory"] = "CloudSync";
        ev.AdditionalFields["ArtifactType"] = "OneDriveConfigOrSettings";
        ev.AdditionalFields["CloudProvider"] = "OneDrive";
        ev.AdditionalFields["SourceFile"] = fileName;
        ev.AdditionalFields["SourceConfigPath"] = filePath;
        ev.AdditionalFields["FileName"] = fileName;
        ev.AdditionalFields["FileExtension"] = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        ev.AdditionalFields["FileSizeBytes"] = info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "0";
        ev.AdditionalFields["ConfigInventoryOnly"] = "true";
        ParserSupport.AddParseQuality(ev, ParserName, "Low", "OneDrive config/state artifact was recognized and inventoried so parser-matched files do not fall back to generic metadata rows.");
        return ev;
    }

    private static List<string> ReadTables(SqliteConnection conn)

    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
        }
        return result;
    }

    private static List<string> ReadColumns(SqliteConnection conn, string table)
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
        }
        return result;
    }

    private static IEnumerable<string> SelectInterestingColumns(IEnumerable<string> columns)
    {
        var list = columns.ToList();
        var preferred = list.Where(IsInterestingColumn).ToList();
        return preferred.Count > 0 ? preferred : list.Take(12);
    }

    private static bool IsInterestingColumn(string column)
    {
        var lower = (column ?? string.Empty).ToLowerInvariant();
        return lower.Contains("path") || lower.Contains("name") || lower.Contains("file") || lower.Contains("folder") ||
               lower.Contains("url") || lower.Contains("resource") || lower.Contains("item") || lower.Contains("parent") ||
               lower.Contains("drive") || lower.Contains("etag") || lower.Contains("size") || lower.Contains("time") ||
               lower.Contains("date") || lower.Contains("status") || lower.Contains("sync");
    }

    private static string FirstUsefulDatabaseTarget(Dictionary<string, string> values)
    {
        foreach (var exactKey in new[] { "path", "localPath", "filePath", "folderPath", "webUrl", "url", "fileName", "folderName", "name" })
        {
            if (TryGetValue(values, exactKey, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        foreach (var keyNeedle in new[] { "path", "url", "filename", "foldername", "name", "file", "folder", "resource", "item" })
        {
            foreach (var kv in values)
            {
                if (kv.Key.Contains(keyNeedle, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                    return kv.Value;
            }
        }
        return values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }

    private static NormalizedEvent MakeError(string filePath, string error)
    {
        var ev = new NormalizedEvent { DataSource = "OneDrive", Operation = "OneDrive_ParseError", ObjectPath = filePath, UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"), TimestampUtc = DateTime.MinValue };
        ev.AdditionalFields["EventCategory"] = "ParserError";
        ev.AdditionalFields["ParseError"] = error;
        ParserSupport.AddParseQuality(ev, "OneDrive Artifact Parser", "Low", "Exception during OneDrive artifact parsing.");
        return ev;
    }

    private static IEnumerable<string> ReadReasonableLines(string filePath, int maxLines)
    {
        var count = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            yield return ParserSupport.Clean(line);
            count++;
            if (count >= maxLines) yield break;
        }
    }

    private static IEnumerable<object> SafeSubKeys(object key)
    {
        var subKeys = RegistryParseSupport.GetProperty(key, "SubKeys") as IEnumerable;
        if (subKeys == null) yield break;
        foreach (var item in subKeys)
            if (item != null) yield return item;
    }

    private static string InferAccountType(string keyPath, string tenant, string email)
    {
        if (keyPath.Contains("Business", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(tenant)) return "Business";
        if (keyPath.Contains("Personal", StringComparison.OrdinalIgnoreCase) || email.EndsWith("@outlook.com", StringComparison.OrdinalIgnoreCase) || email.EndsWith("@hotmail.com", StringComparison.OrdinalIgnoreCase)) return "Personal";
        return string.Empty;
    }

    private static string ExtractEmail(string value)
    {
        var m = EmailRegex.Match(value ?? string.Empty);
        return m.Success ? m.Value : string.Empty;
    }

    private static string FirstUrl(string value)
    {
        var m = UrlRegex.Match(value ?? string.Empty);
        return m.Success ? m.Value.TrimEnd(';', ',', ')', ']') : string.Empty;
    }

    private static string FirstPath(string value)
    {
        foreach (var p in ForensicText.ExtractPathCandidates(value ?? string.Empty)) return p;
        return string.Empty;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
