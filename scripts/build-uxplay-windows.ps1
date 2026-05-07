param(
    [switch]$InstallDependencies,
    [switch]$InstallBonjour
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$uxplayRoot = Join-Path $root "third_party\UxPlay"
$bash = "C:\msys64\usr\bin\bash.exe"

if ($InstallBonjour) {
    $bonjourDll = @(
        "$env:ProgramFiles\Bonjour\dnssd.dll",
        "${env:ProgramFiles(x86)}\Bonjour\dnssd.dll",
        "$env:SystemRoot\System32\dnssd.dll"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $bonjourDll) {
        winget install --id Apple.Bonjour --exact --accept-package-agreements --accept-source-agreements
    }
}

if (-not (Test-Path $bash)) {
    if (-not $InstallDependencies) {
        throw "MSYS2 was not found at C:\msys64. Re-run with -InstallDependencies to install it with winget."
    }

    winget install --id MSYS2.MSYS2 --exact --accept-package-agreements --accept-source-agreements
}

if (-not (Test-Path $bash)) {
    throw "MSYS2 is still not available at $bash. Open a new terminal after installation and rerun this script."
}

if ($InstallDependencies) {
    & $bash -lc "pacman --noconfirm -Syuu"
    & $bash -lc "pacman --noconfirm -S mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad mingw-w64-ucrt-x86_64-gst-libav"
}

$uxplayUnix = ($uxplayRoot -replace "\\", "/")
if ($uxplayUnix -match "^([A-Za-z]):") {
    $drive = $Matches[1].ToLowerInvariant()
    $uxplayUnix = "/$drive$($uxplayUnix.Substring(2))"
}
$ucrtPrefix = "export PATH=/ucrt64/bin:`$PATH"
& $bash -lc "$ucrtPrefix && cd '$uxplayUnix' && cmake -S . -B build-ucrt64 -G Ninja -DNO_MARCH_NATIVE=ON"
& $bash -lc "$ucrtPrefix && cd '$uxplayUnix' && cmake --build build-ucrt64"

$exe = Join-Path $uxplayRoot "build-ucrt64\uxplay.exe"
if (-not (Test-Path $exe)) {
    throw "UxPlay build completed without producing $exe"
}

$bundledDir = Join-Path $root "src\AirMirror\tools\uxplay"
New-Item -ItemType Directory -Force $bundledDir | Out-Null
Copy-Item -Force $exe (Join-Path $bundledDir "uxplay.exe")

Write-Host "Built $exe"
