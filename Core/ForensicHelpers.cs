using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace VestigantTriage;

internal static class TimeUtil
{
    public static TimeZoneInfo EnsureTimeZone(string tzName)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tzName))
                return TimeZoneInfo.FindSystemTimeZoneById(tzName);
        }
        catch
        {
            // Fall through to local zone when a copied case is opened on a host with a different zone catalog.
        }

        return TimeZoneInfo.Local;
    }

    public static DateTime? ParseUtc(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr))
            return null;

        var text = dateStr.Trim();
        var candidates = new List<string> { text };
        if (text.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
            candidates.Add(text[..^4].TrimEnd() + "Z");
        if (text.EndsWith(" GMT", StringComparison.OrdinalIgnoreCase))
            candidates.Add(text[..^4].TrimEnd() + "Z");

        string[] exactFormats =
        {
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            "yyyy-MM-dd HH:mm:ss'UTC'",
            "yyyy-MM-dd HH:mm:ss zzz",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
            "yyyy-MM-dd HH:mm:ss",
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy H:mm:ss"
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DateTimeOffset.TryParseExact(candidate, exactFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exactDto))
                return exactDto.UtcDateTime;

            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.UtcDateTime;

            if (DateTime.TryParseExact(candidate, exactFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exactDt))
                return DateTime.SpecifyKind(exactDt, DateTimeKind.Utc).ToUniversalTime();

            if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt.ToUniversalTime();
        }

        return null;
    }

    public static string WinFileTimeLine(string fileTime)
    {
        if (!long.TryParse(fileTime, out long ft))
            return "Invalid Time";

        try { return DateTime.FromFileTimeUtc(ft).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        catch { return "Invalid Time"; }
    }
}

internal static class ForensicText
{
    private static readonly Regex WindowsDriveOrUncPath = new("(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^\\x00\\r\\n\\\"<>|]{2,520}", RegexOptions.Compiled);

    public static string CleanDisplayValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t' || !char.IsControl(ch))
                sb.Append(ch);
        }

        return sb.ToString().Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
    }

    public static bool IsLocalWorkingEvidencePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Replace('/', '\\');
        return normalized.Contains("\\WorkingEvidence\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\\WorkingEvidence", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyGarbledExecutableText(string? value)
    {
        var text = CleanDisplayValue(value);
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (text.IndexOf('\uFFFD') >= 0)
            return true;

        int asciiPrintable = 0;
        int latinLettersOrDigits = 0;
        int nonAsciiLettersOrSymbols = 0;
        int suspiciousCategories = 0;

        foreach (char ch in text)
        {
            if (ch >= 0x20 && ch <= 0x7E)
            {
                asciiPrintable++;
                if (char.IsLetterOrDigit(ch))
                    latinLettersOrDigits++;
                continue;
            }

            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.Control)
            {
                suspiciousCategories++;
                continue;
            }

            if (char.IsLetterOrDigit(ch) || char.IsSymbol(ch) || char.IsPunctuation(ch))
                nonAsciiLettersOrSymbols++;
        }

        if (suspiciousCategories > 0)
            return true;

        // Prefetch executable names on Windows are expected to be short ASCII executable names in this field.
        // Substantial non-ASCII content here is normally decompression corruption or carved-fragment noise.
        if (text.Length >= 4 && asciiPrintable < text.Length * 0.80)
            return true;

        if (nonAsciiLettersOrSymbols > 0 && !LooksLikeWindowsExecutableName(text))
            return true;

        return latinLettersOrDigits == 0;
    }

    public static bool LooksLikeWindowsExecutableName(string? value)
    {
        var text = CleanDisplayValue(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (IsLikelyLocalPathOrUrlNoise(text))
            return false;

        return Regex.IsMatch(text, @"(?i)^[A-Z0-9][A-Z0-9._+\- ()]{0,180}\.(EXE|COM|BAT|CMD|PS1|SCR|MSI)$");
    }

    public static bool LooksLikeActionableUserDataPathOrName(string? value)
    {
        var text = CleanDisplayValue(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (IsLocalWorkingEvidencePath(text))
            return false;

        if (Regex.IsMatch(text, @"(?i)\\(windows|program files|program files \(x86\))\\"))
            return false;

        if (Regex.IsMatch(text, @"(?i)\.(exe|dll|sys|mui|pf|lnk|automaticdestinations-ms|customdestinations-ms)$"))
            return false;

        return true;
    }

    public static string PrefetchNameFromPath(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown Prefetch";

        var artifactMarker = "_Artifact_";
        var artifactIndex = name.LastIndexOf(artifactMarker, StringComparison.OrdinalIgnoreCase);
        if (artifactIndex >= 0)
            name = name[(artifactIndex + artifactMarker.Length)..];
        else
        {
            var match = Regex.Match(name, @"^(?:Live|Ghost)_[0-9a-fA-F]{16,64}_(?:System_)?(.+)$");
            if (match.Success)
                name = match.Groups[1].Value;
        }

        var exeMatch = Regex.Match(name, @"^(.+?\.EXE)-[0-9A-F]{7,10}$", RegexOptions.IgnoreCase);
        if (exeMatch.Success)
            return exeMatch.Groups[1].Value.ToUpperInvariant();

        return CleanDisplayValue(name);
    }

    public static string FirstLikelyPathFromBinary(byte[] bytes, int offset = 0, int? length = null)
    {
        if (bytes.Length == 0 || offset < 0 || offset >= bytes.Length)
            return string.Empty;

        var count = Math.Min(length ?? bytes.Length - offset, bytes.Length - offset);
        foreach (var candidate in ExtractPathCandidates(Encoding.Unicode.GetString(bytes, offset, count)))
            return candidate;

        foreach (var candidate in ExtractPathCandidates(Encoding.ASCII.GetString(bytes, offset, count)))
            return candidate;

        return string.Empty;
    }

    public static IEnumerable<string> ExtractPathCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in WindowsDriveOrUncPath.Matches(text))
        {
            var candidate = TrimBinaryPathTail(CleanDisplayValue(match.Value));
            if (IsLikelyPath(candidate))
                yield return candidate;
        }
    }

    public static string TrimBinaryPathTail(string value)
    {
        var text = CleanDisplayValue(value);
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        int end = text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < 0x20 || ch == '"' || ch == '<' || ch == '>' || ch == '|')
            {
                end = i;
                break;
            }
        }

        text = text[..end].Trim().TrimEnd('.', ' ');

        // Stop after a known extension when binary residue was captured after the path.
        var knownExt = Regex.Match(text, @"(?i)^(.+?\.(?:docx?|xlsx?|pptx?|pdf|txt|csv|tsv|zip|7z|rar|tar|gz|jpg|jpeg|png|gif|bmp|tif|tiff|pst|ost|msg|eml|lnk|url|exe|bat|cmd|ps1|vbs|js|msi|rdp|dwg|sql|bak|db|sqlite))(?:[^A-Z0-9._\-\\/ ].*)?$", RegexOptions.IgnoreCase);
        if (knownExt.Success)
            return knownExt.Groups[1].Value.TrimEnd('.', ' ');

        return text;
    }

    public static bool IsLikelyPath(string? value)
    {
        var text = CleanDisplayValue(value);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (IsLikelyLocalPathOrUrlNoise(text))
            return false;

        if (!(Regex.IsMatch(text, @"(?i)^[A-Z]:\\") || text.StartsWith("\\\\", StringComparison.Ordinal)))
            return false;

        return text.Count(ch => ch == '\\') >= 1 && text.Length <= 520;
    }

    private static bool IsLikelyLocalPathOrUrlNoise(string text)
    {
        if (text.IndexOf('\uFFFD') >= 0)
            return true;

        int asciiPrintable = text.Count(ch => ch >= 0x20 && ch <= 0x7E);
        return text.Length >= 6 && asciiPrintable < text.Length * 0.80;
    }
}

internal static class ShellLinkTargetExtractor
{
    public static string TryExtractTargetPath(byte[] bytes, int offset = 0)
    {
        try
        {
            if (bytes.Length - offset < 0x4C)
                return string.Empty;

            using var ms = new MemoryStream(bytes, offset, bytes.Length - offset);
            using var br = new BinaryReader(ms);
            if (br.ReadUInt32() != 0x0000004C)
                return string.Empty;

            ms.Seek(0x14, SeekOrigin.Begin);
            uint linkFlags = br.ReadUInt32();
            bool hasTargetIdList = (linkFlags & 0x01) != 0;
            bool hasLinkInfo = (linkFlags & 0x02) != 0;

            ms.Seek(0x4C, SeekOrigin.Begin);
            if (hasTargetIdList)
            {
                if (ms.Position + 2 > ms.Length) return string.Empty;
                ushort idListSize = br.ReadUInt16();
                if (ms.Position + idListSize > ms.Length) return string.Empty;
                ms.Seek(idListSize, SeekOrigin.Current);
            }

            if (!hasLinkInfo || ms.Position + 0x1C > ms.Length)
                return ForensicText.FirstLikelyPathFromBinary(bytes, offset, Math.Min(4096, bytes.Length - offset));

            long linkInfoStart = ms.Position;
            uint linkInfoSize = br.ReadUInt32();
            if (linkInfoSize < 0x1C || linkInfoStart + linkInfoSize > ms.Length)
                return ForensicText.FirstLikelyPathFromBinary(bytes, offset, Math.Min(4096, bytes.Length - offset));

            uint linkInfoHeaderSize = br.ReadUInt32();
            uint linkInfoFlags = br.ReadUInt32();
            uint volumeIdOffset = br.ReadUInt32();
            uint localBasePathOffset = br.ReadUInt32();
            uint commonNetworkRelativeLinkOffset = br.ReadUInt32();
            uint commonPathSuffixOffset = br.ReadUInt32();
            uint localBasePathOffsetUnicode = 0;
            uint commonPathSuffixOffsetUnicode = 0;

            if (linkInfoHeaderSize >= 0x24 && linkInfoStart + 0x24 <= ms.Length)
            {
                localBasePathOffsetUnicode = br.ReadUInt32();
                commonPathSuffixOffsetUnicode = br.ReadUInt32();
            }

            string localBasePath = ReadLinkInfoString(bytes, offset, linkInfoStart, linkInfoSize, localBasePathOffsetUnicode, unicode: true);
            if (string.IsNullOrWhiteSpace(localBasePath))
                localBasePath = ReadLinkInfoString(bytes, offset, linkInfoStart, linkInfoSize, localBasePathOffset, unicode: false);

            string commonPathSuffix = ReadLinkInfoString(bytes, offset, linkInfoStart, linkInfoSize, commonPathSuffixOffsetUnicode, unicode: true);
            if (string.IsNullOrWhiteSpace(commonPathSuffix))
                commonPathSuffix = ReadLinkInfoString(bytes, offset, linkInfoStart, linkInfoSize, commonPathSuffixOffset, unicode: false);

            string networkBasePath = string.Empty;
            if ((linkInfoFlags & 0x02) != 0 && commonNetworkRelativeLinkOffset > 0)
                networkBasePath = ReadCommonNetworkRelativeLink(bytes, offset, linkInfoStart, linkInfoSize, commonNetworkRelativeLinkOffset);

            var combined = CombineTargetParts(!string.IsNullOrWhiteSpace(networkBasePath) ? networkBasePath : localBasePath, commonPathSuffix);
            combined = ForensicText.TrimBinaryPathTail(combined);
            if (ForensicText.IsLikelyPath(combined))
                return combined;

            if (ForensicText.IsLikelyPath(localBasePath))
                return ForensicText.TrimBinaryPathTail(localBasePath);

            var fallback = ForensicText.FirstLikelyPathFromBinary(bytes, offset, Math.Min(4096, bytes.Length - offset));
            return ForensicText.IsLikelyPath(fallback) ? fallback : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadCommonNetworkRelativeLink(byte[] outerBytes, int outerOffset, long linkInfoStart, uint linkInfoSize, uint cnrlOffset)
    {
        try
        {
            long absolute = outerOffset + linkInfoStart + cnrlOffset;
            if (absolute < 0 || absolute + 16 > outerBytes.Length || cnrlOffset >= linkInfoSize)
                return string.Empty;

            using var ms = new MemoryStream(outerBytes);
            using var br = new BinaryReader(ms);
            ms.Seek(absolute, SeekOrigin.Begin);
            uint size = br.ReadUInt32();
            if (size < 0x14 || absolute + size > outerBytes.Length)
                return string.Empty;

            uint flags = br.ReadUInt32();
            uint netNameOffset = br.ReadUInt32();
            br.ReadUInt32(); // device name offset
            br.ReadUInt32(); // provider type
            uint netNameOffsetUnicode = 0;
            if (size >= 0x1C && absolute + 0x1C <= outerBytes.Length)
            {
                netNameOffsetUnicode = br.ReadUInt32();
            }

            string netName = ReadStringAt(outerBytes, (int)(absolute + netNameOffsetUnicode), (int)(size - netNameOffsetUnicode), unicode: true);
            if (string.IsNullOrWhiteSpace(netName))
                netName = ReadStringAt(outerBytes, (int)(absolute + netNameOffset), (int)(size - netNameOffset), unicode: false);

            return ForensicText.TrimBinaryPathTail(netName);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadLinkInfoString(byte[] outerBytes, int outerOffset, long linkInfoStart, uint linkInfoSize, uint stringOffset, bool unicode)
    {
        if (stringOffset == 0 || stringOffset >= linkInfoSize)
            return string.Empty;

        int absolute = checked((int)(outerOffset + linkInfoStart + stringOffset));
        int maxBytes = checked((int)(linkInfoSize - stringOffset));
        return ForensicText.TrimBinaryPathTail(ReadStringAt(outerBytes, absolute, maxBytes, unicode));
    }

    private static string ReadStringAt(byte[] bytes, int absoluteOffset, int maxBytes, bool unicode)
    {
        if (absoluteOffset < 0 || absoluteOffset >= bytes.Length || maxBytes <= 0)
            return string.Empty;

        maxBytes = Math.Min(maxBytes, bytes.Length - absoluteOffset);
        if (unicode)
        {
            if (maxBytes < 2) return string.Empty;
            if ((maxBytes % 2) != 0) maxBytes--;
            int end = absoluteOffset;
            while (end + 1 < absoluteOffset + maxBytes)
            {
                if (bytes[end] == 0 && bytes[end + 1] == 0)
                    break;
                end += 2;
            }

            if (end <= absoluteOffset) return string.Empty;
            return ForensicText.CleanDisplayValue(Encoding.Unicode.GetString(bytes, absoluteOffset, end - absoluteOffset));
        }
        else
        {
            int end = absoluteOffset;
            while (end < absoluteOffset + maxBytes && bytes[end] != 0)
                end++;

            if (end <= absoluteOffset) return string.Empty;
            return ForensicText.CleanDisplayValue(Encoding.Default.GetString(bytes, absoluteOffset, end - absoluteOffset));
        }
    }

    private static string CombineTargetParts(string basePath, string suffix)
    {
        basePath = ForensicText.TrimBinaryPathTail(basePath);
        suffix = ForensicText.TrimBinaryPathTail(suffix);

        if (string.IsNullOrWhiteSpace(basePath))
            return suffix;
        if (string.IsNullOrWhiteSpace(suffix))
            return basePath;
        if (suffix.StartsWith("\\", StringComparison.Ordinal) || Regex.IsMatch(suffix, @"(?i)^[A-Z]:\\"))
            return suffix;
        if (basePath.EndsWith("\\", StringComparison.Ordinal))
            return basePath + suffix;
        return basePath + "\\" + suffix;
    }
}

internal static class CsvUtil
{
    public static IEnumerable<Dictionary<string, string>> ReadRows(string filePath)
    {
        if (!File.Exists(filePath))
            yield break;

        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headers = ReadCsvRecord(reader);
        if (headers == null || headers.Count == 0)
            yield break;

        for (int i = 0; i < headers.Count; i++)
            headers[i] = (headers[i] ?? string.Empty).Trim().TrimStart('\uFEFF');

        while (true)
        {
            var fields = ReadCsvRecord(reader);
            if (fields == null)
                yield break;

            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
                continue;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
                dict[headers[i]] = i < fields.Count ? fields[i] ?? string.Empty : string.Empty;

            yield return dict;
        }
    }

    private static List<string>? ReadCsvRecord(StreamReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool sawAnyCharacter = false;

        while (true)
        {
            int raw = reader.Read();
            if (raw < 0)
            {
                if (!sawAnyCharacter && field.Length == 0 && fields.Count == 0)
                    return null;

                fields.Add(field.ToString());
                return fields;
            }

            sawAnyCharacter = true;
            char ch = (char)raw;

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
                continue;
            }

            if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (ch == '\r')
            {
                if (reader.Peek() == '\n')
                    reader.Read();

                fields.Add(field.ToString());
                return fields;
            }

            if (ch == '\n')
            {
                fields.Add(field.ToString());
                return fields;
            }

            field.Append(ch);
        }
    }
}

internal static class JsonRepair
{
    public static Dictionary<string, string> TryJsonLoads(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var candidate in BuildCandidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true });
                FlattenElement(doc.RootElement, "", result);
                return result;
            }
            catch
            {
                // Try the next repair candidate.
            }
        }

        return result;
    }

    public static Dictionary<string, string> Flatten(Dictionary<string, string> data) => data;

    public static string Clean(string raw) => raw?.Trim() ?? "";

    private static IEnumerable<string> BuildCandidates(string raw)
    {
        string trimmed = raw.Trim();
        yield return trimmed;

        if (trimmed.Length >= 2 && trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal))
        {
            string? unescaped = null;
            try { unescaped = JsonSerializer.Deserialize<string>(trimmed); }
            catch { /* ignored */ }

            if (!string.IsNullOrWhiteSpace(unescaped))
                yield return unescaped.Trim();
        }

        string braceCandidate = trimmed.Replace("\"\"", "\"");
        if (!string.Equals(braceCandidate, trimmed, StringComparison.Ordinal))
            yield return braceCandidate;
    }

    private static void FlattenElement(JsonElement element, string prefix, Dictionary<string, string> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrWhiteSpace(prefix)
                        ? property.Name
                        : $"{prefix}_{property.Name}";

                    FlattenElement(property.Value, key, output);
                }
                break;

            case JsonValueKind.Array:
                var scalarValues = new List<string>();
                int index = 0;

                foreach (var item in element.EnumerateArray())
                {
                    if (IsScalar(item))
                    {
                        scalarValues.Add(ScalarToString(item));
                    }
                    else
                    {
                        FlattenElement(item, $"{prefix}_{index}", output);
                    }

                    index++;
                }

                if (scalarValues.Count > 0 && !string.IsNullOrWhiteSpace(prefix))
                    output[prefix] = string.Join("; ", scalarValues);
                break;

            default:
                if (!string.IsNullOrWhiteSpace(prefix))
                    output[prefix] = ScalarToString(element);
                break;
        }
    }

    private static bool IsScalar(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
    }

    private static string ScalarToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };
    }
}

internal static class Prompt
{
    public static string ShowChoice(Form parent, string title, string prompt, string[] choices)
    {
        using Form f = new Form { Width = 420, Height = 200, Text = title, StartPosition = FormStartPosition.CenterParent };
        Label lbl = new Label { Left = 20, Top = 20, Text = prompt, AutoSize = true };
        ComboBox cb = new ComboBox { Left = 20, Top = 50, Width = 360, DataSource = choices, DropDownStyle = ComboBoxStyle.DropDownList };
        Button btn = new Button { Text = "OK", Left = 305, Top = 100, Width = 75, DialogResult = DialogResult.OK };
        f.AcceptButton = btn;
        f.Controls.AddRange(new Control[] { lbl, cb, btn });
        return f.ShowDialog(parent) == DialogResult.OK ? cb.SelectedItem?.ToString() ?? "" : "";
    }
}

internal static class OfficeArtifacts
{
    public static string ParseOwnerFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return "File Missing";
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 4) return "Corrupt Fragment";

            // Office owner/lock files are small binary records, but stale/corrupt
            // records can contain ZIP/header bytes or binary residue after the owner
            // string. Keep only plausible short owner-name candidates.
            var candidates = new List<string>();

            int nameLen = bytes.Length > 1 ? bytes[1] : 0;
            if (nameLen > 0 && nameLen <= bytes.Length - 2)
                candidates.Add(Encoding.ASCII.GetString(bytes, 2, Math.Min(nameLen, bytes.Length - 2)));

            for (int offset = 0; offset < Math.Min(bytes.Length, 96); offset += 2)
            {
                int len = Math.Min(bytes.Length - offset, 96);
                if (len >= 4)
                    candidates.Add(Encoding.Unicode.GetString(bytes, offset, len));
            }

            candidates.Add(Encoding.ASCII.GetString(bytes));
            candidates.Add(Encoding.UTF8.GetString(bytes));

            var best = candidates
                .Select(CleanOwnerCandidate)
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => new { Value = candidate, Score = OwnerCandidateScore(candidate) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Value.Length)
                .FirstOrDefault();

            return best?.Value ?? "Unknown";
        }
        catch
        {
            return "Error Parsing";
        }
    }

    private static string CleanOwnerCandidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw.Normalize(NormalizationForm.FormKC)
            .Replace('\0', ' ')
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        foreach (var marker in new[] { " PK", "PK\u0003", "PK\u0005", "PK\u0007", "<?xml", "{\\rtf", "\uFFFD" })
        {
            var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                normalized = normalized[..idx];
        }

        var sb = new StringBuilder();
        var lastWasSpace = false;
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            var unsafeChar = category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned;

            if (unsafeChar || ch == '\uFFFD')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '\'')
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }

            if (sb.Length >= 64)
                break;
        }

        var cleaned = sb.ToString().Trim(' ', '\0', '\t', '\r', '\n', '.', '-', '_');
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);

        // Many stale owner files contain the owner name followed by a UTF-16 echo
        // such as "MAHER Tim M A H E R T i m". Keep the normal prefix.
        var spacedEcho = System.Text.RegularExpressions.Regex.Match(cleaned, @"^(.+?)\s+(?:[A-Za-z]\s+){3,}[A-Za-z](?:\s|$)");
        if (spacedEcho.Success)
            cleaned = spacedEcho.Groups[1].Value.Trim();

        if (cleaned.Length > 40)
            cleaned = cleaned[..40].Trim();

        return cleaned;
    }

    private static int OwnerCandidateScore(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return 0;
        if (candidate.Length < 2 || candidate.Length > 40) return 0;

        int letters = candidate.Count(char.IsLetter);
        int digits = candidate.Count(char.IsDigit);
        int spaces = candidate.Count(char.IsWhiteSpace);
        int safePunctuation = candidate.Count(ch => ch == '-' || ch == '_' || ch == '.' || ch == '\'');
        int other = candidate.Length - letters - digits - spaces - safePunctuation;
        if (letters == 0) return 0;
        if (other > 0) return 0;

        int score = letters * 4 + digits + Math.Min(spaces, 3) * 2 + safePunctuation;
        if (candidate.Contains(' ')) score += 6;
        if (candidate.Any(char.IsLower) && candidate.Any(char.IsUpper)) score += 4;
        if (candidate.Length <= 24) score += 5;
        return score;
    }
}

