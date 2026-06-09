# Build Notes - Vestigant Triage v3.21.0


## v3.21.0 - Risk/duplicate validation safeguards

Evidence basis: the v3.20.0 default Google thin bundle completed successfully with version 3.20.0, status complete, 7 sources, risk skipped, validation bundle exported, `google_storage_health=OK`, and `google_raw_storage_health=OK`. v3.21.0 therefore advances the roadmap toward the remaining risk-enabled and duplicate/expanded archive validation runs.

Changes in this package:
- Tightened default risk sequence safeguards for full-investigation protection: `sequenceMaxComparisons=250000`, `sequenceMaxHits=50000`, `sequenceProgressEveryComparisons=10000`, `sequenceMaxCandidatesPerUser=2000`, and `sequenceTimeoutSeconds=60`.
- Keeps bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default as roadmap-blocking full-investigation safeguards.
- Keeps validation-bundle reporting for `vestigant_export_safeguards_summary.csv` and `vestigant_google_v4_readiness_summary.csv`.


## Verified local input artifacts

The v3.21.0 package was prepared from the available v3.15.0 source ZIP and v3.15.0 thin upload in this chat workspace. See `CHANGELOG.md` for the exact evidence used.

## Build and default thin run

```powershell
Set-Location D:\Downloads
Remove-Item -LiteralPath T:\VestigantTriage_v3_21_0 -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath .\VestigantTriage_v3_21_0_v4_readiness_diagnostics.zip -DestinationPath T:\ -Force
Set-Location T:\VestigantTriage_v3_21_0

powershell -ExecutionPolicy Bypass -File .\tools\Build-And-Validate-VestigantTriage.ps1 -Publish
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase
```

## Required v4-readiness risk run

```powershell
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase -IncludeRisk
```

## Required expanded/duplicate coverage run

```powershell
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase -IncludeDuplicateGoogleArchives
```

Optional expanded child-file coverage:

```powershell
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase -IncludeExpandedGoogleFiles
```

## Validation target

The validation bundle should include `vestigant_google_v4_readiness_summary.csv` in addition to the existing Google storage, indexed-field, raw-field, source-coverage, schema-coverage, unmapped-column, field-collision, product, device, IP, and SQLite sizing diagnostics.

## Not verified here

The sandbox used to prepare this ZIP does not have the .NET SDK installed, so Windows build/publish/run results must be verified with the commands above.

## v3.21.0 build/test commands

```powershell
Set-Location D:\Downloads
Remove-Item -LiteralPath T:\VestigantTriage_v3_21_0 -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath .\VestigantTriage_v3_21_0_risk_sequence_full_safeguards.zip -DestinationPath T:\ -Force
Set-Location T:\VestigantTriage_v3_21_0

powershell -ExecutionPolicy Bypass -File .\tools\Build-And-Validate-VestigantTriage.ps1 -Publish
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase
```

Risk/duplicate validation:

```powershell
Set-Location T:\VestigantTriage_v3_21_0
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase -IncludeRisk -IncludeDuplicateGoogleArchives
```

If a prior publish executable is locked, close VestigantTriage and remove `bin`/`obj` before rebuilding.


## v3.21.0 - Risk sequence timeout and export safeguard validation

Evidence basis: the v3.17.x risk/duplicate heartbeat showed per-event risk rules completed, then the run stayed in multi-event sequence detection without new progress. v3.21.0 treats that as a roadmap-blocking full-investigation performance issue.

Implemented safeguards:
- Risk sequence detection now exposes `sequenceMaxCandidatesPerUser` and `sequenceTimeoutSeconds` in addition to comparison and hit caps.
- Sequence detection logs skipped candidates, timeout stops, and bounded progress status.
- Validation now includes `vestigant_export_safeguards_summary.csv` to document bounded exports, export cost classes, timeout/cancel behavior, and the default avoidance of expensive joined CSV dumps.
- Build validation asserts the new risk and export-safeguard markers.