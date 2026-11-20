#requires -Version 5.1
<#
.SYNOPSIS
  Wavee's release runner: one command for a whole release, a dry run unless -Go.

.DESCRIPTION
  A plain run is a dry run: the preflight (read-only) and the commands every step would run; nothing changes.
  -Go acts. Every step decides from reality (git, GitHub, the feed, the staged files) whether it is already done, so a
  second -Go continues where the first one stopped. A -Go started from any checkout makes (or reuses) the release
  worktree C:\WAVEE\release\<version>\WaveeMusic (origin/release/<version>) and C:\WAVEE\release\<version>\fluent-gpu
  (engine.lock), links the PlayPlay junction and the signing config in, and runs the worktree's own release.ps1.

  The steps, in order: preflight, changelog, bump, notes, tag, pack-arm64, pack-x64, symbols, sign, stage, push,
  release, feed, open-next, merge-main, forward-port (a hotfix only), store-submit, notify, store-poll.

  release.ps1 -Version 0.3.12                           dry run: the preflight and the commands of every step
  release.ps1 -Version 0.3.12 -Go                       run every step that is not done, in order
  release.ps1 -Version 0.3.12 -Go -To changelog         stop after a step (-From, -To, -Only take step names)
  release.ps1 -Version 0.3.12 -Go -PauseBefore feed     stop before a step (exit 4); the next -Go with the same
                                                        -PauseBefore continues there and pauses at the next listed one
  release.ps1 -Version 0.3.12 -Status [-Json]           what is done, what is next (read-only)
  release.ps1 -Version 0.3.12 -Preflight -Json          only the preflight, as JSON (read-only)
  release.ps1 -Version 0.4.0 -Go -Codename Drift        a new minor needs its codename
  release.ps1 -Hotfix 0.3.12 [-Go]                      rename the open release/0.3.12 to release/0.3.13 and make
                                                        release/0.3.12 from the last shipped tag
  release.ps1 -RetargetPrs [-Go]                        move open PRs on main (or a shipped release/*) to the open
                                                        release branch, one comment each
  release.ps1 -RepointFeed 0.3.11 -AllowDowngrade [-Go] point wavee-stable back at an older release
  release.ps1 -Version 0.3.12 -Abort [-Go]              before the push step only: delete the local tags and the
                                                        runner's local commits in the release worktree

  Also: -PublicOnly (build without PlayPlay), -SkipArch arm64|x64, -X64Msix <prebuilt.msix>, -PollMinutes <n> (how
  long store-poll waits, default 15).

  Exit codes: 0 done, 1 usage or config, 2 preflight refused (nothing changed), 3 stopped on a condition, 4 paused.
  The state is in %LOCALAPPDATA%\WaveeRelease\<version> (state.json, one log per step, runner.lock). Every -Go run
  ends with a Windows toast on this PC naming the version, the exit code and the step.

.LINK
  docs\guide\releasing-wavee.md
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Go,
    [string]$From,
    [string]$To,
    [string[]]$Only,
    [string[]]$PauseBefore,
    [switch]$Status,
    [switch]$Json,
    [switch]$Preflight,
    [string]$Codename,
    [string]$Hotfix,
    [switch]$RetargetPrs,
    [string]$RepointFeed,
    [switch]$AllowDowngrade,
    [switch]$Abort,
    [switch]$PublicOnly,
    [string]$SkipArch,
    [string]$X64Msix,
    [int]$PollMinutes = 15,
    # Internal: the outer -Go passes it when it relaunches this script inside the release worktree.
    [switch]$InWorktree
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseRunner.psm1') -Force -DisableNameChecking

$code = 1
try {
    # The last object is the exit code; anything a step let slip into the pipeline before it is ignored.
    $out = @(Invoke-WaveeRelease -ScriptRoot $PSScriptRoot @PSBoundParameters)
    $code = 3
    if ($out.Count -gt 0 -and "$($out[-1])" -match '^\d+$') { $code = [int]$out[-1] }
}
catch {
    if ($Json) { [Console]::Error.WriteLine("STOPPED: $($_.Exception.Message)") }
    else { Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red }
    $code = 3
}
finally {
    # Frees runner.lock, records the end of the run and shows the toast (a toast that fails changes nothing).
    $code = Complete-WaveeRelease -ExitCode $code
}
exit $code
