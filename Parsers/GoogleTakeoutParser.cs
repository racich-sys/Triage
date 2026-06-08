using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace VestigantTriage;

public class GoogleTakeoutParser : IArtifactParser
{
    public string ParserName => "Google Takeout";

    public bool CanParse(string filePath)
    {
        var lower = filePath.Replace('\\', '/').ToLowerInvariant();
        if (GoogleSourceSupport.IsZip(filePath))
            return lower.Contains("takeout") || GoogleSourceSupport.ZipContains(filePath, IsTakeoutEntry);
        if (GoogleSourceSupport.IsCsv(filePath))
            return IsTakeoutEntry(filePath);
        if (GoogleSourceSupport.IsJson(filePath))
            return lower.Contains("mail/user settings") || lower.Contains("filters.json") || lower.Contains("blocked addresses.json") || lower.Contains("vacation responder.json");
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
            int row = 0;
            foreach (var raw in CsvUtil.ReadRows(filePath))
            {
                row++;
                yield return BuildCsvEvent(raw, filePath, Path.GetFileName(filePath), row);
            }
            yield break;
        }
        if (GoogleSourceSupport.IsJson(filePath))
        {
            yield return BuildJsonObservedEvent(filePath, Path.GetFileName(filePath), File.ReadAllText(filePath));
        }
    }

    private IEnumerable<NormalizedEvent> ParseZip(string filePath, Action<string> log)
    {
        using var archive = ZipFile.OpenRead(filePath);
        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name) && IsTakeoutEntry(e.FullName)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                int row = 0;
                using var stream = entry.Open();
                foreach (var raw in GoogleSourceSupport.ReadCsvRows(stream))
                {
                    row++;
                    if (row % 10000 == 0) log($"  … parsed {row:N0} Google Takeout rows from {entry.FullName}");
                    yield return BuildCsvEvent(raw, filePath, entry.FullName, row);
                }
            }
            else if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                yield return BuildJsonObservedEvent(filePath, entry.FullName, reader.ReadToEnd());
            }
            else
            {
                yield return BuildEntryObservedEvent(filePath, entry.FullName, entry.Length, entry.LastWriteTime.UtcDateTime);
            }
        }
    }

    private static bool IsTakeoutEntry(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        return p.Contains("takeout") ||
               p.Contains("activities - a list of google services") ||
               p.Contains("devices - a list of devices") ||
               p.Contains("mail/user settings/") ||
               p.Contains("youtube and youtube music") ||
               p.Contains("blocked addresses.json") ||
               p.Contains("vacation responder.json") ||
               p.Contains("filters.json");
    }

    private static NormalizedEvent BuildCsvEvent(Dictionary<string, string> row, string container, string sourceEntry, int rowNumber)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        var timestampText = GoogleSourceSupport.Get(row, "Activity Timestamp", "Date", "Timestamp", "Time", "Last status report time");
        var timestamp = TimeUtil.ParseUtc(timestampText);
        var product = GoogleSourceSupport.Get(row, "Product Name", "Sub-Product Name", "Device Type", "Marketing Name", "Activity Type", "OS", "Brand Name");
        var op = family switch
        {
            "Activities" => "GoogleTakeout_ActivityObserved",
            "Devices" => "GoogleTakeout_DeviceObserved",
            _ => "GoogleTakeout_ProductRowObserved"
        };
        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - " + family,
            UserId = GoogleSourceSupport.FirstNonBlank(GoogleSourceSupport.Get(row, "Gaia ID", "User", "User email", "Email"), "Unknown"),
            Operation = op,
            ObjectPath = product,
            ClientIp = GoogleSourceSupport.Get(row, "IP Address", "Client IP", "Remote IP"),
            TimestampUtc = timestamp ?? DateTime.MinValue,
            EventTimeBasis = family == "Activities" ? "GoogleTakeoutActivityTimestamp" : "GoogleTakeoutInventoryMetadata",
            EventTimeConfidence = timestamp.HasValue && family == "Activities" ? "High" : "MetadataOnly",
            IsBehavioralTimestamp = timestamp.HasValue && family == "Activities",
            TimestampWarning = timestamp.HasValue && family == "Activities" ? string.Empty : "Google Takeout inventory/config row is not treated as endpoint behavior without correlation."
        };
        ev.AdditionalFields["ParserName"] = "Google Takeout";
        ev.AdditionalFields["ArtifactType"] = "Google Takeout Export";
        ev.AdditionalFields["RecordType"] = "GoogleTakeout";
        ev.AdditionalFields["Workload"] = "Google Takeout";
        ev.AdditionalFields["Category"] = "CloudAccountExport";
        ev.AdditionalFields["GoogleTakeoutProductFamily"] = family;
        ev.AdditionalFields["GoogleTakeoutSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleTakeoutContainer"] = container;
        ev.AdditionalFields["GoogleTakeoutRowNumber"] = rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ev.AdditionalFields["CreationDate"] = timestampText;
        ev.AdditionalFields["AuditData"] = SafeSerialize(row);
        ev.AdditionalFields["ObjectId"] = product;
        ev.AdditionalFields["TargetPath"] = product;
        GoogleSourceSupport.AddGoogleRiskFields(ev, "Takeout", row);
        foreach (var kvp in row)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key) && !ev.AdditionalFields.ContainsKey(kvp.Key)) ev.AdditionalFields[kvp.Key] = kvp.Value ?? string.Empty;
        }
        return ev;
    }

    private static NormalizedEvent BuildJsonObservedEvent(string container, string sourceEntry, string rawJson)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        var flat = JsonRepair.TryJsonLoads(rawJson ?? string.Empty);
        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - " + family,
            UserId = "Unknown",
            Operation = sourceEntry.Contains("Filters.json", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_MailFilterObserved" : sourceEntry.Contains("Blocked Addresses.json", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_BlockedAddressObserved" : sourceEntry.Contains("Vacation Responder.json", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_VacationResponderObserved" : "GoogleTakeout_ConfigObserved",
            ObjectPath = sourceEntry,
            TimestampUtc = DateTime.MinValue,
            EventTimeBasis = "GoogleTakeoutConfigMetadata",
            EventTimeConfidence = "MetadataOnly",
            IsBehavioralTimestamp = false,
            TimestampWarning = "Google Takeout config file observation is metadata-only unless correlated with other activity."
        };
        ev.AdditionalFields["ParserName"] = "Google Takeout";
        ev.AdditionalFields["ArtifactType"] = "Google Takeout Config";
        ev.AdditionalFields["RecordType"] = "GoogleTakeout";
        ev.AdditionalFields["Workload"] = "Google Takeout";
        ev.AdditionalFields["Category"] = "CloudAccountExport";
        ev.AdditionalFields["GoogleTakeoutProductFamily"] = family;
        ev.AdditionalFields["GoogleTakeoutSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleTakeoutContainer"] = container;
        ev.AdditionalFields["AuditData"] = rawJson ?? string.Empty;
        foreach (var kvp in flat.Take(250)) if (!ev.AdditionalFields.ContainsKey(kvp.Key)) ev.AdditionalFields[kvp.Key] = kvp.Value;
        return ev;
    }

    private static NormalizedEvent BuildEntryObservedEvent(string container, string sourceEntry, long bytes, DateTime lastWriteUtc)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - " + family,
            UserId = "Unknown",
            Operation = family.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_YouTubeArtifactObserved" : "GoogleTakeout_ProductArtifactObserved",
            ObjectPath = sourceEntry,
            TimestampUtc = lastWriteUtc,
            EventTimeBasis = "GoogleTakeoutArchiveEntryLastWrite",
            EventTimeConfidence = "MetadataOnly",
            IsBehavioralTimestamp = false,
            TimestampWarning = "Archive entry timestamp is not treated as behavioral account activity without correlation."
        };
        ev.AdditionalFields["ParserName"] = "Google Takeout";
        ev.AdditionalFields["ArtifactType"] = "Google Takeout Archive Entry";
        ev.AdditionalFields["RecordType"] = "GoogleTakeout";
        ev.AdditionalFields["GoogleTakeoutProductFamily"] = family;
        ev.AdditionalFields["GoogleTakeoutSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleTakeoutContainer"] = container;
        ev.AdditionalFields["FileSizeBytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ev.AdditionalFields["FileName"] = Path.GetFileName(sourceEntry);
        return ev;
    }

    private static string SafeSerialize(Dictionary<string, string> row)
    {
        try { return JsonSerializer.Serialize(row); }
        catch { return string.Empty; }
    }
}
