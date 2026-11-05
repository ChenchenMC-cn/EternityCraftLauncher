@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Eternity Craft Launcher - one shot build
rem  Order: C++ core -> Java runtime -> C# UI -> dist/
rem
rem  build_all.bat --no-native   skip the C++ step and reuse build\native\ECL.Native.dll
rem                              (vcvars64 pulls in reg.exe, which some locked-down
rem                               environments block; the UI alone needs no VS)
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "SKIP_NATIVE=0"
if /i "%~1"=="--no-native" set "SKIP_NATIVE=1"

set "ROOT=%~dp0.."
for %%i in ("%ROOT%") do set "ROOT=%%~fi"
for %%i in ("%ROOT%\dist") do set "DIST=%%~fi"
set "BINDIR=%ROOT%\src\ECL.Launcher\bin\Release\net10.0-windows"

echo.
echo === [1/4] Native core (C++) ===========================================
if "%SKIP_NATIVE%"=="1" (
    echo [ECL] Skipped ^(--no-native^), reusing build\native\ECL.Native.dll
    if not exist "%ROOT%\build\native\ECL.Native.dll" (
        echo [ECL] ECL.Native.dll is missing, cannot skip the native build.
        goto :fail
    )
) else (
    call "%~dp0build_native.bat"
    if errorlevel 1 goto :fail
)

echo.
echo === [2/4] Java runtime component ======================================
where javac >nul 2>&1
if errorlevel 1 (
    echo [ECL] javac was not found on PATH. Install a JDK 8+ and retry.
    goto :fail
)

if exist "%ROOT%\build\javaout" rmdir /s /q "%ROOT%\build\javaout"
mkdir "%ROOT%\build\javaout"

echo [ECL] Compiling Java component...
pushd "%ROOT%"
javac --release 8 -encoding UTF-8 -d build\javaout src\ECL.Runtime\java\ecl\runtime\*.java
set "JAVAC_RC=%errorlevel%"
popd
if not "%JAVAC_RC%"=="0" (
    echo [ECL] javac failed.
    goto :fail
)

pushd "%ROOT%\build\javaout"
if exist "..\ECL.Runtime.jar" del /q "..\ECL.Runtime.jar"
jar cfe ..\ECL.Runtime.jar ecl.runtime.Probe .
set "JAR_RC=%errorlevel%"
popd
if not "%JAR_RC%"=="0" (
    echo [ECL] jar failed.
    goto :fail
)

set "JARSIZE=0"
for %%F in ("%ROOT%\build\ECL.Runtime.jar") do set "JARSIZE=%%~zF"
if !JARSIZE! LSS 2000 (
    echo [ECL] ECL.Runtime.jar is only !JARSIZE! bytes - packaging failed.
    goto :fail
)
echo [ECL] Java component -^> build\ECL.Runtime.jar (!JARSIZE! bytes)

echo.
echo === [3/4] Launcher UI (C#) ============================================
pushd "%ROOT%"
dotnet build "src\ECL.Launcher\ECL.Launcher.csproj" -c Release -t:Rebuild --nologo -v q
set "BUILD_RC=%errorlevel%"
popd
if not "%BUILD_RC%"=="0" goto :fail

rem MSBuild's incremental copy is unreliable for these two (it once kept a stale
rem 24-byte jar), so drop them into the output folder explicitly.
if not exist "%BINDIR%" mkdir "%BINDIR%"
copy /y "%ROOT%\build\native\ECL.Native.dll" "%BINDIR%\ECL.Native.dll" >nul
copy /y "%ROOT%\build\ECL.Runtime.jar" "%BINDIR%\ECL.Runtime.jar" >nul
if not exist "%BINDIR%\ECL.Native.dll" (
    echo [ECL] ECL.Native.dll missing from output.
    goto :fail
)
if not exist "%BINDIR%\ECL.Runtime.jar" (
    echo [ECL] ECL.Runtime.jar missing from output.
    goto :fail
)

echo.
echo === [4/4] Assemble dist ==============================================
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"
rem /XF *.pdb: a release build must not ship symbols. A .pdb next to an
rem obfuscated assembly hands back the original method names and source paths.
robocopy "%BINDIR%" "%DIST%" *.* /XF *.pdb /NFL /NDL /NJH /NJS /NC /NS /R:2 /W:1 >nul
if errorlevel 8 goto :fail
echo [ECL] dist ready: %DIST%
echo.
echo Build succeeded.
exit /b 0

:fail
echo.
echo Build FAILED.
exit /b 1
