# Copy a published build into the versioned install, switch `current`, start POS,
# and roll back if the new version does not write a fresh heartbeat.
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [string]$InstallRoot = $(if ($env:EGYCHINA_POS_INSTALL_ROOT) { $env:EGYCHINA_POS_INSTALL_ROOT } else { "C:\EgyChina\Pos" }),

    [string]$Version,

    [string]$DataRoot = $(Join-Path $env:LOCALAPPDATA "EgyChina\Pos"),

    [int]$HeartbeatSeconds = 90,

    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$toolsProject = Join-Path $repoRoot "src\EgyChina.Pos.Tools\EgyChina.Pos.Tools.csproj"

if (-not $Version) {
    $Version = Split-Path -Leaf (Resolve-Path $Source)
}

& (Join-Path $scriptDir "install-version.ps1") -Source $Source -InstallRoot $InstallRoot -Version $Version -DataRoot $DataRoot

if ($SkipLaunch) {
    Write-Host "Installed $Version (launch skipped)."
    exit 0
}

& dotnet build $toolsProject -c Release --nologo -v q | Out-Null
& (Join-Path $scriptDir "launch-pos.ps1") -InstallRoot $InstallRoot

$deadline = (Get-Date).AddSeconds($HeartbeatSeconds)
$ok = $false
while ((Get-Date) -lt $deadline) {
    & dotnet run --project $toolsProject -c Release --no-build -- heartbeat-check $DataRoot $Version $HeartbeatSeconds
    if ($LASTEXITCODE -eq 0) {
        $ok = $true
        break
    }
    Start-Sleep -Seconds 2
}

if (-not $ok) {
    Write-Warning "Heartbeat for $Version not seen within ${HeartbeatSeconds}s — rolling back."
    Get-Process -Name "EgyChina.Pos.App" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    & (Join-Path $scriptDir "rollback-version.ps1") -InstallRoot $InstallRoot
    & (Join-Path $scriptDir "launch-pos.ps1") -InstallRoot $InstallRoot
    throw "Update rolled back: new build did not heartbeat."
}

Write-Host "Update to $Version confirmed via heartbeat."
