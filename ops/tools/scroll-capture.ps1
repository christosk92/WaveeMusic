<#
.SYNOPSIS
  Capture the engine-side scroll pipeline of a DIAG build of Wavee while a human scrolls with the touchpad.

.DESCRIPTION
  Companion to ops/tools/scroll-input-probe (which measures the OS side OUTSIDE the engine). This one runs the
  FluentGpuDiag=true build of Wavee (ScrollTrace compiled in) with the engine's diagnostic env gates set, so one
  run yields, in one folder:

    fg-scroll.log        FG_SCROLL_LOG=1   - DM STATUS edges, DM STRIKE/DISABLED, FB BEGIN/UPDATE/END, WHEEL lines
    fg-scrolltrace.csv   FG_SCROLL_TRACE   - the per-frame Frame/Phase/Latch/Release/GestureEnd/Latency rows
    stderr.txt           FG_FPS_LOG=1      - one [fps] line per scroll-active frame incl. `wait <kind><ms> WxH@Hz`
    wavee-<date>.log     the app's always-on log (scroll.frames / nav.frames / frame.slow / session.frames)

  Those env vars are ENGINE diagnostic gates (FG_*), read at process start by the engine; the app itself has no
  environment switches (CLAUDE.md). They only do anything in a build made with -p:FluentGpuDiag=true:

    dotnet build src\apps\Wavee\Wavee.csproj -c Release -p:FluentGpuDiag=true -o src\apps\Wavee\bin\verify\diag

  Single-instance: a second Wavee.exe hands its launch to the running one and exits, so the script refuses to
  start while any Wavee.exe is alive - close the user's instance first (never Stop-Process it from a script).

.PARAMETER Seconds
  How long to capture before the app is asked to close (default 90). 0 = until the user closes the window.
.PARAMETER Exe
  The diag build to run. Defaults to src\apps\Wavee\bin\verify\diag\Wavee.exe under the repo root.
.PARAMETER OutDir
  Where the capture lands. Default: %TEMP%\wavee-scroll-capture-<timestamp>.
#>
[CmdletBinding()]
param(
    [int]$Seconds = 90,
    [string]$Exe,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Exe) { $Exe = Join-Path $repo 'src\apps\Wavee\bin\verify\diag\Wavee.exe' }
if (-not (Test-Path $Exe)) {
    throw "diag build not found at $Exe - build it: dotnet build src\apps\Wavee\Wavee.csproj -c Release -p:FluentGpuDiag=true -o src\apps\Wavee\bin\verify\diag"
}
$running = Get-Process Wavee -ErrorAction SilentlyContinue
if ($running) {
    $paths = ($running | ForEach-Object { "$($_.Id) $($_.Path)" }) -join '; '
    throw "Wavee is already running ($paths). A second Wavee.exe hands off to it and exits; close it first."
}
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ("wavee-scroll-capture-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$traceCsv = Join-Path $OutDir 'fg-scrolltrace.csv'
$scrollLog = Join-Path $env:TEMP 'fg-scroll.log'      # ScrollLog's fixed path; copied out at the end
if (Test-Path $scrollLog) { Remove-Item $scrollLog -Force }

# Engine diagnostic gates (read once at process start).
$env:FG_SCROLL_LOG = '1'
$env:FG_SCROLL_TRACE = $traceCsv
$env:FG_FPS_LOG = '1'
$env:FG_SCROLL_PERF = '1'

$logDir = Join-Path $env:LOCALAPPDATA 'Wavee\logs'
$before = @{}
if (Test-Path $logDir) { Get-ChildItem $logDir -Filter 'wavee-*.log' | ForEach-Object { $before[$_.FullName] = $_.Length } }

Write-Host "capture -> $OutDir"
Write-Host "starting $Exe (diag build) - scroll with the TOUCHPAD in a long list; also try the mouse wheel;"
Write-Host "  do the same on each monitor. $(if ($Seconds -gt 0) { "The app is closed automatically after $Seconds s." } else { 'Close the window to end.' })"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = Split-Path $Exe
$psi.UseShellExecute = $false
$psi.RedirectStandardError = $true
$psi.RedirectStandardOutput = $true
$p = [System.Diagnostics.Process]::Start($psi)
$stderrTask = $p.StandardError.ReadToEndAsync()
$stdoutTask = $p.StandardOutput.ReadToEndAsync()

if ($Seconds -gt 0) {
    if (-not $p.WaitForExit($Seconds * 1000)) {
        # Ask politely first (WM_CLOSE via CloseMainWindow) so ScrollTrace flushes its ring on exit.
        [void]$p.CloseMainWindow()
        if (-not $p.WaitForExit(15000)) { Write-Warning "Wavee did not close within 15 s after WM_CLOSE; leaving it running." }
    }
} else {
    $p.WaitForExit()
}

$stderr = $stderrTask.GetAwaiter().GetResult()
$stdout = $stdoutTask.GetAwaiter().GetResult()
Set-Content -Path (Join-Path $OutDir 'stderr.txt') -Value $stderr -Encoding utf8
Set-Content -Path (Join-Path $OutDir 'stdout.txt') -Value $stdout -Encoding utf8
if (Test-Path $scrollLog) { Copy-Item $scrollLog (Join-Path $OutDir 'fg-scroll.log') -Force }

# The app log: copy the file(s) that grew or appeared during the run.
if (Test-Path $logDir) {
    Get-ChildItem $logDir -Filter 'wavee-*.log' | Where-Object {
        -not $before.ContainsKey($_.FullName) -or $_.Length -gt $before[$_.FullName]
    } | ForEach-Object { Copy-Item $_.FullName (Join-Path $OutDir $_.Name) -Force }
}

# Quick readout so the operator sees whether the run captured anything.
$summary = @()
$summary += "capture: $OutDir"
$summary += "exit: $($p.ExitCode)  ran: $([int]($p.ExitTime - $p.StartTime).TotalSeconds) s"
if (Test-Path (Join-Path $OutDir 'fg-scroll.log')) {
    $lines = Get-Content (Join-Path $OutDir 'fg-scroll.log')
    $summary += "fg-scroll.log: $($lines.Count) lines"
    $edges = $lines | Select-String 'DM STATUS (\w+)->(\w+)' | ForEach-Object { $_.Matches[0].Groups[1].Value + '->' + $_.Matches[0].Groups[2].Value } | Group-Object | Sort-Object Count -Descending
    foreach ($e in $edges) { $summary += "  DM STATUS $($e.Name) x$($e.Count)" }
    $strikes = ($lines | Select-String 'DM STRIKE|DM DISABLED|DM RECOVERED|HITTEST rejected').Count
    $summary += "  DM strikes/disabled/recovered/rejected lines: $strikes"
    $fb = ($lines | Select-String 'FB BEGIN').Count
    $summary += "  wheel-fallback gestures (FB BEGIN): $fb   WHEEL lines: $(($lines | Select-String '^\S+ WHEEL|\bWHEEL\b').Count)"
} else { $summary += "fg-scroll.log: MISSING (is this really the FluentGpuDiag build? did any scroll happen?)" }
if (Test-Path $traceCsv) { $summary += "fg-scrolltrace.csv: $((Get-Content $traceCsv).Count) rows" } else { $summary += "fg-scrolltrace.csv: MISSING (ScrollTrace not compiled in, or no idle flush before exit)" }
$fps = ($stderr -split "`n" | Select-String '^\[fps\]').Count
$summary += "[fps] lines on stderr: $fps"
$summary | Tee-Object -FilePath (Join-Path $OutDir 'capture-summary.txt')
