# Wipe local till data so the cashier can re-sync a clean catalog from ERP.
# Does NOT delete the installed program folder — only %LOCALAPPDATA%\EgyChina\Pos data.
# Run as the Windows user that uses the POS. Close the app first.
$ErrorActionPreference = "Stop"

$root = Join-Path $env:LOCALAPPDATA "EgyChina\Pos"
if (-not (Test-Path $root)) {
    Write-Host "No till data folder found at $root — nothing to clean."
    exit 0
}

$procs = Get-Process -Name "EgyChina.Pos.App" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Stopping EgyChina.Pos.App..."
    $procs | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path ([Environment]::GetFolderPath("Desktop")) "EgyChina-Pos-backup-$stamp"
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -Path (Join-Path $root "*") -Destination $backup -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Backup copied to: $backup"

# Keep logs + assets; remove DB / device token / WAL so sync starts empty.
@(
    "pos.db",
    "pos.db-wal",
    "pos.db-shm",
    "pos.db-journal",
    "device.cred"
) | ForEach-Object {
    $path = Join-Path $root $_
    if (Test-Path $path) {
        Remove-Item -Force $path
        Write-Host "Removed $_"
    }
}

Write-Host ""
Write-Host "Done. Start the POS again — it will create a fresh pos.db and pull catalog/staff from ERP."
Write-Host "Problems log (keep for support): $(Join-Path $root 'logs\problems.log')"
Write-Host "Full log: $(Join-Path $root 'logs\pos.log')"
