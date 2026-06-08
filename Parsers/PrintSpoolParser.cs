using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VestigantTriage;

public class PrintSpoolParser : IArtifactParser
{
    private const int MaxStringScanBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> CoreQueueExtensions = new(StringComparer.OrdinalIgnoreCase) { ".spl", ".shd" };
    private static readonly HashSet<string> RenderedPayloadExtensions = new(StringComparer.OrdinalIgnoreCase) { ".emf", ".xps", ".oxps", ".prn", ".pcl", ".pjl", ".ps", ".eps", ".raw" };
    private static readonly HashSet<string> PrintStateExtensions = new(StringComparer.OrdinalIgnoreCase) { ".tmp", ".bud", ".gpd", ".ppd", ".ntf", ".dat" };

    public string ParserName => "Windows Print Spool";

    public bool CanParse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var normalized = filePath.Replace('/', '\\');
        var name = Path.GetFileName(normalized) ?? string.Empty;
        var ext = Path.GetExtension(name).ToLowerInvariant();

        if (CoreQueueExtensions.Contains(ext)) return true;
        if (RenderedPayloadExtensions.Contains(ext))
            return IsPrintSpoolPath(normalized) || LooksLikePrintArtifactName(name);
        if (PrintStateExtensions.Contains(ext))
            return IsPrintSpoolPath(normalized) || IsPrintConfigurationPath(normalized) || LooksLikeGhostPrintArtifact(name);
        return false;
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var info = new FileInfo(filePath);
        var name = Path.GetFileName(filePath) ?? string.Empty;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var kind = Classify(ext, filePath);
        var ev = new NormalizedEvent
        {
            DataSource = "Print_Spool",
            Operation = "Print Spool Artifact Observed",
            ObjectPath = name,
            UserId = ParserSupport.ExtractUserFromPath(filePath, "System"),
            TimestampUtc = DateTime.MinValue
        };

        ev.AdditionalFields["EventCategory"] = "Printing";
        ev.AdditionalFields["Workload"] = "Endpoint";
        ev.AdditionalFields["ArtifactType"] = kind.ArtifactType;
        ev.AdditionalFields["PrintSpoolEvidenceRole"] = kind.EvidenceRole;
        ev.AdditionalFields["PrintSpoolFileType"] = kind.FileType;
        ev.AdditionalFields["PrintSpoolFile"] = name;
        ev.AdditionalFields["PrintSpoolFileStem"] = Path.GetFileNameWithoutExtension(name) ?? string.Empty;
        ev.AdditionalFields["PrintSpoolLocalEvidencePath"] = filePath;
        ev.AdditionalFields["FileName"] = name;
        ev.AdditionalFields["FileExtension"] = ext.TrimStart('.');
        ev.AdditionalFields["FileSizeBytes"] = info.Exists ? info.Length.ToString() : "0";
        ev.AdditionalFields["PrintSpoolInterpretation"] = "Print spool queue, shadow metadata, rendered payload, and print configuration artifacts are print-related evidence. Treat source/staged timestamps as metadata unless corroborated by PrintService EVTX, registry, LNK, document MRU, or filesystem activity.";
        ParserSupport.AddParseQuality(ev, ParserName, "Medium", "Print artifact recognized by extension/path/name; payload strings and signatures are sampled, but print completion is not inferred from file presence alone.");
        ParserSupport.SetEventTime(ev, null, "PrintSpoolFileObserved", "MetadataOnly", false, "Print artifact source/staged timestamps are not treated as behavioral print times in this parser.");

        AddPairingAndJobFields(ev, name, ext);

        try
        {
            if (!info.Exists || info.Length <= 0)
            {
                ev.AdditionalFields["ParseWarning"] = "Print artifact was empty or unavailable.";
                return new[] { ev };
            }

            var bytes = ReadPrefixBytes(filePath, MaxStringScanBytes);
            ev.AdditionalFields["BytesScannedForStrings"] = bytes.Length.ToString();
            AddSignatureFields(ev, bytes, ext);
            AddCandidateStrings(ev, bytes);
        }
        catch (Exception ex)
        {
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Print artifact recognized, but payload sampling failed.");
        }

        return new[] { ev };
    }

    private static (string FileType, string ArtifactType, string EvidenceRole) Classify(string ext, string path)
    {
        return ext switch
        {
            ".shd" => ("Shadow/job metadata (.SHD)", "PrintSpoolShadowSHD", "Queue shadow/job metadata"),
            ".spl" => ("Spool payload (.SPL)", "PrintSpoolPayloadSPL", "Queue payload"),
            ".emf" => ("Enhanced Metafile rendered print payload (.EMF)", "PrintSpoolEnhancedMetafile", "Rendered print payload"),
            ".xps" => ("XPS spool/output (.XPS)", "PrintSpoolXps", "Rendered print payload"),
            ".oxps" => ("OpenXPS spool/output (.OXPS)", "PrintSpoolOpenXps", "Rendered print payload"),
            ".prn" => ("Print-to-file / raw printer data (.PRN)", "PrintSpoolRawPrinterData", "Raw printer/output payload"),
            ".pcl" => ("Printer Command Language payload (.PCL)", "PrintSpoolPclPayload", "Printer-language payload"),
            ".pjl" => ("Printer Job Language metadata/payload (.PJL)", "PrintSpoolPjlPayload", "Printer-language job metadata"),
            ".ps" => ("PostScript print payload (.PS)", "PrintSpoolPostScriptPayload", "Printer-language payload"),
            ".eps" => ("Encapsulated PostScript print payload (.EPS)", "PrintSpoolEncapsulatedPostScriptPayload", "Printer-language payload"),
            ".raw" => ("Raw print stream (.RAW)", "PrintSpoolRawPayload", "Raw printer/output payload"),
            ".bud" => ("Printer driver/device-mode cache (.BUD)", "PrintSpoolDriverBud", "Print configuration/cache"),
            ".gpd" => ("Generic printer description (.GPD)", "PrintSpoolDriverGpd", "Print driver/configuration"),
            ".ppd" => ("PostScript printer description (.PPD)", "PrintSpoolDriverPpd", "Print driver/configuration"),
            ".ntf" => ("Printer font/resource file (.NTF)", "PrintSpoolDriverNtf", "Print driver/resource"),
            ".dat" when IsPrintConfigurationPath(path) => ("Print configuration/state data (.DAT)", "PrintSpoolConfigurationData", "Print configuration/state"),
            ".tmp" when IsPrintSpoolPath(path) => ("Spool temporary file", "PrintSpoolTemporaryFile", "Queue temporary artifact"),
            _ => ("Print-related file", "PrintSpoolRelatedFile", "Print-related artifact")
        };
    }

    private static bool IsPrintSpoolPath(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Contains(@"\Windows\System32\spool\PRINTERS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\System32\spool\PRINTERS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\spool\PRINTERS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\Windows\System32\spool\SERVERS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\System32\spool\SERVERS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\spool\SERVERS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("[DELETED PRINT SPOOL]", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("[DELETED]", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("GhostPrint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrintConfigurationPath(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Contains(@"\Windows\System32\spool\drivers\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\System32\spool\drivers\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\spool\drivers\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\Windows\System32\spool\PRTPROCS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\System32\spool\PRTPROCS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains(@"\spool\PRTPROCS\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("GhostPrint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePrintArtifactName(string name)
    {
        return name.StartsWith("FP", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("SPL", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("spool", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("print", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("printer", StringComparison.OrdinalIgnoreCase) ||
               LooksLikeGhostPrintArtifact(name);
    }

    private static bool LooksLikeGhostPrintArtifact(string name)
    {
        return name.StartsWith("GhostPrint", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("[DELETED PRINT SPOOL]", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPairingAndJobFields(NormalizedEvent ev, string name, string ext)
    {
        var stem = Path.GetFileNameWithoutExtension(name) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(stem) && stem.All(char.IsDigit))
            ev.AdditionalFields["PrintSpoolNumericJobIdCandidate"] = stem;

        if (ext == ".shd")
        {
            var paired = DerivePairedSpoolFile(name, ".spl");
            if (!string.IsNullOrWhiteSpace(paired)) ev.AdditionalFields["PairedSPLCandidate"] = paired;
        }
        else if (ext == ".spl")
        {
            var paired = DerivePairedSpoolFile(name, ".shd");
            if (!string.IsNullOrWhiteSpace(paired)) ev.AdditionalFields["PairedSHDCandidate"] = paired;
        }
        else if (ext == ".emf" && stem.StartsWith("FP", StringComparison.OrdinalIgnoreCase))
        {
            ev.AdditionalFields["PrintSpoolRenderedPayloadNamePattern"] = "FP*.EMF rendered spool payload candidate";
        }
    }

    private static string DerivePairedSpoolFile(string name, string newExtension)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var stem = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(stem) ? string.Empty : stem + newExtension;
    }

    private static byte[] ReadPrefixBytes(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = (int)Math.Min(maxBytes, fs.Length);
        var data = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = fs.Read(data, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == len) return data;
        var exact = new byte[read];
        Array.Copy(data, exact, read);
        return exact;
    }

    private static void AddSignatureFields(NormalizedEvent ev, byte[] data, string ext)
    {
        if (data.Length >= 4)
        {
            ev.AdditionalFields["FileMagicHex"] = BitConverter.ToString(data, 0, Math.Min(16, data.Length)).Replace("-", " ");
            if (data.Length >= 44 && data[40] == 0x20 && data[41] == 0x45 && data[42] == 0x4D && data[43] == 0x46)
                ev.AdditionalFields["EMFSignature"] = "EMF header signature observed";
            if (data.Length >= 2 && data[0] == 0x50 && data[1] == 0x4B)
                ev.AdditionalFields["ZipContainerSignature"] = "PK ZIP signature observed; possible XPS/OXPS package or compressed payload";
            if (StartsWithAscii(data, "@PJL") || StartsWithBytes(data, new byte[] { 0x1B, 0x25, 0x2D, 0x31, 0x32, 0x33, 0x34, 0x35, 0x58 }))
                ev.AdditionalFields["PjlSignature"] = "Printer Job Language/PJL signature observed";
            if (StartsWithAscii(data, "%!PS"))
                ev.AdditionalFields["PostScriptSignature"] = "PostScript signature observed";
            if (data[0] == 0x1B)
                ev.AdditionalFields["EscPrinterLanguagePrefix"] = "ESC-prefixed printer-language payload candidate";
        }

        if (ext == ".spl")
            ev.AdditionalFields["SpoolPayloadNote"] = "SPL files may contain RAW printer data, EMF spool data, XPS spool data, or device-specific print language. This parser records metadata and sampled strings only.";
    }

    private static bool StartsWithAscii(byte[] data, string text)
    {
        if (data.Length < text.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (data[i] != (byte)text[i]) return false;
        return true;
    }

    private static bool StartsWithBytes(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }

    private static void AddCandidateStrings(NormalizedEvent ev, byte[] data)
    {
        var candidates = ExtractPrintableStrings(data).ToList();
        if (candidates.Count == 0) return;

        ev.AdditionalFields["CandidateStringCount"] = candidates.Count.ToString();
        ev.AdditionalFields["CandidateStringsSample"] = string.Join("; ", candidates.Take(40));

        var paths = candidates.Where(c => LooksLikePathOrDocument(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        if (paths.Count > 0)
        {
            ev.AdditionalFields["CandidateDocumentOrPathCount"] = paths.Count.ToString();
            ev.AdditionalFields["CandidateDocumentOrPaths"] = string.Join("; ", paths);
            ParserSupport.AddTargetFields(ev, paths[0], "PrintSpoolStringCandidate");
        }

        var printers = candidates.Where(c => c.Contains("printer", StringComparison.OrdinalIgnoreCase) || c.Contains("print", StringComparison.OrdinalIgnoreCase) || c.Contains("@PJL", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        if (printers.Count > 0)
            ev.AdditionalFields["CandidatePrinterStrings"] = string.Join("; ", printers);
    }

    private static IEnumerable<string> ExtractPrintableStrings(byte[] data)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in ExtractAsciiStrings(data).Concat(ExtractUnicodeStrings(data)))
        {
            var cleaned = ForensicText.CleanDisplayValue(value);
            if (cleaned.Length < 4 || cleaned.Length > 260) continue;
            if (cleaned.Count(char.IsControl) > 0) continue;
            if (!seen.Add(cleaned)) continue;
            yield return cleaned;
            if (seen.Count >= 200) yield break;
        }
    }

    private static IEnumerable<string> ExtractAsciiStrings(byte[] data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b >= 0x20 && b <= 0x7E)
            {
                sb.Append((char)b);
                continue;
            }
            if (sb.Length >= 4) yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length >= 4) yield return sb.ToString();
    }

    private static IEnumerable<string> ExtractUnicodeStrings(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            var ch = BitConverter.ToChar(data, i);
            if (ch >= 0x20 && ch <= 0x7E)
            {
                sb.Append(ch);
                continue;
            }
            if (sb.Length >= 4) yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length >= 4) yield return sb.ToString();
    }

    private static bool LooksLikePathOrDocument(string value)
    {
        if (value.Contains(@":\", StringComparison.Ordinal) || value.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (value.Contains(".doc", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".xls", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".ppt", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".pdf", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".msg", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".txt", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".rtf", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".png", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
