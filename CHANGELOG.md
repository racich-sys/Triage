# Changelog


## v3.21.0 - Risk/duplicate validation safeguards

Evidence basis: the v3.20.0 default Google thin bundle completed successfully with version 3.20.0, status complete, 7 sources, risk skipped, validation bundle exported, `google_storage_health=OK`, and `google_raw_storage_health=OK`. v3.21.0 therefore advances the roadmap toward the remaining risk-enabled and duplicate/expanded archive validation runs.

Changes in this package:
- Tightened default risk sequence safeguards for full-investigation protection: `sequenceMaxComparisons=250000`, `sequenceMaxHits=50000`, `sequenceProgressEveryComparisons=10000`, `sequenceMaxCandidatesPerUser=2000`, and `sequenceTimeoutSeconds=60`.
- Keeps bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default as roadmap-blocking full-investigation safeguards.
- Keeps validation-bundle reporting for `vestigant_export_safeguards_summary.csv` and `vestigant_google_v4_readiness_summary.csv`.



## v3.21.0 - Risk sequence timeout and export safeguard validation

Evidence basis: the v3.17.x risk/duplicate heartbeat showed per-event risk rules completed, then the run stayed in multi-event sequence detection without new progress. v3.21.0 treats that as a roadmap-blocking full-investigation performance issue.

Implemented safeguards:
- Risk sequence detection now exposes `sequenceMaxCandidatesPerUser` and `sequenceTimeoutSeconds` in addition to comparison and hit caps.
- Sequence detection logs skipped candidates, timeout stops, and bounded progress status.
- Validation now includes `vestigant_export_safeguards_summary.csv` to document bounded exports, export cost classes, timeout/cancel behavior, and the default avoidance of expensive joined CSV dumps.
- Build validation asserts the new risk and export-safeguard markers.

## v3.21.0 - Risk sequence bounds and full-investigation safeguards

Verified input evidence for this build cycle:

- Source package used: `VestigantTriage_v3_17_0_risk_and_duplicate_validation_FIX1.zip`.
- User-provided heartbeat file: `Pasted text.txt`.
- The heartbeat records a v3.17.0 duplicate Google run command with `--include-duplicate-google-archives`.
- The heartbeat records per-event risk evaluation completing `670,011/670,011` events in `26.6s` with `hits_before_sequences=1,142,543`.
- The heartbeat then repeats `Risk phase: detecting multi-event sequences` while status remains `phase=risk_running`, `risk_processed=670011`, `risk_total=670011`, and `risk_hits_so_far=1142542` from 15:41 through at least 15:49.

Implemented in v3.21.0:

- Replaced unbounded risk sequence detection with a bounded candidate-based detector.
- Added configurable sequence safety thresholds in `risk_rules.json` and `RiskThresholds`:
  - `sequenceMaxComparisons`
  - `sequenceMaxHits`
  - `sequenceProgressEveryComparisons`
- Added risk sequence progress logging and status callback updates so sequence detection no longer appears silent after per-event risk rules complete.
- Added warning logs when sequence detection stops because a comparison or hit cap is reached.
- Added full-investigation roadmap blockers for bounded exports, export cost classes, timeout/cancel behavior, and avoiding expensive joined CSV dumps by default.
- Updated v3.21.0 wrapper naming and release validation assertions.

Unable to verify in this environment:

- Windows `dotnet build`, `dotnet publish`, and live Google/risk/duplicate execution are not verified here because the .NET SDK is not installed in this sandbox. Run the PowerShell commands in `BUILD_NOTES.md` on the Windows workstation.