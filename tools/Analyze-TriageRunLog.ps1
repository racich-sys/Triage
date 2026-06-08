param(
    [Parameter(Mandatory=$true)]
    [string]$RunLogPath,
    [string]$OutDir = (Join-Path (Split-Path -Parent $RunLogPath) "ValidationOutput")
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $RunLogPath)) { throw "Run log not found: $RunLogPath" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$SummaryPath = Join-Path $OutDir "triage_runlog_analysis_$Stamp.txt"
$CsvPath = Join-Path $OutDir "triage_runlog_parser_duration_summary_$Stamp.csv"
$text = Get-Content -LiteralPath $RunLogPath -Raw
$lines = $text -split "`r?`n"

function Count-Match([string]$Pattern) {
    return ([regex]::Matches($text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
}

$processing = @{}
$durations = @()
foreach ($line in $lines) {
    if ($line -match '^\d{2}:\d{2}:\d{2}\s+-\s+\s+-\s+Processing\s+(.+?):\s+(.+?)\s+\((Live|Deleted/Recovered|Imported|[^)]*)\)') {
        $parser = $matches[1].Trim()
        if (-not $processing.ContainsKey($parser)) { $processing[$parser] = 0 }
        $processing[$parser]++
    }
    if ($line -match '^\d{2}:\d{2}:\d{2}\s+-\s+\s+✓\s+\[(.*?)\]\s+(.+?):\s+([0-9,]+)\s+events imported in\s+([0-9.]+)s') {
        $durations += [pscustomobject]@{
            Status = $matches[1]
            Parser = $matches[2].Trim()
            EventsImported = [int](($matches[3]) -replace ',', '')
            Seconds = [double]$matches[4]
        }
    }
}

$durationSummary = $durations |
    Group-Object Parser |
    ForEach-Object {
        [pscustomobject]@{
            Parser = $_.Name
            FilesCompleted = $_.Count
            EventsImported = ($_.Group | Measure-Object EventsImported -Sum).Sum
            TotalSeconds = [math]::Round((($_.Group | Measure-Object Seconds -Sum).Sum), 3)
            MaxSeconds = [math]::Round((($_.Group | Measure-Object Seconds -Maximum).Maximum), 3)
            ZeroEventFiles = ($_.Group | Where-Object { $_.EventsImported -eq 0 }).Count
        }
    } |
    Sort-Object TotalSeconds -Descending

$durationSummary | Export-Csv -LiteralPath $CsvPath -NoTypeInformation

$oneDriveSyncedDocumentScan = $lines | Where-Object { $_ -match 'Fast triage artifact discovery' -and $_ -match '\\OneDrive - [^\\]+\\Documents\\' }
$ownerMftMarkers = $lines | Where-Object { $_ -match 'MFT owner/lock' }
$smallGroupMarkers = $lines | Where-Object { $_ -match 'Parallel small artifact ingest group' }
$evtxGroupMarkers = $lines | Where-Object { $_ -match 'Parallel EVTX ingest group' }
$parserFallbacks = Count-Match 'Parser matched but emitted no events'
$oneDriveFallbacks = Count-Match 'Parser matched but emitted no events: OneDrive Artifact Parser'
$jumpListFallbacks = Count-Match 'Parser matched but emitted no events: Windows Jump Lists'
$browserFallbacks = Count-Match 'Parser matched but emitted no events: Browser History and Downloads'

$out = New-Object System.Collections.Generic.List[string]
$out.Add("# Vestigant Triage Run Log Analysis")
$out.Add("Generated: $(Get-Date -Format o)")
$out.Add("RunLogPath: $RunLogPath")
$out.Add("")
$out.Add("## Markers")
$out.Add("MFT owner/lock marker count: $($ownerMftMarkers.Count)")
$out.Add("Parallel small artifact ingest group marker count: $($smallGroupMarkers.Count)")
$out.Add("Parallel EVTX ingest group marker count: $($evtxGroupMarkers.Count)")
$out.Add("Fast triage OneDrive synced Documents progress lines: $($oneDriveSyncedDocumentScan.Count)")
$out.Add("")
$out.Add("## Fallback rows")
$out.Add("All parser no-event fallbacks: $parserFallbacks")
$out.Add("OneDrive no-event fallbacks: $oneDriveFallbacks")
$out.Add("JumpList no-event fallbacks: $jumpListFallbacks")
$out.Add("Browser no-event fallbacks: $browserFallbacks")
$out.Add("")
$out.Add("## Parser processing count")
foreach ($key in ($processing.Keys | Sort-Object)) { $out.Add("$key: $($processing[$key])") }
$out.Add("")
$out.Add("## Slowest parser families by cumulative logged seconds")
foreach ($row in ($durationSummary | Select-Object -First 20)) {
    $out.Add(("{0}: files={1}, events={2}, total_seconds={3}, max_seconds={4}, zero_event_files={5}" -f $row.Parser, $row.FilesCompleted, $row.EventsImported, $row.TotalSeconds, $row.MaxSeconds, $row.ZeroEventFiles))
}
$out.Add("")
$out.Add("CSV summary: $CsvPath")
Set-Content -LiteralPath $SummaryPath -Value $out -Encoding UTF8
Write-Host "Run-log analysis written: $SummaryPath"
Write-Host "Parser duration CSV written: $CsvPath"
