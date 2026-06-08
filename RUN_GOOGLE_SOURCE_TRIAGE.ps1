param(
    [string]$GoogleRoot = "E:\0445_0001",
    [string]$CaseBaseRoot = "Q:\TriageCase",
    [string]$CaseRoot = "",
    [string]$CaseName = "",
    [string]$OutZip = "",
    [switch]$SkipBuild,
    [switch]$SkipIngest,
    [switch]$SkipRisk,
    [switch]$HashGoogleSources,
    [switch]$CleanCase,
    [switch]$ReuseCase
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Split-Path -Parent $MyInvocation.MyCommand.Path)).Path
$Version = "3.4.3"
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"

function Assert-PathIsNotPlaceholder {
    param([string]$Value, [string]$Name)
    if ($Value -match '<[^>]+>') { throw "$Name contains an example placeholder and must be replaced: $Value" }
}
function Write-RunLog {
    param([string]$Message)
    $line = "{0} - {1}" -f (Get-Date -Format "HH:mm:ss"), $Message
    Write-Host $line
    if (-not [string]::IsNullOrWhiteSpace($script:RunLog)) {
        Add-Content -LiteralPath $script:RunLog -Value $line -Encoding UTF8
    }
}
function Invoke-AndLog {
    param([scriptblock]$Command, [string]$FailureMessage)
    $script:LastToolExitCode = 0
    & $Command 2>&1 | ForEach-Object {
        $line = [string]$_
        Write-Host $line
        Add-Content -LiteralPath $script:RunLog -Value $line -Encoding UTF8
    }
    if ($LASTEXITCODE -ne $null) { $script:LastToolExitCode = $LASTEXITCODE } else { $script:LastToolExitCode = 0 }
    if ($script:LastToolExitCode -ne 0) { throw "$FailureMessage ExitCode=$script:LastToolExitCode. See $script:RunLog" }
}
function Join-ProcessArguments {
    param([string[]]$Arguments)
    (($Arguments | ForEach-Object {
        $arg = [string]$_
        if ($arg -match '[\s"]') { '"' + ($arg -replace '"', '\"') + '"' } else { $arg }
    }) -join ' ')
}
function Invoke-ExecutableAndWait {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory, [string]$FailureMessage)
    $argumentText = Join-ProcessArguments -Arguments $Arguments
    Write-RunLog "Start-Process -Wait: `"$FilePath`" $argumentText"
    $process = Start-Process -FilePath $FilePath -ArgumentList $argumentText -WorkingDirectory $WorkingDirectory -Wait -PassThru
    $exitCode = if ($null -ne $process.ExitCode) { [int]$process.ExitCode } else { 0 }
    Write-RunLog "Process exit code: $exitCode"
    if ($exitCode -ne 0) { throw "$FailureMessage ExitCode=$exitCode. See $script:RunLog" }
}
function Write-GoogleFailureDiagnostics {
    param([string]$StatusPath, [string]$HeadlessLog)
    Write-RunLog "Collecting Google headless failure diagnostics."
    if (Test-Path -LiteralPath $StatusPath -PathType Leaf) {
        Write-RunLog "--- headless_google_run_status.txt ---"
        Get-Content -LiteralPath $StatusPath -ErrorAction SilentlyContinue | ForEach-Object { Write-RunLog "STATUS: $_" }
    } else {
        Write-RunLog "Google headless status file not found: $StatusPath"
    }
    if (Test-Path -LiteralPath $HeadlessLog -PathType Leaf) {
        Write-RunLog "--- tail headless_google_triage.log ---"
        Get-Content -LiteralPath $HeadlessLog -Tail 120 -ErrorAction SilentlyContinue | ForEach-Object { Write-RunLog "HEADLESS: $_" }
    } else {
        Write-RunLog "Google headless log file not found: $HeadlessLog"
    }
}

Assert-PathIsNotPlaceholder -Value $GoogleRoot -Name "GoogleRoot"
Assert-PathIsNotPlaceholder -Value $CaseBaseRoot -Name "CaseBaseRoot"
Assert-PathIsNotPlaceholder -Value $CaseRoot -Name "CaseRoot"
Assert-PathIsNotPlaceholder -Value $OutZip -Name "OutZip"

if (-not (Test-Path -LiteralPath $GoogleRoot)) {
    throw "GoogleRoot does not exist: $GoogleRoot"
}
if (-not (Test-Path -LiteralPath $CaseBaseRoot -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $CaseBaseRoot | Out-Null
}
if ([string]::IsNullOrWhiteSpace($CaseRoot)) {
    if ([string]::IsNullOrWhiteSpace($CaseName)) {
        $CaseName = "V3_4_3_Google_0445_0001_$Stamp"
    }
    $CaseRoot = Join-Path $CaseBaseRoot $CaseName
}
if ([string]::IsNullOrWhiteSpace($CaseName)) {
    $CaseName = Split-Path -Leaf $CaseRoot
}
if ((Test-Path -LiteralPath $CaseRoot -PathType Container) -and $CleanCase) {
    Remove-Item -LiteralPath $CaseRoot -Recurse -Force
} elseif ((Test-Path -LiteralPath $CaseRoot -PathType Container) -and -not $ReuseCase) {
    $baseName = Split-Path -Leaf $CaseRoot
    $parent = Split-Path -Parent $CaseRoot
    $CaseName = "${baseName}_$Stamp"
    $CaseRoot = Join-Path $parent $CaseName
}
New-Item -ItemType Directory -Force -Path $CaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $CaseRoot "Upload") | Out-Null
if ([string]::IsNullOrWhiteSpace($OutZip)) {
    $safeCase = ($CaseName -replace '[^A-Za-z0-9._-]', '_').Trim('_','.')
    if ([string]::IsNullOrWhiteSpace($safeCase)) { $safeCase = "V3_4_3_GoogleSourceTriage_$Stamp" }
    $OutZip = "D:\Downloads\Upload_VestigantTriage_v3_4_3_$safeCase.zip"
}

$script:RunLog = Join-Path $CaseRoot "Upload\RUN_GOOGLE_SOURCE_TRIAGE_$Stamp.log"
if (Test-Path -LiteralPath $script:RunLog -PathType Leaf) { Remove-Item -LiteralPath $script:RunLog -Force }
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

Write-RunLog "Vestigant Triage v$Version automated Google source/package run"
Write-RunLog "ProjectRoot: $ProjectRoot"
Write-RunLog "GoogleRoot: $GoogleRoot"
Write-RunLog "CaseRoot: $CaseRoot"
Write-RunLog "CaseName: $CaseName"
Write-RunLog "OutZip: $OutZip"
Write-RunLog "HashGoogleSources: $($HashGoogleSources.IsPresent)"

if (-not $SkipBuild) {
    Write-RunLog "Running build/validation/publish script."
    Invoke-AndLog -Command { & (Join-Path $ProjectRoot "tools\Build-And-Validate-VestigantTriage.ps1") -ProjectRoot $ProjectRoot -Publish -SkipUploadBundleFixtureTest } -FailureMessage "Build-And-Validate-VestigantTriage.ps1 failed."
}

$PublishExe = Join-Path $ProjectRoot "bin\Release\net8.0-windows\win-x64\publish\VestigantTriage.exe"
$BuildExe = Join-Path $ProjectRoot "bin\Release\net8.0-windows\VestigantTriage.exe"
$Exe = if (Test-Path -LiteralPath $PublishExe -PathType Leaf) { $PublishExe } elseif (Test-Path -LiteralPath $BuildExe -PathType Leaf) { $BuildExe } else { throw "VestigantTriage.exe was not found. Expected $PublishExe or $BuildExe" }
Write-RunLog "Using executable: $Exe"

$HeadlessLog = Join-Path $CaseRoot "Upload\headless_google_triage.log"
$HeadlessArgs = @(
    "--headless-google",
    "--google-root", $GoogleRoot,
    "--case-root", $CaseRoot,
    "--case-name", $CaseName,
    "--log-path", $HeadlessLog
)
if ($SkipIngest) { $HeadlessArgs += "--skip-ingest" }
if ($SkipRisk) { $HeadlessArgs += "--skip-risk" }
if ($HashGoogleSources) { $HeadlessArgs += "--hash-google-sources" }

Write-RunLog "Starting headless Google source triage run."
Write-RunLog "Command: `"$Exe`" $($HeadlessArgs -join ' ')"
$ExeDir = Split-Path -Parent $Exe
try {
    Invoke-ExecutableAndWait -FilePath $Exe -Arguments $HeadlessArgs -WorkingDirectory $ExeDir -FailureMessage "Headless Google source triage failed."
} catch {
    Write-GoogleFailureDiagnostics -StatusPath (Join-Path $CaseRoot "Upload\headless_google_run_status.txt") -HeadlessLog $HeadlessLog
    throw
}
Write-RunLog "Headless Google source triage completed."

$StatusPath = Join-Path $CaseRoot "Upload\headless_google_run_status.txt"
if (Test-Path -LiteralPath $StatusPath -PathType Leaf) {
    $StatusText = Get-Content -LiteralPath $StatusPath -Raw
    $StatusClean = $StatusText -replace "`0", ""
    $StatusClean = $StatusClean -replace ([string][char]0xFEFF), ""
    $StatusLines = @($StatusClean -split "\r\n|\n|\r" | ForEach-Object { $_ -replace "^\s+|\s+$", "" } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($statusLine in $StatusLines) { Write-RunLog "GoogleHeadlessStatus: $statusLine" }
    $StatusComplete = $false
    foreach ($statusLine in $StatusLines) {
        if ($statusLine.Equals("status=complete", [System.StringComparison]::OrdinalIgnoreCase)) { $StatusComplete = $true; break }
    }
    if (-not $StatusComplete) {
        throw "Headless Google process returned success, but $StatusPath does not report status=complete. See $script:RunLog and $HeadlessLog."
    }
} else {
    Write-RunLog "WARN: Google headless status file was not found: $StatusPath"
}

$UploadDir = Join-Path $CaseRoot "Upload"
$ValidationBundle = Get-ChildItem -LiteralPath $UploadDir -Recurse -File -Filter "*validation*bundle*.zip" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $ValidationBundle) {
    Write-RunLog "No validation bundle ZIP was found after Google triage. Attempting standalone validation-bundle export from case.json/case.db."
    $FallbackValidationZip = Join-Path $UploadDir (($CaseName -replace '[^A-Za-z0-9._-]', '_').Trim('_','.') + "_validation_bundle.zip")
    $ExportArgs = @(
        "--export-validation-bundle",
        "--case-root", $CaseRoot,
        "--case-json", (Join-Path $CaseRoot "case.json"),
        "--case-db", (Join-Path $CaseRoot "case.db"),
        "--validation-bundle-zip", $FallbackValidationZip,
        "--log-path", $HeadlessLog
    )
    Invoke-ExecutableAndWait -FilePath $Exe -Arguments $ExportArgs -WorkingDirectory $ExeDir -FailureMessage "Standalone validation-bundle export failed."
    $ValidationBundle = Get-Item -LiteralPath $FallbackValidationZip -ErrorAction Stop
}
Write-RunLog "Validation bundle: $($ValidationBundle.FullName)"

$BundleScript = Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1"
Write-RunLog "Creating thin upload bundle."
Invoke-AndLog -Command { & $BundleScript -ProjectRoot $ProjectRoot -CaseRoot $CaseRoot -OutZip $OutZip -RequireValidationBundle } -FailureMessage "Create-TriageUploadBundle.ps1 failed."
if (-not (Test-Path -LiteralPath $OutZip -PathType Leaf)) { throw "Upload ZIP was not created: $OutZip" }
Write-RunLog "Upload ZIP created: $OutZip"
Write-RunLog "Google source run complete."
