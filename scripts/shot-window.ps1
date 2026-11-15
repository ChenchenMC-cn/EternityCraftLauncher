# SPDX-License-Identifier: MPL-2.0
# Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
#
#  Screenshots a GUI window by process name, so the crash reporter can be reviewed
#  without a human sitting in front of it.
#
#  Usage:  powershell -File scripts\shot-window.ps1 -Exe <path> -Args <string> -Out <png> [-Delay 2000]
#
#  Keep this file ASCII-only: PowerShell 5.1 reads ANSI when there is no BOM.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Args = "",
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Delay = 1800
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WinShot
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$proc = if ($Args -ne "") {
    Start-Process -FilePath $Exe -ArgumentList $Args -PassThru
} else {
    Start-Process -FilePath $Exe -PassThru
}

try {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and $proc.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 120
        $proc.Refresh()
    }

    if ($proc.MainWindowHandle -eq 0) { throw "no window appeared" }

    [void][WinShot]::SetForegroundWindow($proc.MainWindowHandle)
    Start-Sleep -Milliseconds $Delay

    $rect = New-Object WinShot+RECT
    if (-not [WinShot]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)) { throw "GetWindowRect failed" }

    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { throw "bad window size ${w}x${h}" }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $gfx.Dispose()
    $bmp.Dispose()

    Write-Host ("captured {0}x{1} -> {2}" -f $w, $h, $Out)
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
}
