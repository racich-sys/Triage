# ai_context.md


## v3.21.0 - Risk/duplicate validation safeguards

Evidence basis: the v3.20.0 default Google thin bundle completed successfully with version 3.20.0, status complete, 7 sources, risk skipped, validation bundle exported, `google_storage_health=OK`, and `google_raw_storage_health=OK`. v3.21.0 therefore advances the roadmap toward the remaining risk-enabled and duplicate/expanded archive validation runs.

Changes in this package:
- Tightened default risk sequence safeguards for full-investigation protection: `sequenceMaxComparisons=250000`, `sequenceMaxHits=50000`, `sequenceProgressEveryComparisons=10000`, `sequenceMaxCandidatesPerUser=2000`, and `sequenceTimeoutSeconds=60`.
- Keeps bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default as roadmap-blocking full-investigation safeguards.
- Keeps validation-bundle reporting for `vestigant_export_safeguards_summary.csv` and `vestigant_google_v4_readiness_summary.csv`.


## Project Overview

Vestigant Triage is a Windows forensic triage application for ingesting endpoint, raw-image, cloud, audit, application, and account-export artifacts into a unified case database for timeline review, metadata export, validation, and risk assessment. The project prioritizes forensic soundness, reproducible workflows, explicit provenance, and fast triage paths that can be expanded into fuller processing.

## Tech Stack

- Language: C#
- Framework: .NET 8.0 Windows / WinForms
- Target runtime: `net8.0-windows`, `win-x64`
- Build/publish environment: Windows PowerShell 5.1+ and .NET SDK 8.x
- Primary UI: Windows Forms
- Case database: SQLite
- Packaging: PowerShell ZIP packaging scripts
- Standard paths:
  - Source ZIPs: `D:\Downloads`
  - Extracted source: `T:\VestigantTriage_v<version>`
  - Case root: `Q:\TriageCase`
  - Fixed raw-image test path: `I:\0186_0015-IT001.E01 - Partition 3 (471.56 GB)_decrypted.img`
  - Current Google test root: `E:\0445_0001`
- Validation style:
  - Windows workstation performs `dotnet build`, `dotnet publish`, and real data runs.
  - Assistant-side validation is limited to static/package checks unless Windows/.NET/source-image access is available.
  - Prefer Perl/sed/Ruby/PowerShell style checks when practical; Python has previously hung in the user’s local environment.


- When a test run is requested, always provide the exact PowerShell commands needed to run it, including expected source ZIP name, extraction path, project directory, script name, and key switches.

## Current State

- Latest generated version: `v3.21.0`.
- `v3.4.4` completed a fast Google test successfully: status complete, 7 sources, MBOX excluded, risk completed, and validation bundle exported.
- `v3.6.0` was a Google cloud-schema separation pass, but the Windows build failed because `Core/DatabaseIngest.cs` was accidentally truncated after a progress-logging `if` statement.
- `v3.6.1` was a narrow build hotfix that restored the missing DatabaseIngest tail while preserving the v3.6.0 Google schema-separation logic.
- During v3.6.1 thin testing, the case database grew beyond 10 GB while other case files stayed small. User-provided SQLite `dbstat` showed `event_fields` and its value indexes dominated size; Google metadata represented about 91.4% of `event_fields` rows.
- `v3.6.2` was a narrow size/performance hotfix that keeps Google schema separation but moves `GoogleAuditRaw_*` / `GoogleTakeoutRaw_*` source columns into `google_event_raw_fields`, skips duplicate/default Google metadata from indexed `event_fields`, drops broad event-field value indexes, and adds Google metadata storage validation metrics.
- During the first v3.6.2 Google thin run, ingest appeared complete but the process was inactive or not visibly progressing for more than 40 minutes after risk analysis started. The status file still only reported `status=started`, which exposed a logging/observability gap.
- The first v3.6.3 thin wrapper run failed before ingest because the wrapper passed literal parameter names into `RUN_GOOGLE_SOURCE_TRIAGE.ps1`, causing `GoogleRoot does not exist: -GoogleRoot`; v3.6.3.1 fixes the wrapper invocation.
- `v3.6.3.1` is a narrow wrapper hotfix on top of v3.6.3 logging/risk-progress changes. It fixes Google thin-test wrapper argument binding by using hashtable splatting and still skips risk by default unless `-IncludeRisk` is supplied.
- `v3.6.3.2` is a narrow release-script hotfix that fixes the stale upload-bundle default version token, keeps the Google thin-test wrapper fix, and adds package/release assertions for wrapper parameter passing and version-token drift.
- `v3.9.1` fixed the v3.9.0 Google thin runtime failure where the published output was missing the native SQLite provider `e_sqlite3.dll`, producing `Microsoft.Data.Sqlite.SqliteConnection` / `SQLitePCLRaw` `DllNotFoundException`.
- `v3.9.2` fixed the follow-on build-validation path assumption by accepting RID-specific build output under `net8.0-windows\win-x64`.
- `v3.10.0` follows the successful v3.9.2 thin upload. It suppresses redundant umbrella `Takeout.zip` archives by default when part-specific Takeout ZIP archives are present, adds `-IncludeDuplicateGoogleArchives` / `--include-duplicate-google-archives` opt-in, and hardens `vestigant_sqlite_object_size_summary.csv` so count/page metrics still export when SQLite `dbstat` is unavailable.
- `v3.21.0` follows review of the successful v3.13.0 thin upload. It compacts repeated low-cardinality Google indexed metadata where the same meaning is already represented by event columns, source/provenance fields, or validation summaries, preserves meaningful searchable fields, and improves SQLite diagnostics when `dbstat` is unavailable.
- The app uses a single consolidated `CHANGELOG.md` rather than separate root-level per-version markdown files.
- Full source packages must include this root-level `ai_context.md` file.
- Existing major functions include local endpoint triage, raw-image headless triage, validation-bundle generation, upload-bundle generation, Master tab all-record metadata export, print artifact expansion, SetupAPI transfer/destruction triage, and Google source ingestion framework.
- The Master tab full metadata export is database-backed and includes the user-provided preferred header sequence first, then appended Vestigant/dynamic metadata.
- DataGridView modal popup loops caused by image-column/string conversion errors were mitigated in v3.3.18.

## Next Steps / Active Goal

Standing workflow: after the user uploads a thin result, review the thin result and proceed directly to the next build/version with scoped fixes and PowerShell test commands unless the user explicitly says to pause.


First validate that `v3.21.0` builds on the Windows workstation, then run the Google thin test against `E:\0445_0001` using `RUN_GOOGLE_THIN_TEST_V3_21_0.ps1`. Confirm that Workspace Audit, Takeout, and Gemini rows ingest correctly; for the default thin test, risk is skipped unless `-IncludeRisk` is supplied. Specific v3.21.0 validation targets: `vestigant_google_indexed_field_summary.csv` should be present; `vestigant_google_metadata_storage_summary.csv` should include threshold rows plus `google_storage_health` and `google_raw_storage_health`; `vestigant_sqlite_object_size_summary.csv` should include page-count byte estimates if `dbstat` is unavailable; validation-export times for schema/collision/unmapped summaries should remain visible; `event_fields / events` and `google_event_raw_fields / events` should be compared against v3.13.0 values of about 28.97 and 13.15 respectively; and Google collision/storage validation CSVs should still be generated.

## A full list of what has been implemented, how and what version the implementation took place in (i.e. Roadmap)

- `v3.2.4`
  - Continued performance/resource work.
  - Planned triage/full processing modes.
  - Identified GUI tab-switching resource pressure.

- `v3.3.4`
  - Added validation-bundle authenticity checks.
  - Required generated validation CSV/JSON/readme entries.
  - Rejected build fixture validation bundles as real case validation bundles.
  - Thinned upload and case copy behavior to avoid build output and staged source-artifact bloat.

- `v3.3.6.1`
  - Added fixed-path headless autorun workflow for the current raw image.
  - Built/published, ran `--headless-triage`, created a timestamped case, ran ingest, exported a validation bundle, and created a thin upload.
  - Skipped only the synthetic upload-bundle fixture during autorun preflight.

- `v3.3.7`
  - Hardened headless output.
  - Skipped redundant WorkingEvidence 7-Zip creation for image-backed cases by default.
  - Added archive-skip manifest and archive diagnostics.
  - Wrote UTF-8 logs without `Tee-Object` UTF-16/NUL output.
  - Improved Office owner-file username extraction.

- `v3.3.10.0`
  - Normalized status files before testing for `status=complete`.

- `v3.3.8.2`
  - Replaced Windows PowerShell 5.1-problematic `String.Trim` overload use with regex-based status normalization.

- `v3.3.8.3`
  - Fixed stale version assertions by deriving version checks from `$Version` and `[regex]::Escape($Version)`.

- `v3.3.9`
  - Tightened Recycle Bin `$I` matching so Office owner/lock files no longer false-match Recycle Bin.
  - Added `vestigant_parser_candidate_conflicts.csv`.
  - Added `vestigant_onedrive_catalog_summary.csv`.

- `v3.3.10`
  - Added consolidated `CHANGELOG.md`.
  - Removed separate root-level `V*_*.md` version-note files and separate `RELEASE_NOTES.md`.
  - Added validation checks to prevent reintroducing root per-version markdown files.

- `v3.3.11`
  - Corrected changelog history from v3.3.10.
  - Fixed stale default version in `Create-TriageUploadBundle.ps1`.
  - Added OneDrive SQLite/SyncEngine timestamp decoding and more specific OneDrive operations.
  - Preferred meaningful OneDrive target values over opaque IDs.

- `v3.3.12`
  - Added `OneDrive_Config_File_Observed` metadata-only rows for OneDrive config/state files.
  - Hardened build-validation logging to append UTF-8 directly.

- `v3.3.13`
  - Relaxed changelog-heading validation to require only the current-version heading.
  - Derived upload-bundle version assertion from `$Version`.

- `v3.3.14`
  - Made validation-bundle compact CSV exports failure-tolerant.
  - Prevented validation-bundle export exceptions from aborting the entire headless run.
  - Added wrapper diagnostics for non-zero headless exits.

- `v3.3.15`
  - Added **Export All Master Metadata CSV** to the Master Timeline & Metadata tab.
  - Implemented database-backed streaming export of all events plus dynamic metadata.

- `v3.3.16`
  - Fixed strict-mode `$I` expansion in PowerShell assertion descriptions.

- `v3.3.17`
  - Fixed strict-mode `.Count` failures by array-wrapping `Get-ChildItem` results.

- `v3.3.18`
  - Added central `DataGridView.DataError` handling.
  - Converted auto-generated image columns to text columns for metadata grids.

- `v3.3.19`
  - Updated Master metadata export so the first columns match the user-provided preferred header order.
  - Appended `Vestigant_Tags`, base `events` columns as `Vestigant_<column>`, and extra dynamic fields as `Metadata_<field>`.

- `v3.3.20`
  - Expanded print artifact coverage for SHD/SPL/EMF/XPS/OXPS/PRN/PCL/PJL/PS/EPS/RAW/TMP/BUD/GPD/PPD/NTF and print-path DAT.
  - Expanded fast triage print collection to additional spool/config paths.
  - Added print artifact role, job ID, pairing, signature, and config metadata.

- `v3.3.21`
  - Expanded SetupAPI parsing for transfer/destruction triage.
  - Added `setupapi.app.log` collection.
  - Classified WPD/MTP/mobile devices, storage controllers/volumes, network adapters, Bluetooth, serial/debug interfaces, generic USB interfaces, and destructive/wiping/formatting sections.
  - Added risk rules `EXF-072` and `CON-071`.

- `v3.4.0`
  - Added Google source framework as a larger version jump.
  - Added Google Workspace Audit & Investigation CSV/ZIP ingestion.
  - Added first-pass Google Takeout ZIP/CSV/JSON ingestion.
  - Added Gemini session archive inventory.
  - Added Google audit-family registry and Google validation outputs.
  - Added Google risk rules `EXF-091`, `EXF-092`, `EXF-093`, `EXF-094`, `ACC-031`, `CON-082`, and `AI-011`.
  - Added GUI source choices for Google Audit, Google Takeout, and Gemini Session sources.
  - Build failure later identified: invalid C# backslash character literal in `ParserRegistry.cs`.

- `v3.4.1`
  - Fixed the invalid backslash character literal in `ParserRegistry.cs`.
  - Added root-level `ai_context.md` and upload-bundle/build-validation coverage for it.
  - Build failure later identified: static helper code referenced instance `ParserName` in `SetupApiDevLogParser.cs`.

- `v3.4.2`
  - Fixed `SetupApiDevLogParser.cs` by adding a static-safe `ParserDisplayName` constant and using it from static helper code.
  - Updated `ai_context.md` and Graveyard with the static/instance parser-name failure.

- `v3.4.3`
  - Added `--headless-google` command-line mode.
  - Added `HeadlessTriageRunner.RunGoogleSourceTriage()`.
  - Added `RUN_GOOGLE_SOURCE_TRIAGE.ps1`, defaulting to `E:\0445_0001` and `Q:\TriageCase`.
  - Google runner auto-discovers candidate Google Audit/Investigation, Takeout, Gemini, and Mail/MBOX files under a root folder.
  - Google runner creates a case, ingests candidate sources, optionally runs risk, exports a validation bundle, and creates a thin upload ZIP.
  - Google runner skips source hashing by default for fast parser validation; use `-HashGoogleSources` for final/source-authenticity runs.
  - Upload bundle includes `RUN_GOOGLE_SOURCE_TRIAGE.ps1` in project documentation.

- `v3.4.4`
  - Excluded large MBOX files by default from `--headless-google` fast validation.
  - Added `--include-mbox` / `-IncludeMbox` opt-in.
  - Wrote `mbox_included=False` to the Google headless status file and logged skipped MBOX files explicitly.

- `v3.5.0`
  - Reviewed the v3.4.4 Google validation bundle and user suggestions.
  - Replaced fuzzy Drive/Gmail operation normalization with exact Google audit event-name mapping for high-value Workspace Audit families.
  - Promoted readable Workspace Audit targets such as Drive title, Gmail subject, attachment name, app name, and device name ahead of opaque IDs while preserving stable IDs in metadata.
  - Elevated Workspace Audit fields such as visibility changes, document type, activity parameters, OAuth app/scope/API fields, Takeout products/destination/status, Gemini app/feature/action, device fields, network info, result status, and display target.
  - Improved Google Takeout CSV target extraction so activity rows can surface Title/URL/Details/Query fields instead of only Product Name.
  - Fixed Google Takeout Activity timestamp parsing for values ending in `UTC`, allowing activity rows to become behavioral timestamps when supported by the export.
  - Prevented Google Audit/Investigation ZIPs from also being parsed as Google Takeout simply because they contain a Takeout audit CSV.
  - Updated Google risk text assembly so rule matching considers operation plus target/source/raw metadata together instead of only the first nonblank field.

- `v3.6.0`
  - Separated Google cloud metadata from endpoint/O365-style metadata fields.
  - Added Google-prefixed canonical fields and prefixed raw source fields for Google Workspace Audit and Takeout rows.
  - Restricted Master metadata export preferred O365/endpoint columns for Google rows to limited universal fields only.
  - Added `vestigant_google_field_collision_review.csv` to validation bundles.
  - Updated ingest timestamp handling to respect parser-supplied behavioral/metadata-only timestamp intent.
  - Build failed because `Core/DatabaseIngest.cs` was truncated after `if (rowCount % 10000 == 0)`.

- `v3.6.1`
  - Restored the missing `DatabaseIngest.cs` tail: progress logging, transaction commit, ingest result return, and helper methods.
  - Preserved the v3.6.0 Google schema-separation logic above the insert path.
  - Added a build-validation assertion for the DatabaseIngest import-completion marker.
  - No Google parser, risk-rule, Master export, validation-bundle schema, or UI behavior was intentionally changed.

- `v3.6.2`
  - Narrow hotfix for v3.6.1 Google metadata/database bloat observed during thin testing.
  - User-provided SQLite metrics showed `events=475,692`, `event_fields=27,683,343`, about `58.2` fields/event, and DB size dominated by `event_fields` plus `ix_event_fields_name_value`, `ix_event_fields_value`, and `ix_event_fields_event_name`.
  - Moved `GoogleAuditRaw_*` and `GoogleTakeoutRaw_*` preservation out of indexed `event_fields` and into `google_event_raw_fields`.
  - Skipped Google metadata fields that duplicate normalized event columns, skipped `GoogleRawSerializedRow` from metadata because it is already retained in `events.raw_json`, skipped `GoogleMasterExportSchema`, and skipped default negative `GoogleRisk* = No` fields.
  - Dropped broad global `event_fields` value indexes and replaced them with a smaller `event_fields(field_name)` index while keeping `(event_id, field_name)` lookups.
  - Added `vestigant_google_metadata_storage_summary.csv` to validation output.

- `v3.6.3.1`
  - Added phase-based `headless_google_run_status.txt` updates for Google source runs.
  - Added risk-engine progress logging every 10,000 events or 60 seconds.
  - Added wrapper heartbeat snapshots while the headless process is running, including process memory, CPU seconds, case database size, status fields, and recent headless log lines.
  - Added `RUN_GOOGLE_THIN_TEST_V3_6_3_1.ps1`, which skips risk by default for thin validation unless `-IncludeRisk` is supplied.
  - Preserved v3.6.2 Google metadata-storage behavior; no parser semantic change was intended.


- `v3.10.0`
  - Reviewed the v3.7.2 Google thin upload and proceeded directly to the next build as requested.
  - Fixed the Google Takeout HTML nullability warning path by making HTML event builders null-safe before observed/fallback event creation.
  - Added `vestigant_google_product_coverage.csv` to validation bundles, with product-level files seen, events generated, behavioral events, fallback/inventory rows, data sources, operations, and coverage status.
  - Added build assertions for Google product coverage export and null-safe Takeout HTML handling.
  - Refreshed package documentation, version strings, and PowerShell build/thin-test commands to v3.10.0.

- `v3.7.2`
  - Reviewed the v3.7.1 Google thin bundle and fixed routing/packaging issues.
  - Relaxed upload-bundle validation to accept real `validation_bundle/*.zip` names instead of one fixed validation bundle filename.
  - Prevented Takeout Access Log Activity CSVs from double-routing as Workspace Audit and Takeout.
  - Prevented Takeout ZIPs containing Gemini files from double-claiming as standalone Gemini Session Archive sources.
  - Refreshed stale project docs/scripts to v3.7.2.

- `v3.7.1`
  - Reviewed the v3.7.0 Google thin bundle and uploaded suggestions.
  - Replaced nested Takeout ZIP in-memory `MemoryStream` parsing with temp-file extraction.
  - Added bounded buffering for Takeout JSON/HTML/ICS and Gemini text artifacts so oversized files are recorded rather than fully loaded into RAM.
  - Added stream-based Gmail Takeout MBOX header parser (`MboxParser`) and registered it; MBOX remains opt-in for Google thin runs through `-IncludeMbox`.
  - Changed Google Chat message target selection to prefer topic/thread ID or message ID while preserving message text in metadata.
  - Expanded Google-aware risk burst operations and changed Google risk evaluation to use parser-populated `GoogleRisk*` metadata instead of rebuilding large search strings per event.
  - Updated package/build assertions and PowerShell test commands for v3.7.1.

- `v3.7.0`
  - Reviewed uploaded Google/Takeout/Gemini sample ZIPs before coding.
  - Identified useful source content in Workspace Audit CSVs, Takeout Access Log Activity CSV, Takeout My Activity HTML, Google Chat `messages.json`, Google Meet conference history CSV, Calendar ICS, Mail settings JSON, NotebookLM source HTML, nested Takeout ZIPs, and Gemini session transcript/code/PDF/screenshot artifacts.
  - Added Takeout My Activity HTML promotion with timestamp extraction and product-family classification.
  - Added Google Chat message-level JSON parsing with creator, message ID, topic ID, message text preview, and first URL metadata.
  - Added Google Meet conference history CSV parsing into behavioral participation events.
  - Added Calendar ICS VEVENT parsing into calendar event rows.
  - Added bounded nested Takeout ZIP inventory/parsing for nested Takeout archives.
  - Added Gemini transcript/code text extraction previews and risk-term summaries while preserving screenshots/PDFs as metadata-only artifacts.
  - Added validation assertions for the new Google parser capabilities and kept PowerShell test commands updated.

- `v3.10.0`
  - Reviewed the v3.7.3 Google thin upload and product coverage outputs.
  - Improved Google Chat `created_date` timestamp parsing for weekday/`at`/UTC strings observed in Takeout `messages.json`.
  - Improved My Activity HTML timestamp extraction/parsing for Google-style strings with optional weekday, `at`, and timezone abbreviations including IDT/IST.
  - Added `docs/Google_Source_Roadmap_v3_10_0.md` to capture evidence-driven next steps from thin validation outputs.
  - Preserved v3.7.x Google routing, metadata-storage, MBOX, validation-bundle, and wrapper behavior.


- `v3.10.0`
  - Built from uploaded v3.8.1 full source plus the v3.8.1 Google thin upload.
  - Added default suppression of expanded Takeout/Gemini child files when source ZIP archives are also present, because the v3.8.1 thin upload showed archive-level and extracted-file-level duplicate ingestion for Activities, Calendar, Meet, and other Takeout products.
  - Added `--include-expanded-google-files` and wrapper `-IncludeExpandedGoogleFiles` opt-in for deliberate full duplicate-path coverage.
  - Added validation-export status heartbeats per validation step.
  - Added `vestigant_sqlite_object_size_summary.csv` to validation bundles for event count, event_fields count, google_event_raw_fields count, fields-per-event ratios, page metrics, and dbstat object sizes when supported.

- `v3.10.0`
  - Narrow runtime hotfix for v3.9.0 Google thin failure.
  - Added explicit `SQLitePCLRaw.bundle_e_sqlite3` package reference and publish-output validation for `e_sqlite3.dll`.
  - Fixed nullable JSON fallback warning in `GoogleTakeoutParser`.
  - Preserved v3.9.0 Google duplicate-source suppression and diagnostics.

- `v3.10.0`
  - Reviewed the successful v3.9.2 Google thin upload first, as required by standing workflow.
  - Suppressed redundant umbrella `Takeout.zip` archives by default when part-specific Takeout ZIP archives are also present.
  - Added `--include-duplicate-google-archives` / `-IncludeDuplicateGoogleArchives` opt-in for deliberate full duplicate-path coverage.
  - Hardened SQLite diagnostics export so count/page metrics are written even when SQLite `dbstat` is unavailable; absence of dbstat is now a warning row, not a CSV-wide failure.
  - Updated thin wrapper, source automation logging, release docs, and exact PowerShell commands.

- `v3.11.0`
  - Reviewed the v3.10.0 Google thin upload first.
  - Added Google raw-storage compaction for raw fields that duplicate promoted event/core/canonical values.
  - Preserved non-duplicative Google raw fields for unmapped-column review.
  - Added validation assertions for the compaction helper and refreshed build/test PowerShell commands.

- `v3.12.0`
  - Reviewed the v3.11.0 Google thin upload first.
  - Suppressed duplicate same-name/same-size Google archive paths by default.
  - Preferred root-level archive copies over nested extracted duplicate copies.
  - Preserved `-IncludeDuplicateGoogleArchives` for deliberate duplicate-path coverage.

- `v3.21.0`
  - Reviewed the v3.13.0 Google thin upload first.
  - Compacted repeated low-cardinality Google indexed metadata: `ArtifactType`, `EventTimeBasis`, `EventTimeConfidence`, `GoogleEventCategory`, `GoogleIPClassification`, `GoogleNetworkType`, `GoogleOperationRaw`, and `GoogleRecordType` where safely represented elsewhere.
  - Preserved meaningful searchable Google fields and forensic provenance.
  - Added clearer storage threshold rows and two-level health flags.
  - Improved SQLite object-size fallback output when `dbstat` is unavailable.

## Known Bugs

- `v3.21.0` has not yet been validated on the Windows workstation with `dotnet build`, `dotnet publish`, and the real `E:\0445_0001` Google source folder.
- `v3.6.0` failed Windows build because `Core/DatabaseIngest.cs` was truncated; v3.6.1 was intended to fix this.
- `v3.4.4` Google headless runtime completed successfully, but its validation showed Google Takeout Activities were metadata-only because `Activity Timestamp` values with a `UTC` suffix were not parsed; v3.5.0 attempted to fix this and v3.6.0-v3.21.0 preserve that fix while changing metadata separation/storage.
- Google Takeout parsing is first-pass only and likely needs product-by-product expansion after reviewing validation output.
- Gemini session ingestion is first-pass archive inventory and likely needs richer transcript/file-content extraction and risk correlation.
- Google Cloud Logging JSON/NDJSON and BigQuery-style exports are planned but not yet implemented.
- The large `Part3_All mail Including Spam and Trash` MBOX remains excluded by default from fast Google thin runs, but v3.7.1 added an opt-in stream-based header-only MBOX parser through `-IncludeMbox`.
- The `v3.3.13` run failed with executable exit code `2`; v3.3.14 added better diagnostics and validation export hardening, but the exact original exception was not confirmed from the pasted log.
- The GUI DataGridView popup issue was addressed in v3.3.18 but still needs confirmation in the user’s workstation GUI after rebuilding and opening affected tabs.

## The "Do Not Do" List (Graveyard)

- Do not create separate root-level `V*_*.md` files for each version. Correct fix: maintain one consolidated `CHANGELOG.md`.
- Do not omit `ai_context.md` from full source packages. Correct fix: keep root `ai_context.md` current and include it in upload/project documentation.
- Do not use stale hard-coded version regexes in validation scripts. Correct fix: derive assertions from `$Version` and escape safely.
- Do not require exact changelog heading text beyond the current-version heading. Correct fix: assert a heading like `## vX.Y.Z`.
- Do not put unescaped `$I` inside double-quoted PowerShell strings under `Set-StrictMode`. Correct fix: use literal strings or escape `$`.
- Do not assume `Get-ChildItem` results always expose `.Count`. Correct fix: wrap results with `@(...)` before `.Count` checks.
- Do not use `Tee-Object` where UTF-16/NUL-style logs can break parsing. Correct fix: append UTF-8 text directly.
- Do not use Windows PowerShell 5.1-hostile `String.Trim` overload patterns. Correct fix: use regex-based normalization.
- Do not test raw status files with strict line regex only. Correct fix: normalize BOM/NUL/CRLF/whitespace and compare parsed lines case-insensitively.
- Do not let synthetic upload-bundle fixture validation block real autorun. Correct fix: skip only the fixture during autorun preflight while requiring real validation bundles for case packaging.
- Do not accept placeholder or build-fixture validation bundles as real artifacts. Correct fix: validate readable ZIP contents and reject fixture paths.
- Do not recursively copy broad project/case folders into upload bundles. Correct fix: thin project/case copy and exclude build output and staged source artifacts unless explicitly requested.
- Do not create redundant WorkingEvidence 7-Zip archives for image-backed headless cases by default. Correct fix: write archive-skip manifests and archive diagnostics.
- Do not choose the longest binary-contaminated Office owner string. Correct fix: score plausible short owner strings and sanitize binary content.
- Do not match Recycle Bin `$I` artifacts by broad substring matching. Correct fix: accept actual `$I*` filenames and staged/deleted `$I*` variants only.
- Do not treat OneDrive SyncEngine rows as metadata-only when timestamp columns exist. Correct fix: decode known OneDrive timestamp fields and emit specific operations.
- Do not export only the visible Master tab grid page for “all metadata.” Correct fix: stream all records from SQLite and pivot `event_fields`.
- Do not let WinForms DataGridView display-conversion exceptions create modal popup loops. Correct fix: central `DataGridView.DataError` handling and text conversion for generated image columns.
- Do not force Google Admin audit exports through the O365 UAL parser. Correct fix: implement Google as its own source family with source registry, schema preservation, and Google-specific risk mapping.
- Do not silently drop unknown Google columns. Correct fix: preserve original columns as metadata and report schema drift/unmapped fields.
- Do not treat Google Takeout alone as proof of local endpoint activity. Correct fix: preserve limitation metadata and correlate with endpoint/IP/browser/sync/login/device evidence before stronger conclusions.
- Do not mark Gemini/AI activity as misconduct by default. Correct fix: treat as review-relevant only when correlated with sensitive terms, generated code/document output, export/share/download activity, or case-specific risk indicators.
- Do not reference instance parser properties such as `ParserName` from static helper code. Correct fix: use a static constant such as `ParserDisplayName` and expose the instance property from that constant.
- Do not hash very large Google test sources by default during fast parser validation. Correct fix: use the Google runner’s default skipped-hash mode for parser validation and rerun with `-HashGoogleSources` for final source-authenticity validation.

- Do not parse Google Audit/Investigation ZIPs through `GoogleTakeoutParser` merely because they contain `Audit and Investigation - Takeout log events.csv`. Correct fix: treat those as Workspace Audit rows and explicitly reject Audit/Investigation containers in the Takeout parser.
- Do not use broad fuzzy `Contains()` operation mapping for high-value Google Drive/Gmail audit events. Correct fix: use exact event-name normalization first, then fall back to safe prefixed tokens.
- Do not use Google Takeout Product Name as the primary target when row fields such as Title, Title URL, URL, Details, or Query exist. Correct fix: surface the activity target and store product as Workload/GoogleTakeoutProduct.
- Do not assume .NET date parsing will reliably parse every Takeout UTC timestamp string. Correct fix: explicitly handle `UTC`/`GMT` suffixes and exact Google-style timestamp formats.

- Do not store Google raw source columns under unprefixed names such as `Title`, `Event`, `Description`, `IP Address`, or `User Agent`. Correct fix: preserve raw columns as `GoogleAuditRaw_*` or `GoogleTakeoutRaw_*` identities in the non-indexed `google_event_raw_fields` table and promote only reviewed Google-specific canonical fields to indexed metadata.
- Do not let the Master metadata export fill the entire O365/endpoint preferred header set for Google rows. Correct fix: only fill limited universal fields and keep cloud-specific values in appended `Metadata_Google*` / `Vestigant_*` columns.
- Do not infer behavioral significance solely because an archive/config row has a timestamp. Correct fix: respect parser-supplied `IsBehavioralTimestamp`, `EventTimeConfidence`, and `TimestampWarning`.

- Do not patch a C# file by replacing the top half without verifying the tail remains present. Correct fix: check for closing markers such as `tx.Commit()`, `return new IngestResult`, helper methods, and balanced braces after every source-file patch.

- Do not store high-cardinality raw Google source columns in globally indexed `event_fields`. Correct fix: keep canonical investigative fields in `event_fields`, preserve raw source columns in `google_event_raw_fields`, and report storage metrics in validation output.
- Do not generate or provide a full/delta ZIP without reviewing package contents, version strings, runner/wrapper PowerShell scripts, `ai_context.md`, `CHANGELOG.md`, and the exact PowerShell commands needed for build/test validation. Correct fix: treat these as release-blocking checklist items before packaging.
- Do not leave `tools/Create-TriageUploadBundle.ps1` with a stale default `$Version` token. Correct fix: derive/assert against the current package version token and fail validation before packaging.
- Do not provide Google thin-test wrapper scripts unless parameter passing has been checked. Correct fix: use splatted named parameters when calling `RUN_GOOGLE_SOURCE_TRIAGE.ps1` and assert the wrapper does not use fragile positional runner argument arrays.

- Do not generate or package a ZIP without reviewing source code, package contents, version strings, ai_context.md, CHANGELOG.md, wrapper scripts, and the needed PowerShell test/run commands together.
- Do not diagnose Google source coverage from assumptions when uploaded sample archives/logs are available. Correct fix: inspect actual ZIP contents, schemas, sample rows, JSON/HTML/ICS structures, and then implement parser support for useful content.
- Do not leave rebuilt packages without exact PowerShell commands for build validation and test runs.

- Do not globally replace old version strings across historical changelog/context entries. Correct fix: update only current package metadata and prepend new version sections while preserving historical version text.
- Do not load massive Google Takeout or Gemini text artifacts fully into RAM. Correct fix: use streaming parsers where practical or bounded buffering with metadata-only observations for oversized artifacts.
- Do not expand nested Takeout ZIPs into large `MemoryStream` buffers. Correct fix: extract bounded nested ZIP entries to temp files and delete them in `finally`.
- Do not rely on generic endpoint-only burst operations for Google risk. Correct fix: include Google Drive, Takeout, Gmail, and MBOX operations in cloud burst detection and use parser-supplied Google risk metadata.

- Do not validate upload-bundle validation inclusion by requiring a fixed inner ZIP filename such as `validation_bundle/VestigantCase_validation_bundle.zip`. Correct fix: accept a real non-empty generated validation bundle ZIP under `validation_bundle/*.zip` and inspect its required CSV/JSON/readme entries.
- Do not route Takeout Access Log Activity CSVs through both Google Workspace Audit and Google Takeout solely because the filename contains `Activities - A list of Google services accessed by.csv`. Correct fix: use path/container context to keep Takeout activity exports in Takeout and Workspace Audit exports in Workspace Audit.
- Do not ship root/package documentation with stale version-specific PowerShell commands after a rebuild. Correct fix: review README, BUILD_NOTES, VALIDATION_STATUS, PROJECT_ROADMAP, ai_context, CHANGELOG, and runner scripts together before exporting ZIPs.

- Do not stop at roadmap-only text after thin upload when the standing instruction is to proceed. Correct fix: generate the next package and include exact PowerShell build/test commands unless the user explicitly says to pause.

## Standing Release/Testing Rule Added in v3.10.0

- If the user uploads a thin upload, review the thin output first and then automatically proceed to the next version/build unless the user explicitly says to pause.
- Every rebuilt package must include exact PowerShell build/test/run commands in the response and in package documentation.
- Before every ZIP is provided, review all code/content and required PowerShell scripts for current version consistency, including `ai_context.md`, `CHANGELOG.md`, `tools/Create-TriageUploadBundle.ps1`, `tools/Build-And-Validate-VestigantTriage.ps1`, Google thin wrappers, and required test helper scripts.
- Treat missing current changelog headings, stale upload-bundle versions, missing runner scripts, and broken wrapper parameter passing as release-blocking graveyard items.
- Google thin validation should include SQLite growth metrics using `tools/Get-GoogleCaseSqliteMetrics.ps1` when a case database is produced.

- Do not let Google thin tests ingest both source ZIP archives and their already-expanded Takeout/Gemini child files by default. Correct fix: prefer archives for default thin validation and require `--include-expanded-google-files` / `-IncludeExpandedGoogleFiles` for deliberate duplicate-path/full-coverage testing.
- Do not ship a Google validation bundle without database-size diagnostics. Correct fix: include event counts, metadata counts, fields-per-event ratios, and SQLite table/index object sizes where available.

- Do not treat a successful managed .NET build/publish as sufficient when native provider DLLs are missing. Correct fix: build validation must verify native runtime dependencies such as `e_sqlite3.dll` exist in the published output before running thin tests.


- Do not assume `dotnet build` always writes the framework-dependent executable to `bin\Release\net8.0-windows`. If the project sets `RuntimeIdentifier=win-x64`, validate the RID-specific build output under `bin\Release\net8.0-windows\win-x64` as well.


## v3.21.0 raw metadata compaction

Built from v3.14.0 after reviewing the v3.14.0 Google thin upload. Focus: reduce remaining Google raw-field volume and add `vestigant_google_raw_field_classification.csv` to classify raw fields by storage-review category. Preserve source row reconstruction, source coverage, schema coverage, unmapped columns, field collision review, and product/device/IP summaries.


## v3.21.0 v4 readiness diagnostics

Prepared from the v3.15.0 source package and v3.15.0 thin upload available in this chat workspace. v3.15.0 thin status evidence: complete run, 7 sources, risk skipped, expanded Google files not included, validation bundle exported. v3.15.0 storage evidence: 498,978 events, 11,436,215 event_fields, 3,800,659 google_event_raw_fields, averages 22.92 indexed fields/event and 7.62 raw Google fields/event, indexed/raw storage health OK. v3.21.0 adds `vestigant_google_v4_readiness_summary.csv` to distinguish storage success from pending risk and expanded/duplicate archive validation.

## v3.21.0 continuation note

User-provided v3.17.0 heartbeat evidence showed risk per-event processing finished, then sequence detection remained in `risk_running` without new progress for several minutes. Treat this as a roadmap-blocking performance issue for full investigations. v3.21.0 adds bounded risk sequence detection and documents full-ingest safeguards: bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default.


## v3.21.0 - Risk sequence timeout and export safeguard validation

Evidence basis: the v3.17.x risk/duplicate heartbeat showed per-event risk rules completed, then the run stayed in multi-event sequence detection without new progress. v3.21.0 treats that as a roadmap-blocking full-investigation performance issue.

Implemented safeguards:
- Risk sequence detection now exposes `sequenceMaxCandidatesPerUser` and `sequenceTimeoutSeconds` in addition to comparison and hit caps.
- Sequence detection logs skipped candidates, timeout stops, and bounded progress status.
- Validation now includes `vestigant_export_safeguards_summary.csv` to document bounded exports, export cost classes, timeout/cancel behavior, and the default avoidance of expensive joined CSV dumps.
- Build validation asserts the new risk and export-safeguard markers.