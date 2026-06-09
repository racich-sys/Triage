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

        using var insertGoogleRawField = conn.CreateCommand();
        insertGoogleRawField.Transaction = tx;
        insertGoogleRawField.CommandText = @"
INSERT INTO google_event_raw_fields (event_id, raw_family, field_name, field_value, source_file, source_row_number)
VALUES ($event_id, $raw_family, $field_name, $field_value, $source_file, $source_row_number);
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
            var isBehavioral = normEvent.IsBehavioralTimestamp ?? (normEvent.TimestampUtc != DateTime.MinValue);
            var timeWarning = FirstNonBlank(normEvent.TimestampWarning, Get(fields, "TimestampWarning"), normEvent.TimestampUtc == DateTime.MinValue ? "UAL row had no parseable CreationDate." : string.Empty);
            fields["EventTimeBasis"] = timeBasis;
            fields["EventTimeConfidence"] = timeConfidence;
            fields["IsBehavioralTimestamp"] = isBehavioral ? "Yes" : "No";
            if (!string.IsNullOrWhiteSpace(timeWarning)) fields["TimestampWarning"] = timeWarning;

            var dataSource = normEvent.DataSource ?? string.Empty;
            var isGoogleSource = dataSource.StartsWith("Google", StringComparison.OrdinalIgnoreCase) || dataSource.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || normEvent.Operation.StartsWith("Google", StringComparison.OrdinalIgnoreCase) || normEvent.Operation.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase);
            var workload = isGoogleSource
                ? FirstNonBlank(Get(fields, "GoogleWorkload"), "Cloud")
                : FirstNonBlank(Get(fields, "Workload"), dataSource.StartsWith("O365", StringComparison.OrdinalIgnoreCase) ? "Cloud" : "Endpoint");
            var category = isGoogleSource ? FirstNonBlank(Get(fields, "GoogleCategory"), Get(fields, "GoogleEventCategory")) : FirstNonBlank(Get(fields, "Category"), Get(fields, "EventCategory"));
            var clientIp = !string.IsNullOrWhiteSpace(normEvent.ClientIp) ? normEvent.ClientIp : (isGoogleSource ? FirstNonBlank(Get(fields, "GoogleClientIp"), Get(fields, "GoogleAuditRaw_IP_address"), Get(fields, "GoogleAuditRaw_IP_Address"), Get(fields, "GoogleTakeoutRaw_IP_Address")) : FirstNonBlank(Get(fields, "ClientIP"), Get(fields, "ClientIPAddress"), Get(fields, "ActorIpAddress"), Get(fields, "IP address"), Get(fields, "IP Address")));
            var clientIpAlt = isGoogleSource ? FirstNonBlank(Get(fields, "GoogleClientIpAlt"), Get(fields, "GoogleClientIp")) : Get(fields, "ClientIPAddress");
            var userAgent = isGoogleSource ? FirstNonBlank(Get(fields, "GoogleUserAgent"), Get(fields, "GoogleAuditRaw_User_agent"), Get(fields, "GoogleAuditRaw_User_Agent"), Get(fields, "GoogleTakeoutRaw_User_Agent_String")) : FirstNonBlank(Get(fields, "UserAgent"), Get(fields, "User agent"), Get(fields, "User Agent"), Get(fields, "User Agent String"));
            var objectId = !string.IsNullOrWhiteSpace(normEvent.ObjectPath) ? normEvent.ObjectPath : (isGoogleSource ? FirstNonBlank(Get(fields, "GoogleTarget"), Get(fields, "GoogleStableObjectId"), Get(fields, "GoogleDisplayTarget")) : FirstNonBlank(Get(fields, "ObjectId"), Get(fields, "ItemId"), Get(fields, "FileId"), Get(fields, "Document ID"), Get(fields, "Title"), Get(fields, "Target"), Get(fields, "URL"), Get(fields, "Subject")));
            var siteUrl = isGoogleSource ? FirstNonBlank(Get(fields, "GoogleSiteUrl"), Get(fields, "GoogleTarget")) : FirstNonBlank(Get(fields, "SiteUrl"), Get(fields, "Site URL"), Get(fields, "URL"), Get(fields, "Resource Url"), Get(fields, "Attachment URL"));
            var sourceRelativeUrl = isGoogleSource ? FirstNonBlank(Get(fields, "GoogleSourceRelativeUrl"), Get(fields, "GoogleTarget")) : FirstNonBlank(Get(fields, "TargetPath"), Get(fields, "SourceRelativeUrl"), Get(fields, "DestinationRelativeUrl"), Get(fields, "URL"), Get(fields, "Target"));
            var fileName = isGoogleSource ? FirstNonBlank(Get(fields, "GoogleFileName"), Get(fields, "GoogleTargetFileName"), SafeFileNameFromPath(objectId)) : FirstNonBlank(Get(fields, "FileName"), Get(fields, "SourceFileName"), Get(fields, "DestinationFileName"), Get(fields, "Filename"), Get(fields, "Attachment name"), Get(fields, "Title"), SafeFileNameFromPath(objectId));
            var fileSize = TryParseLong(isGoogleSource ? FirstNonBlank(Get(fields, "GoogleFileSizeBytes")) : FirstNonBlank(Get(fields, "FileSizeBytes"), Get(fields, "ExchangeMetaData_FileSize"), Get(fields, "Target File Size (Bytes)")));
            var recipients = BuildRecipients(fields);
            var attachmentDetails = BuildAttachments(fields);
            var resultStatus = isGoogleSource ? Get(fields, "GoogleResultStatus") : Get(fields, "ResultStatus");
            var rawJson = isGoogleSource ? Get(fields, "GoogleRawSerializedRow") : Get(fields, "AuditData");

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

            var writtenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in fields)
            {
                if (CoreColumns.Contains(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;

                if (isGoogleSource && IsGoogleRawField(kvp.Key))
                {
                    if (ShouldSkipGoogleRawStorageField(kvp.Key, kvp.Value, normEvent, creationDateUtc, workload, category, clientIp, userAgent, objectId, siteUrl, sourceRelativeUrl, fileName, resultStatus))
                        continue;

                    insertGoogleRawField.Parameters.Clear();
                    insertGoogleRawField.Parameters.AddWithValue("$event_id", eventId);
                    insertGoogleRawField.Parameters.AddWithValue("$raw_family", GoogleRawFamily(kvp.Key));
                    insertGoogleRawField.Parameters.AddWithValue("$field_name", kvp.Key);
                    insertGoogleRawField.Parameters.AddWithValue("$field_value", kvp.Value);
                    insertGoogleRawField.Parameters.AddWithValue("$source_file", Path.GetFileName(filePath));
                    insertGoogleRawField.Parameters.AddWithValue("$source_row_number", rowCount);
                    insertGoogleRawField.ExecuteNonQuery();
                    continue;
                }

                if (ShouldSkipGoogleMetadataField(kvp.Key, kvp.Value, isGoogleSource, normEvent, workload, category, clientIp, userAgent, objectId, siteUrl, sourceRelativeUrl, fileName, resultStatus))
                    continue;

                var fieldFingerprint = kvp.Key + "\u001f" + kvp.Value;
                if (!writtenFields.Add(fieldFingerprint))
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


    private static bool IsGoogleRawField(string key)
        => key.StartsWith("GoogleAuditRaw_", StringComparison.OrdinalIgnoreCase) ||
           key.StartsWith("GoogleTakeoutRaw_", StringComparison.OrdinalIgnoreCase);

    private static string GoogleRawFamily(string key)
    {
        if (key.StartsWith("GoogleAuditRaw_", StringComparison.OrdinalIgnoreCase)) return "GoogleAuditRaw";
        if (key.StartsWith("GoogleTakeoutRaw_", StringComparison.OrdinalIgnoreCase)) return "GoogleTakeoutRaw";
        return "GoogleRaw";
    }

    private static bool ShouldSkipGoogleRawStorageField(
        string key,
        string value,
        NormalizedEvent ev,
        string creationDateUtc,
        string workload,
        string category,
        string clientIp,
        string userAgent,
        string objectId,
        string siteUrl,
        string sourceRelativeUrl,
        string fileName,
        string resultStatus)
    {
        // Google raw fields remain available for unmapped-column review, but raw
        // source columns that are already promoted into the event row or reviewed
        // Google canonical metadata do not need to be stored again per event.
        // This keeps thin-test databases smaller while preserving the source
        // schema-separation principle from v3.6.x.
        var rawName = key;
        if (rawName.StartsWith("GoogleAuditRaw_", StringComparison.OrdinalIgnoreCase))
            rawName = rawName.Substring("GoogleAuditRaw_".Length);
        else if (rawName.StartsWith("GoogleTakeoutRaw_", StringComparison.OrdinalIgnoreCase))
            rawName = rawName.Substring("GoogleTakeoutRaw_".Length);

        var canonical = NormalizeRawFieldName(rawName);

        if (canonical.Length == 0)
            return true;

        if (canonical is "date" or "timestamp" or "time" or "created" or "modified" or "activitytimestamp")
            return true;

        // v3.21.0: remove raw Google fields that were shown by the prior
        // thin run to be high-volume, low-cardinality, and already represented
        // by normalized event columns, indexed Google summaries, or the event
        // raw JSON/source row. This reduces google_event_raw_fields without
        // deleting the source-row reconstruction path.
        if (ShouldSkipLowInformationGoogleRawField(canonical, value))
            return true;

        if (canonical is "actor" or "user" or "email" or "owner")
            return SameText(value, ev.UserId);

        if (canonical is "event" or "eventname" or "name" or "operation" or "activity")
            return true;

        if (canonical is "description" or "target" or "title" or "url" or "doc_title" or "documenttitle" or "resource" or "itemname")
            return true;

        if (canonical is "ipaddress" or "ip" or "clientip" or "sourceip")
            return SameText(value, clientIp);

        if (canonical is "useragent" or "useragentstring")
            return SameText(value, userAgent);

        if (canonical is "applicationname" or "appname" or "product" or "service")
            return SameText(value, workload) || SameText(value, category);

        if (canonical is "result" or "resultstatus" or "status")
            return SameText(value, resultStatus);

        if (canonical is "recurringevent" or "clientsideencrypted" or "notificationmethod" or "notificationtype")
            return true;

        if (canonical is "doctype" or "documenttype" or "mimetype" or "visibility" or "oldvalue" or "newvalue" or "scope" or "oauthclientid" or "clienttype" or "networkinfo" or "serviceaccount" or "apikind" or "accesslevel" or "calendarid" or "eventid" or "eventtype" or "guestresponsestatus")
            return false;

        return false;
    }

    private static string NormalizeRawFieldName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var chars = key.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static bool ShouldSkipGoogleMetadataField(
        string key,
        string value,
        bool isGoogleSource,
        NormalizedEvent ev,
        string workload,
        string category,
        string clientIp,
        string userAgent,
        string objectId,
        string siteUrl,
        string sourceRelativeUrl,
        string fileName,
        string resultStatus)
    {
        if (!isGoogleSource)
            return false;

        if (key.Equals("GoogleRawSerializedRow", StringComparison.OrdinalIgnoreCase))
            return true;

        // v3.21.0: keep repeated Google low-cardinality values out of the
        // generic indexed event_fields table when the same meaning is already
        // stored in the events row, source/provenance columns, or validation
        // summaries. This preserves forensic reviewability while preventing
        // high-volume Google Activity/Audit rows from dominating indexed
        // metadata storage.
        if (ShouldSkipLowCardinalityGoogleIndexedField(key, value, ev, category))
            return true;

        if (key.Equals("GoogleMasterExportSchema", StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.StartsWith("GoogleRisk", StringComparison.OrdinalIgnoreCase) && value.Equals("No", StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.Equals("GoogleUserId", StringComparison.OrdinalIgnoreCase) || key.Equals("GoogleActor", StringComparison.OrdinalIgnoreCase))
            return SameText(value, ev.UserId);

        if (key.Equals("GoogleClientIp", StringComparison.OrdinalIgnoreCase) || key.Equals("GoogleClientIpAlt", StringComparison.OrdinalIgnoreCase))
            return SameText(value, clientIp);

        if (key.Equals("GoogleUserAgent", StringComparison.OrdinalIgnoreCase))
            return SameText(value, userAgent);

        if (key.Equals("GoogleOperationNormalized", StringComparison.OrdinalIgnoreCase))
            return SameText(value, ev.Operation);

        if (key.Equals("GoogleWorkload", StringComparison.OrdinalIgnoreCase))
            return SameText(value, workload);

        if (key.Equals("GoogleCategory", StringComparison.OrdinalIgnoreCase))
            return SameText(value, category);

        if (key.Equals("GoogleTarget", StringComparison.OrdinalIgnoreCase) || key.Equals("GoogleDisplayTarget", StringComparison.OrdinalIgnoreCase))
            return SameText(value, objectId);

        if (key.Equals("GoogleSiteUrl", StringComparison.OrdinalIgnoreCase))
            return SameText(value, siteUrl) || SameText(value, objectId);

        if (key.Equals("GoogleSourceRelativeUrl", StringComparison.OrdinalIgnoreCase))
            return SameText(value, sourceRelativeUrl) || SameText(value, objectId);

        if (key.Equals("GoogleFileName", StringComparison.OrdinalIgnoreCase) || key.Equals("GoogleTargetFileName", StringComparison.OrdinalIgnoreCase))
            return SameText(value, fileName);

        if (key.Equals("GoogleFileSizeBytes", StringComparison.OrdinalIgnoreCase))
            return false;

        if (key.Equals("GoogleResultStatus", StringComparison.OrdinalIgnoreCase))
            return SameText(value, resultStatus);

        return false;
    }

    private static bool ShouldSkipLowInformationGoogleRawField(string canonical, string value)
    {
        if (string.IsNullOrWhiteSpace(canonical))
            return true;

        var trimmed = (value ?? string.Empty).Trim();
        var lowInformationValue = trimmed.Length == 0 ||
            trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("No", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("0.0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Encryption not required", StringComparison.OrdinalIgnoreCase);

        if (lowInformationValue && (canonical is "isnonroutableipaddress" or "billable" or "encrypted" or "impersonation" or "visitor"))
            return true;

        if (lowInformationValue && (canonical is
            "copytype" or "deletionreason" or "documenttype" or "encryptionpolicy" or
            "encryptionchange" or "executiontrigger" or "membershipchangetype" or
            "newpublishvisibilityvalue" or "oldpublishvisibilityvalue" or "querytype" or
            "requestedaccessrole" or "scriptcontainerapp" or "scripttriggersourceapp" or
            "scripttriggertype" or "settingschangetype" or "visibility" or
            "esignaturereviewerdecision" or "esignaturestatus" or "confidentialmode" or
            "spamclassification" or "spamclassificationreason" or "trafficsource" or
            "clienttype" or "configurationsource" or "accesslevel" or "eventtype" or
            "guestresponsestatus" or "apikind"))
            return true;

        if (canonical is "productname" or "subproductname")
            return true;

        return false;
    }

    private static bool ShouldSkipLowCardinalityGoogleIndexedField(string key, string value, NormalizedEvent ev, string category)
    {
        if (key.Equals("EventTimeBasis", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("EventTimeConfidence", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("IsBehavioralTimestamp", StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.Equals("ArtifactType", StringComparison.OrdinalIgnoreCase))
            return ev.DataSource.StartsWith("Google", StringComparison.OrdinalIgnoreCase) ||
                   ev.DataSource.Contains("Gemini", StringComparison.OrdinalIgnoreCase);

        if (key.Equals("GoogleEventCategory", StringComparison.OrdinalIgnoreCase))
            return SameText(value, category);

        if (key.Equals("GoogleOperationRaw", StringComparison.OrdinalIgnoreCase))
            return SameText(value, ev.Operation) || NormalizeRawFieldName(value) == NormalizeRawFieldName(ev.Operation);

        if (key.Equals("GoogleRecordType", StringComparison.OrdinalIgnoreCase))
            return value.Equals("GoogleTakeout", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("GoogleWorkspaceAudit", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("GeminiSession", StringComparison.OrdinalIgnoreCase);

        if (key.Equals("GoogleIPClassification", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("GoogleNetworkType", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool SameText(string? a, string? b)
        => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

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