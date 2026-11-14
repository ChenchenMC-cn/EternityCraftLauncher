# SPDX-License-Identifier: MPL-2.0
# Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
#
#  Cold-start benchmark. Runs the launcher N times and reports the timings the
#  launcher itself records in startup.log, so "faster" is a number, not a feeling.
#
#  Usage:  scripts\bench.bat            5 runs
#          scripts\bench.bat 10         10 runs
#
#  What is measured:
#    ctor -> shown   : from the first log line (App ctor) to "main window shown"
#    palette         : addons loaded + palette built (the extension machinery)
#    construct       : MainWindow construction
#
#  Keep this script pure ASCII: PowerShell 5.1 reads ANSI when there is no BOM.

[CmdletBinding()]
param(
    [int]$Runs = 5,
    [string]$Exe = ""
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

# Default target is the framework-dependent build. Pass -Exe to measure a published
# artifact instead (single-file, ReadyToRun, ...), which is what users actually run.
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $exe = Join-Path $root 'src\ECL.Launcher\bin\Release\net10.0-windows\ECL.exe'
} else {
    $exe = (Resolve-Path -LiteralPath $Exe).Path
}

if (-not (Test-Path $exe)) {
    Write-Host "not built: $exe"
    Write-Host "run scripts\build_all.bat first"
    exit 1
}

$log = Join-Path (Split-Path -Parent $exe) 'startup.log'
$samples = @()

function Parse-Time([string]$line) {
    # [21:05:07.238] main window shown
    if ($line -match '^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]') {
        return [double]$matches[1] * 3600 + [double]$matches[2] * 60 +
               [double]$matches[3] + [double]$matches[4] / 1000
    }
    return $null
}

for ($i = 1; $i -le $Runs; $i++) {
    if (Test-Path $log) { Remove-Item $log -Force }

    $shot = Join-Path $env:TEMP ("ecl-bench-{0}.png" -f $i)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Process -FilePath $exe -ArgumentList @('--shot', $shot) -Wait -WindowStyle Hidden
    $sw.Stop()

    if (-not (Test-Path $log)) { Write-Host "run ${i}: no startup.log"; continue }

    $lines = Get-Content $log
    $first = $null; $shown = $null; $palette = $null; $construct = $null

    foreach ($l in $lines) {
        $t = Parse-Time $l
        if ($null -eq $t) { continue }
        if ($null -eq $first) { $first = $t }
        if ($l -like '*addons:*' -or $l -like '*palette*') { if ($null -eq $palette) { $palette = $t } }
        if ($l -like '*main window constructed*') { $construct = $t }
        if ($l -like '*main window shown*') { $shown = $t }
    }

    if ($null -eq $first -or $null -eq $shown) { Write-Host "run ${i}: log incomplete"; continue }

    $total = ($shown - $first) * 1000
    $toConstruct = if ($null -ne $construct) { ($construct - $first) * 1000 } else { 0 }
    $pal = if ($null -ne $palette) { ($palette - $first) * 1000 } else { 0 }

    $samples += [pscustomobject]@{
        Run       = $i
        CtorToMs  = [math]::Round($total)
        PaletteMs = [math]::Round($pal)
        WindowMs  = [math]::Round($toConstruct)
        WallMs    = [math]::Round($sw.Elapsed.TotalMilliseconds)
    }
    Remove-Item $shot -Force -ErrorAction SilentlyContinue
}

if ($samples.Count -eq 0) { Write-Host "no samples"; exit 1 }

$samples | Format-Table -AutoSize

$t = $samples.CtorToMs | Sort-Object
$w = $samples.WallMs  | Sort-Object
$median = if ($t.Count % 2) { $t[[int]($t.Count / 2)] } else { [math]::Round(($t[$t.Count / 2 - 1] + $t[$t.Count / 2]) / 2) }

Write-Host ("ctor -> shown : min {0} ms   median {1} ms   max {2} ms" -f $t[0], $median, $t[-1])
Write-Host ("wall (incl. screenshot dwell, NOT start-up): median {0} ms" -f $w[[int]($w.Count / 2)])
Write-Host ""
Write-Host "record this before and after a change; a 5% move is noise, not a win."
