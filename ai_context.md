# ai_context.md

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

- Latest generated version: `v3.7.0`.
- `v3.4.4` completed a fast Google test successfully: status complete, 7 sources, MBOX excluded, risk completed, and validation bundle exported.
- `v3.6.0` was a Google cloud-schema separation pass, but the Windows build failed because `Core/DatabaseIngest.cs` was accidentally truncated after a progress-logging `if` statement.
- `v3.6.1` was a narrow build hotfix that restored the missing DatabaseIngest tail while preserving the v3.6.0 Google schema-separation logic.
- During v3.6.1 thin testing, the case database grew beyond 10 GB while other case files stayed small. User-provided SQLite `dbstat` showed `event_fields` and its value indexes dominated size; Google metadata represented about 91.4% of `event_fields` rows.
- `v3.6.2` was a narrow size/performance hotfix that keeps Google schema separation but moves `GoogleAuditRaw_*` / `GoogleTakeoutRaw_*` source columns into `google_event_raw_fields`, skips duplicate/default Google metadata from indexed `event_fields`, drops broad event-field value indexes, and adds Google metadata storage validation metrics.
- During the first v3.6.2 Google thin run, ingest appeared complete but the process was inactive or not visibly progressing for more than 40 minutes after risk analysis started. The status file still only reported `status=started`, which exposed a logging/observability gap.
- The first v3.6.3 thin wrapper run failed before ingest because the wrapper passed literal parameter names into `RUN_GOOGLE_SOURCE_TRIAGE.ps1`, causing `GoogleRoot does not exist: -GoogleRoot`; v3.6.3.1 fixes the wrapper invocation.
- `v3.6.3.1` is a narrow wrapper hotfix on top of v3.6.3 logging/risk-progress changes. It fixes Google thin-test wrapper argument binding by using hashtable splatting and still skips risk by default unless `-IncludeRisk` is supplied.
- `v3.7.0` is a narrow release-script hotfix that fixes the stale upload-bundle default version token, keeps the Google thin-test wrapper fix, and adds package/release assertions for wrapper parameter passing and version-token drift.
- `v3.7.0` still needs Windows build validation and a new Google thin run against `E:\0445_0001`.
- The app uses a single consolidated `CHANGELOG.md` rather than separate root-level per-version markdown files.
- Full source packages must include this root-level `ai_context.md` file.
- Existing major functions include local endpoint triage, raw-image headless triage, validation-bundle generation, upload-bundle generation, Master tab all-record metadata export, print artifact expansion, SetupAPI transfer/destruction triage, and Google source ingestion framework.
- The Master tab full metadata export is database-backed and includes the user-provided preferred header sequence first, then appended Vestigant/dynamic metadata.
- DataGridView modal popup loops caused by image-column/string conversion errors were mitigated in v3.3.18.

## Next Steps / Active Goal

First validate that `v3.7.0` builds on the Windows workstation, then run the Google thin test against `E:\0445_0001` using `RUN_GOOGLE_THIN_TEST_V3_7_0.ps1`. Confirm that Workspace Audit, Takeout, and Gemini rows ingest correctly; for the default thin test, risk is skipped unless `-IncludeRisk` is supplied. Specific v3.7.0 validation targets: Takeout My Activity HTML rows should promote behavioral events where timestamps parse; Google Chat `messages.json` should produce message-level events; Google Meet `conference_history_records.csv` should produce participation events; Calendar `.ics` files should produce VEVENT rows; Gemini transcript/code text artifacts should expose extracted text previews; raw Google source columns should remain in `google_event_raw_fields`; `event_fields / events` should remain materially lower than the v3.6.1 observed ratio of about 58.2; and Google collision/storage validation CSVs should still be generated.

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

- `v3.3.8.1`
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

## Known Bugs

- `v3.7.0` has not yet been validated on the Windows workstation with `dotnet build`, `dotnet publish`, and the real `E:\0445_0001` Google source folder.
- `v3.6.0` failed Windows build because `Core/DatabaseIngest.cs` was truncated; v3.6.1 was intended to fix this.
- `v3.4.4` Google headless runtime completed successfully, but its validation showed Google Takeout Activities were metadata-only because `Activity Timestamp` values with a `UTC` suffix were not parsed; v3.5.0 attempted to fix this and v3.6.0-v3.7.0 preserve that fix while changing metadata separation/storage.
- Google Takeout parsing is first-pass only and likely needs product-by-product expansion after reviewing validation output.
- Gemini session ingestion is first-pass archive inventory and likely needs richer transcript/file-content extraction and risk correlation.
- Google Cloud Logging JSON/NDJSON and BigQuery-style exports are planned but not yet implemented.
- The large `Part3_All mail Including Spam and Trash` MBOX visible in the user screenshot is included only as candidate source/metadata inventory at this stage unless a specific MBOX parser is added.
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
