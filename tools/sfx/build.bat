@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Build ECL-Setup.exe (self-extracting shell).
rem
rem  Same manual toolchain setup as tools\rtcheck\build_manual.bat: vcvars64.bat
rem  calls reg.exe, which some sandboxes block, leaving INCLUDE empty.
rem
rem  rc.exe is not on PATH either, so it is pulled from the SDK bin folder.
rem
rem  Prerequisites:
rem    dist\           the 6 launcher files (run scripts\build_all.bat first)
rem    tools\rtcheck\  rtcheck.exe already built
rem
rem  Keep this file pure ASCII (cmd reads .bat as the OEM codepage).
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [sfx] vswhere.exe not found.
    exit /b 1
)

set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
    echo [sfx] No Visual C++ toolset found.
    exit /b 1
)

set "MSVC="
for /f "delims=" %%d in ('dir /b /ad /o-n "%VSPATH%\VC\Tools\MSVC" 2^>nul') do (
    if not defined MSVC set "MSVC=%VSPATH%\VC\Tools\MSVC\%%d"
)

set "SDKROOT=%~1"
if not defined SDKROOT (
    for %%R in (C D E F G) do (
        if not defined SDKROOT if exist "%%R:\Windows Kits\10\Include" set "SDKROOT=%%R:\Windows Kits\10"
    )
)
if not defined SDKROOT (
    echo [sfx] Windows SDK not found.
    exit /b 1
)

set "SDKVER="
for /f "delims=" %%d in ('dir /b /ad /o-n "%SDKROOT%\Include" 2^>nul ^| findstr /b "10.0."') do (
    if not defined SDKVER (
        if exist "%SDKROOT%\Include\%%d\ucrt\stdio.h" (
            if exist "%SDKROOT%\Include\%%d\um\windows.h" set "SDKVER=%%d"
        )
    )
)
if not defined SDKVER (
    echo [sfx] No complete 10.0.* SDK under %SDKROOT%\Include
    exit /b 1
)

set "INCLUDE=%MSVC%\include;%SDKROOT%\Include\%SDKVER%\ucrt;%SDKROOT%\Include\%SDKVER%\um;%SDKROOT%\Include\%SDKVER%\shared"
set "LIB=%MSVC%\lib\x64;%SDKROOT%\Lib\%SDKVER%\ucrt\x64;%SDKROOT%\Lib\%SDKVER%\um\x64"
set "PATH=%MSVC%\bin\Hostx64\x64;%SDKROOT%\bin\%SDKVER%\x64;%PATH%"

cd /d "%~dp0"

rem sanity: every payload file must exist before rc.exe fails on it
set "MISSING=0"
for %%f in ("..\..\..\EternityCraftLauncher-lite\ECL.exe" "..\..\dist\ECL.Runtime.jar" "..\rtcheck\rtcheck.exe" "..\reporter\eclcr.exe" "..\reporter\eclnfer.exe") do (
    if not exist "%%~f" (
        echo [sfx] missing: %%~f
        set "MISSING=1"
    )
)
if "%MISSING%"=="1" (
    echo [sfx] build dist\ and tools\rtcheck\rtcheck.exe first.
    exit /b 1
)

echo [sfx] MSVC : %MSVC%
echo [sfx] SDK  : %SDKROOT%  (%SDKVER%)

echo [sfx] compiling resource...
rc /nologo /fo payload.res payload.rc
if errorlevel 1 (
    echo [sfx] rc failed.
    exit /b 1
)

echo [sfx] compiling shell...
cl /nologo /std:c++17 /EHsc /O2 /utf-8 /W4 /DUNICODE /D_UNICODE sfx.cpp payload.res /Fe:ECL-Setup.exe /link user32.lib shell32.lib shlwapi.lib
if errorlevel 1 (
    echo [sfx] compile failed.
    exit /b 1
)

del /q *.obj >nul 2>&1
echo [sfx] done
for %%f in (ECL-Setup.exe) do echo [sfx] size: %%~zf bytes
