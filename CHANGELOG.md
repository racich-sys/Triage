# CHANGELOG

## v3.7.0 - Google Takeout/Gemini content expansion

- Expanded Google Takeout ingestion based on uploaded sample archives rather than assumptions.
- Added Takeout My Activity HTML promotion for Gemini Apps, Drive, Takeout, and other My Activity HTML exports with timestamp extraction.
- Added Google Chat `messages.json` promotion to message-level events with creator, message ID, topic ID, message text preview, and first URL metadata.
- Added Google Meet `conference_history_records.csv` promotion to meeting-participation events with meeting code, conference ID, start/end, duration, and participation state.
- Added Google Calendar `.ics` VEVENT promotion to calendar events with title, UID, start/end, organizer, and location metadata.
- Added nested Takeout ZIP inventory and bounded nested parsing for nested Takeout archives.
- Improved Gemini session archive parsing by extracting text previews and risk-term summaries from transcript/code text artifacts while preserving screenshots/PDFs as metadata-only inventory.
- Updated Google source family classification, headless Google candidate discovery, validation assertions, ai_context.md, and PowerShell test commands for v3.7.0.

## v3.7.0 - Release-script version assertion hotfix

- Fixed `tools/Create-TriageUploadBundle.ps1` default `$Version` token from stale `3_6_3` to current `3_7_0`.
- Added/kept build-validation checks requiring the upload-bundle default version to match the current package before packaging.
- Added wrapper-safety assertions requiring Google thin-test PowerShell to use splatted named parameters and not fragile positional runner argument arrays.
- Documented the repeated release-blocking failure pattern in `ai_context.md` so future package generation must review version strings, wrapper scripts, and runnable PowerShell commands together.

## v3.6.3.1 - Google thin-test wrapper argument-binding hotfix

- Fixed `RUN_GOOGLE_THIN_TEST_V3_6_3_1.ps1` to call `RUN_GOOGLE_SOURCE_TRIAGE.ps1` with hashtable splatting instead of array argument forwarding, preventing literal parameter names such as `-GoogleRoot` from being bound as path values.
- Added phase-based `headless_google_run_status.txt` updates for Google source runs, including discovery, ingest, risk, validation export, and completion phases.
- Added risk-engine progress logging every 10,000 events or 60 seconds, plus explicit logs before and after baseline, sequence, persistence, and score-update phases.
- Updated `RUN_GOOGLE_SOURCE_TRIAGE.ps1` to monitor the headless process and emit heartbeat snapshots with process memory, CPU seconds, case database size, status fields, and recent headless log lines.
- Added `RUN_GOOGLE_THIN_TEST_V3_6_3_1.ps1`, which skips risk by default for thin validation unless `-IncludeRisk` is supplied.
- Preserved the v3.6.2 Google metadata-storage hotfix behavior; no Google parser semantic change was intended.

## v3.6.2 - Google metadata storage size hotfix

- Narrow hotfix for the v3.6.x Google schema-separation storage expansion observed during thin testing, where `case.db` grew beyond the prior v3.4.4 case size and `event_fields` plus value indexes dominated database size.
- Added non-indexed `google_event_raw_fields` storage for `GoogleAuditRaw_*` and `GoogleTakeoutRaw_*` source columns so raw Google source columns remain preserved without inflating the globally indexed `event_fields` table.
- Kept reviewed Google canonical fields in `event_fields`, but skipped Google fields that merely duplicate normalized `events` table columns, skipped `GoogleRawSerializedRow` because it is already stored in `events.raw_json`, skipped `GoogleMasterExportSchema`, and skipped negative/default `GoogleRisk* = No` flags.
- Replaced broad global value indexes on `event_fields(field_value)` and `event_fields(field_name, field_value)` with a smaller `event_fields(field_name)` index while preserving the existing `(event_id, field_name)` lookup index.
- Updated Google validation exports so unmapped/raw Google source-column review reads from `google_event_raw_fields` and added `vestigant_google_metadata_storage_summary.csv` with event, metadata, raw-field, and average fields-per-event metrics.
- No Google parser routing, risk-rule mapping, Master export preferred-header restriction, GUI behavior, or non-Google parser behavior was intentionally changed.

## v3.6.1 - DatabaseIngest build fix after Google schema separation

- Fixed a build failure in `Core/DatabaseIngest.cs` where the v3.6.0 patch truncated the file after `if (rowCount % 10000 == 0)`.
- Restored the import progress logging, transaction commit, ingest result return, and helper methods from the prior working implementation while preserving the v3.6.0 Google schema-separation logic above the insert path.
- Added a build-validation assertion to detect the `DatabaseIngest.cs` import-completion marker so this truncation does not recur silently.
- Updated `ai_context.md` with the v3.6.0 build failure and Graveyard note.
- No Google parser, risk-rule, Master export, validation-bundle schema, or UI behavior was intentionally changed.

## v3.6.0 - Google cloud schema separation and validation hardening

- Reviewed the user-supplied Google review note that warned against shoehorning cloud audit and Takeout fields into endpoint/O365-style buckets.
- Preserved the v3.5.0 Google readability improvements while separating Google Workspace Audit, Google Takeout, and Gemini metadata into Google-prefixed fields.
- Google raw source columns are now stored as `GoogleAuditRaw_*` or `GoogleTakeoutRaw_*` metadata instead of unprefixed fields such as `Title`, `Event`, `Description`, `IP Address`, or `User Agent`.
- Added canonical Google fields such as `GoogleRecordType`, `GoogleSourceFamily`, `GoogleActor`, `GoogleClientIp`, `GoogleUserAgent`, `GoogleOperationRaw`, `GoogleOperationNormalized`, `GoogleTarget`, `GoogleStableObjectId`, and `GoogleRawSerializedRow`.
- Updated Workspace Audit promotion to use Google-prefixed names such as `GoogleActivityParameters`, `GoogleVisibilityChange`, `GoogleDocumentType`, `GoogleDrive_Title`, `GoogleGmail_Subject`, `GoogleOAuth_Scope`, and `GoogleGemini_Action`.
- Updated Takeout and Gemini parsers so product/archive/session metadata is retained under Google-prefixed names while the normalized event still has enough generic fields for timeline ordering, search, and risk correlation.
- Updated Master metadata export so Google rows do not populate the full preferred O365/endpoint header set. Only limited universal columns are filled; cloud-specific values remain in appended Google-prefixed metadata and `Vestigant_*` database columns.
- Added `vestigant_google_field_collision_review.csv` to the validation bundle so any remaining Google rows that populate collision-prone generic metadata fields are visible and reviewable.
- Updated ingest logic to respect parser-supplied `IsBehavioralTimestamp` and `TimestampWarning` values instead of deriving behavior solely from the presence of a timestamp.
- Updated `ai_context.md` with the Google schema-separation decision and new Graveyard entries.

## v3.4.4 - Google fast-test MBOX exclusion

- Updated headless Google source discovery so `.mbox` files are excluded by default for fast Google parser validation.
- Added `--include-mbox` support in the executable and `-IncludeMbox` support in `RUN_GOOGLE_SOURCE_TRIAGE.ps1` for later intentional MBOX testing.
- Added `mbox_included=` to `headless_google_run_status.txt` so review bundles show whether large Mail/MBOX sources were included.
- Added log messages for skipped MBOX files so the omission is explicit, reviewable, and not confused with parser miss coverage.
- Updated build validation and `ai_context.md` for the MBOX test-scope decision.
- No Google audit CSV, Takeout ZIP/JSON/CSV, Gemini, risk-engine, or Master metadata export behavior was otherwise intentionally changed.

## v3.4.3 - Google source headless automation

- Added `--headless-google` command-line mode.
- Added `HeadlessTriageRunner.RunGoogleSourceTriage()` to create a case, discover Google source files, ingest them, optionally run risk, export a validation bundle, and report headless status.
- Added `RUN_GOOGLE_SOURCE_TRIAGE.ps1`, defaulting to `E:\0445_0001` and `Q:\TriageCase`, to test the visible Google Audit/Takeout/Gemini/Mail source set without opening the GUI.
- The Google runner auto-discovers candidate Google Audit/Investigation, Takeout, Gemini, and Mail/MBOX files under a root folder.
- Source hashing is skipped by default for fast Google parser validation and can be enabled with `-HashGoogleSources` for final/source-authenticity validation.
- Upload-bundle packaging now includes `RUN_GOOGLE_SOURCE_TRIAGE.ps1` with project documentation.
- Updated `ai_context.md` with the current Google testing workflow and Graveyard notes.

## v3.4.2 - SetupAPI Static ParserName Build Fix

- Fixed a build failure in `Parsers/SetupApiDevLogParser.cs` where static helper code referenced the instance `ParserName` property.
- Added a `ParserDisplayName` constant and used it from static code paths so SetupAPI parse-quality metadata remains consistent without requiring an object reference.
- Updated `ai_context.md` with this build failure in the roadmap and Graveyard.
- Preserved the v3.4.0/v3.4.1 Google source framework and the root `ai_context.md` continuity file.
- No parser behavior, evidence interpretation, risk scoring, validation-bundle output, or UI workflow was intentionally changed.

## v3.4.1 - Google framework build fix and ai_context baseline

- Fixed `Parsers\ParserRegistry.cs` so Google source path normalization uses the valid C# source form `Replace('/', '\\')` instead of an unterminated backslash character literal.
- Added root `ai_context.md` as the living project context file.
- Updated upload-bundle packaging to include `ai_context.md` with project documentation.
- Added build-validation assertions confirming `ai_context.md` exists and is included by upload packaging.
- Preserved all v3.4.0 Google source framework changes.

## v3.4.0 - Google source framework

- Adds a Google source family framework rather than another narrow v3.3.x hotfix.
- Adds Google Workspace Audit & Investigation CSV/ZIP ingestion.
- Adds first-pass Google Takeout archive/export parsing for Activities CSV, Devices CSV, Mail User Settings JSON, and selected YouTube/YouTube Music/product entries.
- Adds Gemini Session Archive artifact inventory for transcripts, code extracts, output PDFs, and screenshots.
- Adds Google-specific risk rules for Takeout, Drive, Gmail, OAuth, account/device access, Vault/Admin, and Gemini/AI use.
- Adds Google validation-bundle exports: source coverage, audit family summary, schema coverage, unmapped columns, and Google risk summary.
- Preserves all prior v3.3.x parser/UI/build hardening.

## v3.3.21 - SetupAPI transfer/destruction-relevant triage expansion

- Expanded SetupAPI parsing beyond USB mass-storage first-install sections.
- Added SetupAPI application log recognition for `setupapi.app.log` and `.old` variants.
- Fast triage now collects `Windows\inf\setupapi.app.log` and `Windows\inf\setupapi.app.log.old` in addition to the device logs.
- Added high-value SetupAPI classifiers for USB storage, WPD/MTP/mobile devices, storage controllers/volumes, network adapters, Bluetooth, serial/debug interfaces, generic USB interfaces, and destruction/wiping/formatting-related application or driver sections.
- Added metadata fields for SetupAPI log type, device category, transfer potential, destruction potential, risk relevance, risk reason, command line, target, class GUID, driver INF, device identifiers, and context lines.
- Added risk rules for SetupAPI transfer-capable device/interface/driver evidence and possible destructive/wiping/formatting application or driver evidence.
- Preserved v3.3.20 print artifact coverage, v3.3.19 Master metadata export header alignment, and v3.3.18 DataGridView popup suppression.
- Kept `CHANGELOG.md` as the single version-difference file.

This is the single version-difference record for Vestigant Triage. Separate root-level per-version `V*_*.md` release-note files are intentionally not shipped.

## v3.3.20 - Print artifact triage expansion

- Reviewed current print coverage and confirmed the existing parser already recognized core `.SHD` and `.SPL` spool artifacts, conditionally recognized `.EMF`, `.XPS`, `.OXPS`, `.PRN`, and `.TMP` print-spool candidates, and normalized PrintService event ID 307 when XML hydration was enabled.
- Expanded print artifact triage routing to include additional print payload/configuration candidates: `.PCL`, `.PJL`, `.PS`, `.EPS`, `.RAW`, `.BUD`, `.GPD`, `.PPD`, `.NTF`, and print-related `.DAT` files when they are in print spool/configuration paths or otherwise clearly print-named.
- Expanded fast triage extraction from `Windows\System32\spool\PRINTERS` to also inspect `spool\SERVERS`, `spool\drivers`, and `spool\PRTPROCS` with extension-gated extraction so broad driver binaries are not copied by default.
- Expanded deleted/MFT print-artifact recovery eligibility through the shared print artifact router, so deleted print queue payload candidates are considered beyond only `.SPL` and `.SHD`.
- Improved `PrintSpoolParser` output with `PrintSpoolEvidenceRole`, spool filename stem, numeric job-id candidate, SHD/SPL pairing candidates, EMF/PJL/PostScript/PCL-style signature markers, and print configuration artifact classification.
- Expanded targeted PrintService EVTX normalization beyond event ID 307 so additional PrintService event records are categorized as print evidence while avoiding unsupported claims about event meaning.
- Preserved v3.3.19 Master metadata export header alignment and v3.3.18 DataGridView popup suppression.

## v3.3.19 - Master metadata export header alignment

- Updated the Master Timeline & Metadata tab's `Export All Master Metadata CSV` workflow so the first columns follow the uploaded Purview/UAL-style preferred header sequence.
- The preferred header sequence begins with `RecordId`, `CreationDate`, `RecordType`, `Operation`, and `UserId`, and ends with `ZipFileName`.
- Vestigant-specific base event columns are appended after the preferred header set with `Vestigant_` prefixes.
- Tag names are appended as `Vestigant_Tags`.
- Dynamic metadata fields that are not already represented by the preferred header set are appended at the end as `Metadata_<field>` columns.
- The export remains database-backed and streaming rather than limited to the visible/paginated grid.
- Preserved v3.3.18 DataGridView display-conversion popup suppression.
- No parser, ingest, validation-bundle, risk-engine, or raw-image discovery behavior was intentionally changed.

## v3.3.16 - Build-validation literal `$I` assertion fix

- Fixed `tools\Build-And-Validate-VestigantTriage.ps1` so the Recycle Bin parser assertion description treats `$I` as a literal string instead of an expandable PowerShell variable.
- This resolves the Windows PowerShell strict-mode failure: `The variable '$I' cannot be retrieved because it has not been set.`
- Preserved the v3.3.15 Master Timeline & Metadata full metadata CSV export.
- Kept `CHANGELOG.md` as the single version-difference record; no root-level per-version `V*_*.md` files were reintroduced.
- No parser, ingest, validation-bundle, risk-engine, UI behavior beyond the existing v3.3.15 export button, or raw-image discovery behavior was intentionally changed.

## v3.3.15 - Master metadata CSV export

- Added a Master Timeline & Metadata tab button labeled `Export All Master Metadata CSV`.
- The export is database-backed and streams every row from the `events` table rather than exporting only the currently loaded/paginated grid page.
- The CSV includes every base `events` column, tag names, and dynamic `event_fields` metadata values pivoted into `Metadata_<field>` columns.
- Duplicate metadata values for the same event and field are joined with `; ` so repeated metadata is preserved in one CSV row per event.
- The export writes UTF-8 CSV with a BOM for Excel compatibility and reports progress in the status bar during long exports.
- Added static build-validation assertions for the Master metadata export button and database export method.
- Preserved v3.3.14 headless validation export hardening.
- No parser-routing, validation-bundle, risk-engine, or raw-image discovery behavior was intentionally changed.

## v3.3.14 - Headless validation export failure hardening

- Reviewed the v3.3.13 Windows result: build/validation completed, then the fixed-path headless run returned exit code 2 during runtime.
- The wrapper failure occurred after the executable launched, so this was not the earlier changelog assertion problem.
- Hardened `ValidationBundleService.ExportValidationBundle()` so compact validation CSV query/export failures write warning CSVs instead of aborting the entire headless run after ingest.
- Hardened `HeadlessTriageRunner` so a validation-bundle export exception after successful ingest is logged to `validation_bundle_export_error.txt`, recorded in `headless_run_status.txt`, and does not cause the whole headless image triage run to return exit code 2.
- Kept the wrapper fallback path that can run standalone validation-bundle export if the headless run did not create a bundle.
- Added wrapper failure diagnostics: if the headless executable still exits non-zero, `RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1` now appends `headless_run_status.txt` and the last 80 lines of `headless_image_triage.log` to the wrapper run log before throwing.
- Preserved the v3.3.13 changelog assertion fix and v3.3.12 OneDrive config-file inventory changes.
- No parser-routing, risk-engine, UI, or raw-image discovery behavior was intentionally changed.

## v3.3.13 - Build-validation changelog assertion fix

- Reviewed the v3.3.12 Windows build-validation failure where `tools\Build-And-Validate-VestigantTriage.ps1` required the current changelog heading to contain the phrase `Validation review`.
- Corrected the changelog assertion so it checks for a current-version heading beginning with `## v<version>` instead of requiring release-title wording.
- Changed the upload-bundle default-version assertion to use the current package version token derived from `$Version` instead of a hard-coded token.
- Kept the v3.3.12 OneDrive config-file inventory and build-log UTF-8 hardening changes intact.
- No parser, ingest, risk-engine, validation-bundle export, or raw-image triage behavior was intentionally changed.

## v3.3.12 - OneDrive config inventory and build-log hardening

- Reviewed the uploaded v3.3.11 headless validation bundle.
- Confirmed the v3.3.11 headless run completed with `status=complete` and produced a real validation bundle.
- Confirmed the v3.3.9 Recycle Bin routing correction remained effective: no Office Owner File / Recycle Bin false-positive conflict appeared in the v3.3.11 parser-candidate conflict export.
- Kept the v3.3.11 OneDrive SQLite/SyncEngine timestamp enrichment intact.
- Added `OneDrive_Config_File_Observed` metadata-only output for OneDrive config/state files that are correctly routed to the OneDrive parser but do not contain a useful decoded OneDrive, SharePoint, URL, path, or account line.
- This should reduce generic `Metadata: Parser matched but emitted no events: OneDrive Artifact Parser` fallback rows for OneDrive `.ini`/`.json` settings artifacts while still avoiding behavioral timestamp claims from source/staged file timestamps.
- Hardened `tools\Build-And-Validate-VestigantTriage.ps1` logging so the build-validation log appends UTF-8 text directly with `Add-Content -Encoding UTF8` rather than writing through `Tee-Object`, which produced UTF-16/NUL-looking output in the v3.3.11 uploaded validation log.
- Added static validation assertions for the new OneDrive config inventory behavior and the build-validation UTF-8 logging marker.

## v3.3.11 - Validation review, changelog correction, and OneDrive timestamp enrichment

- Reviewed the uploaded v3.3.10 headless validation bundle.
- Confirmed the v3.3.10 headless run completed with `status=complete` and produced a real validation bundle.
- Corrected the consolidated changelog so historical sections retain their actual version numbers instead of being relabeled as v3.3.10.
- Fixed `tools\Create-TriageUploadBundle.ps1` stale default version value so standalone upload-bundle output defaults to v3.3.11 instead of v3.3.8.
- Added validation assertions for the upload-bundle default version value and the OneDrive timestamp enrichment helpers.
- Improved OneDrive SQLite/SyncEngine parsing:
  - assigns more specific operations for SyncEngine file, folder, service-operation, hydration, scope, graph metadata, and SafeDelete records;
  - decodes Unix/FileTime-like timestamp columns such as `diskCreationTime`, `diskLastAccessTime`, `timestamp`, `notificationTime`, `firstHydrationTime`, and `lastHydrationTime` where present;
  - records decoded timestamp fields as `OneDriveDb_<column>Utc` values;
  - uses decoded OneDrive database timestamps as medium-confidence artifact-native cloud-sync/catalog activity instead of leaving all database rows metadata-only;
  - prefers path, URL, file-name, and folder-name values before opaque resource IDs when selecting the display target.

## v3.3.10 - Changelog consolidation and package documentation cleanup

- Consolidated prior per-version note files into `CHANGELOG.md`.
- Removed root-level `V*_*.md` note files from the source package.
- Updated upload-bundle packaging to include `CHANGELOG.md` instead of a separate `RELEASE_NOTES.md`.
- Added build validation for the consolidated changelog and absence of root-level per-version note files.
- Updated app/package version strings and fixed-path run documentation to v3.3.10.
- No parser, ingest, validation-bundle, risk-engine, UI, or raw-image triage behavior was intentionally changed.

## v3.3.9 - Validation review, routing, and OneDrive export pass

- Built from the validated v3.3.8.3 fixed-path headless run.
- Tightened `RecycleBinParser.CanParse()` so Office owner/lock files such as `~$ilding Blocks.dotx` no longer match the Recycle Bin `$I` parser simply because the filename contains `$I` after `~`.
- Kept valid Recycle Bin `$I*` and staged/deleted `$I*` matching.
- Added `vestigant_parser_candidate_conflicts.csv` to the validation bundle.
- Added `vestigant_onedrive_catalog_summary.csv` to summarize OneDrive registry/config/database rows by source file, source database, table, operation, and sample target/account fields.

## v3.3.8.3 - Build validation version assertion fix

- Updated app/package version to v3.3.9 for that package line.
- Changed static build-version assertions to use the script `$Version` value and `[regex]::Escape($Version)` instead of stale hard-coded version regexes.
- Preserved the v3.3.8.2 PowerShell status-trim fix.
- No parser, ingest, validation-bundle, database, print-spool, risk-engine, or UI behavior was intentionally changed.

## v3.3.8.2 - Headless status trim fix

- Fixed the PowerShell wrapper status-file parser failure caused by Windows PowerShell 5.1 choosing an incompatible `String.Trim` overload.
- Replaced mixed trim-character calls with regex-based normalization and regex newline splitting.
- Preserved the existing headless raw-image workflow.

## v3.3.8.1 - Headless status parse fix

- Normalized `headless_run_status.txt` before checking completion.
- Removed NUL characters, trimmed BOM/whitespace/CRLF per line, and compared parsed lines case-insensitively against `status=complete`.
- Added validation-script assertion for normalized status handling.
- No parser, ingest, validation-bundle, risk-engine, print-spool, or UI behavior was intentionally changed.

## v3.3.7 - Headless output hardening

- Confirmed the fixed-path headless workflow completed against the raw image test path and generated a real validation bundle.
- Skipped redundant WorkingEvidence 7-Zip archive creation by default for image-backed headless cases and wrote an archive-skip manifest instead.
- Included ArchiveLogs in upload bundles.
- Wrote root automation logs as UTF-8 rather than Tee-Object UTF-16/NUL output.
- Improved Office owner-file username extraction by scoring short plausible owner strings and avoiding the longest binary-contaminated string.
- No broad parser rewrite was included.

## v3.3.6.1 - Fixed-path headless autorun

- Added a minimal-interaction autorun that builds and publishes VestigantTriage, runs `--headless-triage`, uses the fixed raw image path, creates a timestamped case under `Q:\TriageCase`, runs fast triage and ingest, exports a validation bundle, and creates the thin upload ZIP under `D:\Downloads`.
- Adjusted the default autorun path to skip only the synthetic upload-bundle fixture test during autorun preflight while still requiring a real validation bundle for case packaging.

## v3.3.4 - Validation bundle authenticity and upload thinning

- Validated that a validation bundle is a readable ZIP before accepting it for upload inclusion.
- Required generated validation entries: source coverage, parser coverage, parser errors, no-UAL event summary, metadata fallback sources, distinct source files, case source manifest JSON, and validation README.
- Rejected build-fixture validation bundles as real review artifacts.
- Preferred latest cases with valid generated validation bundles when `-RequireValidationBundle` is used.
- Reduced upload-bundle project-copy bloat by excluding `bin`, `obj`, publish output, and fixture directories.
- Avoided broad case-copy inclusion of staged `Live_*` and `Ghost_*` source artifacts by default.
- No parser behavior was intentionally changed.

## v3.3.3 and earlier summarized history

- v3.3.3 fixed upload-bundle PowerShell validation/ZIP creation failures.
- v3.3.x added automatic case creation, risk/ingest locking guards, post-ingest validation-bundle export, office owner-file discovery, print-spool support, and parser-coverage hardening.
- v3.2.x focused on static evidence archive hardening, metadata fallback discipline, parser coverage, performance, and stability.
- v3.1.x improved artifact discovery and parser coverage for ShellBags, browser artifacts, Jump Lists, USN Journal, and UAL attribution.
- v3.0.0 centralized application identity/versioning as `Vestigant Triage v#.#.#`.
- v2.9.1 fixed the EVTX/OAlerts regex compile issue and set the visible application version to v2.9.1.
