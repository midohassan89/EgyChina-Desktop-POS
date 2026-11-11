# Build a Velopack one-click installer (Setup.exe) for EgyChina Desktop POS.
# Self-contained win-x64 — tills do not need a separate .NET runtime.
# Data stays in %LOCALAPPDATA%\EgyChina\Pos and is never packed into the installer.
#
# Examples:
#   .\scripts\build-setup.ps1
#   .\scripts\build-setup.ps1 -Channel beta
#   .\scripts\build-setup.ps1 -Channel stable -PackVersion 0.3.22 -SkipDownload
param(
    [ValidateSet("stable", "beta")]
    [string]$Channel = "stable",

    [string]$PackVersion,

    [string]$FeedUrl,

    [switch]$SkipDownload,

    [string]$OutputSetup
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\EgyChina.Pos.App\EgyChina.Pos.App.csproj"
$iconPath = Join-Path $root "assets\EgyChina.ico"
$publishDir = Join-Path $root "artifacts\velopack\publish"
$releasesDir = Join-Path $root "artifacts\velopack\releases"
$setupOut = if ($OutputSetup) { $OutputSetup } else { Join-Path $root "artifacts\Setup.exe" }
$packId = "EgyChinaPOS"
$packTitle = "EgyChina POS"
$mainExe = "EgyChina.Pos.App.exe"
$velopackPkgVersion = "0.0.1298"
$defaultFeedBase = "https://updates.echinamart.com"

function Ensure-EgyChinaIcon([string]$Path) {
    if ((Test-Path $Path) -and ((Get-Item $Path).Length -gt 0)) {
        Write-Host "Icon OK: $Path"
        return
    }

    Write-Host "Generating temporary brand icon: $Path"
    $code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

public static class EgyChinaIconBuilder
{
    public static void Write(string path)
    {
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> pngs = new List<byte[]>();
        for (int si = 0; si < sizes.Length; si++)
        {
            int size = sizes[si];
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            Graphics g = Graphics.FromImage(bmp);
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                Color green = Color.FromArgb(255, 0x12, 0x40, 0x2c);
                Color gold = Color.FromArgb(255, 0xdc, 0xa5, 0x3a);
                float pad = Math.Max(1f, size * 0.06f);
                SolidBrush greenBrush = new SolidBrush(green);
                try
                {
                    float r = Math.Max(2f, size * 0.18f);
                    FillRoundRect(g, greenBrush, pad, pad, size - 2 * pad, size - 2 * pad, r);
                }
                finally { greenBrush.Dispose(); }
                float cx = size / 2f;
                float cy = size / 2f;
                float outer = size * 0.28f;
                float inner = size * 0.14f;
                Pen pen = new Pen(gold, Math.Max(1.5f, size * 0.08f));
                try
                {
                    pen.Alignment = PenAlignment.Center;
                    g.DrawEllipse(pen, cx - outer, cy - outer, outer * 2, outer * 2);
                }
                finally { pen.Dispose(); }
                SolidBrush goldBrush = new SolidBrush(gold);
                try
                {
                    g.FillEllipse(goldBrush, cx - inner, cy - inner, inner * 2, inner * 2);
                    float s = size * 0.16f;
                    g.FillEllipse(goldBrush, size - pad - s * 1.1f, pad + s * 0.1f, s, s);
                }
                finally { goldBrush.Dispose(); }
            }
            finally { g.Dispose(); }
            MemoryStream ms = new MemoryStream();
            try
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
            finally { ms.Dispose(); bmp.Dispose(); }
        }
        FileStream fs = File.Create(path);
        BinaryWriter bw = new BinaryWriter(fs);
        try
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)pngs.Count);
            int offset = 6 + 16 * pngs.Count;
            for (int i = 0; i < pngs.Count; i++)
            {
                int size = sizes[i];
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(pngs[i].Length);
                bw.Write(offset);
                offset += pngs[i].Length;
            }
            for (int i = 0; i < pngs.Count; i++)
                bw.Write(pngs[i]);
        }
        finally { bw.Dispose(); fs.Dispose(); }
    }

    static void FillRoundRect(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        GraphicsPath path = new GraphicsPath();
        try
        {
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
        finally { path.Dispose(); }
    }
}
'@
    Add-Type -ReferencedAssemblies @("System.Drawing") -TypeDefinition $code -ErrorAction Stop
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    [EgyChinaIconBuilder]::Write($Path)
}

function Assert-CleanPublish([string]$Dir) {
    $forbidden = @(
        "pos.db",
        "pos.db-wal",
        "pos.db-shm",
        "pos.db-journal",
        "device.cred"
    )
    $hits = Get-ChildItem -Path $Dir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $forbidden -contains $_.Name }
    if ($hits) {
        $list = ($hits | ForEach-Object { $_.FullName }) -join "`n"
        throw "Publish folder contains till data that must never ship in Setup.exe:`n$list"
    }
}

function Ensure-Vpk([string]$Version) {
    $vpkCmd = Get-Command vpk -ErrorAction SilentlyContinue
    if ($vpkCmd) {
        Write-Host "Using vpk: $($vpkCmd.Source)"
        return
    }
    Write-Host "Installing vpk $Version as a global dotnet tool..."
    & dotnet tool update -g vpk --version $Version
    if ($LASTEXITCODE -ne 0) {
        & dotnet tool install -g vpk --version $Version
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install vpk tool version $Version"
        }
    }
}

$versionNode = Select-Xml -Path $project -XPath "//Version" | Select-Object -First 1
$version = if ($PackVersion) {
    $PackVersion.Trim().TrimStart('v', 'V')
}
elseif ($versionNode) {
    $versionNode.Node.InnerText.Trim()
}
else {
    "0.0.0"
}
if ($version -notmatch '^\d+\.\d+\.\d+([\-\+][0-9A-Za-z\.\-]+)?$') {
    throw "Version '$version' is not valid for Velopack (semver required)."
}

$feed = if ($FeedUrl) {
    $FeedUrl.Trim().TrimEnd('/') + "/"
}
else {
    "$defaultFeedBase/$Channel/"
}

Ensure-EgyChinaIcon $iconPath

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $releasesDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $setupOut) | Out-Null

Write-Host "Publishing self-contained win-x64 $version ..."
& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:Version=$version `
    -p:DebugType=None `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $publishDir $mainExe
if (-not (Test-Path $exePath)) {
    throw "Publish finished but $mainExe is missing under $publishDir"
}

Assert-CleanPublish $publishDir
Ensure-Vpk $velopackPkgVersion

# vpk 0.0.1298 targets Microsoft.AspNetCore.App 9; tills/devs may only have .NET 10.
$prevRollForward = $env:DOTNET_ROLL_FORWARD
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
try {
    if (-not $SkipDownload) {
        Write-Host "Downloading previous releases for deltas from $feed ..."
        & vpk download http --outputDir $releasesDir --url $feed --channel $Channel
        if ($LASTEXITCODE -ne 0) {
            Write-Host "vpk download skipped/failed (first release or feed empty) — continuing without deltas."
        }
    }

    Write-Host "Packaging Velopack release ($packId $version channel=$Channel) ..."
    & vpk pack `
        --packId $packId `
        --packVersion $version `
        --packDir $publishDir `
        --mainExe $mainExe `
        --packTitle $packTitle `
        --packAuthors "EgyChina" `
        --icon $iconPath `
        --channel $Channel `
        --outputDir $releasesDir
    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack failed with exit code $LASTEXITCODE"
    }
}
finally {
    if ($null -eq $prevRollForward) {
        Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_ROLL_FORWARD = $prevRollForward
    }
}

$generatedSetup = Get-ChildItem -Path $releasesDir -Filter "*-Setup.exe" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $generatedSetup) {
    $generatedSetup = Get-ChildItem -Path $releasesDir -Filter "Setup.exe" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}
if (-not $generatedSetup) {
    throw "Velopack did not produce a Setup.exe under $releasesDir"
}

Copy-Item -Force $generatedSetup.FullName $setupOut

Write-Host ""
Write-Host "Done."
Write-Host "  Version : $version"
Write-Host "  Channel : $Channel"
Write-Host "  Feed    : $feed"
Write-Host "  Setup   : $setupOut"
Write-Host "  Releases: $releasesDir"
Write-Host "Install on a till by running Setup.exe. Cashier data stays in %LOCALAPPDATA%\EgyChina\Pos and is not removed on uninstall."
