#!/usr/bin/env pwsh
# Fetches a pinned FFmpeg 9 build into native/<rid>/ for the tests: the shared libraries the bindings
# load, and the ffmpeg and ffprobe tools the integration tests check them against.
#
# Windows and Linux take BtbN's month-end LGPL builds, which BtbN keeps for about two years (the daily
# builds are gone after two weeks), each verified by SHA-256. They carry openh264, SVT-AV1, libaom and
# dav1d, which is what the software round trips use. macOS links Homebrew's ffmpeg, which has no
# pinned archive; the tests check its major instead.
#
# Usage:  ./eng/fetch-ffmpeg.ps1 [-Rid <rid>]      (defaults to the current machine's RID)
[CmdletBinding()]
param(
    [string]$Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "native/$Rid"

$release = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08'
$build = 'ffmpeg-n9.0.2-17-g2a571b6068'
$sources = @{
    'win-x64'     = @("$build-win64-lgpl-shared-9.0.zip", '7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646')
    'win-arm64'   = @("$build-winarm64-lgpl-shared-9.0.zip", 'f3133abf5c2b6ae45bd428d480cf830a9fbda3a3866dc249110e85f99f13b8ce')
    'linux-x64'   = @("$build-linux64-lgpl-shared-9.0.tar.xz", '7a1ba2af36e5beeb98151e766a903b1d115f2206a86571f17f873cd7292c424f')
    'linux-arm64' = @("$build-linuxarm64-lgpl-shared-9.0.tar.xz", '1aa02c59afe06dea2602b40517a5cb75c0ff6d524f217b5aea1f0ca07977cd51')
}

Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dest | Out-Null

if ($Rid.StartsWith('osx-')) {
    $prefix = (& brew --prefix ffmpeg).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Homebrew ffmpeg is not installed: brew install ffmpeg' }
    if (-not (Test-Path "$prefix/lib/libavcodec.63.dylib")) {
        throw "Homebrew's ffmpeg at $prefix is not FFmpeg 9 (no libavcodec.63.dylib)."
    }

    New-Item -ItemType SymbolicLink -Path "$dest/lib" -Target "$prefix/lib" | Out-Null
    New-Item -ItemType SymbolicLink -Path "$dest/bin" -Target "$prefix/bin" | Out-Null
    Write-Host "Linked Homebrew ffmpeg at $prefix into $dest"
    return
}

if (-not $sources.ContainsKey($Rid)) { throw "No pinned FFmpeg build for RID '$Rid'." }

($file, $sha256) = $sources[$Rid]
$archive = Join-Path ([IO.Path]::GetTempPath()) $file
Write-Host "Fetching $file"
Invoke-WebRequest -Uri "$release/$file" -OutFile $archive -UseBasicParsing

$actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sha256) {
    Remove-Item $archive -Force
    throw "SHA-256 mismatch for ${file}: expected $sha256, got $actual."
}

# Extracted beside the destination, not in the temp directory: /tmp is often another filesystem, and
# moving a directory across filesystems copies it, which fails on the archive's .so symlinks.
$extract = Join-Path $root "native/.extract-$Rid"
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $extract | Out-Null
if ($file.EndsWith('.zip')) {
    Expand-Archive -Path $archive -DestinationPath $extract -Force
} else {
    # A Git for Windows GNU tar earlier on PATH reads "C:" as a remote host; this only runs on Linux,
    # but the Windows bsdtar is named explicitly in case someone fetches a Linux build from Windows.
    $tar = if ($IsWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
    & $tar -xf $archive -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
}

# The archive holds a single top-level directory with bin/, lib/ and include/. Only bin and lib are
# kept; the tools find their libraries through the relative rpath (Linux) or the same directory
# (Windows), so the tree keeps its shape.
$top = Get-ChildItem $extract -Directory | Select-Object -First 1
foreach ($part in 'bin', 'lib') {
    if (Test-Path "$($top.FullName)/$part") {
        Move-Item "$($top.FullName)/$part" "$dest/$part"
    }
}

Remove-Item $extract -Recurse -Force
Remove-Item $archive -Force
Write-Host "FFmpeg $build for $Rid in $dest"
