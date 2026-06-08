using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

public class BrowserHistoryParser : IArtifactParser
{
    public string ParserName => "Browser History and Downloads";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        if (IsBrowserCompanionOrUnsupportedArtifactName(name))
            return true;

        if (!(name.Equals("History", StringComparison.OrdinalIgnoreCase) ||
              name.EndsWith("_History", StringComparison.OrdinalIgnoreCase) ||
              name.Equals("places.sqlite", StringComparison.OrdinalIgnoreCase) ||
              name.EndsWith("_places.sqlite", StringComparison.OrdinalIgnoreCase)))
            return false;

        return IsSqlite(filePath);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        var artifactName = Path.GetFileName(filePath) ?? string.Empty;
        if (IsBrowserCompanionOrUnsupportedArtifactName(artifactName))
        {
            return new[] { CreateFoundButNotFullyParsedEvent(filePath, artifactName) };
        }

        var tempDb = Path.Combine(Path.GetTempPath(), $"VestigantBrowser_{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(filePath, tempDb, true);
            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly;");
            conn.Open();
            var tables = LoadTableNames(conn);

            if (tables.Contains("urls") && tables.Contains("visits"))
            {
                events.AddRange(ParseChromiumVisits(conn, filePath));
                if (tables.Contains("downloads"))
                    events.AddRange(ParseChromiumDownloads(conn, filePath, tables.Contains("downloads_url_chains")));
            }

            if (tables.Contains("moz_places") && tables.Contains("moz_historyvisits"))
            {
                events.AddRange(ParseFirefoxVisits(conn, filePath));
                if (tables.Contains("moz_annos") && tables.Contains("moz_anno_attributes"))
                    events.AddRange(ParseFirefoxDownloads(conn, filePath));
            }

            if (events.Count == 0)
                log($"Browser database parsed but no visits/downloads emitted: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            log($"Failed to parse browser database {Path.GetFileName(filePath)}: {ex.Message}");
            var ev = new NormalizedEvent
            {
                DataSource = "Browser",
                Operation = "Browser_ParseError",
                ObjectPath = filePath,
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = DateTime.MinValue
            };
            ev.AdditionalFields["ParseError"] = ex.Message;
            ev.AdditionalFields["EventCategory"] = "ParserError";
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "SQLite/open/query exception.");
            events.Add(ev);
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }

        return events;
    }

    private static bool IsBrowserCompanionOrUnsupportedArtifactName(string name)
    {
        return name.Equals("WebCacheV01.dat", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_WebCacheV01.dat", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("History-journal", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_History-journal", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("places.sqlite-wal", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("places.sqlite-shm", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_places.sqlite-wal", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_places.sqlite-shm", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("downloads.sqlite", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_downloads.sqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static NormalizedEvent CreateFoundButNotFullyParsedEvent(string filePath, string artifactName)
    {
        var artifactType = artifactName;
        var limitation = artifactName.Contains("WebCache", StringComparison.OrdinalIgnoreCase)
            ? "WebCacheV01.dat is an ESE database. Full WebCache table parsing is not yet implemented in this parser."
            : "This browser companion/legacy artifact was found and preserved, but it is not independently parsed as a primary history/download database.";

        var ev = new NormalizedEvent
        {
            DataSource = "Browser_Artifact",
            Operation = "Browser_Artifact_Found_NotFullyParsed",
            ObjectPath = filePath,
            UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
            TimestampUtc = DateTime.MinValue
        };
        ev.AdditionalFields["EventCategory"] = "Browser";
        ev.AdditionalFields["ArtifactType"] = artifactType;
        ev.AdditionalFields["CoverageStatus"] = "Found but not fully parsed";
        ev.AdditionalFields["ParserLimitation"] = limitation;
        ParserSupport.AddTargetFields(ev, filePath, "BrowserCompanionArtifact");
        ParserSupport.SetEventTime(ev, null, "BrowserArtifactFound", "MetadataOnly", false, "Browser companion or unsupported artifact was found, but no browser-history/download event timestamp was decoded.");
        ParserSupport.AddParseQuality(ev, "Browser History and Downloads", "Low", limitation);
        return ev;
    }

    private static IEnumerable<NormalizedEvent> ParseChromiumVisits(SqliteConnection conn, string filePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT urls.url, urls.title, urls.visit_count, urls.typed_count, urls.last_visit_time,
       visits.visit_time, visits.visit_duration, visits.transition, visits.from_visit
FROM visits LEFT JOIN urls ON visits.url = urls.id
ORDER BY visits.visit_time ASC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = GetString(reader, 0);
            var ev = new NormalizedEvent
            {
                DataSource = "Browser_History",
                Operation = "Web_Visit",
                ObjectPath = url,
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = ChromiumTimeToUtc(GetInt64(reader, 5)) ?? DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "Browser";
            ev.AdditionalFields["ArtifactType"] = "ChromiumVisit";
            ev.AdditionalFields["PageTitle"] = GetString(reader, 1);
            ev.AdditionalFields["VisitCount"] = GetInt64(reader, 2).ToString();
            ev.AdditionalFields["TypedCount"] = GetInt64(reader, 3).ToString();
            ev.AdditionalFields["LastVisitUtc"] = ChromiumTimeToUtc(GetInt64(reader, 4))?.ToString("O") ?? string.Empty;
            ev.AdditionalFields["VisitTimeUtc"] = ev.TimestampUtc == DateTime.MinValue ? string.Empty : ev.TimestampUtc.ToString("O");
            ev.AdditionalFields["DurationSeconds"] = (GetInt64(reader, 6) / 1000000.0).ToString("0.00");
            ev.AdditionalFields["TransitionRaw"] = GetInt64(reader, 7).ToString();
            ev.AdditionalFields["FromVisitId"] = GetInt64(reader, 8).ToString();
            AddBrowserIdentity(ev, filePath, "Chromium");
            var displayTarget = ApplyBrowserUrlClassification(ev, url, "Url");
            ParserSupport.AddTargetFields(ev, displayTarget, "BrowserVisitUrl");
            ParserSupport.AddParseQuality(ev, "Browser History and Downloads", "High", "Chromium urls/visits tables queried successfully.");
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseChromiumDownloads(SqliteConnection conn, string filePath, bool hasChains)
    {
        var dCols = LoadColumnNames(conn, "downloads");
        string Col(string column, string alias) => dCols.Contains(column) ? $"d.{column} AS {alias}" : $"'' AS {alias}";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT d.id AS id,
       {Col("current_path", "current_path")},
       {Col("target_path", "target_path")},
       {Col("start_time", "start_time")},
       {Col("end_time", "end_time")},
       {Col("received_bytes", "received_bytes")},
       {Col("total_bytes", "total_bytes")},
       {Col("state", "state")},
       {Col("danger_type", "danger_type")},
       {Col("interrupt_reason", "interrupt_reason")},
       {Col("opened", "opened")},
       {Col("tab_url", "tab_url")},
       {Col("site_url", "site_url")},
       {Col("referrer", "referrer")},
       {Col("mime_type", "mime_type")},
       {(hasChains ? "COALESCE((SELECT group_concat(url, '; ') FROM downloads_url_chains c WHERE c.id = d.id), '')" : "''")} AS chain_urls
FROM downloads d
ORDER BY d.start_time ASC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var target = FirstNonBlank(GetString(reader, 2), GetString(reader, 1));
            var sourceUrls = FirstNonBlank(GetString(reader, 15), GetString(reader, 11), GetString(reader, 12), GetString(reader, 13));
            var firstUrl = FirstUrl(sourceUrls);
            var ev = new NormalizedEvent
            {
                DataSource = "Browser_Downloads",
                Operation = "File_Downloaded",
                ObjectPath = FirstNonBlank(target, firstUrl),
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = ChromiumTimeToUtc(GetInt64(reader, 3)) ?? DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "Download";
            ev.AdditionalFields["ArtifactType"] = "ChromiumDownload";
            ev.AdditionalFields["DownloadId"] = GetString(reader, 0);
            ev.AdditionalFields["CurrentPath"] = GetString(reader, 1);
            ev.AdditionalFields["TargetPath"] = target;
            ev.AdditionalFields["SourceUrl"] = firstUrl;
            ev.AdditionalFields["SourceUrlChain"] = sourceUrls;
            ev.AdditionalFields["TabUrl"] = GetString(reader, 11);
            ev.AdditionalFields["SiteUrl"] = GetString(reader, 12);
            ev.AdditionalFields["Referrer"] = GetString(reader, 13);
            ev.AdditionalFields["MimeType"] = GetString(reader, 14);
            ev.AdditionalFields["StartTimeUtc"] = ChromiumTimeToUtc(GetInt64(reader, 3))?.ToString("O") ?? string.Empty;
            ev.AdditionalFields["EndTimeUtc"] = ChromiumTimeToUtc(GetInt64(reader, 4))?.ToString("O") ?? string.Empty;
            ev.AdditionalFields["ReceivedBytes"] = GetInt64(reader, 5).ToString();
            ev.AdditionalFields["TotalBytes"] = GetInt64(reader, 6).ToString();
            ev.AdditionalFields["State"] = GetString(reader, 7);
            ev.AdditionalFields["DangerType"] = GetString(reader, 8);
            ev.AdditionalFields["InterruptReason"] = GetString(reader, 9);
            ev.AdditionalFields["Opened"] = GetString(reader, 10);
            AddBrowserIdentity(ev, filePath, "Chromium");
            ApplyBrowserUrlClassification(ev, firstUrl, "SourceUrl");
            ev.AdditionalFields["DownloadTargetPathType"] = ParserSupport.ClassifyPathOrUrl(target);
            ev.AdditionalFields["FileName"] = ParserSupport.SafeFileName(target);
            ev.AdditionalFields["FileExtension"] = ParserSupport.SafeExtension(target);
            ParserSupport.AddTargetFields(ev, FirstNonBlank(target, firstUrl), "BrowserDownload");
            ParserSupport.AddParseQuality(ev, "Browser History and Downloads", "High", "Chromium downloads table queried successfully.");
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseFirefoxVisits(SqliteConnection conn, string filePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT p.url, p.title, p.visit_count, p.typed, h.visit_date, h.visit_type, h.from_visit, h.id
FROM moz_historyvisits h LEFT JOIN moz_places p ON h.place_id = p.id
ORDER BY h.visit_date ASC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = GetString(reader, 0);
            var ev = new NormalizedEvent
            {
                DataSource = "Browser_History",
                Operation = "Web_Visit",
                ObjectPath = url,
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = UnixMicrosecondsToUtc(GetInt64(reader, 4)) ?? DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "Browser";
            ev.AdditionalFields["ArtifactType"] = "FirefoxVisit";
            ev.AdditionalFields["PageTitle"] = GetString(reader, 1);
            ev.AdditionalFields["VisitCount"] = GetString(reader, 2);
            ev.AdditionalFields["TypedCount"] = GetString(reader, 3);
            ev.AdditionalFields["VisitType"] = GetString(reader, 5);
            ev.AdditionalFields["FromVisitId"] = GetString(reader, 6);
            ev.AdditionalFields["VisitId"] = GetString(reader, 7);
            AddBrowserIdentity(ev, filePath, "Firefox");
            var displayTarget = ApplyBrowserUrlClassification(ev, url, "Url");
            ParserSupport.AddTargetFields(ev, displayTarget, "FirefoxVisitUrl");
            ParserSupport.AddParseQuality(ev, "Browser History and Downloads", "High", "Firefox moz_places/moz_historyvisits tables queried successfully.");
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseFirefoxDownloads(SqliteConnection conn, string filePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT p.id, p.url, p.title,
       MAX(CASE WHEN aa.name = 'downloads/destinationFileURI' THEN a.content ELSE '' END) AS dest,
       MAX(CASE WHEN aa.name = 'downloads/metaData' THEN a.content ELSE '' END) AS metadata,
       MAX(CASE WHEN aa.name = 'downloads/destinationFileName' THEN a.content ELSE '' END) AS destname,
       MAX(CASE WHEN aa.name LIKE 'downloads/%' THEN a.dateAdded ELSE 0 END) AS date_added,
       MAX(CASE WHEN aa.name LIKE 'downloads/%' THEN a.lastModified ELSE 0 END) AS last_modified
FROM moz_annos a
JOIN moz_anno_attributes aa ON a.anno_attribute_id = aa.id
JOIN moz_places p ON a.place_id = p.id
WHERE aa.name LIKE 'downloads/%'
GROUP BY p.id, p.url, p.title
ORDER BY date_added ASC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sourceUrl = GetString(reader, 1);
            var destUri = GetString(reader, 3);
            var metadata = GetString(reader, 4);
            var target = FirstNonBlank(FileUriToPath(destUri), GetString(reader, 5), destUri);
            var start = FirefoxDownloadTime(metadata, "startTime") ?? UnixMicrosecondsToUtc(GetInt64(reader, 6)) ?? UnixMicrosecondsToUtc(GetInt64(reader, 7)) ?? DateTime.MinValue;
            var end = FirefoxDownloadTime(metadata, "endTime") ?? UnixMicrosecondsToUtc(GetInt64(reader, 7)) ?? DateTime.MinValue;
            var ev = new NormalizedEvent
            {
                DataSource = "Browser_Downloads",
                Operation = "File_Downloaded",
                ObjectPath = FirstNonBlank(target, sourceUrl),
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = start
            };
            ev.AdditionalFields["EventCategory"] = "Download";
            ev.AdditionalFields["ArtifactType"] = "FirefoxDownload";
            ev.AdditionalFields["PlaceId"] = GetString(reader, 0);
            ev.AdditionalFields["SourceUrl"] = sourceUrl;
            ev.AdditionalFields["TargetPath"] = target;
            ev.AdditionalFields["DestinationFileUri"] = destUri;
            ev.AdditionalFields["DownloadMetadata"] = metadata;
            ev.AdditionalFields["StartTimeUtc"] = start == DateTime.MinValue ? string.Empty : start.ToString("O");
            ev.AdditionalFields["EndTimeUtc"] = end == DateTime.MinValue ? string.Empty : end.ToString("O");
            var fileSize = FirefoxDownloadMetadata(metadata, "fileSize");
            if (!string.IsNullOrWhiteSpace(fileSize)) ev.AdditionalFields["TotalBytes"] = fileSize;
            AddBrowserIdentity(ev, filePath, "Firefox");
            ApplyBrowserUrlClassification(ev, sourceUrl, "SourceUrl");
            ev.AdditionalFields["DownloadTargetPathType"] = ParserSupport.ClassifyPathOrUrl(target);
            ev.AdditionalFields["FileName"] = ParserSupport.SafeFileName(target);
            ev.AdditionalFields["FileExtension"] = ParserSupport.SafeExtension(target);
            ParserSupport.AddTargetFields(ev, FirstNonBlank(target, sourceUrl), "FirefoxDownload");
            ParserSupport.AddParseQuality(ev, "Browser History and Downloads", "High", "Firefox downloads annotations queried successfully.");
            yield return ev;
        }
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

    private static HashSet<string> LoadTableNames(SqliteConnection conn)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(GetString(reader, 0));
        return set;
    }

    private static HashSet<string> LoadColumnNames(SqliteConnection conn, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(GetString(reader, 1));
        return set;
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static DateTime? ChromiumTimeToUtc(long microseconds)
    {
        if (microseconds <= 0) return null;
        try { return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(microseconds * 10); }
        catch { return null; }
    }

    private static DateTime? UnixMicrosecondsToUtc(long microseconds)
    {
        if (microseconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).UtcDateTime; }
        catch { return null; }
    }

    private static DateTime? FirefoxDownloadTime(string metadata, string propertyName)
    {
        var value = FirefoxDownloadMetadata(metadata, propertyName);
        if (!long.TryParse(value, out var parsed)) return null;
        // Firefox download metadata stores milliseconds since Unix epoch in common versions.
        try { return DateTimeOffset.FromUnixTimeMilliseconds(parsed).UtcDateTime; }
        catch { return null; }
    }

    private static string FirefoxDownloadMetadata(string metadata, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.TryGetProperty(propertyName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? string.Empty : prop.ToString();
        }
        catch { }
        var m = Regex.Match(metadata, $"\\\"{Regex.Escape(propertyName)}\\\"\\s*:\\s*\\\"?([^,}}\\\"]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static string FileUriToPath(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
                return Uri.UnescapeDataString(parsed.LocalPath);
        }
        catch { }
        return string.Empty;
    }

    private static string GetString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString() ?? string.Empty;
    private static long GetInt64(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return 0;
        try { return Convert.ToInt64(reader.GetValue(index)); } catch { return 0; }
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string FirstUrl(string urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return string.Empty;
        foreach (var part in urls.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return p;
        }
        return urls.Trim();
    }

    private static string ApplyBrowserUrlClassification(NormalizedEvent ev, string urlOrPath, string sourceFieldName)
    {
        var classification = InternetArtifactClassifier.Classify(urlOrPath);
        InternetArtifactClassifier.AddFields(ev, urlOrPath, sourceFieldName);
        ev.AdditionalFields["Domain"] = classification.Host;

        if (classification.IsCloudStorageUrl)
        {
            ev.AdditionalFields["EventCategory"] = "CloudStorage";
            if (ev.DataSource.Equals("Browser_History", StringComparison.OrdinalIgnoreCase))
                ev.Operation = "Cloud_Storage_Web_Visit";
            else if (ev.DataSource.Equals("Browser_Downloads", StringComparison.OrdinalIgnoreCase))
                ev.Operation = "Cloud_Storage_File_Downloaded";
        }
        else if (classification.IsPersonalEmailUrl)
        {
            ev.AdditionalFields["EventCategory"] = "PersonalEmailWebmail";
            if (ev.DataSource.Equals("Browser_History", StringComparison.OrdinalIgnoreCase))
                ev.Operation = "Personal_Email_Webmail_Visit";
        }
        else if (classification.IsLocalHostUrl || classification.IsLocalFileUrl || classification.IsFileExplorerLocalAccess)
        {
            ev.AdditionalFields["EventCategory"] = "LocalFileOrLocalhostAccess";
            if (ev.DataSource.Equals("Browser_History", StringComparison.OrdinalIgnoreCase))
                ev.Operation = classification.IsLocalFileUrl ? "Local_File_Opened_From_Browser_History" : "Localhost_Web_Visit";
        }

        return FirstNonBlank(classification.LocalFilePath, urlOrPath);
    }

    private static void AddBrowserIdentity(NormalizedEvent ev, string path, string family)
    {
        ev.AdditionalFields["BrowserProfilePath"] = path;
        ev.AdditionalFields["BrowserFamily"] = family;
        ev.AdditionalFields["BrowserName"] = InferBrowserName(path, family);
        ev.AdditionalFields["BrowserProfile"] = InferBrowserProfile(path, family);
    }

    private static string InferBrowserName(string path, string family)
    {
        if (path.Contains("Microsoft\\Edge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (path.Contains("Google\\Chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (path.Contains("BraveSoftware", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (path.Contains("Mozilla\\Firefox", StringComparison.OrdinalIgnoreCase) || family.Equals("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        return family;
    }

    private static string InferBrowserProfile(string path, string family)
    {
        var m = Regex.Match(path ?? string.Empty, @"\\User Data\\([^\\]+)\\History", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(path ?? string.Empty, @"\\Profiles\\([^\\]+)\\places\.sqlite", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }
}
