param(
    [string]$ImagePath = "I:\0186_0015-IT001.E01 - Partition 3 (471.56 GB)_decrypted.img",
    [string]$CaseBaseRoot = "Q:\TriageCase",
    [string]$CaseRoot = "",
    [string]$CaseName = "",
    [ValidateSet("Triage", "Full")]
    [string]$ScanMode = "Triage",
    [string]$OutZip = "",
    [switch]$SkipBuild,
    [switch]$SkipIngest,
    [switch]$CleanCase,
    [switch]$ReuseCase
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Split-Path -Parent $MyInvocation.MyCommand.Path)).Path
$Version = "3.21.0"
$VersionToken = "v3_21_0"
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"

function Assert-PathIsNotPlaceholder {
    param([string]$Value, [string]$Name)
    if ($Value -match '<[^>]+>') { throw "$Name contains an example placeholder and must be replaced: $Value" }
}

Assert-PathIsNotPlaceholder -Value $ImagePath -Name "ImagePath"
Assert-PathIsNotPlaceholder -Value $CaseBaseRoot -Name "CaseBaseRoot"
Assert-PathIsNotPlaceholder -Value $CaseRoot -Name "CaseRoot"
Assert-PathIsNotPlaceholder -Value $OutZip -Name "OutZip"

if (-not (Test-Path -LiteralPath $ImagePath -PathType Leaf)) {
    throw "ImagePath does not exist: $ImagePath"
}

if (-not (Test-Path -LiteralPath $CaseBaseRoot -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $CaseBaseRoot | Out-Null
}

if ([string]::IsNullOrWhiteSpace($CaseRoot)) {
    if ([string]::IsNullOrWhiteSpace($CaseName)) {
        $CaseName = "V3_21_0_0186_0015_IT001_Triage_$Stamp"
    }
    $CaseRoot = Join-Path $CaseBaseRoot $CaseName
}

if ([string]::IsNullOrWhiteSpace($CaseName)) {
    $CaseName = Split-Path -Leaf $CaseRoot
}

if ((Test-Path -LiteralPath $CaseRoot -PathType Container) -and $CleanCase) {
    Remove-Item -LiteralPath $CaseRoot -Recurse -Force
}
elseif ((Test-Path -LiteralPath $CaseRoot -PathType Container) -and -not $ReuseCase) {
    $baseName = Split-Path -Leaf $CaseRoot
    $parent = Split-Path -Parent $CaseRoot
    $CaseName = "${baseName}_$Stamp"
    $CaseRoot = Join-Path $parent $CaseName
}

New-Item -ItemType Directory -Force -Path $CaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $CaseRoot "Upload") | Out-Null

if ([string]::IsNullOrWhiteSpace($OutZip)) {
    $safeCase = ($CaseName -replace '[^A-Za-z0-9._-]', '_').Trim('_','.')
    if ([string]::IsNullOrWhiteSpace($safeCase)) { $safeCase = "V3_21_0_AutoImageTriage_$Stamp" }
    $OutZip = "D:\Downloads\Upload_VestigantTriage_v3_21_0_$safeCase.zip"
}

$RunLog = Join-Path $CaseRoot "Upload\RUN_IMAGE_TRIAGE_AND_PACKAGE_$Stamp.log"
if (Test-Path -LiteralPath $RunLog -PathType Leaf) { Remove-Item -LiteralPath $RunLog -Force }
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
function Write-RunLog {
    param([string]$Message)
    $line = "{0} - {1}" -f (Get-Date -Format "HH:mm:ss"), $Message
    Write-Host $line
    Add-Content -LiteralPath $RunLog -Value $line -Encoding UTF8
}
function Invoke-AndLog {
    param([scriptblock]$Command, [string]$FailureMessage)
    $script:LastToolExitCode = 0
    & $Command 2>&1 | ForEach-Object {
        $line = [string]$_
        Write-Host $line
        Add-Content -LiteralPath $RunLog -Value $line -Encoding UTF8
    }
    if ($LASTEXITCODE -ne $null) { $script:LastToolExitCode = $LASTEXITCODE } else { $script:LastToolExitCode = 0 }
    if ($script:LastToolExitCode -ne 0) { throw "$FailureMessage ExitCode=$script:LastToolExitCode. See $RunLog" }
}

function Join-ProcessArguments {
    param([string[]]$Arguments)
    (($Arguments | ForEach-Object {
        $arg = [string]$_
        if ($arg -match '[\s"]') {
            '"' + ($arg -replace '"', '\"') + '"'
        } else {
            $arg
        }
    }) -join ' ')
}
function Write-HeadlessFailureDiagnostics {
    param([string]$StatusPath, [string]$HeadlessLog)
    Write-RunLog "Collecting headless failure diagnostics."
    if (Test-Path -LiteralPath $StatusPath -PathType Leaf) {
        Write-RunLog "--- headless_run_status.txt ---"
        Get-Content -LiteralPath $StatusPath -ErrorAction SilentlyContinue | ForEach-Object { Write-RunLog "STATUS: $_" }
    } else {
        Write-RunLog "Headless status file not found: $StatusPath"
    }
    if (Test-Path -LiteralPath $HeadlessLog -PathType Leaf) {
        Write-RunLog "--- tail headless_image_triage.log ---"
        Get-Content -LiteralPath $HeadlessLog -Tail 80 -ErrorAction SilentlyContinue | ForEach-Object { Write-RunLog "HEADLESS: $_" }
    } else {
        Write-RunLog "Headless log file not found: $HeadlessLog"
    }
}

function Invoke-ExecutableAndWait {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$FailureMessage
    )
    $argumentText = Join-ProcessArguments -Arguments $Arguments
    Write-RunLog "Start-Process -Wait: `"$FilePath`" $argumentText"
    $process = Start-Process -FilePath $FilePath -ArgumentList $argumentText -WorkingDirectory $WorkingDirectory -Wait -PassThru
    $exitCode = if ($null -ne $process.ExitCode) { [int]$process.ExitCode } else { 0 }
    Write-RunLog "Process exit code: $exitCode"
    if ($exitCode -ne 0) { throw "$FailureMessage ExitCode=$exitCode. See $RunLog" }
}

Write-RunLog "Vestigant Triage v$Version automated image triage/package run"
Write-RunLog "ProjectRoot: $ProjectRoot"
Write-RunLog "ImagePath: $ImagePath"
Write-RunLog "CaseBaseRoot: $CaseBaseRoot"
Write-RunLog "CaseRoot: $CaseRoot"
Write-RunLog "CaseName: $CaseName"
Write-RunLog "ScanMode: $ScanMode"
Write-RunLog "OutZip: $OutZip"

if (-not $SkipBuild) {
    Write-RunLog "Running build/validation/publish script."
    Invoke-AndLog -Command { & (Join-Path $ProjectRoot "tools\Build-And-Validate-VestigantTriage.ps1") -ProjectRoot $ProjectRoot -Publish -SkipUploadBundleFixtureTest } -FailureMessage "Build-And-Validate-VestigantTriage.ps1 failed."
}

$PublishExe = Join-Path $ProjectRoot "bin\Release\net8.0-windows\win-x64\publish\VestigantTriage.exe"
$BuildExe = Join-Path $ProjectRoot "bin\Release\net8.0-windows\VestigantTriage.exe"
$Exe = if (Test-Path -LiteralPath $PublishExe -PathType Leaf) { $PublishExe } elseif (Test-Path -LiteralPath $BuildExe -PathType Leaf) { $BuildExe } else { throw "VestigantTriage.exe was not found. Expected $PublishExe or $BuildExe" }
Write-RunLog "Using executable: $Exe"

$HeadlessLog = Join-Path $CaseRoot "Upload\headless_image_triage.log"
$HeadlessArgs = @(
    "--headless-triage",
    "--image", $ImagePath,
    "--case-root", $CaseRoot,
    "--case-name", $CaseName,
    "--scan-mode", $ScanMode,
    "--log-path", $HeadlessLog
)
if ($SkipIngest) { $HeadlessArgs += "--skip-ingest" }

Write-RunLog "Starting headless image triage run."
Write-RunLog "Command: `"$Exe`" $($HeadlessArgs -join ' ')"
$ExeDir = Split-Path -Parent $Exe
try {
    Invoke-ExecutableAndWait -FilePath $Exe -Arguments $HeadlessArgs -WorkingDirectory $ExeDir -FailureMessage "Headless image triage failed."
} catch {
    Write-HeadlessFailureDiagnostics -StatusPath (Join-Path $CaseRoot "Upload\headless_run_status.txt") -HeadlessLog $HeadlessLog
    throw
}
Write-RunLog "Headless image triage completed."

$StatusPath = Join-Path $CaseRoot "Upload\headless_run_status.txt"
if (Test-Path -LiteralPath $StatusPath -PathType Leaf) {
    $StatusText = Get-Content -LiteralPath $StatusPath -Raw
    # Normalize CRLF, UTF-16/NUL remnants, BOM, and whitespace before testing status.
    # Use regex replacement instead of String.Trim(char[]) overloads so Windows PowerShell 5.1 does not
    # mis-convert multi-character strings such as " `t`r`n" to System.Char.
    $StatusClean = $StatusText -replace "`0", ""
    $StatusClean = $StatusClean -replace ([string][char]0xFEFF), ""
    $StatusLines = @($StatusClean -split "\r\n|\n|\r" | ForEach-Object { $_ -replace "^\s+|\s+$", "" } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($statusLine in $StatusLines) {
        Write-RunLog "HeadlessStatus: $statusLine"
    }
    $StatusComplete = $false
    foreach ($statusLine in $StatusLines) {
        if ($statusLine.Equals("status=complete", [System.StringComparison]::OrdinalIgnoreCase)) {
            $StatusComplete = $true
            break
        }
    }
    if (-not $StatusComplete) {
        throw "Headless process returned success, but $StatusPath does not report status=complete. See $RunLog and $HeadlessLog."
    }
} else {
    Write-RunLog "WARN: Headless status file was not found: $StatusPath"
}

$UploadDir = Join-Path $CaseRoot "Upload"
$ValidationBundle = Get-ChildItem -LiteralPath $UploadDir -Recurse -File -Filter "*validation*bundle*.zip" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not $ValidationBundle) {
    Write-RunLog "No validation bundle ZIP was found after headless triage. Attempting standalone validation-bundle export from case.json/case.db."
    $FallbackValidationZip = Join-Path $UploadDir (($CaseName -replace '[^A-Za-z0-9._-]', '_').Trim('_','.') + "_validation_bundle.zip")
    if ([string]::IsNullOrWhiteSpace((Split-Path -Leaf $FallbackValidationZip))) {
        $FallbackValidationZip = Join-Path $UploadDir "VestigantCase_validation_bundle.zip"
    }
    $ExportArgs = @(
        "--export-validation-bundle",
        "--case-root", $CaseRoot,
        "--case-json", (Join-Path $CaseRoot "case.json"),
        "--case-db", (Join-Path $CaseRoot "case.db"),
        "--validation-bundle-zip", $FallbackValidationZip,
        "--log-path", $HeadlessLog
    )
    Write-RunLog "Standalone validation export command: `"$Exe`" $($ExportArgs -join ' ')"
    Invoke-ExecutableAndWait -FilePath $Exe -Arguments $ExportArgs -WorkingDirectory $ExeDir -FailureMessage "Standalone validation bundle export failed."
    $ValidationBundle = Get-ChildItem -LiteralPath $UploadDir -Recurse -File -Filter "*validation*bundle*.zip" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

if (-not $ValidationBundle) {
    $caseJson = Join-Path $CaseRoot "case.json"
    $caseDb = Join-Path $CaseRoot "case.db"
    throw "No validation bundle ZIP was found or could be generated under $CaseRoot\Upload. Checked CaseJsonExists=$(Test-Path -LiteralPath $caseJson -PathType Leaf), CaseDbExists=$(Test-Path -LiteralPath $caseDb -PathType Leaf). See $RunLog and $HeadlessLog."
}
Write-RunLog "Validation bundle selected for upload package: $($ValidationBundle.FullName)"

Write-RunLog "Creating upload bundle: $OutZip"
Invoke-AndLog -Command { & (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") `
    -ProjectRoot $ProjectRoot `
    -CaseRoot $CaseRoot `
    -ValidationBundleZip $ValidationBundle.FullName `
    -RequireValidationBundle `
    -OutZip $OutZip } -FailureMessage "Create-TriageUploadBundle.ps1 failed."

if (-not (Test-Path -LiteralPath $OutZip -PathType Leaf)) {
    throw "Expected upload ZIP was not created: $OutZip"
}

Write-RunLog "Complete. Upload ZIP: $OutZip"
Write-Host "Upload ZIP: $OutZip"
Write-Host "CaseRoot: $CaseRoot"
Write-Host "RunLog: $RunLog"
Write-Host "ValidationBundle: $($ValidationBundle.FullName)"
