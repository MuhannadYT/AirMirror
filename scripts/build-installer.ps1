<#
.SYNOPSIS
    Publishes AirMirror and produces dist\AirMirror-Setup-<version>-<arch>.exe via Inno Setup.

.PARAMETER Arch
    Target CPU architecture: x64 (default), arm64, or both.

.PARAMETER SkipPublish
    Skip dotnet publish (assume publish output is already up to date).

.PARAMETER SkipUxPlay
    Don't try to build UxPlay; assume src\AirMirror\tools\uxplay\uxplay.exe is already present.

.PREREQUISITES
    - .NET 8 SDK
    - Inno Setup 6 (ISCC.exe). Auto-discovered, or set $env:ISCC.
    - For UxPlay: MSYS2 UCRT64 (only when building uxplay.exe; arm64 builds skip this).
#>

[CmdletBinding()]
param(
    [ValidateSet('x64','arm64','both')]
    [string]$Arch = 'x64',
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [switch]$SkipUxPlay
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

# --- 1. locate ISCC -----------------------------------------------------------
$iscc = $env:ISCC
if (-not $iscc) {
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $iscc = $c; break } }
}
if (-not $iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc -or -not (Test-Path $iscc)) {
    Write-Host ''
    Write-Host 'ERROR: Inno Setup 6 (ISCC.exe) was not found.' -ForegroundColor Red
    Write-Host 'Install it with:  winget install --id JRSoftware.InnoSetup' -ForegroundColor Yellow
    Write-Host '   or download:  https://jrsoftware.org/isinfo.php' -ForegroundColor Yellow
    Write-Host 'Then re-run this script (or set $env:ISCC to the full ISCC.exe path).' -ForegroundColor Yellow
    throw 'ISCC not found.'
}
Write-Host "Using ISCC: $iscc" -ForegroundColor DarkGray

# --- 2. ensure uxplay.exe is staged ------------------------------------------
$uxplayStaged = Join-Path $root 'src\AirMirror\tools\uxplay\uxplay.exe'
$uxplayBuilt  = Join-Path $root 'third_party\UxPlay\build-ucrt64\uxplay.exe'

if (-not (Test-Path $uxplayStaged) -and -not $SkipUxPlay) {
    if (Test-Path $uxplayBuilt) {
        New-Item -ItemType Directory -Force (Split-Path $uxplayStaged) | Out-Null
        Copy-Item -Force $uxplayBuilt $uxplayStaged
        Write-Host "Staged uxplay.exe from third_party build." -ForegroundColor DarkGray
    } else {
        Write-Host 'uxplay.exe not found. Trying to build it via build-uxplay-windows.ps1...' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'build-uxplay-windows.ps1')
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $uxplayStaged)) {
            throw "Could not produce $uxplayStaged. Build UxPlay manually (see README) or pass -SkipUxPlay."
        }
    }
}
if (-not (Test-Path $uxplayStaged)) {
    Write-Warning "uxplay.exe is missing at $uxplayStaged. The installer will still build but the receiver won't run on the target machine."
} elseif (-not $SkipUxPlay) {
    # Ensure all GStreamer/MSYS2 runtime DLLs and plugins are bundled next to
    # uxplay.exe so the installer ships a self-contained UxPlay.
    $stagedPlugins = Join-Path $root 'src\AirMirror\tools\uxplay\lib\gstreamer-1.0'
    if (-not (Test-Path $stagedPlugins)) {
        Write-Host "==> Staging UxPlay runtime DLLs + GStreamer plugins..." -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'stage-uxplay-deps.ps1')
        if ($LASTEXITCODE -ne 0) {
            throw "stage-uxplay-deps.ps1 failed. The installer would not include the runtime libraries that uxplay.exe needs."
        }
    } else {
        Write-Host "UxPlay runtime libraries already staged." -ForegroundColor DarkGray
    }
}

# --- 3. build per-arch --------------------------------------------------------
$targets = if ($Arch -eq 'both') { @('x64','arm64') } else { @($Arch) }

foreach ($a in $targets) {
    $rid = "win-$a"
    $publishDir = Join-Path $root "src\AirMirror\bin\$Configuration\net8.0-windows10.0.19041.0\$rid\publish"

    if (-not $SkipPublish) {
        Write-Host ''
        Write-Host "==> Publishing AirMirror ($rid)..." -ForegroundColor Cyan
        dotnet publish (Join-Path $root 'src\AirMirror\AirMirror.csproj') `
            -c $Configuration `
            -r $rid `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -nologo
        if ($LASTEXITCODE -ne 0) {
            if ($a -eq 'arm64') {
                Write-Warning "ARM64 publish failed. LibVLC.Windows may not ship arm64 native libs in this version. Skipping arm64 installer."
                continue
            }
            throw "dotnet publish failed for $rid"
        }
    }

    if (-not (Test-Path $publishDir)) {
        Write-Warning "Publish directory missing for $rid -> $publishDir. Skipping."
        continue
    }

    Write-Host "==> Compiling installer for $a..." -ForegroundColor Cyan
    & $iscc "/DArch=$a" (Join-Path $root 'installer\AirMirror.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $a" }
}

Write-Host ''
Write-Host '==> Done. Installers in dist\:' -ForegroundColor Green
Get-ChildItem (Join-Path $root 'dist') -Filter '*.exe' -ErrorAction SilentlyContinue |
    Format-Table Name, @{N='Size (MB)';E={[math]::Round($_.Length/1MB,2)}}, LastWriteTime
