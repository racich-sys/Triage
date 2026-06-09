using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace VestigantTriage;

public class GoogleWorkspaceAuditParser : IArtifactParser
{
    public string ParserName => "Google Workspace Audit";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath) ?? string.Empty;
        if (GoogleSourceSupport.IsCsv(filePath))
        {
            if (LooksLikeTakeoutAccessLogActivityPath(filePath)) return false;
            if (GoogleAuditSourceRegistry.LooksLikeGoogleAuditFileName(filePath)) return true;
            if (!LooksLikeWorkspaceAuditPath(filePath)) return false;
            var headers = GoogleSourceSupport.ReadCsvHeaders(filePath);
            return LooksLikeAuditHeaders(headers);
        }
        if (GoogleSourceSupport.IsZip(filePath))
            return GoogleSourceSupport.ZipContains(filePath, IsGoogleAuditEntry);
        return false;
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        if (GoogleSourceSupport.IsZip(filePath))
        {
            foreach (var ev in ParseZip(filePath, log)) yield return ev;
            yield break;
        }

        if (GoogleSourceSupport.IsCsv(filePath))
        {
            var headers = GoogleSourceSupport.ReadCsvHeaders(filePath);
            var family = GoogleAuditSourceRegistry.Identify(filePath, headers);
            int row = 0;
            foreach (var raw in CsvUtil.ReadRows(filePath))
            {
                row++;
                if (row % 10000 == 0) log($"  … parsed {row:N0} Google Workspace Audit rows from {Path.GetFileName(filePath)}");
                yield return BuildEvent(raw, family, Path.GetFileName(filePath), row, filePath);
            }
        }
    }

    private IEnumerable<NormalizedEvent> ParseZip(string filePath, Action<string> log)
    {
        using var archive = ZipFile.OpenRead(filePath);
        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name) && IsGoogleAuditEntry(e.FullName)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var headerStream = entry.Open();
            var headers = GoogleSourceSupport.ReadCsvHeaders(headerStream);
            var family = GoogleAuditSourceRegistry.Identify(entry.FullName, headers);
            int row = 0;
            using var dataStream = entry.Open();
            foreach (var raw in GoogleSourceSupport.ReadCsvRows(dataStream))
            {
                row++;
                if (row % 10000 == 0) log($"  … parsed {row:N0} Google Workspace Audit rows from {entry.FullName}");
                yield return BuildEvent(raw, family, entry.FullName, row, filePath);
            }
        }
    }

    private static bool LooksLikeWorkspaceAuditPath(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        return p.Contains("audit and investigation") || p.Contains("google audit") || p.Contains("admin audit");
    }

    private static bool LooksLikeTakeoutAccessLogActivityPath(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        if (!p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        if (!p.Contains("activities - a list of google services")) return false;
        return p.Contains("/takeout/") || p.Contains("/access log activity/") || p.Contains("part") && p.Contains("takeout");
    }

    private static bool IsGoogleAuditEntry(string entryName)
    {
        if (!entryName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        return GoogleAuditSourceRegistry.LooksLikeGoogleAuditFileName(entryName);
    }

    private static bool LooksLikeAuditHeaders(IReadOnlyList<string> headers)
    {
        if (headers.Count == 0) return false;
        var set = headers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains("Date") && !set.Contains("Activity Timestamp")) return false;
        return set.Contains("Event") || set.Contains("OAuth event") || set.Contains("Description") || set.Contains("Activity Type") || set.Contains("Actor") || set.Contains("Gaia ID");
    }

    private static NormalizedEvent BuildEvent(Dictionary<string, string> row, GoogleAuditFamilyDefinition family, string sourceEntry, int rowNumber, string sourceContainer)
    {
        var operationRaw = GoogleSourceSupport.Get(row, family.OperationFields.Concat(new[] { "Event", "OAuth event", "Activity Type", "Action", "Description" }).ToArray());
        var operation = GoogleSourceSupport.NormalizeOperation(family.Family, operationRaw, row);
        var timestampText = GoogleSourceSupport.Get(row, family.TimestampFields.Concat(new[] { "Date", "Activity Timestamp", "CreationDate", "Creation Time" }).ToArray());
        var timestamp = TimeUtil.ParseUtc(timestampText);
        var user = GoogleSourceSupport.Get(row, family.ActorFields.Concat(new[] { "Actor", "User", "User email", "Owner", "Profile user", "Device user", "Gaia ID", "Takeout initiator" }).ToArray());
        var ip = GoogleSourceSupport.Get(row, family.IpFields.Concat(new[] { "IP address", "IP Address", "Client IP", "Remote IP", "Local IP" }).ToArray());
        var target = GoogleSourceSupport.BuildReadableTarget(family.Family, row, family.TargetFields);
        var stableObjectId = GoogleSourceSupport.StableObjectId(row);

        var ev = new NormalizedEvent
        {
            DataSource = family.CanonicalDataSource,
            UserId = string.IsNullOrWhiteSpace(user) ? "Unknown" : user,
            Operation = string.IsNullOrWhiteSpace(operation) ? "GoogleAudit_Event" : operation,
            ObjectPath = target,
            ClientIp = ip,
            TimestampUtc = timestamp ?? DateTime.MinValue,
            EventTimeBasis = "GoogleWorkspaceAuditDate",
            EventTimeConfidence = timestamp.HasValue ? "High" : "Unknown",
            IsBehavioralTimestamp = timestamp.HasValue,
            TimestampWarning = timestamp.HasValue ? string.Empty : "Google audit row did not contain a parseable Date/Activity Timestamp."
        };

        ev.AdditionalFields["ParserName"] = "Google Workspace Audit";
        ev.AdditionalFields["ArtifactType"] = "Google Workspace Audit CSV";
        GoogleSourceSupport.AddGoogleCoreFields(
            ev,
            "GoogleWorkspaceAudit",
            family.Family,
            ev.UserId,
            ip,
            GoogleSourceSupport.Get(row, "User agent", "User Agent", "User Agent String"),
            operationRaw,
            ev.Operation,
            target,
            stableObjectId,
            timestampText,
            sourceEntry,
            sourceContainer,
            rowNumber);
        ev.AdditionalFields["GoogleWorkload"] = "Google Workspace";
        ev.AdditionalFields["GoogleCategory"] = "CloudAudit";
        ev.AdditionalFields["GoogleEventCategory"] = family.Family;
        ev.AdditionalFields["GoogleAuditFamily"] = family.Family;
        ev.AdditionalFields["GoogleAuditSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleAuditContainer"] = sourceContainer;
        ev.AdditionalFields["GoogleAuditRowNumber"] = rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ev.AdditionalFields["GoogleOperationOriginal"] = operationRaw;
        ev.AdditionalFields["GoogleRawSerializedRow"] = SafeSerialize(row);
        GoogleSourceSupport.AddGoogleField(ev, "GoogleFileName", GoogleSourceSupport.FirstNonBlank(GoogleSourceSupport.Get(row, "FileName", "SourceFileName", "DestinationFileName", "Filename", "Attachment name", "Title", "Content name"), GoogleSourceSupport.InferTargetFileName(target)));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleSiteUrl", GoogleSourceSupport.Get(row, "SiteUrl", "Site URL", "URL", "Resource Url", "Attachment URL"));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleSourceRelativeUrl", GoogleSourceSupport.Get(row, "SourceRelativeUrl", "SourceRelativeURL", "Source Relative URL", "URL", "Target"));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleFileSizeBytes", GoogleSourceSupport.Get(row, "FileSizeBytes", "Content size", "Number of response bytes", "Size", "File size"));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleResultStatus", GoogleSourceSupport.Get(row, "Event status", "Event result", "Status", "Takeout status"));
        GoogleSourceSupport.AddGoogleRiskFields(ev, family.Family, row);
        GoogleSourceSupport.PromoteWorkspaceAuditFields(ev, family.Family, row, operationRaw, ev.Operation, target, stableObjectId);

        GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleAuditRaw", row);
        return ev;
    }

    private static string SafeSerialize(Dictionary<string, string> row)
    {
        try { return JsonSerializer.Serialize(row); }
        catch { return string.Empty; }
    }
}
