# Vestigant Triage v3.6.3.1

v3.6.3.1 preserves the Google metadata storage hotfix and adds Google thin-test logging, phase status, risk progress, and heartbeat diagnostics.

## Google validation command

```powershell
Set-Location D:\Downloads
Get-FileHash .\VestigantTriage_v3_6_3_1_google_logging_risk_progress_hotfix.zip -Algorithm SHA256
Expand-Archive -LiteralPath .\VestigantTriage_v3_6_3_1_google_logging_risk_progress_hotfix.zip -DestinationPath T:\ -Force
& "T:\VestigantTriage_v3_6_3_1\RUN_GOOGLE_SOURCE_TRIAGE.ps1" -GoogleRoot "E:\0445_0001"
```

## Review focus

Review `vestigant_google_field_collision_review.csv`, `vestigant_google_schema_coverage.csv`, `vestigant_google_unmapped_columns.csv`, and the Master metadata export. Google cloud-specific values should appear in Google-prefixed metadata fields rather than unprefixed endpoint/O365 columns.
