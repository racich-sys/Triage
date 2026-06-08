param(
    [string]$GoogleRoot = "E:\0445_0001",
    [string]$CaseBaseRoot = "Q:\TriageCase",
    [switch]$CleanCase,
    [switch]$IncludeRisk,
    [switch]$HashGoogleSources,
    [switch]$IncludeMbox,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Split-Path -Parent $MyInvocation.MyCommand.Path)).Path
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$CaseName = "V3_7_0_GoogleThin_0445_0001_$Stamp"
$CaseRoot = Join-Path $CaseBaseRoot $CaseName
$OutZip = "D:\Downloads\Upload_VestigantTriage_v3_7_0_$CaseName.zip"

$runnerParams = @{
    GoogleRoot = $GoogleRoot
    CaseBaseRoot = $CaseBaseRoot
    CaseRoot = $CaseRoot
    CaseName = $CaseName
    OutZip = $OutZip
}
if ($CleanCase) { $runnerParams.CleanCase = $true }
if ($SkipBuild) { $runnerParams.SkipBuild = $true }
if (-not $IncludeRisk) { $runnerParams.SkipRisk = $true }
if ($HashGoogleSources) { $runnerParams.HashGoogleSources = $true }
if ($IncludeMbox) { $runnerParams.IncludeMbox = $true }

Write-Host "Running Vestigant Triage v3.7.0 Google thin test."
Write-Host "CaseRoot: $CaseRoot"
Write-Host "OutZip: $OutZip"
Write-Host "Risk: $(if ($IncludeRisk) { 'included' } else { 'skipped for thin test' })"

& (Join-Path $ProjectRoot "RUN_GOOGLE_SOURCE_TRIAGE.ps1") @runnerParams
