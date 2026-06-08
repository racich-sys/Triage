using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VestigantTriage;

public sealed class SetupApiDevLogParser : IArtifactParser
{
    private const string ParserDisplayName = "Windows SetupAPI Logs";
    public string ParserName => ParserDisplayName;

    private static readonly Regex SectionHeaderRegex = new(@"^>>?>\s*\[(?<section>[^\]]+?)\s*-\s*(?<device>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionHeaderNoDeviceRegex = new(@"^>>?>\s*\[(?<section>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionStartRegex = new(@"^>>?>\s*Section start\s+(?<dt>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionEndRegex = new(@"^<<<\s*Section end", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VidPidRegex = new(@"(?i)VID[_&](?<vid>[0-9A-F]{4}).*?PID[_&](?<pid>[0-9A-F]{4})", RegexOptions.Compiled);
    private static readonly Regex UsbStorRegex = new(@"(?i)USBSTOR\\(?<deviceType>[^&\\]+)&Ven_(?<vendor>[^&\\]+)&Prod_(?<product>[^&\\]+)&Rev_(?<revision>[^\\]+)\\(?<serial>[^&\\]+)", RegexOptions.Compiled);
    private static readonly Regex InfRegex = new(@"(?i)\b([A-Z]:\\[^\s]+\.inf|oem\d+\.inf|[a-z0-9_\-]+\.inf)\b", RegexOptions.Compiled);
    private static readonly Regex CommandRegex = new(@"(?i)\b(cmd|command|installing application|launching|executing)\b\s*[:=-]?\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex ClassGuidRegex = new(@"(?i)\{(?<guid>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\}", RegexOptions.Compiled);

    private static readonly string[] TransferDeviceKeywords =
    {
        "usbstor", "usb\\vid_", "wpdbusenum", "mtp", "portable device", "iphone", "ipad", "android", "samsung", "pixel", "mtpusb", "winusb", "libusb", "adb",
        "diskdrive", "disk drive", "volume", "storage", "mass storage", "sd card", "card reader", "nvme", "sata", "scsi", "raid",
        "net", "network adapter", "ethernet", "wireless", "wi-fi", "wifi", "rndis", "bluetooth", "bth", "pan", "serial", "usbser", "com port", "ports"
    };

    private static readonly string[] DestructionToolKeywords =
    {
        "eraser", "sdelete", "secure delete", "wipe", "wiper", "shred", "shredder", "bleachbit", "ccleaner", "dban", "sanitize", "sanitise", "diskpart", "format", "cipher", "cleanmgr", "drive scrub", "scrub", "killdisk", "parted magic", "low level format"
    };

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath)?.ToLowerInvariant() ?? string.Empty;
        return name.EndsWith("setupapi.dev.log", StringComparison.Ordinal) ||
               name.EndsWith("setupapi.dev.log.old", StringComparison.Ordinal) ||
               name.EndsWith("setupapi.app.log", StringComparison.Ordinal) ||
               name.EndsWith("setupapi.app.log.old", StringComparison.Ordinal) ||
               name.Contains("_setupapi.dev.log", StringComparison.Ordinal) ||
               name.Contains("_setupapi.dev.log.old", StringComparison.Ordinal) ||
               name.Contains("_setupapi.app.log", StringComparison.Ordinal) ||
               name.Contains("_setupapi.app.log.old", StringComparison.Ordinal);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var tz = TimeUtil.EnsureTimeZone(tzName);
        var logType = DetermineLogType(filePath);
        SetupSection? current = null;
        var emitted = 0;

        foreach (var rawLine in ReadLinesLenient(filePath))
        {
            var line = rawLine.TrimEnd('\r', '\n');
            var header = SectionHeaderRegex.Match(line);
            if (header.Success)
            {
                foreach (var ev in EmitRelevantSetupSection(current, filePath))
                {
                    emitted++;
                    yield return ev;
                }

                current = new SetupSection
                {
                    LogType = logType,
                    Section = ParserSupport.Clean(header.Groups["section"].Value),
                    DeviceInstanceId = ParserSupport.Clean(header.Groups["device"].Value)
                };
                continue;
            }

            var headerNoDevice = SectionHeaderNoDeviceRegex.Match(line);
            if (headerNoDevice.Success && !line.Contains("Section start", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ev in EmitRelevantSetupSection(current, filePath))
                {
                    emitted++;
                    yield return ev;
                }

                current = new SetupSection
                {
                    LogType = logType,
                    Section = ParserSupport.Clean(headerNoDevice.Groups["section"].Value),
                    DeviceInstanceId = string.Empty
                };
                continue;
            }

            if (current == null)
                continue;

            var start = SectionStartRegex.Match(line);
            if (start.Success)
            {
                current.StartUtc = ParseSetupApiLocalTime(start.Groups["dt"].Value, tz);
                current.StartText = ParserSupport.Clean(start.Groups["dt"].Value);
                AddSampleLine(current, line);
                continue;
            }

            if (SectionEndRegex.IsMatch(line))
            {
                current.EndObserved = true;
                foreach (var ev in EmitRelevantSetupSection(current, filePath))
                {
                    emitted++;
                    yield return ev;
                }
                current = null;
                continue;
            }

            AddSampleLine(current, line);
            ExtractKnownLineFields(current, line);
        }

        foreach (var ev in EmitRelevantSetupSection(current, filePath))
        {
            emitted++;
            yield return ev;
        }

        if (emitted == 0)
        {
            var ev = new NormalizedEvent
            {
                DataSource = logType == "Application" ? "SetupAPI_AppLog" : "SetupAPI_DeviceLog",
                Operation = logType == "Application" ? "SetupApi_AppLog_Parsed_NoHighValueSections" : "SetupApi_DeviceLog_Parsed_NoHighValueSections",
                UserId = "System",
                ObjectPath = Path.GetFileName(filePath)
            };
            ev.AdditionalFields["ArtifactType"] = "SetupApiLog";
            ev.AdditionalFields["EventCategory"] = "DeviceOrDriverInstall";
            ev.AdditionalFields["SetupApiLogType"] = logType;
            ev.AdditionalFields["SourceLogName"] = Path.GetFileName(filePath);
            ev.AdditionalFields["SetupApiInterpretation"] = "SetupAPI log parsed; no high-value transfer/destruction-relevant section was identified by the current triage classifier.";
            ParserSupport.AddParseQuality(ev, ParserName, "Medium", "SetupAPI log parsed; no high-value transfer/destruction-relevant section matched configured classifiers.");
            ParserSupport.SetEventTime(ev, null, "SetupApiNoHighValueSections", "MetadataOnly", false, "No SetupAPI transfer/destruction-relevant section with an artifact-native timestamp was decoded.");
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> EmitRelevantSetupSection(SetupSection? section, string filePath)
    {
        if (section == null)
            yield break;

        var classification = ClassifySection(section);
        if (!classification.IsRelevant)
            yield break;

        var ev = new NormalizedEvent
        {
            DataSource = section.LogType == "Application" ? "SetupAPI_AppLog" : "SetupAPI_DeviceLog",
            UserId = "System",
            Operation = BuildOperation(section, classification),
            ObjectPath = FirstNonBlank(section.DeviceInstanceId, section.Target, section.CommandLine, section.DriverInf, section.Section, Path.GetFileName(filePath))
        };

        ev.AdditionalFields["ArtifactType"] = "SetupApiLog";
        ev.AdditionalFields["EventCategory"] = classification.EventCategory;
        ev.AdditionalFields["SetupApiLogType"] = section.LogType;
        ev.AdditionalFields["SetupApiSection"] = section.Section;
        ev.AdditionalFields["SourceLogName"] = Path.GetFileName(filePath);
        ev.AdditionalFields["SetupApiSectionComplete"] = section.EndObserved ? "Yes" : "No";
        ev.AdditionalFields["SetupApiDeviceCategory"] = classification.DeviceCategory;
        ev.AdditionalFields["SetupApiRiskRelevance"] = classification.RiskRelevance;
        ev.AdditionalFields["TransferPotential"] = classification.TransferPotential;
        ev.AdditionalFields["DestructionPotential"] = classification.DestructionPotential;
        ev.AdditionalFields["SetupApiRiskReason"] = classification.Reason;
        ev.AdditionalFields["DeviceInstallInterpretation"] = "SetupAPI section evidence generally reflects device/driver/application installation activity. It is installation/setup evidence and does not by itself prove every later insertion, data transfer, or destructive action.";
        if (!string.IsNullOrWhiteSpace(section.StartText)) ev.AdditionalFields["SetupApiSectionStartLocal"] = section.StartText;
        if (!string.IsNullOrWhiteSpace(section.DeviceInstanceId)) ev.AdditionalFields["DeviceInstanceId"] = section.DeviceInstanceId;
        if (!string.IsNullOrWhiteSpace(section.DeviceDescription)) ev.AdditionalFields["DeviceDescription"] = section.DeviceDescription;
        if (!string.IsNullOrWhiteSpace(section.Manufacturer)) ev.AdditionalFields["Manufacturer"] = section.Manufacturer;
        if (!string.IsNullOrWhiteSpace(section.Provider)) ev.AdditionalFields["Provider"] = section.Provider;
        if (!string.IsNullOrWhiteSpace(section.ClassName)) ev.AdditionalFields["DeviceClass"] = section.ClassName;
        if (!string.IsNullOrWhiteSpace(section.ClassGuid)) ev.AdditionalFields["DeviceClassGuid"] = section.ClassGuid;
        if (!string.IsNullOrWhiteSpace(section.DriverInf)) ev.AdditionalFields["DriverInf"] = section.DriverInf;
        if (!string.IsNullOrWhiteSpace(section.CommandLine)) ev.AdditionalFields["SetupApiCommandLine"] = section.CommandLine;
        if (!string.IsNullOrWhiteSpace(section.Target)) ev.AdditionalFields["SetupApiTarget"] = section.Target;

        ev.AdditionalFields["DisplayTarget"] = ev.ObjectPath;
        ev.AdditionalFields["TargetPath"] = ev.ObjectPath;
        AddUsbIdentityFields(ev, section.DeviceInstanceId);

        if (section.SampleLines.Count > 0)
            ev.AdditionalFields["SetupApiContext"] = ParserSupport.Clean(string.Join("\n", section.SampleLines.GetRange(0, Math.Min(section.SampleLines.Count, 30))));

        ParserSupport.AddParseQuality(ev, ParserDisplayName, classification.Confidence, "SetupAPI section header, section-start timestamp, and transfer/destruction relevance classifier decoded from setupapi.dev.log or setupapi.app.log.");
        ParserSupport.SetEventTime(ev, section.StartUtc, "SetupApiSectionStartLocal", classification.Confidence, true, "Timestamp is from SetupAPI section start and is interpreted using the selected case/local timezone. Treat as installation/setup evidence, not direct proof of data transfer or destruction.");
        yield return ev;
    }

    private static SetupApiClassification ClassifySection(SetupSection section)
    {
        var text = BuildSearchText(section);
        var isAppLog = section.LogType == "Application";
        var category = DetermineDeviceCategory(text, section.DeviceInstanceId, section.ClassName, section.Section);
        var destructive = ContainsAny(text, DestructionToolKeywords);
        var transfer = ContainsAny(text, TransferDeviceKeywords) || category != "Other";

        if (destructive)
        {
            return new SetupApiClassification
            {
                IsRelevant = true,
                DeviceCategory = category == "Other" ? "DestructiveOrDiskUtilitySoftware" : category,
                EventCategory = isAppLog ? "SetupApiDestructionRelevantApplicationInstall" : "SetupApiDestructionRelevantDriverOrDeviceInstall",
                RiskRelevance = "PotentialDestructionOrAntiForensicTooling",
                TransferPotential = transfer ? "Possible" : "Unknown",
                DestructionPotential = "Possible",
                Reason = "SetupAPI section contains wiping, formatting, shredding, sanitizing, or disk-utility terms.",
                Confidence = "Medium"
            };
        }

        if (category == "USB_Storage" || category == "WPD_Mobile_MTP" || category == "Storage_Controller_Or_Volume")
        {
            return new SetupApiClassification
            {
                IsRelevant = true,
                DeviceCategory = category,
                EventCategory = "SetupApiTransferCapableDeviceInstall",
                RiskRelevance = "PotentialDataTransferDevice",
                TransferPotential = "Likely",
                DestructionPotential = category == "Storage_Controller_Or_Volume" ? "Possible" : "Indirect",
                Reason = "SetupAPI section indicates removable/storage/mobile media or storage-related driver/device installation.",
                Confidence = "High"
            };
        }

        if (category == "Network_Adapter" || category == "Bluetooth" || category == "Serial_Or_Debug_Interface" || category == "USB_Generic")
        {
            return new SetupApiClassification
            {
                IsRelevant = true,
                DeviceCategory = category,
                EventCategory = "SetupApiTransferCapableInterfaceInstall",
                RiskRelevance = "PotentialDataTransferInterface",
                TransferPotential = "Possible",
                DestructionPotential = "Indirect",
                Reason = "SetupAPI section indicates an interface that can support data transfer or device-control workflows.",
                Confidence = category == "USB_Generic" ? "Medium" : "High"
            };
        }

        if (isAppLog && transfer)
        {
            return new SetupApiClassification
            {
                IsRelevant = true,
                DeviceCategory = "DeviceAssociatedApplicationOrDriver",
                EventCategory = "SetupApiTransferRelevantApplicationInstall",
                RiskRelevance = "PotentialTransferRelatedDriverOrApplicationInstall",
                TransferPotential = "Possible",
                DestructionPotential = "Unknown",
                Reason = "SetupAPI application log section contains transfer-capable device or driver terms.",
                Confidence = "Medium"
            };
        }

        return new SetupApiClassification { IsRelevant = false, DeviceCategory = category };
    }

    private static string DetermineDeviceCategory(string text, string device, string className, string section)
    {
        var combined = (device + " " + className + " " + section + " " + text).ToLowerInvariant();
        if (combined.Contains("usbstor") || combined.Contains("mass storage") || combined.Contains("usb mass") || combined.Contains("ven_") && combined.Contains("prod_") && combined.Contains("disk")) return "USB_Storage";
        if (combined.Contains("wpdbusenum") || combined.Contains("mtp") || combined.Contains("portable device") || combined.Contains("iphone") || combined.Contains("ipad") || combined.Contains("android") || combined.Contains("samsung") || combined.Contains("pixel")) return "WPD_Mobile_MTP";
        if (combined.Contains("diskdrive") || combined.Contains("disk drive") || combined.Contains("storage") || combined.Contains("volume") || combined.Contains("sd card") || combined.Contains("card reader") || combined.Contains("nvme") || combined.Contains("sata") || combined.Contains("scsi") || combined.Contains("raid")) return "Storage_Controller_Or_Volume";
        if (combined.Contains("bluetooth") || combined.Contains("bth") || combined.Contains("btusb")) return "Bluetooth";
        if (combined.Contains("network adapter") || combined.Contains("ethernet") || combined.Contains("wireless") || combined.Contains("wi-fi") || combined.Contains("wifi") || combined.Contains("rndis") || combined.Contains("ndis") || combined.Contains("net class") || combined.Contains("class net")) return "Network_Adapter";
        if (combined.Contains("usbser") || combined.Contains("serial") || combined.Contains("com port") || combined.Contains("adb") || combined.Contains("android debug") || combined.Contains("ports")) return "Serial_Or_Debug_Interface";
        if (combined.Contains("usb\\vid_") || combined.Contains("winusb") || combined.Contains("libusb")) return "USB_Generic";
        return "Other";
    }

    private static string BuildOperation(SetupSection section, SetupApiClassification classification)
    {
        if (classification.RiskRelevance == "PotentialDestructionOrAntiForensicTooling")
            return section.LogType == "Application" ? "SetupApi_Destruction_Relevant_AppInstall" : "SetupApi_Destruction_Relevant_DeviceInstall";

        return classification.DeviceCategory switch
        {
            "USB_Storage" => "USB_Device_FirstInstall",
            "WPD_Mobile_MTP" => "SetupApi_MobileOrMtp_DeviceInstall",
            "Storage_Controller_Or_Volume" => "SetupApi_Storage_DeviceInstall",
            "Network_Adapter" => "SetupApi_Network_InterfaceInstall",
            "Bluetooth" => "SetupApi_Bluetooth_InterfaceInstall",
            "Serial_Or_Debug_Interface" => "SetupApi_SerialOrDebug_InterfaceInstall",
            "USB_Generic" => "SetupApi_USB_InterfaceInstall",
            _ => section.LogType == "Application" ? "SetupApi_Transfer_Relevant_AppInstall" : "SetupApi_HighValue_DeviceInstall"
        };
    }

    private static string BuildSearchText(SetupSection section)
    {
        var sb = new StringBuilder();
        sb.Append(section.Section).Append(' ')
          .Append(section.DeviceInstanceId).Append(' ')
          .Append(section.DeviceDescription).Append(' ')
          .Append(section.Manufacturer).Append(' ')
          .Append(section.Provider).Append(' ')
          .Append(section.ClassName).Append(' ')
          .Append(section.ClassGuid).Append(' ')
          .Append(section.DriverInf).Append(' ')
          .Append(section.CommandLine).Append(' ')
          .Append(section.Target).Append(' ');
        foreach (var line in section.SampleLines)
            sb.Append(line).Append(' ');
        return sb.ToString().ToLowerInvariant();
    }

    private static void AddUsbIdentityFields(NormalizedEvent ev, string device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return;

        var vidPid = VidPidRegex.Match(device);
        if (vidPid.Success)
        {
            ev.AdditionalFields["UsbVID"] = vidPid.Groups["vid"].Value.ToUpperInvariant();
            ev.AdditionalFields["UsbPID"] = vidPid.Groups["pid"].Value.ToUpperInvariant();
        }

        var stor = UsbStorRegex.Match(device);
        if (stor.Success)
        {
            ev.AdditionalFields["UsbStorageDeviceType"] = CleanDeviceToken(stor.Groups["deviceType"].Value);
            ev.AdditionalFields["UsbVendor"] = CleanDeviceToken(stor.Groups["vendor"].Value);
            ev.AdditionalFields["UsbProduct"] = CleanDeviceToken(stor.Groups["product"].Value);
            ev.AdditionalFields["UsbRevision"] = CleanDeviceToken(stor.Groups["revision"].Value);
            ev.AdditionalFields["UsbSerialNumber"] = CleanDeviceToken(stor.Groups["serial"].Value);
            ev.AdditionalFields["DriveType"] = "USB/Removable";
        }
        else
        {
            var lastSlash = device.LastIndexOf('\\');
            if (lastSlash >= 0 && lastSlash + 1 < device.Length)
            {
                var serial = device[(lastSlash + 1)..];
                if (!string.IsNullOrWhiteSpace(serial)) ev.AdditionalFields["UsbSerialNumber"] = CleanDeviceToken(serial.Split('&')[0]);
            }

            if (device.Contains("USB", StringComparison.OrdinalIgnoreCase))
                ev.AdditionalFields["DriveType"] = "USB/RemovableOrInterface";
        }
    }

    private static void ExtractKnownLineFields(SetupSection section, string line)
    {
        var text = ParserSupport.Clean(line);
        if (string.IsNullOrWhiteSpace(text)) return;

        TryAssign(text, "Device Description", v => section.DeviceDescription = v);
        TryAssign(text, "DeviceDesc", v => section.DeviceDescription = v);
        TryAssign(text, "Manufacturer", v => section.Manufacturer = v);
        TryAssign(text, "Mfg", v => section.Manufacturer = v);
        TryAssign(text, "Provider", v => section.Provider = v);
        TryAssign(text, "Class", v => section.ClassName = v);
        TryAssign(text, "Class Name", v => section.ClassName = v);
        TryAssign(text, "Service", v => section.Target = FirstNonBlank(section.Target, v));

        var inf = InfRegex.Match(text);
        if (inf.Success && string.IsNullOrWhiteSpace(section.DriverInf))
            section.DriverInf = inf.Groups[1].Value;

        var guid = ClassGuidRegex.Match(text);
        if (guid.Success && string.IsNullOrWhiteSpace(section.ClassGuid))
            section.ClassGuid = guid.Value;

        var command = CommandRegex.Match(text);
        if (command.Success && string.IsNullOrWhiteSpace(section.CommandLine))
            section.CommandLine = ParserSupport.Clean(command.Groups["value"].Value);

        if (string.IsNullOrWhiteSpace(section.Target))
        {
            var quotedPath = Regex.Match(text, "(?i)\\\"(?<value>[A-Z]:\\\\[^\\\"]+)\\\"");
            if (quotedPath.Success)
                section.Target = ParserSupport.Clean(quotedPath.Groups["value"].Value);
        }
    }

    private static void TryAssign(string text, string label, Action<string> assign)
    {
        var match = Regex.Match(text, @"(?i)" + Regex.Escape(label) + @"\s*(?:-|:|=)\s*(?<value>.+)$");
        if (match.Success)
        {
            var value = ParserSupport.Clean(match.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(value)) assign(value);
        }
    }

    private static DateTime? ParseSetupApiLocalTime(string value, TimeZoneInfo tz)
    {
        string[] formats = { "yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss.ff", "yyyy/MM/dd HH:mm:ss.f", "yyyy/MM/dd HH:mm:ss" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            try { return TimeZoneInfo.ConvertTimeToUtc(local, tz); }
            catch { return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(); }
        }
        return null;
    }

    private static string DetermineLogType(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        return name.Contains("setupapi.app", StringComparison.OrdinalIgnoreCase) ? "Application" : "Device";
    }

    private static bool ContainsAny(string value, IEnumerable<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var keyword in keywords)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return ParserSupport.Clean(value);
        }
        return string.Empty;
    }

    private static void AddSampleLine(SetupSection section, string line)
    {
        if (section.SampleLines.Count < 250)
            section.SampleLines.Add(line);
    }

    private static string CleanDeviceToken(string value)
    {
        value = ParserSupport.Clean(value).Trim('&', '\\');
        return value.Replace('_', ' ');
    }

    private static IEnumerable<string> ReadLinesLenient(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (!reader.EndOfStream)
            yield return reader.ReadLine() ?? string.Empty;
    }

    private sealed class SetupSection
    {
        public string LogType { get; set; } = "Device";
        public string Section { get; set; } = string.Empty;
        public string DeviceInstanceId { get; set; } = string.Empty;
        public DateTime? StartUtc { get; set; }
        public string StartText { get; set; } = string.Empty;
        public string DeviceDescription { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string ClassGuid { get; set; } = string.Empty;
        public string DriverInf { get; set; } = string.Empty;
        public string CommandLine { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public bool EndObserved { get; set; }
        public List<string> SampleLines { get; } = new();
    }

    private sealed class SetupApiClassification
    {
        public bool IsRelevant { get; set; }
        public string DeviceCategory { get; set; } = "Other";
        public string EventCategory { get; set; } = "DeviceOrDriverInstall";
        public string RiskRelevance { get; set; } = "Unclassified";
        public string TransferPotential { get; set; } = "Unknown";
        public string DestructionPotential { get; set; } = "Unknown";
        public string Reason { get; set; } = string.Empty;
        public string Confidence { get; set; } = "Medium";
    }
}
