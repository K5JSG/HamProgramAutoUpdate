@echo off
setlocal

REM ============================================================
REM  Build script for Chirp Update Script
REM  - Recreates .\build and .\dist next to this .bat / .py
REM  - Builds the EXE from the .spec file into .\dist
REM  - Writes build_log.txt beside the .py file
REM
REM  Called automatically by build.ps1 (repo root) as part of the normal
REM  dashboard build, with AUTOBUILD=1 set so it never blocks on `pause`.
REM  Can still be run by hand for a standalone rebuild.
REM ============================================================

cd /d "%~dp0"

set "SPEC=Chirp Update Script.spec"
set "EXENAME=Chirp Update Script.exe"
set "LOG=%~dp0build_log.txt"

if not exist "%SPEC%" (
    echo ERROR: Cannot find "%SPEC%" in "%~dp0"
    echo Put this .bat in the same folder as the .py and .spec files.
    if not defined AUTOBUILD pause
    exit /b 1
)

REM --- Check PyInstaller is available -------------------------
python -m PyInstaller --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: PyInstaller is not installed for this Python.
    echo Install it with:  python -m pip install pyinstaller
    if not defined AUTOBUILD pause
    exit /b 1
)

REM --- Check the runtime deps are installed -------------------
REM  If these are missing at BUILD time, PyInstaller cannot bundle them and the
REM  exe dies at runtime with ModuleNotFoundError. Note these are the CURRENT
REM  dependencies - undetected_chromedriver is no longer used.
for %%M in (seleniumbase curl_cffi) do (
    python -c "import %%M" >nul 2>&1
    if errorlevel 1 (
        echo ERROR: Python module "%%M" is not installed for this Python.
        echo It cannot be bundled into the exe. Install it with:
        echo     python -m pip install %%M
        if not defined AUTOBUILD pause
        exit /b 1
    )
)

REM --- Warn if the learned checkbox coordinates are missing ----
if not exist "captcha_coords.json" (
    echo.
    echo WARNING: captcha_coords.json not found in this folder.
    echo The updater will fall back to built-in coordinates ^(117,332^), which
    echo only work at a 1100x700 window on a 1366x768 screen. To regenerate:
    echo     python coord_test.py learn
    echo.
)

REM --- Wipe and recreate build / dist -------------------------
echo Cleaning previous build...
if exist "build" rmdir /s /q "build"
if exist "dist"  rmdir /s /q "dist"
mkdir "build"
mkdir "dist"

REM --- Build --------------------------------------------------
echo Building "%EXENAME%" ... (SeleniumBase is large; allow several minutes)
echo Log: %LOG%
echo.

(
    echo ============================================================
    echo Build started %DATE% %TIME%
    echo Folder: %~dp0
    echo ============================================================
) > "%LOG%"

python -m PyInstaller --noconfirm --clean --distpath "dist" --workpath "build" "%SPEC%" >> "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"

echo. >> "%LOG%"
echo Exit code: %RC% >> "%LOG%"

if not "%RC%"=="0" (
    echo.
    echo ***** BUILD FAILED ^(exit code %RC%^) *****
    echo ---------------- last of build_log.txt ----------------
    powershell -NoProfile -Command "Get-Content -Path '%LOG%' -Tail 40"
    echo -------------------------------------------------------
    echo Full log: %LOG%
    if not defined AUTOBUILD pause
    exit /b %RC%
)

if not exist "dist\%EXENAME%" (
    echo.
    echo ***** PyInstaller reported success but "dist\%EXENAME%" is missing *****
    echo Check the log: %LOG%
    if not defined AUTOBUILD pause
    exit /b 1
)

echo.
echo BUILD SUCCEEDED
echo   EXE:   %~dp0dist\%EXENAME%
echo   Build: %~dp0build\
echo   Log:   %LOG%
echo.
echo NOTE: run the exe once by hand ^(as administrator^) before trusting it to
echo       Task Scheduler. SeleniumBase downloads its uc_driver on first use.
echo.
if not defined AUTOBUILD pause
endlocal
