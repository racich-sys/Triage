using System;
using System.Collections.Generic;
using System.IO;

namespace VestigantTriage;

public class O365UalParser : IArtifactParser
{
    public string ParserName => "O365 Unified Audit Log";

    public bool CanParse(string filePath)
    {
        if (!filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var reader = new StreamReader(filePath);
            var header = reader.ReadLine();
            return header != null &&
                   (header.Contains("AuditData", StringComparison.OrdinalIgnoreCase) ||
                    (header.Contains("RecordId", StringComparison.OrdinalIgnoreCase) &&
                     header.Contains("Operation", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        int processed = 0;

        foreach (var row in CsvUtil.ReadRows(filePath))
        {
            processed++;
            if (processed % 10000 == 0)
                log($"  … parsed {processed:N0} UAL records");

            row.TryGetValue("AuditData", out var jsonBlob);
            var flatData = JsonRepair.TryJsonLoads(jsonBlob ?? string.Empty);

            var normEvent = new NormalizedEvent
            {
                DataSource = "O365 Unified Audit Log",
                UserId = FirstNonBlank(Get(row, "UserId"), Get(row, "UserIds"), Get(flatData, "UserId"), Get(flatData, "UserIds"), "Unknown"),
                Operation = FirstNonBlank(Get(row, "Operation"), Get(row, "Operations"), Get(flatData, "Operation"), "Unknown")
            };

            var timeStr = FirstNonBlank(Get(row, "CreationDate"), Get(row, "CreationTime"), Get(flatData, "CreationTime"), Get(flatData, "CreationDate"));
            var utc = TimeUtil.ParseUtc(timeStr);
            normEvent.TimestampUtc = utc ?? DateTime.MinValue;

            normEvent.ClientIp = FirstNonBlank(Get(flatData, "ClientIP"), Get(flatData, "ClientIPAddress"), Get(flatData, "ActorIpAddress"), Get(row, "ClientIP"), Get(row, "ClientIPAddress"));
            normEvent.ObjectPath = FirstNonBlank(Get(flatData, "ObjectId"), Get(flatData, "ItemId"), Get(flatData, "FileId"), Get(flatData, "SourceRelativeUrl"), Get(flatData, "DestinationRelativeUrl"), Get(row, "ObjectId"));

            normEvent.AdditionalFields["ParserName"] = ParserName;
            normEvent.AdditionalFields["ArtifactType"] = "O365 UAL";

            if (!string.IsNullOrWhiteSpace(jsonBlob))
                normEvent.AdditionalFields["AuditData"] = jsonBlob;

            foreach (var kvp in flatData)
            {
                if (!normEvent.AdditionalFields.ContainsKey(kvp.Key))
                    normEvent.AdditionalFields[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in row)
            {
                if (!normEvent.AdditionalFields.ContainsKey(kvp.Key))
                    normEvent.AdditionalFields[kvp.Key] = kvp.Value;
            }

            yield return normEvent;
        }
    }

    private static string Get(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}
