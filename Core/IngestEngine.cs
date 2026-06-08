using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

public static class IngestEngine
{
    private static readonly object DatabaseWriteLock = new();
    private static readonly object LogLock = new();

    public static void ProcessEvidence(string dbPath, List<SourceFileRecord> sources, string localTzName, Action<string> log)
    {
        var originalLog = log;
        log = message =>
        {
            lock (LogLock)
                originalLog(SanitizeSingleLineValue(message));
        };

        log("Starting Forensic Ingestion Engine...");
        DatabaseCore.InitializeDatabase(dbPath);
        SqliteConnection.ClearAllPools();

        var pending = sources.Where(s => !s.ImportedToDb).ToList();
        if (pending.Count == 0)
        {
            log("No pending evidence sources to ingest.");
            return;
        }

        var serialSources = pending.Where(RequiresSerialIngest).ToList();
        var serialSet = new HashSet<SourceFileRecord>(serialSources);
        var parallelSources = pending.Where(s => !serialSet.Contains(s)).ToList();
        var smallParallelSources = parallelSources.Where(s => !IsEvtxSource(s)).OrderBy(ParallelIngestPriority).ThenBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var evtxParallelSources = parallelSources.Where(IsEvtxSource).OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var maxWorkers = Math.Max(1, Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1)));
        var evtxWorkers = Math.Max(1, Math.Min(2, Math.Max(1, Environment.ProcessorCount / 2)));

        if (serialSources.Count > 0)
            log($"  - Serial ingest group: {serialSources.Count:N0} source(s) requiring ordered/direct database import.");
        foreach (var source in serialSources)
            ProcessSingleSource(source, dbPath, localTzName, log);

        ProcessParallelGroup("small artifact ingest group", smallParallelSources, maxWorkers, dbPath, localTzName, log);
        ProcessParallelGroup("EVTX ingest group", evtxParallelSources, evtxWorkers, dbPath, localTzName, log);

        string caseFolder = Path.GetDirectoryName(dbPath) ?? "";
        if (IsImageBackedEvidenceSet(sources))
            WriteImageBackedArchiveSkipManifest(caseFolder, sources, log);
        else
            ArchiveWith7Zip(Path.Combine(caseFolder, "WorkingEvidence"), caseFolder, log);
        log("Ingestion complete. Master timeline database populated.");
    }

    private static void ProcessParallelGroup(string label, List<SourceFileRecord> group, int maxWorkers, string dbPath, string localTzName, Action<string> log)
    {
        if (group.Count == 0)
            return;

        log($"  - Parallel {label}: {group.Count:N0} source(s), max workers: {maxWorkers:N0}. Parser reads run in parallel; SQLite writes remain serialized for database safety.");
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxWorkers };
        Parallel.ForEach(group, options, source => ProcessSingleSource(source, dbPath, localTzName, log));
    }

    private static int ParallelIngestPriority(SourceFileRecord source)
    {
        var name = source.FileName ?? string.Empty;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (IsOfficeOwnerFileSource(source)) return 0;
        if (ext == ".lnk" || ext == ".pf") return 1;
        if (ext.EndsWith("destinations-ms", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("history", StringComparison.OrdinalIgnoreCase) || name.Contains("consolehost_history", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static bool IsEvtxSource(SourceFileRecord source)
    {
        var name = source.FileName ?? string.Empty;
        var original = source.OriginalSourcePath ?? string.Empty;
        return name.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase) || original.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresSerialIngest(SourceFileRecord source)
    {
        var sourceType = source.SourceType ?? string.Empty;
        var name = source.FileName ?? string.Empty;
        var ext = Path.GetExtension(name);
        if (sourceType.Contains("O365", StringComparison.OrdinalIgnoreCase) ||
            sourceType.Contains("Unified Audit", StringComparison.OrdinalIgnoreCase) ||
            sourceType.Contains("UAL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsRegistryHive(name))
            return true;

        return false;
    }

    private static bool IsOfficeOwnerFileSource(SourceFileRecord source)
    {
        var values = new[]
        {
            source.FileName,
            source.OriginalSourcePath,
            source.LocalPath,
            source.SourceType,
            source.ParserName
        };

        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var normalized = value.Replace('/', '\\');
            var fileName = Path.GetFileName(normalized) ?? string.Empty;
            if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.Contains("_~$", StringComparison.OrdinalIgnoreCase) || normalized.Contains("\\~$", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.Contains("[DELETED MFT OWNER]", StringComparison.OrdinalIgnoreCase) || normalized.Contains("GhostOwner_", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.Equals("Office Owner File", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static void ProcessSingleSource(SourceFileRecord source, string dbPath, string localTzName, Action<string> log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source.LocalPath) || !File.Exists(source.LocalPath))
            {
                source.Status = "Missing";
                log($"  ! Source file missing: {source.FileName} ({source.LocalPath})");
                return;
            }

            string forensicStatus = DetermineForensicStatus(source);
            int importedEvents = 0;
            string parserName = string.Empty;

            if (IsOfficeOwnerFileSource(source))
            {
                lock (DatabaseWriteLock)
                {
                    importedEvents = IngestOwnerFile(source, dbPath, forensicStatus, localTzName, log);
                }
                parserName = "Office Owner File";
            }
            else
            {
                var parsers = ParserRegistry.SelectParsersForEvidence(source.LocalPath, source.OriginalSourcePath);
                if (parsers.Count > 0)
                {
                    var parserNames = new List<string>();
                    var matchedButNoEvents = new List<string>();

                    foreach (var parser in parsers)
                    {
                        parserNames.Add(parser.ParserName);
                        log($"  - Processing {parser.ParserName}: {source.FileName} ({forensicStatus})");

                        if (parser is O365UalParser || parser is GoogleWorkspaceAuditParser || parser is GoogleTakeoutParser)
                        {
                            IngestResult result;
                            lock (DatabaseWriteLock)
                            {
                                result = DatabaseIngest.ImportParsedFile(parser, source.LocalPath, dbPath, localTzName, append: true, log);
                            }
                            importedEvents += result.RowsImported;
                            log($"  ✓ [{forensicStatus}] {parser.ParserName}: {result.RowsImported:N0} rows imported.");
                            if (result.RowsImported == 0) matchedButNoEvents.Add(parser.ParserName);
                        }
                        else
                        {
                            var parseWatch = Stopwatch.StartNew();
                            var parsedEvents = parser.Parse(source.LocalPath, localTzName, log).ToList();
                            int inserted;
                            lock (DatabaseWriteLock)
                            {
                                inserted = IngestNormalizedEvents(parsedEvents, dbPath, forensicStatus, source, localTzName, parser.ParserName);
                            }
                            parseWatch.Stop();
                            importedEvents += inserted;
                            log($"  ✓ [{forensicStatus}] {parser.ParserName}: {inserted:N0} events imported in {parseWatch.Elapsed.TotalSeconds:N1}s.");
                            if (inserted == 0) matchedButNoEvents.Add(parser.ParserName);
                        }
                    }

                    parserName = string.Join("; ", parserNames.Distinct(StringComparer.OrdinalIgnoreCase));
                    if (importedEvents == 0)
                    {
                        var reason = matchedButNoEvents.Count == 0
                            ? "Parser matched but emitted no events"
                            : $"Parser matched but emitted no events: {string.Join("; ", matchedButNoEvents)}";
                        lock (DatabaseWriteLock)
                        {
                            IngestMetadataFallback(source, dbPath, forensicStatus, reason);
                        }
                        log($"  ! {reason}. Metadata fallback row inserted for auditability.");
                        importedEvents = 1;
                    }
                }
                else
                {
                    string typeStr = ClassifyFallback(source);
                    parserName = "Metadata Fallback";
                    log($"  ! No parser matched {source.FileName}. Indexing metadata only as {typeStr}.");
                    lock (DatabaseWriteLock)
                    {
                        IngestMetadataFallback(source, dbPath, forensicStatus, typeStr);
                    }
                    importedEvents = 1;
                }
            }

            source.ParserName = parserName;
            source.EventsImported = importedEvents;
            source.LastIngestUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            source.ImportedToDb = true;
            source.Status = importedEvents > 0 ? "Ingested" : "No Events";
        }
        catch (Exception ex)
        {
            source.Status = "Error";
            source.LastIngestUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            log($"  ! Ingest Failure on {source.FileName}: {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string DetermineForensicStatus(SourceFileRecord source)
    {
        var joined = $"{source.OriginalSourcePath} {source.Status}";
        return joined.Contains("[DELETED]", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("Recovered", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            ? "Deleted/Recovered"
            : "Live";
    }

    private static int IngestNormalizedEvents(IEnumerable<NormalizedEvent> events, string dbPath, string status, SourceFileRecord source, string localTzName, string parserName)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var transaction = conn.BeginTransaction();

        using var cmdEvent = conn.CreateCommand();
        cmdEvent.Transaction = transaction;
        cmdEvent.CommandText = @"
            INSERT INTO events (
                data_source, user_id, operation, object_id, creation_date_utc, creation_date_local,
                event_time_basis, event_time_confidence, is_behavioral_timestamp, timestamp_warning,
                forensic_status, source_file, client_ip, workload, category, source_relative_url,
                file_name, file_size_bytes, result_status, raw_json
            )
            VALUES (
                $ds, $u, $op, $obj, $dutc, $dlocal, $basis, $timeconfidence, $behavioral, $timewarning, $stat, $s, $ip, $workload, $category,
                $source_relative_url, $file_name, $file_size_bytes, $result_status, $raw_json
            )
            RETURNING event_id;";

        using var cmdField = conn.CreateCommand();
        cmdField.Transaction = transaction;
        cmdField.CommandText = "INSERT INTO event_fields (event_id, field_name, field_value) VALUES ($eid, $fn, $fv)";
        var pEid = cmdField.Parameters.Add("$eid", SqliteType.Integer);
        var pFn = cmdField.Parameters.Add("$fn", SqliteType.Text);
        var pFv = cmdField.Parameters.Add("$fv", SqliteType.Text);

        var tz = TimeUtil.EnsureTimeZone(localTzName);
        var count = 0;

        foreach (var e in events)
        {
            count++;
            var fields = e.AdditionalFields ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parsedTargetPath = FirstNonBlank(
                Get(fields, "TargetPath"),
                Get(fields, "ObjectId"),
                Get(fields, "OriginalPath"),
                Get(fields, "Original Path"),
                Get(fields, "Path"),
                Get(fields, "SourceRelativeUrl"),
                Get(fields, "DestinationRelativeUrl"));

            var objectPath = ResolveDisplayTarget(
                FirstNonBlank(parsedTargetPath, e.ObjectPath, source.OriginalSourcePath, source.FileName),
                source);

            var timestampVerdict = TimestampProvenance.Evaluate(e, fields, parserName, source.LocalPath);
            var effectiveTimestampUtc = timestampVerdict.TimestampUtc;
            fields["EventTimeBasis"] = timestampVerdict.Basis;
            fields["EventTimeConfidence"] = timestampVerdict.Confidence;
            fields["IsBehavioralTimestamp"] = timestampVerdict.IsBehavioral ? "Yes" : "No";
            if (!string.IsNullOrWhiteSpace(timestampVerdict.Warning)) fields["TimestampWarning"] = timestampVerdict.Warning;

            var timestampUtc = effectiveTimestampUtc == DateTime.MinValue ? string.Empty : effectiveTimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var timestampLocal = effectiveTimestampUtc == DateTime.MinValue ? string.Empty : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(effectiveTimestampUtc, DateTimeKind.Utc), tz).ToString("MM/dd/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
            var fileName = FirstNonBlank(Get(fields, "FileName"), Get(fields, "TargetFileName"), Get(fields, "OriginalFileName"), SafeFileNameFromPath(objectPath), source.FileName);
            var sourceRelativeUrl = ResolveDisplayTarget(FirstNonBlank(parsedTargetPath, objectPath, source.OriginalSourcePath), source);
            var fileSize = TryParseLong(FirstNonBlank(Get(fields, "FileSizeBytes"), Get(fields, "Target File Size (Bytes)"), Get(fields, "File Size"), Get(fields, "Size")));
            var category = FirstNonBlank(Get(fields, "EventCategory"), InferCategory(e.DataSource, e.Operation));
            var workload = FirstNonBlank(Get(fields, "Workload"), e.DataSource.StartsWith("O365", StringComparison.OrdinalIgnoreCase) ? "Cloud" : "Endpoint");
            var resultStatus = fields.ContainsKey("ParseError") ? "ParseError" : "Parsed";
            var rawJson = Get(fields, "AuditData");
            if (string.IsNullOrWhiteSpace(rawJson) && fields.Count > 0)
            {
                try { rawJson = JsonSerializer.Serialize(fields); }
                catch { rawJson = string.Empty; }
            }

            cmdEvent.Parameters.Clear();
            cmdEvent.Parameters.AddWithValue("$ds", FirstNonBlank(e.DataSource, "Unknown"));
            cmdEvent.Parameters.AddWithValue("$u", FirstNonBlank(e.UserId, "Unknown"));
            cmdEvent.Parameters.AddWithValue("$op", FirstNonBlank(e.Operation, "Unknown"));
            cmdEvent.Parameters.AddWithValue("$obj", objectPath);
            cmdEvent.Parameters.AddWithValue("$dutc", timestampUtc);
            cmdEvent.Parameters.AddWithValue("$dlocal", timestampLocal);
            cmdEvent.Parameters.AddWithValue("$basis", timestampVerdict.Basis);
            cmdEvent.Parameters.AddWithValue("$timeconfidence", timestampVerdict.Confidence);
            cmdEvent.Parameters.AddWithValue("$behavioral", timestampVerdict.IsBehavioral ? 1 : 0);
            cmdEvent.Parameters.AddWithValue("$timewarning", timestampVerdict.Warning);
            cmdEvent.Parameters.AddWithValue("$stat", status);
            cmdEvent.Parameters.AddWithValue("$s", source.FileName);
            cmdEvent.Parameters.AddWithValue("$ip", e.ClientIp ?? string.Empty);
            cmdEvent.Parameters.AddWithValue("$workload", workload);
            cmdEvent.Parameters.AddWithValue("$category", category);
            cmdEvent.Parameters.AddWithValue("$source_relative_url", sourceRelativeUrl);
            cmdEvent.Parameters.AddWithValue("$file_name", fileName);
            cmdEvent.Parameters.AddWithValue("$file_size_bytes", (object?)fileSize ?? DBNull.Value);
            cmdEvent.Parameters.AddWithValue("$result_status", resultStatus);
            cmdEvent.Parameters.AddWithValue("$raw_json", rawJson);

            long eventId = Convert.ToInt64(cmdEvent.ExecuteScalar());

            AddField(cmdField, pEid, pFn, pFv, eventId, "ParserName", parserName);
            AddField(cmdField, pEid, pFn, pFv, eventId, "DisplayTarget", objectPath);
            AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalSourcePath", source.OriginalSourcePath);
            AddField(cmdField, pEid, pFn, pFv, eventId, "LocalEvidencePath", source.LocalPath);
            AddField(cmdField, pEid, pFn, pFv, eventId, "SourceHashSHA256", source.HashValue);
            AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalCreatedUtc", source.OriginalCreatedUtc);
            AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalAccessedUtc", source.OriginalAccessedUtc);
            AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalModifiedUtc", source.OriginalModifiedUtc);
            AddField(cmdField, pEid, pFn, pFv, eventId, "EventTimeBasis", timestampVerdict.Basis);
            AddField(cmdField, pEid, pFn, pFv, eventId, "EventTimeConfidence", timestampVerdict.Confidence);
            AddField(cmdField, pEid, pFn, pFv, eventId, "IsBehavioralTimestamp", timestampVerdict.IsBehavioral ? "Yes" : "No");
            if (!string.IsNullOrWhiteSpace(timestampVerdict.Warning))
                AddField(cmdField, pEid, pFn, pFv, eventId, "TimestampWarning", timestampVerdict.Warning);
            AddField(cmdField, pEid, pFn, pFv, eventId, "ForensicStatus", status);

            foreach (var kvp in fields)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                    continue;

                AddField(cmdField, pEid, pFn, pFv, eventId, kvp.Key, kvp.Value ?? string.Empty);
            }
        }

        transaction.Commit();
        return count;
    }

    private static void AddField(SqliteCommand cmdField, SqliteParameter pEid, SqliteParameter pFn, SqliteParameter pFv, long eventId, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        pEid.Value = eventId;
        pFn.Value = name;
        pFv.Value = value ?? string.Empty;
        cmdField.ExecuteNonQuery();
    }

    private static void IngestMetadataFallback(SourceFileRecord source, string dbPath, string status, string typeStr)
    {
        var info = new FileInfo(source.LocalPath);
        using var conn = DatabaseCore.Open(dbPath);
        using var transaction = conn.BeginTransaction();

        using var cmdEvent = conn.CreateCommand();
        cmdEvent.Transaction = transaction;
        cmdEvent.CommandText = @"
            INSERT INTO events (data_source, user_id, operation, object_id, creation_date_utc, creation_date_local, event_time_basis, event_time_confidence, is_behavioral_timestamp, timestamp_warning, forensic_status, source_file, file_name, file_size_bytes, result_status)
            VALUES ($ds, 'System', 'File Discovered', $o, '', '', 'MetadataFallback', 'MetadataOnly', 0, 'Metadata fallback row only; working-copy timestamps suppressed.', $stat, $s, $fn, $size, 'MetadataOnly')
            RETURNING event_id;";

        cmdEvent.Parameters.AddWithValue("$ds", $"Metadata: {typeStr}");
        cmdEvent.Parameters.AddWithValue("$o", string.IsNullOrWhiteSpace(source.OriginalSourcePath) ? source.LocalPath : source.OriginalSourcePath);
        cmdEvent.Parameters.AddWithValue("$stat", status);
        cmdEvent.Parameters.AddWithValue("$s", source.FileName);
        cmdEvent.Parameters.AddWithValue("$fn", source.FileName);
        cmdEvent.Parameters.AddWithValue("$size", info.Exists ? info.Length : 0);

        long eventId = Convert.ToInt64(cmdEvent.ExecuteScalar());

        using var cmdField = conn.CreateCommand();
        cmdField.Transaction = transaction;
        cmdField.CommandText = "INSERT INTO event_fields (event_id, field_name, field_value) VALUES ($eid, $fn, $fv)";
        var pEid = cmdField.Parameters.Add("$eid", SqliteType.Integer);
        var pFn = cmdField.Parameters.Add("$fn", SqliteType.Text);
        var pFv = cmdField.Parameters.Add("$fv", SqliteType.Text);

        AddField(cmdField, pEid, pFn, pFv, eventId, "DisplayTarget", string.IsNullOrWhiteSpace(source.OriginalSourcePath) ? source.FileName : source.OriginalSourcePath);
        AddField(cmdField, pEid, pFn, pFv, eventId, "Original Path", source.OriginalSourcePath);
        AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalSourcePath", source.OriginalSourcePath);
        AddField(cmdField, pEid, pFn, pFv, eventId, "Local Path", source.LocalPath);
        AddField(cmdField, pEid, pFn, pFv, eventId, "LocalEvidencePath", source.LocalPath);
        AddField(cmdField, pEid, pFn, pFv, eventId, "File Size", info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "0");
        AddField(cmdField, pEid, pFn, pFv, eventId, "SHA256", source.HashValue);
        AddField(cmdField, pEid, pFn, pFv, eventId, "File Type", typeStr);
        AddField(cmdField, pEid, pFn, pFv, eventId, "Forensic Status", status);
        AddField(cmdField, pEid, pFn, pFv, eventId, "EventTimeBasis", "MetadataFallback");
        AddField(cmdField, pEid, pFn, pFv, eventId, "EventTimeConfidence", "MetadataOnly");
        AddField(cmdField, pEid, pFn, pFv, eventId, "IsBehavioralTimestamp", "No");
        AddField(cmdField, pEid, pFn, pFv, eventId, "TimestampWarning", "Metadata fallback row only; working-copy timestamps suppressed.");

        transaction.Commit();
    }

    private static int IngestOwnerFile(SourceFileRecord source, string dbPath, string status, string localTzName, Action<string> log)
    {
        string activeUser = SanitizeSingleLineValue(OfficeArtifacts.ParseOwnerFile(source.LocalPath));
        var info = new FileInfo(source.LocalPath);
        var ownerCreatedUtc = TimeUtil.ParseUtc(source.OriginalCreatedUtc);
        var ownerModifiedUtc = TimeUtil.ParseUtc(source.OriginalModifiedUtc);
        var eventUtc = ownerCreatedUtc ?? ownerModifiedUtc;
        var tz = TimeUtil.EnsureTimeZone(localTzName);
        var eventUtcText = eventUtc.HasValue ? eventUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "";
        var eventLocalText = eventUtc.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(eventUtc.Value, tz).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
        var timeBasis = eventUtc.HasValue ? "OfficeOwnerFileOriginalFileTime" : "OfficeOwnerFileMetadata";
        var timeConfidence = eventUtc.HasValue ? "FileSystemMetadata" : "MetadataOnly";
        var isBehavioral = eventUtc.HasValue ? 1 : 0;
        var warning = eventUtc.HasValue
            ? "Owner lock file timestamp is original filesystem metadata for the lock file; interpret as document-open proximity evidence and corroborate with LNK, Office MRU, cloud sync, or file system activity."
            : "Owner lock file indicates document-open state, but copied file timestamps are not used as behavioral event time.";

        using var conn = DatabaseCore.Open(dbPath);
        using var transaction = conn.BeginTransaction();

        using var cmdEvent = conn.CreateCommand();
        cmdEvent.Transaction = transaction;
        cmdEvent.CommandText = @"
            INSERT INTO events (data_source, user_id, operation, object_id, creation_date_utc, creation_date_local, event_time_basis, event_time_confidence, is_behavioral_timestamp, timestamp_warning, forensic_status, source_file, file_name, file_size_bytes, result_status, workload, category)
            VALUES ('Office Owner File', $u, 'Document Open Lock', $o, $utc, $local, $basis, $confidence, $behavioral, $warning, $stat, $s, $fn, $size, 'Parsed', 'Endpoint', 'DocumentActivity')
            RETURNING event_id;";

        var targetDocument = ReconstructOwnerTargetDocument(source.FileName, source.OriginalSourcePath);
        cmdEvent.Parameters.AddWithValue("$u", activeUser);
        cmdEvent.Parameters.AddWithValue("$o", targetDocument);
        cmdEvent.Parameters.AddWithValue("$utc", eventUtcText);
        cmdEvent.Parameters.AddWithValue("$local", eventLocalText);
        cmdEvent.Parameters.AddWithValue("$basis", timeBasis);
        cmdEvent.Parameters.AddWithValue("$confidence", timeConfidence);
        cmdEvent.Parameters.AddWithValue("$behavioral", isBehavioral);
        cmdEvent.Parameters.AddWithValue("$warning", warning);
        cmdEvent.Parameters.AddWithValue("$stat", status);
        cmdEvent.Parameters.AddWithValue("$s", source.FileName);
        cmdEvent.Parameters.AddWithValue("$fn", Path.GetFileName(targetDocument));
        cmdEvent.Parameters.AddWithValue("$size", info.Exists ? info.Length : 0);

        long eventId = Convert.ToInt64(cmdEvent.ExecuteScalar());

        using var cmdField = conn.CreateCommand();
        cmdField.Transaction = transaction;
        cmdField.CommandText = "INSERT INTO event_fields (event_id, field_name, field_value) VALUES ($eid, $fn, $fv)";
        var pEid = cmdField.Parameters.Add("$eid", SqliteType.Integer);
        var pFn = cmdField.Parameters.Add("$fn", SqliteType.Text);
        var pFv = cmdField.Parameters.Add("$fv", SqliteType.Text);

        AddField(cmdField, pEid, pFn, pFv, eventId, "ParserName", "Office Owner File");
        AddField(cmdField, pEid, pFn, pFv, eventId, "ArtifactType", "OfficeOwnerLockFile");
        AddField(cmdField, pEid, pFn, pFv, eventId, "Active User", activeUser);
        AddField(cmdField, pEid, pFn, pFv, eventId, "Target Document", targetDocument);
        AddField(cmdField, pEid, pFn, pFv, eventId, "OwnerFileName", source.FileName);
        AddField(cmdField, pEid, pFn, pFv, eventId, "Original Path", source.OriginalSourcePath);
        AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalCreatedUtc", source.OriginalCreatedUtc);
        AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalAccessedUtc", source.OriginalAccessedUtc);
        AddField(cmdField, pEid, pFn, pFv, eventId, "OriginalModifiedUtc", source.OriginalModifiedUtc);
        AddField(cmdField, pEid, pFn, pFv, eventId, "SourceHashSHA256", source.HashValue);
        AddField(cmdField, pEid, pFn, pFv, eventId, "EventTimeBasis", timeBasis);
        AddField(cmdField, pEid, pFn, pFv, eventId, "EventTimeConfidence", timeConfidence);
        AddField(cmdField, pEid, pFn, pFv, eventId, "IsBehavioralTimestamp", isBehavioral == 1 ? "Yes" : "No");
        AddField(cmdField, pEid, pFn, pFv, eventId, "TimestampWarning", warning);

        transaction.Commit();
        log($"  ✓ [{status}] Office owner lock evidence: user '{activeUser}' for '{SanitizeSingleLineValue(Path.GetFileName(targetDocument))}'.");
        return 1;
    }

    private static string SanitizeSingleLineValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = new List<char>(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            var unsafeSeparator = category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned;

            if (ch == '\uFFFD' || char.IsControl(ch) || unsafeSeparator || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && chars.Count > 0)
                {
                    chars.Add(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            chars.Add(ch);
            lastWasSpace = false;
        }

        var cleaned = new string(chars.ToArray()).Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        return cleaned;
    }

    private static string ReconstructOwnerTargetDocument(string ownerFileName, string originalSourcePath)
    {
        var normalizedOriginal = (originalSourcePath ?? string.Empty).Replace('/', '\\');
        var originalFileName = Path.GetFileName(normalizedOriginal) ?? string.Empty;
        var stagedFileName = ownerFileName ?? string.Empty;
        var fileName = originalFileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) || originalFileName.Contains("~$", StringComparison.OrdinalIgnoreCase)
            ? originalFileName
            : stagedFileName;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = originalFileName;

        var targetName = fileName.StartsWith("~$", StringComparison.Ordinal) ? fileName[2..] : fileName.Replace("~$", "", StringComparison.Ordinal);
        var dir = string.IsNullOrWhiteSpace(normalizedOriginal) ? string.Empty : Path.GetDirectoryName(normalizedOriginal) ?? string.Empty;
        if (dir.StartsWith("[DELETED MFT OWNER]", StringComparison.OrdinalIgnoreCase))
            dir = dir.Substring("[DELETED MFT OWNER]".Length).Trim();
        return string.IsNullOrWhiteSpace(dir) ? targetName : dir.TrimEnd('\\') + "\\" + targetName;
    }

    private static string ClassifyFallback(SourceFileRecord source)
    {
        string ext = Path.GetExtension(source.FileName).ToLowerInvariant();
        if (IsRegistryHive(source.FileName)) return "Registry Hive";
        if (ext == ".evtx") return "Event Log";
        if (ext is ".spl" or ".shd" or ".emf" or ".xps" or ".oxps" or ".prn") return "Print Spool";
        if (ext == ".pf") return "Prefetch";
        if (ext == ".lnk" || source.FileName.Contains("Destinations-ms", StringComparison.OrdinalIgnoreCase)) return "Shell Link / Jump List";
        if (source.SourceType.Contains("UAL", StringComparison.OrdinalIgnoreCase) || ext == ".csv") return "CSV";
        return "Unclassified";
    }

    private static bool IsRegistryHive(string name)
    {
        string n = (Path.GetFileName(name) ?? string.Empty).ToUpperInvariant();
        return n.EndsWith("SYSTEM", StringComparison.Ordinal) ||
               n.EndsWith("SOFTWARE", StringComparison.Ordinal) ||
               n.EndsWith("SAM", StringComparison.Ordinal) ||
               n.EndsWith("SECURITY", StringComparison.Ordinal) ||
               n.EndsWith("NTUSER.DAT", StringComparison.Ordinal) ||
               n.Contains("USRCLASS.DAT", StringComparison.Ordinal);
    }

    private static string Get(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static string FirstNonBlank(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static long? TryParseLong(string s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string ResolveDisplayTarget(string candidate, SourceFileRecord source)
    {
        candidate = ForensicText.CleanDisplayValue(candidate);

        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, source.LocalPath, StringComparison.OrdinalIgnoreCase) ||
            ForensicText.IsLocalWorkingEvidencePath(candidate))
        {
            candidate = FirstNonBlank(source.OriginalSourcePath, source.FileName);
        }

        if (string.IsNullOrWhiteSpace(candidate))
            candidate = source.FileName;

        return candidate;
    }

    private static string SafeFileNameFromPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.LocalPath))
            return Path.GetFileName(uri.LocalPath);

        try { return Path.GetFileName(value.TrimEnd('\\', '/')); }
        catch { return string.Empty; }
    }

    private static string InferCategory(string dataSource, string operation)
    {
        var combined = $"{dataSource} {operation}";
        if (combined.Contains("Recycle", StringComparison.OrdinalIgnoreCase) || combined.Contains("Delete", StringComparison.OrdinalIgnoreCase)) return "Deletion";
        if (combined.Contains("Browser", StringComparison.OrdinalIgnoreCase) || combined.Contains("Web", StringComparison.OrdinalIgnoreCase)) return "Browser";
        if (combined.Contains("USB", StringComparison.OrdinalIgnoreCase) || combined.Contains("Drive", StringComparison.OrdinalIgnoreCase)) return "ExternalMedia";
        if (combined.Contains("Prefetch", StringComparison.OrdinalIgnoreCase) || combined.Contains("Executed", StringComparison.OrdinalIgnoreCase)) return "Execution";
        if (combined.Contains("LNK", StringComparison.OrdinalIgnoreCase) || combined.Contains("JumpList", StringComparison.OrdinalIgnoreCase) || combined.Contains("Shortcut", StringComparison.OrdinalIgnoreCase)) return "FileAccess";
        if (combined.Contains("Registry", StringComparison.OrdinalIgnoreCase) || combined.Contains("ShellBags", StringComparison.OrdinalIgnoreCase)) return "SystemArtifact";
        if (combined.Contains("EventLog", StringComparison.OrdinalIgnoreCase)) return "EventLog";
        return "Artifact";
    }

    private static void ArchiveWith7Zip(string sourceDir, string targetDir, Action<string> log)
    {
        var result = EvidenceArchiveManager.CreateWorkingEvidenceArchive(sourceDir, targetDir, log);
        if (!string.IsNullOrWhiteSpace(result.Status))
            log($"  - Static evidence archive status: {result.Status}. {result.Message}");
    }

    private static bool IsImageBackedEvidenceSet(IEnumerable<SourceFileRecord> sources)
    {
        foreach (var source in sources)
        {
            var sourceType = source.SourceType ?? string.Empty;
            if (sourceType.Contains("Headless Raw Image", StringComparison.OrdinalIgnoreCase) ||
                sourceType.Contains("Headless EWF/TSK", StringComparison.OrdinalIgnoreCase) ||
                sourceType.Contains("Raw Image", StringComparison.OrdinalIgnoreCase) ||
                sourceType.Contains("EWF/TSK", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void WriteImageBackedArchiveSkipManifest(string caseFolder, IEnumerable<SourceFileRecord> sources, Action<string> log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(caseFolder))
            {
                log("  - Image-backed source detected. Static WorkingEvidence 7-Zip archive skipped; case folder was unavailable for skip manifest.");
                return;
            }

            var archiveLogDir = Path.Combine(caseFolder, "ArchiveLogs");
            Directory.CreateDirectory(archiveLogDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(archiveLogDir, $"EvidenceArchiveSkipped_ImageBacked_{stamp}_manifest.txt");
            var sourceCount = sources.Count();
            var lines = new List<string>
            {
                "status=Skipped",
                "reason=Image-backed evidence source; original image/container should be preserved outside the staged WorkingEvidence folder.",
                $"created_utc={DateTime.UtcNow:O}",
                $"case_folder={caseFolder}",
                $"source_record_count={sourceCount}",
                "note=WorkingEvidence artifacts remain represented by source coverage, source manifest, hashes, and validation bundle exports."
            };
            File.WriteAllLines(path, lines);
            log($"  - Static WorkingEvidence 7-Zip archive skipped for image-backed evidence. Manifest: {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            log($"  ! Unable to write image-backed archive-skip manifest: {ex.Message}");
        }
    }
}
