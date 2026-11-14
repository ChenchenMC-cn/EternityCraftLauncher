@echo off
rem ===========================================================================
rem  Cold-start benchmark wrapper. See bench.ps1 for what is measured.
rem
rem    scripts\bench.bat          5 runs
rem    scripts\bench.bat 10       10 runs
rem
rem  Pure ASCII: cmd reads .bat with the OEM code page.
rem ===========================================================================
setlocal
set "N=%~1"
if "%N%"=="" set "N=5"
rem Optional second argument: path to a published exe (single-file, R2R build).
if not "%~2"=="" set "EXEARG=-Exe "%~2""
set "PS=powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bench.ps1" -Runs %N% %EXEARG%"
%PS%
exit /b %errorlevel%
