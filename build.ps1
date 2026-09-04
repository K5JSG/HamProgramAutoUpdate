<#
.SYNOPSIS
    Builds the Ham Program Auto Update.

.DESCRIPTION
    Publishes a single self-contained exe (no .NET runtime needed on the
    target PC) and, if Inno Setup is installed, compiles the installer.

.PARAMETER Version
    Version stamped into the exe and the installer filename.

.PARAMETER SkipInstaller
    Publish the exe only; do not build the installer.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 1.1.0
#>

[CmdletBinding()]
param(
    [string]$Version = "1.3.3",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "HamProgramAutoUpdate.csproj"
$publishDir = Join-Path $root "publish"
$distDir = Join-Path $root "dist"

Write-Host ""
Write-Host "Ham Program Auto Update - build v$Version" -ForegroundColor Cyan
Write-Host ("=" * 55)

# --- 1. Publish ------------------------------------------------------------
#
# CHIRP's updater runs in-process now (Services\Updaters\Programs\Chirp\
# ChirpCloudflareAutomation.cs) - nothing extra needs building for it. Its
# learned captcha coordinates ship straight from
# ChirpUpdaterSource\captcha_coords.json via the csproj (see the ItemGroup
# comment there). The old Python implementation that folder also still holds
# (build_chirp.bat etc.) is a local-only, unwired fallback kept out of git -
# not part of this build - see ChirpUpdaterSource\README.txt.

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host ""
Write-Host "Publishing self-contained exe..." -ForegroundColor Yellow

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:FileVersion="$Version.0" `
    -p:AssemblyVersion="$Version.0" `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "HamProgramAutoUpdate.exe"
if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "  $exe  ($sizeMb MB)" -ForegroundColor Green

# --- 2. Version-stamped standalone copy -------------------------------------
#
# The exe itself is always named HamProgramAutoUpdate.exe (the installer expects
# that fixed name). For people who just grab the standalone exe with no
# installer, also drop a copy in dist\ whose filename carries the version, so
# it is identifiable on its own once it is sitting in a downloads folder.
#
# The version is read back from the exe's own FileVersion rather than trusted
# from the -Version parameter, so the filename always matches what was
# actually compiled in.

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# Clear out prior versions' standalone exe / installer so dist\ never holds
# more than the build that's about to be produced.
Get-ChildItem -Path $distDir -Filter "HamProgramAutoUpdate-*.exe" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$builtVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if (-not $builtVersion) {
    Write-Host "Warning: could not read FileVersion from the published exe; using $Version instead." -ForegroundColor Yellow
    $builtVersion = "$Version.0"
}
$standaloneVersion = ($builtVersion -split '\.')[0..2] -join '.'

$standaloneExe = Join-Path $distDir "HamProgramAutoUpdate-v$standaloneVersion.exe"
if (Test-Path $standaloneExe) { Remove-Item $standaloneExe -Force }
Copy-Item $exe $standaloneExe

Write-Host "  $standaloneExe" -ForegroundColor Green

# --- 3. Installer ----------------------------------------------------------

if ($SkipInstaller) {
    Write-Host ""
    Write-Host "Skipping installer (-SkipInstaller)." -ForegroundColor DarkGray
    exit 0
}

$iscc = @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup was not found, so no installer was built." -ForegroundColor Yellow
    Write-Host "Install it from https://jrsoftware.org/isdl.php and run this again," -ForegroundColor Yellow
    Write-Host "or just distribute publish\HamProgramAutoUpdate.exe on its own." -ForegroundColor Yellow
    exit 0
}

Write-Host ""
Write-Host "Building installer..." -ForegroundColor Yellow

& $iscc "/DMyAppVersion=$Version" (Join-Path $root "installer\HamProgramAutoUpdate.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$setup = Join-Path $distDir "HamProgramAutoUpdate-v$Version-setup.exe"
if (Test-Path $setup) {
    $setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host ""
    Write-Host "  $setup  ($setupMb MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
Write-Host ""
