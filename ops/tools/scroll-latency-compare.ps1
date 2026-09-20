<#
.SYNOPSIS
  End-to-end scroll-latency measurement: input -> present, for Wavee and an installed WinUI reference app, on the
  SAME seeded synthetic gesture - plus, for Wavee, the in-app RawWheel -> OffsetWrite -> Frame breakdown when a
  FG_SCROLL_TRACE CSV is available (Diag build only).

.DESCRIPTION
  Built for docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md section 9 ("MEASUREMENT") / section 8.5. The user's ask:
  "launch a quick measurement of scroll, from the moment of input until computation and actual scroll effect,
  measure the whole pipeline, then also somehow compare it to WinUI apps or other stuff outside of fluent-gpu".

  For each app in turn (Target, then Reference):
    1. Launch it (or find it already running) and bring its window to the foreground.
    2. Start PresentMon capturing that process's presents to a CSV, with -qpc_time_ms so its CPU-start column is
       in the SAME clock domain (QueryPerformanceCounter, ms) as our own injected-input timestamps.
    3. Drive the SAME seeded swipe gesture ops/tools/perf-tour.ps1 uses in -InputMode Swipe (copied, not imported -
       perf-tour.ps1 is owned by another workstream right now and this script must not touch it), recording the
       QPC-ms of every injected wheel event to a sidecar CSV.
    4. Post-process with ops/tools/ScrollLatency.psm1 (pure functions, unit-tested separately): join each input
       timestamp to the first present at/after it, report p50/p90/p99 input->present latency, present cadence
       (mean/p99 interval, dropped frames), and - for the Target only, when it is Wavee and a ScrollTrace CSV
       turns up - the RawWheel -> OffsetWrite -> Frame breakdown.

  HONEST CEILING (also printed in the report - do not strip this):
    - This measures input -> present, NOT input -> photons. Scanout adds a further fixed delay identical for
      every app measured this way, so a COMPARISON is fair even though neither number is "true" click-to-photon.
    - SendInput cannot post touchpad phase packets (MOUSEEVENTF_WHEEL has no begin/end/phase concept), so this
      script's gesture is a wheel of varying cadence, not a touchpad contact. It CANNOT exercise the
      stranded-DM-INERTIA-contact bug (handoff section 8.0 / A0) or the touchpad classifier (section 8.2 A1). Settling those
      needs a human hand on the touchpad plus the engine's own ops/tools/dm-probe.

.PARAMETER Target
  Exe path (launched fresh) or a bare process name (must already be running). Default: the AOT Release publish
  under src\apps\Wavee\bin\..., falling back to the -Diag publish tree; throws with the exact publish command if
  neither exists.

.PARAMETER Reference
  Process name of the WinUI comparison app. 'SystemSettings' (default), 'WinUIGallery' and 'Files' are launched by
  known protocol/best-effort; anything else must already be running (this script will not guess how to start an
  app it has never launched before).

.PARAMETER Seconds
  Capture length per app (also the swipe gesture's time budget when -Swipes is 0).

.PARAMETER Seed
  RNG seed for the swipe gesture model - the SAME seed drives both apps' runs, matching perf-tour.ps1's
  -GestureSeed contract ("two tours drive the SAME sequence of swipes").

.PARAMETER Swipes
  Stop the gesture after this many swipes, whichever of -Seconds / -Swipes is reached first. 0 (default) = no
  swipe-count limit; -Seconds alone governs, exactly like perf-tour.ps1's default Swipe mode.

.PARAMETER OutDir
  Where the sidecar CSVs, PresentMon CSVs and the Markdown report land. Default: a timestamped folder under
  %TEMP%\wavee-scroll-latency-<stamp>.

.EXAMPLE
  powershell -File ops\tools\scroll-latency-compare.ps1
  powershell -File ops\tools\scroll-latency-compare.ps1 -Reference WinUIGallery -Seconds 8 -Swipes 20
  powershell -File ops\tools\scroll-latency-compare.ps1 -Target C:\...\bin\publish-aot-diag\win-x64\Wavee.exe

.NOTES
  Windows PowerShell 5.1 ONLY (no &&/||, no ternary, no ??/?., no -AsHashtable). PresentMon needs Administrator or
  "Performance Log Users" membership to open an ETW session; if the current process has neither, this script
  elevates PresentMon alone (via a one-shot Verb RunAs on a tiny generated .cmd wrapper - NOT the whole script) and
  says so. On a normal desktop with UAC prompting enabled that elevation shows a consent dialog; there is no way to
  avoid that short of running the whole script elevated yourself.
#>
[CmdletBinding()]
param(
    [string]$Target,
    [string]$Reference = 'SystemSettings',
    [ValidateRange(1, 60)][int]$Seconds = 6,
    [int]$Seed = 20260909,
    [ValidateRange(0, 500)][int]$Swipes = 0,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $PSScriptRoot 'ScrollLatency.psm1') -Force -DisableNameChecking

if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('wavee-scroll-latency-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

# -- native helpers (copied, minimal subset of perf-tour.ps1's PerfTourNative - NOT importing/editing that script,
#    which another workstream owns right now; kept import-shaped so a future dedup is a straight cut-and-paste) ----
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ScrollLatencyNative
{
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    public static bool FocusWindow(IntPtr hwnd) { if (hwnd == IntPtr.Zero) return false; ShowWindow(hwnd, 9); BringWindowToTop(hwnd); return SetForegroundWindow(hwnd); }
    public static bool CursorOverWindow(IntPtr hwnd)
    {
        RECT r; if (!GetWindowRect(hwnd, out r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top; if (w <= 0 || h <= 0) return false;
        return SetCursorPos(r.Left + (int)(w * 0.65), r.Top + (int)(h * 0.60));
    }
    public static uint Wheel(int delta)
    {
        INPUT[] i = new INPUT[1]; i[0].type = 0; i[0].u.mi.mouseData = unchecked((uint)delta); i[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
        return SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

$script:QpcFrequency = [System.Diagnostics.Stopwatch]::Frequency
function Get-QpcMs { return [System.Diagnostics.Stopwatch]::GetTimestamp() / $script:QpcFrequency * 1000.0 }

# -- arch + default target ----------------------------------------------------------------------------------------
function Get-ScrollLatencyArch {
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    if ("$a" -match 'ARM64') { return 'arm64' }
    return 'x64'
}

function Resolve-DefaultTarget {
    param([string]$RepoRoot)
    $arch = Get-ScrollLatencyArch
    $release = Join-Path $RepoRoot "src\apps\Wavee\bin\Release\net10.0\win-$arch\publish\Wavee.exe"
    $diag = Join-Path $RepoRoot "src\apps\Wavee\bin\publish-aot-diag\win-$arch\Wavee.exe"
    if (Test-Path $release) { return $release }
    if (Test-Path $diag) { return $diag }
    throw "No published AOT Wavee.exe found. Checked:`n  $release`n  $diag`nPublish first: powershell -File ops\build\publish-wavee-aot.ps1 -Arch $arch  (add -Diag for a ScrollTrace-capable build)."
}

if (-not $Target) { $Target = Resolve-DefaultTarget -RepoRoot $root }

# -- PresentMon discovery + elevation -----------------------------------------------------------------------------
function Find-PresentMon {
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\presentmon.exe'
    if (Test-Path $candidate) { return $candidate }
    $cmd = Get-Command 'presentmon.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-CurrentProcessHasEtwRights {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object System.Security.Principal.WindowsPrincipal($id)
    $isAdmin = $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $inPerfGroup = [bool](@($id.Groups | Where-Object { $_.Value -eq 'S-1-5-32-559' }).Count)   # Performance Log Users
    return ($isAdmin -or $inPerfGroup)
}

# Elevates PresentMon ALONE (a tiny generated .cmd, never this script) via one Verb RunAs, started asynchronously so
# the caller can drive input while it captures. -qpc_time_ms is what makes CPUStart* comparable across processes -
# see ScrollLatency.psm1's header comment. Returns a handle Wait-PresentMonCapture polls for completion.
function Start-PresentMonCapture {
    param(
        [Parameter(Mandatory = $true)][string]$PresentMonPath,
        [Parameter(Mandatory = $true)][string]$ProcessImageName,
        [Parameter(Mandatory = $true)][string]$OutputCsv,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [Parameter(Mandatory = $true)][string]$WorkDir
    )
    $stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $log = Join-Path $WorkDir "presentmon-$stamp.log"
    $cmdFile = Join-Path $WorkDir "run-presentmon-$stamp.cmd"
    $doneFile = Join-Path $WorkDir "presentmon-$stamp.done"
    # Completion is signalled by a zero-byte FILE's existence (`type nul >`), never by matching text inside the log.
    # An elevated cmd's own `echo` was observed on this machine writing that text in an encoding Get-Content decodes
    # as CJK-looking garbage (UTF-16-ish bytes from an elevated console session, vs the plain bytes PresentMon's own
    # redirected output uses) - a `-match` against it silently never fires even though the capture finished in
    # seconds, which made every elevated capture report as timed-out. `type nul >` sidesteps text encoding entirely.
    @"
@echo off
"$PresentMonPath" --process_name $ProcessImageName --output_file "$OutputCsv" --qpc_time_ms --timed $Seconds --terminate_after_timed --stop_existing_session --no_console_stats > "$log" 2>&1
type nul > "$doneFile"
"@ | Set-Content -Path $cmdFile -Encoding ASCII

    $elevated = -not (Test-CurrentProcessHasEtwRights)
    if ($elevated) {
        Warn "PresentMon needs elevation on this account (no Administrator/Performance Log Users token) - requesting it for PresentMon only."
        $proc = Start-Process -FilePath $cmdFile -Verb RunAs -WindowStyle Hidden -PassThru
    }
    else {
        $proc = Start-Process -FilePath $cmdFile -WindowStyle Hidden -PassThru
    }
    return [pscustomobject]@{ Process = $proc; LogPath = $log; DoneFile = $doneFile; OutputCsv = $OutputCsv; Elevated = $elevated; ProcessImageName = $ProcessImageName }
}

function Wait-PresentMonCapture {
    param([Parameter(Mandatory = $true)]$Handle, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Handle.DoneFile) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# -- reference-app resolution -------------------------------------------------------------------------------------
# Known launch methods, best-effort. Anything not in this table (or whose launch does not surface the process
# within the timeout) must already be running - this script will not guess an unfamiliar app's launch protocol.
$script:KnownReferenceLaunch = @{
    'SystemSettings' = { Start-Process 'ms-settings:' }
    'WinUIGallery'   = { Start-Process 'winui3gallery:' }
    'Files'          = { Start-Process 'files-uwp:' }
}

function Get-AppWindow {
    param([Parameter(Mandatory = $true)][string]$ProcessName)
    # Case 1: the process owns its own top-level window directly (most non-UWP apps, incl. Wavee and unpackaged WinUI apps).
    $direct = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if ($direct) { return [pscustomobject]@{ Hwnd = $direct.MainWindowHandle; RenderProcessId = $direct.Id; RenderProcessName = $ProcessName; Hosted = $false } }
    # Case 2: UWP-hosted (ApplicationFrameHost owns the visible window; ProcessName is the render/present process,
    # confirmed on this machine for SystemSettings.exe). Best-effort match: the frame host window with a non-empty
    # title. If more than one UWP app frame is open this picks the first one found - fine for a deliberate,
    # single-app measurement run, and flagged in the report as a heuristic.
    $render = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $render) { return $null }
    $frame = Get-Process ApplicationFrameHost -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -ne '' } | Select-Object -First 1
    if ($frame) { return [pscustomobject]@{ Hwnd = $frame.MainWindowHandle; RenderProcessId = $render.Id; RenderProcessName = $ProcessName; Hosted = $true } }
    return $null
}

function Resolve-ReferenceWindow {
    param([Parameter(Mandatory = $true)][string]$Name)
    $w = Get-AppWindow -ProcessName $Name
    if ($w) { return $w }
    if ($script:KnownReferenceLaunch.ContainsKey($Name)) {
        Info "Launching reference app '$Name'..."
        & $script:KnownReferenceLaunch[$Name]
    }
    else {
        Warn "No known launch method for reference app '$Name' - it must already be running."
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $w = Get-AppWindow -ProcessName $Name
        if ($w) { return $w }
    }
    throw "Reference app '$Name' did not present a window within 10s. Launch it manually and re-run, or pass -Reference <a process name that is already running>."
}

function Wait-MainWindow {
    param([Parameter(Mandatory = $true)][int]$ProcessId, [int]$TimeoutSeconds = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $proc) { return [IntPtr]::Zero }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 200
    }
    return [IntPtr]::Zero
}

# -- the seeded swipe gesture (SAME shape as perf-tour.ps1 -InputMode Swipe, copied not imported) -------------------
<#
  Not a metronome: variable-length swipes (4-20 ticks), variable within-swipe speed (6-16ms), 1-3x force, and after
  each swipe a re-grab (45%, 20-70ms - fingers back down on a still-decaying fling), an ordinary lift (35%,
  110-260ms) or a full settle (20%, 380-750ms), with a 12% chance of reversing direction. Same constants as
  perf-tour.ps1's Swipe mode so a seeded run there and a seeded run here drive comparable cadence, even though
  perf-tour.ps1 itself is not touched or imported (another workstream owns it right now).
#>
function Invoke-SeededSwipe {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$Hwnd,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [Parameter(Mandatory = $true)][int]$Seed,
        [int]$MaxSwipes = 0,
        [Parameter(Mandatory = $true)][string]$SidecarCsv,
        [int]$BaseDelta = 120
    )
    "qpcMs,qpcTicks,deltaNotch,swipeIndex" | Set-Content -Path $SidecarCsv -Encoding UTF8

    [ScrollLatencyNative]::FocusWindow($Hwnd) | Out-Null
    Start-Sleep -Milliseconds 150
    $focused = [ScrollLatencyNative]::GetForegroundWindow() -eq $Hwnd
    $cursorOk = [ScrollLatencyNative]::CursorOverWindow($Hwnd)
    Start-Sleep -Milliseconds 80

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $rng = New-Object System.Random($Seed)
    $direction = -1
    $sent = 0; $failed = 0; $swipeIndex = 0
    while ($sw.Elapsed.TotalSeconds -lt $Seconds -and (-not $focused -or [ScrollLatencyNative]::GetForegroundWindow() -eq $Hwnd)) {
        if ($MaxSwipes -gt 0 -and $swipeIndex -ge $MaxSwipes) { break }
        $len = $rng.Next(4, 21)
        $tickMs = $rng.Next(6, 17)
        $force = 1 + $rng.Next(0, 3)
        for ($k = 0; $k -lt $len; $k++) {
            if ($sw.Elapsed.TotalSeconds -ge $Seconds) { break }
            $delta = $direction * [Math]::Abs($BaseDelta) * $force
            $ticks = [System.Diagnostics.Stopwatch]::GetTimestamp()
            $ok = [ScrollLatencyNative]::Wheel($delta)
            $sent++
            if ($ok -ne 1) { $failed++ }
            $qpcMs = $ticks / $script:QpcFrequency * 1000.0
            Add-Content -Path $SidecarCsv -Value "$qpcMs,$ticks,$delta,$swipeIndex"
            Start-Sleep -Milliseconds $tickMs
        }
        $swipeIndex++
        $roll = $rng.Next(0, 100)
        if ($roll -lt 45) { $rest = $rng.Next(20, 70) }
        elseif ($roll -lt 80) { $rest = $rng.Next(110, 260) }
        else { $rest = $rng.Next(380, 750) }
        if ($rng.Next(0, 100) -lt 12) { $direction = -$direction }
        Start-Sleep -Milliseconds $rest
    }
    return [pscustomobject]@{ Sent = $sent; Failed = $failed; Swipes = $swipeIndex; Focused = $focused; CursorOk = $cursorOk }
}

# -- diag-build hazard guard (mirrors fluent-gpu-pin\ops\diag\wavee-scroll-session.ps1's env block) ------------------
function Set-DiagEnvironmentGuard {
    param([string]$ExePath)
    $isDiag = $ExePath -match '(?i)diag'
    if (-not $isDiag) { return $false }
    $env:FG_BIND_CONTRACT = '0'      # MANDATORY: default-ON once compiled in; would change the feel being measured
    $env:FG_BACKWARDS_WRITE = '0'    # MANDATORY: default-ON once compiled in; per-signal subscriber-list scan
    if (Test-Path Env:FG_SCROLL_LOG) { Remove-Item Env:FG_SCROLL_LOG }   # per-event Console.WriteLine perturbs pacing
    return $true
}

# -- one app's full measurement leg -------------------------------------------------------------------------------
function Measure-ScrollLatencyForApp {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$PresentMonPath,
        [Parameter(Mandatory = $true)][IntPtr]$Hwnd,
        [Parameter(Mandatory = $true)][string]$ImageName,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [Parameter(Mandatory = $true)][int]$Seed,
        [int]$MaxSwipes,
        [Parameter(Mandatory = $true)][string]$WorkDir
    )
    $presentCsv = Join-Path $WorkDir ("presentmon-" + $Label + ".csv")
    $sidecarCsv = Join-Path $WorkDir ("input-sidecar-" + $Label + ".csv")

    $capture = Start-PresentMonCapture -PresentMonPath $PresentMonPath -ProcessImageName $ImageName -OutputCsv $presentCsv -Seconds $Seconds -WorkDir $WorkDir
    Start-Sleep -Milliseconds 700   # let the ETW session actually start before the gesture begins

    $swipe = Invoke-SeededSwipe -Hwnd $Hwnd -Seconds $Seconds -Seed $Seed -MaxSwipes $MaxSwipes -SidecarCsv $sidecarCsv

    $captureOk = Wait-PresentMonCapture -Handle $capture -TimeoutSeconds ($Seconds + 25)
    Start-Sleep -Milliseconds 500

    $presentMonNote = $null
    $normalized = [pscustomobject]@{ IsAbsoluteQpc = $false; Rows = @() }
    if (-not $captureOk) { $presentMonNote = "PresentMon did not report completion within the timeout (log: $($capture.LogPath))" }
    elseif (-not (Test-Path $presentCsv)) { $presentMonNote = "PresentMon produced no CSV: zero presents were attributed to '$ImageName' during the capture window (log: $($capture.LogPath))" }
    else {
        try { $normalized = Import-PresentMonCsv -Path $presentCsv }
        catch { $presentMonNote = "PresentMon CSV failed to parse: $($_.Exception.Message)" }
    }

    $inputTimestamps = @()
    if (Test-Path $sidecarCsv) {
        $inputTimestamps = @(Import-Csv -Path $sidecarCsv | ForEach-Object { [double]$_.qpcMs })
    }

    $cadence = Get-PresentCadence -NormalizedRows $normalized.Rows
    $joinNote = $null
    $latencyPercentiles = [pscustomobject]@{ Count = 0; Mean = $null; P50 = $null; P90 = $null; P99 = $null }
    $matchedCount = 0
    if (-not $normalized.IsAbsoluteQpc -and $normalized.Rows.Count -gt 0) {
        $joinNote = "PresentMon did not report an absolute-QPC start column (unexpected - -qpc_time_ms was requested); input->present latency cannot be computed for this leg."
    }
    elseif ($inputTimestamps.Count -eq 0) {
        $joinNote = "No input events were recorded (window never focused, or SendInput failed every time)."
    }
    else {
        $joined = Join-InputToPresent -InputTimestampsMs $inputTimestamps -NormalizedRows $normalized.Rows
        $matched = @($joined | Where-Object { $_.Matched } | ForEach-Object { [double]$_.LatencyMs })
        $matchedCount = $matched.Count
        $latencyPercentiles = Get-LatencyPercentiles -Values $matched
        if ($matchedCount -lt $inputTimestamps.Count) {
            $joinNote = "$($inputTimestamps.Count - $matchedCount) of $($inputTimestamps.Count) injected inputs had no later present in the capture window (gesture outlived the capture, or the app stopped repainting)."
        }
    }

    return [pscustomobject]@{
        Label               = $Label
        ImageName           = $ImageName
        PresentCsv          = $presentCsv
        SidecarCsv          = $sidecarCsv
        PresentMonLog       = $capture.LogPath
        PresentMonElevated  = $capture.Elevated
        CaptureOk           = $captureOk
        PresentMonNote      = $presentMonNote
        InputEventsSent     = $swipe.Sent
        InputEventsFailed   = $swipe.Failed
        Swipes              = $swipe.Swipes
        Focused             = $swipe.Focused
        CursorOk            = $swipe.CursorOk
        PresentRows         = $normalized.Rows.Count
        IsAbsoluteQpc       = $normalized.IsAbsoluteQpc
        Cadence             = $cadence
        LatencyPercentiles  = $latencyPercentiles
        MatchedInputs       = $matchedCount
        TotalInputs         = $inputTimestamps.Count
        JoinNote            = $joinNote
    }
}

# -- ScrollTrace breakdown (Wavee target only, Diag build only) --------------------------------------------------
function Get-WaveeScrollTraceLeg {
    param([string]$TraceCsvPath)
    if (-not $TraceCsvPath -or -not (Test-Path $TraceCsvPath)) {
        return [pscustomobject]@{ Available = $false; Note = 'No FG_SCROLL_TRACE CSV present (not a Diag/FLUENTGPU_DIAG build, or ScrollTrace never armed).' }
    }
    $rows = Import-ScrollTraceRows -Path $TraceCsvPath
    $breakdown = Get-ScrollTraceBreakdown -NormalizedRows $rows
    $matched = @($breakdown | Where-Object { $_.Matched } | ForEach-Object { [double]$_.TotalMs })
    $percentiles = Get-LatencyPercentiles -Values $matched
    return [pscustomobject]@{
        Available   = $true
        Events      = $breakdown.Count
        Matched     = $matched.Count
        Percentiles = $percentiles
        Note        = $(if ($matched.Count -lt $breakdown.Count) { "$($breakdown.Count - $matched.Count) of $($breakdown.Count) RawWheel packets never reached a joined OffsetWrite+Frame within the join window." } else { $null })
    }
}

# -- main ---------------------------------------------------------------------------------------------------------
Step "Wavee scroll-latency comparison"
Info "OutDir: $OutDir"
Info "Target: $Target"
Info "Reference: $Reference"
Info "Seconds=$Seconds Seed=$Seed Swipes=$(if ($Swipes -gt 0) { $Swipes } else { 'unbounded (time-limited)' })"

$presentMonPath = Find-PresentMon
if (-not $presentMonPath) { throw "PresentMon not found on PATH or under %LOCALAPPDATA%\Microsoft\WinGet\Links. Install it: winget install Intel.PresentMon.Console" }
Info "PresentMon: $presentMonPath"

$results = New-Object System.Collections.Generic.List[object]
$waveeTraceLeg = $null

# -- Target leg ---------------------------------------------------------------------------------------------------
Step "Target leg: $Target"
$targetIsWavee = ([System.IO.Path]::GetFileName($Target) -ieq 'Wavee.exe')
$targetIsPath = (Test-Path $Target) -and ($Target -match '\.exe$')
$diagGuarded = $false
$traceCsvForWavee = $null

if ($targetIsPath) {
    if ($targetIsWavee -and (Get-Process Wavee -ErrorAction SilentlyContinue)) {
        throw "Wavee is already running - a second Wavee.exe hands its launch to the running one and exits, so this would measure the WRONG process. Close it first."
    }
    $diagGuarded = Set-DiagEnvironmentGuard -ExePath $Target
    if ($diagGuarded) { Warn "Diag-build hazard guarded: FG_BIND_CONTRACT=0, FG_BACKWARDS_WRITE=0, FG_SCROLL_LOG cleared (mirrors wavee-scroll-session.ps1)." }
    if ($targetIsWavee) {
        $traceCsvForWavee = Join-Path $OutDir 'wavee-scrolltrace.csv'
        $env:FG_SCROLL_TRACE = $traceCsvForWavee   # a no-op unless this exe was built with FLUENTGPU_DIAG
    }
    $proc = Start-Process -FilePath $Target -PassThru -WorkingDirectory (Split-Path $Target)
    $hwnd = Wait-MainWindow -ProcessId $proc.Id -TimeoutSeconds 30
    if ($hwnd -eq [IntPtr]::Zero) { throw "Target process never presented a main window within 30s." }
    $imageName = [System.IO.Path]::GetFileName($Target)
    Start-Sleep -Seconds 3   # let it settle onto its first screen before capturing
}
else {
    $w = Get-AppWindow -ProcessName $Target
    if (-not $w) { throw "Target process '$Target' is not running and is not a file path. Launch it, or pass an exe path." }
    $hwnd = $w.Hwnd
    $imageName = $Target + '.exe'
}

$targetResult = Measure-ScrollLatencyForApp -Label 'target' -PresentMonPath $presentMonPath -Hwnd $hwnd -ImageName $imageName `
    -Seconds $Seconds -Seed $Seed -MaxSwipes $Swipes -WorkDir $OutDir
$results.Add($targetResult)

if ($targetIsWavee) {
    Start-Sleep -Seconds 1   # ScrollTrace flushes on idle frames, not mid-gesture
    $waveeTraceLeg = Get-WaveeScrollTraceLeg -TraceCsvPath $traceCsvForWavee
}

if ($targetIsPath) {
    Step "Closing target"
    $tproc = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($tproc) {
        if (-not $tproc.CloseMainWindow()) { Warn "Clean close request was rejected; the process may still be running." }
        elseif (-not $tproc.WaitForExit(15000)) { Warn "Target did not exit within 15s of a clean close request." }
    }
}

# -- Reference leg ------------------------------------------------------------------------------------------------
Step "Reference leg: $Reference"
$refWindow = Resolve-ReferenceWindow -Name $Reference
$refImageName = $Reference + '.exe'
Start-Sleep -Seconds 1
$referenceResult = Measure-ScrollLatencyForApp -Label 'reference' -PresentMonPath $presentMonPath -Hwnd $refWindow.Hwnd -ImageName $refImageName `
    -Seconds $Seconds -Seed $Seed -MaxSwipes $Swipes -WorkDir $OutDir
$results.Add($referenceResult)

# -- report -------------------------------------------------------------------------------------------------------
function Fmt2($v) { if ($null -eq $v) { return 'n/a' }; return [math]::Round([double]$v, 2) }

Step "Results"
$table = @()
foreach ($r in $results) {
    $table += [pscustomobject]@{
        App              = $r.Label
        Image            = $r.ImageName
        PresentRows      = $r.PresentRows
        'InputsMatched'  = "$($r.MatchedInputs)/$($r.TotalInputs)"
        'p50 ms'         = Fmt2 $r.LatencyPercentiles.P50
        'p90 ms'         = Fmt2 $r.LatencyPercentiles.P90
        'p99 ms'         = Fmt2 $r.LatencyPercentiles.P99
        'meanCadence ms' = Fmt2 $r.Cadence.MeanIntervalMs
        'p99Cadence ms'  = Fmt2 $r.Cadence.P99IntervalMs
        Dropped          = $r.Cadence.DroppedFrames
    }
}
$table | Format-Table -AutoSize | Out-String | Write-Host

$reportPath = Join-Path $OutDir 'scroll-latency-report.md'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Wavee scroll-latency comparison - " + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
[void]$sb.AppendLine()
[void]$sb.AppendLine("- Target: ``$Target``$(if ($diagGuarded) { ' (Diag build - FG_BIND_CONTRACT=0, FG_BACKWARDS_WRITE=0, FG_SCROLL_LOG cleared, mirroring wavee-scroll-session.ps1)' })")
[void]$sb.AppendLine("- Reference: ``$Reference``")
[void]$sb.AppendLine("- Seconds=$Seconds Seed=$Seed Swipes=$(if ($Swipes -gt 0) { $Swipes } else { 'unbounded' }) (SAME seed drives both apps' gesture)")
[void]$sb.AppendLine("- PresentMon: ``$presentMonPath``")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Honest ceiling - read this before the numbers below')
[void]$sb.AppendLine()
[void]$sb.AppendLine('- This measures **input -> present**, not input -> photons. Scanout adds a further fixed delay identical for every app measured this way; that is fine for a COMPARISON between apps, but neither number here is a true click-to-photon latency.')
[void]$sb.AppendLine('- `SendInput` cannot post touchpad phase packets (`MOUSEEVENTF_WHEEL` has no begin/end/phase concept), so this run drove a scripted WHEEL gesture, not a touchpad contact. It therefore does **NOT** exercise the stranded-DM-INERTIA-contact bug (handoff doc section 8.0/A0) or the touchpad classifier (section 8.2 A1) - settling those needs a human hand on the touchpad plus the engine''s own `ops/tools/dm-probe`.')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Side-by-side')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| app | image | present rows | inputs matched | p50 ms | p90 ms | p99 ms | mean cadence ms | p99 cadence ms | dropped |')
[void]$sb.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($r in $results) {
    [void]$sb.AppendLine("| $($r.Label) | $($r.ImageName) | $($r.PresentRows) | $($r.MatchedInputs)/$($r.TotalInputs) | $(Fmt2 $r.LatencyPercentiles.P50) | $(Fmt2 $r.LatencyPercentiles.P90) | $(Fmt2 $r.LatencyPercentiles.P99) | $(Fmt2 $r.Cadence.MeanIntervalMs) | $(Fmt2 $r.Cadence.P99IntervalMs) | $($r.Cadence.DroppedFrames) |")
}
[void]$sb.AppendLine()
foreach ($r in $results) {
    [void]$sb.AppendLine("### $($r.Label) ($($r.ImageName))")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- elevated PresentMon: $($r.PresentMonElevated); capture completed: $($r.CaptureOk); focused: $($r.Focused); cursor positioned: $($r.CursorOk)")
    [void]$sb.AppendLine("- input events sent: $($r.InputEventsSent) (failed: $($r.InputEventsFailed)); swipes: $($r.Swipes)")
    [void]$sb.AppendLine("- absolute-QPC join available: $($r.IsAbsoluteQpc)")
    if ($r.PresentMonNote) { [void]$sb.AppendLine("- PresentMon: $($r.PresentMonNote)") }
    if ($r.JoinNote) { [void]$sb.AppendLine("- join: $($r.JoinNote)") }
    [void]$sb.AppendLine("- PresentMon CSV: ``$($r.PresentCsv)``; input sidecar: ``$($r.SidecarCsv)``; PresentMon log: ``$($r.PresentMonLog)``")
    [void]$sb.AppendLine()
}
if ($waveeTraceLeg) {
    [void]$sb.AppendLine('## Wavee in-app breakdown (RawWheel -> OffsetWrite -> Frame, ScrollTrace)')
    [void]$sb.AppendLine()
    if ($waveeTraceLeg.Available) {
        [void]$sb.AppendLine("- events: $($waveeTraceLeg.Events); matched (reached a joined OffsetWrite + Frame): $($waveeTraceLeg.Matched)")
        [void]$sb.AppendLine("- total (RawWheel -> Frame) ms: p50=$(Fmt2 $waveeTraceLeg.Percentiles.P50) p90=$(Fmt2 $waveeTraceLeg.Percentiles.P90) p99=$(Fmt2 $waveeTraceLeg.Percentiles.P99)")
        if ($waveeTraceLeg.Note) { [void]$sb.AppendLine("- $($waveeTraceLeg.Note)") }
    }
    else {
        [void]$sb.AppendLine("- $($waveeTraceLeg.Note)")
    }
    [void]$sb.AppendLine()
}

$sb.ToString() | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ''
Write-Host "wrote $reportPath" -ForegroundColor Green
