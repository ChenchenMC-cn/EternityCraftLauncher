@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Build rtcheck.exe without vcvars64.
rem
rem  Why not vcvars64.bat:
rem    It shells out to reg.exe, which some sandboxes block outright. The call
rem    then dies with "cannot open include file: windows.h" because INCLUDE was
rem    never populated. This script sets INCLUDE/LIB/PATH directly - no registry.
rem
rem  Why the SDK is picked carefully:
rem    A machine can carry both the normal SDK and the WDK. The WDK publishes
rem    newer "10.0.*" folders but ships no ucrt headers, so taking the highest
rem    version number is wrong. Require ucrt\stdio.h and um\windows.h.
rem
rem  NOTE: keep this file pure ASCII. cmd.exe reads .bat as the OEM codepage and
rem  mis-parses multibyte comments as commands ("'heck.exe' is not recognized").
rem
rem  Usage: build_manual.bat ["D:\Windows Kits\10"]
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [rtcheck] vswhere.exe not found.
    exit /b 1
)

set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
    echo [rtcheck] No Visual C++ toolset found.
    exit /b 1
)

set "MSVC="
for /f "delims=" %%d in ('dir /b /ad /o-n "%VSPATH%\VC\Tools\MSVC" 2^>nul') do (
    if not defined MSVC set "MSVC=%VSPATH%\VC\Tools\MSVC\%%d"
)
if not defined MSVC (
    echo [rtcheck] MSVC toolset directory not found.
    exit /b 1
)

set "SDKROOT=%~1"
if not defined SDKROOT (
    for %%R in (C D E F G) do (
        if not defined SDKROOT if exist "%%R:\Windows Kits\10\Include" set "SDKROOT=%%R:\Windows Kits\10"
    )
)
if not defined SDKROOT (
    echo [rtcheck] Windows SDK not found. Pass the root as the first argument.
    exit /b 1
)

rem highest 10.0.* that actually contains the user-mode + ucrt headers
set "SDKVER="
for /f "delims=" %%d in ('dir /b /ad /o-n "%SDKROOT%\Include" 2^>nul ^| findstr /b "10.0."') do (
    if not defined SDKVER (
        if exist "%SDKROOT%\Include\%%d\ucrt\stdio.h" (
            if exist "%SDKROOT%\Include\%%d\um\windows.h" set "SDKVER=%%d"
        )
    )
)
if not defined SDKVER (
    echo [rtcheck] No complete 10.0.* SDK under %SDKROOT%\Include
    exit /b 1
)

echo [rtcheck] VS    : %VSPATH%
echo [rtcheck] MSVC  : %MSVC%
echo [rtcheck] SDK   : %SDKROOT%  (%SDKVER%)

set "INCLUDE=%MSVC%\include;%SDKROOT%\Include\%SDKVER%\ucrt;%SDKROOT%\Include\%SDKVER%\um;%SDKROOT%\Include\%SDKVER%\shared;%SDKROOT%\Include\%SDKVER%\winrt"
set "LIB=%MSVC%\lib\x64;%SDKROOT%\Lib\%SDKVER%\ucrt\x64;%SDKROOT%\Lib\%SDKVER%\um\x64"
set "PATH=%MSVC%\bin\Hostx64\x64;%PATH%"

cd /d "%~dp0"
echo [rtcheck] compiling...
cl /nologo /std:c++17 /EHsc /O2 /utf-8 /W4 /DUNICODE /D_UNICODE rtcheck.cpp /Fe:rtcheck.exe /link user32.lib advapi32.lib shell32.lib shlwapi.lib
if errorlevel 1 (
    echo [rtcheck] compile failed.
    exit /b 1
)

del /q *.obj >nul 2>&1
echo [rtcheck] done
for %%f in (rtcheck.exe) do echo [rtcheck] size: %%~zf bytes
