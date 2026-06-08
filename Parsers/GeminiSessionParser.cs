using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VestigantTriage;

public class GeminiSessionParser : IArtifactParser
{
    public string ParserName => "Gemini Session Archive";

    public bool CanParse(string filePath)
    {
        var lower = filePath.Replace('\\', '/').ToLowerInvariant();
        if (GoogleSourceSupport.IsZip(filePath))
            return lower.Contains("gemini") || GoogleSourceSupport.ZipContains(filePath, IsGeminiEntry);
        return IsGeminiEntry(filePath);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        if (GoogleSourceSupport.IsZip(filePath))
        {
            using var archive = ZipFile.OpenRead(filePath);
            foreach (var entry in archive.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name) && IsGeminiEntry(e.FullName)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.FullName.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    yield return BuildTextEvent(filePath, entry.FullName, entry.Length, entry.LastWriteTime.UtcDateTime, reader.ReadToEnd());
                }
                else
                {
                    yield return BuildEvent(filePath, entry.FullName, entry.Length, entry.LastWriteTime.UtcDateTime);
                }
            }
            yield break;
        }

        var fi = new FileInfo(filePath);
        var textExt = filePath.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        if (textExt && fi.Exists)
            yield return BuildTextEvent(filePath, Path.GetFileName(filePath), fi.Length, fi.LastWriteTimeUtc, File.ReadAllText(filePath));
        else
            yield return BuildEvent(filePath, Path.GetFileName(filePath), fi.Exists ? fi.Length : 0, fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue);
    }

    private static bool IsGeminiEntry(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (!p.Contains("gemini")) return false;
        return p.Contains("transcript") || p.Contains("code extract") || p.Contains("output pdf") || p.Contains("screenshot") || p.EndsWith(".rtf") || p.EndsWith(".rtfd") || p.EndsWith(".py") || p.EndsWith(".pdf") || p.EndsWith(".png") || p.EndsWith(".jpg") || p.EndsWith(".jpeg") || p.EndsWith(".txt");
    }

    private static NormalizedEvent BuildTextEvent(string container, string entry, long bytes, DateTime lastWriteUtc, string text)
    {
        var ev = BuildEvent(container, entry, bytes, lastWriteUtc);
        var cleaned = entry.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) ? RtfToPlainText(text) : GoogleSourceSupport.Clean(text);
        ev.Operation = "GeminiSession_" + Classify(entry) + "_TextExtracted";
        ev.AdditionalFields["GoogleGeminiExtractedTextAvailable"] = "Yes";
        ev.AdditionalFields["GoogleGeminiExtractedTextPreview"] = Truncate(cleaned, 4000);
        ev.AdditionalFields["GoogleGeminiExtractedTextLength"] = cleaned.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ev.AdditionalFields["GoogleGeminiContentRiskTerms"] = BuildRiskTermSummary(cleaned);
        return ev;
    }

    private static NormalizedEvent BuildEvent(string container, string entry, long bytes, DateTime lastWriteUtc)
    {
        var kind = Classify(entry);
        var ev = new NormalizedEvent
        {
            DataSource = "Gemini Session Archive",
            UserId = "Unknown",
            Operation = "GeminiSession_" + kind + "_Observed",
            ObjectPath = entry,
            TimestampUtc = lastWriteUtc == DateTime.MinValue ? DateTime.MinValue : lastWriteUtc,
            EventTimeBasis = "GeminiSessionArchiveEntryLastWrite",
            EventTimeConfidence = "MetadataOnly",
            IsBehavioralTimestamp = false,
            TimestampWarning = "Gemini session file/archive timestamp is metadata-only unless correlated with Google Workspace Gemini audit or endpoint artifacts."
        };
        ev.AdditionalFields["ParserName"] = "Gemini Session Archive";
        ev.AdditionalFields["ArtifactType"] = "Gemini Session Artifact";
        GoogleSourceSupport.AddGoogleCoreFields(ev, "GoogleGeminiSessionArtifact", "GeminiSession", ev.UserId, string.Empty, string.Empty, ev.Operation, ev.Operation, entry, entry, string.Empty, entry, container, 0);
        ev.AdditionalFields["GoogleWorkload"] = "Google Gemini";
        ev.AdditionalFields["GoogleCategory"] = "AIArtifact";
        ev.AdditionalFields["GoogleGeminiArtifactKind"] = kind;
        ev.AdditionalFields["GeminiArtifactKind"] = kind;
        ev.AdditionalFields["GoogleGeminiSourceEntry"] = entry;
        ev.AdditionalFields["GoogleGeminiContainer"] = container;
        ev.AdditionalFields["GoogleFileName"] = Path.GetFileName(entry);
        ev.AdditionalFields["GoogleFileSizeBytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ev.AdditionalFields["GoogleRiskAiPotential"] = "Yes";
        ev.AdditionalFields["GoogleRiskReason"] = "Gemini AI session artifact retained for AI-use review and correlation.";
        return ev;
    }

    private static string Classify(string entry)
    {
        var p = entry.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("transcript")) return "Transcript";
        if (p.Contains("code extract") || p.EndsWith(".py") || p.EndsWith(".js") || p.EndsWith(".cs") || p.EndsWith(".java")) return "CodeExtract";
        if (p.Contains("output pdf") || p.EndsWith(".pdf")) return "OutputDocument";
        if (p.Contains("screenshot") || p.EndsWith(".png") || p.EndsWith(".jpg") || p.EndsWith(".jpeg")) return "Screenshot";
        return "Artifact";
    }

    private static string RtfToPlainText(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return string.Empty;
        var text = Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}", " ");
        text = Regex.Replace(text, @"\\[a-zA-Z]+\d* ?", " ");
        text = text.Replace("{", " ").Replace("}", " ");
        text = Regex.Replace(text, @"\s+", " ");
        return GoogleSourceSupport.Clean(text);
    }

    private static string BuildRiskTermSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.ToLowerInvariant();
        var terms = new List<string>();
        foreach (var term in new[] { "confidential", "privileged", "source code", "api key", "password", "token", "export", "download", "competitor", "competitive intelligence", "notebooklm" })
            if (lower.Contains(term, StringComparison.OrdinalIgnoreCase)) terms.Add(term);
        return string.Join("; ", terms.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Truncate(string value, int max)
    {
        value = GoogleSourceSupport.Clean(value);
        if (value.Length <= max) return value;
        return value[..max];
    }
}
