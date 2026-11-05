@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Build rtcheck.exe (C++, MSVC x64, no third-party dependencies)
rem
rem  Same vswhere discovery as scripts\build_native.bat, so it works no matter
rem  where Visual Studio is installed (this machine has it on F:).
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [rtcheck] vswhere.exe not found. Is Visual Studio installed?
    exit /b 1
)

set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
    echo [rtcheck] No Visual C++ toolset found.
    exit /b 1
)

echo [rtcheck] toolset: %VSPATH%
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
    echo [rtcheck] vcvars64 failed.
    exit /b 1
)

cd /d "%~dp0"
echo [rtcheck] compiling...
cl /nologo /std:c++17 /EHsc /O2 /utf-8 /W4 /DUNICODE /D_UNICODE rtcheck.cpp /Fe:rtcheck.exe /link user32.lib advapi32.lib shell32.lib shlwapi.lib
if errorlevel 1 (
    echo [rtcheck] compile failed.
    exit /b 1
)

del /q *.obj >nul 2>&1
echo [rtcheck] done: %~dp0rtcheck.exe
for %%f in (rtcheck.exe) do echo [rtcheck] size: %%~zf bytes
