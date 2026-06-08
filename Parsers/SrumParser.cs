using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VestigantTriage;

public sealed class SrumParser : IArtifactParser
{
    private static readonly Regex SidRegex = new(@"S-1-5-21-[0-9\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UrlRegex = new(@"https?://[^\s\x00""'<>]{4,300}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExecutablePathRegex = new(@"(?i)(?:[A-Z]:\\|\\\\)[^\x00\r\n""<>|]{2,300}\.(?:exe|com|bat|cmd|ps1|msi|dll)", RegexOptions.Compiled);
    private static readonly Regex ExecutableNameRegex = new(@"(?i)\b[a-z0-9][a-z0-9._+\- ()]{0,120}\.(?:exe|com|bat|cmd|ps1|msi)\b", RegexOptions.Compiled);

    public string ParserName => "Windows SRUM Parser";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        return name.Equals("SRUDB.dat", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_SRUDB.dat", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var timestamp = DateTime.MinValue;
            var sourceLastWriteUtc = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).ToString("O") : string.Empty;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var indicator in ExtractIndicators(bytes).Take(1500))
            {
                var cleaned = ParserSupport.Clean(indicator.Value);
                if (string.IsNullOrWhiteSpace(cleaned) || !seen.Add(indicator.Kind + "\u001F" + cleaned)) continue;

                var ev = new NormalizedEvent
                {
                    DataSource = "SRUM",
                    Operation = indicator.Kind == "ExecutablePath" || indicator.Kind == "ExecutableName" ? "Srum_Process_Indicator" : indicator.Kind == "Url" ? "Srum_Network_Indicator" : "Srum_Identity_Indicator",
                    ObjectPath = cleaned,
                    UserId = indicator.Kind == "SID" ? cleaned : "Unknown",
                    TimestampUtc = timestamp
                };
                ParserSupport.SetEventTime(ev, null, "SrumEseTimestampNotDecoded", "MetadataOnly", false, "SRUM full ESE row timestamps are not decoded in this best-effort parser; file timestamps suppressed.");
                ev.AdditionalFields["SourceFileLastWriteUtc"] = sourceLastWriteUtc;
                ev.AdditionalFields["EventCategory"] = indicator.Kind == "Url" ? "NetworkUsage" : indicator.Kind == "SID" ? "Identity" : "Execution";
                ev.AdditionalFields["ArtifactType"] = "SRUM_BestEffortIndicator";
                ev.AdditionalFields["SrumParseMode"] = "BestEffortStringCarve";
                ev.AdditionalFields["SrumIndicatorType"] = indicator.Kind;
                ev.AdditionalFields["SrumIndicatorValue"] = cleaned;
                ev.AdditionalFields["SourceWarning"] = "SRUDB.dat is an ESE database. This parser safely extracts searchable indicators but does not reconstruct SRUM table rows or byte counters.";

                if (indicator.Kind == "ExecutablePath")
                    ParserSupport.AddTargetFields(ev, cleaned, "SRUMStringCarveExecutablePath");
                else if (indicator.Kind == "Url")
                {
                    ev.AdditionalFields["Domain"] = ParserSupport.InferCloudOrWebDomain(cleaned);
                    ParserSupport.AddTargetFields(ev, cleaned, "SRUMStringCarveUrl");
                }
                else if (indicator.Kind == "ExecutableName")
                    ev.AdditionalFields["FileName"] = cleaned;

                ParserSupport.AddParseQuality(ev, ParserName, "Low", "Best-effort string extraction from SRUDB.dat; exact SRUM ESE row parsing not yet implemented.");
                events.Add(ev);
            }

            if (events.Count == 0)
            {
                var ev = new NormalizedEvent
                {
                    DataSource = "SRUM",
                    Operation = "Srum_Database_Observed",
                    ObjectPath = filePath,
                    UserId = "Unknown",
                    TimestampUtc = timestamp
                };
                ParserSupport.SetEventTime(ev, null, "SrumEseTimestampNotDecoded", "MetadataOnly", false, "SRUM full ESE row timestamps are not decoded in this best-effort parser; file timestamps suppressed.");
                ev.AdditionalFields["SourceFileLastWriteUtc"] = sourceLastWriteUtc;
                ev.AdditionalFields["EventCategory"] = "NetworkUsage";
                ev.AdditionalFields["ArtifactType"] = "SRUDB_dat";
                ev.AdditionalFields["SrumParseMode"] = "MetadataOnly";
                ParserSupport.AddParseQuality(ev, ParserName, "Low", "SRUDB.dat present, but no useful strings were recovered.");
                events.Add(ev);
            }
        }
        catch (Exception ex)
        {
            log($"Failed to parse SRUM {Path.GetFileName(filePath)}: {ex.Message}");
            var ev = new NormalizedEvent
            {
                DataSource = "SRUM",
                Operation = "Srum_ParseError",
                ObjectPath = filePath,
                UserId = "Unknown",
                TimestampUtc = DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "ParserError";
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.SetEventTime(ev, null, "SrumParseError", "MetadataOnly", false, "SRUM parse error row has no behavioral timestamp.");
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Exception during SRUM string extraction.");
            events.Add(ev);
        }

        return events;
    }

    private static IEnumerable<(string Kind, string Value)> ExtractIndicators(byte[] bytes)
    {
        foreach (var text in EnumerateStringViews(bytes))
        {
            foreach (Match m in SidRegex.Matches(text))
                yield return ("SID", m.Value);
            foreach (Match m in UrlRegex.Matches(text))
                yield return ("Url", ForensicText.TrimBinaryPathTail(m.Value));
            foreach (Match m in ExecutablePathRegex.Matches(text))
            {
                var candidate = ForensicText.TrimBinaryPathTail(m.Value);
                if (ForensicText.IsLikelyPath(candidate)) yield return ("ExecutablePath", candidate);
            }
            foreach (Match m in ExecutableNameRegex.Matches(text))
            {
                var name = ParserSupport.Clean(m.Value);
                if (name.Length >= 5 && ForensicText.LooksLikeWindowsExecutableName(name)) yield return ("ExecutableName", name);
            }
        }
    }

    private static IEnumerable<string> EnumerateStringViews(byte[] bytes)
    {
        if (bytes.Length == 0) yield break;
        string ascii = Encoding.ASCII.GetString(bytes);
        if (!string.IsNullOrWhiteSpace(ascii)) yield return ascii;
        string unicode = Encoding.Unicode.GetString(bytes);
        if (!string.IsNullOrWhiteSpace(unicode)) yield return unicode;
    }
}
