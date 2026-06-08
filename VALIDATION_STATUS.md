# Validation Status - Vestigant Triage v3.6.3.1

Assistant-side validation only. The assistant environment does not contain the Windows .NET SDK/runtime or the local Google test folder at `E:\0445_0001`.

Windows validation required:

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
