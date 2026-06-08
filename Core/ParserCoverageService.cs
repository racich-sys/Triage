using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

internal static class ParserCoverageService
{
    public static DataTable BuildParserCoverageTable(string dbPath, IEnumerable<SourceFileRecord>? sources, string caseFolder = "")
    {
        var sourceList = (sources ?? Enumerable.Empty<SourceFileRecord>()).Where(s => s != null).ToList();
        var stats = LoadParserEventStats(dbPath);
        var table = NewTable(
            "Parser", "Registered", "Coverage_Status", "Candidate_Source_Files", "Ingested_Source_Files",
            "Events", "Behavioral_Events", "MetadataOnly_Events", "ParseError_Events", "Example_Source", "Notes");

        foreach (var parser in ParserRegistry.OrderedParsers)
        {
            var parserName = NormalizeParserName(SafeText(parser.ParserName, parser.GetType().Name));
            var candidates = sourceList.Where(s => SourceMatchesParser(s, parser, caseFolder)).ToList();
            var st = stats.TryGetValue(parserName, out var foundStats) && foundStats != null ? foundStats : new ParserEventStats();
            var ingested = candidates.Count(s => s.ImportedToDb || s.EventsImported > 0 || SafeText(s.ParserName).Contains(parserName, StringComparison.OrdinalIgnoreCase));
            var status = BuildCoverageStatus(candidates.Count, ingested, st.Events, st.ParseErrors, st.MetadataOnlyEvents, st.NotFullyParsedEvents);
            table.Rows.Add(parserName, "Yes", status, candidates.Count, ingested, st.Events, st.BehavioralEvents, st.MetadataOnlyEvents, st.ParseErrors, FirstExample(candidates), BuildCoverageNotes(candidates.Count, st));
        }

        AddOfficeOwnerFileCoverageRow(table, sourceList, stats, caseFolder);

        foreach (var kvp in stats.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ParserRegistry.OrderedParsers.Any(p => NormalizeParserName(SafeText(p.ParserName, p.GetType().Name)).Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)) ||
                kvp.Key.Equals("Office Owner File", StringComparison.OrdinalIgnoreCase))
                continue;
            var st = kvp.Value ?? new ParserEventStats();
            table.Rows.Add(SafeText(kvp.Key, "Unknown"), "No", st.ParseErrors > 0 ? "Parsed With Errors" : "Parsed/Legacy", 0, 0, st.Events, st.BehavioralEvents, st.MetadataOnlyEvents, st.ParseErrors, "", "Parser name was present in event_fields but is not registered in the current parser registry.");
        }

        if (sourceList.Count > 0)
        {
            var unmatched = sourceList.Where(s => !IsOfficeOwnerFileSource(s, ResolveLocalPath(s.LocalPath, caseFolder)) && !ParserRegistry.SelectParsersForEvidence(ResolveLocalPath(s.LocalPath, caseFolder), s.OriginalSourcePath).Any()).ToList();
            if (unmatched.Count > 0)
                table.Rows.Add("Unmatched / Metadata Fallback", "No", "Unsupported or metadata-only", unmatched.Count, unmatched.Count(s => s.ImportedToDb), CountMetadataEvents(dbPath), 0, CountMetadataEvents(dbPath), 0, FirstExample(unmatched), "Source files did not match a registered parser or were intentionally indexed as metadata-only.");
        }

        return table;
    }

    public static DataTable BuildSourceCoverageTable(IEnumerable<SourceFileRecord>? sources, string caseFolder = "")
    {
        var table = NewTable(
            "Source_File", "Original_Source_Path", "Normalized_Original_Source_Path", "Source_Path_Key", "Original_Created_Utc", "Original_Accessed_Utc", "Original_Modified_Utc", "Local_Path", "Normalized_Local_Path",
            "Source_Type", "Status", "Imported_To_DB", "Events_Imported", "Recorded_Parser", "Candidate_Parsers", "Is_Target_Artifact", "File_Size_Bytes", "Hash_SHA256");
        foreach (var source in (sources ?? Enumerable.Empty<SourceFileRecord>()).Where(s => s != null).OrderBy(s => SafeText(s.FileName), StringComparer.OrdinalIgnoreCase))
        {
            var local = ResolveLocalPath(source.LocalPath, caseFolder);
            var normalizedOriginal = NormalizePathForComparison(source.OriginalSourcePath);
            var normalizedLocal = NormalizePathForComparison(local);
            var pathKey = BuildPathKey(FirstNonBlank(source.OriginalSourcePath, local, source.FileName));
            var candidates = CandidateParserNames(source, local)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var targetArtifact = ParserRegistry.IsTargetArtifactPath(FirstNonBlank(source.OriginalSourcePath, local, source.FileName)) || candidates.Length > 0 || IsOfficeOwnerFileSource(source, local);
            table.Rows.Add(
                SafeText(source.FileName), SafeText(source.OriginalSourcePath), normalizedOriginal, pathKey, SafeText(source.OriginalCreatedUtc), SafeText(source.OriginalAccessedUtc), SafeText(source.OriginalModifiedUtc), SafeText(source.LocalPath), normalizedLocal,
                SafeText(source.SourceType), SafeText(source.Status), source.ImportedToDb ? "Yes" : "No", source.EventsImported, SafeText(source.ParserName), string.Join("; ", candidates), targetArtifact ? "Yes" : "No", source.FileSizeBytes, SafeText(source.HashValue));
        }
        return table;
    }

    public static DataTable BuildParserErrorTable(string dbPath)
    {
        var table = NewTable("Event_ID", "Parser", "Source", "Operation", "Source_File", "Error", "Target", "Time_Basis", "Timestamp_Warning");
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return table;

        DatabaseCore.InitializeDatabase(dbPath);
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT e.event_id,
                   COALESCE((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'ParserName' LIMIT 1), e.data_source) AS parser,
                   e.data_source,
                   e.operation,
                   e.source_file,
                   COALESCE((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'ParseError' LIMIT 1), '') AS error,
                   COALESCE((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('DisplayTarget','TargetPath','OriginalSourcePath') LIMIT 1), e.object_id) AS target,
                   e.event_time_basis,
                   e.timestamp_warning
            FROM events e
            WHERE e.result_status = 'ParseError'
               OR e.operation LIKE '%ParseError%'
               OR EXISTS (SELECT 1 FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name = 'ParseError')
            ORDER BY e.event_id DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            table.Rows.Add(Read(reader, 0), Read(reader, 1), Read(reader, 2), Read(reader, 3), Read(reader, 4), Read(reader, 5), Read(reader, 6), Read(reader, 7), Read(reader, 8));
        return table;
    }

    public static DataTable ValidateFixtureFolder(string folderPath, string tzName, Action<string>? log)
    {
        var table = NewTable("Fixture_File", "Relative_Path", "Parser", "Matched", "Events_Emitted", "Behavioral_Events", "Parse_Error_Events", "Result", "Example_Operations", "Fields_Observed", "Expected_Min_Events", "Required_Fields", "Validation_Status", "Error_Message");
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return table;

        var expectations = LoadFixtureExpectations(folderPath);

        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(folderPath, file);
            var parsers = ParserRegistry.SelectParsers(file).Where(p => p != null).ToList();
            if (parsers.Count == 0)
            {
                var expected = FindExpectation(expectations, relative, "(none)");
                table.Rows.Add(Path.GetFileName(file), relative, "(none)", "No", 0, 0, 0, "No parser matched", "", "", expected?.MinEventsText ?? "", expected?.RequiredFieldsText ?? "", EvaluateExpectation(expected, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "No parser matched"), "");
                continue;
            }

            foreach (var parser in parsers)
            {
                try
                {
                    var emitted = (parser.Parse(file, tzName, m => log?.Invoke($"[{SafeText(parser.ParserName, parser.GetType().Name)}] {m}")) ?? Enumerable.Empty<NormalizedEvent>()).Where(e => e != null).ToList();
                    var parseErrors = emitted.Count(e => (e.AdditionalFields?.ContainsKey("ParseError") ?? false) || SafeText(e.Operation).Contains("ParseError", StringComparison.OrdinalIgnoreCase));
                    var behavioral = emitted.Count(e => e.IsBehavioralTimestamp == true || (e.TimestampUtc != DateTime.MinValue && !ContainsMetadataOnly(e)));
                    var operations = string.Join("; ", emitted.Select(e => SafeText(e.Operation)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
                    var fieldSet = emitted.SelectMany(e => e.AdditionalFields?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var fields = string.Join("; ", fieldSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(30));
                    var result = parseErrors > 0 ? "Parsed with parser-error row" : emitted.Count > 0 ? "Parsed" : "Matched but emitted no events";
                    var parserName = SafeText(parser.ParserName, parser.GetType().Name);
                    var expected = FindExpectation(expectations, relative, parserName);
                    table.Rows.Add(Path.GetFileName(file), relative, parserName, "Yes", emitted.Count, behavioral, parseErrors, result, operations, fields, expected?.MinEventsText ?? "", expected?.RequiredFieldsText ?? "", EvaluateExpectation(expected, emitted.Count, fieldSet, result), "");
                }
                catch (Exception ex)
                {
                    var parserName = SafeText(parser.ParserName, parser.GetType().Name);
                    var expected = FindExpectation(expectations, relative, parserName);
                    table.Rows.Add(Path.GetFileName(file), relative, parserName, "Yes", 0, 0, 1, "Exception", "", "", expected?.MinEventsText ?? "", expected?.RequiredFieldsText ?? "", EvaluateExpectation(expected, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "Exception"), ex.Message);
                }
            }
        }

        return table;
    }

    private static List<FixtureExpectation> LoadFixtureExpectations(string folderPath)
    {
        var candidates = new[] { Path.Combine(folderPath, "parser_expected.csv"), Path.Combine(folderPath, "parser_expectations.csv") };
        var path = candidates.FirstOrDefault(File.Exists);
        var expectations = new List<FixtureExpectation>();
        if (string.IsNullOrWhiteSpace(path)) return expectations;

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return expectations;
        var header = SplitCsvLine(lines[0]).Select((name, index) => new { name = name.Trim(), index }).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        string Cell(string[] cells, string name) => header.TryGetValue(name, out var idx) && idx >= 0 && idx < cells.Length ? cells[idx].Trim() : string.Empty;

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitCsvLine(line);
            var exp = new FixtureExpectation
            {
                RelativePath = Cell(cells, "RelativePath"),
                Parser = FirstNonBlank(Cell(cells, "Parser"), Cell(cells, "ParserName")),
                RequiredFieldsText = Cell(cells, "RequiredFields"),
                ExpectedResult = Cell(cells, "ExpectedResult")
            };
            exp.MinEventsText = FirstNonBlank(Cell(cells, "MinEvents"), Cell(cells, "ExpectedMinEvents"));
            if (int.TryParse(exp.MinEventsText, out var min)) exp.MinEvents = min;
            exp.RequiredFields = exp.RequiredFieldsText.Split(new[] { ';', '|', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(exp.RelativePath)) expectations.Add(exp);
        }
        return expectations;
    }

    private static FixtureExpectation? FindExpectation(List<FixtureExpectation> expectations, string relativePath, string parserName)
    {
        return expectations.FirstOrDefault(e => MatchesExpectation(e, relativePath, parserName));
    }

    private static bool MatchesExpectation(FixtureExpectation expectation, string relativePath, string parserName)
    {
        var expectedPath = SafeText(expectation.RelativePath);
        var observedPath = SafeText(relativePath);
        var expectedFile = SafeText(Path.GetFileName(expectedPath));
        var observedFile = SafeText(Path.GetFileName(observedPath));
        var pathMatch = expectedPath.Equals(observedPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(expectedFile) && expectedFile.Equals(observedFile, StringComparison.OrdinalIgnoreCase)) ||
                        expectedPath == "*";
        var expectedParser = SafeText(expectation.Parser);
        var parserMatch = string.IsNullOrWhiteSpace(expectedParser) || expectedParser == "*" || expectedParser.Equals(SafeText(parserName), StringComparison.OrdinalIgnoreCase);
        return pathMatch && parserMatch;
    }

    private static string EvaluateExpectation(FixtureExpectation? expectation, int eventCount, HashSet<string> fields, string result)
    {
        if (expectation == null) return "No expectation manifest";
        var failures = new List<string>();
        if (expectation.MinEvents.HasValue && eventCount < expectation.MinEvents.Value)
            failures.Add($"events {eventCount} < min {expectation.MinEvents.Value}");
        foreach (var field in expectation.RequiredFields)
            if (!fields.Contains(field)) failures.Add($"missing field {field}");
        if (!string.IsNullOrWhiteSpace(expectation.ExpectedResult) && !result.Contains(expectation.ExpectedResult, StringComparison.OrdinalIgnoreCase))
            failures.Add($"result does not contain {expectation.ExpectedResult}");
        return failures.Count == 0 ? "Pass" : "Fail: " + string.Join("; ", failures);
    }

    private static string[] SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else current.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == ',') { values.Add(current.ToString()); current.Clear(); }
                else current.Append(ch);
            }
        }
        values.Add(current.ToString());
        return values.ToArray();
    }

    private static Dictionary<string, ParserEventStats> LoadParserEventStats(string dbPath)
    {
        var stats = new Dictionary<string, ParserEventStats>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return stats;

        DatabaseCore.InitializeDatabase(dbPath);
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'ParserName' LIMIT 1), e.data_source) AS parser,
                   COUNT(*) AS event_count,
                   SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=1 THEN 1 ELSE 0 END) AS behavioral_count,
                   SUM(CASE WHEN IFNULL(e.event_time_confidence,'')='MetadataOnly' OR IFNULL(e.result_status,'')='MetadataOnly' THEN 1 ELSE 0 END) AS metadata_count,
                   SUM(CASE WHEN IFNULL(e.result_status,'')='ParseError' OR e.operation LIKE '%ParseError%' OR EXISTS (SELECT 1 FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name='ParseError') THEN 1 ELSE 0 END) AS error_count,
                   SUM(CASE WHEN EXISTS (SELECT 1 FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.field_name IN ('CoverageStatus','ParserLimitation') AND ef.field_value LIKE '%not fully parsed%') THEN 1 ELSE 0 END) AS not_fully_parsed_count
            FROM events e
            GROUP BY parser;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = NormalizeParserName(Read(reader, 0));
            if (string.IsNullOrWhiteSpace(key)) key = "Unknown";
            var rowStats = new ParserEventStats
            {
                Events = ToInt(reader, 1),
                BehavioralEvents = ToInt(reader, 2),
                MetadataOnlyEvents = ToInt(reader, 3),
                ParseErrors = ToInt(reader, 4),
                NotFullyParsedEvents = ToInt(reader, 5)
            };

            if (!stats.TryGetValue(key, out var existing))
            {
                stats[key] = rowStats;
            }
            else
            {
                existing.Events += rowStats.Events;
                existing.BehavioralEvents += rowStats.BehavioralEvents;
                existing.MetadataOnlyEvents += rowStats.MetadataOnlyEvents;
                existing.ParseErrors += rowStats.ParseErrors;
                existing.NotFullyParsedEvents += rowStats.NotFullyParsedEvents;
            }
        }
        return stats;
    }

    private static int CountMetadataEvents(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return 0;
        try
        {
            using var conn = DatabaseCore.Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE data_source LIKE 'Metadata:%' OR result_status='MetadataOnly';";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { return 0; }
    }


    private static void AddOfficeOwnerFileCoverageRow(DataTable table, List<SourceFileRecord> sourceList, Dictionary<string, ParserEventStats> stats, string caseFolder)
    {
        const string parserName = "Office Owner File";
        var candidates = sourceList.Where(s => IsOfficeOwnerFileSource(s, ResolveLocalPath(s.LocalPath, caseFolder))).ToList();
        var st = stats.TryGetValue(parserName, out var foundStats) && foundStats != null ? foundStats : new ParserEventStats();
        var ingested = candidates.Count(s => s.ImportedToDb || s.EventsImported > 0 || SafeText(s.ParserName).Contains(parserName, StringComparison.OrdinalIgnoreCase));
        var status = BuildCoverageStatus(candidates.Count, ingested, st.Events, st.ParseErrors, st.MetadataOnlyEvents, st.NotFullyParsedEvents);
        var notes = candidates.Count == 0
            ? "No Office owner/lock (~$) file sources are currently listed. Triage discovers these from MFT filename records rather than recursively walking document trees."
            : "Office owner/lock files are document-open proximity indicators. Corroborate with LNK, Jump List, Office MRU, OneDrive, or filesystem activity.";
        table.Rows.Add(parserName, "Yes", status, candidates.Count, ingested, st.Events, st.BehavioralEvents, st.MetadataOnlyEvents, st.ParseErrors, FirstExample(candidates), notes);
    }

    private static IEnumerable<string> CandidateParserNames(SourceFileRecord source, string localPath)
    {
        if (IsOfficeOwnerFileSource(source, localPath))
            yield return "Office Owner File";

        foreach (var parser in ParserRegistry.SelectParsersForEvidence(localPath, source.OriginalSourcePath).Where(p => p != null))
        {
            var name = SafeText(parser.ParserName, parser.GetType().Name);
            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
        }
    }

    private static bool IsOfficeOwnerFileSource(SourceFileRecord source, string localPath)
    {
        var values = new[]
        {
            source.FileName,
            source.OriginalSourcePath,
            localPath,
            source.SourceType,
            source.ParserName
        };

        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var normalized = value.Replace('/', '\\');
            var fileName = Path.GetFileName(normalized) ?? string.Empty;
            if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.Contains("_~$", StringComparison.OrdinalIgnoreCase) || normalized.Contains("\\~$", StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.Contains("[DELETED MFT OWNER]", StringComparison.OrdinalIgnoreCase) || normalized.Contains("GhostOwner_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.Equals("Office Owner File", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool SourceMatchesParser(SourceFileRecord source, IArtifactParser parser, string caseFolder)
    {
        try
        {
            var local = ResolveLocalPath(source.LocalPath, caseFolder);
            return parser.CanParse(local) || (!string.IsNullOrWhiteSpace(source.OriginalSourcePath) && parser.CanParse(source.OriginalSourcePath));
        }
        catch { return false; }
    }

    private static string BuildCoverageStatus(int candidates, int ingested, int events, int parseErrors, int metadataOnly, int notFullyParsed)
    {
        if (events > 0 && parseErrors > 0) return "Parsed With Errors";
        if (events > 0 && events == notFullyParsed) return "Found But Not Fully Parsed";
        if (events > 0 && events == metadataOnly) return notFullyParsed > 0 ? "Metadata Only / Not Fully Parsed" : "Metadata Only";
        if (events > 0 && notFullyParsed > 0) return "Parsed With Partial Unsupported Artifacts";
        if (events > 0) return "Parsed";
        if (candidates > 0 && ingested > 0) return "Matched But No Events";
        if (candidates > 0) return "Candidate Pending";
        return "No Candidate Evidence";
    }

    private static string BuildCoverageNotes(int candidates, ParserEventStats stats)
    {
        if (stats.ParseErrors > 0) return "Review Parser Errors tab.";
        if (stats.Events > 0 && stats.Events == stats.NotFullyParsedEvents) return "Artifact(s) were found, but this parser currently records them as not fully parsed.";
        if (stats.NotFullyParsedEvents > 0) return "Some artifact(s) were found but are not fully parsed by the current parser implementation.";
        if (stats.Events > 0 && stats.Events == stats.MetadataOnlyEvents) return "Only metadata/fallback rows are present.";
        if (candidates == 0) return "No matching source files currently listed in the case source inventory.";
        if (stats.Events == 0) return "Parser matched source file(s), but no normalized events are present yet.";
        return "Parser has normalized event output.";
    }

    private static bool ContainsMetadataOnly(NormalizedEvent ev)
    {
        return SafeText(ev.EventTimeConfidence).Equals("MetadataOnly", StringComparison.OrdinalIgnoreCase) ||
               (ev.AdditionalFields?.TryGetValue("EventTimeConfidence", out var c) == true && SafeText(c).Equals("MetadataOnly", StringComparison.OrdinalIgnoreCase));
    }

    private static DataTable NewTable(params string[] columns)
    {
        var table = new DataTable();
        foreach (var c in columns) table.Columns.Add(c);
        return table;
    }

    private static string Read(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index)?.ToString() ?? string.Empty;
    private static int ToInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : Convert.ToInt32(reader.GetValue(index));
    private static string FirstExample(IEnumerable<SourceFileRecord> sources) => sources.Where(s => s != null).Select(s => FirstNonBlank(s.OriginalSourcePath, s.FileName)).FirstOrDefault() ?? string.Empty;
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    private static string NormalizePathForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().Trim('"').Replace('/', '\\');
        while (text.Contains("\\\\", StringComparison.Ordinal)) text = text.Replace("\\\\", "\\", StringComparison.Ordinal);
        return text.Trim();
    }

    private static string BuildPathKey(string? value)
    {
        var path = NormalizePathForComparison(value).ToLowerInvariant();
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') path = path[2..];
        return path.TrimStart('\\');
    }


    private static string SafeText(string? value, string fallback = "") => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string SafePath(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty : path;

    private static string ResolveLocalPath(string path, string caseFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (Path.IsPathRooted(path)) return path;
        return string.IsNullOrWhiteSpace(caseFolder) ? path : Path.Combine(caseFolder, path);
    }

    private sealed class FixtureExpectation
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Parser { get; set; } = string.Empty;
        public int? MinEvents { get; set; }
        public string MinEventsText { get; set; } = string.Empty;
        public string RequiredFieldsText { get; set; } = string.Empty;
        public HashSet<string> RequiredFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ExpectedResult { get; set; } = string.Empty;
    }

    private static string NormalizeParserName(string? parserName)
    {
        var value = SafeText(parserName);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        if (value.Equals("O365_UAL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("O365 UAL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Unified Audit Log", StringComparison.OrdinalIgnoreCase))
            return "O365 Unified Audit Log";

        if (value.StartsWith("Metadata: Parser matched but emitted no events: ", StringComparison.OrdinalIgnoreCase))
            return value.Substring("Metadata: Parser matched but emitted no events: ".Length).Trim();

        if (value.Equals("Metadata: Registry Hive", StringComparison.OrdinalIgnoreCase))
            return "Windows Registry Parser";

        return value;
    }

    private sealed class ParserEventStats
    {
        public int Events { get; set; }
        public int BehavioralEvents { get; set; }
        public int MetadataOnlyEvents { get; set; }
        public int ParseErrors { get; set; }
        public int NotFullyParsedEvents { get; set; }
    }
}
