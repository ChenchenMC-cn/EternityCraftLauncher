@rem SPDX-License-Identifier: MPL-2.0
@rem Copyright (c) 2026 ChenchenMC
@echo off
rem ===========================================================================
rem  Eternity Craft Launcher - full rebuild
rem
rem  Produces every deliverable from source, in dependency order:
rem
rem    build\native\ECL.Native.dll         C++ core
rem    build\ECL.Runtime.jar               Java 8 probe
rem    dist\ + dist.zip                    framework-dependent, unobfuscated
rem    ..\EternityCraftLauncher-lite\      obfuscated single file, needs .NET 10
rem    ..\EternityCraftLauncher-single\    obfuscated single file, self-contained
rem    ..\EternityCraftLauncher-setup\     self-extracting installer + viewers
rem
rem  Usage:
rem    AllRebuild.bat                 normal rebuild (bumps the version first)
rem    AllRebuild.bat --clean         delete bin/obj first (slower, always correct)
rem    AllRebuild.bat --no-test       skip the headless self-test at the end
rem    AllRebuild.bat --keep-native   reuse build\native\ECL.Native.dll (no VS needed)
rem    AllRebuild.bat --no-bump       rebuild without advancing the version
rem
rem  Flags can be combined: AllRebuild.bat --clean --no-test
rem
rem  Step order is not arbitrary:
rem    * the version is bumped first, so the fresh number is compiled in
rem    * the jar is an EmbeddedResource, so it must exist before dotnet build
rem    * tools\sfx\payload.rc embeds dist\ECL.Runtime.jar, so dist must be
rem      assembled before the setup is linked
rem    * the setup embeds the lite exe, so lite must be published first
rem
rem  NOTE: keep this file pure ASCII. cmd.exe reads .bat as the OEM codepage and
rem  will execute multi-byte comments as commands ("'heck.exe' is not recognized"
rem  when "rtcheck.exe" gets its first two bytes eaten).
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "DIST=%ROOT%\dist"
set "LITE=%ROOT%\..\EternityCraftLauncher-lite"
set "SINGLE=%ROOT%\..\EternityCraftLauncher-single"
set "SETUP=%ROOT%\..\EternityCraftLauncher-setup"
set "BIN=%ROOT%\src\ECL.Launcher\bin\Release\net10.0-windows"
set "PROJ=%ROOT%\src\ECL.Launcher\ECL.Launcher.csproj"
set "NATIVE=%ROOT%\build\native\ECL.Native.dll"
set "JAR=%ROOT%\build\ECL.Runtime.jar"
set "SELFTEST=%ROOT%\build\last-selftest.log"

set "DO_CLEAN=0"
set "DO_TEST=1"
set "KEEP_NATIVE=0"
set "DO_BUMP=1"
for %%a in (%*) do (
    if /i "%%~a"=="--clean"       set "DO_CLEAN=1"
    if /i "%%~a"=="--no-test"     set "DO_TEST=0"
    if /i "%%~a"=="--keep-native" set "KEEP_NATIVE=1"
    if /i "%%~a"=="--no-bump"     set "DO_BUMP=0"
)

echo.
echo ============================================================
echo   ECL full rebuild
echo   root  : %ROOT%
echo   clean : %DO_CLEAN%   test : %DO_TEST%   native : %KEEP_NATIVE% (1 = reuse)
echo   bump  : %DO_BUMP%   (0 = version unchanged)
echo ============================================================
echo.

rem ----------------------------------------------------------------- bump ---
rem Bumped before anything is compiled, so the new number is in the binaries.
rem A failed build therefore still consumes a number -- use --no-bump while
rem iterating on a fix, and let the final build do the bump.
echo [bump] version...
if "%DO_BUMP%"=="1" (
    call "%ROOT%\scripts\version.bat" -Bump
    if errorlevel 1 (
        echo [FAIL] version bump failed
        goto :fail
    )
) else (
    call "%ROOT%\scripts\version.bat"
    echo       ^(not advanced, --no-bump given^)
)
echo.

rem ---------------------------------------------------------------- clean ---
echo [1/9] clean...
if "%DO_CLEAN%"=="1" (
    if exist "%ROOT%\src\ECL.Launcher\bin" rmdir /s /q "%ROOT%\src\ECL.Launcher\bin"
    if exist "%ROOT%\src\ECL.Launcher\obj" rmdir /s /q "%ROOT%\src\ECL.Launcher\obj"
    if exist "%ROOT%\build\javaout"        rmdir /s /q "%ROOT%\build\javaout"
    echo       bin, obj and javaout deleted
) else (
    echo       skipped ^(pass --clean to force^)
)

rem --------------------------------------------------------- native core ---
echo [2/9] native core (C++)...
if "%KEEP_NATIVE%"=="1" (
    if not exist "%NATIVE%" (
        echo [FAIL] --keep-native given but %NATIVE% is missing
        goto :fail
    )
    echo       reused existing ECL.Native.dll
) else (
    call "%ROOT%\scripts\build_native.bat"
    if errorlevel 1 (
        echo [FAIL] native core build failed
        goto :fail
    )
)
if not exist "%NATIVE%" (
    echo [FAIL] %NATIVE% does not exist
    goto :fail
)

rem -------------------------------------------------------- java runtime ---
rem The jar is wired into the csproj as an EmbeddedResource, so a stale jar here
rem silently ships an old probe inside the single-file exe. Rebuild it first.
echo [3/9] java runtime (ECL.Runtime.jar)...
where javac >nul 2>&1
if errorlevel 1 (
    echo [FAIL] javac was not found on PATH. Install a JDK 8+ and retry.
    goto :fail
)
if exist "%ROOT%\build\javaout" rmdir /s /q "%ROOT%\build\javaout"
mkdir "%ROOT%\build\javaout"

pushd "%ROOT%"
javac --release 8 -encoding UTF-8 -d build\javaout src\ECL.Runtime\java\ecl\runtime\*.java
set "JAVAC_RC=%errorlevel%"
popd
if not "%JAVAC_RC%"=="0" (
    echo [FAIL] javac failed
    goto :fail
)

pushd "%ROOT%\build\javaout"
if exist "..\ECL.Runtime.jar" del /q "..\ECL.Runtime.jar"
jar cfe ..\ECL.Runtime.jar ecl.runtime.Probe .
set "JAR_RC=%errorlevel%"
popd
if not "%JAR_RC%"=="0" (
    echo [FAIL] jar packaging failed
    goto :fail
)

if not exist "%JAR%" (
    echo [FAIL] jar packaging left no ECL.Runtime.jar
    goto :fail
)
set "JSIZE=0"
for %%f in ("%JAR%") do set "JSIZE=%%~zf"
if !JSIZE! LSS 2000 (
    echo [FAIL] ECL.Runtime.jar is only !JSIZE! bytes - packaging produced an empty archive
    goto :fail
)
echo       build\ECL.Runtime.jar !JSIZE! bytes

rem --------------------------------------------------------- dotnet build ---
echo [4/9] dotnet build...
dotnet build "%PROJ%" -c Release -v q --nologo
if errorlevel 1 goto :fail
if not exist "%BIN%\ECL.exe" (
    echo [FAIL] build produced no ECL.exe at %BIN%
    goto :fail
)

rem MSBuild's incremental copy is unreliable for these two (it once kept a stale
rem 24-byte jar), so drop them into the output folder explicitly.
copy /y "%NATIVE%" "%BIN%\ECL.Native.dll" >nul || goto :fail
copy /y "%JAR%"    "%BIN%\ECL.Runtime.jar" >nul || goto :fail

rem ------------------------------------------------------- native tools ---
echo [5/9] native tools (rtcheck, eclcr, eclnfer)...
if exist "%ROOT%\tools\rtcheck\build_manual.bat" (
    pushd "%ROOT%\tools\rtcheck"
    call build_manual.bat >nul
    set "RC=!errorlevel!"
    popd
    if not "!RC!"=="0" (
        echo [FAIL] rtcheck build failed
        goto :fail
    )
) else (
    echo       rtcheck build script not found, keeping existing exe
)

if exist "%ROOT%\tools\reporter\build.bat" (
    pushd "%ROOT%\tools\reporter"
    call build.bat >nul
    set "RC=!errorlevel!"
    popd
    if not "!RC!"=="0" (
        echo [FAIL] reporter build failed
        goto :fail
    )
) else (
    echo       reporter build script not found, keeping existing exe
)

rem ---------------------------------------------------------------- dist ---
echo [6/9] dist + dist.zip...
if exist "%DIST%" rmdir /s /q "%DIST%" >nul 2>&1
if not exist "%DIST%" mkdir "%DIST%"
if exist "%DIST%\ECL.exe" (
    echo [FAIL] dist is locked - close the running launcher and retry
    goto :fail
)

copy /y "%BIN%\ECL.exe"                "%DIST%\" >nul || goto :fail
copy /y "%BIN%\ECL.dll"                "%DIST%\" >nul || goto :fail
copy /y "%BIN%\ECL.deps.json"          "%DIST%\" >nul || goto :fail
copy /y "%BIN%\ECL.runtimeconfig.json" "%DIST%\" >nul || goto :fail
copy /y "%NATIVE%"                     "%DIST%\ECL.Native.dll" >nul || goto :fail
copy /y "%JAR%"                        "%DIST%\ECL.Runtime.jar" >nul || goto :fail
if exist "%ROOT%\tools\reporter\eclcr.exe"   copy /y "%ROOT%\tools\reporter\eclcr.exe"   "%DIST%\" >nul
if exist "%ROOT%\tools\reporter\eclnfer.exe" copy /y "%ROOT%\tools\reporter\eclnfer.exe" "%DIST%\" >nul

rem tar parses "-f F:\path" as the remote spec host:path ("Cannot connect to F:"),
rem so run from the project root and pass relative paths only.
pushd "%ROOT%"
if exist "dist.zip" del /q "dist.zip"
tar -a -c -f dist.zip -C dist .
set "RC=!errorlevel!"
popd
if not "!RC!"=="0" (
    echo [FAIL] could not create dist.zip
    goto :fail
)

rem ------------------------------------------------------- publish lite ---
echo [7/9] publishing lite (obfuscated, framework-dependent)...
if not exist "%LITE%" mkdir "%LITE%"
if exist "%ROOT%\build\sf-fd" rmdir /s /q "%ROOT%\build\sf-fd"
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Obfuscate=true -o "%ROOT%\build\sf-fd" -v q --nologo
if errorlevel 1 (
    echo [FAIL] lite publish failed
    goto :fail
)
if not exist "%ROOT%\build\sf-fd\ECL.exe" (
    echo [FAIL] lite publish produced no exe
    goto :fail
)
rem the single file carries the jar as an embedded resource; a loose copy next to
rem it would be preferred at runtime and could drift from the embedded one
del /q "%ROOT%\build\sf-fd\ECL.Runtime.jar" >nul 2>&1
copy /y "%ROOT%\build\sf-fd\ECL.exe" "%LITE%\ECL.exe" >nul || goto :fail

rem ----------------------------------------------------- publish single ---
echo [8/9] publishing single (obfuscated, self-contained)...
if not exist "%SINGLE%" mkdir "%SINGLE%"
if exist "%ROOT%\build\singlefile-obf" rmdir /s /q "%ROOT%\build\singlefile-obf"
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Obfuscate=true -o "%ROOT%\build\singlefile-obf" -v q --nologo
if errorlevel 1 (
    echo [FAIL] single publish failed
    goto :fail
)
if not exist "%ROOT%\build\singlefile-obf\ECL.exe" (
    echo [FAIL] single publish produced no exe
    goto :fail
)
del /q "%ROOT%\build\singlefile-obf\ECL.Runtime.jar" >nul 2>&1
copy /y "%ROOT%\build\singlefile-obf\ECL.exe" "%SINGLE%\ECL.exe" >nul || goto :fail

rem -------------------------------------------------------------- setup ---
echo [9/9] rebuilding ECL-Setup.exe (embeds the lite build)...
if not exist "%SETUP%" mkdir "%SETUP%"
pushd "%ROOT%\tools\sfx"
call build.bat >nul
set "RC=!errorlevel!"
popd
if not "!RC!"=="0" (
    echo [FAIL] setup build failed
    goto :fail
)
copy /y "%ROOT%\tools\sfx\ECL-Setup.exe"        "%SETUP%\ECL-Setup.exe" >nul || goto :fail
copy /y "%ROOT%\tools\rtcheck\rtcheck.exe"      "%SETUP%\rtcheck.exe" >nul 2>&1
copy /y "%ROOT%\tools\reporter\eclcr.exe"       "%SETUP%\eclcr.exe" >nul 2>&1
copy /y "%ROOT%\tools\reporter\eclnfer.exe"     "%SETUP%\eclnfer.exe" >nul 2>&1

rem ------------------------------------------------------------- verify ---
echo.
echo [verify] headless self-test...
if "%DO_TEST%"=="1" (
    if exist "%SINGLE%\ECL.exe" (
        "%SINGLE%\ECL.exe" --self-test --log "%SELFTEST%"
        rem find is NOT used to count lines: Git Bash puts GNU find.exe on PATH,
        rem so a piped count would recurse over C: and the build would look hung
        rem behind a wall of Permission denied. findstr alone is enough.
        set "PASSED=0"
        set "FAILED=0"
        if exist "%SELFTEST%" (
            for /f "delims=" %%l in ('findstr /b /c:"PASS" "%SELFTEST%"') do set /a PASSED+=1
            for /f "delims=" %%l in ('findstr /b /c:"FAIL" "%SELFTEST%"') do set /a FAILED+=1
        )
        echo       !PASSED! passed, !FAILED! failed  ^(build\last-selftest.log^)
        if not "!FAILED!"=="0" (
            echo       failing checks:
            findstr /b /c:"FAIL" "%SELFTEST%"
        )
        if not exist "%SELFTEST%" (
            echo       [WARN] self-test produced no log
        )
    ) else (
        echo       skipped, no single-file build
    )
) else (
    echo       skipped ^(--no-test^)
)

echo.
echo ============================================================
echo   done - artifacts
echo ============================================================
for %%f in ("%NATIVE%" "%JAR%" "%DIST%\ECL.dll" "%ROOT%\dist.zip" "%LITE%\ECL.exe" "%SINGLE%\ECL.exe" "%SETUP%\ECL-Setup.exe" "%ROOT%\tools\reporter\eclcr.exe" "%ROOT%\tools\reporter\eclnfer.exe" "%ROOT%\tools\rtcheck\rtcheck.exe") do (
    if exist "%%~f" (
        echo   %%~zf bytes  %%~nxf
    ) else (
        echo   MISSING        %%~nxf
    )
)
echo.
echo   installer: %SETUP%\ECL-Setup.exe
echo.
goto :eof

:fail
echo.
echo ============================================================
echo   BUILD FAILED
echo ============================================================
exit /b 1
