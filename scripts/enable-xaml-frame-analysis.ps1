# enable-xaml-frame-analysis.ps1
#
# One-time setup: enables the XAML Frame Analysis plugin in Windows Performance
# Analyzer by adding "perf_xaml.dll" to perfcore.ini. After this runs, WPA will
# display the "XAML Frame Analysis" table in the System Activity section, which
# computes Frame::Navigating -> Frame::Navigated durations, UpdateLayout times,
# and "Region of Interest" latencies from any trace that includes the XAMLActivity
# profile.
#
# PREREQUISITES
# =============
# - Run from an ELEVATED PowerShell (perfcore.ini is under Program Files).
# - Windows Assessment Toolkit (ADK) 10.1.26100.1 or later must be installed.
#   The target file ships with the ADK; this script only enables it.
# - Close any open WPA instances before running (the plugin only loads at WPA startup).
#
# USAGE
# =====
#
#   .\scripts\enable-xaml-frame-analysis.ps1
#
# Idempotent - safe to run multiple times.

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run from an elevated PowerShell window (Run as administrator)."
    exit 1
}

$wptDir = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit"
$perfcore = Join-Path $wptDir "perfcore.ini"
$plugin = Join-Path $wptDir "perf_xaml.dll"

if (-not (Test-Path $perfcore)) {
    Write-Error "perfcore.ini not found at $perfcore. Install Windows ADK 10.1.26100.1 or later."
    exit 1
}

if (-not (Test-Path $plugin)) {
    Write-Error "perf_xaml.dll not found at $plugin. Your ADK version may be too old; upgrade to 10.1.26100.1+."
    exit 1
}

$content = Get-Content $perfcore
if ($content -contains "perf_xaml.dll") {
    Write-Host "perf_xaml.dll is already enabled in perfcore.ini." -ForegroundColor Green
    Write-Host "Restart WPA for the XAML Frame Analysis table to appear." -ForegroundColor Yellow
    exit 0
}

# Backup the original once
$backup = "$perfcore.bak"
if (-not (Test-Path $backup)) {
    Copy-Item $perfcore $backup
    Write-Host "Backed up original perfcore.ini to: $backup" -ForegroundColor Gray
}

Add-Content -Path $perfcore -Value "perf_xaml.dll"
Write-Host "Enabled perf_xaml.dll in perfcore.ini." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Close any open WPA instances." -ForegroundColor White
Write-Host "  2. Run .\scripts\measure-page-nav.ps1 -Scenario baseline" -ForegroundColor White
Write-Host "  3. In WPA, open System Activity > XAML Frame Analysis." -ForegroundColor White
