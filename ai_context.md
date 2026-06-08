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

## Current State

- Latest generated version: `v3.4.3`.
- `v3.4.3` adds a dedicated headless Google source triage runner and script for the current `E:\0445_0001` Google data folder.
- `v3.4.2` fixed the last known compile failure in `SetupApiDevLogParser.cs`; `v3.4.3` still needs Windows build validation.
- `v3.4.0` introduced the Google source framework: Google Workspace Audit/Investigation CSV/ZIP, first-pass Google Takeout ZIP/CSV/JSON, Gemini session archive inventory, Google-specific risk rules, and Google validation CSVs.
- The app uses a single consolidated `CHANGELOG.md` rather than separate root-level per-version markdown files.
- Full source packages must include this root-level `ai_context.md` file.
- Existing major functions include local endpoint triage, raw-image headless triage, validation-bundle generation, upload-bundle generation, Master tab all-record metadata export, print artifact expansion, SetupAPI transfer/destruction triage, and Google source ingestion framework.
- The Master tab full metadata export is database-backed and includes the user-provided preferred header sequence first, then appended Vestigant/dynamic metadata.
- DataGridView modal popup loops caused by image-column/string conversion errors were mitigated in v3.3.18.

## Next Steps / Active Goal

Validate `v3.4.3` on the Windows workstation against `E:\0445_0001` using `RUN_GOOGLE_SOURCE_TRIAGE.ps1`. Review the resulting upload bundle, especially the `vestigant_google_*.csv` validation outputs, parser errors, schema drift/unmapped columns, risk hits, and Master metadata export behavior. The active development goal is to harden Google Workspace Audit/Investigation, Google Takeout, MBOX inventory, and Gemini source handling as first-class forensic triage sources.

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
  - Upload bundle now includes `RUN_GOOGLE_SOURCE_TRIAGE.ps1` in project documentation.

## Known Bugs

- `v3.4.3` has not yet been validated on the Windows workstation with `dotnet build`, `dotnet publish`, and the real `E:\0445_0001` Google source folder.
- Exact runtime behavior of the Google parser family is not yet confirmed from a validation upload.
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
- Do not hash very large Google test sources by default during fast parser validation. Correct fix: use the v3.4.3 Google runner’s default skipped-hash mode for parser validation and rerun with `-HashGoogleSources` for final source-authenticity validation.
