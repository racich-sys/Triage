# Validation Status - Vestigant Triage v3.4.3

## Assistant-side validation

Static/package checks only. The assistant environment does not have the Windows .NET SDK/runtime environment or the test image at `I:\0186_0015-IT001.E01 - Partition 3 (471.56 GB)_decrypted.img`.

## Windows validation required

Run:

```powershell
Set-Location D:\Downloads
Get-FileHash .\VestigantTriage_v3_4_3_google_framework_build_fix.zip -Algorithm SHA256
Expand-Archive -LiteralPath .\VestigantTriage_v3_4_3_google_framework_build_fix.zip -DestinationPath T:\ -Force
& "T:\VestigantTriage_v3_4_3\RUN_DEFAULT_IMAGE_TRIAGE.ps1"
```

Expected upload:

```text
D:\Downloads\Upload_VestigantTriage_v3_4_3_*.zip
```
