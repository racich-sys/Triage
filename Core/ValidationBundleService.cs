using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VestigantTriage;

internal static class ValidationBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ValidationBundleResult ExportValidationBundle(string zipPath, CaseFile caseFile, string caseFolder, string dbPath, Action<string>? log = null)
    {
        if (caseFile == null) throw new ArgumentNullException(nameof(caseFile));
        if (string.IsNullOrWhiteSpace(zipPath)) throw new ArgumentException("Validation bundle zip path is blank.", nameof(zipPath));

        var zipFullPath = Path.GetFullPath(zipPath);
        var zipDirectory = Path.GetDirectoryName(zipFullPath);
        if (!string.IsNullOrWhiteSpace(zipDirectory)) Directory.CreateDirectory(zipDirectory);

        var tempRoot = Path.Combine(Path.GetTempPath(), "VestigantValidation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            log?.Invoke("Building validation bundle source coverage export...");
            var sources = caseFile.Sources ?? new List<SourceFileRecord>();
            var sourceCoverage = ParserCoverageService.BuildSourceCoverageTable(sources, caseFolder);
            WriteDataTableCsv(Path.Combine(tempRoot, "vestigant_source_coverage.csv"), sourceCoverage);

            log?.Invoke("Building validation bundle parser candidate conflict export...");
            var candidateConflictRows = ExportParserCandidateConflicts(sourceCoverage, Path.Combine(tempRoot, "vestigant_parser_candidate_conflicts.csv"));

            log?.Invoke("Building validation bundle parser coverage export...");
            var parserCoverage = ParserCoverageService.BuildParserCoverageTable(dbPath, sources, caseFolder);
            WriteDataTableCsv(Path.Combine(tempRoot, "vestigant_parser_coverage.csv"), parserCoverage);

            log?.Invoke("Building validation bundle parser error export...");
            var parserErrors = ParserCoverageService.BuildParserErrorTable(dbPath);
            WriteDataTableCsv(Path.Combine(tempRoot, "vestigant_parser_errors.csv"), parserErrors);

            var dbExists = !string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath);
            var eventSummaryRows = 0;
            var fallbackRows = 0;
            var distinctSourceRows = 0;
            var oneDriveSummaryRows = 0;
            var googleSourceRows = 0;
            var googleFamilyRows = 0;
            var googleSchemaRows = 0;
            var googleRiskRows = 0;
            var googleUnmappedRows = 0;
            var googleCollisionRows = 0;
            var googleStorageRows = 0;
            if (dbExists)
            {
                log?.Invoke("Building compact no-UAL event summary export...");
                eventSummaryRows = RunValidationCsvExport(
                    "vestigant_event_summary_no_ual.csv",
                    Path.Combine(tempRoot, "vestigant_event_summary_no_ual.csv"),
                    output => ExportEventSummaryNoUal(dbPath, output),
                    log);

                log?.Invoke("Building metadata/fallback source summary export...");
                fallbackRows = RunValidationCsvExport(
                    "vestigant_metadata_fallback_sources.csv",
                    Path.Combine(tempRoot, "vestigant_metadata_fallback_sources.csv"),
                    output => ExportMetadataFallbackSources(dbPath, output),
                    log);

                log?.Invoke("Building distinct source-file summary export...");
                distinctSourceRows = RunValidationCsvExport(
                    "vestigant_distinct_source_files_no_ual.csv",
                    Path.Combine(tempRoot, "vestigant_distinct_source_files_no_ual.csv"),
                    output => ExportDistinctSourceFilesNoUal(dbPath, output),
                    log);

                log?.Invoke("Building OneDrive catalog/source summary export...");
                oneDriveSummaryRows = RunValidationCsvExport(
                    "vestigant_onedrive_catalog_summary.csv",
                    Path.Combine(tempRoot, "vestigant_onedrive_catalog_summary.csv"),
                    output => ExportOneDriveCatalogSummary(dbPath, output),
                    log);

                log?.Invoke("Building Google source coverage export...");
                googleSourceRows = RunValidationCsvExport("vestigant_google_source_coverage.csv", Path.Combine(tempRoot, "vestigant_google_source_coverage.csv"), output => ExportGoogleSourceCoverage(dbPath, output), log);
                log?.Invoke("Building Google audit family summary export...");
                googleFamilyRows = RunValidationCsvExport("vestigant_google_audit_family_summary.csv", Path.Combine(tempRoot, "vestigant_google_audit_family_summary.csv"), output => ExportGoogleAuditFamilySummary(dbPath, output), log);
                log?.Invoke("Building Google schema coverage export...");
                googleSchemaRows = RunValidationCsvExport("vestigant_google_schema_coverage.csv", Path.Combine(tempRoot, "vestigant_google_schema_coverage.csv"), output => ExportGoogleSchemaCoverage(dbPath, output), log);
                log?.Invoke("Building Google risk summary export...");
                googleRiskRows = RunValidationCsvExport("vestigant_google_risk_summary.csv", Path.Combine(tempRoot, "vestigant_google_risk_summary.csv"), output => ExportGoogleRiskSummary(dbPath, output), log);
                log?.Invoke("Building Google unmapped columns export...");
                googleUnmappedRows = RunValidationCsvExport("vestigant_google_unmapped_columns.csv", Path.Combine(tempRoot, "vestigant_google_unmapped_columns.csv"), output => ExportGoogleUnmappedColumns(dbPath, output), log);
                log?.Invoke("Building Google field collision review export...");
                googleCollisionRows = RunValidationCsvExport("vestigant_google_field_collision_review.csv", Path.Combine(tempRoot, "vestigant_google_field_collision_review.csv"), output => ExportGoogleFieldCollisionReview(dbPath, output), log);
                log?.Invoke("Building Google metadata storage summary export...");
                googleStorageRows = RunValidationCsvExport("vestigant_google_metadata_storage_summary.csv", Path.Combine(tempRoot, "vestigant_google_metadata_storage_summary.csv"), output => ExportGoogleMetadataStorageSummary(dbPath, output), log);
            }
            else
            {
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_event_summary_no_ual.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_metadata_fallback_sources.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_distinct_source_files_no_ual.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_onedrive_catalog_summary.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_source_coverage.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_audit_family_summary.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_schema_coverage.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_risk_summary.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_unmapped_columns.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_field_collision_review.csv"), "Warning", "Database file was not found or not configured.");
                WriteEmptyCsv(Path.Combine(tempRoot, "vestigant_google_metadata_storage_summary.csv"), "Warning", "Database file was not found or not configured.");
            }

            WriteCaseSourceManifest(tempRoot, caseFile, caseFolder, dbPath, sourceCoverage.Rows.Count, parserCoverage.Rows.Count, parserErrors.Rows.Count, eventSummaryRows, fallbackRows, distinctSourceRows);
            WriteReadme(tempRoot, caseFile, sourceCoverage.Rows.Count, parserCoverage.Rows.Count, parserErrors.Rows.Count, eventSummaryRows, fallbackRows, distinctSourceRows, candidateConflictRows, oneDriveSummaryRows, googleSourceRows, googleFamilyRows, googleSchemaRows, googleRiskRows, googleUnmappedRows, googleCollisionRows, googleStorageRows, dbExists);

            if (File.Exists(zipFullPath)) File.Delete(zipFullPath);
            ZipFile.CreateFromDirectory(tempRoot, zipFullPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            var fileInfo = new FileInfo(zipFullPath);
            return new ValidationBundleResult(zipFullPath, fileInfo.Length, sourceCoverage.Rows.Count, parserCoverage.Rows.Count, parserErrors.Rows.Count, eventSummaryRows, fallbackRows, distinctSourceRows);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static int RunValidationCsvExport(string exportName, string outputPath, Func<string, int> exporter, Action<string>? log)
    {
        try
        {
            return exporter(outputPath);
        }
        catch (Exception ex)
        {
            var message = $"Validation export {exportName} failed: {ex.GetType().Name}: {ex.Message}";
            log?.Invoke("WARN: " + message);
            WriteEmptyCsv(outputPath, "Warning", message);
            return 0;
        }
    }


    private static int ExportParserCandidateConflicts(DataTable sourceCoverage, string outputPath)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("Issue,Source_File,Original_Source_Path,Recorded_Parser,Candidate_Parsers,Events_Imported,Notes");
        var rows = 0;
        foreach (DataRow row in sourceCoverage.Rows)
        {
            var sourceFile = row.Table.Columns.Contains("Source_File") ? row["Source_File"]?.ToString() ?? string.Empty : string.Empty;
            var originalPath = row.Table.Columns.Contains("Original_Source_Path") ? row["Original_Source_Path"]?.ToString() ?? string.Empty : string.Empty;
            var recordedParser = row.Table.Columns.Contains("Recorded_Parser") ? row["Recorded_Parser"]?.ToString() ?? string.Empty : string.Empty;
            var candidateText = row.Table.Columns.Contains("Candidate_Parsers") ? row["Candidate_Parsers"]?.ToString() ?? string.Empty : string.Empty;
            var eventsImported = row.Table.Columns.Contains("Events_Imported") ? row["Events_Imported"]?.ToString() ?? string.Empty : string.Empty;
            var candidates = candidateText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count <= 1) continue;

            var issue = "MultipleCandidateParsers";
            var notes = "Source artifact matched more than one parser; review routing specificity.";
            if (candidates.Any(c => c.Contains("Office Owner File", StringComparison.OrdinalIgnoreCase)) &&
                candidates.Any(c => c.Contains("Recycle Bin", StringComparison.OrdinalIgnoreCase)))
            {
                issue = "OwnerFileRecycleBinCandidateOverlap";
                notes = "Office owner/lock file should not match Recycle Bin $I metadata parser.";
            }

            writer.WriteLine(string.Join(',', new[] { issue, sourceFile, originalPath, recordedParser, candidateText, eventsImported, notes }.Select(CsvEscape)));
            rows++;
        }
        return rows;
    }

    private static int ExportOneDriveCatalogSummary(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            WITH field_pivot AS (
                SELECT event_id,
                       MAX(CASE WHEN field_name='ArtifactType' THEN field_value END) AS Artifact_Type,
                       MAX(CASE WHEN field_name='SourceDatabase' THEN field_value END) AS Source_Database,
                       MAX(CASE WHEN field_name='SourceTable' THEN field_value END) AS Source_Table,
                       MAX(CASE WHEN field_name='CloudAccount' THEN field_value END) AS Cloud_Account,
                       MAX(CASE WHEN field_name='OneDriveUserEmail' THEN field_value END) AS OneDrive_User_Email,
                       MAX(CASE WHEN field_name IN ('DisplayTarget','TargetPath','OriginalSourcePath') THEN field_value END) AS Display_Target,
                       GROUP_CONCAT(CASE WHEN field_name LIKE 'OneDriveDb_%' THEN field_name || '=' || SUBSTR(field_value,1,240) END, '; ') AS Sample_OneDriveDb_Fields
                FROM event_fields
                GROUP BY event_id
            )
            SELECT
                e.source_file AS Source_File,
                COALESCE(f.Artifact_Type,'') AS Artifact_Type,
                COALESCE(f.Source_Database,'') AS Source_Database,
                COALESCE(f.Source_Table,'') AS Source_Table,
                e.operation AS Operation,
                e.event_time_confidence AS Time_Confidence,
                COUNT(*) AS Rows,
                SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=1 THEN 1 ELSE 0 END) AS Behavioral_Events,
                MIN(NULLIF(e.creation_date_utc,'')) AS First_Event_Utc,
                MAX(NULLIF(e.creation_date_utc,'')) AS Last_Event_Utc,
                MIN(COALESCE(NULLIF(f.Cloud_Account,''), NULLIF(f.OneDrive_User_Email,''), '')) AS Sample_Account,
                MIN(COALESCE(NULLIF(f.Display_Target,''), NULLIF(e.object_id,''), '')) AS Sample_Target,
                MIN(COALESCE(NULLIF(f.Sample_OneDriveDb_Fields,''), '')) AS Sample_OneDriveDb_Fields
            FROM events e
            LEFT JOIN field_pivot f ON f.event_id = e.event_id
            WHERE IFNULL(e.data_source,'')='OneDrive'
               OR IFNULL(e.operation,'') LIKE 'OneDrive_%'
               OR IFNULL(f.Artifact_Type,'') LIKE 'OneDrive%'
            GROUP BY e.source_file, f.Artifact_Type, f.Source_Database, f.Source_Table, e.operation, e.event_time_confidence
            ORDER BY Rows DESC, e.source_file, f.Source_Table;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportEventSummaryNoUal(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            WITH parser_fields AS (
                SELECT event_id, MIN(field_value) AS parser_name
                FROM event_fields
                WHERE field_name = 'ParserName'
                GROUP BY event_id
            )
            SELECT
                COALESCE(p.parser_name, e.data_source) AS Parser,
                e.data_source AS Data_Source,
                e.operation AS Operation,
                e.forensic_status AS Forensic_Status,
                e.result_status AS Result_Status,
                e.source_file AS Source_File,
                COUNT(*) AS Events_Imported,
                SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=1 THEN 1 ELSE 0 END) AS Behavioral_Events,
                SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=0 THEN 1 ELSE 0 END) AS Non_Behavioral_Events,
                MIN(NULLIF(e.creation_date_utc,'')) AS First_Event_Utc,
                MAX(NULLIF(e.creation_date_utc,'')) AS Last_Event_Utc
            FROM events e
            LEFT JOIN parser_fields p ON p.event_id = e.event_id
            WHERE IFNULL(e.data_source,'') NOT LIKE 'O365%'
              AND IFNULL(e.source_file,'') NOT LIKE 'Evidence-O365logs-%'
            GROUP BY Parser, e.data_source, e.operation, e.forensic_status, e.result_status, e.source_file
            ORDER BY Parser, Events_Imported DESC, e.source_file;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportMetadataFallbackSources(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            WITH quality_fields AS (
                SELECT event_id,
                       GROUP_CONCAT(field_name || '=' || field_value, '; ') AS Quality_Fields
                FROM event_fields
                WHERE field_name IN ('ParserConfidence','ParserConfidenceBasis','CoverageStatus','ParserLimitation','ParseError')
                GROUP BY event_id
            )
            SELECT
                e.source_file AS Source_File,
                e.data_source AS Data_Source,
                e.operation AS Operation,
                e.result_status AS Result_Status,
                e.forensic_status AS Forensic_Status,
                e.event_time_basis AS Time_Basis,
                e.event_time_confidence AS Time_Confidence,
                e.timestamp_warning AS Timestamp_Warning,
                COALESCE(q.Quality_Fields, '') AS Quality_Fields,
                COUNT(*) AS Rows
            FROM events e
            LEFT JOIN quality_fields q ON q.event_id = e.event_id
            WHERE IFNULL(e.data_source,'') NOT LIKE 'O365%'
              AND IFNULL(e.source_file,'') NOT LIKE 'Evidence-O365logs-%'
              AND (
                    IFNULL(e.data_source,'') LIKE 'Metadata:%'
                 OR IFNULL(e.operation,'') LIKE '%Fallback%'
                 OR IFNULL(e.operation,'') LIKE '%Metadata%'
                 OR IFNULL(e.result_status,'') LIKE '%Fallback%'
                 OR IFNULL(e.result_status,'') = 'MetadataOnly'
                 OR IFNULL(e.event_time_confidence,'') = 'MetadataOnly'
                 OR IFNULL(q.Quality_Fields,'') LIKE '%not fully parsed%'
                 OR IFNULL(q.Quality_Fields,'') LIKE '%fallback%'
                 OR IFNULL(q.Quality_Fields,'') LIKE '%metadata%'
                 OR IFNULL(q.Quality_Fields,'') LIKE '%ParseError%'
              )
            GROUP BY e.source_file, e.data_source, e.operation, e.result_status, e.forensic_status, e.event_time_basis, e.event_time_confidence, e.timestamp_warning, q.Quality_Fields
            ORDER BY Rows DESC, e.source_file;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportDistinctSourceFilesNoUal(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT
                e.source_file AS Source_File,
                COUNT(*) AS Db_Rows,
                GROUP_CONCAT(DISTINCT e.data_source) AS Data_Sources,
                GROUP_CONCAT(DISTINCT e.operation) AS Operations,
                SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=1 THEN 1 ELSE 0 END) AS Behavioral_Events,
                MIN(NULLIF(e.creation_date_utc,'')) AS First_Event_Utc,
                MAX(NULLIF(e.creation_date_utc,'')) AS Last_Event_Utc
            FROM events e
            WHERE IFNULL(e.data_source,'') NOT LIKE 'O365%'
              AND IFNULL(e.source_file,'') NOT LIKE 'Evidence-O365logs-%'
            GROUP BY e.source_file
            ORDER BY Db_Rows DESC, e.source_file;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleSourceCoverage(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT
                e.source_file AS Source_File,
                e.data_source AS Data_Source,
                COALESCE((SELECT field_value FROM event_fields WHERE event_id=e.event_id AND field_name='ParserName' LIMIT 1), e.data_source) AS Parser,
                COALESCE((SELECT field_value FROM event_fields WHERE event_id=e.event_id AND field_name IN ('GoogleAuditFamily','GoogleTakeoutProductFamily','GoogleGeminiArtifactKind','GeminiArtifactKind') LIMIT 1), '') AS Google_Family,
                e.operation AS Operation,
                COUNT(*) AS Rows,
                SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=1 THEN 1 ELSE 0 END) AS Behavioral_Rows,
                MIN(NULLIF(e.creation_date_utc,'')) AS First_Event_Utc,
                MAX(NULLIF(e.creation_date_utc,'')) AS Last_Event_Utc,
                MIN(COALESCE(NULLIF(e.user_id,''),'Unknown')) AS Sample_User,
                MIN(COALESCE(NULLIF(e.client_ip,''),'')) AS Sample_IP,
                MIN(COALESCE(NULLIF(e.object_id,''),'')) AS Sample_Target
            FROM events e
            WHERE IFNULL(e.data_source,'') LIKE 'Google%'
               OR IFNULL(e.data_source,'') LIKE 'Gemini%'
               OR IFNULL(e.operation,'') LIKE 'Google%'
               OR IFNULL(e.operation,'') LIKE 'Gemini%'
            GROUP BY e.source_file, e.data_source, Parser, Google_Family, e.operation
            ORDER BY Rows DESC, Source_File, Data_Source;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleAuditFamilySummary(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            WITH gf AS (
                SELECT event_id,
                       MAX(CASE WHEN field_name='GoogleAuditFamily' THEN field_value END) AS GoogleAuditFamily,
                       MAX(CASE WHEN field_name='GoogleTakeoutProductFamily' THEN field_value END) AS GoogleTakeoutProductFamily,
                       MAX(CASE WHEN field_name='GoogleGeminiArtifactKind' THEN field_value END) AS GoogleGeminiArtifactKind,
                       MAX(CASE WHEN field_name='GeminiArtifactKind' THEN field_value END) AS GeminiArtifactKind
                FROM event_fields
                GROUP BY event_id
            )
            SELECT
                COALESCE(gf.GoogleAuditFamily, gf.GoogleTakeoutProductFamily, gf.GoogleGeminiArtifactKind, gf.GeminiArtifactKind, e.data_source) AS Google_Family,
                e.data_source AS Data_Source,
                COUNT(*) AS Rows,
                COUNT(DISTINCT e.source_file) AS Source_Files,
                SUM(CASE WHEN IFNULL(e.is_behavioral_timestamp,0)=1 THEN 1 ELSE 0 END) AS Behavioral_Rows,
                MIN(NULLIF(e.creation_date_utc,'')) AS First_Event_Utc,
                MAX(NULLIF(e.creation_date_utc,'')) AS Last_Event_Utc
            FROM events e
            LEFT JOIN gf ON gf.event_id=e.event_id
            WHERE IFNULL(e.data_source,'') LIKE 'Google%' OR IFNULL(e.data_source,'') LIKE 'Gemini%' OR IFNULL(e.operation,'') LIKE 'Google%' OR IFNULL(e.operation,'') LIKE 'Gemini%'
            GROUP BY Google_Family, e.data_source
            ORDER BY Rows DESC, Google_Family;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleSchemaCoverage(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT
                e.source_file AS Source_File,
                e.data_source AS Data_Source,
                COALESCE((SELECT field_value FROM event_fields WHERE event_id=e.event_id AND field_name IN ('GoogleAuditFamily','GoogleTakeoutProductFamily','GoogleGeminiArtifactKind','GeminiArtifactKind') LIMIT 1), '') AS Google_Family,
                ef.field_name AS Field_Name,
                COUNT(*) AS Populated_Rows,
                MIN(SUBSTR(ef.field_value,1,240)) AS Sample_Value
            FROM events e
            JOIN event_fields ef ON ef.event_id=e.event_id
            WHERE (IFNULL(e.data_source,'') LIKE 'Google%' OR IFNULL(e.data_source,'') LIKE 'Gemini%' OR IFNULL(e.operation,'') LIKE 'Google%' OR IFNULL(e.operation,'') LIKE 'Gemini%')
              AND IFNULL(ef.field_value,'') <> ''
            GROUP BY e.source_file, e.data_source, Google_Family, ef.field_name
            ORDER BY e.source_file, e.data_source, ef.field_name;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleFieldCollisionReview(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT
                e.source_file AS Source_File,
                e.data_source AS Data_Source,
                COALESCE((SELECT field_value FROM event_fields WHERE event_id=e.event_id AND field_name IN ('GoogleAuditFamily','GoogleTakeoutProductFamily','GoogleGeminiArtifactKind','GeminiArtifactKind') LIMIT 1), '') AS Google_Family,
                ef.field_name AS Collision_Field,
                COUNT(*) AS Rows,
                MIN(SUBSTR(ef.field_value,1,240)) AS Sample_Value
            FROM events e
            JOIN event_fields ef ON ef.event_id=e.event_id
            WHERE (IFNULL(e.data_source,'') LIKE 'Google%' OR IFNULL(e.data_source,'') LIKE 'Gemini%' OR IFNULL(e.operation,'') LIKE 'Google%' OR IFNULL(e.operation,'') LIKE 'Gemini%')
              AND ef.field_name IN ('ActivityParameters','ActorIpAddress','ClientIP','ClientIPAddress','DisplayTarget','Document_Type','FileName','FileSizeBytes','ObjectId','OperationOriginal','ResultStatus','SiteUrl','SourceRelativeUrl','TargetPath','UserAgent','Visibility_Change')
            GROUP BY e.source_file, e.data_source, Google_Family, ef.field_name
            ORDER BY Rows DESC, e.source_file, e.data_source, ef.field_name;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleRiskSummary(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT
                rh.rule_code AS Rule_Code,
                rh.rule_name AS Rule_Name,
                rh.risk_domain AS Risk_Domain,
                rh.risk_level AS Risk_Level,
                COUNT(*) AS Hits,
                COUNT(DISTINCT e.source_file) AS Source_Files,
                MIN(NULLIF(e.creation_date_utc,'')) AS First_Event_Utc,
                MAX(NULLIF(e.creation_date_utc,'')) AS Last_Event_Utc,
                MIN(COALESCE(NULLIF(e.user_id,''),'Unknown')) AS Sample_User,
                MIN(COALESCE(NULLIF(rh.supporting_value,''), NULLIF(e.object_id,''), '')) AS Sample_Supporting_Value
            FROM risk_hits rh
            JOIN events e ON e.event_id=rh.event_id
            WHERE IFNULL(e.data_source,'') LIKE 'Google%' OR IFNULL(e.data_source,'') LIKE 'Gemini%' OR IFNULL(e.operation,'') LIKE 'Google%' OR IFNULL(e.operation,'') LIKE 'Gemini%'
            GROUP BY rh.rule_code, rh.rule_name, rh.risk_domain, rh.risk_level
            ORDER BY Hits DESC, rh.rule_code;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleUnmappedColumns(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT
                e.source_file AS Source_File,
                e.data_source AS Data_Source,
                COALESCE((SELECT field_value FROM event_fields WHERE event_id=e.event_id AND field_name IN ('GoogleAuditFamily','GoogleTakeoutProductFamily','GoogleGeminiArtifactKind','GeminiArtifactKind') LIMIT 1), '') AS Google_Family,
                gr.field_name AS Preserved_Source_Column,
                COUNT(*) AS Rows,
                MIN(SUBSTR(gr.field_value,1,240)) AS Sample_Value
            FROM events e
            JOIN google_event_raw_fields gr ON gr.event_id=e.event_id
            WHERE IFNULL(gr.field_value,'') <> ''
            GROUP BY e.source_file, e.data_source, Google_Family, gr.field_name
            ORDER BY e.source_file, e.data_source, gr.field_name;";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int ExportGoogleMetadataStorageSummary(string dbPath, string outputPath)
    {
        using var conn = DatabaseCore.Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            SELECT 'events' AS Metric, CAST(COUNT(*) AS TEXT) AS Value FROM events
            UNION ALL
            SELECT 'event_fields', CAST(COUNT(*) AS TEXT) FROM event_fields
            UNION ALL
            SELECT 'google_event_raw_fields', CAST(COUNT(*) AS TEXT) FROM google_event_raw_fields
            UNION ALL
            SELECT 'avg_event_fields_per_event', printf('%.2f', (SELECT COUNT(*) * 1.0 FROM event_fields) / NULLIF((SELECT COUNT(*) FROM events),0))
            UNION ALL
            SELECT 'avg_google_raw_fields_per_event', printf('%.2f', (SELECT COUNT(*) * 1.0 FROM google_event_raw_fields) / NULLIF((SELECT COUNT(*) FROM events),0));";
        return WriteReaderCsv(cmd, outputPath);
    }

    private static int WriteReaderCsv(SqliteCommand cmd, string outputPath)
    {
        using var reader = cmd.ExecuteReader();
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(CsvEscape(reader.GetName(i)));
        }
        writer.WriteLine();

        var rows = 0;
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) writer.Write(',');
                writer.Write(CsvEscape(reader.IsDBNull(i) ? string.Empty : reader.GetValue(i)?.ToString() ?? string.Empty));
            }
            writer.WriteLine();
            rows++;
        }
        return rows;
    }

    private static void WriteDataTableCsv(string outputPath, DataTable table)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(',', table.Columns.Cast<DataColumn>().Select(c => CsvEscape(c.ColumnName))));
        foreach (DataRow row in table.Rows)
            writer.WriteLine(string.Join(',', table.Columns.Cast<DataColumn>().Select(c => CsvEscape(row[c]?.ToString() ?? string.Empty))));
    }

    private static void WriteEmptyCsv(string outputPath, string column, string value)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(CsvEscape(column));
        writer.WriteLine(CsvEscape(value));
    }

    private static void WriteCaseSourceManifest(string tempRoot, CaseFile caseFile, string caseFolder, string dbPath, int sourceRows, int parserRows, int errorRows, int eventSummaryRows, int fallbackRows, int distinctSourceRows)
    {
        var manifest = new
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            Application = AppInfo.DisplayName,
            Case = new
            {
                caseFile.CaseName,
                caseFile.CaseNumber,
                caseFile.SubjectName,
                caseFile.Company,
                caseFile.Investigator,
                CaseFolder = caseFolder,
                DatabasePath = dbPath
            },
            Counts = new
            {
                SourceCoverageRows = sourceRows,
                ParserCoverageRows = parserRows,
                ParserErrorRows = errorRows,
                EventSummaryNoUalRows = eventSummaryRows,
                MetadataFallbackRows = fallbackRows,
                DistinctSourceFileRows = distinctSourceRows,
                CaseSourceRecords = caseFile.Sources?.Count ?? 0
            },
            Sources = (caseFile.Sources ?? new List<SourceFileRecord>()).Select(s => new
            {
                s.FileName,
                s.LocalPath,
                s.OriginalSourcePath,
                NormalizedOriginalSourcePath = NormalizePathForComparison(s.OriginalSourcePath),
                SourcePathKey = BuildPathKey(s.OriginalSourcePath),
                s.OriginalCreatedUtc,
                s.OriginalAccessedUtc,
                s.OriginalModifiedUtc,
                s.SourceType,
                s.Status,
                s.ImportedToDb,
                s.EventsImported,
                s.ParserName,
                s.FileSizeBytes,
                s.HashAlgorithm,
                s.HashValue,
                s.LastIngestUtc
            }).ToList()
        };
        File.WriteAllText(Path.Combine(tempRoot, "vestigant_case_source_manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteReadme(string tempRoot, CaseFile caseFile, int sourceRows, int parserRows, int errorRows, int eventSummaryRows, int fallbackRows, int distinctSourceRows, int candidateConflictRows, int oneDriveSummaryRows, int googleSourceRows, int googleFamilyRows, int googleSchemaRows, int googleRiskRows, int googleUnmappedRows, int googleCollisionRows, int googleStorageRows, bool dbExists)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Vestigant Triage Validation Bundle");
        sb.AppendLine("===================================");
        sb.AppendLine();
        sb.AppendLine($"GeneratedUtc: {DateTime.UtcNow:O}");
        sb.AppendLine($"Application: {AppInfo.DisplayName}");
        sb.AppendLine($"CaseName: {caseFile.CaseName}");
        sb.AppendLine($"DatabaseAvailable: {dbExists}");
        sb.AppendLine();
        sb.AppendLine("Files:");
        sb.AppendLine($"- vestigant_source_coverage.csv: {sourceRows:N0} rows. Source artifact manifest with normalized path fields for AXIOM Source/Location comparison.");
        sb.AppendLine($"- vestigant_parser_coverage.csv: {parserRows:N0} rows. Parser-level candidate/event/error summary.");
        sb.AppendLine($"- vestigant_parser_errors.csv: {errorRows:N0} rows. Parse errors and parser exception rows.");
        sb.AppendLine($"- vestigant_event_summary_no_ual.csv: {eventSummaryRows:N0} rows. Compact event summary excluding O365 Unified Audit Log rows.");
        sb.AppendLine($"- vestigant_metadata_fallback_sources.csv: {fallbackRows:N0} rows. Metadata-only, fallback, not-fully-parsed, and parse-error source summaries.");
        sb.AppendLine($"- vestigant_distinct_source_files_no_ual.csv: {distinctSourceRows:N0} rows. Distinct source files represented in the local forensic event database, excluding UAL.");
        sb.AppendLine($"- vestigant_parser_candidate_conflicts.csv: {candidateConflictRows:N0} rows. Source artifacts matching multiple parser families; useful for routing false positives.");
        sb.AppendLine($"- vestigant_onedrive_catalog_summary.csv: {oneDriveSummaryRows:N0} rows. Compact OneDrive registry/config/database/source-table summary for catalog parity review.");
        sb.AppendLine($"- vestigant_google_source_coverage.csv: {googleSourceRows:N0} rows. Google source/file/parser/operation coverage summary.");
        sb.AppendLine($"- vestigant_google_audit_family_summary.csv: {googleFamilyRows:N0} rows. Google Workspace audit, Takeout, and Gemini family count summary.");
        sb.AppendLine($"- vestigant_google_schema_coverage.csv: {googleSchemaRows:N0} rows. Preserved Google field coverage by source and family.");
        sb.AppendLine($"- vestigant_google_unmapped_columns.csv: {googleUnmappedRows:N0} rows. Google raw source columns preserved in the non-indexed google_event_raw_fields table and not promoted into generic endpoint/O365 fields.");
        sb.AppendLine($"- vestigant_google_field_collision_review.csv: {googleCollisionRows:N0} rows. Google rows that still populate collision-prone generic metadata fields and require review; expected target is zero except explicitly tolerated parser fields.");
        sb.AppendLine($"- vestigant_google_metadata_storage_summary.csv: {googleStorageRows:N0} rows. Google metadata storage metrics for events, indexed metadata fields, raw source fields, and average fields per event.");
        sb.AppendLine($"- vestigant_google_risk_summary.csv: {googleRiskRows:N0} rows. Google-specific risk-hit summary after risk analysis has been run.");
        sb.AppendLine("- vestigant_case_source_manifest.json: case/source manifest for repeatable validation.");
        sb.AppendLine();
        sb.AppendLine("Suggested comparison order:");
        sb.AppendLine("1. Compare AXIOM Source/Location values to vestigant_source_coverage.csv Normalized_Original_Source_Path and Source_Path_Key.");
        sb.AppendLine("2. Check Parser_Name / Candidate_Parsers / Events_Imported for source artifacts present in both tools.");
        sb.AppendLine("3. Review vestigant_metadata_fallback_sources.csv where Vestigant found a source artifact but emitted only fallback or partial rows.");
        sb.AppendLine("4. Use vestigant_event_summary_no_ual.csv for artifact-family count comparisons without exporting the full SQLite database.");
        sb.AppendLine("5. Review vestigant_parser_candidate_conflicts.csv for parser-routing ambiguity, especially mutually exclusive artifact families.");
        sb.AppendLine("6. Review vestigant_onedrive_catalog_summary.csv before requesting the full SQLite database for OneDrive parity questions.");
        sb.AppendLine("7. Review vestigant_google_* validation CSVs to confirm Google audit family coverage, schema drift, and Google-specific risk scoring.");
        File.WriteAllText(Path.Combine(tempRoot, "README_validation_bundle.txt"), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string CsvEscape(string value)
    {
        value ??= string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string NormalizePathForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().Trim('"').Replace('/', '\\');
        while (text.Contains("\\\\", StringComparison.Ordinal)) text = text.Replace("\\\\", "\\", StringComparison.Ordinal);
        return text.Trim();
    }

    private static string BuildPathKey(string? value)
    {
        var path = NormalizePathForComparison(value).ToLowerInvariant();
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') path = path[2..];
        path = path.TrimStart('\\');
        return path;
    }
}

internal sealed record ValidationBundleResult(string ZipPath, long ZipBytes, int SourceCoverageRows, int ParserCoverageRows, int ParserErrorRows, int EventSummaryRows, int MetadataFallbackRows, int DistinctSourceFileRows);
