# Publish the cashier shell for Windows tills.
# Run this on Windows (or a Windows CI agent). ReadyToRun cross-publish from Linux is not the supported path.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\EgyChina.Pos.App\EgyChina.Pos.App.csproj"
$out = Join-Path $root "artifacts\win-x64"

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -o $out

Write-Host "Published to $out"
