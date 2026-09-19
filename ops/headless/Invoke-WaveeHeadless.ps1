<#
.SYNOPSIS
  Run one headless Wavee script (ops/headless/*.wh) and return its exit code.

.DESCRIPTION
  Launches Wavee.exe --headless --script <file> the way a WinExe must be launched to be waited on
  (Start-Process -NoNewWindow, the process handle held, WaitForExit), captures stdout (one JSON object per line)
  into a .jsonl file under artifacts\headless, prints the step / fault / verdict lines, and exits with the app's
  own exit code:

    0 ok   1 fault   2 assertion   3 host error   64 usage   67 no stored credential   69 no audio endpoint
    75 login timeout   77 credential rejected (the credential is KEPT)   78 profile/config

  Refuses to run without -Profile when a packaged Wavee (cproducts.Wavee) is installed: an unpackaged run writes
  the literal %LOCALAPPDATA%\Wavee, which must never linger while a packaged build is under test (CLAUDE.md).
  A -Profile run has no credential unless one was saved into that profile.

  Plan: docs/plans/wavee/wavee-0.3-headless-implementation.md section 4. Run by the orchestrator only.
  Windows PowerShell 5.1 compatible.

.EXAMPLE
  powershell -File ops\headless\Invoke-WaveeHeadless.ps1 ops\headless\login-smoke.wh
.EXAMPLE
  powershell -File ops\headless\Invoke-WaveeHeadless.ps1 parse-only.wh -NoLogin -Profile $env:TEMP\wavee-hl
.EXAMPLE
  powershell -File ops\headless\Invoke-WaveeHeadless.ps1 ogg320.wh -Configuration Release -EchoLog
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Script,
    [string]$Exe = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [Alias('Profile')]
    [string]$ProfileDir = '',
    [switch]$Silent,
    [switch]$Store,
    [switch]$Connect,
    [switch]$NoLogin,
    [switch]$EchoLog,
    [int]$TimeoutSec = 300,
    [int]$LoginTimeoutSec = 0,
    [string]$OutFile = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Get-ExitMeaning([int]$exitCode) {
    switch ($exitCode) {
        0  { return 'ok' }
        1  { return 'fault' }
        2  { return 'assertion' }
        3  { return 'host error' }
        64 { return 'usage' }
        67 { return 'no stored credential' }
        69 { return 'no audio endpoint' }
        75 { return 'login timeout' }
        77 { return 'credential rejected (kept)' }
        78 { return 'profile/config' }
        default { return 'quit ' + $exitCode }
    }
}

function ConvertTo-ArgText([string]$argValue) {
    # One argument for CreateProcess: quote it when it has spaces or quotes; a trailing backslash inside quotes would
    # escape the closing quote, so directory values arrive without one.
    if ($argValue -notmatch '[\s"]') { return $argValue }
    return '"' + ($argValue -replace '"', '\"') + '"'
}

function Test-PackagedWavee {
    $found = $false
    try {
        $packages = @(Get-AppxPackage -Name 'cproducts.Wavee*' -ErrorAction Stop)
        if ($packages.Count -gt 0) { $found = $true }
    } catch {
        # Get-AppxPackage is not loadable in every host (PowerShell 7 without the Appx module): fall back to the
        # package data folder every installed package owns.
        $packagesRoot = Join-Path $env:LOCALAPPDATA 'Packages'
        if (Test-Path -LiteralPath $packagesRoot) {
            $dirs = @(Get-ChildItem -LiteralPath $packagesRoot -Filter 'cproducts.Wavee_*' -Directory -ErrorAction SilentlyContinue)
            if ($dirs.Count -gt 0) { $found = $true }
        }
    }
    return $found
}

# -- the script ---------------------------------------------------------------------------------------------------------
$scriptPath = $Script
if (-not (Test-Path -LiteralPath $scriptPath)) {
    $candidate = Join-Path $PSScriptRoot $Script
    if (Test-Path -LiteralPath $candidate) { $scriptPath = $candidate }
}
if (-not (Test-Path -LiteralPath $scriptPath)) { throw ('Script not found: ' + $Script) }
$scriptPath = (Resolve-Path -LiteralPath $scriptPath).Path
$scriptName = [IO.Path]::GetFileNameWithoutExtension($scriptPath)

# -- the exe ------------------------------------------------------------------------------------------------------------
if (-not $Exe) {
    $binRoot = Join-Path $repoRoot ('src\apps\Wavee\bin\' + $Configuration + '\net10.0')
    foreach ($leaf in @('Wavee.exe', 'win-arm64\Wavee.exe', 'win-x64\Wavee.exe')) {
        $candidate = Join-Path $binRoot $leaf
        if (Test-Path -LiteralPath $candidate) { $Exe = $candidate; break }
    }
    if (-not $Exe) { throw ('Wavee.exe not found under ' + $binRoot + ' - build first (dotnet build Wavee.slnx -c ' + $Configuration + ').') }
}
if (-not (Test-Path -LiteralPath $Exe)) { throw ('Wavee.exe not found: ' + $Exe) }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

# -- the profile rule (plan section 2.8 rule 5) ---------------------------------------------------------------------------
if (-not $ProfileDir) {
    if (Test-PackagedWavee) {
        Write-Host 'A packaged Wavee (cproducts.Wavee) is installed on this machine.' -ForegroundColor Red
        Write-Host 'An unpackaged headless run would write %LOCALAPPDATA%\Wavee, which must never linger while a packaged' -ForegroundColor Red
        Write-Host 'build is under test (CLAUDE.md). Pass -Profile <dir>; that profile needs its own signed-in credential.' -ForegroundColor Red
        exit 78
    }
} else {
    if (-not (Test-Path -LiteralPath $ProfileDir)) { New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null }
    $ProfileDir = (Resolve-Path -LiteralPath $ProfileDir).Path.TrimEnd('\')
}

# -- the capture --------------------------------------------------------------------------------------------------------
if (-not $OutFile) {
    $outDir = Join-Path $repoRoot 'artifacts\headless'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $OutFile = Join-Path $outDir ($scriptName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.jsonl')
}
$errFile = [IO.Path]::ChangeExtension($OutFile, '.stderr.txt')

$argParts = New-Object System.Collections.Generic.List[string]
$argParts.Add('--headless')
$argParts.Add('--script'); $argParts.Add((ConvertTo-ArgText $scriptPath))
if ($TimeoutSec -gt 0) { $argParts.Add('--timeout'); $argParts.Add([string]($TimeoutSec * 1000)) }
if ($LoginTimeoutSec -gt 0) { $argParts.Add('--login-timeout'); $argParts.Add([string]($LoginTimeoutSec * 1000)) }
if ($ProfileDir) { $argParts.Add('--profile'); $argParts.Add((ConvertTo-ArgText $ProfileDir)) }
if ($Silent) { $argParts.Add('--silent') }
if ($Store) { $argParts.Add('--store') }
if ($Connect) { $argParts.Add('--connect') }
if ($NoLogin) { $argParts.Add('--no-login') }
if ($EchoLog) { $argParts.Add('--echo-log') }
$argText = [string]::Join(' ', $argParts.ToArray())

Write-Host ('==> ' + $scriptName + ': ' + $Exe + ' ' + $argText) -ForegroundColor Cyan
$proc = Start-Process -FilePath $Exe -ArgumentList $argText -NoNewWindow -PassThru `
    -RedirectStandardOutput $OutFile -RedirectStandardError $errFile
$null = $proc.Handle   # hold the handle, or Windows PowerShell 5.1 reports no ExitCode

$exitCode = 3
if ($TimeoutSec -gt 0) {
    # The app enforces --timeout itself; this is the backstop for a host that cannot even do that.
    if (-not $proc.WaitForExit(($TimeoutSec + 60) * 1000)) {
        Write-Host ('Wavee did not exit within ' + ($TimeoutSec + 60) + ' s - killing it.') -ForegroundColor Red
        try { $proc.Kill() } catch { }
        $proc.WaitForExit()
    } else {
        $proc.WaitForExit()
        $exitCode = $proc.ExitCode
    }
} else {
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
}

# -- the report ---------------------------------------------------------------------------------------------------------
if (Test-Path -LiteralPath $OutFile) {
    Get-Content -LiteralPath $OutFile | Where-Object { $_ -match '"kind":"(step|verdict|fault)"' } | ForEach-Object {
        $color = 'Gray'
        if ($_ -match '"ok":false' -or $_ -match '"kind":"fault"') { $color = 'Yellow' }
        if ($_ -match '"kind":"verdict"') { if ($exitCode -eq 0) { $color = 'Green' } else { $color = 'Red' } }
        Write-Host $_ -ForegroundColor $color
    }
}
if ((Test-Path -LiteralPath $errFile) -and ((Get-Item -LiteralPath $errFile).Length -gt 0)) {
    Write-Host '--- stderr ---' -ForegroundColor Yellow
    Get-Content -LiteralPath $errFile | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}
Write-Host ('exit ' + $exitCode + ' (' + (Get-ExitMeaning $exitCode) + ')  capture: ' + $OutFile)
exit $exitCode
