using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Registry;

namespace VestigantTriage;

public sealed class AmCacheParser : IArtifactParser
{
    public string ParserName => "Windows AmCache Parser";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        return name.Equals("Amcache.hve", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Amcache.hve", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Amcache", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Amcache", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            var hive = new RegistryHive(filePath);
            hive.ParseHive();

            events.AddRange(ParseInventoryApplicationFile(hive, filePath));
            events.AddRange(ParseInventoryApplication(hive, filePath));
            events.AddRange(ParseLegacyFileKeys(hive, filePath));

            if (events.Count == 0)
                log($"AmCache parsed but no application/file inventory rows were emitted: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            log($"Failed to parse AmCache {Path.GetFileName(filePath)}: {ex.Message}");
            var ev = new NormalizedEvent
            {
                DataSource = "AmCache",
                Operation = "AmCache_ParseError",
                ObjectPath = filePath,
                UserId = "LocalSystem",
                TimestampUtc = DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "ParserError";
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Registry hive open/query exception.");
            events.Add(ev);
        }

        return events;
    }

    private static IEnumerable<NormalizedEvent> ParseInventoryApplicationFile(RegistryHive hive, string filePath)
    {
        foreach (var rootPath in new[] { @"Root\InventoryApplicationFile", @"InventoryApplicationFile" })
        {
            var root = hive.GetKey(rootPath);
            if (root == null) continue;

            foreach (object item in SafeSubKeys(root))
            {
                var longPath = FirstNonBlank(
                    GetValueString(item, "LowerCaseLongPath"),
                    GetValueString(item, "LongPath"),
                    GetValueString(item, "FullPath"),
                    GetValueString(item, "Name"));

                var name = FirstNonBlank(GetValueString(item, "Name"), ParserSupport.SafeFileName(longPath), SafeKeyName(item));
                var ts = FirstValidDate(
                    TryParseAmCacheDate(GetValueString(item, "Modified")),
                    TryParseAmCacheDate(GetValueString(item, "Created")),
                    TryParseAmCacheDate(GetValueString(item, "LinkDate")),
                    LastWriteUtc(item));

                var ev = new NormalizedEvent
                {
                    DataSource = "AmCache",
                    Operation = "Application_File_Observed",
                    ObjectPath = FirstNonBlank(longPath, name),
                    UserId = "LocalSystem",
                    TimestampUtc = ts
                };

                ev.AdditionalFields["EventCategory"] = "Execution";
                ev.AdditionalFields["ArtifactType"] = "AmCache_InventoryApplicationFile";
                ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(item);
                ev.AdditionalFields["ApplicationName"] = name;
                AddKnownValues(ev, item, new[]
                {
                    "Name", "LowerCaseLongPath", "LongPath", "FileId", "ProgramId", "Publisher",
                    "Version", "ProductName", "ProductVersion", "BinaryType", "Size", "SHA1",
                    "LinkDate", "Created", "Modified", "Language", "IsOsComponent", "Usn"
                });

                ParserSupport.AddTargetFields(ev, FirstNonBlank(longPath, name), "AmCacheInventoryApplicationFile");
                ParserSupport.AddParseQuality(ev, "Windows AmCache Parser", "High", "AmCache InventoryApplicationFile registry key extracted.");
                yield return ev;
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseInventoryApplication(RegistryHive hive, string filePath)
    {
        foreach (var rootPath in new[] { @"Root\InventoryApplication", @"InventoryApplication" })
        {
            var root = hive.GetKey(rootPath);
            if (root == null) continue;

            foreach (object app in SafeSubKeys(root))
            {
                var displayName = FirstNonBlank(GetValueString(app, "Name"), GetValueString(app, "ProgramName"), SafeKeyName(app));
                if (string.IsNullOrWhiteSpace(displayName)) continue;

                var ev = new NormalizedEvent
                {
                    DataSource = "AmCache",
                    Operation = "Installed_Application_Observed",
                    ObjectPath = displayName,
                    UserId = "LocalSystem",
                    TimestampUtc = FirstValidDate(LastWriteUtc(app))
                };

                ev.AdditionalFields["EventCategory"] = "InstalledSoftware";
                ev.AdditionalFields["ArtifactType"] = "AmCache_InventoryApplication";
                ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(app);
                AddKnownValues(ev, app, new[] { "Name", "ProgramName", "Publisher", "Version", "InstallDate", "RootDirPath", "Source", "StoreAppType" });
                ParserSupport.AddParseQuality(ev, "Windows AmCache Parser", "High", "AmCache InventoryApplication registry key extracted.");
                yield return ev;
            }
        }
    }

    private static IEnumerable<NormalizedEvent> ParseLegacyFileKeys(RegistryHive hive, string filePath)
    {
        foreach (var rootPath in new[] { @"Root\File", @"File" })
        {
            var root = hive.GetKey(rootPath);
            if (root == null) continue;

            foreach (object volume in SafeSubKeys(root))
            foreach (object item in SafeSubKeys(volume))
            {
                var path = FirstNonBlank(GetValueString(item, "15"), GetValueString(item, "LowerCaseLongPath"), SafeKeyName(item));
                var ev = new NormalizedEvent
                {
                    DataSource = "AmCache",
                    Operation = "Application_File_Observed",
                    ObjectPath = path,
                    UserId = "LocalSystem",
                    TimestampUtc = FirstValidDate(LastWriteUtc(item))
                };

                ev.AdditionalFields["EventCategory"] = "Execution";
                ev.AdditionalFields["ArtifactType"] = "AmCache_LegacyFile";
                ev.AdditionalFields["RegistryKeyPath"] = SafeKeyPath(item);
                ev.AdditionalFields["LegacyVolumeKey"] = SafeKeyName(volume);
                AddKnownValues(ev, item, new[] { "0", "1", "6", "11", "12", "15", "17", "101" });
                ParserSupport.AddTargetFields(ev, path, "AmCacheLegacyFileKey");
                ParserSupport.AddParseQuality(ev, "Windows AmCache Parser", "Medium", "Legacy AmCache File key extracted; numeric value names preserved.");
                yield return ev;
            }
        }
    }

    private static void AddKnownValues(NormalizedEvent ev, object key, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = GetValueString(key, name);
            if (!string.IsNullOrWhiteSpace(value))
                ev.AdditionalFields[$"AmCache_{name}"] = value;
        }
    }

    private static IEnumerable<object> SafeSubKeys(object key)
    {
        var subKeys = GetProperty(key, "SubKeys");
        if (subKeys is not IEnumerable enumerable) yield break;
        foreach (var subKey in enumerable)
        {
            if (subKey != null) yield return subKey;
        }
    }

    private static IEnumerable<object> SafeValues(object key)
    {
        var values = GetProperty(key, "Values");
        if (values is not IEnumerable enumerable) yield break;
        foreach (var value in enumerable)
        {
            if (value != null) yield return value;
        }
    }

    private static string GetValueString(object key, string valueName)
    {
        foreach (var val in SafeValues(key))
        {
            if (string.Equals(SafeValueName(val), valueName, StringComparison.OrdinalIgnoreCase))
                return GetValueDisplay(val);
        }
        return string.Empty;
    }

    private static string GetValueDisplay(object val)
    {
        try
        {
            var rawObject = GetProperty(val, "ValueData");
            if (rawObject != null) return ParserSupport.Clean(rawObject.ToString());
        }
        catch { }
        return string.Empty;
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

    private static DateTime? TryParseAmCacheDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, out var fileTime))
        {
            var fromFileTime = ParserSupport.FromFileTime(fileTime);
            if (fromFileTime != null) return fromFileTime;
        }
        return TimeUtil.ParseUtc(value);
    }

    private static DateTime FirstValidDate(params DateTime?[] values)
    {
        foreach (var value in values)
        {
            if (value.HasValue && value.Value != DateTime.MinValue)
                return value.Value.Kind == DateTimeKind.Utc ? value.Value : value.Value.ToUniversalTime();
        }
        return DateTime.MinValue;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
