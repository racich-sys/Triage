using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Registry;

namespace VestigantTriage;

public sealed class OfficeActivityParser : IArtifactParser
{
    private static readonly Regex UrlRegex = new("https?://[^\\s\\\"'<>|{}]{4,1000}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxEventsPerHive = 10000;

    public string ParserName => "Microsoft Office Activity Parser";

    public bool CanParse(string filePath)
    {
        var name = (Path.GetFileName(filePath) ?? string.Empty).ToUpperInvariant();
        return name.Equals("NTUSER.DAT", StringComparison.Ordinal) ||
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
            var office = hive.GetKey(@"Software\Microsoft\Office");
            if (office == null)
                return events;

            foreach (var ev in TraverseOfficeKeys(office, user, filePath))
            {
                events.Add(ev);
                if (events.Count >= MaxEventsPerHive)
                {
                    log($"Office activity parser reached {MaxEventsPerHive:N0} events for {Path.GetFileName(filePath)}; remaining registry values skipped.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            log($"Office activity parser failed for {Path.GetFileName(filePath)}: {ex.Message}");
            var ev = new NormalizedEvent
            {
                DataSource = "Office_Activity",
                Operation = "OfficeActivity_ParseError",
                ObjectPath = filePath,
                UserId = ParserSupport.ExtractUserFromPath(filePath, "LocalUser"),
                TimestampUtc = DateTime.MinValue
            };
            ev.AdditionalFields["EventCategory"] = "ParserError";
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Exception during Office registry parsing.");
            events.Add(ev);
        }

        return events;
    }

    private IEnumerable<NormalizedEvent> TraverseOfficeKeys(object root, string user, string sourceFile)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        var visited = 0;

        while (stack.Count > 0 && visited < 25000)
        {
            var key = stack.Pop();
            visited++;
            var keyPath = RegistryParseSupport.SafeKeyPath(key);
            var keyName = RegistryParseSupport.SafeKeyName(key);
            var lower = keyPath.ToLowerInvariant();
            var relevantKey = IsRelevantOfficeKey(lower, keyName);

            if (relevantKey)
            {
                foreach (var value in SafeValues(key))
                {
                    var raw = RegistryParseSupport.GetValueDisplay(value);
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var targets = ExtractTargets(raw).Take(10).ToList();
                    if (targets.Count == 0 && LooksLikeCloudOrMruMetadata(raw))
                        targets.Add(raw.Length > 500 ? raw[..500] : raw);

                    foreach (var target in targets)
                    {
                        var ev = MakeOfficeEvent(key, value, target, raw, user, sourceFile);
                        yield return ev;
                    }
                }
            }

            foreach (var sub in SafeSubKeys(key))
                stack.Push(sub);
        }
    }

    private static NormalizedEvent MakeOfficeEvent(object key, object value, string target, string raw, string user, string sourceFile)
    {
        var keyPath = RegistryParseSupport.SafeKeyPath(key);
        var valueName = RegistryParseSupport.SafeValueName(value);
        var kind = ClassifyOfficeKey(keyPath, valueName, raw);
        var ts = RegistryParseSupport.LastWriteUtc(key);
        var ev = new NormalizedEvent
        {
            DataSource = "Office_Activity",
            Operation = kind.Operation,
            ObjectPath = ParserSupport.Clean(target),
            UserId = user,
            TimestampUtc = ts
        };
        ev.AdditionalFields["EventCategory"] = kind.Category;
        ev.AdditionalFields["ArtifactType"] = kind.ArtifactType;
        ev.AdditionalFields["RegistryKeyPath"] = keyPath;
        ev.AdditionalFields["RegistryValueName"] = valueName;
        ev.AdditionalFields["RegistryTimestampBasis"] = "KeyLastWrite";
        ev.AdditionalFields["OfficeVersion"] = ExtractOfficeVersion(keyPath);
        ev.AdditionalFields["OfficeApplication"] = ExtractOfficeApplication(keyPath);
        ev.AdditionalFields["OfficeActivitySourceFile"] = sourceFile;
        ev.AdditionalFields["OfficeRawValue"] = raw.Length > 4000 ? raw[..4000] : raw;
        ev.AdditionalFields["IsCloudDocument"] = IsCloudTarget(target).ToString();
        var domain = ParserSupport.InferCloudOrWebDomain(target);
        if (!string.IsNullOrWhiteSpace(domain)) ev.AdditionalFields["Domain"] = domain;
        if (ForensicText.IsLikelyPath(target) || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ParserSupport.AddTargetFields(ev, target, kind.ArtifactType);
        ParserSupport.AddParseQuality(ev, "Microsoft Office Activity Parser", "High", "Office registry MRU/Backstage/recent-document value extracted with registry key last-write timestamp.");
        return ev;
    }

    private static bool IsRelevantOfficeKey(string lowerKeyPath, string keyName)
    {
        if (!lowerKeyPath.Contains("\\microsoft\\office", StringComparison.Ordinal)) return false;
        return lowerKeyPath.Contains("mru", StringComparison.Ordinal) ||
               lowerKeyPath.Contains("backstage", StringComparison.Ordinal) ||
               lowerKeyPath.Contains("recent", StringComparison.Ordinal) ||
               lowerKeyPath.Contains("place", StringComparison.Ordinal) ||
               lowerKeyPath.Contains("file name", StringComparison.Ordinal) ||
               lowerKeyPath.Contains("open find", StringComparison.Ordinal) ||
               lowerKeyPath.Contains("common\\internet", StringComparison.Ordinal) ||
               keyName.Contains("MRU", StringComparison.OrdinalIgnoreCase) ||
               keyName.Contains("Backstage", StringComparison.OrdinalIgnoreCase) ||
               keyName.Contains("Recent", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Operation, string Category, string ArtifactType) ClassifyOfficeKey(string keyPath, string valueName, string raw)
    {
        var text = $"{keyPath} {valueName} {raw}";
        if (text.Contains("Backstage", StringComparison.OrdinalIgnoreCase)) return ("Office_Backstage_Item", "DocumentActivity", "OfficeBackstage");
        if (text.Contains("Place MRU", StringComparison.OrdinalIgnoreCase) || text.Contains("Place", StringComparison.OrdinalIgnoreCase)) return ("Office_Place_MRU", "FolderAccess", "OfficePlaceMRU");
        if (text.Contains("User MRU", StringComparison.OrdinalIgnoreCase)) return ("Office_User_MRU", "DocumentActivity", "OfficeUserMRU");
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase)) return ("Office_Cloud_Document_MRU", "CloudDocumentActivity", "OfficeCloudMRU");
        if (text.Contains("Recent", StringComparison.OrdinalIgnoreCase)) return ("Office_Recent_Item", "DocumentActivity", "OfficeRecent");
        return ("Office_Document_MRU", "DocumentActivity", "OfficeFileMRU");
    }

    private static IEnumerable<string> ExtractTargets(string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in UrlRegex.Matches(raw))
        {
            var url = ParserSupport.Clean(m.Value.TrimEnd(';', ',', ')', ']'));
            if (seen.Add(url)) yield return url;
        }

        foreach (var path in ForensicText.ExtractPathCandidates(raw))
        {
            if (seen.Add(path)) yield return path;
        }

        foreach (var part in raw.Split(new[] { '*', '|', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = ParserSupport.Clean(part);
            if (clean.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = FileUriToPath(clean);
                if (!string.IsNullOrWhiteSpace(filePath) && seen.Add(filePath)) yield return filePath;
            }
            else if ((clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || ForensicText.IsLikelyPath(clean)) && seen.Add(clean))
            {
                yield return clean;
            }
        }
    }

    private static bool LooksLikeCloudOrMruMetadata(string raw) => raw.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) || raw.Contains("SharePoint", StringComparison.OrdinalIgnoreCase) || raw.Contains("d.docs.live.net", StringComparison.OrdinalIgnoreCase);
    private static bool IsCloudTarget(string target) => target.Contains("sharepoint", StringComparison.OrdinalIgnoreCase) || target.Contains("onedrive", StringComparison.OrdinalIgnoreCase) || target.Contains("docs.live.net", StringComparison.OrdinalIgnoreCase) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string ExtractOfficeVersion(string keyPath)
    {
        var m = Regex.Match(keyPath, @"Office\\([^\\]+)\\", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static string ExtractOfficeApplication(string keyPath)
    {
        var m = Regex.Match(keyPath, @"Office\\[^\\]+\\([^\\]+)\\", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static string FileUriToPath(string uri)
    {
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
                return Uri.UnescapeDataString(parsed.LocalPath);
        }
        catch { }
        return string.Empty;
    }

    private static IEnumerable<object> SafeSubKeys(object key)
    {
        try
        {
            if (RegistryParseSupport.GetProperty(key, "SubKeys") is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    if (item != null) yield return item;
            }
        }
        finally { }
    }

    private static IEnumerable<object> SafeValues(object key)
    {
        try
        {
            if (RegistryParseSupport.GetProperty(key, "Values") is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    if (item != null) yield return item;
            }
        }
        finally { }
    }
}
