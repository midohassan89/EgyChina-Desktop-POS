# Copies the till problems.log to the Desktop so it is easy to paste into chat.
$ErrorActionPreference = "Stop"
$src = Join-Path $env:LOCALAPPDATA "EgyChina\Pos\logs\problems.log"
if (-not (Test-Path $src)) {
    Write-Host "No problems.log yet at $src"
    Write-Host "Run the POS once so errors can be recorded."
    exit 1
}

$dest = Join-Path ([Environment]::GetFolderPath("Desktop")) ("EgyChina-problems-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
Copy-Item -Path $src -Destination $dest -Force
Write-Host "Copied to: $dest"
notepad $dest
