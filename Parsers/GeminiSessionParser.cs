using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

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
                yield return BuildEvent(filePath, entry.FullName, entry.Length, entry.LastWriteTime.UtcDateTime);
            yield break;
        }

        var fi = new FileInfo(filePath);
        yield return BuildEvent(filePath, Path.GetFileName(filePath), fi.Exists ? fi.Length : 0, fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue);
    }

    private static bool IsGeminiEntry(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (!p.Contains("gemini")) return false;
        return p.Contains("transcript") || p.Contains("code extract") || p.Contains("output pdf") || p.Contains("screenshot") || p.EndsWith(".rtf") || p.EndsWith(".rtfd") || p.EndsWith(".py") || p.EndsWith(".pdf") || p.EndsWith(".png") || p.EndsWith(".jpg") || p.EndsWith(".jpeg") || p.EndsWith(".txt");
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
        ev.AdditionalFields["RecordType"] = "GeminiSessionArtifact";
        ev.AdditionalFields["Workload"] = "Google Gemini";
        ev.AdditionalFields["Category"] = "AIArtifact";
        ev.AdditionalFields["GeminiArtifactKind"] = kind;
        ev.AdditionalFields["GeminiSourceEntry"] = entry;
        ev.AdditionalFields["GeminiContainer"] = container;
        ev.AdditionalFields["FileName"] = Path.GetFileName(entry);
        ev.AdditionalFields["FileSizeBytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
}
