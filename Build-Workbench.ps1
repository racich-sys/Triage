param(
    [switch]$Publish,
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$log = "build_$stamp.log"

function Run-Step($label, $command) {
    "=== $label ===" | Tee-Object -FilePath $log -Append
    cmd /c $command 2>&1 | Tee-Object -FilePath $log -Append

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "FAILED: $label"
        Write-Host "See log: $log"
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path ".\VestigantTriage.csproj")) {
    Write-Host "Run this script from the project folder containing VestigantTriage.csproj."
    exit 1
}

# REBRANDED TARGET
$projectName = "VestigantTriage"
$buildDir = Join-Path $PWD "bin\$Configuration\net8.0-windows"
$buildExe = Join-Path $buildDir "$projectName.exe"
$publishDir = Join-Path $PWD "bin\$Configuration\net8.0-windows\win-x64\publish"
$publishExe = Join-Path $publishDir "$projectName.exe"

"=== Build started $(Get-Date) ===" | Out-File -FilePath $log -Encoding utf8

Run-Step "dotnet restore" "dotnet restore"
Run-Step "dotnet build ($Configuration)" "dotnet build -c $Configuration"

Write-Host ""
if (Test-Path $buildExe) {
    Write-Host "Build EXE:"
    Write-Host $buildExe
} else {
    Write-Host "Build completed, but EXE not found at expected path:"
    Write-Host $buildExe
}

if ($Publish) {
    Run-Step "dotnet restore (win-x64)" "dotnet restore -r win-x64"
    Run-Step "dotnet publish ($Configuration)" "dotnet publish -c $Configuration -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false --no-restore"

    Write-Host ""
    if (Test-Path $publishExe) {
        Write-Host "Published standalone EXE:"
        Write-Host $publishExe
    } else {
        Write-Host "Publish completed, but EXE not found at expected path:"
        Write-Host $publishExe
    }
}

Write-Host ""
Write-Host "Log file:"
Write-Host (Join-Path $PWD $log)