param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish,
    [string]$RunLogPath = "",
    [string]$CaseRoot = "",
    [switch]$SkipUploadBundleFixtureTest
)

$ErrorActionPreference = "Stop"
$Version = "3.4.3"
# BuildValidationUsesUtf8AddContent marker: build validation logs are appended with Add-Content -Encoding UTF8 instead of Tee-Object.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$VersionRegex = [regex]::Escape($Version)
$VersionToken = $Version -replace '\.', '_'
$ProjectRoot = (Resolve-Path $ProjectRoot).Path
$ValidationRoot = Join-Path $ProjectRoot "ValidationOutput"
New-Item -ItemType Directory -Force -Path $ValidationRoot | Out-Null
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$LogPath = Join-Path $ValidationRoot "build_validate_$Stamp.log"
$StatusPath = Join-Path $ValidationRoot "VALIDATION_STATUS_GENERATED.txt"

function Write-Log {
    param([string]$Message)
    Write-Host $Message
    Add-Content -LiteralPath $LogPath -Value $Message -Encoding UTF8
}

function Run-Step {
    param(
        [string]$Label,
        [scriptblock]$Body
    )
    Write-Log ""
    Write-Log "=== $Label ==="
    & $Body 2>&1 | ForEach-Object {
        $line = [string]$_
        Write-Host $line
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed: $Label"
    }
}

function Assert-FileContains {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Description
    )
    if (-not (Test-Path $Path)) {
        throw "Missing file for assertion: $Path"
    }
    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -notmatch $Pattern) {
        throw "Assertion failed: $Description. Pattern not found: $Pattern in $Path"
    }
    Write-Log "PASS: $Description"
}

function Assert-FileNotContains {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Description
    )
    if (-not (Test-Path $Path)) {
        throw "Missing file for assertion: $Path"
    }
    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -match $Pattern) {
        throw "Assertion failed: $Description. Unexpected pattern found: $Pattern in $Path"
    }
    Write-Log "PASS: $Description"
}

Set-Location $ProjectRoot
Write-Log "Vestigant Triage v$Version build/validation"
Write-Log "ProjectRoot: $ProjectRoot"
Write-Log "Started: $(Get-Date -Format o)"

if (-not (Test-Path (Join-Path $ProjectRoot "VestigantTriage.csproj"))) {
    throw "VestigantTriage.csproj not found at ProjectRoot: $ProjectRoot"
}

Run-Step "dotnet --info" { dotnet --info }
Run-Step "dotnet restore" { dotnet restore }
Run-Step "dotnet build -c $Configuration" { dotnet build -c $Configuration --no-restore }

$BuildExe = Join-Path $ProjectRoot "bin\$Configuration\net8.0-windows\VestigantTriage.exe"
if (-not (Test-Path $BuildExe)) {
    throw "Build completed but executable was not found: $BuildExe"
}
Write-Log "PASS: Build executable exists: $BuildExe"

if ($Publish) {
    Run-Step "dotnet restore -r win-x64" {
        dotnet restore -r win-x64
    }
    Run-Step "dotnet publish -c $Configuration -r win-x64" {
        dotnet publish -c $Configuration -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false --no-restore
    }
    $PublishExe = Join-Path $ProjectRoot "bin\$Configuration\net8.0-windows\win-x64\publish\VestigantTriage.exe"
    if (-not (Test-Path $PublishExe)) {
        throw "Publish completed but executable was not found: $PublishExe"
    }
    Write-Log "PASS: Published executable exists: $PublishExe"

    try {
        $item = Get-Item $PublishExe
        Write-Log "Published FileVersion: $($item.VersionInfo.FileVersion)"
        Write-Log "Published ProductVersion: $($item.VersionInfo.ProductVersion)"
    } catch {
        Write-Log "WARN: Unable to read published version info: $($_.Exception.Message)"
    }
}

Write-Log ""
Write-Log "=== Static source assertions ==="
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\AppInfo.cs") -Pattern "Version\s*\=\s*`"$VersionRegex`"" -Description "AppInfo version is $Version"
Assert-FileContains -Path (Join-Path $ProjectRoot "VestigantTriage.csproj") -Pattern "<Version>$VersionRegex</Version>" -Description "csproj package version is $Version"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ParserCoverageService.cs") -Pattern 'Office Owner File' -Description "Parser Coverage contains Office Owner File family"
Assert-FileContains -Path (Join-Path $ProjectRoot "Triage\ImageTriageCore.cs") -Pattern 'Starting MFT owner/lock file scan' -Description "MFT owner/lock scan marker exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "Triage\ImageTriageCore.cs") -Pattern 'Owner/lock files are collected from the MFT filename index' -Description "Owner files are MFT-index driven"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\IngestEngine.cs") -Pattern 'small artifact ingest group' -Description "Small artifact ingest group exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\IngestEngine.cs") -Pattern 'EVTX ingest group' -Description "EVTX ingest group exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'Test-ContainsPlaceholder' -Description "Upload bundle script detects placeholder paths"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'Case outputs will be skipped' -Description "Upload bundle script supports build-only bundles when CaseRoot is absent or placeholder"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'OutZip contains an example placeholder' -Description "Upload bundle script handles placeholder OutZip paths safely"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'RequireValidationBundle' -Description "Upload bundle script can require validation bundle inclusion"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'Copy-ValidationBundleCandidates' -Description "Upload bundle script searches for validation bundle ZIPs"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'Find-LatestTriageCaseDirectory' -Description "Upload bundle script can auto-detect latest case folder"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'ZIP creation reported no error' -Description "Upload bundle script validates ZIP creation"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'Get-ValidationBundleValidation' -Description "Upload bundle script validates real generated validation bundle ZIP contents"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ValidationBundleService.cs") -Pattern 'vestigant_parser_candidate_conflicts\.csv' -Description "Validation bundle exports parser candidate conflicts"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ValidationBundleService.cs") -Pattern 'vestigant_onedrive_catalog_summary\.csv' -Description "Validation bundle exports OneDrive catalog summary"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\GoogleSourceSupport.cs") -Pattern 'DocumentedAuditFamilies' -Description "Google source registry tracks Workspace audit family taxonomy"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\GoogleWorkspaceAuditParser.cs") -Pattern 'Google Workspace Audit' -Description "Google Workspace Audit parser exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\GoogleTakeoutParser.cs") -Pattern 'Google Takeout' -Description "Google Takeout parser exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\GeminiSessionParser.cs") -Pattern 'Gemini Session Archive' -Description "Gemini session parser exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\ParserRegistry.cs") -Pattern 'new GoogleWorkspaceAuditParser\(\)' -Description "Google Workspace Audit parser is registered"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\IngestEngine.cs") -Pattern 'GoogleWorkspaceAuditParser' -Description "Google audit sources use streaming database import path"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'Google Workspace Audit / Investigation CSV or ZIP' -Description "GUI can add Google audit CSV/ZIP sources"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ValidationBundleService.cs") -Pattern 'vestigant_google_source_coverage\.csv' -Description "Validation bundle exports Google source coverage"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ValidationBundleService.cs") -Pattern 'vestigant_google_schema_coverage\.csv' -Description "Validation bundle exports Google schema coverage"
Assert-FileContains -Path (Join-Path $ProjectRoot "Risk\RiskEngine.cs") -Pattern 'EXF-091' -Description "Risk engine includes Google Takeout risk rule"
Assert-FileContains -Path (Join-Path $ProjectRoot "Risk\RiskEngine.cs") -Pattern 'AI-011' -Description "Risk engine includes Gemini/AI risk rule"
Assert-FileNotContains -Path (Join-Path $ProjectRoot "Parsers\RecycleBinParser.cs") -Pattern 'name\.Contains\("\$I"' -Description 'Recycle Bin parser no longer uses broad $I substring matching'
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'vestigant_source_coverage.csv' -Description "Upload bundle script requires validation bundle source coverage CSV"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'upload_bundle_fixture' -Description "Upload bundle script rejects build fixture validation bundles"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'Is-ProjectReviewFile' -Description "Upload bundle script excludes bin/obj/source-artifact bloat from project review copy"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern 'ArchiveLogs' -Description "Upload bundle includes archive diagnostics and image-backed archive-skip manifests"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ForensicHelpers.cs") -Pattern 'CleanOwnerCandidate' -Description "Office owner parser sanitizes binary owner strings"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ForensicHelpers.cs") -Pattern 'OwnerCandidateScore' -Description "Office owner parser scores plausible owner names instead of longest binary strings"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\IngestEngine.cs") -Pattern 'LogLock' -Description "Parallel ingest logging is serialized"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\IngestEngine.cs") -Pattern 'IsImageBackedEvidenceSet' -Description "Image-backed headless cases skip redundant WorkingEvidence 7-Zip archive"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'Add-Content -LiteralPath \$RunLog -Value \$line -Encoding UTF8' -Description "Root automation script writes UTF-8 logs without Tee-Object UTF-16 output"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\PrintSpoolParser.cs") -Pattern 'Windows Print Spool' -Description "Print spool parser exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\ParserRegistry.cs") -Pattern 'new PrintSpoolParser\(\)' -Description "Print spool parser is registered"
Assert-FileContains -Path (Join-Path $ProjectRoot "Triage\ImageTriageCore.cs") -Pattern 'ExtractPrintSpoolFastTriageArtifacts' -Description "Fast triage collects print spool directory artifacts"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\PrintSpoolParser.cs") -Pattern 'PrintSpoolEnhancedMetafile' -Description "Print spool parser handles EMF rendered payload artifacts"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\PrintSpoolParser.cs") -Pattern 'PrintSpoolPclPayload' -Description "Print spool parser handles PCL payload artifacts"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\PrintSpoolParser.cs") -Pattern 'PrintSpoolDriverBud' -Description "Print spool parser handles print driver/configuration cache artifacts"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\ParserRegistry.cs") -Pattern '\.pjl' -Description "Print spool registry routes PJL/PCL-style artifacts"
Assert-FileContains -Path (Join-Path $ProjectRoot "Triage\ImageTriageCore.cs") -Pattern 'spool\\drivers' -Description "Fast triage collects print driver/configuration artifacts with extension gating"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\EvtxParser.cs") -Pattern 'PrintService_EVTX' -Description "EVTX parser normalizes PrintService events"
Assert-FileContains -Path (Join-Path $ProjectRoot "Risk\RiskEngine.cs") -Pattern 'ExecuteWithSqliteRetry' -Description "Risk engine includes SQLite lock retry handling"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'Ingest is still running' -Description "Risk engine is blocked while ingest is active"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'Auto-created case for' -Description "Case auto-create workflow exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'Automatic validation bundle exported' -Description "Post-ingest automatic validation bundle export exists"
Assert-FileNotContains -Path (Join-Path $ProjectRoot "Triage\ImageTriageCore.cs") -Pattern 'OneDrive -.*StartsWith\("~\$"' -Description "No explicit broad OneDrive owner-file scan pattern remains"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'internal static class HeadlessTriageRunner' -Description "Headless image triage runner exists"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern '--headless-triage' -Description "Application command-line switch supports headless triage"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern '--export-validation-bundle' -Description "Application command-line switch supports standalone validation bundle export"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'ImageTriageCore\.ExtractTargetedArtifacts' -Description "Headless runner can call raw image triage"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'IngestEngine\.ProcessEvidence' -Description "Headless runner can run ingestion"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'ValidationBundleService\.ExportValidationBundle' -Description "Headless runner exports validation bundle"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'HeadlessTriageRunner\.Run' -Description "Application entrypoint supports headless triage"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern '0186_0015-IT001' -Description "Root automation script defaults to current raw image path"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'Create-TriageUploadBundle\.ps1' -Description "Root automation script creates upload bundle"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'Standalone validation export command' -Description "Root automation script falls back to standalone validation bundle export if needed"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'Start-Process -Wait' -Description "Root automation waits for WinExe headless process to finish before packaging"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'StatusComplete' -Description "Root automation normalizes headless status file before testing completion"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'Q:\\TriageCase' -Description "Root automation script defaults to Q:\TriageCase"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_DEFAULT_IMAGE_TRIAGE.ps1") -Pattern 'RUN_IMAGE_TRIAGE_AND_PACKAGE\.ps1' -Description "Default image triage wrapper delegates to root automation script"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern '--headless-google' -Description "Application command-line switch supports headless Google source triage"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'RunGoogleSourceTriage' -Description "Headless runner supports Google source triage"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'FindGoogleSourceCandidates' -Description "Headless Google runner auto-discovers candidate source files"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'SKIPPED_FOR_FAST_GOOGLE_TEST' -Description "Headless Google runner can skip source hashing for fast parser validation"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_GOOGLE_SOURCE_TRIAGE.ps1") -Pattern 'E:\\0445_0001' -Description "Google source automation defaults to current test folder"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_GOOGLE_SOURCE_TRIAGE.ps1") -Pattern '--headless-google' -Description "Google source automation invokes headless Google switch"
Assert-FileContains -Path (Join-Path $ProjectRoot "CHANGELOG.md") -Pattern "(?m)^##\s+v$VersionRegex\b" -Description "Consolidated CHANGELOG contains current version heading"
Assert-FileContains -Path (Join-Path $ProjectRoot "ai_context.md") -Pattern "Project Overview" -Description "Root ai_context.md exists and contains project overview"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern "ai_context\.md" -Description "Upload bundle includes ai_context.md in project documentation"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern "RUN_GOOGLE_SOURCE_TRIAGE\.ps1" -Description "Upload bundle includes Google source automation script in project documentation"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Build-And-Validate-VestigantTriage.ps1") -Pattern '\$separateVersionNotes = @\(' -Description "Strict-mode array wrapping prevents singleton Get-ChildItem Count failures"
if (Test-Path (Join-Path $ProjectRoot "RELEASE_NOTES.md")) { throw "RELEASE_NOTES.md should not be shipped separately; use CHANGELOG.md" }
$separateVersionNotes = @(Get-ChildItem -LiteralPath $ProjectRoot -File -Filter "V*.md" | Where-Object { $_.Name -match '^V[0-9]_' })
if ($separateVersionNotes.Count -gt 0) { throw "Per-version markdown files should be consolidated into CHANGELOG.md: $($separateVersionNotes.Name -join ', ')" }
$legacyVersionDocs = @(Get-ChildItem -LiteralPath $ProjectRoot -File | Where-Object { $_.Name -match '^(PHASE[0-9_].*|TARGET_AND_PREFETCH_DISPLAY_FIXES|REVIEW_NOTES_AND_BUILD_STEPS)\.md$' })
if ($legacyVersionDocs.Count -gt 0) { throw "Legacy phase/version markdown files should be consolidated into CHANGELOG.md: $($legacyVersionDocs.Name -join ', ')" }
Assert-FileNotContains -Path (Join-Path $ProjectRoot "CHANGELOG.md") -Pattern "v3\.3\.10 Headless Status" -Description "Consolidated changelog does not relabel historical v3.3.8 status fixes as v3.3.10"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") -Pattern ('\$Version = "{0}"' -f [regex]::Escape($VersionToken)) -Description "Upload bundle script default version matches current package"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\OneDriveParser.cs") -Pattern 'BestOneDriveDatabaseTimestamp' -Description "OneDrive parser decodes database timestamps"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\OneDriveParser.cs") -Pattern 'OneDrive_Service_Operation' -Description "OneDrive parser emits specific service-operation rows"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\OneDriveParser.cs") -Pattern 'OneDrive_SafeDelete_Record' -Description "OneDrive parser emits specific SafeDelete rows"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\OneDriveParser.cs") -Pattern 'OneDriveDecodedTimestampField' -Description "OneDrive parser records selected decoded timestamp field"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\OneDriveParser.cs") -Pattern 'OneDrive_Config_File_Observed' -Description "OneDrive parser inventories config files that do not contain useful account/path lines"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\ValidationBundleService.cs") -Pattern 'RunValidationCsvExport' -Description "Validation bundle compact CSV exports are failure-tolerant"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'validation_bundle_status' -Description "Headless status records validation bundle export state"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\HeadlessTriageRunner.cs") -Pattern 'validation_bundle_export_error.txt' -Description "Headless runner preserves validation bundle export exceptions"
Assert-FileContains -Path (Join-Path $ProjectRoot "RUN_IMAGE_TRIAGE_AND_PACKAGE.ps1") -Pattern 'Write-HeadlessFailureDiagnostics' -Description "Wrapper prints headless status/log diagnostics on executable failure"
Assert-FileContains -Path (Join-Path $ProjectRoot "tools\Build-And-Validate-VestigantTriage.ps1") -Pattern 'BuildValidationUsesUtf8AddContent' -Description "Build validation logging uses UTF-8 Add-Content marker"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'SafeGrid_DataError' -Description "DataGridView display conversion errors are suppressed instead of showing modal popups"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'ConvertAutoGeneratedImageColumnsToText' -Description "Auto-generated image columns are converted to text columns for metadata grids"
Assert-FileContains -Path (Join-Path $ProjectRoot "UI\Program.cs") -Pattern 'Export All Master Metadata CSV' -Description "Master tab includes all-record metadata CSV export button"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\DatabaseCore.cs") -Pattern 'ExportAllMasterMetadataCsv' -Description "Database core exports all events with dynamic metadata fields"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\DatabaseCore.cs") -Pattern 'PreferredMasterMetadataExportHeaderText' -Description "Master metadata export has preferred UAL-style header text"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\DatabaseCore.cs") -Pattern 'ZipFileName' -Description "Master metadata preferred header list includes trailing ZipFileName column"
Assert-FileContains -Path (Join-Path $ProjectRoot "Core\DatabaseCore.cs") -Pattern 'Vestigant_Tags' -Description "Master metadata export appends Vestigant tags after preferred headers"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\SetupApiDevLogParser.cs") -Pattern 'setupapi\.app\.log' -Description "SetupAPI parser recognizes application installation logs"
Assert-FileContains -Path (Join-Path $ProjectRoot "Triage\ImageTriageCore.cs") -Pattern 'setupapi\.app\.log' -Description "Fast triage collects SetupAPI application logs"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\SetupApiDevLogParser.cs") -Pattern 'SetupApiRiskRelevance' -Description "SetupAPI parser records transfer/destruction risk relevance"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\SetupApiDevLogParser.cs") -Pattern 'ParserDisplayName' -Description "SetupAPI static helper uses static-safe parser display name"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\SetupApiDevLogParser.cs") -Pattern 'WPD_Mobile_MTP' -Description "SetupAPI parser classifies mobile/MTP transfer-capable devices"
Assert-FileContains -Path (Join-Path $ProjectRoot "Parsers\SetupApiDevLogParser.cs") -Pattern 'DestructiveOrDiskUtilitySoftware' -Description "SetupAPI parser classifies destruction/wiping-related application or driver sections"
Assert-FileContains -Path (Join-Path $ProjectRoot "Risk\RiskEngine.cs") -Pattern 'EXF-072' -Description "Risk engine includes SetupAPI transfer-capable device/interface rule"
Assert-FileContains -Path (Join-Path $ProjectRoot "Risk\RiskEngine.cs") -Pattern 'CON-071' -Description "Risk engine includes SetupAPI destructive/wiping tool rule"
Write-Log "PASS: Consolidated changelog is the only version-difference file and parser assertions passed"


if ($SkipUploadBundleFixtureTest) {
    Write-Log ""
    Write-Log "=== Upload-bundle validation-bundle inclusion test ==="
    Write-Log "SKIP: Upload-bundle fixture test skipped for fixed-path headless autorun. The real post-ingest validation bundle is required when packaging the case output."
} else {
    Write-Log ""
    Write-Log "=== Upload-bundle validation-bundle inclusion test ==="
    $FixtureRoot = Join-Path $ValidationRoot "upload_bundle_validation_test"
    $FixtureCase = Join-Path $FixtureRoot "Case"
    $FixtureOut = Join-Path $ValidationRoot "UploadBundleFixture_v$Version.zip"
    $FixtureInnerRoot = Join-Path $FixtureRoot "GeneratedValidationBundle"
    $FixtureExtract = Join-Path $ValidationRoot "UploadBundleFixture_Extracted"
    if (Test-Path -LiteralPath $FixtureRoot) { Remove-Item -LiteralPath $FixtureRoot -Recurse -Force }
    if (Test-Path -LiteralPath $FixtureOut) { Remove-Item -LiteralPath $FixtureOut -Force }
    if (Test-Path -LiteralPath $FixtureExtract) { Remove-Item -LiteralPath $FixtureExtract -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $FixtureCase | Out-Null
    New-Item -ItemType Directory -Force -Path $FixtureInnerRoot | Out-Null
    
    $requiredValidationEntries = @(
        "vestigant_source_coverage.csv",
        "vestigant_parser_coverage.csv",
        "vestigant_parser_errors.csv",
        "vestigant_event_summary_no_ual.csv",
        "vestigant_metadata_fallback_sources.csv",
        "vestigant_distinct_source_files_no_ual.csv",
        "vestigant_case_source_manifest.json",
        "README_validation_bundle.txt"
    )
    foreach ($entryName in $requiredValidationEntries) {
        $entryPath = Join-Path $FixtureInnerRoot $entryName
        if ($entryName -match '\.json$') {
            Set-Content -LiteralPath $entryPath -Value '{"Synthetic":true,"Purpose":"Upload bundle validation fixture"}' -Encoding UTF8
        } elseif ($entryName -match '\.csv$') {
            Set-Content -LiteralPath $entryPath -Value "Synthetic,Rows`r`nTrue,0" -Encoding UTF8
        } else {
            Set-Content -LiteralPath $entryPath -Value "Synthetic generated validation bundle fixture for build-script testing." -Encoding UTF8
        }
    }
    $FixtureValidationZip = Join-Path $FixtureCase "VestigantCase_validation_bundle.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::CreateFromDirectory($FixtureInnerRoot, $FixtureValidationZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    
    try {
        & (Join-Path $ProjectRoot "tools\Create-TriageUploadBundle.ps1") `
          -ProjectRoot $ProjectRoot `
          -CaseRoot $FixtureCase `
          -OutZip $FixtureOut `
          -RequireValidationBundle 2>&1 | ForEach-Object {
            $line = [string]$_
            Write-Host $line
            Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
          }

    } catch {
        throw "Upload bundle script failed validation-bundle inclusion test: $($_.Exception.Message)"
    }
    
    for ($i = 0; $i -lt 20 -and -not (Test-Path -LiteralPath $FixtureOut -PathType Leaf); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $FixtureOut -PathType Leaf)) { throw "Upload bundle script reported completion, but fixture ZIP was not found: $FixtureOut" }
    
    try {
        $outerZip = [System.IO.Compression.ZipFile]::OpenRead($FixtureOut)
        try {
            $IncludedValidationBundle = $outerZip.Entries | Where-Object { $_.Name -eq "VestigantCase_validation_bundle.zip" -and $_.FullName -match '^validation_bundle[\/]' } | Select-Object -First 1
        } finally { $outerZip.Dispose() }
    } catch { throw "Unable to inspect fixture upload ZIP '$FixtureOut': $($_.Exception.Message)" }
    if (-not $IncludedValidationBundle) { throw "Upload bundle did not include a validation_bundle/VestigantCase_validation_bundle.zip entry." }
    Write-Log "PASS: Upload bundle includes validation bundle ZIP entry: $($IncludedValidationBundle.FullName)"
    
    $InnerExtract = Join-Path $FixtureExtract "InnerValidationBundle"
    New-Item -ItemType Directory -Force -Path $FixtureExtract | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($FixtureOut, $FixtureExtract)
    $ExtractedInnerZip = Get-ChildItem -LiteralPath $FixtureExtract -Recurse -File -Filter "VestigantCase_validation_bundle.zip" | Select-Object -First 1
    if (-not $ExtractedInnerZip) { throw "Extracted upload bundle does not contain VestigantCase_validation_bundle.zip." }
    New-Item -ItemType Directory -Force -Path $InnerExtract | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ExtractedInnerZip.FullName, $InnerExtract)
    foreach ($entryName in $requiredValidationEntries) {
        if (-not (Test-Path -LiteralPath (Join-Path $InnerExtract $entryName) -PathType Leaf)) { throw "Inner validation bundle is missing expected entry: $entryName" }
    }
    Write-Log "PASS: Inner validation bundle contains expected generated validation CSV/JSON/readme entries"
}

if ($RunLogPath -and (Test-Path $RunLogPath)) {
    Write-Log ""
    Write-Log "=== Run-log analysis ==="
    $Analyzer = Join-Path $ProjectRoot "tools\Analyze-TriageRunLog.ps1"
    & $Analyzer -RunLogPath $RunLogPath -OutDir $ValidationRoot 2>&1 | ForEach-Object {
        $line = [string]$_
        Write-Host $line
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    }

    if ($LASTEXITCODE -ne 0) { throw "Run-log analyzer failed." }
}

if ($CaseRoot -and ($CaseRoot -notmatch '<[^>]+>') -and (Test-Path -LiteralPath $CaseRoot)) {
    Write-Log ""
    Write-Log "=== Case output inventory ==="
    $inventoryText = Get-ChildItem -LiteralPath $CaseRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -lt 50MB -and ($_.Name -match 'log|validation|coverage|summary|upload|\.csv$|\.json$|\.txt$') } |
        Select-Object FullName, Length, LastWriteTime |
        Format-Table -AutoSize | Out-String -Width 240
    foreach ($line in ($inventoryText -split "\r\n|\n|\r")) {
        Write-Log $line
    }

}

$CaseRootUsable = [bool]($CaseRoot -and ($CaseRoot -notmatch '<[^>]+>') -and (Test-Path -LiteralPath $CaseRoot))

$lines = @(
    "# Generated Validation Status - Vestigant Triage v$Version",
    "Generated: $(Get-Date -Format o)",
    "ProjectRoot: $ProjectRoot",
    "Configuration: $Configuration",
    "BuildExe: $BuildExe",
    "BuildResult: PASS",
    "PublishRequested: $Publish",
    "RunLogAnalyzed: $([bool]($RunLogPath -and (Test-Path $RunLogPath)))",
    "CaseRootProvided: $CaseRootUsable",
    "",
    "## Next upload",
    "Run a real GUI ingest, export the Validation Bundle, then run tools\\Create-TriageUploadBundle.ps1."
)
Set-Content -LiteralPath $StatusPath -Value $lines -Encoding UTF8
Write-Log ""
Write-Log "Validation status written: $StatusPath"
Write-Log "Completed: $(Get-Date -Format o)"
Write-Host ""
Write-Host "Build/validation completed."
Write-Host "Log: $LogPath"
Write-Host "Status: $StatusPath"
