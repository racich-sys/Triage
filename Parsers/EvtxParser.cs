using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using WinEventRecord = System.Diagnostics.Eventing.Reader.EventRecord;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VestigantTriage;

public class EvtxParser : IArtifactParser
{
    public string ParserName => "Windows Event Log (EVTX)";

    public bool CanParse(string filePath) => filePath.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var sourceLogName = InferLogName(filePath);
        var sourceFileName = Path.GetFileName(filePath);
        using var reader = new EventLogReader(filePath, PathType.FilePath);
        WinEventRecord? record;
        var count = 0;

        while ((record = reader.ReadEvent()) != null)
        {
            using (record)
            {
                count++;
                var provider = record.ProviderName ?? string.Empty;
                var logName = string.IsNullOrWhiteSpace(record.LogName) ? sourceLogName : record.LogName;
                var timeUtc = record.TimeCreated?.ToUniversalTime();
                var ev = new NormalizedEvent
                {
                    DataSource = "WinEventLog",
                    UserId = record.UserId?.Value ?? string.Empty,
                    Operation = $"EventID_{record.Id}",
                    ObjectPath = provider
                };

                ParserSupport.SetEventTime(ev, timeUtc, "EvtxRecordTime", "High", timeUtc.HasValue, timeUtc.HasValue ? string.Empty : "EVTX record did not contain a TimeCreated value.");
                ev.AdditionalFields["EventCategory"] = "EventLog";
                ev.AdditionalFields["ArtifactType"] = "EVTXRecord";
                ev.AdditionalFields["EventId"] = record.Id.ToString();
                ev.AdditionalFields["ProviderName"] = provider;
                ev.AdditionalFields["LogName"] = logName;
                ev.AdditionalFields["SourceEvtxFile"] = sourceFileName;
                ev.AdditionalFields["RecordId"] = record.RecordId?.ToString() ?? string.Empty;
                ev.AdditionalFields["Level"] = record.Level?.ToString() ?? string.Empty;
                ev.AdditionalFields["Task"] = record.Task?.ToString() ?? string.Empty;
                ev.AdditionalFields["Opcode"] = record.Opcode?.ToString() ?? string.Empty;
                ParserSupport.AddParseQuality(ev, ParserName, "Medium", "EVTX core fields decoded through Windows EventLog API; XML hydration is restricted to targeted logs/providers for ingest performance.");

                try
                {
                    var shouldHydrate = ShouldHydrateXmlOrMessage(provider, logName, sourceFileName, record.Id);
                    string xml = string.Empty;
                    string message = string.Empty;

                    if (shouldHydrate)
                    {
                        try { xml = record.ToXml(); } catch (Exception ex) { ev.AdditionalFields["EventXmlWarning"] = ex.Message; }
                        if (!string.IsNullOrWhiteSpace(xml))
                        {
                            ev.AdditionalFields["EventXmlSnippet"] = xml.Length > 2000 ? xml[..2000] : xml;
                            HydrateXmlFields(ev, xml);
                        }

                        if (IsOAlerts(provider, logName, sourceFileName))
                        {
                            try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                            if (!string.IsNullOrWhiteSpace(message))
                                ev.AdditionalFields["RenderedMessage"] = message.Length > 4000 ? message[..4000] : message;
                        }
                    }

                    ApplyTargetedNormalization(ev, filePath, xml, message);
                    if (ev.AdditionalFields.TryGetValue("IpAddress", out var ip) && !string.IsNullOrWhiteSpace(ip)) ev.ClientIp = ip;
                    if (string.IsNullOrWhiteSpace(ev.UserId))
                    {
                        foreach (var key in new[] { "TargetUserName", "SubjectUserName", "User", "AccountName" })
                        {
                            if (ev.AdditionalFields.TryGetValue(key, out var user) && !string.IsNullOrWhiteSpace(user))
                            {
                                ev.UserId = user;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ev.AdditionalFields["ParseWarning"] = ex.Message;
                }

                yield return ev;
            }
        }

        if (count > 50000)
            log($"    EVTX performance note: {sourceFileName} emitted {count:N0} records. XML/message hydration was limited to targeted providers/logs.");
    }

    private static bool ShouldHydrateXmlOrMessage(string provider, string logName, string fileName, int eventId)
    {
        var combined = $"{provider} {logName} {fileName}";
        if (IsOAlerts(provider, logName, fileName)) return true;
        if (combined.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)) return true;
        if (combined.Contains("PrintService", StringComparison.OrdinalIgnoreCase) && IsPrintServiceHydrationEvent(eventId)) return true;
        if (combined.Contains("Kernel-PnP", StringComparison.OrdinalIgnoreCase)) return true;
        if (combined.Contains("DeviceSetupManager", StringComparison.OrdinalIgnoreCase)) return true;
        if (combined.Contains("Bits-Client", StringComparison.OrdinalIgnoreCase)) return true;
        if (combined.Contains("Security", StringComparison.OrdinalIgnoreCase) && (eventId == 4688 || eventId == 1102 || eventId == 4663 || eventId == 4624 || eventId == 4634 || eventId == 4720 || eventId == 4726)) return true;
        return false;
    }

    private static void HydrateXmlFields(NormalizedEvent ev, string xml)
    {
        var xDoc = XDocument.Parse(xml);
        var ns = xDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        int unnamed = 0;
        foreach (var data in xDoc.Descendants(ns + "EventData").Elements().Concat(xDoc.Descendants(ns + "UserData").Descendants()))
        {
            var name = data.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = data.Name.LocalName;
                if (string.Equals(name, "Data", StringComparison.OrdinalIgnoreCase))
                    name = $"Data{++unnamed}";
            }

            var value = ParserSupport.Clean(data.Value);
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value) && !ev.AdditionalFields.ContainsKey(name))
                ev.AdditionalFields[name] = value;
        }
    }

    private static void ApplyTargetedNormalization(NormalizedEvent ev, string filePath, string xml, string message)
    {
        var provider = Field(ev, "ProviderName");
        var id = Field(ev, "EventId");
        var sourceFileName = Path.GetFileName(filePath);
        var logName = Field(ev, "LogName");
        var combined = $"{provider} {logName} {sourceFileName}";

        if (IsOAlerts(provider, logName, sourceFileName))
        {
            ApplyOAlertsNormalization(ev, xml, message);
        }
        else if (combined.Contains("PrintService", StringComparison.OrdinalIgnoreCase))
        {
            ApplyPrintServiceNormalization(ev);
        }
        else if (combined.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            ev.AdditionalFields["EventCategory"] = "ScriptExecution";
            ev.AdditionalFields["ArtifactType"] = "PowerShellEVTX";
            ev.Operation = id switch
            {
                "4104" => "PowerShell_ScriptBlock",
                "4103" => "PowerShell_Module_Command",
                "400" => "PowerShell_Engine_Start",
                "403" => "PowerShell_Engine_Stop",
                "600" => "PowerShell_Provider_Lifecycle",
                _ => ev.Operation
            };
            var script = FirstNonBlank(Field(ev, "ScriptBlockText"), Field(ev, "Payload"), Field(ev, "CommandLine"), Field(ev, "HostApplication"), Field(ev, "ContextInfo"), Field(ev, "Data1"));
            if (!string.IsNullOrWhiteSpace(script))
            {
                PowerShellCommandSupport.AddCommandFields(ev, script);
                if (!string.IsNullOrWhiteSpace(Field(ev, "ScriptBlockId"))) ev.AdditionalFields["ScriptBlockId"] = Field(ev, "ScriptBlockId");
                if (!string.IsNullOrWhiteSpace(Field(ev, "RunspaceId"))) ev.AdditionalFields["RunspaceId"] = Field(ev, "RunspaceId");
                if (!string.IsNullOrWhiteSpace(Field(ev, "HostApplication"))) ev.AdditionalFields["HostApplication"] = Field(ev, "HostApplication");
            }
        }
        else if (combined.Contains("Kernel-PnP", StringComparison.OrdinalIgnoreCase) || combined.Contains("DeviceSetupManager", StringComparison.OrdinalIgnoreCase))
        {
            ev.AdditionalFields["EventCategory"] = "Device";
            var device = FirstNonBlank(Field(ev, "DeviceInstanceId"), Field(ev, "DeviceId"), Field(ev, "DeviceName"), Field(ev, "Data1"));
            if (!string.IsNullOrWhiteSpace(device)) ParserSupport.AddTargetFields(ev, device, "DeviceEvent");
        }
        else if (combined.Contains("Bits-Client", StringComparison.OrdinalIgnoreCase))
        {
            ev.AdditionalFields["EventCategory"] = "NetworkTransfer";
            var url = FirstNonBlank(Field(ev, "url"), Field(ev, "Url"), Field(ev, "RemoteName"), Field(ev, "Data1"));
            if (!string.IsNullOrWhiteSpace(url)) ParserSupport.AddTargetFields(ev, url, "BITS");
        }
        else if (combined.Contains("Security", StringComparison.OrdinalIgnoreCase))
        {
            ev.AdditionalFields["EventCategory"] = "Security";
        }
    }

    private static bool IsPrintServiceHydrationEvent(int eventId)
    {
        return eventId is 307 or 308 or 310 or 311 or 372 or 805 or 806 or 808 or 842;
    }

    private static void ApplyPrintServiceNormalization(NormalizedEvent ev)
    {
        var id = Field(ev, "EventId");
        ev.DataSource = "PrintService_EVTX";
        ev.AdditionalFields["EventCategory"] = "Print";
        ev.AdditionalFields["ArtifactType"] = "PrintServiceEVTX";
        ev.AdditionalFields["ParserConfidence"] = "Medium";
        ev.AdditionalFields["ParserConfidenceBasis"] = "PrintService EVTX record; XML hydration is applied to targeted print event IDs.";
        ev.Operation = id switch
        {
            "307" => "FilePrinted",
            "308" => "PrintService_Event_308",
            "310" => "PrintService_Event_310",
            "311" => "PrintService_Event_311",
            "372" => "PrintService_Event_372",
            "805" => "PrintService_Event_805",
            "806" => "PrintService_Event_806",
            "808" => "PrintService_Event_808",
            "842" => "PrintService_Event_842",
            _ => string.IsNullOrWhiteSpace(id) ? "PrintService_Event" : "PrintService_Event_" + id
        };

        var target = FirstNonBlank(
            Field(ev, "Document"),
            Field(ev, "DocumentName"),
            Field(ev, "Param2"),
            Field(ev, "Data2"),
            Field(ev, "FileName"),
            Field(ev, "JobName"));
        if (!string.IsNullOrWhiteSpace(target))
            ParserSupport.AddTargetFields(ev, target, "PrintServiceDocument");

        var printer = FirstNonBlank(
            Field(ev, "Printer"),
            Field(ev, "PrinterName"),
            Field(ev, "Param5"),
            Field(ev, "Data5"),
            Field(ev, "Port"),
            Field(ev, "PortName"));
        if (!string.IsNullOrWhiteSpace(printer))
            ev.AdditionalFields["PrintServicePrinterCandidate"] = printer;

        var user = FirstNonBlank(Field(ev, "User"), Field(ev, "UserName"), Field(ev, "Param3"), Field(ev, "Data3"));
        if (!string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(ev.UserId))
            ev.UserId = user;
    }

    private static void ApplyOAlertsNormalization(NormalizedEvent ev, string xml, string message)
    {
        ev.DataSource = "Office_OAlerts";
        ev.AdditionalFields["EventCategory"] = "OfficeFileActivity";
        ev.AdditionalFields["ArtifactType"] = "OAlertsEVTX";
        ev.AdditionalFields["ParserConfidence"] = "Medium";
        ev.AdditionalFields["ParserConfidenceBasis"] = "OAlerts EVTX record with Office alert message/XML hydration.";

        var text = string.Join("\n", new[] { message, xml }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var target = FirstUsefulOfficePathOrName(text, ev);
        if (!string.IsNullOrWhiteSpace(target))
        {
            ParserSupport.AddTargetFields(ev, target, "OAlertsMessageOrXml");
            ParserSupport.AddInternetClassificationFields(ev, target, "OAlertsTarget");
        }

        var lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "save", "saved", "saving", "autosave", "auto save"))
            ev.Operation = "Office_File_Saved_OAlert";
        else if (ContainsAny(lower, "edit", "editing", "modified", "change", "conflict"))
            ev.Operation = "Office_File_Edited_OAlert";
        else if (ContainsAny(lower, "upload", "download", "sync", "sharepoint", "onedrive"))
            ev.Operation = "Office_Cloud_File_Activity_OAlert";
        else if (!string.IsNullOrWhiteSpace(target))
            ev.Operation = "Office_File_Activity_OAlert";
        else
            ev.Operation = "Office_OAlert_Event";

        ev.AdditionalFields["OAlertsInterpretation"] = "Microsoft Office OAlerts event. Treat as Office application alert/context evidence; confirm with Office MRU, file system, UAL, or cloud sync artifacts when making findings.";
    }

    private static string FirstUsefulOfficePathOrName(string text, NormalizedEvent ev)
    {
        foreach (var key in new[] { "DocumentPath", "FilePath", "Path", "FullPath", "FileName", "Document", "Name", "Data1", "Data2", "Data3" })
        {
            var value = Field(ev, key);
            if (LooksLikeOfficeTarget(value)) return value;
        }

        foreach (var candidate in ForensicText.ExtractPathCandidates(text))
        {
            if (LooksLikeOfficeTarget(candidate)) return candidate;
        }

        var url = Regex.Match(text, "(?i)https?://[^\\s\"\'<>]+(?:docx?|xlsx?|pptx?|xlsm|xlsb|csv|pdf|msg|eml)?[^\\s\"\'<>]*");
        if (url.Success) return ParserSupport.Clean(url.Value);

        var file = Regex.Match(text, @"(?i)([A-Z0-9][A-Z0-9._() \-]{2,220}\.(?:docx?|xlsx?|pptx?|xlsm|xlsb|csv|pdf|msg|eml))");
        if (file.Success) return ParserSupport.Clean(file.Groups[1].Value);

        return string.Empty;
    }

    private static bool LooksLikeOfficeTarget(string? value)
    {
        value = ParserSupport.Clean(value);
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length < 4) return false;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(value, @"(?i)([A-Z]:\\|\\\\).+\.(docx?|xlsx?|pptx?|xlsm|xlsb|csv|pdf|msg|eml)$")) return true;
        if (Regex.IsMatch(value, @"(?i)\.(docx?|xlsx?|pptx?|xlsm|xlsb|csv|pdf|msg|eml)$")) return true;
        return false;
    }

    private static bool IsOAlerts(string provider, string logName, string fileName)
    {
        var combined = $"{provider} {logName} {fileName}";
        return combined.Contains("OAlerts", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("OAlert", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Microsoft Office Alerts", StringComparison.OrdinalIgnoreCase) ||
               (combined.Contains("Office", StringComparison.OrdinalIgnoreCase) && combined.Contains("Alert", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string text, params string[] terms) => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    private static string Field(NormalizedEvent ev, string key) => ev.AdditionalFields.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    private static string InferLogName(string filePath) => Path.GetFileNameWithoutExtension(filePath).Replace("%4", "/");
}
