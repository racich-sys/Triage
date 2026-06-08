using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Registry;

namespace VestigantTriage;

public class RegistryParser : IArtifactParser
{
    public string ParserName => "Windows Registry Parser";

    public bool CanParse(string filePath)
    {
        var name = (Path.GetFileName(filePath) ?? string.Empty).ToUpperInvariant();
        return name.Equals("SYSTEM", StringComparison.Ordinal) ||
               name.EndsWith("_SYSTEM", StringComparison.Ordinal) ||
               name.Equals("SOFTWARE", StringComparison.Ordinal) ||
               name.EndsWith("_SOFTWARE", StringComparison.Ordinal) ||
               name.Equals("NTUSER.DAT", StringComparison.Ordinal) ||
               name.EndsWith("_NTUSER.DAT", StringComparison.Ordinal) ||
               name.Equals("USRCLASS.DAT", StringComparison.Ordinal) ||
               name.EndsWith("_USRCLASS.DAT", StringComparison.Ordinal);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var fileName = (Path.GetFileName(filePath) ?? string.Empty).ToUpperInvariant();
            var hive = new RegistryHive(filePath);
            hive.ParseHive();
            var userId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");

            if (fileName.Equals("SYSTEM", StringComparison.Ordinal) || fileName.EndsWith("_SYSTEM", StringComparison.Ordinal))
            {
                events.AddRange(ParseUsbStor(hive));
                events.AddRange(ParseUsbEnum(hive));
                events.AddRange(ParseWpdbusEnum(hive));
                events.AddRange(ParseMountedDevices(hive));
                events.AddRange(ParseBamDam(hive));
            }
            else if (fileName.Equals("SOFTWARE", StringComparison.Ordinal) || fileName.EndsWith("_SOFTWARE", StringComparison.Ordinal))
            {
                events.AddRange(ParseProfileList(hive));
                events.AddRange(ParseUninstallEntries(hive));
            }
            else if (fileName.Equals("NTUSER.DAT", StringComparison.Ordinal) || fileName.EndsWith("_NTUSER.DAT", StringComparison.Ordinal))
            {
                events.AddRange(ParseUserAssist(hive, userId));
                events.AddRange(ParseExplorerMrus(hive, userId));
                events.AddRange(ParseRunMru(hive, userId));
                events.AddRange(ParseTypedPaths(hive, userId));
                events.AddRange(ParseMountPoints2(hive, userId));
                events.AddRange(ParseOfficeMrus(hive, userId));
                events.AddRange(ParseOneDriveAccounts(hive, userId));
            }
            else if (fileName.Equals("USRCLASS.DAT", StringComparison.Ordinal) || fileName.EndsWith("_USRCLASS.DAT", StringComparison.Ordinal))
            {
                // ShellBagsParser owns the deep BagMRU parsing.  This emits registry-level audit coverage for the hive.
                events.Add(MakeRegistryEvent("Registry_USRCLASS", "UsrClassHive_Observed", filePath, userId, DateTime.MinValue, "RegistryHive", "Medium", "UsrClass hive staged for ShellBags parser; source file timestamp suppressed."));
            }
        }
        catch (Exception ex)
        {
            log($"Registry parser failed for {Path.GetFileName(filePath)}: {ex.Message}");
            events.Add(new NormalizedEvent
            {
                DataSource = "Registry",
                Operation = "Registry_ParseError",
                ObjectPath = filePath,
                TimestampUtc = DateTime.MinValue,
                UserId = ParserSupport.ExtractUserFromPath(filePath),
                AdditionalFields = { ["ParseError"] = ex.Message, ["EventCategory"] = "ParserError" }
            });
        }

        return events;
    }

    private static IEnumerable<NormalizedEvent> ParseUsbStor(RegistryHive hive)
    {
        foreach (var controlSet in ControlSets())
        {
            var usbStor = hive.GetKey($@"{controlSet}\Enum\USBSTOR");
            if (usbStor == null) continue;

            foreach (dynamic device in usbStor.SubKeys)
            {
                foreach (dynamic instance in device.SubKeys)
                {
                    var deviceClass = SafeKeyName(device);
                    var instanceId = SafeKeyName(instance);
                    var friendly = GetValueString(instance, "FriendlyName");
                    var ev = MakeRegistryEvent("Registry_SYSTEM", "USB_Device_Connected", $@"USBSTOR\{deviceClass}\{instanceId}", "LocalSystem", LastWriteUtc(instance), "ExternalMedia", "High", "USBSTOR device instance key last-write and values extracted.");
                    ev.AdditionalFields["ControlSet"] = controlSet;
                    ev.AdditionalFields["UsbDeviceClass"] = deviceClass;
                    ev.AdditionalFields["UsbInstanceId"] = instanceId;
                    ev.AdditionalFields["UsbSerialOrInstance"] = instanceId;
                    ev.AdditionalFields["DriveType"] = "Removable/Secondary";
                    ev.AdditionalFields["FriendlyName"] = friendly;
                    AddRegistryValue(ev, instance, "ParentIdPrefix");
                    AddRegistryValue(ev, instance, "ContainerID");
                    AddRegistryValue(ev, instance, "Service");
                    AddRegistryValue(ev, instance, "ClassGUID");
                    yield return ev;
                }
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseUsbEnum(RegistryHive hive)
    {
        foreach (var controlSet in ControlSets())
        {
            foreach (var root in new[] { "USB", "SCSI", "SWD\\WPDBUSENUM" })
            {
                var key = hive.GetKey($@"{controlSet}\Enum\{root}");
                if (key == null) continue;
                foreach (dynamic device in key.SubKeys)
                {
                    foreach (dynamic instance in device.SubKeys)
                    {
                        var path = $@"{root}\{SafeKeyName(device)}\{SafeKeyName(instance)}";
                        var friendly = FirstNonBlank(GetValueString(instance, "FriendlyName"), GetValueString(instance, "DeviceDesc"));
                        if (string.IsNullOrWhiteSpace(friendly) && root.Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;
                        var ev = MakeRegistryEvent("Registry_SYSTEM", "PnP_Device_Instance", path, "LocalSystem", LastWriteUtc(instance), root.Contains("USB", StringComparison.OrdinalIgnoreCase) ? "ExternalMedia" : "Device", "Medium", "Device enum instance key extracted.");
                        ev.AdditionalFields["ControlSet"] = controlSet;
                        ev.AdditionalFields["DeviceEnumRoot"] = root;
                        ev.AdditionalFields["DeviceInstanceId"] = SafeKeyName(instance);
                        ev.AdditionalFields["DeviceClass"] = SafeKeyName(device);
                        ev.AdditionalFields["FriendlyName"] = friendly;
                        AddRegistryValue(ev, instance, "ContainerID");
                        AddRegistryValue(ev, instance, "ClassGUID");
                        AddRegistryValue(ev, instance, "Service");
                        AddRegistryValue(ev, instance, "Mfg");
                        yield return ev;
                    }
                }
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseWpdbusEnum(RegistryHive hive)
    {
        foreach (var controlSet in ControlSets())
        {
            var key = hive.GetKey($@"{controlSet}\Enum\SWD\WPDBUSENUM");
            if (key == null) continue;
            foreach (dynamic instance in key.SubKeys)
            {
                var path = $@"SWD\WPDBUSENUM\{SafeKeyName(instance)}";
                var ev = MakeRegistryEvent("Registry_SYSTEM", "Portable_Device_Observed", path, "LocalSystem", LastWriteUtc(instance), "ExternalMedia", "Medium", "WPDBUSENUM portable device key extracted.");
                ev.AdditionalFields["ControlSet"] = controlSet;
                ev.AdditionalFields["DeviceInstanceId"] = SafeKeyName(instance);
                ev.AdditionalFields["DriveType"] = "Removable/Secondary";
                AddRegistryValue(ev, instance, "FriendlyName");
                AddRegistryValue(ev, instance, "DeviceDesc");
                AddRegistryValue(ev, instance, "ContainerID");
                yield return ev;
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseMountedDevices(RegistryHive hive)
    {
        var key = hive.GetKey(@"MountedDevices");
        if (key == null) yield break;
        foreach (dynamic val in key.Values)
        {
            string name = SafeValueName(val);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var raw = SafeValueRaw(val);
            var ev = MakeRegistryEvent("Registry_SYSTEM", "MountedDevice_Mapping", name, "LocalSystem", LastWriteUtc(key), "ExternalMedia", "Medium", "MountedDevices value extracted for drive-letter/volume correlation.");
            ev.AdditionalFields["MountedDeviceValueName"] = name;
            ev.AdditionalFields["MountedDeviceValueDataHex"] = BitConverter.ToString(raw).Replace("-", string.Empty);
            ev.AdditionalFields["MountedDeviceValueDataText"] = DecodeBestEffort(raw);
            if (name.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase))
            {
                ev.AdditionalFields["DriveLetter"] = name.Replace(@"\DosDevices\", string.Empty, StringComparison.OrdinalIgnoreCase);
                ev.AdditionalFields["DriveType"] = name.Contains("C:", StringComparison.OrdinalIgnoreCase) ? "Local" : "Removable/Secondary";
            }
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseBamDam(RegistryHive hive)
    {
        foreach (var controlSet in ControlSets())
        foreach (var root in new[] { @"Services\bam\State\UserSettings", @"Services\dam\State\UserSettings" })
        {
            var key = hive.GetKey($@"{controlSet}\{root}");
            if (key == null) continue;
            foreach (dynamic sidKey in key.SubKeys)
            {
                string sid = SafeKeyName(sidKey);
                foreach (dynamic val in sidKey.Values)
                {
                    string exePath = SafeValueName(val);
                    if (string.IsNullOrWhiteSpace(exePath)) continue;
                    var raw = SafeValueRaw(val);
                    var ts = raw.Length >= 8 ? ParserSupport.FromFileTime(BitConverter.ToInt64(raw, 0)) ?? LastWriteUtc(sidKey) : LastWriteUtc(sidKey);
                    var ev = MakeRegistryEvent(root.Contains("bam", StringComparison.OrdinalIgnoreCase) ? "Registry_BAM" : "Registry_DAM", "Application_Executed", exePath, sid, ts, "Execution", "High", "BAM/DAM user execution value extracted.");
                    ParserSupport.AddTargetFields(ev, exePath, "BAM/DAM");
                    ev.AdditionalFields["SID"] = sid;
                    ev.AdditionalFields["ControlSet"] = controlSet;
                    ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(sidKey);
                    yield return ev;
                }
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseProfileList(RegistryHive hive)
    {
        var key = hive.GetKey(@"Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (key == null) yield break;
        foreach (dynamic sidKey in key.SubKeys)
        {
            string sid = SafeKeyName(sidKey);
            string profilePath = GetValueString(sidKey, "ProfileImagePath");
            if (string.IsNullOrWhiteSpace(profilePath)) continue;
            var ev = MakeRegistryEvent("Registry_SOFTWARE", "User_Profile_Mapping", profilePath, sid, LastWriteUtc(sidKey), "Identity", "High", "ProfileList SID to profile path mapping extracted.");
            ev.AdditionalFields["SID"] = sid;
            ev.AdditionalFields["ProfileImagePath"] = profilePath;
            ev.AdditionalFields["ProfileUserName"] = ParserSupport.ExtractUserFromPath(profilePath, string.Empty);
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseUninstallEntries(RegistryHive hive)
    {
        foreach (var root in new[] { @"Microsoft\Windows\CurrentVersion\Uninstall", @"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            var key = hive.GetKey(root);
            if (key == null) continue;
            foreach (dynamic app in key.SubKeys)
            {
                var displayName = GetValueString(app, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                var ev = MakeRegistryEvent("Registry_SOFTWARE", "Installed_Application", displayName, "LocalSystem", LastWriteUtc(app), "InstalledSoftware", "Medium", "Uninstall registry entry extracted.");
                ev.AdditionalFields["DisplayName"] = displayName;
                ev.AdditionalFields["DisplayVersion"] = GetValueString(app, "DisplayVersion");
                ev.AdditionalFields["Publisher"] = GetValueString(app, "Publisher");
                ev.AdditionalFields["InstallLocation"] = GetValueString(app, "InstallLocation");
                ev.AdditionalFields["UninstallString"] = GetValueString(app, "UninstallString");
                yield return ev;
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseUserAssist(RegistryHive hive, string userId)
    {
        var ua = hive.GetKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
        if (ua == null) yield break;
        foreach (dynamic guid in ua.SubKeys)
        {
            var count = hive.GetKey($@"{SafeKeyPath(ua)}\{SafeKeyName(guid)}\Count");
            if (count == null) continue;
            foreach (dynamic val in count.Values)
            {
                var decoded = Rot13(SafeValueName(val));
                if (string.IsNullOrWhiteSpace(decoded)) continue;
                var ev = MakeRegistryEvent("Registry_UserAssist", "Application_Executed", decoded, userId, LastWriteUtc(count), "Execution", "Medium", "UserAssist ROT13 value decoded; key last-write used as timestamp.");
                ParserSupport.AddTargetFields(ev, decoded, "UserAssist");
                ev.AdditionalFields["DecodedUserAssistName"] = decoded;
                ev.AdditionalFields["UserAssistGuid"] = SafeKeyName(guid);
                ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(count);
                yield return ev;
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseExplorerMrus(RegistryHive hive, string userId)
    {
        foreach (var root in new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU"
        })
        {
            var key = hive.GetKey(root);
            if (key == null) continue;
            foreach (var ev in WalkMruKey(key, userId, root)) yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> WalkMruKey(dynamic key, string userId, string rootName)
    {
        foreach (dynamic val in key.Values)
        {
            string name = SafeValueName(val);
            if (name.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase) || name.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = SafeValueRaw(val);
            var decoded = FirstNonBlank(DecodeShellValue(raw), DecodeBestEffort(raw), name);
            if (string.IsNullOrWhiteSpace(decoded)) continue;
            var ev = MakeRegistryEvent("Registry_ExplorerMRU", "User_MRU_Entry", decoded, userId, LastWriteUtc(key), "FileAccess", "Medium", "Explorer MRU registry value decoded best-effort.");
            ParserSupport.AddTargetFields(ev, decoded, "ExplorerMRU");
            ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(key);
            ev.AdditionalFields["RegistryValueName"] = name;
            ev.AdditionalFields["MruRoot"] = rootName;
            yield return ev;
        }
        foreach (dynamic sub in key.SubKeys)
        foreach (var ev in WalkMruKey(sub, userId, rootName)) yield return ev;
    }

    private static IEnumerable<NormalizedEvent> ParseRunMru(RegistryHive hive, string userId)
    {
        var key = hive.GetKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU");
        if (key == null) yield break;
        foreach (dynamic val in key.Values)
        {
            var name = SafeValueName(val);
            if (name.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
            var cmd = GetValueDisplay(val);
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            var ev = MakeRegistryEvent("Registry_RunMRU", "Command_Run", cmd, userId, LastWriteUtc(key), "Execution", "Medium", "RunMRU command value extracted.");
            ev.AdditionalFields["CommandLine"] = cmd;
            ev.AdditionalFields["RegistryValueName"] = name;
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseTypedPaths(RegistryHive hive, string userId)
    {
        var key = hive.GetKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths");
        if (key == null) yield break;
        foreach (dynamic val in key.Values)
        {
            var path = GetValueDisplay(val);
            if (string.IsNullOrWhiteSpace(path)) continue;
            var ev = MakeRegistryEvent("Registry_TypedPaths", "Explorer_TypedPath", path, userId, LastWriteUtc(key), "FolderAccess", "Medium", "Explorer TypedPaths value extracted.");
            ParserSupport.AddTargetFields(ev, path, "TypedPaths");
            ev.AdditionalFields["RegistryValueName"] = SafeValueName(val);
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseMountPoints2(RegistryHive hive, string userId)
    {
        var key = hive.GetKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2");
        if (key == null) yield break;
        foreach (dynamic sub in key.SubKeys)
        {
            var name = SafeKeyName(sub);
            var ev = MakeRegistryEvent("Registry_MountPoints2", "User_MountPoint", name, userId, LastWriteUtc(sub), "ExternalMedia", "High", "User MountPoints2 key observed.");
            ev.AdditionalFields["MountPointName"] = name;
            ev.AdditionalFields["DriveType"] = name.StartsWith("##", StringComparison.Ordinal) ? "Network" : "Removable/Secondary";
            ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(sub);
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> ParseOfficeMrus(RegistryHive hive, string userId)
    {
        var office = hive.GetKey(@"Software\Microsoft\Office");
        if (office == null) yield break;
        foreach (dynamic version in office.SubKeys)
        foreach (dynamic app in version.SubKeys)
        {
            foreach (var subPath in new[] { "File MRU", "Place MRU", "User MRU" })
            {
                var key = hive.GetKey($@"{SafeKeyPath(app)}\{subPath}");
                if (key == null) continue;
                foreach (dynamic val in key.Values)
                {
                    var valueName = SafeValueName(val);
                    if (!valueName.StartsWith("Item", StringComparison.OrdinalIgnoreCase) && !valueName.StartsWith("Place", StringComparison.OrdinalIgnoreCase)) continue;
                    var data = GetValueDisplay(val);
                    if (string.IsNullOrWhiteSpace(data)) continue;
                    var target = ExtractOfficeTarget(data);
                    var ev = MakeRegistryEvent("Registry_OfficeMRU", "Office_Document_MRU", FirstNonBlank(target, data), userId, LastWriteUtc(key), "DocumentActivity", "Medium", "Office MRU value extracted.");
                    if (!string.IsNullOrWhiteSpace(target)) ParserSupport.AddTargetFields(ev, target, "OfficeMRU");
                    ev.AdditionalFields["OfficeVersion"] = SafeKeyName(version);
                    ev.AdditionalFields["OfficeApplication"] = SafeKeyName(app);
                    ev.AdditionalFields["OfficeMruRaw"] = data;
                    ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(key);
                    yield return ev;
                }
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseOneDriveAccounts(RegistryHive hive, string userId)
    {
        var key = hive.GetKey(@"Software\Microsoft\OneDrive\Accounts");
        if (key == null) yield break;
        foreach (dynamic acct in key.SubKeys)
        {
            var userEmail = FirstNonBlank(GetValueString(acct, "UserEmail"), GetValueString(acct, "UserFolder"));
            var folder = GetValueString(acct, "UserFolder");
            var ev = MakeRegistryEvent("Registry_OneDrive", "OneDrive_Account_Configured", FirstNonBlank(userEmail, folder, SafeKeyName(acct)), userId, LastWriteUtc(acct), "CloudSync", "High", "OneDrive account configuration extracted from NTUSER hive.");
            ev.AdditionalFields["OneDriveAccountType"] = SafeKeyName(acct);
            ev.AdditionalFields["OneDriveUserEmail"] = userEmail;
            ev.AdditionalFields["OneDriveUserFolder"] = folder;
            ev.AdditionalFields["OneDriveCID"] = GetValueString(acct, "cid");
            ev.AdditionalFields["OneDriveTenantId"] = GetValueString(acct, "ConfiguredTenantId");
            ev.AdditionalFields["CloudAccount"] = userEmail;
            if (!string.IsNullOrWhiteSpace(folder)) ParserSupport.AddTargetFields(ev, folder, "OneDriveAccountUserFolder");
            yield return ev;
        }
    }

    private static NormalizedEvent MakeRegistryEvent(string source, string operation, string target, string user, DateTime ts, string category, string confidence, string basis)
    {
        var ev = new NormalizedEvent
        {
            DataSource = source,
            Operation = operation,
            ObjectPath = ParserSupport.Clean(target),
            UserId = string.IsNullOrWhiteSpace(user) ? "Unknown" : user,
            TimestampUtc = ts
        };
        ev.AdditionalFields["EventCategory"] = category;
        ev.AdditionalFields["RegistryTimestampBasis"] = "KeyLastWrite";
        ParserSupport.AddParseQuality(ev, "Windows Registry Parser", confidence, basis);
        return ev;
    }

    private static IEnumerable<string> ControlSets() => new[] { "ControlSet001", "ControlSet002", "CurrentControlSet" };

    private static void AddRegistryValue(NormalizedEvent ev, object key, string valueName)
    {
        var value = GetValueString(key, valueName);
        if (!string.IsNullOrWhiteSpace(value)) ev.AdditionalFields[valueName] = value;
    }

    private static string GetValueString(object key, string valueName)
    {
        try
        {
            var valuesObject = GetProperty(key, "Values");
            if (valuesObject is not IEnumerable values) return string.Empty;
            foreach (var item in values)
            {
                if (item == null) continue;
                if (string.Equals(SafeValueName(item), valueName, StringComparison.OrdinalIgnoreCase))
                    return GetValueDisplay(item);
            }
        }
        catch { }
        return string.Empty;
    }

    private static string GetValueDisplay(object val)
    {
        try
        {
            var rawObject = GetProperty(val, "ValueData");
            if (rawObject != null) return ParserSupport.Clean(rawObject.ToString());
            var raw = SafeValueRaw(val);
            return DecodeBestEffort(raw);
        }
        catch { return string.Empty; }
    }

    private static byte[] SafeValueRaw(object val)
    {
        try
        {
            var raw = GetProperty(val, "ValueDataRaw");
            if (raw is byte[] bytes) return bytes;
        }
        catch { }
        return Array.Empty<byte>();
    }

    private static string SafeValueName(object val)
    {
        try { return ParserSupport.Clean(GetProperty(val, "ValueName")?.ToString()); }
        catch { return string.Empty; }
    }

    private static string SafeKeyName(object key)
    {
        try { return ParserSupport.Clean(GetProperty(key, "KeyName")?.ToString()); }
        catch { return string.Empty; }
    }

    private static string SafeKeyPath(object key)
    {
        try { return ParserSupport.Clean(GetProperty(key, "KeyPath")?.ToString()); }
        catch { return string.Empty; }
    }

    private static DateTime LastWriteUtc(object key)
    {
        try
        {
            var v = GetProperty(key, "LastWriteTime");
            if (v is DateTimeOffset dto) return dto.UtcDateTime;
            if (v is DateTime dt) return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        }
        catch { }
        return DateTime.MinValue;
    }

    private static object? GetProperty(object obj, string name) => obj.GetType().GetProperty(name)?.GetValue(obj);

    private static string DecodeBestEffort(byte[] raw)
    {
        if (raw == null || raw.Length == 0) return string.Empty;
        string unicode = ParserSupport.Clean(Encoding.Unicode.GetString(raw).TrimEnd('\0'));
        string ascii = ParserSupport.Clean(Encoding.Default.GetString(raw).TrimEnd('\0'));
        return unicode.Count(c => c >= 0x20 && c <= 0x7e) >= ascii.Count(c => c >= 0x20 && c <= 0x7e) ? unicode : ascii;
    }

    private static string DecodeShellValue(byte[] raw)
    {
        foreach (var path in ParserSupport.ExtractUsefulPathCandidates(raw, 1)) return path;
        return DecodeBestEffort(raw);
    }

    private static string ExtractOfficeTarget(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var parts = raw.Split('*', '|');
        foreach (var part in parts)
        {
            var clean = ParserSupport.Clean(part);
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || ForensicText.IsLikelyPath(clean))
                return clean;
        }
        foreach (var p in ForensicText.ExtractPathCandidates(raw)) return p;
        return string.Empty;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string Rot13(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        char[] array = input.ToCharArray();
        for (int i = 0; i < array.Length; i++)
        {
            int n = array[i];
            if (n >= 'a' && n <= 'z') array[i] = (char)((n - 'a' + 13) % 26 + 'a');
            else if (n >= 'A' && n <= 'Z') array[i] = (char)((n - 'A' + 13) % 26 + 'A');
        }
        return new string(array);
    }
}
