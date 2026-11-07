# Start the till from the `current` junction (or a specific version folder).
param(
    [string]$InstallRoot = $(if ($env:EGYCHINA_POS_INSTALL_ROOT) { $env:EGYCHINA_POS_INSTALL_ROOT } else { "C:\EgyChina\Pos" }),
    [string]$Version
)

$ErrorActionPreference = "Stop"

if ($Version) {
    $exe = Join-Path $InstallRoot "app\$Version\EgyChina.Pos.App.exe"
} else {
    $exe = Join-Path $InstallRoot "current\EgyChina.Pos.App.exe"
}

if (-not (Test-Path $exe)) {
    throw "Executable not found: $exe — run install-version.ps1 first."
}

$running = Get-Process -Name "EgyChina.Pos.App" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "EgyChina POS is already running (PID $($running.Id -join ','))."
    exit 0
}

Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
Write-Host "Started $exe"
