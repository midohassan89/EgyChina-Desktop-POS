# Publish the cashier shell for Windows tills into a versioned folder.
# Run this on Windows (or a Windows CI agent). ReadyToRun cross-publish from Linux is not the supported path.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\EgyChina.Pos.App\EgyChina.Pos.App.csproj"

$versionNode = Select-Xml -Path $project -XPath "//Version" | Select-Object -First 1
$version = if ($versionNode) { $versionNode.Node.InnerText.Trim() } else { "0.0.0" }

$stage = Join-Path $root "artifacts\win-x64\$version"
if (Test-Path $stage) {
    Remove-Item -Recurse -Force $stage
}
New-Item -ItemType Directory -Path $stage | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -o $stage

if (-not (Test-Path (Join-Path $stage "EgyChina.Pos.App.exe"))) {
    throw "Publish finished but EgyChina.Pos.App.exe is missing under $stage"
}

$latest = Join-Path $root "artifacts\win-x64\latest.txt"
Set-Content -Path $latest -Value $version -NoNewline
Write-Host "Published version $version to $stage"
Write-Host "Install with: .\scripts\install-version.ps1 -Source $stage"
