# Vestigant Triage v3.4.3 Explicit Path Run Reference

```powershell
Set-Location D:\Downloads
Get-FileHash .\VestigantTriage_v3_4_3_print_artifact_triage_expansion.zip -Algorithm SHA256
Expand-Archive -LiteralPath .\VestigantTriage_v3_4_3_print_artifact_triage_expansion.zip -DestinationPath T:\ -Force

$ImagePath = "I:\0186_0015-IT001.E01 - Partition 3 (471.56 GB)_decrypted.img"
$CaseRoot = "Q:\TriageCase\V3_4_3_0186_0015_IT001_Triage"
$OutZip = "D:\Downloads\Upload_VestigantTriage_v3_4_3_0186_0015_IT001_Triage.zip"

& "T:\VestigantTriage_v3_4_3\RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1" `
  -ImagePath $ImagePath `
  -CaseRoot $CaseRoot `
  -CaseName "V3_4_3_0186_0015_IT001_Triage" `
  -OutZip $OutZip `
  -ScanMode Triage
```
