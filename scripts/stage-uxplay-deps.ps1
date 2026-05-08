<#
.SYNOPSIS
  Stage uxplay.exe and all its runtime dependencies (MSYS2/UCRT64 DLLs +
  GStreamer plugins) under src\AirMirror\tools\uxplay\ so the installer
  can ship a self-contained UxPlay that runs on machines without MSYS2.

.DESCRIPTION
  Walks DLL imports recursively starting from uxplay.exe, copying every
  DLL that lives under the MSYS2 UCRT64 prefix into the staging dir.
  Also copies the entire gstreamer-1.0 plugin tree (UxPlay loads plugins
  dynamically at runtime so we cannot infer them from imports).
#>

[CmdletBinding()]
param(
  [string] $UxPlayExe   = "$PSScriptRoot\..\third_party\UxPlay\build-ucrt64\uxplay.exe",
  [string] $Msys2Root   = "C:\msys64",
  [string] $StageDir    = "$PSScriptRoot\..\src\AirMirror\tools\uxplay"
)

$ErrorActionPreference = 'Stop'

$ucrtBin     = Join-Path $Msys2Root 'ucrt64\bin'
$ucrtLib     = Join-Path $Msys2Root 'ucrt64\lib'
$ucrtShare   = Join-Path $Msys2Root 'ucrt64\share'
$objdump     = Join-Path $ucrtBin 'objdump.exe'

if (-not (Test-Path $UxPlayExe)) { throw "uxplay.exe not found at $UxPlayExe" }
if (-not (Test-Path $objdump))   { throw "objdump.exe not found at $objdump (install mingw-w64-ucrt-x86_64-binutils)" }

New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

# --- Stage uxplay.exe itself ---
Copy-Item -Force $UxPlayExe (Join-Path $StageDir 'uxplay.exe')

# --- Recursively walk DLL imports, copying every UCRT64 DLL ---
$visited = @{}
$queue   = New-Object System.Collections.Queue
$queue.Enqueue($UxPlayExe)

function Get-DllImports([string] $path) {
  $out = & $objdump -p $path 2>$null
  $out | Where-Object { $_ -match '^\s*DLL Name:\s+(.+)$' } | ForEach-Object {
    ($_ -replace '^\s*DLL Name:\s+', '').Trim()
  }
}

while ($queue.Count -gt 0) {
  $current = $queue.Dequeue()
  $imports = Get-DllImports $current
  foreach ($dllName in $imports) {
    $key = $dllName.ToLowerInvariant()
    if ($visited.ContainsKey($key)) { continue }
    $candidate = Join-Path $ucrtBin $dllName
    if (-not (Test-Path $candidate)) {
      # Not a UCRT64 DLL (system DLL); skip.
      $visited[$key] = $false
      continue
    }
    $visited[$key] = $true
    $dest = Join-Path $StageDir $dllName
    Copy-Item -Force $candidate $dest
    $queue.Enqueue($candidate)
  }
}

$copiedDlls = ($visited.Values | Where-Object { $_ -eq $true }).Count
Write-Host "Copied $copiedDlls UCRT64 DLLs to $StageDir"

# --- Copy GStreamer plugins (loaded dynamically at runtime) ---
$gstPluginsSrc = Join-Path $ucrtLib 'gstreamer-1.0'
if (Test-Path $gstPluginsSrc) {
  $gstPluginsDst = Join-Path $StageDir 'lib\gstreamer-1.0'
  if (Test-Path $gstPluginsDst) { Remove-Item -Recurse -Force $gstPluginsDst }
  New-Item -ItemType Directory -Force -Path $gstPluginsDst | Out-Null
  # Copy *.dll plugins only, not .a/.la/dev files
  Get-ChildItem -Path $gstPluginsSrc -Filter *.dll -File | ForEach-Object {
    Copy-Item -Force $_.FullName (Join-Path $gstPluginsDst $_.Name)
  }
  $pluginCount = (Get-ChildItem $gstPluginsDst -Filter *.dll).Count
  Write-Host "Copied $pluginCount GStreamer plugins"

  # Plugins themselves have DLL deps that must also live next to uxplay.exe
  # (they import gstpbutils, gstaudio, gstvideo, gsttag, gstbase, etc.)
  Get-ChildItem $gstPluginsDst -Filter *.dll | ForEach-Object {
    $pluginImports = Get-DllImports $_.FullName
    foreach ($dllName in $pluginImports) {
      $key = $dllName.ToLowerInvariant()
      if ($visited.ContainsKey($key) -and $visited[$key]) { continue }
      $candidate = Join-Path $ucrtBin $dllName
      if (-not (Test-Path $candidate)) {
        $visited[$key] = $false
        continue
      }
      $visited[$key] = $true
      Copy-Item -Force $candidate (Join-Path $StageDir $dllName)
      # Recurse into this DLL's own imports
      $subQueue = New-Object System.Collections.Queue
      $subQueue.Enqueue($candidate)
      while ($subQueue.Count -gt 0) {
        $cur = $subQueue.Dequeue()
        foreach ($sub in (Get-DllImports $cur)) {
          $subKey = $sub.ToLowerInvariant()
          if ($visited.ContainsKey($subKey) -and $visited[$subKey]) { continue }
          $subCand = Join-Path $ucrtBin $sub
          if (-not (Test-Path $subCand)) { $visited[$subKey] = $false; continue }
          $visited[$subKey] = $true
          Copy-Item -Force $subCand (Join-Path $StageDir $sub)
          $subQueue.Enqueue($subCand)
        }
      }
    }
  }
  $totalDlls = ($visited.Values | Where-Object { $_ -eq $true }).Count
  Write-Host "Total UCRT64 DLLs after plugin walk: $totalDlls"
}
else {
  Write-Warning "GStreamer plugin dir not found: $gstPluginsSrc"
}

# --- Copy GIO modules (some GStreamer elements need them) ---
$gioModSrc = Join-Path $ucrtLib 'gio\modules'
if (Test-Path $gioModSrc) {
  $gioModDst = Join-Path $StageDir 'lib\gio\modules'
  if (Test-Path $gioModDst) { Remove-Item -Recurse -Force $gioModDst }
  New-Item -ItemType Directory -Force -Path $gioModDst | Out-Null
  Get-ChildItem -Path $gioModSrc -Filter *.dll -File -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -Force $_.FullName (Join-Path $gioModDst $_.Name)
  }
  # Walk imports of every GIO module too (e.g. libgiolibproxy.dll needs libproxy.dll).
  Get-ChildItem $gioModDst -Filter *.dll | ForEach-Object {
    $gioImports = Get-DllImports $_.FullName
    foreach ($dllName in $gioImports) {
      $key = $dllName.ToLowerInvariant()
      if ($visited.ContainsKey($key) -and $visited[$key]) { continue }
      $candidate = Join-Path $ucrtBin $dllName
      if (-not (Test-Path $candidate)) { $visited[$key] = $false; continue }
      $visited[$key] = $true
      Copy-Item -Force $candidate (Join-Path $StageDir $dllName)
      $sq = New-Object System.Collections.Queue
      $sq.Enqueue($candidate)
      while ($sq.Count -gt 0) {
        $cur = $sq.Dequeue()
        foreach ($sub in (Get-DllImports $cur)) {
          $sk = $sub.ToLowerInvariant()
          if ($visited.ContainsKey($sk) -and $visited[$sk]) { continue }
          $sc = Join-Path $ucrtBin $sub
          if (-not (Test-Path $sc)) { $visited[$sk] = $false; continue }
          $visited[$sk] = $true
          Copy-Item -Force $sc (Join-Path $StageDir $sub)
          $sq.Enqueue($sc)
        }
      }
    }
  }
}

# --- Drop noisy plugins that have unused/missing deps and pollute uxplay's stderr ---
# libgstcodec2json is for amateur-radio Codec2 audio; nothing in UxPlay touches it,
# but it's in the standard gst-plugins-bad bundle and its missing dep (codec2)
# spams "Failed to load plugin" warnings on every run. Just remove it.
$noisyPlugins = @('libgstcodec2json.dll')
foreach ($p in $noisyPlugins) {
  $f = Join-Path (Join-Path $StageDir 'lib\gstreamer-1.0') $p
  if (Test-Path $f) { Remove-Item -Force $f }
}

# --- Copy gst-plugin-scanner.exe (used by GStreamer to enumerate plugins) ---
$scannerSrc = Join-Path $Msys2Root 'ucrt64\libexec\gstreamer-1.0\gst-plugin-scanner.exe'
if (Test-Path $scannerSrc) {
  Copy-Item -Force $scannerSrc (Join-Path $StageDir 'gst-plugin-scanner.exe')
  Write-Host "Copied gst-plugin-scanner.exe"
}

# --- Final summary ---
$totalSize = (Get-ChildItem -Recurse -File $StageDir | Measure-Object Length -Sum).Sum
"Staged UxPlay runtime to: $StageDir"
"Total size: {0:N1} MB" -f ($totalSize / 1MB)
