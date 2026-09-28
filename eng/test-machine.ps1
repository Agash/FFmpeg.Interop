#!/usr/bin/env pwsh
# Runs every test this machine can run: the CPU suites CI also runs, plus the hardware suites for the
# GPUs present. No CI runner has a GPU, so the hardware paths are covered only by running this on
# machines that have them. Coverage goes to artifacts/coverage/<machine>.cobertura.xml; merge it with
# CI's with `dotnet dotnet-coverage merge`.
#
# A vendor's suite is included when the GPU is present and fails if its device will not open: a skip
# would read as a pass.
#
# Usage:  ./eng/test-machine.ps1 [-Configuration Release]
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-GpuVendors {
    if ($IsWindows) {
        return Get-CimInstance Win32_VideoController | ForEach-Object {
            switch -Regex ($_.PNPDeviceID) { 'VEN_10DE' { 'Nvidia' } 'VEN_1002|VEN_1022' { 'Amd' } 'VEN_8086' { 'Intel' } }
        }
    }

    if ($IsMacOS) { return @('Apple') }

    return Get-ChildItem /sys/class/drm -Filter 'renderD*' -ErrorAction SilentlyContinue | ForEach-Object {
        switch ((Get-Content "$($_.FullName)/device/vendor" -ErrorAction SilentlyContinue)) {
            '0x10de' { 'Nvidia' } '0x1002' { 'Amd' } '0x8086' { 'Intel' }
        }
    }
}

$vendors = @(Get-GpuVendors | Sort-Object -Unique)
$suites = [ordered]@{
    RequiresNvidia       = $IsWindows -and $vendors -contains 'Nvidia'
    RequiresAmf          = $IsWindows -and $vendors -contains 'Amd'
    RequiresVaapi        = $IsLinux -and $vendors -contains 'Amd'
    RequiresVideoToolbox = [bool]$IsMacOS
}

# The Vulkan suite needs a Vulkan loader: Windows and Linux GPU drivers install one, macOS has one only
# with MoltenVK. It is probed the way the tests will load it, not assumed.
function Test-VulkanLoader {
    $names = if ($IsWindows) { @('vulkan-1') }
        elseif ($IsMacOS) { @('libvulkan.1.dylib', '/opt/homebrew/lib/libvulkan.1.dylib', '/usr/local/lib/libvulkan.1.dylib') }
        else { @('libvulkan.so.1') }
    foreach ($name in $names) {
        $handle = [IntPtr]::Zero
        if ([System.Runtime.InteropServices.NativeLibrary]::TryLoad($name, [ref]$handle)) { return $true }
    }
    return $false
}

$suites['RequiresVulkan'] = Test-VulkanLoader

# The generic GPU tests (RequiresGpu without a vendor) run wherever there is any GPU.
$exclusions = @($suites.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { "TestCategory!=$($_.Key)" })
if ($vendors.Count -eq 0) { $exclusions += 'TestCategory!=RequiresGpu' }
$filter = $exclusions -join '&'

Write-Host "GPUs: $(if ($vendors) { $vendors -join ', ' } else { 'none' })"
$suites.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-22} {1}" -f $_.Key, $(if ($_.Value) { 'run' } else { 'not on this machine' })) }

& ./eng/fetch-ffmpeg.ps1
dotnet build FFmpeg.Interop.slnx -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$coverageDir = Join-Path $root 'artifacts/coverage'
New-Item -ItemType Directory -Force $coverageDir | Out-Null
$arguments = @(
    'test', '--solution', 'FFmpeg.Interop.slnx', '--no-build', '-c', $Configuration, '--settings', 'tests.runsettings',
    '--coverage', '--coverage-settings', 'coverage.settings.xml', '--coverage-output-format', 'cobertura',
    '--coverage-output', 'machine.cobertura.xml'
)
if ($filter) { $arguments += @('--filter', $filter) }
dotnet @arguments
$result = $LASTEXITCODE

$report = Get-ChildItem $root -Recurse -Filter machine.cobertura.xml | Sort-Object LastWriteTime | Select-Object -Last 1
if ($report) {
    $target = Join-Path $coverageDir "$([Environment]::MachineName.ToLowerInvariant()).cobertura.xml"
    Copy-Item $report.FullName $target -Force
    [xml]$xml = Get-Content $target
    Write-Host ("Coverage: {0:P1} lines, {1:P1} branches -> {2}" -f [double]$xml.coverage.'line-rate', [double]$xml.coverage.'branch-rate', $target)
}

exit $result
