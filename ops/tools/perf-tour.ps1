<#
.SYNOPSIS
  The objective performance tour: launch Wavee, walk a fixed set of routes via deep links twice (cold lap, warm lap),
  wheel-scroll every list page, then turn the app's own always-on log lines (nav.frames, scroll.frames, frame.slow,
  page.reveal, query.first-pass, commit.slow, catalog.gate.wait, mem.sample) into one markdown report with a verdict
  per route against the gates: no completed UI frame over 8.3 ms, working set <= 200 MB, every cold/warm reveal <= 100 ms.

.EXAMPLE
  powershell -File ops/tools/perf-tour.ps1
  powershell -File ops/tools/perf-tour.ps1 -Exe C:\wavee\WaveeMusic\src\apps\Wavee\bin\Release\net10.0\Wavee.exe -Laps 2

.NOTES
  Uses SendInput for the wheel bursts: the machine must be unlocked and the Wavee window focusable. Nothing here
  reads production source; the report is built only from the log the app writes for every user.
#>
param(
    [string]$Exe = "C:\wavee\WaveeMusic\src\apps\Wavee\bin\Release\net10.0\Wavee.exe",
    [string]$Out = "",
    [ValidateRange(1, 20)][int]$Laps = 2,
    [ValidateRange(1, 60)][int]$SettleSeconds = 6,
    [ValidateRange(1, 60)][int]$ScrollSeconds = 4,
    [ValidateRange(1, 1200)][int]$WheelPerTick = 120,
    [ValidateRange(1, 1000)][int]$TickIntervalMs = 16,
    [ValidateRange(1, 200)][int]$WorkingSetGateMB = 200,
    [ValidateRange(1, 100)][double]$RevealGateMs = 100,
    [switch]$KeepOpen,
    [switch]$CollectHeap,
    [ValidateRange(0, 120)][int]$UserScroll = 0,
    # Stress: an unbroken wheel event every TickIntervalMs. Never lets the scroll settle, so it exercises a sustained
    #   fling and nothing else — in particular the viewport never returns to Idle, which is where a real gesture spends
    #   most of its time and where the realize scheduler makes its most expensive decisions.
    # Swipe (default): the gesture people actually make on a touchpad — swipes of varying length and force, and most
    #   of the time the fingers come back DOWN before the previous fling has settled (the re-grab), which is how
    #   someone crosses a long list quickly. SendInput cannot post touchpad phase packets (MOUSEEVENTF_WHEEL has no
    #   begin/end), so the device class is still a wheel; what this reproduces is the CADENCE, which is what the
    #   realize scheduler reacts to. Stress never settles and a fixed swipe+lift always settles — the interesting
    #   state, a burst arriving on top of a live fling, is between them.
    # Varied: mixed directions with occasional long pauses.
    [ValidateSet('Swipe', 'Stress', 'Varied')][string]$InputMode = 'Swipe',
    # Seed for the Swipe gesture model, so two tours drive the SAME sequence of swipes and are comparable.
    [int]$GestureSeed = 20260909
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'perf-tour-analysis.ps1')
Import-Module (Join-Path $PSScriptRoot '..\build\Wavee.Build.psm1') -Force -DisableNameChecking
$coverageIssues = New-Object 'System.Collections.Generic.List[string]'

# Uncommitted tree fingerprint for a repo: file-count of `git status --porcelain` plus a SHA256 of `git diff HEAD`,
# so a run against a dirty tree is distinguishable from one against a clean HEAD without recording the diff itself.
# Git failures must not abort the tour.
function Get-PerfDirtyFingerprint([string]$RepoRoot) {
    try {
        $statusOut = @(& git -C $RepoRoot status --porcelain 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ($statusOut -join "`n") }
        $dirtyCount = @($statusOut | Where-Object { $_ -ne '' }).Count
        $diffOut = & git -C $RepoRoot diff HEAD 2>&1
        if ($LASTEXITCODE -ne 0) { throw (($diffOut | Out-String).Trim()) }
        $diffBytes = [System.Text.Encoding]::UTF8.GetBytes(($diffOut | Out-String))
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try { $diffHash = ([BitConverter]::ToString($sha256.ComputeHash($diffBytes)) -replace '-', '').ToLowerInvariant() }
        finally { $sha256.Dispose() }
        return [pscustomobject]@{ Ok = $true; Text = "dirty=$dirtyCount diffSha256=$diffHash" }
    } catch {
        return [pscustomobject]@{ Ok = $false; Text = 'unavailable: ' + $_.Exception.Message }
    }
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PerfTourNative
{
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, MOUSEEVENTF_WHEEL = 0x0800, KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_HOME = 0x24;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POWER { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out POWER status);
    [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    public static bool FocusWindow(IntPtr hwnd) { if (hwnd == IntPtr.Zero) return false; ShowWindow(hwnd, 9); BringWindowToTop(hwnd); return SetForegroundWindow(hwnd); }
    public static bool CursorOverList(IntPtr hwnd)
    {
        RECT r; if (!GetWindowRect(hwnd, out r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top; if (w <= 0 || h <= 0) return false;
        return SetCursorPos(r.Left + (int)(w * 0.65), r.Top + (int)(h * 0.60));
    }
    public static uint Wheel(int delta, bool move)
    {
        INPUT[] i = new INPUT[1]; i[0].type = INPUT_MOUSE; i[0].u.mi.mouseData = unchecked((uint)delta); i[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
        if (move) { i[0].u.mi.dwFlags |= 1; i[0].u.mi.dx = delta > 0 ? -2 : 2; }
        return SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static uint Home()
    {
        INPUT[] i = new INPUT[2]; i[0].type = INPUT_KEYBOARD; i[0].u.ki.wVk = VK_HOME; i[1].type = INPUT_KEYBOARD; i[1].u.ki.wVk = VK_HOME; i[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        return SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

# ── The tour ─────────────────────────────────────────────────────────────────────────────────────────────────────────
# Real-account entities: the ones every earlier finding used, so numbers stay comparable across runs.
$tour = @(
    @{ Name = "album-A";   Route = "wavee://open?route=album&arg=spotify%3Aalbum%3A5Qg2iNuV6zTCGlIYunvvTd";            Scroll = $true  },
    @{ Name = "home";      Route = "wavee://open?route=home";                                                          Scroll = $true  },
    @{ Name = "artist-A";  Route = "wavee://open?route=artist&arg=spotify%3Aartist%3A3eVa5w3URK5duf6eyVDbu9";          Scroll = $true  },
    @{ Name = "playlist-A";Route = "wavee://open?route=pl&arg=spotify%3Aplaylist%3A37i9dQZF1EP6YuccBxUcC1";            Scroll = $true  },
    @{ Name = "playlist-B";Route = "wavee://open?route=pl&arg=spotify%3Aplaylist%3A2pnt79m93NytfAj2lByLlQ";            Scroll = $true  },
    @{ Name = "album-B";   Route = "wavee://open?route=album&arg=spotify%3Aalbum%3A7kFyd5oyJdVX2pIi6P4iHE";            Scroll = $false },
    @{ Name = "artist-B";  Route = "wavee://open?route=artist&arg=spotify%3Aartist%3A4lxfqrEsLX6N1N4OCSkILp";          Scroll = $true  },
    @{ Name = "liked";     Route = "wavee://open?route=liked";                                                         Scroll = $true  },
    @{ Name = "albums";    Route = "wavee://open?route=albums";                                                        Scroll = $true  }
)

function Wait-MainWindow([int]$ProcessId, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $proc) { return [IntPtr]::Zero }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 200
    }
    return [IntPtr]::Zero
}

function Invoke-ScrollBurst([IntPtr]$Hwnd, [int]$Seconds, [int]$Delta, [int]$IntervalMs) {
    $result = [pscustomobject]@{ StartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); Sent = 0; Failed = 0; Swipes = 0; Regrabs = 0; Focus = $false; Cursor = $false; Mode = $(if ($UserScroll -gt 0) { 'UserScroll' } else { $InputMode }) }
    if ($Hwnd -eq [IntPtr]::Zero) { return $result }
    [PerfTourNative]::FocusWindow($Hwnd) | Out-Null
    Start-Sleep -Milliseconds 150
    $result.Focus = [PerfTourNative]::GetForegroundWindow() -eq $Hwnd
    $result.Cursor = [PerfTourNative]::CursorOverList($Hwnd)
    Start-Sleep -Milliseconds 80
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($UserScroll -gt 0) {
        Write-Host "Touchpad recording for $UserScroll seconds: scroll the active page now."
        while ($sw.Elapsed.TotalSeconds -lt $UserScroll) {
            if ([PerfTourNative]::GetForegroundWindow() -ne $Hwnd) { $result.Focus = $false }
            Start-Sleep -Milliseconds 100
        }
    } else {
        $tick = 0
        if ($InputMode -eq 'Swipe') {
            # ── the human gesture model ────────────────────────────────────────────────────────────────────────────
            # Not a metronome. A person scrolling a long list does short flicks and long drags, changes speed inside a
            # swipe, and — the case that matters most — puts their fingers back down while the previous fling is still
            # running, over and over, to get somewhere fast. That re-grab is a different code path from a settled
            # start: it interrupts a decaying fling instead of beginning from rest, and it is where the app was
            # observed to lag. Neither the Stress cadence (never settles) nor a fixed swipe+lift (always settles)
            # produces it.
            #
            # Seeded, so two tours are comparable; the seed is recorded in the report.
            $rng = [System.Random]::new($GestureSeed)
            $direction = -1
            while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
                # One swipe: how far the finger travels, how fast, and how hard it is thrown.
                $len = $rng.Next(4, 21)                       # ticks of travel — a flick to a long drag
                $tickMs = $rng.Next(6, 17)                    # within-swipe speed
                $force = 1 + $rng.Next(0, 3)                  # 1x..3x — an ordinary push vs a hard flick at the end
                for ($k = 0; $k -lt $len; $k++) {
                    if ($sw.Elapsed.TotalSeconds -ge $Seconds) { break }
                    if ([PerfTourNative]::GetForegroundWindow() -ne $Hwnd) { $result.Focus = $false; break }
                    $sent = [PerfTourNative]::Wheel(($direction * [Math]::Abs($Delta) * $force), $true)
                    $result.Sent += $sent
                    if ($sent -ne 1) { $result.Failed++ }
                    Start-Sleep -Milliseconds $tickMs
                    $tick++
                }
                if (-not $result.Focus) { break }
                $result.Swipes++
                # The gap before the next swipe decides WHAT the next swipe interrupts.
                $roll = $rng.Next(0, 100)
                if ($roll -lt 45) {
                    # Re-grab: fingers back down while the fling is still moving. The dominant gesture when someone is
                    # trying to reach the end of a long list, and the one the old cadences never produced.
                    $rest = $rng.Next(20, 70); $result.Regrabs++
                } elseif ($roll -lt 80) {
                    $rest = $rng.Next(110, 260)               # an ordinary lift and re-place
                } else {
                    $rest = $rng.Next(380, 750)               # a real pause — the list fully settles, then starts cold
                }
                if ($rng.Next(0, 100) -lt 12) { $direction = -$direction }   # occasional reversal
                Start-Sleep -Milliseconds $rest
            }
        }
        else {
            while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
                if ([PerfTourNative]::GetForegroundWindow() -ne $Hwnd) { $result.Focus = $false; break }
                $direction = if ($InputMode -eq 'Varied' -and ($tick % 12) -ge 8) { 1 } else { -1 }
                $sent = [PerfTourNative]::Wheel(($direction * [Math]::Abs($Delta)), ($InputMode -eq 'Varied'))
                $result.Sent += $sent
                if ($sent -ne 1) { $result.Failed++ }
                $delay = if ($InputMode -eq 'Varied') { if (($tick % 12) -eq 7) { 450 } else { 100 } } else { $IntervalMs }
                Start-Sleep -Milliseconds $delay
                $tick++
            }
        }
    }
    Start-Sleep -Milliseconds 700   # let the burst close (scroll.frames flushes 500 ms after the last scroll frame)
    if ($UserScroll -eq 0 -and [PerfTourNative]::GetForegroundWindow() -eq $Hwnd -and [PerfTourNative]::Home() -ne 2) { $result.Failed++ }
    Start-Sleep -Milliseconds 400
    return $result
}

# Close the app the way a user does (WM_CLOSE), never kill it: a killed Wavee leaves its "running" marker behind and
# the NEXT launch offers an unclean-exit crash report for a session that did not crash.
function Stop-WaveeGracefully([int]$ProcessId = 0) {
    $procs = if ($ProcessId -gt 0) { @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) } else { @(Get-Process Wavee -ErrorAction SilentlyContinue) }
    foreach ($p in $procs) {
        if ($null -eq $p) { continue }
        try {
            if ($p.HasExited) { continue }
            if ($p.MainWindowHandle -eq [IntPtr]::Zero) { $coverageIssues.Add("Wavee pid $($p.Id) has no main window for a clean close request") }
            elseif (-not $p.CloseMainWindow()) { $coverageIssues.Add("Wavee pid $($p.Id) rejected the clean close request") }
            if (-not $p.WaitForExit(15000)) { $coverageIssues.Add("Wavee pid $($p.Id) did not exit on close; process remains running and final coverage is unavailable"); Write-Warning $coverageIssues[$coverageIssues.Count - 1] }
        } catch {
            $coverageIssues.Add("Wavee pid $($p.Id) clean shutdown failed: " + $_.Exception.Message)
            Write-Warning $coverageIssues[$coverageIssues.Count - 1]
        }
    }
}

# ── Launch ───────────────────────────────────────────────────────────────────────────────────────────────────────────
if (-not (Test-Path $Exe)) { throw "Wavee.exe not found at $Exe (build Release first)" }
Stop-WaveeGracefully
if (@(Get-Process Wavee -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Existing Wavee did not close; refusing to measure a different process.' }
Start-Sleep -Milliseconds 800
$log = Join-Path $env:LOCALAPPDATA ("Wavee\logs\wavee-" + (Get-Date -Format "yyyyMMdd") + ".log")
$startLines = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
$exeHash = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash
$appRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# Resolved like the build itself (G-237): -Override, then this worktree's EngineRoot.local.props pin, then the
# sibling checkout - a report stamped "engine revision" must name the engine the measured build actually links.
$engineRoot = [IO.Path]::GetFullPath((Resolve-EngineRoot -RepoRoot $appRoot -Override $env:EngineRoot))
$appRevision = (& git -C $appRoot rev-parse HEAD | Out-String).Trim()
$engineRevision = (& git -C $engineRoot rev-parse HEAD | Out-String).Trim()
$appDirty = Get-PerfDirtyFingerprint $appRoot
$engineDirty = Get-PerfDirtyFingerprint $engineRoot
if (-not $appDirty.Ok) { $coverageIssues.Add('App repo dirty fingerprint unavailable: ' + $appDirty.Text) }
if (-not $engineDirty.Ok) { $coverageIssues.Add('Engine repo dirty fingerprint unavailable: ' + $engineDirty.Text) }
$powerState = New-Object PerfTourNative+POWER
$powerKnown = [PerfTourNative]::GetSystemPowerStatus([ref]$powerState)

$launchWall = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "launching $Exe"
$app = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
$processMachine = [uint16]0; $nativeMachine = [uint16]0
$archKnown = $false
try { $archKnown = [PerfTourNative]::IsWow64Process2($app.Handle, [ref]$processMachine, [ref]$nativeMachine) }
catch { $coverageIssues.Add('Architecture query failed during startup: ' + $_.Exception.Message) }
$actualMachine = if ($processMachine -eq 0) { $nativeMachine } else { $processMachine }
$archNames = @{ 0x014c = 'x86'; 0x8664 = 'x64'; 0xaa64 = 'arm64' }
$processArch = if ($archNames.ContainsKey([int]$actualMachine)) { $archNames[[int]$actualMachine] } else { 'unknown' }
$machineArch = if ($archNames.ContainsKey([int]$nativeMachine)) { $archNames[[int]$nativeMachine] } else { 'unknown' }
if (-not $archKnown -or $processArch -eq 'unknown') { $coverageIssues.Add('Actual process architecture unavailable') }
elseif ($actualMachine -ne $nativeMachine) { $coverageIssues.Add("Emulated $processArch on ${machineArch}: invalid native baseline") }
$ready = $false
$startupFailure = ''
while ($launchWall.Elapsed.TotalSeconds -lt 60) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $log) {
        $tail = Get-Content $log -Encoding UTF8 | Select-Object -Skip $startLines
        if ($tail | Where-Object { $_ -match "pid=$($app.Id) .*(protocol-installed|dealer connected)" }) { $ready = $true; break }
        if ($tail | Where-Object { $_ -match "pid=$($app.Id) .*Fatal error" }) { $startupFailure = 'Fatal error before startup readiness'; Write-Host $startupFailure; break }
    }
    $app.Refresh()
    if ($app.HasExited) { $startupFailure = 'Process exited before startup readiness'; break }
}
$readyAfterS = [Math]::Round($launchWall.Elapsed.TotalSeconds, 1)
Write-Host ("ready=" + $ready + " after " + $readyAfterS + "s")
if (-not $ready) {
    if ($startupFailure -eq '') { $startupFailure = 'Startup readiness timed out after 60 seconds; protocol-installed marker missing' }
    $coverageIssues.Add($startupFailure)
    Write-Warning ($startupFailure + '; skipping all navigation, input and heap collection')
}
$startupPolicy = Get-PerfStartupPolicy $ready ([bool]$KeepOpen) ([bool]$CollectHeap)
$hwnd = [IntPtr]::Zero
if ($startupPolicy.RunTour) {
    Start-Sleep -Seconds 8
    $hwnd = Wait-MainWindow -ProcessId $app.Id -TimeoutSeconds 15
    if ($hwnd -eq [IntPtr]::Zero) { Write-Host "warn: no main window handle; scroll bursts will be skipped" }
}
$windowRect = New-Object PerfTourNative+RECT
$windowKnown = $hwnd -ne [IntPtr]::Zero -and [PerfTourNative]::GetWindowRect($hwnd, [ref]$windowRect)
$windowDpi = if ($windowKnown) { [PerfTourNative]::GetDpiForWindow($hwnd) } else { 0 }
if (-not $windowKnown) { $coverageIssues.Add('Window geometry unavailable') }

# ── Tour ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
$stepLog = @()
for ($lap = 1; $startupPolicy.RunTour -and $lap -le $Laps; $lap++) {
    $lapName = if ($lap -eq 1) { "cold" } else { "warm" }
    foreach ($step in $tour) {
        Write-Host ("[" + $lapName + "] nav " + $step.Name)
        $t = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $p = Start-Process -FilePath $Exe -ArgumentList ('"' + $step.Route + '"') -PassThru -WorkingDirectory (Split-Path $Exe)
        if (-not $p.WaitForExit(15000)) { $coverageIssues.Add("Deep-link handoff process did not exit for $lapName $($step.Name)") }
        Start-Sleep -Seconds $SettleSeconds
        $inputResult = $null
        if ($step.Scroll) { $inputResult = Invoke-ScrollBurst -Hwnd $hwnd -Seconds $ScrollSeconds -Delta $WheelPerTick -IntervalMs $TickIntervalMs }
        $stepLog += [pscustomobject]@{ Lap = $lapName; Name = $step.Name; Uri = $step.Route; StartedAt = $t; Scrolled = $step.Scroll; Input = $inputResult }
    }
}
if ($startupPolicy.RunTour) { Start-Sleep -Seconds 6 }   # the last nav window + a periodic mem.sample

# ── Managed heap attribution (JIT builds only: dotnet-gcdump needs the runtime's diagnostic port, which NativeAOT
# does not have). Who owns the managed heap after two laps — the number the mem.sample "heap"/"loh" columns cannot name.
$preCollectionAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$heapCollected = $false
$gcTop = @(); $gcDumpNote = "disabled (use -CollectHeap for attribution; collected runs cannot establish an unpolluted session gate)"
$gcdump = Get-Command dotnet-gcdump -ErrorAction SilentlyContinue
if (-not $startupPolicy.RunTour) { $gcDumpNote = 'skipped: startup readiness absent; no heap collection attempted' }
elseif ($startupPolicy.CollectHeap -and $gcdump -and $Exe -notmatch "\\(native|publish)\\") {
    $heapCollected = $true
    $dmp = Join-Path $env:TEMP ("wavee-tour-" + $app.Id + ".gcdump")
    try {
        & $gcdump.Source collect -p $app.Id -o $dmp 2>&1 | Out-Null
        if (Test-Path $dmp) {
            $rep = & $gcdump.Source report $dmp 2>&1 | Out-String
            $gcTop = @(($rep -split "`r?`n") | Where-Object { $_ -match "^\s*[\d,]+\s+[\d,]+\s+\S" } | Select-Object -First 30)
            $gcDumpNote = "``$dmp``"
        } else { $gcDumpNote = "collect produced no file" }
    } catch { $gcDumpNote = "collect failed: " + $_.Exception.Message }
} elseif ($CollectHeap -and -not $gcdump) { $gcDumpNote = "dotnet-gcdump not installed (dotnet tool install -g dotnet-gcdump)" }
elseif ($CollectHeap) { $gcDumpNote = "skipped: NativeAOT build has no diagnostic port" }

$shutdownAttempted = $startupPolicy.Close
$shutdownCompleted = $false
if ($shutdownAttempted) {
    Stop-WaveeGracefully -ProcessId $app.Id
    Start-Sleep -Seconds 2
    $shutdownCompleted = $null -eq (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)
    if (-not $shutdownCompleted) { $coverageIssues.Add('Measured process remains running after the clean shutdown attempt') }
}

# ── Parse ────────────────────────────────────────────────────────────────────────────────────────────────────────────
$lines = @(if (Test-Path $log) { Get-Content $log -Encoding UTF8 | Select-Object -Skip $startLines | Where-Object { $_ -match "pid=$($app.Id) " } })
$rawLines = $lines
if ($heapCollected) { $lines = @($lines | Where-Object { (Get-T $_) -lt $preCollectionAt }) }
# Each step owns the log lines between its deep-link hand-off and the next one's.
for ($i = 0; $i -lt $stepLog.Count; $i++) {
    $from = $stepLog[$i].StartedAt
    $to = if ($i + 1 -lt $stepLog.Count) { $stepLog[$i + 1].StartedAt } else { [long]::MaxValue }
    $stepLog[$i] | Add-Member -NotePropertyName Lines -NotePropertyValue @($lines | Where-Object { $_ -match " t=(\d+) " -and [long]$Matches[1] -ge $from -and [long]$Matches[1] -lt $to })
}
$firstStepAt = if ($stepLog.Count -gt 0) { $stepLog[0].StartedAt } else { [long]::MaxValue }
$launchLines = @($lines | Where-Object { $_ -match " t=(\d+) " -and [long]$Matches[1] -lt $firstStepAt })

$playbackState = Get-PerfPlaybackState $lines
if ($playbackState.Observed) { $coverageIssues.Add("Playback observed during the tour ($($playbackState.Text)): playing audio during a frame gate is a different scenario than the idle tour") }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Wavee performance tour — " + (Get-Date -Format "yyyy-MM-dd HH:mm"))
[void]$sb.AppendLine()
[void]$sb.AppendLine("- exe: ``$Exe``")
[void]$sb.AppendLine("- SHA256: ``$exeHash``; actual process architecture: $processArch; native machine: $machineArch (IsWow64Process2)")
[void]$sb.AppendLine("- window: $($windowRect.Right - $windowRect.Left) x $($windowRect.Bottom - $windowRect.Top); DPI: $windowDpi")
[void]$sb.AppendLine("- laps: $Laps (first visit in this process, then reopen; disk/network caches are not cleared); settle ${SettleSeconds}s")
[void]$sb.AppendLine("- input: $(if ($UserScroll -gt 0) { 'physical user input, ' + $UserScroll + ' seconds per route' } elseif ($InputMode -eq 'Swipe') { 'Swipe synthetic wheel (human gesture model: variable-length swipes, 1-3x force, 45% re-grab mid-fling / 35% lift / 20% full settle, 12% reversal); ' + $ScrollSeconds + ' seconds, base delta=' + $WheelPerTick + ', seed=' + $GestureSeed } else { $InputMode + ' synthetic wheel; ' + $ScrollSeconds + ' seconds, configured delta=' + $WheelPerTick + ', stress interval=' + $TickIntervalMs + 'ms' })")
[void]$sb.AppendLine("- log: ``$log`` pid=$($app.Id), $($lines.Count) lines")
[void]$sb.AppendLine("- gates: 0 completed UI frames above 8.3 ms including startup, working set <= $WorkingSetGateMB MB, every cold and warm reveal <= $RevealGateMs ms")
[void]$sb.AppendLine('- FrameMs measures completed UI work; PAL input dispatch, FrameCompleted diagnostic overhead and verified screen presentation are not established by this gate.')
[void]$sb.AppendLine('- Reveals mark ready content in the active UI frame, not independently verified presentation. ScrollActive rollups evidence active viewport scrolling, not touchpad gesture classification.')
[void]$sb.AppendLine("- pre-collection boundary (Unix ms): $preCollectionAt; heap capture attempted: $heapCollected; KeepOpen: $KeepOpen")
[void]$sb.AppendLine("- app revision: $appRevision ($($appDirty.Text)); engine revision: $engineRevision ($($engineDirty.Text))")
[void]$sb.AppendLine()

# Launch
[void]$sb.AppendLine("## Launch")
[void]$sb.AppendLine()
if ($ready) { [void]$sb.AppendLine("- protocol-installed (ready) after **${readyAfterS}s** wall (includes the login handshake)") }
else { [void]$sb.AppendLine("- startup **INCOMPLETE** after **${readyAfterS}s** wall: $startupFailure. Navigation, input and heap collection were skipped.") }
[void]$sb.AppendLine("- clean shutdown requested: $shutdownAttempted; process exit observed: $shutdownCompleted")
[void]$sb.AppendLine("- playback state: " + $playbackState.Text)
$firstNav = $launchLines | Where-Object { $_ -match "nav\.frames" } | Select-Object -First 1
if ($firstNav) { [void]$sb.AppendLine("- first route: firstFrameMs=" + (Get-Field $firstNav "firstFrameMs") + " overBudget=" + (Get-Field $firstNav "overBudget") + " worst=" + (Get-Field $firstNav "frameMs") + " ms") }
$launchSlow = @($launchLines | Where-Object { $_ -match "frame\.slow " })
[void]$sb.AppendLine("- slow frames logged before the tour: " + $launchSlow.Count)
foreach ($l in ($launchSlow | Sort-Object { -(Get-Num $_ "frameMs") } | Select-Object -First 3)) {
    [void]$sb.AppendLine("  - " + (Fmt (Get-Num $l "frameMs")) + " ms: flush=" + (Get-Field $l "flush") + " layout=" + (Get-Field $l "layout") + " record=" + (Get-Field $l "record") + " submit=" + (Get-Field $l "submit") + " unaccounted=" + (Get-Field $l "unaccounted") + " comps=" + (Get-Field $l "comps") + " hotAlloc=" + (Get-Field $l "hotAlloc") + " gc=" + (Get-Field $l "gc") + $(if ($l -match "census=\[render-census\] (.+)$") { " census: " + $Matches[1] } else { "" }))
}
$launchMem = $launchLines | Where-Object { $_ -match "mem\.sample" } | Select-Object -First 1
if ($launchMem) { [void]$sb.AppendLine("- first memory sample: ws=" + (Get-Field $launchMem "ws") + " MB heap=" + (Get-Field $launchMem "heap") + " MB imageBytes=" + (Get-Field $launchMem "imageBytes") + " MB gpu=" + (Get-Field $launchMem "bytes") + " MB") }
$launchQueries = @($launchLines | Where-Object { $_ -match "query\.first-pass" })
if ($launchQueries.Count -gt 0) {
    [void]$sb.AppendLine("- first passes during launch: " + $launchQueries.Count + "; slowest:")
    foreach ($q in ($launchQueries | Sort-Object { -(Get-Num $_ "totalMs") } | Select-Object -First 4)) {
        [void]$sb.AppendLine("  - " + $(if ($q -match "query (\S+) totalMs") { $Matches[1] } else { "?" }) + " total=" + (Get-Field $q "totalMs") + " gateWait=" + (Get-Field $q "gateWaitMs") + " join=" + (Get-Field $q "joinMs") + " publish=" + (Get-Field $q "publishMs") + " plan=" + (Get-Field $q "planMs") + " keys=" + (Get-Field $q "keys") + " cold=" + (Get-Field $q "coldKeys"))
    }
}
[void]$sb.AppendLine()

# Per-route table
[void]$sb.AppendLine("## Routes")
[void]$sb.AppendLine()
if ($stepLog.Count -eq 0) { [void]$sb.AppendLine('- No route steps executed because startup readiness was absent.'); [void]$sb.AppendLine() }
[void]$sb.AppendLine("| lap | route | handoff ms | reveal ms | first frame ms | nav frames | over budget | slow33 | stall100 | missed vblanks | worst ms | worst phase | scroll frames | scroll over budget | scroll worst ms | scroll missed | ws MB | heap MB |")
[void]$sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|")
$totalOver = 0; $worstFrame = 0.0; $worstLine = ""; $scrollOverTotal = 0
$details = New-Object System.Text.StringBuilder
foreach ($s in $stepLog) {
    $routeLines = @($s.Lines | Where-Object { $_ -match '\bnav\.route\b' })
    $routeLine = $routeLines | Select-Object -First 1
    if ($routeLines.Count -ne 1) { $coverageIssues.Add("$($s.Lap) $($s.Name): expected one route anchor, observed $($routeLines.Count)") }
    $requestedRoute = if ($s.Uri -match '[?&]route=([^&]+)') { [uri]::UnescapeDataString($Matches[1]) } else { '' }
    $requestedArg = if ($s.Uri -match '[?&]arg=([^&]+)') { [uri]::UnescapeDataString($Matches[1]) } else { '' }
    $expectedRoute = if ($requestedArg -ne '') { $requestedRoute + ':' + $requestedArg } else { $requestedRoute }
    if ((Get-Field $routeLine 'route' '') -ne $expectedRoute -or (Get-Field $routeLine 'arg' '') -ne '-') { $coverageIssues.Add("$($s.Lap) $($s.Name): route identity does not match requested $expectedRoute") }
    $navId = Get-Field $routeLine 'navId' ''
    $navLines = @($lines | Where-Object { $_ -match '\bnav\.frames\b' -and (Get-Field $_ 'navId' '') -eq $navId -and $navId -ne '' })
    $scrollLines = @($lines | Where-Object { $_ -match '\bscroll\.frames\b' -and (Get-Field $_ 'navId' '') -eq $navId -and $navId -ne '' })
    $navRollup = Get-PerfRollup $navLines
    $scrollRollup = Get-PerfRollup $scrollLines
    $nav = $navRollup.WorstLine
    $scroll = $scrollRollup.WorstLine
    if (-not $navRollup.Complete) { $coverageIssues.Add("$($s.Lap) $($s.Name): navigation frame coverage missing or invalid") }
    if ($s.Scrolled) {
        if (-not $s.Input.Focus -or -not $s.Input.Cursor -or $s.Input.Failed -gt 0 -or ($UserScroll -eq 0 -and $s.Input.Sent -eq 0)) { $coverageIssues.Add("$($s.Lap) $($s.Name): input/focus verification failed") }
        if (-not $scrollRollup.Complete -or @($scrollLines | Where-Object { (Get-T $_) -ge $s.Input.StartedAt }).Count -eq 0) { $coverageIssues.Add("$($s.Lap) $($s.Name): scroll movement not observed during input interval") }
    }
    $mem = $s.Lines | Where-Object { $_ -match "mem\.sample" } | Select-Object -Last 1
    $handoffMs = [double]::NaN; $revealMs = [double]::NaN; $reveal = $null
    if ($routeLine) {
        $tRoute = Get-T $routeLine
        $handoffMs = $tRoute - $s.StartedAt
        $revealResult = Get-PerfReveal $routeLine $lines
        $reveal = $revealResult.Line; $revealMs = $revealResult.Ms
        if (-not $revealResult.Complete) { $coverageIssues.Add("$($s.Lap) $($s.Name): missing, duplicate or foreign reveal for navId=$navId") }
    }
    $over = $navRollup.overBudget
    $worst = $navRollup.Worst
    $totalOver += $over
    if (-not [double]::IsNaN($worst) -and $worst -gt $worstFrame) { $worstFrame = $worst; $worstLine = "$($s.Lap) $($s.Name) nav" }
    $sOver = $scrollRollup.overBudget
    $sWorst = $scrollRollup.Worst
    $scrollOverTotal += $sOver
    if (-not [double]::IsNaN($sWorst) -and $sWorst -gt $worstFrame) { $worstFrame = $sWorst; $worstLine = "$($s.Lap) $($s.Name) scroll" }
    $ws = if ($mem) { Get-Num $mem "ws" } else { [double]::NaN }
    # worst phase: the largest of the worst frame's engine phases
    $phase = "-"
    if ($nav) {
        $ph = @{}
        foreach ($k in "flush", "layout", "anim", "record", "submit", "fenceWait", "present", "unaccounted") { $ph[$k] = Get-Num $nav $k }
        $top = $ph.GetEnumerator() | Where-Object { -not [double]::IsNaN($_.Value) } | Sort-Object -Property Value -Descending | Select-Object -First 1
        if ($top) { $phase = $top.Key + "=" + (Fmt $top.Value) }
    }
    [void]$sb.AppendLine("| $($s.Lap) | $($s.Name) | $(Fmt $handoffMs) | $(Fmt $revealMs) | $(if ($nav) { Get-Field $nav 'firstFrameMs' } else { '-' }) | $($navRollup.frames) | $over | $($navRollup.slow33) | $($navRollup.stall100) | $($navRollup.missedVblanks) | $(Fmt $worst) | $phase | $($scrollRollup.frames) | $sOver | $(Fmt $sWorst) | $($scrollRollup.missedVblanks) | $(Fmt $ws) | $(if ($mem) { Get-Field $mem 'heap' } else { '-' }) |")

    # Details block per step
    [void]$details.AppendLine("### $($s.Lap) · $($s.Name)")
    [void]$details.AppendLine()
    [void]$details.AppendLine("- rollups: nav=$($navLines.Count), scroll=$($scrollLines.Count); totals include every matching navId rollup. Navigation and scrolling windows overlap and are not added into session totals.")
    [void]$details.AppendLine("- end-to-end handoff + reveal: $(Fmt ($handoffMs + $revealMs)) ms; reveal marker: ``$reveal``")
    if ($s.Input) { [void]$details.AppendLine("- input: mode=$($s.Input.Mode) sent=$($s.Input.Sent) failures=$($s.Input.Failed) focused=$($s.Input.Focus) cursorPositioned=$($s.Input.Cursor)") }
    foreach ($rollupLine in @($navLines + $scrollLines)) { [void]$details.AppendLine('- rollup: `' + $rollupLine + '`') }
    if ($nav -and $nav -match "worstCensus=\[render-census\] (.+)$") { [void]$details.AppendLine("- nav worst frame census: ``" + $Matches[1] + "``") }
    if ($nav) { [void]$details.AppendLine("- nav worst frame: frameMs=" + (Get-Field $nav "frameMs") + " flush=" + (Get-Field $nav "flush") + " reactive=" + (Get-Field $nav "reactive") + " realize=" + (Get-Field $nav "realize") + " layout=" + (Get-Field $nav "layout") + " anim=" + (Get-Field $nav "anim") + " record=" + (Get-Field $nav "record") + " submit=" + (Get-Field $nav "submit") + " present=" + (Get-Field $nav "present") + " unaccounted=" + (Get-Field $nav "unaccounted") + " comps=" + (Get-Field $nav "comps") + " hotAlloc=" + (Get-Field $nav "hotAlloc") + " measures=" + (Get-Field $nav "measures") + " shapes=" + (Get-Field $nav "shapes") + " gc=" + (Get-Field $nav "gc") + "; window comps=" + $(if ($nav -match " comps=(\d+) hotAllocKB") { $Matches[1] } else { "-" }) + " hotAllocKB=" + (Get-Field $nav "hotAllocKB")) }
    if ($scroll) {
        [void]$details.AppendLine("- scroll: frames=" + (Get-Field $scroll "frames") + " fps=" + (Get-Field $scroll "fps") + " avgFrameMs=" + (Get-Field $scroll "avgFrameMs") + " overBudget=" + (Get-Field $scroll "overBudget") + " missedVblanks=" + (Get-Field $scroll "missedVblanks") + " comps=" + $(if ($scroll -match " comps=(\d+) hotAllocKB") { $Matches[1] } else { "-" }) + " hotAllocKB=" + (Get-Field $scroll "hotAllocKB") + " gc=" + (Get-Field $scroll "gc") + " worst: frameMs=" + (Get-Field $scroll "frameMs") + " flush=" + (Get-Field $scroll "flush") + " layout=" + (Get-Field $scroll "layout") + " record=" + (Get-Field $scroll "record") + " submit=" + (Get-Field $scroll "submit") + " unaccounted=" + (Get-Field $scroll "unaccounted"))
        if ($scroll -match "worstCensus=\[render-census\] (.+)$") { [void]$details.AppendLine("- scroll worst frame census: ``" + $Matches[1] + "``") }
    } elseif ($s.Scrolled) { [void]$details.AppendLine("- scroll: burst sent but no scroll.frames line (fewer than 5 ScrollActive frames — the wheel missed the list or the page did not scroll)") }
    $slow = @($s.Lines | Where-Object { $_ -match "frame\.slow " })
    $suppressed = 0; foreach ($l in ($s.Lines | Where-Object { $_ -match "frame\.slow\.suppressed" })) { $suppressed += [int](Get-Num $l "count") }
    [void]$details.AppendLine("- slow frames: " + $slow.Count + " logged" + $(if ($suppressed -gt 0) { " (+$suppressed suppressed by the per-second cap)" } else { "" }))
    foreach ($l in ($slow | Sort-Object { -(Get-Num $_ "frameMs") } | Select-Object -First 4)) {
        [void]$details.AppendLine("  - " + (Fmt (Get-Num $l "frameMs")) + " ms @" + (Get-Field $l "sinceNavMs") + "ms scroll=" + (Get-Field $l "scroll") + " flush=" + (Get-Field $l "flush") + " layout=" + (Get-Field $l "layout") + " anim=" + (Get-Field $l "anim") + " record=" + (Get-Field $l "record") + " submit=" + (Get-Field $l "submit") + " present=" + (Get-Field $l "present") + " unaccounted=" + (Get-Field $l "unaccounted") + " comps=" + (Get-Field $l "comps") + " hotAlloc=" + (Get-Field $l "hotAlloc") + " measures=" + (Get-Field $l "measures") + " gc=" + (Get-Field $l "gc") + $(if ($l -match "census=\[render-census\] (.+)$") { "`n    census: ``" + $Matches[1] + "``" } else { "" }))
    }
    $queries = @($s.Lines | Where-Object { $_ -match "query\.(first-pass|pass) " })
    if ($queries.Count -gt 0) {
        [void]$details.AppendLine("- query passes: " + $queries.Count + " (first-pass " + @($queries | Where-Object { $_ -match "query\.first-pass" }).Count + "); slowest:")
        foreach ($q in ($queries | Sort-Object { -(Get-Num $_ "totalMs") } | Select-Object -First 4)) {
            [void]$details.AppendLine("  - " + $(if ($q -match "query (\S+) totalMs") { $Matches[1] } else { "?" }) + " total=" + (Get-Field $q "totalMs") + " gateWait=" + (Get-Field $q "gateWaitMs") + " join=" + (Get-Field $q "joinMs") + " require=" + (Get-Field $q "requireMs") + " publish=" + (Get-Field $q "publishMs") + " plan=" + (Get-Field $q "planMs") + " keys=" + (Get-Field $q "keys") + " cold=" + (Get-Field $q "coldKeys") + " hasData=" + (Get-Field $q "hasData") + " sinceNav=" + (Get-Field $q "sinceNavMs"))
        }
    }
    $commits = @($s.Lines | Where-Object { $_ -match "commit\.slow|catalog\.gate\.wait|catalog\.publish\.slow" })
    if ($commits.Count -gt 0) {
        [void]$details.AppendLine("- commit queue / publication gate: " + $commits.Count + " slow lines")
        foreach ($c in ($commits | Select-Object -First 4)) { [void]$details.AppendLine("  - " + $(if ($c -match "\] (\S+) - (.+)$") { $Matches[1] + ": " + $Matches[2] } else { $c })) }
    }
    $waves = @($s.Lines | Where-Object { $_ -match "catalog\.demand\.wave|catalog\.fetch\.batch" })
    if ($waves.Count -gt 0) { [void]$details.AppendLine("- catalog demand waves / fetch batches: " + $waves.Count) }
    $errors = @($s.Lines | Where-Object { $_ -match " (E|C) \[| W \[.*(Surface error|Fatal|exception|HTTP 4|HTTP 5)" })
    if ($errors.Count -gt 0) { [void]$details.AppendLine("- errors: " + $errors.Count); foreach ($e in ($errors | Select-Object -First 3)) { [void]$details.AppendLine("  - " + $(if ($e -match "\] (.+)$") { $Matches[1] } else { $e })) } }
    if ($mem) { [void]$details.AppendLine("- memory at step end: " + $(if ($mem -match "memory (.+)$") { $Matches[1] } else { $mem })) }
    [void]$details.AppendLine()
}
[void]$sb.AppendLine()

# Memory over time
[void]$sb.AppendLine("## Memory over the tour")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| when | ws MB | private MB | heap MB | committed MB | loh MB | gcs | alloc MB/s | image MB | gpu MB | components | scene | catalog resident | query deps |")
[void]$sb.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($m in ($lines | Where-Object { $_ -match "mem\.sample" })) {
    $when = if ($m -match "memory (\S+(?: route=\S+)?)") { $Matches[1] } else { "?" }
    [void]$sb.AppendLine("| $when | $(Get-Field $m 'ws') | $(Get-Field $m 'private') | $(Get-Field $m 'heap') | $(Get-Field $m 'committed') | $(Get-Field $m 'loh') | $(Get-Field $m 'gcs') | $(Get-Field $m 'allocMBs') | $(Get-Field $m 'imageBytes') | $(Get-Field $m 'bytes') | $(Get-Field $m 'components') | $(Get-Field $m 'scene') | $(Get-Field $m 'resident') | $(Get-Field $m 'deps') |")
}
[void]$sb.AppendLine()

# Census aggregate across every slow frame: which component types keep showing up.
$censusAgg = @{}
foreach ($l in ($lines | Where-Object { $_ -match '\bframe\.slow\b' -and $_ -match 'census=\[render-census\]' })) {
    if ($l -match "top=([^\s]+(?: [^\s]+)*?) bytes=") {
        $topCensus = $Matches[1]
        foreach ($m in [regex]::Matches($topCensus, '([A-Za-z0-9_`]+)×(\d+)\(r=([\d.]+) c=([\d.]+) a=(\d+)K\)')) {
            $name = $m.Groups[1].Value
            if (-not $censusAgg.ContainsKey($name)) { $censusAgg[$name] = [pscustomobject]@{ Frames = 0; Renders = 0; Ms = 0.0; KB = 0 } }
            $e = $censusAgg[$name]; $e.Frames++; $e.Renders += [int]$m.Groups[2].Value
            $e.Ms += [double]::Parse($m.Groups[3].Value, [cultureinfo]::InvariantCulture) + [double]::Parse($m.Groups[4].Value, [cultureinfo]::InvariantCulture)
            $e.KB += [int]$m.Groups[5].Value
        }
    }
}
if ($censusAgg.Count -gt 0) {
    [void]$sb.AppendLine("## Components on slow frames (census aggregate)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| component | slow frames it rendered in | renders | render+reconcile ms | UI-thread KB |")
    [void]$sb.AppendLine("|---|---:|---:|---:|---:|")
    foreach ($kv in ($censusAgg.GetEnumerator() | Sort-Object { -$_.Value.Ms } | Select-Object -First 20)) {
        [void]$sb.AppendLine("| $($kv.Key) | $($kv.Value.Frames) | $($kv.Value.Renders) | $(Fmt $kv.Value.Ms) | $($kv.Value.KB) |")
    }
    [void]$sb.AppendLine()
}

# Managed heap owners at the end of the tour
[void]$sb.AppendLine("## Managed heap at tour end (dotnet-gcdump)")
[void]$sb.AppendLine()
if ($gcTop.Count -gt 0) {
    [void]$sb.AppendLine("Top types by retained size, from " + $gcDumpNote + ":")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('```')
    foreach ($g in $gcTop) { [void]$sb.AppendLine($g.TrimEnd()) }
    [void]$sb.AppendLine('```')
} else { [void]$sb.AppendLine("- " + $gcDumpNote) }
[void]$sb.AppendLine()

# Whole-session verdict. Window sums are attribution only, and can overlap.
$session = Get-PerfSession -Lines $lines -CoverageComplete ($coverageIssues.Count -eq 0 -and $shutdownAttempted) -HeapCollected $heapCollected -WorkingSetGateMB $WorkingSetGateMB -RevealGateMs $RevealGateMs
[void]$sb.AppendLine("## Verdict")
[void]$sb.AppendLine()
$allSlow = @($lines | Where-Object { $_ -match "frame\.slow " })
$allSuppressed = 0; foreach ($l in ($lines | Where-Object { $_ -match "frame\.slow\.suppressed" })) { $allSuppressed += [int](Get-Num $l "count") }
$stalls = @($allSlow | Where-Object { (Get-Num $_ "frameMs") -ge 100 }).Count
[void]$sb.AppendLine("- completed UI-frame gate: **$($session.FramesVerdict)**; session frames=$(Fmt $session.Frames '0'), over 8.3 ms=$(Fmt $session.Over83 '0'), worst=$(Fmt $session.WorstMs '0.000000') ms; final snapshots=$($session.FinalCount). Startup and late refresh are included.")
[void]$sb.AppendLine("- attribution: nav over-refresh=$totalOver, scroll over-refresh=$scrollOverTotal (overlapping); $($allSlow.Count) slow exemplars (+$allSuppressed suppressed), $stalls logged >=100 ms. These counts do not establish session acceptance.")
[void]$sb.AppendLine("- working-set gate: **$($session.MemoryVerdict)**; all-sample maximum=$(Fmt $session.SampledPeak) MB; OS lifetime process peak=$(Fmt $session.ProcessPeak) MB; threshold=$WorkingSetGateMB MB.")
[void]$sb.AppendLine("- every cold/warm/startup reveal <=${RevealGateMs}ms: **$($session.RevealVerdict)**; route anchors=$($session.Routes), known violations=$($session.RevealBreaches.Count). Missing/duplicate identities cannot pass.")
foreach ($breach in $session.RevealBreaches) { [void]$sb.AppendLine("  - navId=$($breach.NavId): $(Fmt $breach.Ms '0.000000') ms") }
if (-not $shutdownAttempted) { [void]$sb.AppendLine('- INCOMPLETE: process left open (KeepOpen), so final shutdown coverage is missing.') }
if ($heapCollected) { [void]$sb.AppendLine('- INCOMPLETE: diagnostic collection perturbs the session; only pre-collection evidence is analyzed, and no unpolluted final counter exists.') }
foreach ($issue in $coverageIssues) { [void]$sb.AppendLine('- INCOMPLETE coverage: ' + $issue) }
[void]$sb.AppendLine('- Full butter-smooth acceptance additionally requires input/presentation timing, controlled cold-data coverage, physical touchpad coverage and repeated native runs; this report alone does not establish them.')
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Per-route detail")
[void]$sb.AppendLine()
[void]$sb.Append($details.ToString())

if ($Out -eq "") { $Out = Join-Path $env:TEMP ("wavee-perf-tour-" + (Get-Date -Format "yyyyMMdd-HHmm") + ".md") }
$sb.ToString() | Set-Content $Out -Encoding UTF8
$raw = [System.IO.Path]::ChangeExtension($Out, ".log")
$rawLines | Set-Content $raw -Encoding UTF8
$manifestPath = [System.IO.Path]::ChangeExtension($Out, '.manifest.json')
$manifest = [ordered]@{
    Schema = 1; Executable = (Resolve-Path -LiteralPath $Exe).Path; Sha256 = $exeHash
    ProcessId = $app.Id; ProcessArchitecture = $processArch; NativeArchitecture = $machineArch
    WindowWidth = $windowRect.Right - $windowRect.Left; WindowHeight = $windowRect.Bottom - $windowRect.Top; WindowDpi = $windowDpi
    Laps = $Laps; SettleSeconds = $SettleSeconds; ScrollSeconds = $ScrollSeconds; InputMode = $InputMode; UserScrollSeconds = $UserScroll
    GestureSeed = $GestureSeed
    WheelDelta = $WheelPerTick; TickIntervalMs = $TickIntervalMs; PreCollectionUnixMs = $preCollectionAt; HeapCaptureAttempted = $heapCollected
    Ready = $ready; ReadyAfterSeconds = $readyAfterS; KeepOpen = [bool]$KeepOpen
    StartupFailure = $startupFailure; ShutdownAttempted = [bool]$shutdownAttempted; ShutdownCompleted = [bool]$shutdownCompleted
    CacheCondition = 'Existing user cache; first visit in fresh process is not controlled cold data'
    PowerState = $(if ($powerKnown) { @{ ACLineStatus = $powerState.ACLineStatus; BatteryLifePercent = $powerState.BatteryLifePercent; BatterySaver = $powerState.SystemStatusFlag } } else { 'unmeasured' })
    PlaybackState = $playbackState.Text; RefreshInterval = 'per-frame budgetMs in raw log; rounded display only'
    AppRevision = $appRevision; EngineRevision = $engineRevision
    DirtyFingerprints = @{ App = $appDirty.Text; Engine = $engineDirty.Text }
    Steps = @($stepLog | Select-Object Lap, Name, Uri, StartedAt, Scrolled, Input)
    CoverageIssues = @($coverageIssues); FramesVerdict = $session.FramesVerdict; MemoryVerdict = $session.MemoryVerdict; RevealVerdict = $session.RevealVerdict
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host ("wrote " + $Out)
Write-Host ("raw log slice: " + $raw + " (" + $lines.Count + " lines)")
Write-Host ""
Write-Host ("frames=" + $session.FramesVerdict + " over83=" + $session.Over83 + " worst=" + (Fmt $session.WorstMs) + "ms memory=" + $session.MemoryVerdict + " peakWS=" + (Fmt $session.PeakWs) + "MB reveals=" + $session.RevealVerdict)
Write-Host ("manifest: " + $manifestPath)

