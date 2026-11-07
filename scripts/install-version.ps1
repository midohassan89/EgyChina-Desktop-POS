# Install (or update) a published till build under C:\EgyChina\Pos\app\<version>
# and retarget the `current` junction. Refuses while POS is running or a shift is open.
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [string]$InstallRoot = $(if ($env:EGYCHINA_POS_INSTALL_ROOT) { $env:EGYCHINA_POS_INSTALL_ROOT } else { "C:\EgyChina\Pos" }),

    [string]$Version,

    [string]$DataRoot = $(Join-Path $env:LOCALAPPDATA "EgyChina\Pos"),

    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Test-PosRunning {
    return $null -ne (Get-Process -Name "EgyChina.Pos.App" -ErrorAction SilentlyContinue)
}

function Test-OpenShift([string]$DbPath) {
    if (-not (Test-Path $DbPath)) { return $false }
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $toolsProject = Join-Path $repoRoot "src\EgyChina.Pos.Tools\EgyChina.Pos.Tools.csproj"
    & dotnet run --project $toolsProject -c Release --no-build -- has-open-shift $DbPath 2>$null
    if ($LASTEXITCODE -eq 10) { return $true }
    if ($LASTEXITCODE -eq 0) { return $false }
    & dotnet build $toolsProject -c Release --nologo -v q | Out-Null
    & dotnet run --project $toolsProject -c Release --no-build -- has-open-shift $DbPath
    return ($LASTEXITCODE -eq 10)
}

function Set-CurrentJunction([string]$Root, [string]$TargetDir) {
    $current = Join-Path $Root "current"
    if (Test-Path $current) {
        cmd.exe /c "rmdir `"$current`"" | Out-Null
    }
    New-Item -ItemType Junction -Path $current -Target $TargetDir | Out-Null
}

if (-not (Test-Path $Source)) {
    throw "Source folder not found: $Source"
}
$exe = Join-Path $Source "EgyChina.Pos.App.exe"
if (-not (Test-Path $exe)) {
    throw "EgyChina.Pos.App.exe missing in $Source"
}

if (-not $Version) {
    $Version = Split-Path -Leaf (Resolve-Path $Source)
}

if (Test-PosRunning) {
    throw "أغلق برنامج نقطة البيع قبل التحديث."
}

$db = Join-Path $DataRoot "pos.db"
if (-not $Force -and (Test-OpenShift $db)) {
    throw "أغلق الوردية المفتوحة قبل التحديث."
}

$appRoot = Join-Path $InstallRoot "app"
$dest = Join-Path $appRoot $Version
New-Item -ItemType Directory -Force -Path $appRoot | Out-Null

$previousFile = Join-Path $InstallRoot "previous.version"
$current = Join-Path $InstallRoot "current"
$previousVersion = $null
if (Test-Path $current) {
    try {
        $resolved = (Get-Item $current).Target
        if ($resolved) {
            $previousVersion = Split-Path -Leaf ([string]$resolved)
        }
    } catch { }
}

if (Test-Path $dest) {
    Remove-Item -Recurse -Force $dest
}
New-Item -ItemType Directory -Path $dest | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $dest -Recurse -Force

if ($previousVersion -and $previousVersion -ne $Version) {
    Set-Content -Path $previousFile -Value $previousVersion -NoNewline
}

Set-CurrentJunction -Root $InstallRoot -TargetDir $dest
Write-Host "Installed $Version"
Write-Host "current -> $dest"
if ($previousVersion) {
    Write-Host "previous.version = $previousVersion"
}
Write-Host "Start with: .\scripts\launch-pos.ps1"
