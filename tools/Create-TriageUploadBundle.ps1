param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$CaseRoot = "",
    [string]$OutZip = "",
    [switch]$IncludeDatabase,
    [string]$ValidationBundleZip = "",
    [switch]$RequireValidationBundle,
    [switch]$RequireCaseRoot
)

$ErrorActionPreference = "Stop"
$Version = "3_7_0"
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$script:ValidationBundleIncluded = $false
$script:ValidationBundleFiles = New-Object System.Collections.Generic.List[string]
$script:ValidationBundleSource = ""

function Test-ContainsPlaceholder {
    param([string]$Value)
    return (-not [string]::IsNullOrWhiteSpace($Value)) -and ($Value -match '<[^>]+>')
}

function Get-DefaultOutputZip {
    $downloadRoot = "D:\Downloads"
    if (-not (Test-Path -LiteralPath $downloadRoot -PathType Container)) { $downloadRoot = $env:TEMP }
    return (Join-Path $downloadRoot "Upload_VestigantTriage_v$Version`_$Stamp.zip")
}

function Resolve-RequiredDirectory {
    param([string]$Path, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label was not provided." }
    if (Test-ContainsPlaceholder $Path) { throw "$Label still contains an example placeholder: $Path. Replace values like <CaseName> with the real folder name." }
    try { return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path }
    catch { throw "$Label does not exist or cannot be resolved: $Path" }
}

function Resolve-OutputZipPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return Get-DefaultOutputZip }
    if (Test-ContainsPlaceholder $Path) {
        $fallback = Get-DefaultOutputZip
        Write-Warning "OutZip contains an example placeholder: $Path. Using generated output path instead: $fallback"
        return $fallback
    }
    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = Get-Location
        $Path = Join-Path $parent $Path
    }
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    return $Path
}

function Get-ValidationBundleValidation {
    param([string]$Path)
    $result = New-Object PSObject -Property @{ IsValid = $false; Reason = ""; Entries = @() }
    if ([string]::IsNullOrWhiteSpace($Path)) { $result.Reason = "Blank path"; return $result }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $result.Reason = "File does not exist"; return $result }
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.Length -lt 256) { $result.Reason = "File is too small to be a generated validation bundle ZIP ($($item.Length) bytes)"; return $result }
    if ($item.Name -notmatch '(?i)(validation.*bundle|bundle.*validation|VestigantCase_validation_bundle).*\.zip$') { $result.Reason = "File name does not look like a validation-bundle ZIP"; return $result }
    if ($item.FullName -match '(?i)[\\/]upload_bundle_fixture[\\/]') { $result.Reason = "Build-fixture validation bundle is not a real case validation bundle"; return $result }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $zip = [System.IO.Compression.ZipFile]::OpenRead($item.FullName)
        try {
            $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('/', '\') })
            $result.Entries = $entries
            $required = @(
                'vestigant_source_coverage.csv',
                'vestigant_parser_coverage.csv',
                'vestigant_parser_errors.csv',
                'vestigant_event_summary_no_ual.csv',
                'vestigant_metadata_fallback_sources.csv',
                'vestigant_distinct_source_files_no_ual.csv',
                'vestigant_case_source_manifest.json',
                'README_validation_bundle.txt'
            )
            $missing = @()
            foreach ($name in $required) {
                if (-not ($entries | Where-Object { $_ -ieq $name })) { $missing += $name }
            }
            if ($missing.Count -gt 0) { $result.Reason = "Missing expected validation-bundle entries: $($missing -join ', ')"; return $result }
            $result.IsValid = $true
            $result.Reason = "OK"
            return $result
        } finally { $zip.Dispose() }
    } catch {
        $result.Reason = "Not a readable ZIP or not a generated validation bundle: $($_.Exception.Message)"
        return $result
    }
}

function Copy-IfExists {
    param([string]$Path, [string]$SubDir = "")
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $destDir = if ($SubDir) { Join-Path $Stage $SubDir } else { $Stage }
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item -LiteralPath $Path -Destination $destDir -Force
}

function Copy-TreeFiltered {
    param([string]$Root, [string]$SubDir, [scriptblock]$Predicate)
    if ([string]::IsNullOrWhiteSpace($Root)) { return }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    $destRoot = Join-Path $Stage $SubDir
    New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
    Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        $file = $_
        if (-not (& $Predicate $file $Root)) { return }
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\','/')
        $dest = Join-Path $destRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
    }
}

function Is-ProjectReviewFile {
    param($File, [string]$Root)
    $relative = $File.FullName.Substring($Root.Length).TrimStart('\','/')
    if ($relative -match '(?i)^(bin|obj|publish|case_outputs|upload_bundle_fixture)[\\/]') { return $false }
    if ($relative -match '(?i)[\\/](bin|obj|publish|upload_bundle_fixture)[\\/]') { return $false }
    if ($File.Length -gt 50MB) { return $false }
    if ($relative -match '(?i)^ValidationOutput[\\/]' -and $File.Name -match '(?i)(\.log$|status.*\.txt$|VALIDATION_STATUS.*\.txt$|UploadBundleFixture.*\.zip$)') { return $true }
    if ($relative -match '(?i)^tools[\\/].*\.ps1$') { return $true }
    if ($relative -match '(?i)^docs[\\/].*\.(md|txt)$') { return $true }
    if ($relative -notmatch '[\\/]' -and $File.Name -match '(?i)(ai_context|roadmap|release|build|validation|readme|notes).*\.(md|txt|json)$') { return $true }
    return $false
}

function Is-CaseReviewFile {
    param($File, [string]$Root)
    $relative = $File.FullName.Substring($Root.Length).TrimStart('\','/')
    if ($File.Length -gt 100MB) { return $false }
    if ($File.Extension -match '(?i)^\.(db|sqlite|sqlite3)$') { return [bool]$IncludeDatabase }
    if ($relative -match '(?i)(^|[\\/])ArchiveLogs[\\/]') { return $true }
    if ($relative -match '(?i)^[^\\/]*$' -and $File.Name -match '(?i)^(Live_|Ghost_)') { return $false }
    if ($relative -match '(?i)(^[\\/]?Upload[\\/]|[\\/]Upload[\\/]|^[\\/]?Validation[\\/]|[\\/]Validation[\\/]|^[\\/]?ValidationOutput[\\/]|[\\/]ValidationOutput[\\/]|^[\\/]?logs?[\\/]|[\\/]logs?[\\/]|^[\\/]?reports?[\\/]|[\\/]reports?[\\/]|^[\\/]?exports[\\/]|[\\/]exports[\\/])') { return $true }
    if ($File.Name -match '(?i)(validation.*bundle|vestigant_.*coverage|parser_coverage|source_coverage|parser_errors|event_summary|metadata_fallback|distinct_source|case_source_manifest|run_status|last_stage|audit.*\.log$|ingest.*\.log$|triage.*\.log$|.*summary.*\.(txt|json|csv)$|.*manifest.*\.(txt|json|csv)$|.*coverage.*\.(txt|json|csv)$|.*status.*\.(txt|json|csv)$)') { return $true }
    return $false
}

function Copy-ValidationBundleFile {
    param([string]$Path, [string]$SubDir = "validation_bundle", [switch]$Explicit)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (Test-ContainsPlaceholder $Path) {
        if ($RequireValidationBundle -or $Explicit) { throw "ValidationBundleZip still contains an example placeholder: $Path" }
        Write-Warning "ValidationBundleZip contains an example placeholder and will be skipped: $Path"
        return $false
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ($RequireValidationBundle -or $Explicit) { throw "ValidationBundleZip does not exist: $Path" }
        return $false
    }
    $validation = Get-ValidationBundleValidation -Path $Path
    if (-not $validation.IsValid) {
        $message = "Invalid validation bundle ZIP '$Path': $($validation.Reason)"
        if ($RequireValidationBundle -or $Explicit) { throw $message }
        Write-Warning "$message. Skipping."
        return $false
    }
    $destDir = Join-Path $Stage $SubDir
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    $name = Split-Path -Leaf $Path
    $dest = Join-Path $destDir $name
    Copy-Item -LiteralPath $Path -Destination $dest -Force
    $script:ValidationBundleIncluded = $true
    $script:ValidationBundleSource = $Path
    $script:ValidationBundleFiles.Add((Join-Path $SubDir $name)) | Out-Null
    return $true
}

function Copy-ValidationBundleCandidates {
    param([string]$Root, [string]$SubDir = "validation_bundle")
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $false }
    $candidates = Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -lt 250MB -and $_.Name -match '(?i)(validation.*bundle|bundle.*validation|VestigantCase_validation_bundle).*\.zip$' -and $_.FullName -notmatch '(?i)[\\/]upload_bundle_fixture[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending
    foreach ($candidate in $candidates) {
        if (Copy-ValidationBundleFile -Path $candidate.FullName -SubDir $SubDir) { return $true }
    }
    return $false
}

function Test-CaseHasValidValidationBundle {
    param([string]$Root)
    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) { return $false }
    $candidates = Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -lt 250MB -and $_.Name -match '(?i)(validation.*bundle|bundle.*validation|VestigantCase_validation_bundle).*\.zip$' -and $_.FullName -notmatch '(?i)[\\/]upload_bundle_fixture[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending
    foreach ($candidate in $candidates) {
        if ((Get-ValidationBundleValidation -Path $candidate.FullName).IsValid) { return $true }
    }
    return $false
}

function Find-LatestTriageCaseDirectory {
    foreach ($root in @("Q:\TriageCase", "Q:\TriageCases", "Q:\VestigantTriage", "Q:\VestigantTriageCases")) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $dirs = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending
        $ranked = foreach ($dir in $dirs) {
            $hasValidValidation = Test-CaseHasValidValidationBundle -Root $dir.FullName
            $uploadPath = Join-Path $dir.FullName "Upload"
            $hasUploadFolder = Test-Path -LiteralPath $uploadPath -PathType Container -ErrorAction SilentlyContinue
            $hasRelevantCaseFile = $false
            if (-not $hasUploadFolder) {
                $matched = Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '(?i)(case\.json|case_summary|triage|ingest|coverage|\.db$|\.sqlite$)' -and $_.FullName -notmatch '(?i)[\\/]upload_bundle_fixture[\\/]' } |
                    Select-Object -First 1
                $hasRelevantCaseFile = [bool]$matched
            }
            if ($RequireValidationBundle -and -not $hasValidValidation) { continue }
            if ($hasValidValidation -or $hasUploadFolder -or $hasRelevantCaseFile) {
                $score = 0
                if ($hasValidValidation) { $score += 100 }
                if ($hasUploadFolder) { $score += 20 }
                if ($hasRelevantCaseFile) { $score += 5 }
                New-Object PSObject -Property @{ FullName = $dir.FullName; Score = $score; LastWriteTimeUtc = $dir.LastWriteTimeUtc }
            }
        }
        $selected = $ranked | Sort-Object Score, LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($selected) { return $selected.FullName }
    }
    return $null
}

function Resolve-OptionalCaseDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        if ($RequireCaseRoot) { throw "CaseRoot is required but was not provided." }
        $autoCase = Find-LatestTriageCaseDirectory
        if ($autoCase) { Write-Warning "No CaseRoot supplied. Auto-detected latest case folder: $autoCase"; return (Resolve-Path -LiteralPath $autoCase -ErrorAction Stop).Path }
        Write-Warning "No CaseRoot supplied and no recent case folder was auto-detected. Creating a build/project validation upload bundle only."
        return $null
    }
    if (Test-ContainsPlaceholder $Path) {
        $message = "CaseRoot still contains an example placeholder: $Path. Replace <CaseName> with the real case folder path to include case outputs."
        if ($RequireCaseRoot) { throw $message }
        $autoCase = Find-LatestTriageCaseDirectory
        if ($autoCase) { Write-Warning "$message Auto-detected latest case folder instead: $autoCase"; return (Resolve-Path -LiteralPath $autoCase -ErrorAction Stop).Path }
        Write-Warning "$message Case outputs will be skipped for this bundle."
        return $null
    }
    try { return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path }
    catch {
        $message = "CaseRoot does not exist or cannot be resolved: $Path"
        if ($RequireCaseRoot) { throw $message }
        $autoCase = Find-LatestTriageCaseDirectory
        if ($autoCase) { Write-Warning "$message. Auto-detected latest case folder instead: $autoCase"; return (Resolve-Path -LiteralPath $autoCase -ErrorAction Stop).Path }
        Write-Warning "$message. Case outputs will be skipped for this bundle."
        return $null
    }
}

$ProjectRoot = Resolve-RequiredDirectory -Path $ProjectRoot -Label "ProjectRoot"
$ResolvedCaseRoot = Resolve-OptionalCaseDirectory -Path $CaseRoot
$OutZip = Resolve-OutputZipPath -Path $OutZip

$Stage = Join-Path $env:TEMP "VestigantTriageUpload_v$Version`_$Stamp"
if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

foreach ($name in @("ai_context.md", "PROJECT_ROADMAP_AND_CONTINUATION.md", "CHANGELOG.md", "BUILD_NOTES.md", "VALIDATION_STATUS.md", "README.md", "RUN_GOOGLE_SOURCE_TRIAGE.ps1", "RUN_GOOGLE_THIN_TEST_V3_7_0.ps1")) { Copy-IfExists (Join-Path $ProjectRoot $name) "project_docs" }
Copy-TreeFiltered -Root (Join-Path $ProjectRoot "docs") -SubDir "project_docs\docs" -Predicate { param($f,$r) $f.Name -match '(?i)\.(md|txt)$' -and $f.Length -lt 10MB }
Copy-TreeFiltered -Root $ProjectRoot -SubDir "project_review" -Predicate ${function:Is-ProjectReviewFile}

if (-not [string]::IsNullOrWhiteSpace($ValidationBundleZip)) { Copy-ValidationBundleFile -Path $ValidationBundleZip -Explicit | Out-Null }
if (-not $script:ValidationBundleIncluded -and $ResolvedCaseRoot) { Copy-ValidationBundleCandidates -Root $ResolvedCaseRoot | Out-Null }

if ($RequireValidationBundle -and -not $script:ValidationBundleIncluded) {
    throw "No valid generated validation bundle ZIP was included. The bundle must be a readable ZIP containing vestigant_source_coverage.csv, vestigant_parser_coverage.csv, vestigant_parser_errors.csv, vestigant_event_summary_no_ual.csv, vestigant_metadata_fallback_sources.csv, vestigant_distinct_source_files_no_ual.csv, vestigant_case_source_manifest.json, and README_validation_bundle.txt. Run ingest, let the app auto-export the validation bundle under the case Upload folder, or pass -ValidationBundleZip <path>."
}

if ($ResolvedCaseRoot) { Copy-TreeFiltered -Root $ResolvedCaseRoot -SubDir "case_review" -Predicate ${function:Is-CaseReviewFile} }

$readme = @(
    "Vestigant Triage v3.7.0 Upload Bundle",
    "Generated: $(Get-Date -Format o)",
    "ProjectRoot: $ProjectRoot",
    "Requested CaseRoot: $CaseRoot",
    "Resolved CaseRoot: $ResolvedCaseRoot",
    "Case review outputs included: $([bool]$ResolvedCaseRoot)",
    "ValidationBundleZip parameter: $ValidationBundleZip",
    "Validation bundle included: $script:ValidationBundleIncluded",
    "Validation bundle source: $script:ValidationBundleSource",
    "Validation bundle files: $($script:ValidationBundleFiles -join '; ')",
    "IncludeDatabase: $IncludeDatabase",
    "",
    "This bundle intentionally excludes large SQLite databases and staged source artifacts unless -IncludeDatabase is specified.",
    "A validation bundle must be a real generated ZIP with the expected Vestigant validation CSV/JSON files; build-fixture sentinel files are rejected.",
    "Use -RequireValidationBundle for next-version review uploads."
)
Set-Content -LiteralPath (Join-Path $Stage "UPLOAD_README.txt") -Value $readme -Encoding UTF8

$manifest = @(
    "ValidationBundleIncluded=$script:ValidationBundleIncluded",
    "ValidationBundleSource=$script:ValidationBundleSource",
    "ValidationBundleFiles=$($script:ValidationBundleFiles -join '; ')",
    "CaseRoot=$ResolvedCaseRoot"
)
Set-Content -LiteralPath (Join-Path $Stage "UPLOAD_MANIFEST.txt") -Value $manifest -Encoding UTF8

if (Test-Path -LiteralPath $OutZip) { Remove-Item -LiteralPath $OutZip -Force }
$OutZipFull = [System.IO.Path]::GetFullPath($OutZip)
$OutZipParent = [System.IO.Path]::GetDirectoryName($OutZipFull)
if (-not [string]::IsNullOrWhiteSpace($OutZipParent) -and -not (Test-Path -LiteralPath $OutZipParent -PathType Container)) { New-Item -ItemType Directory -Force -Path $OutZipParent | Out-Null }
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $OutZipFull, [System.IO.Compression.CompressionLevel]::Optimal, $false)
} catch { throw "Failed to create upload ZIP '$OutZipFull' from staging folder '$Stage': $($_.Exception.Message)" }
if (-not (Test-Path -LiteralPath $OutZipFull -PathType Leaf)) { throw "ZIP creation reported no error, but output ZIP was not created: $OutZipFull" }
$OutZip = $OutZipFull
Write-Host "Upload bundle written: $OutZip"
Write-Host "Staging folder: $Stage"
Write-Host "Validation bundle included: $script:ValidationBundleIncluded"
if ($script:ValidationBundleIncluded) { Write-Host "Validation bundle source: $script:ValidationBundleSource" }
if (-not $ResolvedCaseRoot) { Write-Host "Case review outputs were not included. Re-run with a real -CaseRoot path after a case run to include validation exports/logs." }
