using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VestigantTriage;

internal static class ParserSupport
{
    private static readonly Regex UserPathRegex = new(@"(?i)(?:^|[\\/])Users[\\/]([^\\/]+)[\\/]", RegexOptions.Compiled);
    private static readonly Regex SidRegex = new(@"S-1-5-21-[0-9\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);


    public static bool HasPathSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment))
            return false;

        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool HasPathSequence(string? path, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(path) || segments == null || segments.Length == 0)
            return false;

        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i <= parts.Length - segments.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < segments.Length; j++)
            {
                if (!parts[i + j].Equals(segments[j], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }

    public static string ExtractUserFromPath(string? path, string fallback = "Unknown")
    {
        if (string.IsNullOrWhiteSpace(path))
            return fallback;

        var m = UserPathRegex.Match(path);
        if (m.Success)
        {
            var user = Clean(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(user) && !user.Equals("Default", StringComparison.OrdinalIgnoreCase) && !user.Equals("Public", StringComparison.OrdinalIgnoreCase))
                return user;
        }

        var sid = ExtractSid(path);
        if (!string.IsNullOrWhiteSpace(sid))
            return sid;

        return fallback;
    }

    public static string ExtractSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var m = SidRegex.Match(value);
        return m.Success ? m.Value : string.Empty;
    }

    public static string Clean(string? value) => ForensicText.CleanDisplayValue(value);

    public static string SafeFileName(string? value)
    {
        value = Clean(value);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.LocalPath))
                return Path.GetFileName(uri.LocalPath.TrimEnd('/', '\\'));

            return Path.GetFileName(value.TrimEnd('/', '\\'));
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string SafeExtension(string? value)
    {
        try { return Path.GetExtension(SafeFileName(value)).TrimStart('.').ToLowerInvariant(); }
        catch { return string.Empty; }
    }

    public static string DetermineDriveType(string? path)
    {
        path = Clean(path);
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
            return "Network";
        if (Regex.IsMatch(path, @"(?i)^[A-Z]:\\"))
            return path.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ? "Local" : "Removable/Secondary";
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Web/Cloud";
        return string.Empty;
    }

    public static string ClassifyPathOrUrl(string? value)
    {
        value = Clean(value);
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Url";
        if (value.StartsWith("\\\\", StringComparison.Ordinal)) return "UncPath";
        if (Regex.IsMatch(value, @"(?i)^[A-Z]:\\")) return "LocalOrRemovablePath";
        return "NameOnly";
    }

    public static void AddTargetFields(NormalizedEvent ev, string? target, string source = "")
    {
        target = ForensicText.TrimBinaryPathTail(Clean(target));
        if (string.IsNullOrWhiteSpace(target))
            return;

        ev.ObjectPath = target;
        ev.AdditionalFields["DisplayTarget"] = target;
        ev.AdditionalFields["TargetPath"] = target;
        ev.AdditionalFields["TargetSource"] = source;
        ev.AdditionalFields["FileName"] = SafeFileName(target);
        ev.AdditionalFields["FileExtension"] = SafeExtension(target);
        ev.AdditionalFields["PathType"] = ClassifyPathOrUrl(target);
        var driveType = DetermineDriveType(target);
        if (!string.IsNullOrWhiteSpace(driveType)) ev.AdditionalFields["DriveType"] = driveType;
    }

    public static void AddParseQuality(NormalizedEvent ev, string parserName, string confidence, string basis)
    {
        ev.AdditionalFields["ParserName"] = parserName;
        ev.AdditionalFields["ParserConfidence"] = confidence;
        ev.AdditionalFields["ParserConfidenceBasis"] = basis;
    }

    public static void SetEventTime(NormalizedEvent ev, DateTime? timestampUtc, string basis, string confidence, bool isBehavioral, string warning = "")
    {
        TimestampProvenance.ApplyToEvent(ev, timestampUtc, basis, confidence, isBehavioral, warning);
    }

    public static DateTime? FromFileTime(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            var dt = DateTime.FromFileTimeUtc(fileTime);
            if (dt.Year < 1980 || dt.Year > DateTime.UtcNow.Year + 2) return null;
            return dt;
        }
        catch { return null; }
    }

    public static string FileTimeToString(long fileTime) => FromFileTime(fileTime)?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string DecodeFileAttributes(uint attrs)
    {
        var flags = new List<string>();
        void Add(uint mask, string label) { if ((attrs & mask) != 0) flags.Add(label); }
        Add(0x00000001, "ReadOnly");
        Add(0x00000002, "Hidden");
        Add(0x00000004, "System");
        Add(0x00000010, "Directory");
        Add(0x00000020, "Archive");
        Add(0x00000080, "Normal");
        Add(0x00000100, "Temporary");
        Add(0x00000400, "ReparsePoint");
        Add(0x00001000, "Offline");
        Add(0x00004000, "Encrypted");
        return flags.Count == 0 ? "None" : string.Join(", ", flags);
    }

    public static string DecodeShowCommand(uint cmd) => cmd switch
    {
        1 => "Normal",
        3 => "Maximized",
        7 => "Minimized",
        _ => $"Unknown ({cmd})"
    };

    public static string ReadUnicodeNullTerminated(byte[] data, int offset, int maxBytes)
    {
        if (offset < 0 || offset >= data.Length || maxBytes <= 0)
            return string.Empty;

        var available = Math.Min(maxBytes, data.Length - offset);
        if (available < 2) return string.Empty;
        if ((available % 2) != 0) available--;

        int end = offset;
        while (end + 1 < offset + available)
        {
            if (data[end] == 0 && data[end + 1] == 0) break;
            end += 2;
        }

        if (end <= offset) return string.Empty;
        return Clean(Encoding.Unicode.GetString(data, offset, end - offset));
    }

    public static string ReadAnsiNullTerminated(byte[] data, int offset, int maxBytes)
    {
        if (offset < 0 || offset >= data.Length || maxBytes <= 0)
            return string.Empty;

        var available = Math.Min(maxBytes, data.Length - offset);
        int end = offset;
        while (end < offset + available && data[end] != 0) end++;
        if (end <= offset) return string.Empty;
        return Clean(Encoding.Default.GetString(data, offset, end - offset));
    }

    public static IEnumerable<string> ExtractUsefulPathCandidates(byte[] bytes, int maxCount = 50)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ForensicText.ExtractPathCandidates(Encoding.Unicode.GetString(bytes)).Concat(ForensicText.ExtractPathCandidates(Encoding.Default.GetString(bytes))))
        {
            var c = ForensicText.TrimBinaryPathTail(candidate);
            if (string.IsNullOrWhiteSpace(c) || c.Length < 4 || seen.Contains(c)) continue;
            seen.Add(c);
            yield return c;
            if (seen.Count >= maxCount) yield break;
        }
    }

    public static string InferCloudOrWebDomain(string? urlOrPath)
    {
        urlOrPath = Clean(urlOrPath);
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
            return uri.Host.ToLowerInvariant();
        return string.Empty;
    }

    public static void AddInternetClassificationFields(NormalizedEvent ev, string? urlOrPath, string sourceFieldName)
    {
        InternetArtifactClassifier.AddFields(ev, urlOrPath, sourceFieldName);
    }
}
