using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

internal static class DatabaseIngest
{
    private static readonly HashSet<string> CoreColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "RecordId", "CreationDate", "UserId", "Operation", "Workload", "Category",
        "ClientIP", "ClientIPAddress", "ActorIpAddress", "UserAgent", "ObjectId",
        "ItemId", "FileId", "SiteUrl", "SourceRelativeUrl", "DestinationRelativeUrl",
        "FileName", "SourceFileName", "DestinationFileName", "FileSizeBytes",
        "ExchangeMetaData_FileSize", "Recipients", "Recipient", "To", "CC", "BCC",
        "AttachmentDetails", "ResultStatus", "AuditData"
    };

    public static IngestResult ImportParsedCsv(string csvPath, string dbPath, string localTzName, Action<string> log)
    {
        var parser = new O365UalParser();
        return ImportParsedFile(parser, csvPath, dbPath, localTzName, append: true, log);
    }

    public static IngestResult ImportParsedFile(IArtifactParser parser, string filePath, string dbPath, string localTzName, bool append, Action<string> log)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Parsed file not found.", filePath);

        // Ensure connection and table structures exist.
        DatabaseCore.InitializeDatabase(dbPath);
        using var conn = DatabaseCore.Open(dbPath);
        
        if (!append)
        {
            using var clearCmd = conn.CreateCommand();
            clearCmd.CommandText = "DELETE FROM events; DELETE FROM event_fields; DELETE FROM risk_hits;";
            clearCmd.ExecuteNonQuery();
            log("Cleared existing database records.");
        }

        using var tx = conn.BeginTransaction();

        using var insertEvent = conn.CreateCommand();
        insertEvent.Transaction = tx;
        insertEvent.CommandText = @"
INSERT INTO events (
    data_source, record_id, creation_date_utc, creation_date_local, event_time_basis, event_time_confidence, is_behavioral_timestamp, timestamp_warning, user_id, operation, workload, category,
    client_ip, client_ip_alt, user_agent, object_id, site_url, source_relative_url, file_name,
    file_size_bytes, recipients, attachment_details, result_status, raw_json, source_file, source_row_number
)
VALUES (
    $data_source, $record_id, $creation_date_utc, $creation_date_local, $event_time_basis, $event_time_confidence, $is_behavioral_timestamp, $timestamp_warning, $user_id, $operation, $workload, $category,
    $client_ip, $client_ip_alt, $user_agent, $object_id, $site_url, $source_relative_url, $file_name,
    $file_size_bytes, $recipients, $attachment_details, $result_status, $raw_json, $source_file, $source_row_number
);
SELECT last_insert_rowid();
";

        using var insertField = conn.CreateCommand();
        insertField.Transaction = tx;
        insertField.CommandText = @"
INSERT INTO event_fields (event_id, field_name, field_value)
VALUES ($event_id, $field_name, $field_value);
";

        var rowCount = 0;

        foreach (var normEvent in parser.Parse(filePath, localTzName, log))
        {
            rowCount++;
            var fields = normEvent.AdditionalFields;

            var recordId = Get(fields, "RecordId");
            var creationDateUtc = normEvent.TimestampUtc == DateTime.MinValue ? string.Empty : normEvent.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var creationDateLocal = Get(fields, "CreationDate");
            if (string.IsNullOrWhiteSpace(creationDateLocal) && normEvent.TimestampUtc != DateTime.MinValue)
            {
                var tz = TimeUtil.EnsureTimeZone(localTzName);
                creationDateLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(normEvent.TimestampUtc, DateTimeKind.Utc), tz).ToString("MM/dd/yyyy h:mm:ss tt");
            }

            var timeBasis = FirstNonBlank(normEvent.EventTimeBasis, Get(fields, "EventTimeBasis"), "O365UALCreationDate");
            var timeConfidence = FirstNonBlank(normEvent.EventTimeConfidence, Get(fields, "EventTimeConfidence"), normEvent.TimestampUtc == DateTime.MinValue ? "Unknown" : "High");
            var isBehavioral = normEvent.TimestampUtc != DateTime.MinValue;
            var timeWarning = normEvent.TimestampUtc == DateTime.MinValue ? "UAL row had no parseable CreationDate." : string.Empty;
            fields["EventTimeBasis"] = timeBasis;
            fields["EventTimeConfidence"] = timeConfidence;
            fields["IsBehavioralTimestamp"] = isBehavioral ? "Yes" : "No";
            if (!string.IsNullOrWhiteSpace(timeWarning)) fields["TimestampWarning"] = timeWarning;

            var dataSource = normEvent.DataSource ?? string.Empty;
            var workload = FirstNonBlank(Get(fields, "Workload"), dataSource.StartsWith("O365", StringComparison.OrdinalIgnoreCase) || dataSource.StartsWith("Google", StringComparison.OrdinalIgnoreCase) || dataSource.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "Cloud" : "Endpoint");
            var category = FirstNonBlank(Get(fields, "Category"), Get(fields, "EventCategory"));
            var clientIp = !string.IsNullOrWhiteSpace(normEvent.ClientIp) ? normEvent.ClientIp : FirstNonBlank(Get(fields, "ClientIP"), Get(fields, "ClientIPAddress"), Get(fields, "ActorIpAddress"), Get(fields, "IP address"), Get(fields, "IP Address"));
            var clientIpAlt = Get(fields, "ClientIPAddress");
            var userAgent = FirstNonBlank(Get(fields, "UserAgent"), Get(fields, "User agent"), Get(fields, "User Agent"), Get(fields, "User Agent String"));
            var objectId = !string.IsNullOrWhiteSpace(normEvent.ObjectPath) ? normEvent.ObjectPath : FirstNonBlank(Get(fields, "ObjectId"), Get(fields, "ItemId"), Get(fields, "FileId"), Get(fields, "Document ID"), Get(fields, "Title"), Get(fields, "Target"), Get(fields, "URL"), Get(fields, "Subject"));
            var siteUrl = FirstNonBlank(Get(fields, "SiteUrl"), Get(fields, "Site URL"), Get(fields, "URL"), Get(fields, "Resource Url"), Get(fields, "Attachment URL"));
            var sourceRelativeUrl = FirstNonBlank(Get(fields, "TargetPath"), Get(fields, "SourceRelativeUrl"), Get(fields, "DestinationRelativeUrl"), Get(fields, "URL"), Get(fields, "Target"));
            var fileName = FirstNonBlank(Get(fields, "FileName"), Get(fields, "SourceFileName"), Get(fields, "DestinationFileName"), Get(fields, "Filename"), Get(fields, "Attachment name"), Get(fields, "Title"), SafeFileNameFromPath(objectId));
            var fileSize = TryParseLong(FirstNonBlank(Get(fields, "FileSizeBytes"), Get(fields, "ExchangeMetaData_FileSize"), Get(fields, "Target File Size (Bytes)")));
            var recipients = BuildRecipients(fields);
            var attachmentDetails = BuildAttachments(fields);
            var resultStatus = Get(fields, "ResultStatus");
            var rawJson = Get(fields, "AuditData");

            insertEvent.Parameters.Clear();
            insertEvent.Parameters.AddWithValue("$data_source", dataSource);
            insertEvent.Parameters.AddWithValue("$record_id", recordId);
            insertEvent.Parameters.AddWithValue("$creation_date_utc", creationDateUtc);
            insertEvent.Parameters.AddWithValue("$creation_date_local", creationDateLocal);
            insertEvent.Parameters.AddWithValue("$event_time_basis", timeBasis);
            insertEvent.Parameters.AddWithValue("$event_time_confidence", timeConfidence);
            insertEvent.Parameters.AddWithValue("$is_behavioral_timestamp", isBehavioral ? 1 : 0);
            insertEvent.Parameters.AddWithValue("$timestamp_warning", timeWarning);
            insertEvent.Parameters.AddWithValue("$user_id", normEvent.UserId);
            insertEvent.Parameters.AddWithValue("$operation", normEvent.Operation);
            insertEvent.Parameters.AddWithValue("$workload", workload);
            insertEvent.Parameters.AddWithValue("$category", category);
            insertEvent.Parameters.AddWithValue("$client_ip", clientIp);
            insertEvent.Parameters.AddWithValue("$client_ip_alt", clientIpAlt);
            insertEvent.Parameters.AddWithValue("$user_agent", userAgent);
            insertEvent.Parameters.AddWithValue("$object_id", objectId);
            insertEvent.Parameters.AddWithValue("$site_url", siteUrl);
            insertEvent.Parameters.AddWithValue("$source_relative_url", sourceRelativeUrl);
            insertEvent.Parameters.AddWithValue("$file_name", fileName);
            insertEvent.Parameters.AddWithValue("$file_size_bytes", (object?)fileSize ?? DBNull.Value);
            insertEvent.Parameters.AddWithValue("$recipients", recipients);
            insertEvent.Parameters.AddWithValue("$attachment_details", attachmentDetails);
            insertEvent.Parameters.AddWithValue("$result_status", resultStatus);
            insertEvent.Parameters.AddWithValue("$raw_json", rawJson);
            insertEvent.Parameters.AddWithValue("$source_file", Path.GetFileName(filePath));
            insertEvent.Parameters.AddWithValue("$source_row_number", rowCount);

            var eventId = (long)(insertEvent.ExecuteScalar() ?? 0L);

            foreach (var kvp in fields)
            {
                if (CoreColumns.Contains(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;

                insertField.Parameters.Clear();
                insertField.Parameters.AddWithValue("$event_id", eventId);
                insertField.Parameters.AddWithValue("$field_name", kvp.Key);
                insertField.Parameters.AddWithValue("$field_value", kvp.Value);
                insertField.ExecuteNonQuery();
            }

            if (rowCount % 10000 == 0)
                log($"  … imported {rowCount:N0} rows");
        }

        tx.Commit();
        log($"  ✓ Imported {rowCount:N0} rows into database.");

        return new IngestResult
        {
            RowsImported = rowCount,
            DatabasePath = dbPath
        };
    }

    private static string Get(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value ?? "" : "";

    private static string FirstNonBlank(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static long? TryParseLong(string s)
        => long.TryParse(s, out var v) ? v : null;

    private static string BuildRecipients(Dictionary<string, string> row)
    {
        var recipients = new List<string>();

        foreach (var kvp in row)
        {
            var name = kvp.Key;

            if (name.Equals("Recipients", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("To", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CC", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("BCC", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Recipient", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                    recipients.Add(kvp.Value.Trim());
            }
        }

        return string.Join("; ", recipients.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string SafeFileNameFromPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.LocalPath))
                return Path.GetFileName(uri.LocalPath);

            return Path.GetFileName(value.TrimEnd('\\', '/'));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildAttachments(Dictionary<string, string> row)
    {
        var parts = new List<string>();

        foreach (var kvp in row)
        {
            if (kvp.Key.Contains("Attachment", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kvp.Value))
            {
                parts.Add(kvp.Value.Trim());
            }
        }

        return string.Join("; ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}