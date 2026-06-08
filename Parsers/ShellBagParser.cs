using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Registry;

namespace VestigantTriage;

public class ShellBagsParser : IArtifactParser
{
    public string ParserName => "Windows ShellBags";

    public bool CanParse(string filePath)
    {
        var name = (Path.GetFileName(filePath) ?? string.Empty).ToUpperInvariant();
        return name.Equals("USRCLASS.DAT", StringComparison.Ordinal) ||
               name.EndsWith("_USRCLASS.DAT", StringComparison.Ordinal) ||
               name.Equals("NTUSER.DAT", StringComparison.Ordinal) ||
               name.EndsWith("_NTUSER.DAT", StringComparison.Ordinal);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var hive = new RegistryHive(filePath);
            hive.ParseHive();
            var user = ParserSupport.ExtractUserFromPath(filePath, "LocalUser");

            foreach (var rootPath in new[]
            {
                // UsrClass.dat primary locations
                @"Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
                @"Local Settings\Software\Microsoft\Windows\Shell\Bags",

                // NTUSER.DAT / legacy locations
                @"Software\Microsoft\Windows\Shell\BagMRU",
                @"Software\Microsoft\Windows\Shell\Bags",
                @"Software\Microsoft\Windows\ShellNoRoam\BagMRU",
                @"Software\Microsoft\Windows\ShellNoRoam\Bags",
                @"Local Settings\Software\Microsoft\Windows\ShellNoRoam\BagMRU",
                @"Local Settings\Software\Microsoft\Windows\ShellNoRoam\Bags"
            })
            {
                if (rootPath.EndsWith("\\Bags", StringComparison.OrdinalIgnoreCase))
                    events.AddRange(ProcessBagsSettingsNode(hive, rootPath, user, rootPath, 0));
                else
                    events.AddRange(ProcessBagNode(hive, rootPath, string.Empty, user, rootPath, 0));
            }
        }
        catch (Exception ex)
        {
            log($"ShellBag parser failed for {Path.GetFileName(filePath)}: {ex.Message}");
            events.Add(new NormalizedEvent
            {
                DataSource = "ShellBags",
                Operation = "ShellBags_ParseError",
                ObjectPath = filePath,
                TimestampUtc = DateTime.MinValue,
                UserId = ParserSupport.ExtractUserFromPath(filePath),
                AdditionalFields = { ["ParseError"] = ex.Message, ["EventCategory"] = "ParserError" }
            });
        }
        return events;
    }

    private IEnumerable<NormalizedEvent> ProcessBagNode(RegistryHive hive, string keyPath, string currentPath, string userId, string rootPath, int depth)
    {
        var nodeEvents = new List<NormalizedEvent>();
        var node = hive.GetKey(keyPath);
        if (node == null) return nodeEvents;

        var childPathBySlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic val in node.Values)
        {
            string valueName = SafeValueName(val);
            if (valueName.Equals("NodeSlot", StringComparison.OrdinalIgnoreCase) || valueName.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase) || valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase))
                continue;

            byte[] raw = SafeValueRaw(val);
            if (raw.Length == 0) continue;

            var folderName = ExtractStringFromPidl(raw);
            if (string.IsNullOrWhiteSpace(folderName) || folderName.Equals("Unknown Folder", StringComparison.OrdinalIgnoreCase))
                continue;

            var newPath = string.IsNullOrWhiteSpace(currentPath) || LooksLikeRootedPath(folderName) ? folderName : $@"{currentPath}\{folderName}";
            newPath = ForensicText.TrimBinaryPathTail(newPath);
            if (string.IsNullOrWhiteSpace(newPath)) continue;

            if (int.TryParse(valueName, out _)) childPathBySlot[valueName] = newPath;

            var ev = new NormalizedEvent
            {
                DataSource = "ShellBags",
                Operation = "Folder_Navigated",
                ObjectPath = newPath,
                TimestampUtc = LastWriteUtc(node),
                UserId = string.IsNullOrWhiteSpace(userId) ? "LocalUser" : userId
            };

            ev.AdditionalFields["EventCategory"] = "FolderAccess";
            ev.AdditionalFields["ArtifactType"] = "ShellBagMRU";
            ev.AdditionalFields["FolderPath"] = newPath;
            ev.AdditionalFields["RegistryKeyPath"] = keyPath;
            ev.AdditionalFields["ShellBagRoot"] = rootPath;
            ev.AdditionalFields["ShellBagValueName"] = valueName;
            ev.AdditionalFields["ShellBagDepth"] = depth.ToString();
            ev.AdditionalFields["RegistryKeyLastWriteUtc"] = LastWriteUtc(node).ToString("O");
            ev.AdditionalFields["DriveType"] = ParserSupport.DetermineDriveType(newPath);
            ParserSupport.AddTargetFields(ev, newPath, "ShellBagPath");
            ParserSupport.AddParseQuality(ev, ParserName, LooksLikeRootedPath(newPath) ? "High" : "Medium", "BagMRU shell item decoded best-effort; registry key last-write used as timestamp.");
            nodeEvents.Add(ev);
        }

        foreach (dynamic subKey in node.SubKeys)
        {
            string keyName = SafeKeyName(subKey);
            if (int.TryParse(keyName, out _))
            {
                var childBasePath = childPathBySlot.TryGetValue(keyName, out var mappedPath) ? mappedPath : currentPath;
                nodeEvents.AddRange(ProcessBagNode(hive, $@"{keyPath}\{keyName}", childBasePath, userId, rootPath, depth + 1));
            }
        }
        return nodeEvents;
    }

    private IEnumerable<NormalizedEvent> ProcessBagsSettingsNode(RegistryHive hive, string keyPath, string userId, string rootPath, int depth)
    {
        var events = new List<NormalizedEvent>();
        var node = hive.GetKey(keyPath);
        if (node == null) return events;

        bool hasShellValues = false;
        try
        {
            foreach (dynamic val in node.Values)
            {
                var valueName = SafeValueName(val);
                if (!string.IsNullOrWhiteSpace(valueName))
                {
                    hasShellValues = true;
                    break;
                }
            }
        }
        catch { }

        if (hasShellValues)
        {
            var ev = new NormalizedEvent
            {
                DataSource = "ShellBags",
                Operation = "Folder_View_Settings_Observed",
                ObjectPath = keyPath,
                TimestampUtc = LastWriteUtc(node),
                UserId = string.IsNullOrWhiteSpace(userId) ? "LocalUser" : userId
            };
            ev.AdditionalFields["EventCategory"] = "FolderAccess";
            ev.AdditionalFields["ArtifactType"] = "ShellBagBagsSettings";
            ev.AdditionalFields["RegistryKeyPath"] = keyPath;
            ev.AdditionalFields["ShellBagRoot"] = rootPath;
            ev.AdditionalFields["ShellBagDepth"] = depth.ToString();
            ev.AdditionalFields["RegistryKeyLastWriteUtc"] = LastWriteUtc(node).ToString("O");
            ParserSupport.AddTargetFields(ev, keyPath, "ShellBagBagsRegistryKey");
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "ShellBags Bags view-settings key observed. BagMRU contains stronger folder-name evidence; Bags keys are retained as supporting artifacts.");
            events.Add(ev);
        }

        try
        {
            foreach (dynamic subKey in node.SubKeys)
            {
                var keyName = SafeKeyName(subKey);
                if (!string.IsNullOrWhiteSpace(keyName))
                    events.AddRange(ProcessBagsSettingsNode(hive, $@"{keyPath}\{keyName}", userId, rootPath, depth + 1));
            }
        }
        catch { }

        return events;
    }

    private string ExtractStringFromPidl(byte[] data)
    {
        foreach (var candidate in ParserSupport.ExtractUsefulPathCandidates(data, 1)) return candidate;

        var unicode = Encoding.Unicode.GetString(data);
        var ascii = Encoding.Default.GetString(data);
        foreach (var text in new[] { unicode, ascii })
        {
            var drive = Regex.Match(text, @"[A-Za-z]:\\[^\0]+", RegexOptions.IgnoreCase);
            if (drive.Success) return CleanPath(drive.Value);
            var unc = Regex.Match(text, @"\\\\[A-Za-z0-9_.\-]+\\[^\0]+", RegexOptions.IgnoreCase);
            if (unc.Success) return CleanPath(unc.Value);
        }

        var match = Regex.Match(unicode, @"[A-Za-z0-9 _.$(){}\[\]\-]{3,}");
        if (!match.Success) match = Regex.Match(ascii, @"[A-Za-z0-9 _.$(){}\[\]\-]{3,}");
        return match.Success ? ParserSupport.Clean(match.Value) : "Unknown Folder";
    }

    private static string CleanPath(string value) => ParserSupport.Clean(value.Split('\0')[0]).TrimEnd('\0');
    private static bool LooksLikeRootedPath(string value) => value.StartsWith(@"\\", StringComparison.Ordinal) || Regex.IsMatch(value, @"^[A-Za-z]:\\");

    private static string SafeValueName(object val)
    {
        try { return ParserSupport.Clean(val.GetType().GetProperty("ValueName")?.GetValue(val)?.ToString()); }
        catch { return string.Empty; }
    }

    private static byte[] SafeValueRaw(object val)
    {
        try
        {
            var raw = val.GetType().GetProperty("ValueDataRaw")?.GetValue(val);
            if (raw is byte[] bytes) return bytes;
        }
        catch { }
        return Array.Empty<byte>();
    }

    private static string SafeKeyName(object key)
    {
        try { return ParserSupport.Clean(key.GetType().GetProperty("KeyName")?.GetValue(key)?.ToString()); }
        catch { return string.Empty; }
    }

    private static DateTime LastWriteUtc(object key)
    {
        try
        {
            var v = key.GetType().GetProperty("LastWriteTime")?.GetValue(key);
            if (v is DateTimeOffset dto) return dto.UtcDateTime;
            if (v is DateTime dt) return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        }
        catch { }
        return DateTime.MinValue;
    }
}
