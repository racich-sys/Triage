$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Split-Path -Parent $MyInvocation.MyCommand.Path)).Path
& (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") `
  -ImagePath "I:\0186_0015-IT001.E01 - Partition 3 (471.56 GB)_decrypted.img" `
  -CaseBaseRoot "Q:\TriageCase" `
  -ScanMode Triage
