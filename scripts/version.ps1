# SPDX-License-Identifier: MPL-2.0
# Copyright (c) 2026 ChenchenMC
# ===========================================================================
#  ECL version tool
#
#  Naming rule (set by chen, 2026-11-04):
#
#      1.0.0                   release
#      1.0.1 .. 1.0.9          small fix  (a tenth rolls over to 1.1.0)
#      1.1.0                   medium update
#      2.0.0                   major update
#      1.0.0 Build01..Build99  development (numeric 1.0.0.<build>)
#
#  The displayed version IS the numeric version -- no "Fix N" suffix, so the
#  badge, the crash report and the file properties always read the same.
#
#  No "Beta" prefix and no leading "v" anywhere: the product is released, and the
#  number itself carries the stage. A tenth fix of the same minor rolls over to the
#  next minor (there is no "Fix 10").
#
#  One source of truth per artifact:
#      src/ECL.Launcher/ECL.Launcher.csproj   -> what the UI shows
#      tools/sfx/payload.rc                   -> Explorer properties of the setup
#
#  Numeric fields stay monotonic so Windows and the launcher can order builds:
#      Version / AssemblyVersion / FileVersion = <major>.<minor>.<fix>[.<build>]
#
#  Usage:
#      version.ps1              print the current version
#      version.ps1 -Bump        one step: release -> Fix 1 -> ... -> Fix 9 -> next minor
#      version.ps1 -Medium      next minor        (1.0 -> 1.1)
#      version.ps1 -Major       next major        (1.1 -> 2.0)
#      version.ps1 -Dev         development build (1.0 -> 1.0 Build01 -> Build02)
#      version.ps1 -DryRun      print what would change, write nothing
#
#  NOTE: keep this file pure ASCII. PowerShell 5.1 decodes a BOM-less script with
#  the ANSI code page, so non-ASCII text inside would come out garbled.
# ===========================================================================
[CmdletBinding()]
param(
    [switch]$Bump,
    [switch]$Medium,
    [switch]$Major,
    [switch]$Dev,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$csproj = Join-Path $root 'src\ECL.Launcher\ECL.Launcher.csproj'
$rcfile = Join-Path $root 'tools\sfx\payload.rc'

foreach ($p in @($csproj, $rcfile)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing file: $p" }
}

# --- encoding-preserving read/write (the csproj may or may not carry a BOM) ---
function Read-Text([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $skip = if ($bom) { 3 } else { 0 }
    $text = [Text.Encoding]::UTF8.GetString($bytes, $skip, $bytes.Length - $skip)
    return [pscustomobject]@{ Text = $text; Bom = $bom }
}

function Write-Text([string]$path, [string]$text, [bool]$bom) {
    $enc = New-Object System.Text.UTF8Encoding($bom)
    [IO.File]::WriteAllText($path, $text, $enc)
}

# Replaces every match of $pattern and fails loudly when nothing matched --
# a silent no-op here would ship the previous version number and we would only
# notice after a 5 minute rebuild.
function Replace-Or-Fail([string]$text, [string]$pattern, [string]$replacement, [string]$label) {
    $re = [regex]$pattern
    if (-not $re.IsMatch($text)) { throw "pattern not found ($label): $pattern" }
    return $re.Replace($text, $replacement)
}

# --- read current state ------------------------------------------------------
$cs = Read-Text $csproj
$m = [regex]::Match($cs.Text, '<InformationalVersion>([^<]*)</InformationalVersion>')
if (-not $m.Success) { throw 'InformationalVersion is not set in ECL.Launcher.csproj' }

$current = $m.Groups[1].Value.Trim()

# "1.0", "1.0 Fix 3", "1.0 Build07"
$vm = [regex]::Match($current, '^(?<maj>\d+)\.(?<min>\d+)\.(?<fix>\d+)$')
$vd = [regex]::Match($current, '^(?<maj>\d+)\.(?<min>\d+)\.(?<fix>\d+)\s+Build(?<build>\d{2})$')
if (-not $vm.Success -and -not $vd.Success) {
    throw "cannot parse the version '$current'. Expected '1.0.0', '1.0.1' or '1.0.0 Build03'."
}

$isDev = $vd.Success
# Variable names here are case-insensitive in PS, so $major would BE the
# -Major switch parameter. Internal state carries a cur prefix for that reason.
if ($isDev) {
    $curMajor = [int]$vd.Groups['maj'].Value
    $curMinor = [int]$vd.Groups['min'].Value
    $curFix   = 0
    $curBuild = [int]$vd.Groups['build'].Value
}
else {
    $curMajor = [int]$vm.Groups['maj'].Value
    $curMinor = [int]$vm.Groups['min'].Value
    $curFix   = if ($vm.Groups['fix'].Success) { [int]$vm.Groups['fix'].Value } else { 0 }
    $curBuild = 0
}

function Format-Display([int]$maj, [int]$min, [int]$f, [int]$b) {
    if ($b -gt 0) { return "$maj.$min.$f Build$('{0:D2}' -f $b)" }
    return "$maj.$min.$f"
}

function Format-Numeric([int]$maj, [int]$min, [int]$f, [int]$b) {
    if ($b -gt 0) { return "$maj.$min.0.$b" }
    return "$maj.$min.$f"
}

$display = Format-Display $curMajor $curMinor $curFix $curBuild
$numeric = Format-Numeric $curMajor $curMinor $curFix $curBuild

Write-Host "current : ECL $display"
Write-Host "numeric : $numeric"

if (-not ($Bump -or $Medium -or $Major -or $Dev)) {
    Write-Host ""
    Write-Host "run with -Bump / -Medium / -Major / -Dev to advance (AllRebuild.bat uses -Bump)"
    exit 0
}

# --- advance one step --------------------------------------------------------
$nextMajor = $curMajor
$nextMinor = $curMinor
$nextFix   = $curFix
$nextBuild = 0

if ($Major) {
    $nextMajor = $curMajor + 1
    $nextMinor = 0
    $nextFix   = 0
}
elseif ($Medium) {
    $nextMinor = $curMinor + 1
    $nextFix   = 0
}
elseif ($Dev) {
    $nextBuild = $curBuild + 1
    $nextFix   = 0
    if ($nextBuild -gt 99) { throw 'Build number exhausted (max 99); release instead.' }
}
else {
    # -Bump: a fix of the current minor, rolling over after the ninth.
    $nextFix = $curFix + 1
    if ($nextFix -gt 9) {
        $nextMinor = $curMinor + 1
        $nextFix   = 1
    }
}

$nextDisplay = Format-Display $nextMajor $nextMinor $nextFix $nextBuild
$nextNumeric = Format-Numeric $nextMajor $nextMinor $nextFix $nextBuild

Write-Host "next    : ECL $nextDisplay   (numeric $nextNumeric)"

if ($DryRun) {
    Write-Host "dry run : nothing written"
    exit 0
}

# --- rewrite the csproj ------------------------------------------------------
$parts = $nextNumeric.Split('.')
while ($parts.Length -lt 4) { $parts += '0' }
$assembly = ($parts[0..3] -join '.')
$comma = ($parts[0..3] -join ',')

$csNew = $cs.Text
$csNew = Replace-Or-Fail $csNew '<Version>[^<]*</Version>' `
    "<Version>$nextNumeric</Version>" 'csproj Version'
$csNew = Replace-Or-Fail $csNew '<AssemblyVersion>[^<]*</AssemblyVersion>' `
    "<AssemblyVersion>$assembly</AssemblyVersion>" 'csproj AssemblyVersion'
$csNew = Replace-Or-Fail $csNew '<FileVersion>[^<]*</FileVersion>' `
    "<FileVersion>$assembly</FileVersion>" 'csproj FileVersion'
$csNew = Replace-Or-Fail $csNew '<InformationalVersion>[^<]*</InformationalVersion>' `
    "<InformationalVersion>$nextDisplay</InformationalVersion>" 'csproj InformationalVersion'
Write-Text $csproj $csNew $cs.Bom

# --- rewrite the setup's VERSIONINFO ----------------------------------------
# Both the binary fields and the strings, otherwise Explorer shows a version
# that disagrees with the launcher.
$rc = Read-Text $rcfile
$rcNew = $rc.Text
$rcNew = Replace-Or-Fail $rcNew '(?m)^FILEVERSION[^\r\n]*' `
    "FILEVERSION $comma" 'rc FILEVERSION'
$rcNew = Replace-Or-Fail $rcNew '(?m)^PRODUCTVERSION[^\r\n]*' `
    "PRODUCTVERSION $comma" 'rc PRODUCTVERSION'
$rcNew = Replace-Or-Fail $rcNew 'VALUE "FileVersion", "[^"]*"' `
    "VALUE ""FileVersion"", ""$nextDisplay""" 'rc FileVersion string'
$rcNew = Replace-Or-Fail $rcNew 'VALUE "ProductVersion", "[^"]*"' `
    "VALUE ""ProductVersion"", ""$nextDisplay""" 'rc ProductVersion string'
Write-Text $rcfile $rcNew $rc.Bom

Write-Host ""
Write-Host "written : src\ECL.Launcher\ECL.Launcher.csproj"
Write-Host "written : tools\sfx\payload.rc"
Write-Host "now     : ECL $nextDisplay"
exit 0
