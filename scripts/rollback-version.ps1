# Point `current` back at previous.version after a bad update.
param(
    [string]$InstallRoot = $(if ($env:EGYCHINA_POS_INSTALL_ROOT) { $env:EGYCHINA_POS_INSTALL_ROOT } else { "C:\EgyChina\Pos" }),
    [string]$ToVersion
)

$ErrorActionPreference = "Stop"

if (Get-Process -Name "EgyChina.Pos.App" -ErrorAction SilentlyContinue) {
    throw "أغلق برنامج نقطة البيع قبل التراجع عن التحديث."
}

$previousFile = Join-Path $InstallRoot "previous.version"
if (-not $ToVersion) {
    if (-not (Test-Path $previousFile)) {
        throw "previous.version missing — pass -ToVersion explicitly."
    }
    $ToVersion = (Get-Content -Raw $previousFile).Trim()
}

$dest = Join-Path $InstallRoot "app\$ToVersion"
$exe = Join-Path $dest "EgyChina.Pos.App.exe"
if (-not (Test-Path $exe)) {
    throw "Version folder incomplete: $dest"
}

$current = Join-Path $InstallRoot "current"
$failedVersion = $null
if (Test-Path $current) {
    try {
        $resolved = (Get-Item $current).Target
        if ($resolved) { $failedVersion = Split-Path -Leaf ([string]$resolved) }
        cmd.exe /c "rmdir `"$current`"" | Out-Null
    } catch {
        Remove-Item -Force $current -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Junction -Path $current -Target $dest | Out-Null
if ($failedVersion -and $failedVersion -ne $ToVersion) {
    Set-Content -Path $previousFile -Value $failedVersion -NoNewline
}

Write-Host "Rolled back current -> $dest"
Write-Host "Start with: .\scripts\launch-pos.ps1"
