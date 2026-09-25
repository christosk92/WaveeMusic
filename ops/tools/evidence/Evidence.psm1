#requires -Version 5.1
<#
    Evidence harness helpers (docs/plans/evidence-diagnostics-implementation.md §C, in ..\fluent-gpu) — shared by
    Start-VerifyWavee.ps1, Send-WaveeDiag.ps1 and Invoke-Scenario.ps1, and unit-tested by
    ops/release/tests/Evidence.Tests.ps1 (the PURE functions: reply / TSV parsing, the band geometry). Run from Windows
    PowerShell 5.1 or pwsh, sandbox-free (the GPU is not visible inside the sandbox).

    Rules this module keeps (§C.6): it NEVER stops a process; windows are found BY PID (never FindWindow by class — the
    owner's Wavee has the same class); input bursts wait until the user has been idle and hand the foreground back.
#>

$ErrorActionPreference = 'Stop'

if (-not ('EvidenceWin32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class EvidenceWin32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct COPYDATASTRUCT { public UIntPtr dwData; public uint cbData; public IntPtr lpData; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, UIntPtr w, ref COPYDATASTRUCT l, uint f, uint t, out UIntPtr r);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    public static IntPtr FindByPid(uint pid, string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(64); GetClassNameW(h, sb, 64);
            if (sb.ToString() != cls) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static long SendUri(IntPtr h, string uri) {
        IntPtr buf = Marshal.StringToHGlobalUni(uri);
        try {
            var cds = new COPYDATASTRUCT { dwData = (UIntPtr)0x46474143u, cbData = (uint)(uri.Length * 2), lpData = buf };
            UIntPtr r;
            IntPtr ok = SendMessageTimeoutW(h, 0x004A, UIntPtr.Zero, ref cds, 0x0002, 5000, out r);
            return ok.ToInt64();
        } finally { Marshal.FreeHGlobal(buf); }
    }
}
'@
    [void][EvidenceWin32]::SetProcessDpiAwarenessContext([IntPtr](-4))
}

# ── pure (tested) ──────────────────────────────────────────────────────────────────────────────────────────────────

function ConvertFrom-EvidenceTsv {
    <# A bundle TSV (header row, then rows; '#' comment lines skipped) → objects with the header's column names. #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Lines)
    $data = @($Lines | Where-Object { $_ -and -not $_.StartsWith('#') })
    if ($data.Count -eq 0) { return @() }
    $cols = $data[0].Split("`t")
    $out = New-Object System.Collections.Generic.List[object]
    for ($i = 1; $i -lt $data.Count; $i++) {
        $cells = $data[$i].Split("`t")
        $o = [ordered]@{}
        for ($c = 0; $c -lt $cols.Count; $c++) { $o[$cols[$c]] = if ($c -lt $cells.Count) { $cells[$c] } else { '' } }
        $out.Add([pscustomobject]$o)
    }
    return $out.ToArray()
}

function Read-EvidenceTsv {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { return @() }
    return ConvertFrom-EvidenceTsv -Lines (Get-Content -LiteralPath $Path -Encoding UTF8)
}

function ConvertFrom-EvidenceReplies {
    <# replies.tsv lines "seq \t pid \t cmd \t result \t path" → objects. #>
    param([AllowEmptyCollection()][string[]]$Lines)
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($l in $Lines) {
        if (-not $l) { continue }
        $c = $l.Split("`t")
        if ($c.Count -lt 5) { continue }
        $out.Add([pscustomobject]@{ Seq = [int]$c[0]; Pid = [int]$c[1]; Cmd = $c[2]; Result = $c[3]; Path = $c[4] })
    }
    return $out.ToArray()
}

function Get-EvidenceReplies {
    param([Parameter(Mandatory)][string]$Root)
    $p = Join-Path $Root 'replies.tsv'
    if (-not (Test-Path $p)) { return @() }
    $fs = [System.IO.File]::Open($p, 'Open', 'Read', 'ReadWrite')
    try { $text = (New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    return ConvertFrom-EvidenceReplies -Lines ($text -split "`n")
}

function Get-BandGeometry {
    <#
        The artist band's scroll geometry from a bundle's keyed.tsv at REST (offset 0): the viewport keyed
        artist-scroll:* and the magazine keyed artist-under-band share the sentinel's content y, so the collapse distance
        is  cd = (y(magazine) − y(viewport)) − 56  and the 44-DIP reveal ramp is [cd − 44, cd]. Returns $null when either
        key is missing.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Keyed, [string]$ArtistUri, [double]$BandHeight = 56, [double]$Reveal = 44)
    $ci = [System.Globalization.CultureInfo]::InvariantCulture
    # A restored session keeps OTHER artist pages alive (hidden tabs): pick the named artist's viewport, and the
    # magazine in that viewport's column (the nearest x), never simply the first keyed row.
    $vp = if ($ArtistUri) { $Keyed | Where-Object { $_.path -eq ('artist-scroll:artist:' + $ArtistUri) } | Select-Object -First 1 }
          else { $Keyed | Where-Object { $_.path -like 'artist-scroll:*' } | Select-Object -First 1 }
    if (-not $vp) { return $null }
    $vx = [double]::Parse($vp.x, $ci)
    $mag = $Keyed | Where-Object { $_.path -eq 'artist-under-band' } |
        Sort-Object { [math]::Abs([double]::Parse($_.x, $ci) - $vx) } | Select-Object -First 1
    if (-not $mag) { return $null }
    $contentY = [double]::Parse($mag.y, $ci) - [double]::Parse($vp.y, $ci)
    $cd = $contentY - $BandHeight
    [pscustomobject]@{ ViewportX = $vx; ViewportY = [double]::Parse($vp.y, $ci); ViewportW = [double]::Parse($vp.w, $ci)
        MagazineContentY = $contentY; CollapseDistance = $cd; RampStart = $cd - $Reveal }
}

function Get-BandSweepOffsets {
    <# The §C.1 offsets: rest (0), the 44-DIP reveal ramp in 4-DIP steps through cd + 4, and pinned (cd + 200). #>
    param([Parameter(Mandatory)][double]$CollapseDistance, [double]$Step = 4, [double]$Reveal = 44, [double]$Pinned = 200)
    $o = New-Object System.Collections.Generic.List[double]
    $o.Add(0)
    for ($x = $CollapseDistance - $Reveal; $x -le $CollapseDistance + $Step + 1e-6; $x += $Step) { $o.Add([math]::Round($x, 3)) }
    $o.Add([math]::Round($CollapseDistance + $Pinned, 3))
    return $o.ToArray()
}

function Test-OwnerIdle {
    <# True when the user has not touched keyboard/mouse for $Ms. #>
    param([int]$Ms = 4000)
    $li = New-Object EvidenceWin32+LASTINPUTINFO; $li.cbSize = 8
    [void][EvidenceWin32]::GetLastInputInfo([ref]$li)
    return (([Environment]::TickCount - $li.dwTime) -ge $Ms)
}

# ── the running verify instance ────────────────────────────────────────────────────────────────────────────────────

function Wait-WindowByPid {
    param([Parameter(Mandatory)][int]$ProcessId, [string]$Class = 'FluentGpuWindow', [int]$TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { throw "process $ProcessId exited before showing a window (a single-instance hand-off? the build must be profile-scoped, §A.7)" }
        $h = [EvidenceWin32]::FindByPid([uint32]$ProcessId, $Class)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 250
    }
    throw "no $Class window for pid $ProcessId within $TimeoutSec s"
}

function Send-WaveeUri {
    <# WM_COPYDATA (dwData 'FGAC') straight to the window handle — the activation path SingleInstanceGate uses. #>
    param([Parameter(Mandatory)][IntPtr]$Hwnd, [Parameter(Mandatory)][string]$Uri)
    $r = [EvidenceWin32]::SendUri($Hwnd, $Uri)
    if ($r -eq 0) { throw "SendMessageTimeout failed/timed out for $Uri" }
}

function Invoke-WaveeDiag {
    <#
        Send a wavee://diag verb and wait for ITS reply line in <profile>\logs\evidence\replies.tsv (WM_COPYDATA is
        one-way; the app answers with files). Returns the reply object.
    #>
    param([Parameter(Mandatory)][IntPtr]$Hwnd, [Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$EvidenceRoot,
          [int]$TimeoutMs = 8000)
    $cmd = if ($Uri -match 'cmd=([a-z]+)') { $Matches[1] } else { throw "no cmd= in $Uri" }
    $before = @(Get-EvidenceReplies -Root $EvidenceRoot).Count
    Send-WaveeUri -Hwnd $Hwnd -Uri $Uri
    $deadline = [Environment]::TickCount + $TimeoutMs
    while ([Environment]::TickCount -lt $deadline) {
        $all = @(Get-EvidenceReplies -Root $EvidenceRoot)
        if ($all.Count -gt $before) {
            $mine = $all[$before..($all.Count - 1)] | Where-Object { $_.Cmd -eq $cmd } | Select-Object -Last 1
            if ($mine) { return $mine }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "no reply to $Uri within $TimeoutMs ms (is developer mode on in the verify profile's settings.json?)"
}

function Send-WheelBurst {
    <#
        Wheel input over client DIP ($X,$Y): $Count packets of $Delta (120 = one detent, -15 = a hi-res eighth) every
        $IntervalMs. Waits for the owner to be idle ≥ $IdleMs, brings the window to the foreground (the engine ignores
        input while inactive), then hands the foreground and the cursor back (Drive-WaveeWindow.ps1's dance).
    #>
    param([Parameter(Mandatory)][IntPtr]$Hwnd, [double]$X, [double]$Y, [int]$Delta = -120, [int]$Count = 1,
          [int]$IntervalMs = 250, [int]$IdleMs = 4000)
    for ($i = 0; $i -lt 240 -and -not (Test-OwnerIdle -Ms $IdleMs); $i++) { Start-Sleep -Milliseconds 500 }
    $scale = [EvidenceWin32]::GetDpiForWindow($Hwnd) / 96.0
    $pt = New-Object EvidenceWin32+POINT; $pt.X = [int]($X * $scale); $pt.Y = [int]($Y * $scale)
    [void][EvidenceWin32]::ClientToScreen($Hwnd, [ref]$pt)
    $prevFg = [EvidenceWin32]::GetForegroundWindow()
    $cur = New-Object EvidenceWin32+POINT; [void][EvidenceWin32]::GetCursorPos([ref]$cur)
    $fgTid = [EvidenceWin32]::GetWindowThreadProcessId($prevFg, [ref]([uint32]0)); $me = [EvidenceWin32]::GetCurrentThreadId()
    [void][EvidenceWin32]::AttachThreadInput($me, $fgTid, $true); [void][EvidenceWin32]::BringWindowToTop($Hwnd)
    [void][EvidenceWin32]::SetForegroundWindow($Hwnd); [void][EvidenceWin32]::AttachThreadInput($me, $fgTid, $false)
    Start-Sleep -Milliseconds 200
    [void][EvidenceWin32]::SetCursorPos($pt.X, $pt.Y); Start-Sleep -Milliseconds 60
    [EvidenceWin32]::mouse_event(1, 0, 0, 0, [IntPtr]::Zero)
    for ($i = 0; $i -lt $Count; $i++) {
        [EvidenceWin32]::mouse_event(0x0800, 0, 0, $Delta, [IntPtr]::Zero)
        if ($IntervalMs -gt 0) { Start-Sleep -Milliseconds $IntervalMs }
    }
    [void][EvidenceWin32]::SetCursorPos($cur.X, $cur.Y)
    $tid2 = [EvidenceWin32]::GetWindowThreadProcessId($Hwnd, [ref]([uint32]0))
    [void][EvidenceWin32]::AttachThreadInput($me, $tid2, $true); [void][EvidenceWin32]::SetForegroundWindow($prevFg)
    [void][EvidenceWin32]::AttachThreadInput($me, $tid2, $false)
}

function Get-ThumbGeometry {
    <#
        PURE (tested). Where a vertical overlay scrollbar's THUMB sits for a viewport row of vps.tsv (window DIP), in the
        EXPANDED bar the drag grabs (Send-ThumbDrag hovers the lane first) — the engine's own metrics
        (FluentGpu InputDispatcher.TryGetScrollbarMetrics, the recorder's EmitScrollbar): a 12-DIP arrow button at each
        end, track = H − 2·12, thumb = min(track, max(30, clamp(H/extent, 0.08, 1)·track)), travel = track − thumb, the
        thumb's top = 12 + t·travel. X is the lane's centre (the 12-DIP lane at the viewport's right edge; the expanded
        thumb paints [right−9, right−3]). TrackTop is where a thumb at t = 0 starts, so a target fraction f maps to a
        centre at TrackTop + Length/2 + f·Travel. Returns $null for a viewport with nothing to scroll.
    #>
    param([Parameter(Mandatory)][double]$X, [Parameter(Mandatory)][double]$Y, [Parameter(Mandatory)][double]$W,
          [Parameter(Mandatory)][double]$H, [Parameter(Mandatory)][double]$Offset, [Parameter(Mandatory)][double]$Extent,
          [double]$Button = 12.0, [double]$MinThumb = 30.0, [double]$MinFraction = 0.08)
    if ($Extent -le $H + 0.5 -or $H -le 1) { return $null }
    $track = [math]::Max(1.0, $H - 2 * $Button)
    $fraction = [math]::Min(1.0, [math]::Max($MinFraction, $H / $Extent))
    $thumb = [math]::Min($track, [math]::Max($MinThumb, $fraction * $track))
    $travel = [math]::Max(1.0, $track - $thumb)
    $t = [math]::Min(1.0, [math]::Max(0.0, $Offset / [math]::Max(1.0, $Extent - $H)))
    $trackTop = $Y + $Button
    [pscustomobject]@{ X = $X + $W - $Button / 2; Y = $trackTop + $t * $travel + $thumb / 2; Length = $thumb; Travel = $travel
        TrackTop = $trackTop; Top = $Y; Bottom = $Y + $H }
}

function Send-ThumbDrag {
    <#
        A REAL left-button drag of a scrollbar thumb, client DIP: hover the bar lane first (the overlay bar expands on
        hover), press at ($X,$Y0), move to ($X,$Y1) in $Steps steps $StepMs apart, release. Same foreground / idle / hand-back
        dance as Send-WheelBurst.
    #>
    param([Parameter(Mandatory)][IntPtr]$Hwnd, [double]$X, [double]$Y0, [double]$Y1, [int]$Steps = 24, [int]$StepMs = 8,
          [int]$HoverMs = 450, [int]$IdleMs = 4000,
          # Run WHILE THE BUTTON IS HELD, after step $MidwayStep (the thumb still captured): mid-drag evidence.
          [scriptblock]$Midway, [int]$MidwayStep = -1)
    for ($i = 0; $i -lt 240 -and -not (Test-OwnerIdle -Ms $IdleMs); $i++) { Start-Sleep -Milliseconds 500 }
    $scale = [EvidenceWin32]::GetDpiForWindow($Hwnd) / 96.0
    function ToScreen([double]$cx, [double]$cy) {
        $p = New-Object EvidenceWin32+POINT; $p.X = [int]($cx * $scale); $p.Y = [int]($cy * $scale)
        [void][EvidenceWin32]::ClientToScreen($Hwnd, [ref]$p); $p
    }
    $prevFg = [EvidenceWin32]::GetForegroundWindow()
    $cur = New-Object EvidenceWin32+POINT; [void][EvidenceWin32]::GetCursorPos([ref]$cur)
    $fgTid = [EvidenceWin32]::GetWindowThreadProcessId($prevFg, [ref]([uint32]0)); $me = [EvidenceWin32]::GetCurrentThreadId()
    [void][EvidenceWin32]::AttachThreadInput($me, $fgTid, $true); [void][EvidenceWin32]::BringWindowToTop($Hwnd)
    [void][EvidenceWin32]::SetForegroundWindow($Hwnd); [void][EvidenceWin32]::AttachThreadInput($me, $fgTid, $false)
    Start-Sleep -Milliseconds 200
    $a = ToScreen ($X - 30) $Y0; [void][EvidenceWin32]::SetCursorPos($a.X, $a.Y); Start-Sleep -Milliseconds 60
    $b = ToScreen $X $Y0; [void][EvidenceWin32]::SetCursorPos($b.X, $b.Y); [EvidenceWin32]::mouse_event(1, 0, 0, 0, [IntPtr]::Zero)
    Write-Verbose ("thumb press at client DIP ({0:0.0},{1:0.0}) = screen px ({2},{3}) dpi={4}" -f $X, $Y0, $b.X, $b.Y, [EvidenceWin32]::GetDpiForWindow($Hwnd))
    Start-Sleep -Milliseconds $HoverMs
    [EvidenceWin32]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)          # LEFTDOWN at the thumb
    Start-Sleep -Milliseconds 40
    for ($i = 1; $i -le $Steps; $i++) {
        $y = $Y0 + ($Y1 - $Y0) * $i / $Steps
        $p = ToScreen $X $y; [void][EvidenceWin32]::SetCursorPos($p.X, $p.Y); [EvidenceWin32]::mouse_event(1, 0, 0, 0, [IntPtr]::Zero)
        if ($StepMs -gt 0) { Start-Sleep -Milliseconds $StepMs }
        if ($Midway -and $i -eq $MidwayStep) { & $Midway }
    }
    Start-Sleep -Milliseconds 60
    [EvidenceWin32]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)          # LEFTUP
    [void][EvidenceWin32]::SetCursorPos($cur.X, $cur.Y)
    $tid2 = [EvidenceWin32]::GetWindowThreadProcessId($Hwnd, [ref]([uint32]0))
    [void][EvidenceWin32]::AttachThreadInput($me, $tid2, $true); [void][EvidenceWin32]::SetForegroundWindow($prevFg)
    [void][EvidenceWin32]::AttachThreadInput($me, $tid2, $false)
}

Export-ModuleMember -Function ConvertFrom-EvidenceTsv, Read-EvidenceTsv, ConvertFrom-EvidenceReplies, Get-EvidenceReplies,
    Get-BandGeometry, Get-BandSweepOffsets, Test-OwnerIdle, Wait-WindowByPid, Send-WaveeUri, Invoke-WaveeDiag, Send-WheelBurst,
    Get-ThumbGeometry, Send-ThumbDrag
