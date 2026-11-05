@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Build eclcr.exe (crash report) and eclnfer.exe (non-fatal errors).
rem
rem  One source, two binaries: /DCRASH_REPORTER flips the title, the intro text
rem  and the data source. See reporter.cpp.
rem
rem  Same manual toolchain setup as the other tools - vcvars64.bat calls reg.exe,
rem  which some sandboxes block, leaving INCLUDE empty.
rem
rem  Keep this file pure ASCII (cmd reads .bat as the OEM codepage).
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [reporter] vswhere.exe not found.
    exit /b 1
)

set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
    echo [reporter] No Visual C++ toolset found.
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
    echo [reporter] Windows SDK not found.
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
    echo [reporter] No complete 10.0.* SDK under %SDKROOT%\Include
    exit /b 1
)

set "INCLUDE=%MSVC%\include;%SDKROOT%\Include\%SDKVER%\ucrt;%SDKROOT%\Include\%SDKVER%\um;%SDKROOT%\Include\%SDKVER%\shared"
set "LIB=%MSVC%\lib\x64;%SDKROOT%\Lib\%SDKVER%\ucrt\x64;%SDKROOT%\Lib\%SDKVER%\um\x64"
set "PATH=%MSVC%\bin\Hostx64\x64;%PATH%"

cd /d "%~dp0"

echo [reporter] compiling eclcr.exe...
cl /nologo /std:c++17 /EHsc /O2 /utf-8 /W4 /DUNICODE /D_UNICODE /DCRASH_REPORTER reporter.cpp /Fe:eclcr.exe /link user32.lib shell32.lib shlwapi.lib comctl32.lib gdi32.lib
if errorlevel 1 (
    echo [reporter] eclcr failed.
    exit /b 1
)

echo [reporter] compiling eclnfer.exe...
cl /nologo /std:c++17 /EHsc /O2 /utf-8 /W4 /DUNICODE /D_UNICODE reporter.cpp /Fe:eclnfer.exe /link user32.lib shell32.lib shlwapi.lib comctl32.lib gdi32.lib
if errorlevel 1 (
    echo [reporter] eclnfer failed.
    exit /b 1
)

del /q *.obj >nul 2>&1
echo [reporter] done
for %%f in (eclcr.exe eclnfer.exe) do echo [reporter] %%~zf bytes  %%f
