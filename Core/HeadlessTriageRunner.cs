using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;

namespace VestigantTriage;

internal static class HeadlessTriageRunner
{
    public static int Run(string[] args)
    {
        var options = ParseArgs(args);
        var logPath = string.Empty;
        var statusPath = string.Empty;
        try
        {
            var imagePath = GetRequired(options, "image");
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image path does not exist.", imagePath);

            var scanModeText = GetOptional(options, "scan-mode", "triage");
            var scanMode = scanModeText.Equals("full", StringComparison.OrdinalIgnoreCase)
                ? ImageArtifactScanMode.Full
                : ImageArtifactScanMode.Triage;

            var caseRoot = GetOptional(options, "case-root", string.Empty);
            if (string.IsNullOrWhiteSpace(caseRoot))
                caseRoot = BuildDefaultCaseRoot();
            caseRoot = Path.GetFullPath(caseRoot);

            var caseName = GetOptional(options, "case-name", Path.GetFileName(caseRoot));
            if (string.IsNullOrWhiteSpace(caseName))
                caseName = "AutoImageTriage_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            Directory.CreateDirectory(caseRoot);
            Directory.CreateDirectory(Path.Combine(caseRoot, "Upload"));
            Directory.CreateDirectory(Path.Combine(caseRoot, "WorkingEvidence"));

            statusPath = Path.Combine(caseRoot, "Upload", "headless_run_status.txt");
            File.WriteAllText(statusPath,
                "status=started" + Environment.NewLine +
                "version=" + AppInfo.Version + Environment.NewLine +
                "case_root=" + caseRoot + Environment.NewLine +
                "image=" + imagePath + Environment.NewLine +
                "scan_mode=" + scanMode + Environment.NewLine +
                "started_utc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine);

            logPath = GetOptional(options, "log-path", Path.Combine(caseRoot, "Upload", "headless_image_triage.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? caseRoot);
            if (File.Exists(logPath)) File.Delete(logPath);

            void Log(string message)
            {
                var line = $"{DateTime.Now:HH:mm:ss} - {SanitizeLogLine(message)}";
                File.AppendAllText(logPath, line + Environment.NewLine);
                try { Console.WriteLine(line); } catch { }
            }

            Log($"Starting {AppInfo.DisplayName} headless image triage run.");
            Log($"Image: {imagePath}");
            Log($"CaseRoot: {caseRoot}");
            Log($"ScanMode: {scanMode}");

            var casePath = Path.Combine(caseRoot, "case.json");
            var dbPath = Path.Combine(caseRoot, "case.db");
            var workingDir = Path.Combine(caseRoot, "WorkingEvidence");

            var model = new CaseFile
            {
                CaseName = caseName,
                CaseNumber = GetOptional(options, "case-number", string.Empty),
                SubjectName = GetOptional(options, "subject", string.Empty),
                Company = GetOptional(options, "company", string.Empty),
                Investigator = GetOptional(options, "investigator", string.Empty),
                Description = $"Headless image triage run against {imagePath}",
                DatabasePath = CaseManager.StorePath(caseRoot, dbPath)
            };
            model.AuditTrail.Add(new CaseAuditEntry { TimestampUtc = DateTime.UtcNow.ToString("O"), Message = "Headless image triage case created." });

            var extracted = new List<ExtractedArtifact>();
            if (IsEwfImage(imagePath))
            {
                Log("E01/EWF input detected. Attempting TSK extraction route.");
                var tskExtracted = TskTriageCore.ExtractFromEwf(new[] { imagePath }, workingDir, Log);
                foreach (var item in tskExtracted)
                {
                    extracted.Add(new ExtractedArtifact { ExtractedPath = item.ExtractedPath, InternalPath = item.InternalPath });
                }
            }
            else
            {
                extracted.AddRange(ImageTriageCore.ExtractTargetedArtifacts(new[] { imagePath }, workingDir, Log, scanMode));
            }

            Log($"Extracted artifacts: {extracted.Count:N0}");
            foreach (var item in extracted)
            {
                if (string.IsNullOrWhiteSpace(item.ExtractedPath) || !File.Exists(item.ExtractedPath))
                    continue;

                var sourceType = scanMode == ImageArtifactScanMode.Full
                    ? "Headless Raw Image (Full Discovery)"
                    : "Headless Raw Image (Fast Triage)";
                if (IsEwfImage(imagePath)) sourceType = "Headless EWF/TSK";

                var source = CaseManager.CreateSourceRecord(caseRoot, item.ExtractedPath, sourceType);
                source.OriginalSourcePath = string.IsNullOrWhiteSpace(item.InternalPath) ? imagePath : item.InternalPath;
                source.OriginalCreatedUtc = item.CreatedUtc.HasValue ? item.CreatedUtc.Value.ToUniversalTime().ToString("O") : string.Empty;
                source.OriginalAccessedUtc = item.AccessedUtc.HasValue ? item.AccessedUtc.Value.ToUniversalTime().ToString("O") : string.Empty;
                source.OriginalModifiedUtc = item.ModifiedUtc.HasValue ? item.ModifiedUtc.Value.ToUniversalTime().ToString("O") : string.Empty;

                if (!model.Sources.Any(s => !string.IsNullOrWhiteSpace(s.HashValue) && s.HashValue.Equals(source.HashValue, StringComparison.OrdinalIgnoreCase)))
                    model.Sources.Add(source);
            }

            CaseManager.Save(casePath, model);
            Log($"Case saved before ingest: {casePath}");

            DatabaseCore.InitializeDatabase(dbPath);
            foreach (var s in model.Sources)
            {
                if (!Path.IsPathRooted(s.LocalPath))
                    s.LocalPath = Path.Combine(caseRoot, s.LocalPath);
            }

            if (!HasFlag(options, "skip-ingest"))
            {
                IngestEngine.ProcessEvidence(dbPath, model.Sources, TimeZoneInfo.Local.Id, Log);
                foreach (var s in model.Sources)
                    s.LocalPath = CaseManager.StorePath(caseRoot, s.LocalPath);
                CaseManager.Save(casePath, model);
                Log("Case saved after ingest.");
            }
            else
            {
                Log("skip-ingest was supplied. Evidence was staged and the case was saved, but parser ingest was not run.");
            }

            var validationBundleStatus = "skipped";
            var validationBundleError = string.Empty;
            if (!HasFlag(options, "skip-validation-bundle"))
            {
                var validationZip = Path.Combine(caseRoot, "Upload", SanitizeFileName(caseName) + "_validation_bundle.zip");
                try
                {
                    var result = ValidationBundleService.ExportValidationBundle(validationZip, model, caseRoot, dbPath, Log);
                    validationBundleStatus = "exported";
                    Log($"Validation bundle exported: {result.ZipPath} ({result.ZipBytes:N0} bytes).");
                }
                catch (Exception validationEx)
                {
                    validationBundleStatus = "failed";
                    validationBundleError = SanitizeLogLine(validationEx.Message);
                    Log("WARN: Validation bundle export failed after ingest; wrapper fallback may attempt standalone export. " + validationBundleError);
                    TryWriteText(Path.Combine(caseRoot, "Upload", "validation_bundle_export_error.txt"), validationEx.ToString());
                }
            }

            File.WriteAllText(statusPath,
                "status=complete" + Environment.NewLine +
                "version=" + AppInfo.Version + Environment.NewLine +
                "case_root=" + caseRoot + Environment.NewLine +
                "case_json=" + casePath + Environment.NewLine +
                "case_db=" + dbPath + Environment.NewLine +
                "image=" + imagePath + Environment.NewLine +
                "scan_mode=" + scanMode + Environment.NewLine +
                "validation_bundle_status=" + validationBundleStatus + Environment.NewLine +
                "validation_bundle_error=" + validationBundleError + Environment.NewLine +
                "completed_utc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine);

            Log("Headless image triage run complete.");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(logPath))
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + " - FATAL: " + ex + Environment.NewLine);
                if (!string.IsNullOrWhiteSpace(statusPath))
                    File.WriteAllText(statusPath, "status=failed" + Environment.NewLine + "version=" + AppInfo.Version + Environment.NewLine + "failed_utc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine + "error=" + SanitizeLogLine(ex.Message) + Environment.NewLine);
            }
            catch { }
            try { Console.Error.WriteLine(ex.ToString()); } catch { }
            return 2;
        }
    }


    public static int RunGoogleSourceTriage(string[] args)
    {
        var options = ParseArgs(args);
        var logPath = string.Empty;
        var statusPath = string.Empty;
        try
        {
            var googleRoot = GetRequired(options, "google-root");
            googleRoot = Path.GetFullPath(googleRoot);
            if (!Directory.Exists(googleRoot) && !File.Exists(googleRoot))
                throw new FileNotFoundException("Google source root/file does not exist.", googleRoot);

            var caseRoot = GetOptional(options, "case-root", string.Empty);
            if (string.IsNullOrWhiteSpace(caseRoot))
                caseRoot = BuildDefaultGoogleCaseRoot();
            caseRoot = Path.GetFullPath(caseRoot);

            var caseName = GetOptional(options, "case-name", Path.GetFileName(caseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            if (string.IsNullOrWhiteSpace(caseName))
                caseName = "GoogleSourceTriage_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            Directory.CreateDirectory(caseRoot);
            Directory.CreateDirectory(Path.Combine(caseRoot, "Upload"));
            Directory.CreateDirectory(Path.Combine(caseRoot, "WorkingEvidence"));

            statusPath = Path.Combine(caseRoot, "Upload", "headless_google_run_status.txt");
            logPath = GetOptional(options, "log-path", Path.Combine(caseRoot, "Upload", "headless_google_triage.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? caseRoot);
            if (File.Exists(logPath)) File.Delete(logPath);

            void Log(string message)
            {
                var line = $"{DateTime.Now:HH:mm:ss} - {SanitizeLogLine(message)}";
                File.AppendAllText(logPath, line + Environment.NewLine);
                try { Console.WriteLine(line); } catch { }
            }

            var runStartedUtc = DateTime.UtcNow;
            void WriteGoogleStatus(string status, string phase, string extra = "")
            {
                try
                {
                    var lines = new List<string>
                    {
                        "status=" + status,
                        "version=" + AppInfo.Version,
                        "phase=" + phase,
                        "case_root=" + caseRoot,
                        "google_root=" + googleRoot,
                        "started_utc=" + runStartedUtc.ToString("O"),
                        "updated_utc=" + DateTime.UtcNow.ToString("O")
                    };
                    if (!string.IsNullOrWhiteSpace(extra))
                    {
                        foreach (var part in extra.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            lines.Add(part);
                    }
                    File.WriteAllText(statusPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                }
                catch { }
            }

            WriteGoogleStatus("started", "initializing");

            Log($"Starting {AppInfo.DisplayName} headless Google source triage run.");
            Log($"GoogleRoot: {googleRoot}");
            Log($"CaseRoot: {caseRoot}");
            Log($"CaseName: {caseName}");

            var casePath = Path.Combine(caseRoot, "case.json");
            var dbPath = Path.Combine(caseRoot, "case.db");
            var hashSources = HasFlag(options, "hash-google-sources");
            var skipRisk = HasFlag(options, "skip-risk");
            var includeMbox = HasFlag(options, "include-mbox");
            Log(includeMbox
                ? "Google source discovery: MBOX files are included for this run."
                : "Google source discovery: MBOX files are excluded by default for fast Google parser validation. Re-run with --include-mbox when MBOX testing is intended.");
            WriteGoogleStatus("running", "discovering_sources");
            var candidateFiles = FindGoogleSourceCandidates(googleRoot, includeMbox, Log).ToList();
            WriteGoogleStatus("running", "sources_discovered", "candidate_sources=" + candidateFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (candidateFiles.Count == 0)
                throw new InvalidOperationException("No candidate Google source files were found. Expected Google audit/takeout/Gemini ZIP/CSV/JSON/HTML files; MBOX files are excluded unless --include-mbox is supplied.");

            var model = new CaseFile
            {
                CaseName = caseName,
                CaseNumber = GetOptional(options, "case-number", string.Empty),
                SubjectName = GetOptional(options, "subject", string.Empty),
                Company = GetOptional(options, "company", string.Empty),
                Investigator = GetOptional(options, "investigator", string.Empty),
                Description = $"Headless Google source triage run against {googleRoot}",
                DatabasePath = CaseManager.StorePath(caseRoot, dbPath)
            };
            model.AuditTrail.Add(new CaseAuditEntry { TimestampUtc = DateTime.UtcNow.ToString("O"), Message = "Headless Google source triage case created." });

            foreach (var file in candidateFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                var source = new SourceFileRecord
                {
                    FileName = info.Name,
                    LocalPath = info.FullName,
                    OriginalSourcePath = info.FullName,
                    SourceType = ClassifyGoogleSourceType(info.FullName),
                    FileSizeBytes = info.Length,
                    HashAlgorithm = hashSources ? "SHA256" : "SKIPPED_FOR_FAST_GOOGLE_TEST",
                    HashValue = hashSources ? ComputeSha256(info.FullName) : string.Empty,
                    Status = hashSources ? "Found" : "Found - Source hash skipped for fast Google test"
                };
                model.Sources.Add(source);
            }

            Log($"Candidate Google source files: {model.Sources.Count:N0}");
            Log(hashSources ? "Google source hashing: enabled" : "Google source hashing: skipped by default for fast parser validation. Re-run with --hash-google-sources for final validation.");

            CaseManager.Save(casePath, model);
            Log($"Case saved before ingest: {casePath}");

            WriteGoogleStatus("running", "initializing_database", "source_count=" + model.Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DatabaseCore.InitializeDatabase(dbPath);
            if (!HasFlag(options, "skip-ingest"))
            {
                WriteGoogleStatus("running", "ingesting_sources", "source_count=" + model.Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                IngestEngine.ProcessEvidence(dbPath, model.Sources, TimeZoneInfo.Local.Id, Log);
                CaseManager.Save(casePath, model);
                WriteGoogleStatus("running", "ingest_complete", "source_count=" + model.Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Log("Case saved after Google source ingest.");
            }
            else
            {
                WriteGoogleStatus("running", "ingest_skipped", "source_count=" + model.Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Log("skip-ingest was supplied. Google sources were indexed in the case, but parser ingest was not run.");
            }

            var riskStatus = "skipped";
            if (!skipRisk && !HasFlag(options, "skip-ingest"))
            {
                try
                {
                    WriteGoogleStatus("running", "risk_starting");
                    var risk = RiskEngine.Run(dbPath, null, Log, (processed, total, hits) =>
                    {
                        WriteGoogleStatus("running", "risk_running", "risk_processed=" + processed.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine + "risk_total=" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine + "risk_hits_so_far=" + hits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    });
                    riskStatus = $"complete:{risk.TotalHits}";
                    WriteGoogleStatus("running", "risk_complete", "risk_hits=" + risk.TotalHits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Log($"Risk engine completed. Hits: {risk.TotalHits:N0}.");
                }
                catch (Exception riskEx)
                {
                    riskStatus = "failed:" + SanitizeLogLine(riskEx.Message);
                    WriteGoogleStatus("running", "risk_failed", "risk_error=" + SanitizeLogLine(riskEx.Message));
                    Log("WARN: Risk engine failed after Google ingest. " + SanitizeLogLine(riskEx.Message));
                    TryWriteText(Path.Combine(caseRoot, "Upload", "google_risk_error.txt"), riskEx.ToString());
                }
            }
            else
            {
                WriteGoogleStatus("running", skipRisk ? "risk_skipped" : "risk_not_run", "risk_status=" + riskStatus);
            }

            var validationBundleStatus = "skipped";
            var validationBundleError = string.Empty;
            if (!HasFlag(options, "skip-validation-bundle"))
            {
                var validationZip = Path.Combine(caseRoot, "Upload", SanitizeFileName(caseName) + "_validation_bundle.zip");
                try
                {
                    WriteGoogleStatus("running", "validation_export_starting", "risk_status=" + riskStatus);
                    var result = ValidationBundleService.ExportValidationBundle(validationZip, model, caseRoot, dbPath, Log);
                    validationBundleStatus = "exported";
                    WriteGoogleStatus("running", "validation_export_complete", "risk_status=" + riskStatus + Environment.NewLine + "validation_bundle_bytes=" + result.ZipBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Log($"Validation bundle exported: {result.ZipPath} ({result.ZipBytes:N0} bytes). ");
                }
                catch (Exception validationEx)
                {
                    validationBundleStatus = "failed";
                    validationBundleError = SanitizeLogLine(validationEx.Message);
                    WriteGoogleStatus("running", "validation_export_failed", "risk_status=" + riskStatus + Environment.NewLine + "validation_bundle_error=" + validationBundleError);
                    Log("WARN: Validation bundle export failed after Google ingest; wrapper fallback may attempt standalone export. " + validationBundleError);
                    TryWriteText(Path.Combine(caseRoot, "Upload", "validation_bundle_export_error.txt"), validationEx.ToString());
                }
            }

            File.WriteAllText(statusPath,
                "status=complete" + Environment.NewLine +
                "version=" + AppInfo.Version + Environment.NewLine +
                "phase=complete" + Environment.NewLine +
                "case_root=" + caseRoot + Environment.NewLine +
                "case_json=" + casePath + Environment.NewLine +
                "case_db=" + dbPath + Environment.NewLine +
                "google_root=" + googleRoot + Environment.NewLine +
                "source_count=" + model.Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine +
                "mbox_included=" + includeMbox.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine +
                "risk_status=" + riskStatus + Environment.NewLine +
                "validation_bundle_status=" + validationBundleStatus + Environment.NewLine +
                "validation_bundle_error=" + validationBundleError + Environment.NewLine +
                "updated_utc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine +
                "completed_utc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine);

            Log("Headless Google source triage run complete.");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(logPath))
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + " - FATAL GOOGLE: " + ex + Environment.NewLine);
                if (!string.IsNullOrWhiteSpace(statusPath))
                    File.WriteAllText(statusPath, "status=failed" + Environment.NewLine + "version=" + AppInfo.Version + Environment.NewLine + "failed_utc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine + "error=" + SanitizeLogLine(ex.Message) + Environment.NewLine);
            }
            catch { }
            try { Console.Error.WriteLine(ex.ToString()); } catch { }
            return 2;
        }
    }


    public static int ExportValidationBundle(string[] args)
    {
        var options = ParseArgs(args);
        var logPath = string.Empty;
        try
        {
            var caseRoot = GetRequired(options, "case-root");
            caseRoot = Path.GetFullPath(caseRoot);
            var casePath = GetOptional(options, "case-json", Path.Combine(caseRoot, "case.json"));
            var dbPath = GetOptional(options, "case-db", Path.Combine(caseRoot, "case.db"));
            var zipPath = GetOptional(options, "validation-bundle-zip", string.Empty);
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                var caseNameForPath = Path.GetFileName(caseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                zipPath = Path.Combine(caseRoot, "Upload", SanitizeFileName(caseNameForPath) + "_validation_bundle.zip");
            }
            zipPath = Path.GetFullPath(zipPath);
            logPath = GetOptional(options, "log-path", Path.Combine(caseRoot, "Upload", "headless_image_triage.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? caseRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? caseRoot);

            void Log(string message)
            {
                var line = $"{DateTime.Now:HH:mm:ss} - {SanitizeLogLine(message)}";
                File.AppendAllText(logPath, line + Environment.NewLine);
                try { Console.WriteLine(line); } catch { }
            }

            if (!File.Exists(casePath))
                throw new FileNotFoundException("Case JSON was not found for validation bundle export.", casePath);

            var model = CaseManager.Load(casePath);
            if (string.IsNullOrWhiteSpace(model.CaseName))
                model.CaseName = Path.GetFileName(caseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            Log($"Starting standalone validation bundle export for case: {caseRoot}");
            Log($"Case JSON: {casePath}");
            Log($"Case DB: {dbPath}");
            Log($"Validation ZIP: {zipPath}");
            var result = ValidationBundleService.ExportValidationBundle(zipPath, model, caseRoot, dbPath, Log);
            Log($"Standalone validation bundle exported: {result.ZipPath} ({result.ZipBytes:N0} bytes).");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(logPath))
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + " - FATAL validation export: " + ex + Environment.NewLine);
            }
            catch { }
            try { Console.Error.WriteLine(ex.ToString()); } catch { }
            return 2;
        }
    }


    private static IEnumerable<string> FindGoogleSourceCandidates(string root, bool includeMbox, Action<string> log)
    {
        if (File.Exists(root))
        {
            if (IsGoogleSourceCandidate(root, includeMbox, log)) yield return Path.GetFullPath(root);
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            log("WARN: Could not enumerate Google root. " + SanitizeLogLine(ex.Message));
            yield break;
        }

        foreach (var file in files)
        {
            bool include;
            try { include = IsGoogleSourceCandidate(file, includeMbox, log); }
            catch { include = false; }
            if (include) yield return Path.GetFullPath(file);
        }
    }

    private static bool IsGoogleSourceCandidate(string path, bool includeMbox, Action<string>? log = null)
    {
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileName(lower) ?? string.Empty;
        var ext = Path.GetExtension(lower);

        if (ext == ".zip")
            return lower.Contains("google") || lower.Contains("audit") || lower.Contains("investigation") || lower.Contains("takeout") || lower.Contains("gemini") || name.StartsWith("part", StringComparison.Ordinal);

        if (ext == ".csv")
            return lower.Contains("audit") || lower.Contains("investigation") || lower.Contains("takeout") || lower.Contains("activities") || lower.Contains("devices") || lower.Contains("google") || lower.Contains("gemini");

        if (ext == ".json")
            return lower.Contains("takeout") || lower.Contains("google") || lower.Contains("filters.json") || lower.Contains("blocked addresses.json") || lower.Contains("vacation responder.json") || lower.Contains("gemini");

        if (ext == ".html" || ext == ".htm" || ext == ".rtf" || ext == ".txt" || ext == ".pdf" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".ics")
            return lower.Contains("takeout") || lower.Contains("google") || lower.Contains("gemini") || lower.Contains("calendar");

        if (ext == ".mbox")
        {
            var isGoogleMbox = lower.Contains("mail") || lower.Contains("takeout") || lower.Contains("google");
            if (isGoogleMbox && !includeMbox)
            {
                log?.Invoke("Skipping MBOX source for fast Google test: " + Path.GetFileName(path));
                return false;
            }
            return isGoogleMbox;
        }

        return false;
    }

    private static string ClassifyGoogleSourceType(string path)
    {
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        if (lower.Contains("audit") || lower.Contains("investigation")) return "Google Workspace Audit / Investigation CSV or ZIP";
        if (lower.Contains("gemini")) return "Gemini Session Archive";
        if (lower.Contains("takeout") || lower.Contains("mail")) return "Google Takeout Archive / Export Files";
        return "Google Source File";
    }

    private static string BuildDefaultGoogleCaseRoot()
    {
        var root = Directory.Exists(@"Q:\") ? @"Q:\TriageCase" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VestigantTriageCases");
        return Path.Combine(root, "GoogleSourceTriage_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = token[2..].Trim();
            if (key.Length == 0) continue;
            var value = "true";
            var equalsIndex = key.IndexOf('=');
            if (equalsIndex >= 0)
            {
                value = key[(equalsIndex + 1)..];
                key = key[..equalsIndex];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            result[key] = value;
        }
        return result;
    }

    private static string GetRequired(Dictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) || ContainsPlaceholder(value))
            throw new ArgumentException($"Missing required --{key} value.");
        return value;
    }

    private static string GetOptional(Dictionary<string, string> args, string key, string defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) || ContainsPlaceholder(value))
            return defaultValue;
        return value;
    }

    private static bool HasFlag(Dictionary<string, string> args, string key)
    {
        return args.TryGetValue(key, out var value) && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
    }

    private static bool ContainsPlaceholder(string value) => value.Contains('<') || value.Contains('>');

    private static bool IsEwfImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".e01", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ex01", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".s01", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".l01", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDefaultCaseRoot()
    {
        var root = Directory.Exists(@"Q:\") ? @"Q:\TriageCase" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VestigantTriageCases");
        return Path.Combine(root, "AutoImageTriage_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    }

    private static void TryWriteText(string path, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text);
        }
        catch { }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray();
        var cleaned = new string(chars).Trim('_', ' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "VestigantCase" : cleaned;
    }

    private static string SanitizeLogLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var chars = value.Select(ch => char.IsControl(ch) && ch != '\t' ? ' ' : ch).ToArray();
        return new string(chars).Replace("\r", " ").Replace("\n", " ");
    }
}
