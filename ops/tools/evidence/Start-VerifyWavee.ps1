#requires -Version 5.1
<#
.SYNOPSIS
  Start a VERIFY Wavee beside the owner's own (docs/plans/evidence-diagnostics-implementation.md §C.0, in ..\fluent-gpu).

.DESCRIPTION
  Runs a build of Wavee from its own --profile folder: its own settings.json (developer mode on — the wavee://diag verbs
  are developer-only), its own logs\evidence, library.db and cache. The build must be PROFILE-SCOPED (§A.7: the instance
  id is "Wavee.<hash of the profile>" and the window carries its tag) — then it neither hands its launch to the owner's
  Wavee nor receives theirs, and it never rewrites the owner's OS registrations (protocol handlers, start-on-login, toast
  activator, jump list). A build that is NOT profile-scoped hands off and exits: this script detects that and stops.

  store.json is copied from the owner's profile (the DPAPI credential blob decrypts for the same Windows user, so the
  verify instance is signed in) with its device.id REMOVED, so the verify instance mints its own Spotify Connect device
  id instead of impersonating the owner's device. It still appears as one more "Wavee" device in the account's Connect
  picker while it runs. Nothing here plays audio.

  Never stops a process. Launch from PowerShell, sandbox-free (the GPU is not visible inside the sandbox).

.PARAMETER ProfileDir   The verify profile folder (default C:\wavee\verify-profile).
.PARAMETER Exe          The verify build (default <repo>\src\apps\Wavee\bin\verify\evidence\Wavee.exe — build it with
                        dotnet build src\apps\Wavee\Wavee.csproj -c Release -o src\apps\Wavee\bin\verify\evidence).
.PARAMETER CopyLibrary  Also copy the owner's library.db (a warm cache). Omit for a cold-navigation run.
.PARAMETER RefreshStore Re-copy store.json even when the profile already has one.
.OUTPUTS  [pscustomobject] Pid, Hwnd, Profile, LogFolder, EvidenceRoot.
#>
[CmdletBinding()]
param(
    [string]$ProfileDir = 'C:\wavee\verify-profile',
    [string]$Exe,
    [switch]$CopyLibrary,
    [switch]$RefreshStore,
    [int]$X = 0, [int]$Y = 0, [int]$Width = 1600, [int]$Height = 1000,
    [string]$FgSwitches = 'gpu-timing'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Evidence.psm1') -Force -DisableNameChecking
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Exe) { $Exe = Join-Path $repo 'src\apps\Wavee\bin\verify\evidence\Wavee.exe' }
if (-not (Test-Path $Exe)) { throw "verify build not found: $Exe" }
$Exe = (Resolve-Path $Exe).Path

# A verify instance already running from this exe: reuse it rather than launching a second (it would hand off to it).
$mine = Get-Process Wavee -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe }
if ($mine) {
    $p = $mine | Select-Object -First 1
    $h = Wait-WindowByPid -ProcessId $p.Id -TimeoutSec 10
    $logs = Join-Path $ProfileDir 'logs'
    return [pscustomobject]@{ Pid = $p.Id; Hwnd = $h; Profile = $ProfileDir; LogFolder = $logs; EvidenceRoot = (Join-Path $logs 'evidence'); Reused = $true }
}

New-Item -ItemType Directory -Force $ProfileDir | Out-Null
$ownerDir = Join-Path $env:LOCALAPPDATA 'Wavee'

# store.json: the signed-in account, minus the owner's Connect device id.
$store = Join-Path $ProfileDir 'store.json'
if ($RefreshStore -or -not (Test-Path $store)) {
    $src = Join-Path $ownerDir 'store.json'
    if (-not (Test-Path $src)) { throw "no owner store.json at $src — sign in once in Wavee, or copy one into $ProfileDir" }
    $json = Get-Content -LiteralPath $src -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($json.PSObject.Properties.Name -contains 'device.id') { $json.PSObject.Properties.Remove('device.id') }
    [System.IO.File]::WriteAllText($store, ($json | ConvertTo-Json -Depth 8 -Compress), (New-Object System.Text.UTF8Encoding($false)))
}
if ($CopyLibrary) {
    # the database is library.<account hash>.db (+ its -wal/-shm): copy the set so the copy opens consistent
    foreach ($db in @(Get-ChildItem -LiteralPath $ownerDir -Filter 'library*.db*' -File -ErrorAction SilentlyContinue)) {
        Copy-Item -LiteralPath $db.FullName -Destination (Join-Path $ProfileDir $db.Name) -Force
    }
}

# settings.json (a string→string map, FileAppSettings): developer mode on — the diag verbs are developer-only.
$settingsPath = Join-Path $ProfileDir 'settings.json'
$settings = if (Test-Path $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($settings.PSObject.Properties.Name -contains 'diag.developerMode') { $settings.'diag.developerMode' = 'true' }
else { $settings | Add-Member -NotePropertyName 'diag.developerMode' -NotePropertyValue 'true' }
[System.IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))

$launchArgs = @('--profile', $ProfileDir)
if ($FgSwitches) { $launchArgs += @('--fg', $FgSwitches) }
$p = Start-Process -FilePath $Exe -ArgumentList $launchArgs -WorkingDirectory (Split-Path $Exe) -PassThru
$h = Wait-WindowByPid -ProcessId $p.Id -TimeoutSec 60
[void][EvidenceWin32]::MoveWindow($h, $X, $Y, $Width, $Height, $true)
$logs = Join-Path $ProfileDir 'logs'
[pscustomobject]@{ Pid = $p.Id; Hwnd = $h; Profile = $ProfileDir; LogFolder = $logs; EvidenceRoot = (Join-Path $logs 'evidence'); Reused = $false }
