# Vestigant Triage Project Roadmap and Continuation - v3.21.0


## v3.21.0 - Risk/duplicate validation safeguards

Evidence basis: the v3.20.0 default Google thin bundle completed successfully with version 3.20.0, status complete, 7 sources, risk skipped, validation bundle exported, `google_storage_health=OK`, and `google_raw_storage_health=OK`. v3.21.0 therefore advances the roadmap toward the remaining risk-enabled and duplicate/expanded archive validation runs.

Changes in this package:
- Tightened default risk sequence safeguards for full-investigation protection: `sequenceMaxComparisons=250000`, `sequenceMaxHits=50000`, `sequenceProgressEveryComparisons=10000`, `sequenceMaxCandidatesPerUser=2000`, and `sequenceTimeoutSeconds=60`.
- Keeps bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default as roadmap-blocking full-investigation safeguards.
- Keeps validation-bundle reporting for `vestigant_export_safeguards_summary.csv` and `vestigant_google_v4_readiness_summary.csv`.


## v3.21.0 release-blocking correction: full-investigation safeguards wording

v3.18.0 Windows validation failed because `Build-And-Validate-VestigantTriage.ps1` asserted that `PROJECT_ROADMAP_AND_CONTINUATION.md` must contain the exact phrase `bounded exports`, but the roadmap used different wording. v3.21.0 corrects that packaging/documentation defect.

Full-investigation safeguards remain roadmap-blocking before v4.0 full-ingest confidence:

1. Default full-investigation exports must use **bounded exports** unless the investigator explicitly opts into full detail output.
2. Export planning must use **export cost classes** so cheap summaries, moderate detail exports, and expensive joined CSV dumps are separated.
3. Expensive joined CSV dumps must not run by default during thin, risk, duplicate, or full-investigation validation.
4. Long-running export and risk phases must expose timeout/cancel behavior and heartbeat/status progress.
5. Validation bundles must favor compact diagnostic summaries over expensive joined row dumps by default.
6. Risk sequence detection must remain bounded by candidate filtering, comparison caps, hit caps, and progress logging.


## Verified latest thin evidence

The v3.15.0 thin upload available in this chat records completion in `case_review/Upload/headless_google_run_status.txt` with 7 sources, risk skipped, expanded Google files not included, and validation bundle exported. The validation CSV `vestigant_google_metadata_storage_summary.csv` records storage health OK for both indexed and raw Google metadata.

## v3.21.0 purpose

Add explicit v4-readiness diagnostics so future thin, risk-enabled, and expanded/duplicate runs produce a single CSV showing which gates are satisfied and which remain pending.

## Before v4.0

1. Complete default Google thin run with storage health OK.
2. Complete risk-enabled Google run with `-IncludeRisk`.
3. Complete duplicate or expanded coverage run with `-IncludeDuplicateGoogleArchives` or `-IncludeExpandedGoogleFiles`.
4. Confirm validation bundle includes `vestigant_google_v4_readiness_summary.csv` and existing Google coverage/storage CSVs.
5. Confirm Windows build/publish validation passes on the workstation.

## v3.21.0 roadmap-blocking full-investigation safeguards

The v3.17.0 risk/duplicate heartbeat showed per-event risk evaluation completed, followed by a silent/long-running sequence-detection phase. v3.21.0 treats this as a roadmap-blocking full-ingest performance issue.

Required safeguards before v4.0 full-investigation confidence:

1. Risk sequence detection must be bounded by candidate filtering, comparison caps, hit caps, and progress logging.
2. Full exports must be bounded by default.
3. Export cost classes must distinguish cheap summary exports from expensive joined/detail dumps.
4. Expensive joined CSV dumps must be opt-in, not default.
5. Long-running exports/risk phases must expose timeout/cancel behavior and heartbeat progress.
6. Validation bundles must prioritize compact diagnostics over full joined row dumps unless explicitly requested.


## v3.21.0 - Risk sequence timeout and export safeguard validation

Evidence basis: the v3.17.x risk/duplicate heartbeat showed per-event risk rules completed, then the run stayed in multi-event sequence detection without new progress. v3.21.0 treats that as a roadmap-blocking full-investigation performance issue.

Implemented safeguards:
- Risk sequence detection now exposes `sequenceMaxCandidatesPerUser` and `sequenceTimeoutSeconds` in addition to comparison and hit caps.
- Sequence detection logs skipped candidates, timeout stops, and bounded progress status.
- Validation now includes `vestigant_export_safeguards_summary.csv` to document bounded exports, export cost classes, timeout/cancel behavior, and the default avoidance of expensive joined CSV dumps.
- Build validation asserts the new risk and export-safeguard markers.