using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

public sealed class GoogleDriveParser : IArtifactParser
{
    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UrlRegex = new("https?://[^\\s\\\"'<>|{}]{4,1000}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string ParserName => "Google Drive Artifact Parser";

    public bool CanParse(string filePath)
    {
        var name = (Path.GetFileName(filePath) ?? string.Empty).ToLowerInvariant();
        if (name.EndsWith(".pf", StringComparison.Ordinal) || name.EndsWith(".exe", StringComparison.Ordinal))
            return false;

        var supportedName = name is "metadata_sqlite_db" or "mirror_sqlite.db" or "snapshot.db" or "sync_config.db" ||
                            name.EndsWith(".db", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".log", StringComparison.Ordinal);
        if (!supportedName)
            return false;

        return ParserSupport.HasPathSequence(filePath, "Google", "DriveFS") || ParserSupport.HasPathSegment(filePath, "Google Drive") || name.Contains("google_drive", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            if (IsSqlite(filePath)) events.AddRange(ParseSqlite(filePath));
            else if (Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase)) events.AddRange(ParseJson(filePath));
            else events.AddRange(ParseTextIndicators(filePath));
        }
        catch (Exception ex)
        {
            log($"Google Drive parser failed for {Path.GetFileName(filePath)}: {ex.Message}");
            events.Add(MakeError(filePath, ex.Message));
        }
        return events;
    }

    private IEnumerable<NormalizedEvent> ParseSqlite(string filePath)
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"VestigantGDrive_{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(filePath, tempDb, true);
            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly;");
            conn.Open();
            foreach (var table in LoadTables(conn))
            {
                var columns = LoadColumns(conn, table);
                if (!columns.Any(IsInterestingColumn)) continue;
                foreach (var ev in ParseInterestingRows(conn, filePath, table, columns))
                    yield return ev;
            }
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    private IEnumerable<NormalizedEvent> ParseInterestingRows(SqliteConnection conn, string filePath, string table, List<string> columns)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Quote(table)} LIMIT 8000;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                values[reader.GetName(i)] = reader.IsDBNull(i) ? string.Empty : ParserSupport.Clean(reader.GetValue(i)?.ToString());
            var combined = string.Join(" ", values.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
            var target = FirstNonBlank(FirstPath(combined), FirstUrl(combined), FirstValue(values, "local", "path", "filename", "name", "cloud", "doc_id", "id"));
            var email = FirstNonBlank(ExtractEmail(combined), ExtractEmailFromDriveFsPath(filePath));
            if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(email)) continue;

            var ev = MakeBase("GoogleDrive_Metadata_Record", filePath, GuessTimestamp(values, DateTime.MinValue));
            ev.ObjectPath = FirstNonBlank(target, email, table);
            ev.AdditionalFields["EventCategory"] = "CloudSync";
            ev.AdditionalFields["ArtifactType"] = "GoogleDriveSQLite";
            ev.AdditionalFields["CloudProvider"] = "GoogleDrive";
            ev.AdditionalFields["CloudAccount"] = email;
            ev.AdditionalFields["GoogleDriveTable"] = table;
            ev.AdditionalFields["GoogleDriveAccountFromPath"] = ExtractEmailFromDriveFsPath(filePath);
            foreach (var kv in values.Take(100))
                if (!string.IsNullOrWhiteSpace(kv.Value)) ev.AdditionalFields[$"GoogleDrive_{table}_{kv.Key}"] = kv.Value.Length > 1000 ? kv.Value[..1000] : kv.Value;
            if (ForensicText.IsLikelyPath(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ParserSupport.AddTargetFields(ev, target, "GoogleDriveSqliteTarget");
            ParserSupport.AddParseQuality(ev, ParserName, "Medium", "Google Drive/DriveFS SQLite row with path, URL, file ID, or account indicator extracted; table-specific semantics may require examiner review.");
            yield return ev;
        }
    }

    private IEnumerable<NormalizedEvent> ParseJson(string filePath)
    {
        var text = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(text);
        var flattened = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FlattenJson(doc.RootElement, string.Empty, flattened, 300);
        var combined = string.Join(" ", flattened.Values);
        var target = FirstNonBlank(FirstPath(combined), FirstUrl(combined), FirstValue(flattened, "path", "url", "file", "name"));
        var email = FirstNonBlank(ExtractEmail(combined), ExtractEmailFromDriveFsPath(filePath));
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(email)) yield break;

        var ev = MakeBase("GoogleDrive_Config_Record", filePath, DateTime.MinValue);
        ev.ObjectPath = FirstNonBlank(target, email, Path.GetFileName(filePath));
        ev.AdditionalFields["EventCategory"] = "CloudSync";
        ev.AdditionalFields["ArtifactType"] = "GoogleDriveJson";
        ev.AdditionalFields["CloudProvider"] = "GoogleDrive";
        ev.AdditionalFields["CloudAccount"] = email;
        foreach (var kv in flattened.Take(100))
            if (!string.IsNullOrWhiteSpace(kv.Value)) ev.AdditionalFields[$"GoogleDriveJson_{kv.Key}"] = kv.Value.Length > 1000 ? kv.Value[..1000] : kv.Value;
        if (ForensicText.IsLikelyPath(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ParserSupport.AddTargetFields(ev, target, "GoogleDriveJsonTarget");
        ParserSupport.AddParseQuality(ev, ParserName, "Medium", "Google Drive JSON/config file parsed for path, URL, and account indicators.");
        yield return ev;
    }

    private IEnumerable<NormalizedEvent> ParseTextIndicators(string filePath)
    {
        var ts = DateTime.MinValue;
        var count = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            var clean = ParserSupport.Clean(line);
            if (!clean.Contains("drive.google", StringComparison.OrdinalIgnoreCase) && !clean.Contains("DriveFS", StringComparison.OrdinalIgnoreCase) && !EmailRegex.IsMatch(clean) && !ForensicText.ExtractPathCandidates(clean).Any()) continue;
            var target = FirstNonBlank(FirstPath(clean), FirstUrl(clean), ExtractEmail(clean), clean.Length > 400 ? clean[..400] : clean);
            var ev = MakeBase("GoogleDrive_Log_Indicator", filePath, ts);
            ev.ObjectPath = target;
            ev.AdditionalFields["EventCategory"] = "CloudSync";
            ev.AdditionalFields["ArtifactType"] = "GoogleDriveTextOrLog";
            ev.AdditionalFields["CloudProvider"] = "GoogleDrive";
            ev.AdditionalFields["CloudAccount"] = FirstNonBlank(ExtractEmail(clean), ExtractEmailFromDriveFsPath(filePath));
            ev.AdditionalFields["SourceLine"] = clean.Length > 4000 ? clean[..4000] : clean;
            if (ForensicText.IsLikelyPath(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ParserSupport.AddTargetFields(ev, target, "GoogleDriveTextTarget");
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Google Drive text/log line contained path, URL, or account indicator.");
            yield return ev;
            count++;
            if (count >= 2000) yield break;
        }
    }

    private NormalizedEvent MakeBase(string operation, string filePath, DateTime timestamp)
    {
        return new NormalizedEvent
        {
            DataSource = "GoogleDrive",
            Operation = operation,
            UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
            TimestampUtc = timestamp,
            ObjectPath = filePath,
            AdditionalFields = { ["SourceFile"] = Path.GetFileName(filePath) ?? string.Empty, ["SourcePath"] = filePath }
        };
    }

    private static NormalizedEvent MakeError(string filePath, string error)
    {
        var ev = new NormalizedEvent { DataSource = "GoogleDrive", Operation = "GoogleDrive_ParseError", ObjectPath = filePath, UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"), TimestampUtc = DateTime.MinValue };
        ev.AdditionalFields["EventCategory"] = "ParserError";
        ev.AdditionalFields["ParseError"] = error;
        ParserSupport.AddParseQuality(ev, "Google Drive Artifact Parser", "Low", "Exception during Google Drive artifact parsing.");
        return ev;
    }

    private static bool IsSqlite(string filePath)
    {
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length < 100) return false;
            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.Read(header) == 16 && System.Text.Encoding.ASCII.GetString(header).StartsWith("SQLite format 3", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static List<string> LoadTables(SqliteConnection conn)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private static List<string> LoadColumns(SqliteConnection conn, string table)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(1));
        return list;
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> output, int limit)
    {
        if (output.Count >= limit) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
                FlattenJson(prop.Value, string.IsNullOrWhiteSpace(prefix) ? prop.Name : $"{prefix}.{prop.Name}", output, limit);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                FlattenJson(item, $"{prefix}[{i}]", output, limit);
                i++;
                if (i >= 50) break;
            }
        }
        else
        {
            output[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static bool IsInterestingColumn(string c) => c.Contains("path", StringComparison.OrdinalIgnoreCase) || c.Contains("file", StringComparison.OrdinalIgnoreCase) || c.Contains("url", StringComparison.OrdinalIgnoreCase) || c.Contains("email", StringComparison.OrdinalIgnoreCase) || c.Contains("account", StringComparison.OrdinalIgnoreCase) || c.Contains("doc", StringComparison.OrdinalIgnoreCase) || c.Contains("drive", StringComparison.OrdinalIgnoreCase) || c.Contains("sync", StringComparison.OrdinalIgnoreCase) || c.Contains("modified", StringComparison.OrdinalIgnoreCase) || c.Contains("time", StringComparison.OrdinalIgnoreCase) || c.Contains("name", StringComparison.OrdinalIgnoreCase);
    private static string ExtractEmail(string value) { var m = EmailRegex.Match(value ?? string.Empty); return m.Success ? m.Value : string.Empty; }
    private static string FirstUrl(string value) { var m = UrlRegex.Match(value ?? string.Empty); return m.Success ? m.Value.TrimEnd(';', ',', ')', ']') : string.Empty; }
    private static string FirstPath(string value) { foreach (var p in ForensicText.ExtractPathCandidates(value ?? string.Empty)) return p; return string.Empty; }
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    private static string FirstValue(Dictionary<string, string> values, params string[] keys) => values.FirstOrDefault(kv => keys.Any(k => kv.Key.Contains(k, StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(kv.Value)).Value ?? string.Empty;

    private static string ExtractEmailFromDriveFsPath(string filePath)
    {
        var m = Regex.Match(filePath, @"DriveFS[\\/]+([^\\/]+)", RegexOptions.IgnoreCase);
        return m.Success ? ExtractEmail(m.Groups[1].Value) : string.Empty;
    }

    private static DateTime GuessTimestamp(Dictionary<string, string> values, DateTime fallback)
    {
        foreach (var kv in values)
        {
            if (!kv.Key.Contains("time", StringComparison.OrdinalIgnoreCase) && !kv.Key.Contains("date", StringComparison.OrdinalIgnoreCase) && !kv.Key.Contains("modified", StringComparison.OrdinalIgnoreCase) && !kv.Key.Contains("viewed", StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTimeOffset.TryParse(kv.Value, out var dto)) return dto.UtcDateTime;
            if (long.TryParse(kv.Value, out var n))
            {
                try
                {
                    if (n > 116444736000000000 && n < 265046774400000000) return DateTime.FromFileTimeUtc(n);
                    if (n > 1000000000000000) return DateTimeOffset.FromUnixTimeMilliseconds(n / 1000).UtcDateTime;
                    if (n > 1000000000000) return DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime;
                    if (n > 1000000000) return DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
                }
                catch { }
            }
        }
        return fallback;
    }
}
