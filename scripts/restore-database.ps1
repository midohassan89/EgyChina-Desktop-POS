# Restore pos.db from a Phase-9 backup while the POS is stopped.
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [string]$DataRoot = $(Join-Path $env:LOCALAPPDATA "EgyChina\Pos"),

    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (Get-Process -Name "EgyChina.Pos.App" -ErrorAction SilentlyContinue) {
    throw "أغلق برنامج نقطة البيع قبل استرجاع قاعدة البيانات."
}

if (-not (Test-Path $BackupPath)) {
    throw "Backup not found: $BackupPath"
}

$live = Join-Path $DataRoot "pos.db"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsProject = Join-Path $repoRoot "src\EgyChina.Pos.Tools\EgyChina.Pos.Tools.csproj"
$forceArgs = @()
if ($Force) { $forceArgs = @("--force") }

& dotnet run --project $toolsProject -c Release -- restore $BackupPath $live @forceArgs
if ($LASTEXITCODE -ne 0) {
    throw "Database restore failed."
}
Write-Host "Restored $BackupPath -> $live"
