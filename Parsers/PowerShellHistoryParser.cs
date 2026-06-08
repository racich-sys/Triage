using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VestigantTriage;

public sealed class PowerShellHistoryParser : IArtifactParser
{
    public string ParserName => "PowerShell History Parser";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        return name.Equals("ConsoleHost_history.txt", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_ConsoleHost_history.txt", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var timestamp = DateTime.MinValue;
            var historyFileLastWriteUtc = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).ToString("O") : string.Empty;
            var user = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");
            var lines = File.ReadLines(filePath).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                var command = ParserSupport.Clean(lines[i]);
                if (string.IsNullOrWhiteSpace(command)) continue;

                var target = FirstNonBlank(ExtractFirstPath(command), ExtractFirstUrl(command), command.Length > 240 ? command[..240] : command);
                var ev = new NormalizedEvent
                {
                    DataSource = "PowerShell_History",
                    Operation = "PowerShell_Command_History",
                    ObjectPath = target,
                    UserId = user,
                    TimestampUtc = timestamp
                };

                ev.AdditionalFields["EventCategory"] = "CommandHistory";
                ev.AdditionalFields["ArtifactType"] = "PSReadLineConsoleHistory";
                PowerShellCommandSupport.AddCommandFields(ev, command);
                ev.AdditionalFields["CommandIndex"] = (i + 1).ToString();
                ev.AdditionalFields["TimestampBasis"] = "HistoryFileLastWriteTimeUtc";
                ev.AdditionalFields["HistoryFile"] = filePath;
                ev.AdditionalFields["HistoryFileLastWriteUtc"] = historyFileLastWriteUtc;
                ParserSupport.SetEventTime(ev, null, "HistoryFileLastWriteTimeUtc", "MetadataOnly", false, "PSReadLine history has command order but no per-command execution timestamp; file last-write suppressed.");
                ParserSupport.AddParseQuality(ev, ParserName, "Medium", "PSReadLine command history has command text but no per-command timestamp.");
                events.Add(ev);
            }

            if (events.Count == 0)
                log($"PowerShell history was present but contained no nonblank commands: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            log($"Failed to parse PowerShell history {Path.GetFileName(filePath)}: {ex.Message}");
            var ev = new NormalizedEvent
            {
                DataSource = "PowerShell_History",
                Operation = "PowerShellHistory_ParseError",
                ObjectPath = filePath,
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "ParserError";
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.SetEventTime(ev, null, "PowerShellHistoryParseError", "MetadataOnly", false, "Parser error row has no behavioral timestamp.");
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Exception during PSReadLine history parsing.");
            events.Add(ev);
        }

        return events;
    }

    private static string ExtractFirstPath(string command) => PowerShellCommandSupport.ExtractFirstPath(command);
    private static string ExtractFirstUrl(string command) => PowerShellCommandSupport.ExtractFirstUrl(command);
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
