#!/usr/bin/env pwsh
# Merges Cobertura coverage reports from several machines into one, and prints the totals.
#
# Each report records absolute source paths from the machine that produced it (D:\a\..., /home/runner/...,
# /Users/...), and the merge matches files by path, so the same file from two platforms would be counted
# twice rather than combined. The paths are rewritten to repository-relative form first.
#
# Usage:  ./eng/merge-coverage.ps1 -Reports <cobertura files...> -Output merged.cobertura.xml
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]]$Reports,
    [Parameter(Mandatory)] [string]$Output
)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) "ffmpeg-interop-coverage-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $normalized = @()
    $index = 0
    foreach ($report in $Reports) {
        [xml]$xml = Get-Content -Raw $report
        foreach ($node in $xml.SelectNodes('//*[@filename]')) {
            $path = $node.GetAttribute('filename').Replace('\', '/')
            $match = [regex]::Match($path, '(?:^|/)((?:src|tests|samples)/.*)$')
            if ($match.Success) { $node.SetAttribute('filename', $match.Groups[1].Value) }
        }

        foreach ($source in @($xml.SelectNodes('//sources/source'))) { $source.InnerText = '.' }
        $target = Join-Path $work "$((++$index)).cobertura.xml"
        $xml.Save($target)
        $normalized += $target
    }

    dnx dotnet-coverage@18.11.2 --yes -- merge --output $Output --output-format cobertura @normalized
    if ($LASTEXITCODE -ne 0) { throw "dotnet-coverage merge failed ($LASTEXITCODE)." }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

[xml]$merged = Get-Content -Raw $Output
[pscustomobject]@{
    Lines          = [double]$merged.coverage.'line-rate'
    Branches       = [double]$merged.coverage.'branch-rate'
    LinesCovered   = [int]$merged.coverage.'lines-covered'
    LinesValid     = [int]$merged.coverage.'lines-valid'
    BranchesCovered = [int]$merged.coverage.'branches-covered'
    BranchesValid  = [int]$merged.coverage.'branches-valid'
}
