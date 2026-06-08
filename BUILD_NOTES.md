# Build Notes - Vestigant Triage v3.6.3.1

v3.6.3.1 is a Google cloud-schema separation and validation hardening build. It preserves the v3.5.0 Google readability improvements, but moves Google raw/source metadata into Google-prefixed fields so Google cloud evidence is not mixed into endpoint/O365-specific metadata columns.

## Windows validation command

```powershell
Set-Location D:\Downloads
Get-FileHash .\VestigantTriage_v3_6_3_1_google_logging_risk_progress_hotfix.zip -Algorithm SHA256
Expand-Archive -LiteralPath .\VestigantTriage_v3_6_3_1_google_logging_risk_progress_hotfix.zip -DestinationPath T:\ -Force
& "T:\VestigantTriage_v3_6_3_1\RUN_GOOGLE_SOURCE_TRIAGE.ps1" -GoogleRoot "E:\0445_0001"
```

Expected upload:

```text
D:\Downloads\Upload_VestigantTriage_v3_6_3_1_*.zip
```
