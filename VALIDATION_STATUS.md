# Build Notes - Vestigant Triage v3.21.0

Prepared from the verified v3.19.0 source package available in this chat workspace.

This environment cannot verify `dotnet build` or `dotnet publish` because the .NET SDK is not installed here. Windows validation is required.

## Required Windows commands

```powershell
Set-Location D:\Downloads
Remove-Item -LiteralPath T:\VestigantTriage_v3_21_0 -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath .\VestigantTriage_v3_21_0_risk_sequence_export_safeguards.zip -DestinationPath T:\ -Force
Set-Location T:\VestigantTriage_v3_21_0

powershell -ExecutionPolicy Bypass -File .\tools\Build-And-Validate-VestigantTriage.ps1 -Publish
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase
```

## Risk + duplicate validation

```powershell
powershell -ExecutionPolicy Bypass -File .\RUN_GOOGLE_THIN_TEST_V3_21_0.ps1 -CleanCase -IncludeRisk -IncludeDuplicateGoogleArchives
```
