# measure-page-nav.ps1
#
# Captures a Windows Performance Recorder trace of a Wavee page-navigation scenario
# using CPU + XAMLAppResponsiveness + GPU profiles, then opens it in Windows
# Performance Analyzer
# (WPA) so you can inspect frame timings via the XAML Frame Analysis table.
#
# PREREQUISITES
# =============
#
# 1. Run from an ELEVATED PowerShell. WPR needs SeSystemProfilePrivilege which
#    normal users do not have. Right-click PowerShell and choose "Run as administrator".
#
# 2. (Optional but recommended) Enable the XAML Frame Analysis plugin in WPA by
#    running scripts\enable-xaml-frame-analysis.ps1 once. Without it, you can still
#    see the raw XAML events in WPA's "Generic Events" table but without the
#    pre-computed frame durations.
#
# USAGE
# =====
#
#   .\scripts\measure-page-nav.ps1 -Scenario baseline
#   .\scripts\measure-page-nav.ps1 -Scenario after-fix-A
#
# The scenario name is used to tag the trace file so you can compare runs.

param(
    [string]$Scenario = "baseline",
    [string]$OutDir = "perf-traces"
)

# Use explicit exit-code checks instead of -ErrorActionPreference Stop so that
# native commands writing benign messages to stderr (e.g. "no recording in
# progress") don't abort the script.
$ErrorActionPreference = "Continue"

# -- Sanity checks ------------------------------------------------------

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run from an elevated PowerShell window (Run as administrator)."
    exit 1
}

$wpr = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpr.exe"
$wpa = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe"
if (-not (Test-Path $wpr)) {
    Write-Error "wpr.exe not found at $wpr. Install the Windows Assessment Toolkit (ADK) 10.1.26100.1+."
    exit 1
}

# Create output directory relative to the repo root (one level up from scripts/)
$repoRoot = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $repoRoot $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$etl = Join-Path $outPath "$Scenario-$timestamp.etl"

# -- Record -------------------------------------------------------------

Write-Host ""
Write-Host "===== Wavee page-navigation measurement =====" -ForegroundColor Cyan
Write-Host "Scenario: $Scenario" -ForegroundColor Cyan
Write-Host "Output:   $etl" -ForegroundColor Cyan
Write-Host ""

# Layer in Wavee's custom providers (Wavee-UI-Navigation etc.) on top of the
# built-in profiles. The custom .wprp registers our EventSource GUID so wpaexporter
# can surface Navigating/Navigated pairs in the Generic Events table, which
# analyze-trace.ps1 pairs into NavigationE2E rows.
$customProfile = Join-Path $repoRoot "scripts\wpa-profiles\WaveeCustomProviders.wprp"
$wprStartArgs = @('-start', 'CPU', '-start', 'XAMLAppResponsiveness', '-start', 'GPU')
if (Test-Path $customProfile) {
    $wprStartArgs += @('-start', "$customProfile!WaveeCustomProviders")
    Write-Host "Starting ETW recording (CPU + XAMLAppResponsiveness + GPU + WaveeCustomProviders)..." -ForegroundColor Yellow
}
else {
    Write-Host "Starting ETW recording (CPU + XAMLAppResponsiveness + GPU)..." -ForegroundColor Yellow
    Write-Host "Warning: $customProfile not found - custom Wavee providers will NOT be captured." -ForegroundColor Yellow
    Write-Host "NavigationE2E metrics will read as 0 without these providers." -ForegroundColor Yellow
}
# XAMLAppResponsiveness is a superset of XAMLActivity that captures the rich
# event set the WPA "XAML App Responsiveness Analysis" profile expects (frame
# navigating/navigated timestamps, layout passes, focus changes, touch input
# correlated with frames, etc.). With plain XAMLActivity, most of those tables
# come out empty in the analysis.
& $wpr @wprStartArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "WPR failed to start. Exit code: $LASTEXITCODE" -ForegroundColor Red
    Write-Host "If a previous run left a stale recording, cancel it first with:" -ForegroundColor Yellow
    Write-Host "    wpr -cancel" -ForegroundColor Gray
    exit 1
}

try {
    Write-Host ""
    Write-Host "RECORDING IS LIVE." -ForegroundColor Green
    Write-Host ""
    Write-Host "  1. Launch Wavee and sign in if needed." -ForegroundColor White
    Write-Host "  2. Wait for the Home page to fully load." -ForegroundColor White
    Write-Host "  3. Navigate the scenario you want to measure. Suggested:" -ForegroundColor White
    Write-Host "       - Click Search, click Library, click a playlist, click Home." -ForegroundColor Gray
    Write-Host "  4. Stop when the last page has finished loading." -ForegroundColor White
    Write-Host ""
    Read-Host "Press Enter when the scenario is complete"
}
finally {
    Write-Host ""
    Write-Host "Stopping recording and writing ETL..." -ForegroundColor Yellow
    & $wpr -stop $etl "Wavee page nav scenario: $Scenario"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to stop WPR recording. Exit code: $LASTEXITCODE"
        exit 1
    }
}

Write-Host ""
Write-Host "Trace saved: $etl" -ForegroundColor Green
$fileSize = (Get-Item $etl).Length / 1MB
Write-Host ("Size: {0:N1} MB" -f $fileSize) -ForegroundColor Gray

# -- Open in WPA --------------------------------------------------------

if (Test-Path $wpa) {
    Write-Host ""
    Write-Host "Opening in Windows Performance Analyzer..." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "WHAT TO LOOK FOR:" -ForegroundColor Cyan
    Write-Host "  - If perf_xaml.dll is enabled in perfcore.ini: open System Activity > XAML Frame Analysis." -ForegroundColor White
    Write-Host "    Switch the view to 'All Xaml Info' and sort by Duration (ms)." -ForegroundColor White
    Write-Host "    Look for the 'Frame' rows around a 'Frame::Navigated' event - long ones are slow nav." -ForegroundColor White
    Write-Host "  - Otherwise: open System Activity > Generic Events and filter Provider by 'Microsoft-Windows-XAML'." -ForegroundColor White
    Write-Host "    Look for 'FrameNavigatingStart' and 'FrameNavigatedStop' event pairs." -ForegroundColor White
    Write-Host "  - For broad CPU usage, also open Computation > CPU Usage (Sampled) > Thread Activity." -ForegroundColor White
    Write-Host ""
    & $wpa $etl
}
else {
    Write-Host ""
    Write-Host "WPA not found at $wpa" -ForegroundColor Yellow
    Write-Host "Open the ETL manually: $etl" -ForegroundColor Yellow
}
