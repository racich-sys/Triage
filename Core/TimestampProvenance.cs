using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VestigantTriage;

internal sealed class TimestampVerdict
{
    public DateTime TimestampUtc { get; set; } = DateTime.MinValue;
    public string Basis { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Unknown";
    public bool IsBehavioral { get; set; }
    public string Warning { get; set; } = "";
}

internal static class TimestampProvenance
{
    public static TimestampVerdict Evaluate(NormalizedEvent ev, IDictionary<string, string> fields, string parserName, string? localEvidencePath = null)
    {
        fields ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var basis = FirstNonBlank(
            ev.EventTimeBasis,
            Get(fields, "EventTimeBasis"),
            Get(fields, "TimestampBasis"),
            Get(fields, "RegistryTimestampBasis"),
            InferBasis(ev, fields, parserName));

        var confidence = FirstNonBlank(
            ev.EventTimeConfidence,
            Get(fields, "EventTimeConfidence"),
            InferConfidence(basis, ev, fields, parserName));

        var timestamp = ev.TimestampUtc;
        var native = IsArtifactNativeBasis(basis);
        var sourceMetadata = IsSourceMetadataBasis(basis);
        var parseError = fields.ContainsKey("ParseError") || ev.Operation.Contains("ParseError", StringComparison.OrdinalIgnoreCase);
        var sameAsWorkingCopy = TimestampMatchesFileLastWrite(timestamp, localEvidencePath);
        var bestEffortMetadata = IsKnownBestEffortMetadata(ev, fields, parserName);

        bool behavioral = ev.IsBehavioralTimestamp ?? (timestamp != DateTime.MinValue && native && !sourceMetadata && !parseError && !bestEffortMetadata);
        var warning = FirstNonBlank(ev.TimestampWarning, Get(fields, "TimestampWarning"));

        if (timestamp == DateTime.MinValue)
        {
            behavioral = false;
            if (string.IsNullOrWhiteSpace(warning))
                warning = "No artifact-native timestamp was decoded.";
        }
        else if (sourceMetadata || sameAsWorkingCopy || bestEffortMetadata || parseError)
        {
            behavioral = false;

            if (sameAsWorkingCopy && string.IsNullOrWhiteSpace(warning))
                warning = "Timestamp matched the staged WorkingEvidence file last-write time and was suppressed as behavioral evidence.";
            else if (sourceMetadata && string.IsNullOrWhiteSpace(warning))
                warning = "Source-file metadata timestamp was not treated as a user/system behavior timestamp.";
            else if (bestEffortMetadata && string.IsNullOrWhiteSpace(warning))
                warning = "Best-effort or metadata-only artifact timestamp was not treated as behavioral activity.";
            else if (parseError && string.IsNullOrWhiteSpace(warning))
                warning = "Parser-error rows are not treated as behavioral timestamp evidence.";

            // Avoid false current-date timeline rows caused by copied/staged evidence metadata.
            timestamp = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(confidence) || confidence.Equals("High", StringComparison.OrdinalIgnoreCase) || confidence.Equals("Medium", StringComparison.OrdinalIgnoreCase))
                confidence = "MetadataOnly";
        }

        if (string.IsNullOrWhiteSpace(confidence))
            confidence = behavioral ? "Medium" : "Unknown";

        return new TimestampVerdict
        {
            TimestampUtc = timestamp,
            Basis = string.IsNullOrWhiteSpace(basis) ? "Unknown" : basis,
            Confidence = confidence,
            IsBehavioral = behavioral,
            Warning = warning
        };
    }

    public static void ApplyToEvent(NormalizedEvent ev, DateTime? timestampUtc, string basis, string confidence, bool isBehavioral, string warning = "")
    {
        ev.TimestampUtc = timestampUtc ?? DateTime.MinValue;
        ev.EventTimeBasis = basis ?? "Unknown";
        ev.EventTimeConfidence = confidence ?? (isBehavioral ? "Medium" : "Unknown");
        ev.IsBehavioralTimestamp = isBehavioral;
        ev.TimestampWarning = warning ?? string.Empty;
        ev.AdditionalFields["EventTimeBasis"] = ev.EventTimeBasis;
        ev.AdditionalFields["EventTimeConfidence"] = ev.EventTimeConfidence;
        ev.AdditionalFields["IsBehavioralTimestamp"] = isBehavioral ? "Yes" : "No";
        if (!string.IsNullOrWhiteSpace(ev.TimestampWarning)) ev.AdditionalFields["TimestampWarning"] = ev.TimestampWarning;
    }

    private static string InferBasis(NormalizedEvent ev, IDictionary<string, string> fields, string parserName)
    {
        var ds = ev.DataSource ?? string.Empty;
        var op = ev.Operation ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(Get(fields, "LastRunUtc"))) return "PrefetchLastRun";
        if (!string.IsNullOrWhiteSpace(Get(fields, "DeletedUtc"))) return "RecycleBinDeletionTime";
        if (!string.IsNullOrWhiteSpace(Get(fields, "VisitTimeUtc"))) return "BrowserVisitTime";
        if (!string.IsNullOrWhiteSpace(Get(fields, "StartTimeUtc")) && ds.Contains("Download", StringComparison.OrdinalIgnoreCase)) return "BrowserDownloadStartTime";
        if (!string.IsNullOrWhiteSpace(Get(fields, "TargetAccessedUtc"))) return ds.Contains("Jump", StringComparison.OrdinalIgnoreCase) ? "JumpListTargetAccessTime" : "LnkTargetAccessTime";
        if (!string.IsNullOrWhiteSpace(Get(fields, "RegistryKeyLastWriteUtc")) || Get(fields, "RegistryTimestampBasis").Equals("KeyLastWrite", StringComparison.OrdinalIgnoreCase)) return "RegistryKeyLastWrite";
        if (ds.Equals("WinEventLog", StringComparison.OrdinalIgnoreCase)) return "EvtxRecordTime";
        if (ds.Equals("O365_UAL", StringComparison.OrdinalIgnoreCase) || ds.StartsWith("O365", StringComparison.OrdinalIgnoreCase)) return "O365UALCreationDate";
        if (ds.Equals("USN_Journal", StringComparison.OrdinalIgnoreCase)) return "UsnRecordTimestamp";
        if (ds.Equals("Office_Activity", StringComparison.OrdinalIgnoreCase)) return "OfficeRegistryKeyLastWrite";
        if (ds.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Get(fields, "RegistryKeyPath"))) return "OneDriveRegistryKeyLastWrite";
        if (ds.Equals("PowerShell_Transcript", StringComparison.OrdinalIgnoreCase) && !Get(fields, "TimestampBasis").Contains("FileLastWrite", StringComparison.OrdinalIgnoreCase)) return "PowerShellTranscriptHeader";
        if (ds.Equals("AmCache", StringComparison.OrdinalIgnoreCase)) return "AmCacheRegistryKeyLastWrite";
        if (op.Contains("Owner", StringComparison.OrdinalIgnoreCase) || ds.Contains("Owner File", StringComparison.OrdinalIgnoreCase)) return "OfficeOwnerFileMetadata";
        if (ds.Equals("Print_Spool", StringComparison.OrdinalIgnoreCase) || op.Contains("Print Spool", StringComparison.OrdinalIgnoreCase)) return "PrintSpoolFileObserved";
        if (ds.StartsWith("Metadata", StringComparison.OrdinalIgnoreCase)) return "MetadataFallback";

        return "Unknown";
    }

    private static string InferConfidence(string basis, NormalizedEvent ev, IDictionary<string, string> fields, string parserName)
    {
        if (IsSourceMetadataBasis(basis) || IsKnownBestEffortMetadata(ev, fields, parserName)) return "MetadataOnly";
        if (basis.Contains("Unknown", StringComparison.OrdinalIgnoreCase)) return ev.TimestampUtc == DateTime.MinValue ? "Unknown" : "Low";
        if (basis.Contains("Browser", StringComparison.OrdinalIgnoreCase) || basis.Contains("Evtx", StringComparison.OrdinalIgnoreCase) || basis.Contains("UAL", StringComparison.OrdinalIgnoreCase) || basis.Contains("RecycleBin", StringComparison.OrdinalIgnoreCase) || basis.Contains("Prefetch", StringComparison.OrdinalIgnoreCase)) return "High";
        if (basis.Contains("Registry", StringComparison.OrdinalIgnoreCase) || basis.Contains("Lnk", StringComparison.OrdinalIgnoreCase) || basis.Contains("JumpList", StringComparison.OrdinalIgnoreCase) || basis.Contains("USN", StringComparison.OrdinalIgnoreCase) || basis.Contains("AmCache", StringComparison.OrdinalIgnoreCase)) return "Medium";
        return "Medium";
    }

    private static bool IsArtifactNativeBasis(string basis)
    {
        if (string.IsNullOrWhiteSpace(basis)) return false;
        if (IsSourceMetadataBasis(basis)) return false;
        var b = basis.ToLowerInvariant();
        return b.Contains("prefetchlastrun") || b.Contains("lnktarget") || b.Contains("jumplisttarget") || b.Contains("recyclebin") || b.Contains("registrykeylastwrite") || b.Contains("browservisit") || b.Contains("browserdownload") || b.Contains("evtx") || b.Contains("ual") || b.Contains("usn") || b.Contains("amcache") || b.Contains("office") || b.Contains("onedriveregistry") || b.Contains("transcriptheader") || b.Contains("setupapisectionstartlocal");
    }

    private static bool IsSourceMetadataBasis(string basis)
    {
        if (string.IsNullOrWhiteSpace(basis)) return false;
        return basis.Contains("FileLastWrite", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("SourceFile", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("MetadataFallback", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("OfficeOwnerFileMetadata", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("PrintSpoolFileObserved", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("WorkingEvidence", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("ParserRun", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("HistoryFileLastWrite", StringComparison.OrdinalIgnoreCase) ||
               basis.Contains("TranscriptFileLastWrite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownBestEffortMetadata(NormalizedEvent ev, IDictionary<string, string> fields, string parserName)
    {
        var ds = ev.DataSource ?? string.Empty;
        var op = ev.Operation ?? string.Empty;
        if (ds.Equals("SRUM", StringComparison.OrdinalIgnoreCase)) return true;
        if (op.Contains("ParseError", StringComparison.OrdinalIgnoreCase)) return true;
        if (op.Contains("Database_Observed", StringComparison.OrdinalIgnoreCase)) return true;
        if (op.Contains("MetadataOnly", StringComparison.OrdinalIgnoreCase)) return true;
        if (Get(fields, "SrumParseMode").Contains("BestEffort", StringComparison.OrdinalIgnoreCase)) return true;
        if (Get(fields, "SrumParseMode").Contains("MetadataOnly", StringComparison.OrdinalIgnoreCase)) return true;
        if (ds.StartsWith("Metadata", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TimestampMatchesFileLastWrite(DateTime timestamp, string? filePath)
    {
        if (timestamp == DateTime.MinValue || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            var lw = File.GetLastWriteTimeUtc(filePath);
            return Math.Abs((timestamp.ToUniversalTime() - lw).TotalSeconds) <= 2.0;
        }
        catch { return false; }
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return string.Empty;
    }

    private static string Get(IDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
}
