# Vestigant Triage v3.21.0


## v3.21.0 - Risk/duplicate validation safeguards

Evidence basis: the v3.20.0 default Google thin bundle completed successfully with version 3.20.0, status complete, 7 sources, risk skipped, validation bundle exported, `google_storage_health=OK`, and `google_raw_storage_health=OK`. v3.21.0 therefore advances the roadmap toward the remaining risk-enabled and duplicate/expanded archive validation runs.

Changes in this package:
- Tightened default risk sequence safeguards for full-investigation protection: `sequenceMaxComparisons=250000`, `sequenceMaxHits=50000`, `sequenceProgressEveryComparisons=10000`, `sequenceMaxCandidatesPerUser=2000`, and `sequenceTimeoutSeconds=60`.
- Keeps bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default as roadmap-blocking full-investigation safeguards.
- Keeps validation-bundle reporting for `vestigant_export_safeguards_summary.csv` and `vestigant_google_v4_readiness_summary.csv`.


v3.21.0 adds a validation-bundle readiness gate for the remaining v4.0 validation work. The new `vestigant_google_v4_readiness_summary.csv` is intended to distinguish default thin-run storage success from still-pending risk-enabled and expanded/duplicate archive coverage runs.

See `BUILD_NOTES.md` for exact PowerShell build and run commands.


## v3.21.0 - Risk sequence timeout and export safeguard validation

Evidence basis: the v3.17.x risk/duplicate heartbeat showed per-event risk rules completed, then the run stayed in multi-event sequence detection without new progress. v3.21.0 treats that as a roadmap-blocking full-investigation performance issue.

Implemented safeguards:
- Risk sequence detection now exposes `sequenceMaxCandidatesPerUser` and `sequenceTimeoutSeconds` in addition to comparison and hit caps.
- Sequence detection logs skipped candidates, timeout stops, and bounded progress status.
- Validation now includes `vestigant_export_safeguards_summary.csv` to document bounded exports, export cost classes, timeout/cancel behavior, and the default avoidance of expensive joined CSV dumps.
- Build validation asserts the new risk and export-safeguard markers.