using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VestigantTriage;

public sealed class PowerShellTranscriptParser : IArtifactParser
{
    private static readonly Regex PromptRegex = new(@"^PS\s+([^>]+)>\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderRegex = new(@"^\*+\s*(?<key>[^:]+):\s*(?<value>.+?)\s*\**$", RegexOptions.Compiled);

    public string ParserName => "PowerShell Transcript Parser";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        var path = filePath.Replace('/', '\\');
        return name.StartsWith("PowerShell_transcript", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("PowerShell_transcript", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\PowerShell\\Transcripts", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var transcriptStart = DateTime.MinValue;
            var user = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");
            var index = 0;
            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = ParserSupport.Clean(rawLine);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var h = HeaderRegex.Match(line);
                if (h.Success)
                {
                    var key = h.Groups["key"].Value.Trim('*', ' ');
                    var value = h.Groups["value"].Value.Trim('*', ' ');
                    header[key] = value;
                    if (key.Contains("Start time", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out var parsed))
                        transcriptStart = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                    if (key.Equals("Username", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                        user = value;
                    continue;
                }

                var match = PromptRegex.Match(line);
                if (!match.Success) continue;
                var command = match.Groups[2].Value.Trim();
                if (string.IsNullOrWhiteSpace(command)) continue;
                index++;
                var target = FirstNonBlank(PowerShellCommandSupport.ExtractFirstPath(command), PowerShellCommandSupport.ExtractFirstUrl(command), command.Length > 240 ? command[..240] : command);
                var ev = new NormalizedEvent
                {
                    DataSource = "PowerShell_Transcript",
                    Operation = "PowerShell_Transcript_Command",
                    ObjectPath = target,
                    UserId = user,
                    TimestampUtc = transcriptStart == DateTime.MinValue ? DateTime.MinValue : transcriptStart
                };
                ev.AdditionalFields["EventCategory"] = "ScriptExecution";
                ev.AdditionalFields["ArtifactType"] = "PowerShellTranscript";
                ev.AdditionalFields["TranscriptFile"] = filePath;
                ev.AdditionalFields["CommandIndex"] = index.ToString();
                ev.AdditionalFields["PromptPath"] = match.Groups[1].Value;
                ev.AdditionalFields["TimestampBasis"] = transcriptStart == DateTime.MinValue ? "TranscriptFileLastWriteTimeUtc" : "TranscriptStartTimeHeader";
                ParserSupport.SetEventTime(ev, transcriptStart == DateTime.MinValue ? null : transcriptStart, transcriptStart == DateTime.MinValue ? "TranscriptFileLastWriteTimeUtc" : "TranscriptStartTimeHeader", transcriptStart == DateTime.MinValue ? "MetadataOnly" : "Medium", transcriptStart != DateTime.MinValue, transcriptStart == DateTime.MinValue ? "Transcript file last-write suppressed; no transcript header time was decoded." : string.Empty);
                foreach (var kv in header)
                    if (!string.IsNullOrWhiteSpace(kv.Value)) ev.AdditionalFields[$"Transcript_{kv.Key}"] = kv.Value;
                PowerShellCommandSupport.AddCommandFields(ev, command);
                ParserSupport.AddParseQuality(ev, ParserName, "Medium", "PowerShell transcript prompt command extracted; transcript start time or file last-write used as event timestamp.");
                events.Add(ev);
            }

            if (events.Count == 0)
                log($"PowerShell transcript matched but no prompt commands were extracted: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            log($"PowerShell transcript parser failed for {Path.GetFileName(filePath)}: {ex.Message}");
            var ev = new NormalizedEvent
            {
                DataSource = "PowerShell_Transcript",
                Operation = "PowerShellTranscript_ParseError",
                ObjectPath = filePath,
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "ParserError";
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.SetEventTime(ev, null, "PowerShellTranscriptParseError", "MetadataOnly", false, "Parser error row has no behavioral timestamp.");
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Exception during PowerShell transcript parsing.");
            events.Add(ev);
        }
        return events;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
