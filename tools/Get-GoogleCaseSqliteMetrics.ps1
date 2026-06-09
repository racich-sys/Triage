param(
    [Parameter(Mandatory=$true)]
    [string]$CaseRoot,
    [string]$SqliteExe = "sqlite3"
)

$ErrorActionPreference = "Stop"
$dbCandidates = @(
    (Join-Path $CaseRoot "case.db"),
    (Join-Path $CaseRoot "VestigantTriage.db"),
    (Join-Path $CaseRoot "triage.db")
)
$db = $dbCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $db) {
    $db = Get-ChildItem -LiteralPath $CaseRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '(^case\.db$|\.sqlite$|\.sqlite3$|\.db$)' } |
        Sort-Object Length -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $db) { throw "No SQLite case database found under: $CaseRoot" }

Write-Host "SQLite metrics for: $db"
Write-Host ""

$sql = @"
.headers on
.mode box
SELECT 'events' AS metric, COUNT(*) AS value FROM events;
SELECT 'event_fields' AS metric, COUNT(*) AS value FROM event_fields;
SELECT 'google_event_raw_fields_exists' AS metric, COUNT(*) AS value FROM sqlite_schema WHERE type='table' AND name='google_event_raw_fields';
SELECT 'ix_event_fields_value_exists' AS metric, COUNT(*) AS value FROM sqlite_schema WHERE type='index' AND name='ix_event_fields_value';
SELECT 'ix_event_fields_name_value_exists' AS metric, COUNT(*) AS value FROM sqlite_schema WHERE type='index' AND name='ix_event_fields_name_value';
SELECT
  CASE
    WHEN field_name LIKE 'GoogleAuditRaw_%' THEN 'GoogleAuditRaw'
    WHEN field_name LIKE 'GoogleTakeoutRaw_%' THEN 'GoogleTakeoutRaw'
    WHEN field_name LIKE 'Google%' THEN 'GoogleCanonical'
    ELSE 'Other'
  END AS field_group,
  COUNT(*) AS row_count
FROM event_fields
GROUP BY field_group
ORDER BY row_count DESC;
SELECT field_name, COUNT(*) AS row_count
FROM event_fields
GROUP BY field_name
ORDER BY row_count DESC
LIMIT 80;
SELECT event_id, COUNT(*) AS fields_per_event
FROM event_fields
GROUP BY event_id
ORDER BY fields_per_event DESC
LIMIT 30;
PRAGMA page_count;
PRAGMA page_size;
PRAGMA freelist_count;
SELECT name, SUM(pgsize) AS bytes
FROM dbstat
GROUP BY name
ORDER BY bytes DESC
LIMIT 40;
"@

$tmp = [System.IO.Path]::GetTempFileName()
try {
    Set-Content -LiteralPath $tmp -Value $sql -Encoding ASCII
    & $SqliteExe $db ".read $tmp"
    if ($LASTEXITCODE -ne 0) { throw "sqlite3 exited with code $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
}
