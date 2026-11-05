@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Version helper. Thin wrapper around version.ps1 so it can be double-clicked
rem  or called from AllRebuild.bat.
rem
rem    version.bat            show the current version
rem    version.bat -Bump      advance one step and rewrite csproj + payload.rc
rem    version.bat -DryRun    show what a bump would do, write nothing
rem
rem  Keep this file pure ASCII (cmd reads .bat as the OEM codepage).
rem ===========================================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0version.ps1" %*
exit /b %errorlevel%
