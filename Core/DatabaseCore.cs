using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

public static class DatabaseCore
{
    private static readonly object InitializationLock = new();
    private static readonly HashSet<string> InitializedDatabasePaths = new(StringComparer.OrdinalIgnoreCase);

    public static SqliteConnection Open(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is blank.", nameof(dbPath));

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 60
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        ApplyConnectionPragmas(conn);
        return conn;
    }

    private static void ApplyConnectionPragmas(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA busy_timeout = 120000;
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA cache_size = -64000;
            ";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort performance settings. Case access must not fail because of a PRAGMA.
        }
    }

    public static void InitializeDatabase(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is blank.", nameof(dbPath));

        var fullPath = Path.GetFullPath(dbPath);
        lock (InitializationLock)
        {
            if (InitializedDatabasePaths.Contains(fullPath))
                return;

            using var conn = Open(fullPath);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    data_source TEXT DEFAULT '',
                    record_id TEXT DEFAULT '',
                    creation_date_utc TEXT DEFAULT '',
                    creation_date_local TEXT DEFAULT '',
                    event_time_basis TEXT DEFAULT '',
                    event_time_confidence TEXT DEFAULT '',
                    is_behavioral_timestamp INTEGER DEFAULT 1,
                    timestamp_warning TEXT DEFAULT '',
                    user_id TEXT DEFAULT '',
                    operation TEXT DEFAULT '',
                    workload TEXT DEFAULT '',
                    category TEXT DEFAULT '',
                    client_ip TEXT DEFAULT '',
                    client_ip_alt TEXT DEFAULT '',
                    user_agent TEXT DEFAULT '',
                    object_id TEXT DEFAULT '',
                    site_url TEXT DEFAULT '',
                    source_relative_url TEXT DEFAULT '',
                    file_name TEXT DEFAULT '',
                    file_size_bytes INTEGER,
                    recipients TEXT DEFAULT '',
                    attachment_details TEXT DEFAULT '',
                    result_status TEXT DEFAULT '',
                    raw_json TEXT DEFAULT '',
                    source_file TEXT DEFAULT '',
                    source_row_number INTEGER DEFAULT 0,
                    forensic_status TEXT DEFAULT '',
                    risk_score INTEGER DEFAULT 0,
                    risk_level TEXT DEFAULT 'Low'
                );

                CREATE TABLE IF NOT EXISTS event_fields (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER NOT NULL,
                    field_name TEXT NOT NULL,
                    field_value TEXT DEFAULT '',
                    FOREIGN KEY(event_id) REFERENCES events(event_id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS risk_hits (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER NOT NULL,
                    rule_code TEXT DEFAULT '',
                    rule_name TEXT DEFAULT '',
                    risk_domain TEXT DEFAULT '',
                    risk_score INTEGER DEFAULT 0,
                    risk_level TEXT DEFAULT 'Low',
                    reason TEXT DEFAULT '',
                    supporting_value TEXT DEFAULT '',
                    created_utc TEXT DEFAULT '',
                    FOREIGN KEY(event_id) REFERENCES events(event_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_events_creation_date ON events(creation_date_utc);
                CREATE INDEX IF NOT EXISTS ix_events_behavioral_time ON events(is_behavioral_timestamp, creation_date_utc);
                CREATE INDEX IF NOT EXISTS ix_events_user_operation ON events(user_id, operation);
                CREATE INDEX IF NOT EXISTS ix_events_source_file ON events(source_file);
                CREATE INDEX IF NOT EXISTS ix_event_fields_event_name ON event_fields(event_id, field_name);
                CREATE INDEX IF NOT EXISTS ix_risk_hits_event ON risk_hits(event_id);
                CREATE TABLE IF NOT EXISTS tags (
                    tag_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tag_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    description TEXT DEFAULT '',
                    color_hex TEXT DEFAULT '',
                    created_utc TEXT DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS event_tags (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER NOT NULL,
                    tag_id INTEGER NOT NULL,
                    source_context TEXT DEFAULT '',
                    notes TEXT DEFAULT '',
                    created_utc TEXT DEFAULT '',
                    UNIQUE(event_id, tag_id),
                    FOREIGN KEY(event_id) REFERENCES events(event_id) ON DELETE CASCADE,
                    FOREIGN KEY(tag_id) REFERENCES tags(tag_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_risk_hits_score ON risk_hits(risk_score);
                CREATE INDEX IF NOT EXISTS ix_tags_name ON tags(tag_name);
                CREATE INDEX IF NOT EXISTS ix_event_tags_event ON event_tags(event_id);
                CREATE INDEX IF NOT EXISTS ix_event_tags_tag ON event_tags(tag_id);
            ";
            cmd.ExecuteNonQuery();
        }

        EnsureEventColumns(conn);
        EnsureRiskHitColumns(conn);
        EnsureTagTables(conn);
        NormalizeLegacyTimestampFlags(conn);
        RebuildViews(conn);
        EnsurePerformanceIndexes(conn);
            InitializedDatabasePaths.Add(fullPath);
        }
    }

    private static void EnsurePerformanceIndexes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS ix_events_creation_user ON events(creation_date_utc, user_id);
            CREATE INDEX IF NOT EXISTS ix_events_user_time ON events(user_id, creation_date_utc);
            CREATE INDEX IF NOT EXISTS ix_events_operation_time ON events(operation, creation_date_utc);
            CREATE INDEX IF NOT EXISTS ix_events_source_time ON events(data_source, creation_date_utc);
            CREATE INDEX IF NOT EXISTS ix_events_file_name ON events(file_name);
            CREATE INDEX IF NOT EXISTS ix_events_risk ON events(risk_level, risk_score DESC);
            CREATE INDEX IF NOT EXISTS ix_event_fields_name_value ON event_fields(field_name, field_value);
            CREATE INDEX IF NOT EXISTS ix_event_fields_value ON event_fields(field_value);
            CREATE INDEX IF NOT EXISTS ix_risk_hits_level_score ON risk_hits(risk_level, risk_score DESC);
            CREATE INDEX IF NOT EXISTS ix_risk_hits_domain_score ON risk_hits(risk_domain, risk_score DESC);
            CREATE INDEX IF NOT EXISTS ix_risk_hits_rule ON risk_hits(rule_code);
            CREATE INDEX IF NOT EXISTS ix_event_tags_tag_event ON event_tags(tag_id, event_id);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureEventColumns(SqliteConnection conn)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["data_source"] = "TEXT DEFAULT ''",
            ["record_id"] = "TEXT DEFAULT ''",
            ["creation_date_utc"] = "TEXT DEFAULT ''",
            ["creation_date_local"] = "TEXT DEFAULT ''",
            ["event_time_basis"] = "TEXT DEFAULT ''",
            ["event_time_confidence"] = "TEXT DEFAULT ''",
            ["is_behavioral_timestamp"] = "INTEGER DEFAULT 1",
            ["timestamp_warning"] = "TEXT DEFAULT ''",
            ["user_id"] = "TEXT DEFAULT ''",
            ["operation"] = "TEXT DEFAULT ''",
            ["workload"] = "TEXT DEFAULT ''",
            ["category"] = "TEXT DEFAULT ''",
            ["client_ip"] = "TEXT DEFAULT ''",
            ["client_ip_alt"] = "TEXT DEFAULT ''",
            ["user_agent"] = "TEXT DEFAULT ''",
            ["object_id"] = "TEXT DEFAULT ''",
            ["site_url"] = "TEXT DEFAULT ''",
            ["source_relative_url"] = "TEXT DEFAULT ''",
            ["file_name"] = "TEXT DEFAULT ''",
            ["file_size_bytes"] = "INTEGER",
            ["recipients"] = "TEXT DEFAULT ''",
            ["attachment_details"] = "TEXT DEFAULT ''",
            ["result_status"] = "TEXT DEFAULT ''",
            ["raw_json"] = "TEXT DEFAULT ''",
            ["source_file"] = "TEXT DEFAULT ''",
            ["source_row_number"] = "INTEGER DEFAULT 0",
            ["forensic_status"] = "TEXT DEFAULT ''",
            ["risk_score"] = "INTEGER DEFAULT 0",
            ["risk_level"] = "TEXT DEFAULT 'Low'"
        };

        EnsureColumns(conn, "events", columns);
    }


    private static void NormalizeLegacyTimestampFlags(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE events
            SET creation_date_utc = '',
                creation_date_local = '',
                event_time_basis = CASE WHEN IFNULL(event_time_basis, '') = '' THEN 'LegacyMetadataTimestampSuppressed' ELSE event_time_basis END,
                event_time_confidence = 'MetadataOnly',
                is_behavioral_timestamp = 0,
                timestamp_warning = CASE WHEN IFNULL(timestamp_warning, '') = '' THEN 'Legacy metadata/source-file timestamp suppressed during Phase 6 timestamp provenance migration.' ELSE timestamp_warning END
            WHERE
                (data_source LIKE 'Metadata:%' OR result_status = 'MetadataOnly' OR data_source = 'SRUM' OR operation LIKE '%ParseError%')
                AND IFNULL(event_time_basis, '') = '';

            UPDATE events
            SET creation_date_utc = '',
                creation_date_local = '',
                event_time_basis = 'LegacySourceFileTimestampSuppressed',
                event_time_confidence = 'MetadataOnly',
                is_behavioral_timestamp = 0,
                timestamp_warning = CASE WHEN IFNULL(timestamp_warning, '') = '' THEN 'Legacy parser row appeared to use artifact source-file metadata rather than an artifact-native behavioral timestamp.' ELSE timestamp_warning END
            WHERE IFNULL(event_time_basis, '') = ''
              AND event_id IN (
                    SELECT event_id FROM event_fields
                    WHERE field_name IN ('PrefetchLastWriteUtc','ShortcutLastWriteUtc','JumpListLastWriteUtc','HistoryFileLastWriteUtc')
              )
              AND event_id NOT IN (
                    SELECT event_id FROM event_fields
                    WHERE field_name IN ('LastRunUtc','TargetAccessedUtc','DeletedUtc','VisitTimeUtc','StartTimeUtc','RegistryKeyLastWriteUtc')
              );

            UPDATE events
            SET event_time_basis = CASE WHEN IFNULL(event_time_basis, '') = '' THEN 'LegacyBehavioralTimestamp' ELSE event_time_basis END,
                event_time_confidence = CASE WHEN IFNULL(event_time_confidence, '') = '' THEN 'Legacy' ELSE event_time_confidence END,
                is_behavioral_timestamp = CASE WHEN IFNULL(creation_date_utc, '') = '' THEN 0 ELSE is_behavioral_timestamp END
            WHERE IFNULL(event_time_basis, '') = '';
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureRiskHitColumns(SqliteConnection conn)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["event_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["rule_code"] = "TEXT DEFAULT ''",
            ["rule_name"] = "TEXT DEFAULT ''",
            ["risk_domain"] = "TEXT DEFAULT ''",
            ["risk_score"] = "INTEGER DEFAULT 0",
            ["risk_level"] = "TEXT DEFAULT 'Low'",
            ["reason"] = "TEXT DEFAULT ''",
            ["supporting_value"] = "TEXT DEFAULT ''",
            ["created_utc"] = "TEXT DEFAULT ''"
        };

        EnsureColumns(conn, "risk_hits", columns);
    }

    private static void EnsureTagTables(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS tags (
                    tag_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tag_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    description TEXT DEFAULT '',
                    color_hex TEXT DEFAULT '',
                    created_utc TEXT DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS event_tags (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id INTEGER NOT NULL,
                    tag_id INTEGER NOT NULL,
                    source_context TEXT DEFAULT '',
                    notes TEXT DEFAULT '',
                    created_utc TEXT DEFAULT '',
                    UNIQUE(event_id, tag_id),
                    FOREIGN KEY(event_id) REFERENCES events(event_id) ON DELETE CASCADE,
                    FOREIGN KEY(tag_id) REFERENCES tags(tag_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_tags_name ON tags(tag_name);
                CREATE INDEX IF NOT EXISTS ix_event_tags_event ON event_tags(event_id);
                CREATE INDEX IF NOT EXISTS ix_event_tags_tag ON event_tags(tag_id);
            ";
            cmd.ExecuteNonQuery();
        }

        EnsureColumns(conn, "tags", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tag_name"] = "TEXT DEFAULT ''",
            ["description"] = "TEXT DEFAULT ''",
            ["color_hex"] = "TEXT DEFAULT ''",
            ["created_utc"] = "TEXT DEFAULT ''"
        });

        EnsureColumns(conn, "event_tags", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["event_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["tag_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["source_context"] = "TEXT DEFAULT ''",
            ["notes"] = "TEXT DEFAULT ''",
            ["created_utc"] = "TEXT DEFAULT ''"
        });
    }

    private static void EnsureColumns(SqliteConnection conn, string tableName, Dictionary<string, string> requiredColumns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in requiredColumns)
        {
            if (existing.Contains(column.Key))
                continue;

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {column.Key} {column.Value};";
            alter.ExecuteNonQuery();
        }
    }

    private static void RebuildViews(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DROP VIEW IF EXISTS v_master_timeline;

            CREATE VIEW v_master_timeline AS
            SELECT
                e.event_id AS event_id,
                e.event_id AS ID,
                e.risk_score AS Risk_Score,
                e.risk_level AS Risk_Level,
                IFNULL((SELECT group_concat(t.tag_name, '; ') FROM event_tags et JOIN tags t ON t.tag_id = et.tag_id WHERE et.event_id = e.event_id), '') AS Tags,
                e.creation_date_utc AS Date_Time,
                e.creation_date_local AS Local_Date_Time,
                CASE WHEN IFNULL(e.is_behavioral_timestamp, 0) = 1 THEN 'Yes' ELSE 'No' END AS Behavioral_Timestamp,
                e.event_time_basis AS Time_Basis,
                e.event_time_confidence AS Time_Confidence,
                e.timestamp_warning AS Timestamp_Warning,
                e.data_source AS Source,
                e.operation AS Operation,
                e.user_id AS User_Account,
                COALESCE(
                    NULLIF((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('DisplayTarget','TargetPath','OriginalSourcePath','Original Path') AND IFNULL(field_value,'') NOT LIKE '%WorkingEvidence%' LIMIT 1), ''),
                    NULLIF(CASE WHEN e.object_id LIKE '%WorkingEvidence%' THEN '' ELSE e.object_id END, ''),
                    NULLIF(e.source_relative_url, ''),
                    NULLIF(e.file_name, ''),
                    e.object_id
                ) AS Target_Object,
                e.record_id AS RecordId,
                e.workload AS Workload,
                e.category AS Category,
                COALESCE(NULLIF(e.client_ip, ''), NULLIF(e.client_ip_alt, ''), '') AS Client_IP,
                e.user_agent AS User_Agent,
                e.site_url AS Site_Url,
                e.source_relative_url AS Source_Relative_Url,
                e.file_name AS File_Name,
                CASE
                    WHEN e.file_size_bytes IS NOT NULL THEN CAST(e.file_size_bytes AS TEXT)
                    ELSE IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('File Size', 'Size', 'Bytes', 'FileSizeBytes') LIMIT 1), '')
                END AS File_Size,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('Process Name', 'Application', 'App Executed', 'ClientApp') LIMIT 1), '') AS Process_App,
                COALESCE(NULLIF(e.result_status, ''), IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('Result', 'Logon Type', 'Status', 'EventStatus') LIMIT 1), '')) AS Action_Result,
                COALESCE(NULLIF(e.recipients, ''), IFNULL((SELECT group_concat(field_value, '; ') FROM event_fields WHERE event_id = e.event_id AND (field_name LIKE 'EmailInfo_To_%' OR field_name LIKE 'ExchangeMetaData_To_%' OR field_name LIKE 'EmailInfo_Cc_%' OR field_name LIKE 'ExchangeMetaData_CC_%' OR field_name LIKE 'EmailInfo_Bcc_%' OR field_name LIKE 'ExchangeMetaData_BCC_%' OR field_name IN ('Recipients', 'Recipient', 'To', 'TargetUser'))), '')) AS To_Recipient,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('EmailInfo_From', 'ExchangeMetaData_From', 'Sender', 'From') LIMIT 1), '') AS From_Recipient,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('Subject', 'ItemName', 'EmailInfo_Subject', 'ExchangeMetaData_Subject', 'Item_Subject', 'AffectedItems_0_Subject') LIMIT 1), '') AS Subject,
                COALESCE(NULLIF(e.attachment_details, ''), IFNULL((SELECT group_concat(field_value, '; ') FROM event_fields WHERE event_id = e.event_id AND field_name LIKE '%Attachment%'), '')) AS Attachments,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('DriveType', 'Drive Type') LIMIT 1), '') AS Drive_Type,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'DeviceInstanceId' LIMIT 1), '') AS Device_Instance_ID,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'UsbSerialNumber' LIMIT 1), '') AS USB_Serial,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'UsbVID' LIMIT 1), '') AS USB_VID,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'UsbPID' LIMIT 1), '') AS USB_PID,
                COALESCE(NULLIF((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'UsbVendor' LIMIT 1), ''), IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'Manufacturer' LIMIT 1), '')) AS Device_Manufacturer,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'UrlCategory' LIMIT 1), '') AS Url_Category,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'UrlHost' LIMIT 1), '') AS Url_Host,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'IsCloudStorageUrl' LIMIT 1), '') AS Cloud_Storage_Url,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'IsPersonalEmailUrl' LIMIT 1), '') AS Personal_Email_Url,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name IN ('IsLocalFileUrl','IsLocalHostUrl','IsFileExplorerLocalAccess') AND field_value = 'Yes' LIMIT 1), '') AS Local_File_Or_Localhost_Url,
                e.forensic_status AS Forensic_Status,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'ArtifactType' LIMIT 1), '') AS Artifact_Type,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'ParserConfidence' LIMIT 1), '') AS Parser_Confidence,
                IFNULL((SELECT field_value FROM event_fields WHERE event_id = e.event_id AND field_name = 'ParserConfidenceBasis' LIMIT 1), '') AS Parser_Confidence_Basis,
                e.source_file AS Source_File,
                e.source_row_number AS Source_Row
            FROM events e;
        ";
        cmd.ExecuteNonQuery();
    }

    private const string PreferredMasterMetadataExportHeaderText = "RecordId\tCreationDate\tRecordType\tOperation\tUserId\tActivity\tActivityId\tActivityParameters\tActorContextId\tActorInfoString\tActorIpAddress\tActor_0_ID\tActor_0_Type\tActor_1_ID\tActor_1_Type\tAdditionalInfo_EnvironmentName\tAffectedItems_0_Attachments\tAffectedItems_0_Id\tAffectedItems_0_InternetMessageId\tAffectedItems_0_ParentFolder_Id\tAffectedItems_0_ParentFolder_Path\tAffectedItems_0_Subject\tAffectedItems_1_Attachments\tAffectedItems_1_Id\tAffectedItems_1_InternetMessageId\tAffectedItems_1_ParentFolder_Id\tAffectedItems_1_ParentFolder_Path\tAffectedItems_1_Subject\tAffectedItems_2_Attachments\tAffectedItems_2_Id\tAffectedItems_2_InternetMessageId\tAffectedItems_2_ParentFolder_Id\tAffectedItems_2_ParentFolder_Path\tAffectedItems_2_Subject\tAffectedItems_3_Attachments\tAffectedItems_3_Id\tAffectedItems_3_InternetMessageId\tAffectedItems_3_ParentFolder_Id\tAffectedItems_3_ParentFolder_Path\tAffectedItems_3_Subject\tAffectedItems_4_Attachments\tAffectedItems_4_Id\tAffectedItems_4_InternetMessageId\tAffectedItems_4_ParentFolder_Id\tAffectedItems_4_ParentFolder_Path\tAffectedItems_4_Subject\tAppAccessContext_AADSessionId\tAppAccessContext_APIId\tAppAccessContext_ApiId\tAppAccessContext_AuthTime\tAppAccessContext_ClientAppId\tAppAccessContext_ClientAppName\tAppAccessContext_CorrelationId\tAppAccessContext_DeviceId\tAppAccessContext_IssuedAtTime\tAppAccessContext_TokenIssuedAtTime\tAppAccessContext_UniqueTokenId\tAppAccessContext_UserObjectId\tAppId\tAppName\tAppPoolName\tAppReportId\tApplicationDisplayName\tApplicationId\tArtifactId\tArtifactKind\tArtifactName\tArtifactsShared_0_ArtifactShareSessions_0_EndTimestamp\tArtifactsShared_0_ArtifactShareSessions_0_ScreenShareId\tArtifactsShared_0_ArtifactShareSessions_0_StartTimestamp\tArtifactsShared_0_ArtifactShareSessions_1_EndTimestamp\tArtifactsShared_0_ArtifactShareSessions_1_ScreenShareId\tArtifactsShared_0_ArtifactShareSessions_1_StartTimestamp\tArtifactsShared_0_ArtifactSharedName\tAssertingApplicationId\tAttendees_0_DisplayName\tAttendees_0_InviterInfo_DisplayName\tAttendees_0_InviterInfo_InviteTime\tAttendees_0_InviterInfo_OrganizationId\tAttendees_0_InviterInfo_UPN\tAttendees_0_InviterInfo_UserIdentifier\tAttendees_0_InviterInfo_UserType\tAttendees_0_IsAADGuest\tAttendees_0_IsOrganizer\tAttendees_0_OrganizationId\tAttendees_0_ProviderType\tAttendees_0_RecipientType\tAttendees_0_Role\tAttendees_0_UPN\tAttendees_0_UserIdType\tAttendees_0_UserObjectId\tAuthType\tAuthenticationType\tAzureActiveDirectoryEventType\tBillingType\tBrowserName\tBrowserVersion\tCallId\tCapacityId\tCapacityName\tChatName\tChatThreadId\tClientAppId\tClientIP\tClientIPAddress\tClientInfoString\tClientProcessName\tClientRequestId\tClientVersion\tCommunicationSubType\tCommunicationType\tConferenceUri\tConsumptionMethod\tContactEmail1DisplayName\tContactEmail1EmailAddress\tContainerId\tContainerType\tCorrelationID\tCorrelationId\tCreationTime\tCrossMailboxOperation\tCrossScopeSyncDelete\tCustomUniqueId\tCustomizedDoclib\tDatasetId\tDatasetName\tDestFolder_Id\tDestFolder_Path\tDestinationFileExtension\tDestinationFileName\tDestinationRelativeUrl\tDeviceDisplayName\tDeviceId\tDeviceInformation\tDeviceProperties_0_Name\tDeviceProperties_0_Value\tDeviceProperties_1_Name\tDeviceProperties_1_Value\tDeviceProperties_2_Name\tDeviceProperties_2_Value\tDeviceProperties_3_Name\tDeviceProperties_3_Value\tDeviceProperties_4_Name\tDeviceProperties_4_Value\tDeviceProperties_5_Name\tDeviceProperties_5_Value\tDeviceProperties_6_Name\tDeviceProperties_6_Value\tDeviceProperties_7_Name\tDeviceProperties_7_Value\tDistributionMethod\tDoNotDistributeEvent\tEndTime\tErrorNumber\tEventData\tEventSignature\tEventSource\tExchangeId\tExtendedProperties_0_Name\tExtendedProperties_0_Value\tExtendedProperties_1_Name\tExtendedProperties_1_Value\tExtendedProperties_2_Name\tExtendedProperties_2_Value\tExtendedProperties_3_Name\tExtendedProperties_3_Value\tExternalAccess\tExtraProperties_0_Key\tExtraProperties_0_Value\tExtraProperties_1_Key\tExtraProperties_1_Value\tExtraProperties_2_Key\tExtraProperties_2_Value\tExtraProperties_3_Key\tExtraProperties_3_Value\tExtraProperties_4_Key\tExtraProperties_4_Value\tExtraProperties_5_Key\tExtraProperties_5_Value\tExtraProperties_6_Key\tExtraProperties_6_Value\tFileSizeBytes\tFileSyncBytesCommitted\tFolder_Id\tFolder_Path\tFolders_0_FolderItems_0_ClientRequestId\tFolders_0_FolderItems_0_CreationTime\tFolders_0_FolderItems_0_Id\tFolders_0_FolderItems_0_ImmutableId\tFolders_0_FolderItems_0_InternetMessageId\tFolders_0_FolderItems_0_SizeInBytes\tFolders_0_FolderItems_0_Subject\tFolders_0_FolderItems_10_ClientRequestId\tFolders_0_FolderItems_10_CreationTime\tFolders_0_FolderItems_10_Id\tFolders_0_FolderItems_10_ImmutableId\tFolders_0_FolderItems_10_InternetMessageId\tFolders_0_FolderItems_10_SizeInBytes\tFolders_0_FolderItems_10_Subject\tFolders_0_FolderItems_1_ClientRequestId\tFolders_0_FolderItems_1_CreationTime\tFolders_0_FolderItems_1_Id\tFolders_0_FolderItems_1_ImmutableId\tFolders_0_FolderItems_1_InternetMessageId\tFolders_0_FolderItems_1_SizeInBytes\tFolders_0_FolderItems_1_Subject\tFolders_0_FolderItems_2_ClientRequestId\tFolders_0_FolderItems_2_CreationTime\tFolders_0_FolderItems_2_Id\tFolders_0_FolderItems_2_ImmutableId\tFolders_0_FolderItems_2_InternetMessageId\tFolders_0_FolderItems_2_SizeInBytes\tFolders_0_FolderItems_2_Subject\tFolders_0_FolderItems_3_ClientRequestId\tFolders_0_FolderItems_3_CreationTime\tFolders_0_FolderItems_3_Id\tFolders_0_FolderItems_3_ImmutableId\tFolders_0_FolderItems_3_InternetMessageId\tFolders_0_FolderItems_3_SizeInBytes\tFolders_0_FolderItems_3_Subject\tFolders_0_FolderItems_4_ClientRequestId\tFolders_0_FolderItems_4_CreationTime\tFolders_0_FolderItems_4_Id\tFolders_0_FolderItems_4_ImmutableId\tFolders_0_FolderItems_4_InternetMessageId\tFolders_0_FolderItems_4_SizeInBytes\tFolders_0_FolderItems_4_Subject\tFolders_0_FolderItems_5_ClientRequestId\tFolders_0_FolderItems_5_CreationTime\tFolders_0_FolderItems_5_Id\tFolders_0_FolderItems_5_ImmutableId\tFolders_0_FolderItems_5_InternetMessageId\tFolders_0_FolderItems_5_SizeInBytes\tFolders_0_FolderItems_5_Subject\tFolders_0_FolderItems_6_ClientRequestId\tFolders_0_FolderItems_6_CreationTime\tFolders_0_FolderItems_6_Id\tFolders_0_FolderItems_6_ImmutableId\tFolders_0_FolderItems_6_InternetMessageId\tFolders_0_FolderItems_6_SizeInBytes\tFolders_0_FolderItems_6_Subject\tFolders_0_FolderItems_7_ClientRequestId\tFolders_0_FolderItems_7_CreationTime\tFolders_0_FolderItems_7_Id\tFolders_0_FolderItems_7_ImmutableId\tFolders_0_FolderItems_7_InternetMessageId\tFolders_0_FolderItems_7_SizeInBytes\tFolders_0_FolderItems_7_Subject\tFolders_0_FolderItems_8_ClientRequestId\tFolders_0_FolderItems_8_CreationTime\tFolders_0_FolderItems_8_Id\tFolders_0_FolderItems_8_ImmutableId\tFolders_0_FolderItems_8_InternetMessageId\tFolders_0_FolderItems_8_SizeInBytes\tFolders_0_FolderItems_8_Subject\tFolders_0_FolderItems_9_ClientRequestId\tFolders_0_FolderItems_9_CreationTime\tFolders_0_FolderItems_9_Id\tFolders_0_FolderItems_9_ImmutableId\tFolders_0_FolderItems_9_InternetMessageId\tFolders_0_FolderItems_9_SizeInBytes\tFolders_0_FolderItems_9_Subject\tFolders_0_Id\tFolders_0_Path\tFolders_1_FolderItems_0_ClientRequestId\tFolders_1_FolderItems_0_CreationTime\tFolders_1_FolderItems_0_Id\tFolders_1_FolderItems_0_ImmutableId\tFolders_1_FolderItems_0_InternetMessageId\tFolders_1_FolderItems_0_SizeInBytes\tFolders_1_FolderItems_0_Subject\tFolders_1_FolderItems_1_ClientRequestId\tFolders_1_FolderItems_1_CreationTime\tFolders_1_FolderItems_1_Id\tFolders_1_FolderItems_1_ImmutableId\tFolders_1_FolderItems_1_InternetMessageId\tFolders_1_FolderItems_1_SizeInBytes\tFolders_1_FolderItems_1_Subject\tFolders_1_FolderItems_2_ClientRequestId\tFolders_1_FolderItems_2_CreationTime\tFolders_1_FolderItems_2_Id\tFolders_1_FolderItems_2_ImmutableId\tFolders_1_FolderItems_2_InternetMessageId\tFolders_1_FolderItems_2_SizeInBytes\tFolders_1_FolderItems_2_Subject\tFolders_1_FolderItems_3_ClientRequestId\tFolders_1_FolderItems_3_CreationTime\tFolders_1_FolderItems_3_Id\tFolders_1_FolderItems_3_ImmutableId\tFolders_1_FolderItems_3_InternetMessageId\tFolders_1_FolderItems_3_SizeInBytes\tFolders_1_FolderItems_3_Subject\tFolders_1_FolderItems_4_ClientRequestId\tFolders_1_FolderItems_4_CreationTime\tFolders_1_FolderItems_4_Id\tFolders_1_FolderItems_4_ImmutableId\tFolders_1_FolderItems_4_InternetMessageId\tFolders_1_FolderItems_4_SizeInBytes\tFolders_1_FolderItems_4_Subject\tFolders_1_FolderItems_5_ClientRequestId\tFolders_1_FolderItems_5_CreationTime\tFolders_1_FolderItems_5_Id\tFolders_1_FolderItems_5_ImmutableId\tFolders_1_FolderItems_5_InternetMessageId\tFolders_1_FolderItems_5_SizeInBytes\tFolders_1_FolderItems_5_Subject\tFolders_1_FolderItems_6_ClientRequestId\tFolders_1_FolderItems_6_CreationTime\tFolders_1_FolderItems_6_Id\tFolders_1_FolderItems_6_ImmutableId\tFolders_1_FolderItems_6_InternetMessageId\tFolders_1_FolderItems_6_SizeInBytes\tFolders_1_FolderItems_6_Subject\tFolders_1_FolderItems_7_ClientRequestId\tFolders_1_FolderItems_7_CreationTime\tFolders_1_FolderItems_7_Id\tFolders_1_FolderItems_7_ImmutableId\tFolders_1_FolderItems_7_InternetMessageId\tFolders_1_FolderItems_7_SizeInBytes\tFolders_1_FolderItems_7_Subject\tFolders_1_FolderItems_8_ClientRequestId\tFolders_1_FolderItems_8_CreationTime\tFolders_1_FolderItems_8_Id\tFolders_1_FolderItems_8_ImmutableId\tFolders_1_FolderItems_8_InternetMessageId\tFolders_1_FolderItems_8_SizeInBytes\tFolders_1_FolderItems_8_Subject\tFolders_1_FolderItems_9_ClientRequestId\tFolders_1_FolderItems_9_CreationTime\tFolders_1_FolderItems_9_Id\tFolders_1_FolderItems_9_ImmutableId\tFolders_1_FolderItems_9_InternetMessageId\tFolders_1_FolderItems_9_SizeInBytes\tFolders_1_FolderItems_9_Subject\tFolders_1_Id\tFolders_1_Path\tFolders_2_FolderItems_0_ClientRequestId\tFolders_2_FolderItems_0_CreationTime\tFolders_2_FolderItems_0_Id\tFolders_2_FolderItems_0_ImmutableId\tFolders_2_FolderItems_0_InternetMessageId\tFolders_2_FolderItems_0_SizeInBytes\tFolders_2_FolderItems_0_Subject\tFolders_2_FolderItems_1_ClientRequestId\tFolders_2_FolderItems_1_CreationTime\tFolders_2_FolderItems_1_Id\tFolders_2_FolderItems_1_ImmutableId\tFolders_2_FolderItems_1_InternetMessageId\tFolders_2_FolderItems_1_SizeInBytes\tFolders_2_FolderItems_1_Subject\tFolders_2_FolderItems_2_ClientRequestId\tFolders_2_FolderItems_2_CreationTime\tFolders_2_FolderItems_2_Id\tFolders_2_FolderItems_2_ImmutableId\tFolders_2_FolderItems_2_InternetMessageId\tFolders_2_FolderItems_2_SizeInBytes\tFolders_2_FolderItems_2_Subject\tFolders_2_FolderItems_3_ClientRequestId\tFolders_2_FolderItems_3_CreationTime\tFolders_2_FolderItems_3_Id\tFolders_2_FolderItems_3_ImmutableId\tFolders_2_FolderItems_3_InternetMessageId\tFolders_2_FolderItems_3_SizeInBytes\tFolders_2_FolderItems_3_Subject\tFolders_2_Id\tFolders_2_Path\tFolders_3_FolderItems_0_ClientRequestId\tFolders_3_FolderItems_0_CreationTime\tFolders_3_FolderItems_0_Id\tFolders_3_FolderItems_0_ImmutableId\tFolders_3_FolderItems_0_InternetMessageId\tFolders_3_FolderItems_0_SizeInBytes\tFolders_3_FolderItems_0_Subject\tFolders_3_FolderItems_1_ClientRequestId\tFolders_3_FolderItems_1_CreationTime\tFolders_3_FolderItems_1_Id\tFolders_3_FolderItems_1_ImmutableId\tFolders_3_FolderItems_1_InternetMessageId\tFolders_3_FolderItems_1_SizeInBytes\tFolders_3_FolderItems_1_Subject\tFolders_3_Id\tFolders_3_Path\tFormId\tFormName\tFormsUserType\tFromApp\tGeoLocation\tHighPriorityMediaProcessing\tHostAppId\tICalUid\tId\tImplicitShare\tInterSystemsId\tInternalLogonType\tIntraSystemId\tIsBilateral\tIsCopilotMentioned\tIsManagedDevice\tIsSuccess\tItemCount\tItemName\tItemType\tItem_Attachments\tItem_Id\tItem_ImmutableId\tItem_InternetMessageId\tItem_IsRecord\tItem_ParentFolder_Id\tItem_ParentFolder_Name\tItem_ParentFolder_Path\tItem_SizeInBytes\tItem_Subject\tJoinTime\tLeaveTime\tListBaseTemplateType\tListBaseType\tListColor\tListIcon\tListId\tListItemUniqueId\tListName\tListServerTemplate\tListTitle\tListUrl\tLogonError\tLogonType\tLogonUserSid\tMachineDomainInfo\tMachineId\tMailboxGuid\tMailboxOwnerSid\tMailboxOwnerUPN\tMeetingDetailId\tMeetingURL\tMessageId\tMessageURLs_0\tMessageVersion\tMessages_0_Id\tMessages_0_MessageItems_0_Id\tMessages_0_MessageItems_0_SizeInBytes\tMessages_0_MessageItems_1_Id\tMessages_0_MessageItems_1_SizeInBytes\tMessages_0_MessageItems_2_Id\tMessages_0_MessageItems_2_SizeInBytes\tMessages_0_MessageItems_3_Id\tMessages_0_MessageItems_3_SizeInBytes\tMessages_0_MessageItems_4_Id\tMessages_0_MessageItems_4_SizeInBytes\tMessages_0_MessageItems_5_Id\tMessages_0_MessageItems_5_SizeInBytes\tMessages_0_MessageItems_6_Id\tMessages_0_MessageItems_6_SizeInBytes\tMessages_0_MessageItems_7_Id\tMessages_0_MessageItems_7_SizeInBytes\tMessages_0_MessageItems_8_Id\tMessages_0_MessageItems_8_SizeInBytes\tMessages_0_MessageItems_9_Id\tMessages_0_MessageItems_9_SizeInBytes\tMessages_0_Path\tMessages_1_Id\tMessages_1_MessageItems_0_Id\tMessages_1_MessageItems_0_SizeInBytes\tMessages_1_MessageItems_1_Id\tMessages_1_MessageItems_1_SizeInBytes\tMessages_1_MessageItems_2_Id\tMessages_1_MessageItems_2_SizeInBytes\tMessages_1_Path\tMessages_2_Id\tMessages_2_MessageItems_0_Id\tMessages_2_MessageItems_0_SizeInBytes\tMessages_2_MessageItems_1_Id\tMessages_2_MessageItems_1_SizeInBytes\tMessages_2_Path\tModalities\tModifiedProperties_0\tModifiedProperties_0_Name\tModifiedProperties_0_NewValue\tModifiedProperties_0_OldValue\tModifiedProperties_1\tModifiedProperties_10\tModifiedProperties_11\tModifiedProperties_12\tModifiedProperties_13\tModifiedProperties_14\tModifiedProperties_15\tModifiedProperties_16\tModifiedProperties_17\tModifiedProperties_18\tModifiedProperties_19\tModifiedProperties_2\tModifiedProperties_20\tModifiedProperties_21\tModifiedProperties_22\tModifiedProperties_23\tModifiedProperties_24\tModifiedProperties_25\tModifiedProperties_26\tModifiedProperties_27\tModifiedProperties_28\tModifiedProperties_29\tModifiedProperties_3\tModifiedProperties_30\tModifiedProperties_31\tModifiedProperties_32\tModifiedProperties_33\tModifiedProperties_34\tModifiedProperties_35\tModifiedProperties_36\tModifiedProperties_37\tModifiedProperties_38\tModifiedProperties_39\tModifiedProperties_4\tModifiedProperties_40\tModifiedProperties_41\tModifiedProperties_42\tModifiedProperties_43\tModifiedProperties_44\tModifiedProperties_45\tModifiedProperties_46\tModifiedProperties_47\tModifiedProperties_48\tModifiedProperties_49\tModifiedProperties_5\tModifiedProperties_50\tModifiedProperties_51\tModifiedProperties_52\tModifiedProperties_53\tModifiedProperties_54\tModifiedProperties_6\tModifiedProperties_7\tModifiedProperties_8\tModifiedProperties_9\tObjectId\tOperationCount\tOperationProperties_0_Name\tOperationProperties_0_Value\tOrganizationId\tOrganizationName\tOrganizer_OrganizationId\tOrganizer_RecipientType\tOrganizer_Role\tOrganizer_UserObjectId\tOriginatingServer\tParameters_0_Name\tParameters_0_Value\tParameters_1_Name\tParameters_1_Value\tParticipantInfo_HasForeignTenantUsers\tParticipantInfo_HasGuestUsers\tParticipantInfo_HasOtherGuestUsers\tParticipantInfo_HasUnauthenticatedUsers\tParticipantInfo_ParticipatingTenantIds_0\tPermission\tPlatform\tProviderTypes\tRefreshEnforcementPolicy\tReportId\tReportName\tReportType\tRequestId\tResourceTenantId\tResultStatus\tSaveToSentItems\tSearchQueryText\tSessionId\tSharingLinkScope\tSharingType\tSite\tSiteUrl\tSource\tSourceApp\tSourceFileExtension\tSourceFileName\tSourceRelativeUrl\tStartTime\tSupportTicketId\tTargetContextId\tTargetUserOrGroupName\tTargetUserOrGroupType\tTarget_0_ID\tTarget_0_Type\tTaskList\tTemplateTypeId\tTokenObjectId\tTokenTenantId\tTokenType\tUniqueSharingId\tUserAgent\tUserKey\tUserSessionId\tUserType\tVersion\tWebId\tWorkSpaceName\tWorkload\tWorkspaceId\tZipFileName";

    private static readonly Dictionary<string, string> PreferredMasterHeaderEventColumnMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RecordId"] = "record_id",
        ["CreationDate"] = "creation_date_utc",
        ["CreationTime"] = "creation_date_utc",
        ["StartTime"] = "creation_date_utc",
        ["Operation"] = "operation",
        ["Activity"] = "operation",
        ["UserId"] = "user_id",
        ["UserKey"] = "user_id",
        ["Workload"] = "workload",
        ["ClientIP"] = "client_ip",
        ["ClientIPAddress"] = "client_ip",
        ["ActorIpAddress"] = "client_ip",
        ["UserAgent"] = "user_agent",
        ["ObjectId"] = "object_id",
        ["SiteUrl"] = "site_url",
        ["Site"] = "site_url",
        ["SourceRelativeUrl"] = "source_relative_url",
        ["DestinationRelativeUrl"] = "source_relative_url",
        ["SourceFileName"] = "file_name",
        ["DestinationFileName"] = "file_name",
        ["ItemName"] = "file_name",
        ["FileSizeBytes"] = "file_size_bytes",
        ["ResultStatus"] = "result_status",
        ["Source"] = "data_source"
    };

    public static long ExportAllMasterMetadataCsv(string dbPath, string csvPath, Action<string>? progress = null)
    {
        InitializeDatabase(dbPath);

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(csvPath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var conn = Open(dbPath);
        var eventColumns = GetTableColumnNames(conn, "events");
        var metadataFieldNames = GetDistinctMetadataFieldNames(conn);
        var preferredHeaders = GetPreferredMasterMetadataExportHeaders();
        var preferredHeaderMetadataSource = BuildPreferredHeaderMetadataSourceMap(preferredHeaders, metadataFieldNames);
        var preferredHeaderMatchSet = new HashSet<string>(preferredHeaders.Select(NormalizeHeaderForMatching), StringComparer.OrdinalIgnoreCase);

        var usedHeaders = new HashSet<string>(preferredHeaders, StringComparer.OrdinalIgnoreCase);
        var tagsHeader = CreateUniqueCsvHeader("Vestigant_Tags", usedHeaders);
        var eventAdditionalHeaders = BuildAdditionalEventColumnHeaders(eventColumns, usedHeaders);
        var additionalMetadataFieldNames = metadataFieldNames
            .Where(field => !preferredHeaderMatchSet.Contains(NormalizeHeaderForMatching(field)))
            .ToList();
        var additionalMetadataHeaders = BuildAdditionalMetadataHeaders(additionalMetadataFieldNames, usedHeaders);

        var exportedRows = 0L;
        var totalRows = QueryScalarLong(dbPath, "SELECT COUNT(*) FROM events;");

        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var header = new List<string>();
        header.AddRange(preferredHeaders);
        header.Add(tagsHeader);
        header.AddRange(eventAdditionalHeaders.Select(item => item.Header));
        header.AddRange(additionalMetadataHeaders.Select(item => item.Header));
        writer.WriteLine(string.Join(",", header.Select(CsvEscape)));

        const int batchSize = 500;
        var lastEventId = 0L;
        while (true)
        {
            var rows = ReadEventBatch(conn, eventColumns, lastEventId, batchSize);
            if (rows.Count == 0)
                break;

            var eventIds = rows.Select(row => row.EventId).ToArray();
            var metadataByEvent = ReadMetadataForEvents(conn, eventIds);

            foreach (var row in rows)
            {
                if (!metadataByEvent.TryGetValue(row.EventId, out var metadataForEvent))
                    metadataForEvent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var values = new List<string>(preferredHeaders.Count + 1 + eventAdditionalHeaders.Count + additionalMetadataHeaders.Count);
                foreach (var headerName in preferredHeaders)
                    values.Add(ResolvePreferredMasterExportValue(headerName, row, metadataForEvent, preferredHeaderMetadataSource));

                values.Add(row.Tags);

                foreach (var item in eventAdditionalHeaders)
                    values.Add(row.Values.TryGetValue(item.Column, out var value) ? value : string.Empty);

                foreach (var item in additionalMetadataHeaders)
                    values.Add(metadataForEvent.TryGetValue(item.FieldName, out var value) ? value : string.Empty);

                writer.WriteLine(string.Join(",", values.Select(CsvEscape)));
                exportedRows++;
                lastEventId = Math.Max(lastEventId, row.EventId);
            }

            if (exportedRows == totalRows || exportedRows % 10000 == 0)
                progress?.Invoke($"Exported {exportedRows:N0} of {totalRows:N0} master records...");
        }

        progress?.Invoke($"Exported {exportedRows:N0} master records to {csvPath}");
        return exportedRows;
    }

    private sealed record EventExportColumn(string Header, string Column);

    private sealed record MetadataExportColumn(string Header, string FieldName);

    private static List<string> GetPreferredMasterMetadataExportHeaders()
    {
        return PreferredMasterMetadataExportHeaderText
            .Split(new[] { "\t" }, StringSplitOptions.None)
            .Select(header => header.Trim())
            .Where(header => !string.IsNullOrWhiteSpace(header))
            .ToList();
    }

    private static Dictionary<string, string> BuildPreferredHeaderMetadataSourceMap(IReadOnlyList<string> preferredHeaders, IReadOnlyList<string> metadataFieldNames)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exactFields = new HashSet<string>(metadataFieldNames, StringComparer.OrdinalIgnoreCase);
        var normalizedFieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldName in metadataFieldNames)
        {
            var normalized = NormalizeHeaderForMatching(fieldName);
            if (!string.IsNullOrWhiteSpace(normalized) && !normalizedFieldMap.ContainsKey(normalized))
                normalizedFieldMap[normalized] = fieldName;
        }

        foreach (var header in preferredHeaders)
        {
            if (exactFields.Contains(header))
            {
                map[header] = header;
                continue;
            }

            var normalized = NormalizeHeaderForMatching(header);
            if (normalizedFieldMap.TryGetValue(normalized, out var fieldName))
                map[header] = fieldName;
        }

        return map;
    }

    private static List<EventExportColumn> BuildAdditionalEventColumnHeaders(IReadOnlyList<string> eventColumns, ISet<string> usedHeaders)
    {
        var columns = new List<EventExportColumn>();
        foreach (var column in eventColumns)
        {
            var header = CreateUniqueCsvHeader("Vestigant_" + NormalizeCsvHeader(column), usedHeaders);
            columns.Add(new EventExportColumn(header, column));
        }
        return columns;
    }

    private static List<MetadataExportColumn> BuildAdditionalMetadataHeaders(IReadOnlyList<string> fieldNames, ISet<string> usedHeaders)
    {
        var columns = new List<MetadataExportColumn>();
        foreach (var fieldName in fieldNames)
        {
            var header = CreateUniqueCsvHeader("Metadata_" + NormalizeCsvHeader(fieldName), usedHeaders);
            columns.Add(new MetadataExportColumn(header, fieldName));
        }
        return columns;
    }

    private static string CreateUniqueCsvHeader(string requestedHeader, ISet<string> usedHeaders)
    {
        var baseHeader = string.IsNullOrWhiteSpace(requestedHeader) ? "Metadata_Field" : requestedHeader;
        var candidate = baseHeader;
        var suffix = 2;
        while (usedHeaders.Contains(candidate))
        {
            candidate = $"{baseHeader}_{suffix}";
            suffix++;
        }
        usedHeaders.Add(candidate);
        return candidate;
    }

    private static string ResolvePreferredMasterExportValue(
        string headerName,
        EventExportRow row,
        IReadOnlyDictionary<string, string> metadataForEvent,
        IReadOnlyDictionary<string, string> preferredHeaderMetadataSource)
    {
        if (preferredHeaderMetadataSource.TryGetValue(headerName, out var metadataField) &&
            metadataForEvent.TryGetValue(metadataField, out var metadataValue) &&
            !string.IsNullOrWhiteSpace(metadataValue))
        {
            return metadataValue;
        }

        if (PreferredMasterHeaderEventColumnMap.TryGetValue(headerName, out var eventColumn) &&
            row.Values.TryGetValue(eventColumn, out var eventValue))
        {
            return eventValue ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeHeaderForMatching(string value)
    {
        var normalized = new StringBuilder(value?.Length ?? 0);
        foreach (var ch in value ?? string.Empty)
        {
            if (char.IsLetterOrDigit(ch))
                normalized.Append(char.ToLowerInvariant(ch));
        }
        return normalized.ToString();
    }

    private sealed record EventExportRow(long EventId, Dictionary<string, string> Values, string Tags);

    private static List<string> GetTableColumnNames(SqliteConnection conn, string tableName)
    {
        var columns = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteSqlIdentifier(tableName)});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
                columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static List<string> GetDistinctMetadataFieldNames(SqliteConnection conn)
    {
        var fields = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT field_name
            FROM event_fields
            WHERE IFNULL(field_name,'') <> ''
            GROUP BY field_name COLLATE NOCASE
            ORDER BY field_name COLLATE NOCASE;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                fields.Add(value);
        }
        return fields;
    }

    private static Dictionary<string, string> BuildMetadataHeaderMap(IReadOnlyCollection<string> eventColumns, IEnumerable<string> metadataFieldNames)
    {
        var usedHeaders = new HashSet<string>(eventColumns, StringComparer.OrdinalIgnoreCase) { "Tags" };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldName in metadataFieldNames)
        {
            var baseName = "Metadata_" + NormalizeCsvHeader(fieldName);
            if (string.IsNullOrWhiteSpace(baseName) || baseName.Equals("Metadata_", StringComparison.OrdinalIgnoreCase))
                baseName = "Metadata_Field";

            var candidate = baseName;
            var suffix = 2;
            while (usedHeaders.Contains(candidate))
            {
                candidate = $"{baseName}_{suffix}";
                suffix++;
            }

            usedHeaders.Add(candidate);
            map[fieldName] = candidate;
        }
        return map;
    }

    private static string NormalizeCsvHeader(string value)
    {
        var cleaned = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' || ch == ' ')
                cleaned.Append(ch);
            else if (char.IsWhiteSpace(ch))
                cleaned.Append(' ');
            else
                cleaned.Append('_');
        }
        return cleaned.ToString().Trim().Replace(' ', '_');
    }

    private static List<EventExportRow> ReadEventBatch(SqliteConnection conn, IReadOnlyList<string> eventColumns, long lastEventId, int batchSize)
    {
        var rows = new List<EventExportRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT e.*, IFNULL((SELECT group_concat(t.tag_name, '; ') FROM event_tags et JOIN tags t ON t.tag_id = et.tag_id WHERE et.event_id = e.event_id), '') AS Tags
            FROM events e
            WHERE e.event_id > $lastEventId
            ORDER BY e.event_id
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$lastEventId", lastEventId);
        cmd.Parameters.AddWithValue("$limit", batchSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long eventId = 0;
            foreach (var column in eventColumns)
            {
                var ordinal = reader.GetOrdinal(column);
                var value = reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;
                values[column] = value;
                if (column.Equals("event_id", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out var parsedEventId))
                    eventId = parsedEventId;
            }

            var tagsOrdinal = reader.GetOrdinal("Tags");
            var tags = reader.IsDBNull(tagsOrdinal) ? string.Empty : reader.GetString(tagsOrdinal);
            rows.Add(new EventExportRow(eventId, values, tags));
        }
        return rows;
    }

    private static Dictionary<long, Dictionary<string, string>> ReadMetadataForEvents(SqliteConnection conn, IReadOnlyList<long> eventIds)
    {
        var metadataByEvent = new Dictionary<long, Dictionary<string, string>>();
        if (eventIds.Count == 0)
            return metadataByEvent;

        using var cmd = conn.CreateCommand();
        var parameterNames = new List<string>(eventIds.Count);
        for (var i = 0; i < eventIds.Count; i++)
        {
            var parameterName = "$eventId" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, eventIds[i]);
        }

        cmd.CommandText = $@"
            SELECT event_id, field_name, group_concat(field_value, '; ') AS field_values
            FROM event_fields
            WHERE event_id IN ({string.Join(",", parameterNames)})
              AND IFNULL(field_name,'') <> ''
            GROUP BY event_id, field_name
            ORDER BY event_id, field_name;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var eventId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
            if (eventId <= 0)
                continue;

            var fieldName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(fieldName))
                continue;

            var fieldValue = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            if (!metadataByEvent.TryGetValue(eventId, out var metadataForEvent))
            {
                metadataForEvent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                metadataByEvent[eventId] = metadataForEvent;
            }
            metadataForEvent[fieldName] = fieldValue;
        }

        return metadataByEvent;
    }

    private static string QuoteSqlIdentifier(string identifier)
    {
        return "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }

    private static string CsvEscape(string value)
    {
        value ??= string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }


    public static DataTable QueryToDataTable(string dbPath, string sql, Dictionary<string, object>? parameters = null)
    {
        // Do not repeat full schema/migration initialization for every read query.
        // InitializeDatabase can run migrations and index/view work, which is expensive on multi-GB cases.
        var dt = new DataTable();
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            }
        }

        using var reader = cmd.ExecuteReader();
        dt.Load(reader);
        return dt;
    }


    public static DataTable GetTags(string dbPath)
    {
        InitializeDatabase(dbPath);
        return QueryToDataTable(dbPath, "SELECT tag_id AS Tag_ID, tag_name AS Tag, description AS Description, color_hex AS Color, created_utc AS Created_UTC FROM tags ORDER BY tag_name;");
    }

    public static long CreateOrUpdateTag(string dbPath, string tagName, string description, string colorHex = "")
    {
        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name is blank.", nameof(tagName));

        tagName = tagName.Trim();
        description = description?.Trim() ?? string.Empty;
        colorHex = colorHex?.Trim() ?? string.Empty;

        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO tags(tag_name, description, color_hex, created_utc)
            VALUES($name, $description, $color, $created)
            ON CONFLICT(tag_name) DO UPDATE SET
                description = excluded.description,
                color_hex = CASE WHEN excluded.color_hex = '' THEN tags.color_hex ELSE excluded.color_hex END
            RETURNING tag_id;";
        cmd.Parameters.AddWithValue("$name", tagName);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$color", colorHex);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public static void DeleteTag(string dbPath, long tagId)
    {
        if (tagId <= 0) return;
        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();
        using (var unlink = conn.CreateCommand())
        {
            unlink.Transaction = tx;
            unlink.CommandText = "DELETE FROM event_tags WHERE tag_id = $id;";
            unlink.Parameters.AddWithValue("$id", tagId);
            unlink.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM tags WHERE tag_id = $id;";
            cmd.Parameters.AddWithValue("$id", tagId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static void AddTagToEvents(string dbPath, IEnumerable<long> eventIds, long tagId, string sourceContext, string notes = "")
    {
        var ids = eventIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0 || tagId <= 0)
            return;

        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO event_tags(event_id, tag_id, source_context, notes, created_utc)
            VALUES($event_id, $tag_id, $context, $notes, $created);";
        var pEvent = cmd.Parameters.Add("$event_id", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$tag_id", tagId);
        cmd.Parameters.AddWithValue("$context", sourceContext ?? string.Empty);
        cmd.Parameters.AddWithValue("$notes", notes ?? string.Empty);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));

        foreach (var id in ids)
        {
            pEvent.Value = id;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public static void RemoveTagFromEvents(string dbPath, IEnumerable<long> eventIds, long tagId)
    {
        var ids = eventIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0 || tagId <= 0)
            return;

        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM event_tags WHERE event_id = $event_id AND tag_id = $tag_id;";
        var pEvent = cmd.Parameters.Add("$event_id", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$tag_id", tagId);

        foreach (var id in ids)
        {
            pEvent.Value = id;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public static DataTable QueryTaggedEvents(string dbPath)
    {
        InitializeDatabase(dbPath);
        var sql = @"
            SELECT
                et.id AS Tag_Link_ID,
                t.tag_id AS Tag_ID,
                t.tag_name AS Tag,
                t.description AS Tag_Description,
                et.source_context AS Tag_Context,
                et.notes AS Tag_Notes,
                et.created_utc AS Tagged_UTC,
                mt.*
            FROM event_tags et
            JOIN tags t ON t.tag_id = et.tag_id
            JOIN v_master_timeline mt ON mt.event_id = et.event_id
            ORDER BY et.created_utc DESC, mt.Date_Time DESC;";
        return QueryToDataTable(dbPath, sql);
    }

    public static long QueryScalarLong(string dbPath, string sql, Dictionary<string, object>? parameters = null)
    {
        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameters != null)
        {
            foreach (var p in parameters)
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
        }
        var value = cmd.ExecuteScalar();
        if (value == null || value == DBNull.Value) return 0;
        return Convert.ToInt64(value);
    }

    public static DataTable GetDatabaseDiagnostics(string dbPath)
    {
        InitializeDatabase(dbPath);
        var dt = new DataTable();
        dt.Columns.Add("Metric");
        dt.Columns.Add("Value");
        void Add(string metric, object? value) => dt.Rows.Add(metric, value?.ToString() ?? string.Empty);

        var dbInfo = new FileInfo(dbPath);
        Add("Database path", dbPath);
        Add("Database size bytes", dbInfo.Exists ? dbInfo.Length : 0);
        Add("Events", QueryScalarLong(dbPath, "SELECT COUNT(*) FROM events;"));
        Add("Behavioral events", QueryScalarLong(dbPath, "SELECT COUNT(*) FROM events WHERE is_behavioral_timestamp = 1;"));
        Add("Event fields", QueryScalarLong(dbPath, "SELECT COUNT(*) FROM event_fields;"));
        Add("Risk hits", QueryScalarLong(dbPath, "SELECT COUNT(*) FROM risk_hits;"));
        Add("Tags", QueryScalarLong(dbPath, "SELECT COUNT(*) FROM tags;"));
        Add("Tagged links", QueryScalarLong(dbPath, "SELECT COUNT(*) FROM event_tags;"));

        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA page_count;"; Add("SQLite page_count", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA page_size;"; Add("SQLite page_size", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA freelist_count;"; Add("SQLite freelist_count", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA journal_mode;"; Add("SQLite journal_mode", cmd.ExecuteScalar());
        return dt;
    }

    public static void OptimizeDatabase(string dbPath)
    {
        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        EnsurePerformanceIndexes(conn);
        OptimizeDatabaseConnection(conn);
    }

    public static void VacuumDatabase(string dbPath)
    {
        InitializeDatabase(dbPath);
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM;";
        cmd.ExecuteNonQuery();
        OptimizeDatabaseConnection(conn);
    }

    private static void OptimizeDatabaseConnection(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA optimize; ANALYZE;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Maintenance should never prevent app startup or case open.
        }
    }

    public static Dictionary<string, string> GetReconstructedEventRow(string dbPath, long eventId)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        InitializeDatabase(dbPath);

        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT * FROM events WHERE event_id = $id";
        cmd.Parameters.AddWithValue("$id", eventId);
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string colName = reader.GetName(i);
                    string val = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                    dict[colName] = val;
                }
            }
        }

        cmd.CommandText = "SELECT field_name, field_value FROM event_fields WHERE event_id = $id ORDER BY field_name";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string fn = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string fv = reader.IsDBNull(1) ? "" : reader.GetString(1);
                dict[$"[Metadata] {fn}"] = fv;
            }
        }

        return dict;
    }
}
