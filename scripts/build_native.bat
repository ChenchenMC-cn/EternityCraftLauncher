@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  ECL.Native build script (MSVC x64, no third-party deps)
rem
rem  Toolchain discovery is done by hand instead of calling vcvars64.bat.
rem  vcvars64 shells out to reg.exe; environments that block it leave INCLUDE
rem  empty and the build dies on "cannot open include file: windows.h".
rem  vcvars64 is still used as a fallback if the manual scan finds nothing.
rem
rem  Why the SDK is picked carefully:
rem    A machine can carry both the normal SDK and the WDK. The WDK publishes
rem    newer "10.0.*" folders but ships no ucrt headers, so taking the highest
rem    version number is wrong. Require ucrt\stdio.h and um\windows.h.
rem
rem  NOTE: keep this file pure ASCII. cmd.exe reads .bat as the OEM codepage and
rem  mis-parses multi-byte comments as commands.
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "ROOT=%~dp0.."
for %%i in ("%ROOT%") do set "ROOT=%%~fi"
set "OUT=%ROOT%\build\native"
if not exist "%OUT%" mkdir "%OUT%"

call :toolchain
if not defined INCLUDE (
    echo [ECL] Could not set up the MSVC toolchain.
    exit /b 1
)

echo [ECL] MSVC : %MSVC%
echo [ECL] SDK  : %SDKROOT%  (%SDKVER%)
echo [ECL] Compiling ECL.Native...

pushd "%OUT%"
cl /nologo /LD /O2 /EHsc /std:c++20 /W3 /DUNICODE /D_UNICODE /Fe:ECL.Native.dll "%ROOT%\src\ECL.Native\src\common.cpp" "%ROOT%\src\ECL.Native\src\hash.cpp" "%ROOT%\src\ECL.Native\src\http.cpp" "%ROOT%\src\ECL.Native\src\zip.cpp" /link /MACHINE:X64
set "RC=%errorlevel%"
popd

if not "%RC%"=="0" (
    echo [ECL] Build FAILED.
    exit /b 1
)

if not exist "%OUT%\ECL.Native.dll" (
    echo [ECL] Build reported success but produced no DLL.
    exit /b 1
)

for %%f in ("%OUT%\ECL.Native.dll") do echo [ECL] Native library -^> %%~zf bytes
exit /b 0

rem ---------------------------------------------------------------------------
rem  :toolchain - populate INCLUDE / LIB / PATH / MSVC / SDKROOT / SDKVER
rem ---------------------------------------------------------------------------
:toolchain
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [ECL] vswhere.exe not found. Is Visual Studio installed?
    goto :eof
)

set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
    echo [ECL] No Visual C++ toolset found.
    goto :eof
)

set "MSVC="
for /f "delims=" %%d in ('dir /b /ad /o-n "%VSPATH%\VC\Tools\MSVC" 2^>nul') do (
    if not defined MSVC set "MSVC=%VSPATH%\VC\Tools\MSVC\%%d"
)
if not defined MSVC (
    echo [ECL] MSVC toolset directory not found.
    goto :eof
)

set "SDKROOT="
for %%R in (C D E F G) do (
    if not defined SDKROOT if exist "%%R:\Windows Kits\10\Include" set "SDKROOT=%%R:\Windows Kits\10"
)

set "SDKVER="
if defined SDKROOT (
    for /f "delims=" %%d in ('dir /b /ad /o-n "%SDKROOT%\Include" 2^>nul ^| findstr /b "10.0."') do (
        if not defined SDKVER (
            if exist "%SDKROOT%\Include\%%d\ucrt\stdio.h" (
                if exist "%SDKROOT%\Include\%%d\um\windows.h" set "SDKVER=%%d"
            )
        )
    )
)

if defined SDKVER (
    set "INCLUDE=%MSVC%\include;%SDKROOT%\Include\%SDKVER%\ucrt;%SDKROOT%\Include\%SDKVER%\um;%SDKROOT%\Include\%SDKVER%\shared;%SDKROOT%\Include\%SDKVER%\winrt"
    set "LIB=%MSVC%\lib\x64;%SDKROOT%\Lib\%SDKVER%\ucrt\x64;%SDKROOT%\Lib\%SDKVER%\um\x64"
    set "PATH=%MSVC%\bin\Hostx64\x64;%PATH%"
    goto :eof
)

rem last resort: let Microsoft's script do it (works when reg.exe is allowed)
echo [ECL] manual SDK scan failed, falling back to vcvars64.bat
if exist "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
goto :eof
