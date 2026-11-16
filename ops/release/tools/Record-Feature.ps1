#requires -Version 5.1
<#
.SYNOPSIS
  Record a Wavee feature on REAL data: a verify Wavee on a copy of the owner's profile, driven by a script, filmed at the
  display's native refresh rate.

.DESCRIPTION
  Dev-box tool; run it from Windows PowerShell OUTSIDE any sandbox (the GPU, the window capture and ffmpeg are not visible
  inside one), and hold the GPU lock: bash C:/WAVEE/scratch/gpulock.sh powershell -File Record-Feature.ps1 ...

  1. Picks the monitor: the highest refresh rate whose work area holds the window (-Width x -Height DIP at its scale).
  2. Starts a VERIFY Wavee with ops\tools\evidence\Start-VerifyWavee.ps1 on -ProfileDir (its own profile, signed in from a
     copy of store.json minus the Connect device id; the owner's playplay folder copied in so previews can play locally).
     A verify Wavee already running from -Exe is reused and left running; one this script started is closed at the end.
     It NEVER sends cmd=here (a Connect takeover would steal the owner's playback) and never plays or pauses anything.
  3. Sets presentation mode (no account name or picture), the theme, the client size and the route, then plays the script's
     SETUP part (the lines before `#rec`: e.g. `#live ready key=...`), so the take never opens on a loading state.
  4. Records the window (ffmpeg gfxcapture = Windows.Graphics.Capture of THIS window only: nothing that covers it, nothing
     beside it, no OS cursor; a window that shrinks keeps the frame size with black bars) at the refresh rate into a
     lossless intermediate while the script's TAKE part (after `#rec`) plays through wavee://diag?cmd=drive (the app's own
     input queue: no SendInput, the owner's pointer never moves; the app draws the driver's pointer). The window is topmost
     (no activation) for the take. -Audio adds the verify process's own output (WASAPI process loopback, Record-AppAudio.cs):
     nothing else on the machine is heard.
  5. Writes <Out>\take.mkv (lossless), take-<fps>.mp4 (H.264 High, full rate, CFR), share-60.mp4 (60 fps, <= 50 MB),
     sheet.png (a contact sheet), frames.tsv (every captured frame's time), drive\ (the app's steps.tsv / frames.tsv /
     summary.txt) and report.txt: the frame accounting (frames missing inside continuous motion, counted on the capture's
     own timestamps and on the app's presents).

  Recordings hold the owner's account content: keep them under C:\WAVEE\scratch\rec, never commit or upload them.

.PARAMETER Script   The drive script (inspector grammar; Screens/Diagnostics.Drive.cs). `#rec` splits setup from the take.
.PARAMETER Out      Output folder (default C:\WAVEE\scratch\rec\<script name>-<yyyyMMdd-HHmmss>).
.PARAMETER Exe      The verify build (a profile-scoped Wavee with the drive verb).
.PARAMETER Theme    classic | winui | zune (default winui).
.PARAMETER Route    The route to open before the setup part (default home; '' leaves it).
.PARAMETER TailMs   How long the take keeps rolling after the script ends (default 600).
.EXAMPLE
  bash C:/WAVEE/scratch/gpulock.sh powershell -NoProfile -File ops/release/tools/Record-Feature.ps1 -Script ops/release/tools/record/home-scroll-pager.txt -Exe C:\WAVEE\scratch\rec\build\Wavee.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Exe,
    [string]$Out,
    [string]$ProfileDir = 'C:\wavee\verify-rec',
    [ValidateSet('classic', 'winui', 'zune')][string]$Theme = 'winui',
    [string]$Route = 'home',
    [int]$Width = 1600, [int]$Height = 1000,
    [int]$Fps = 0,
    [int]$TailMs = 600,
    [switch]$Audio,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
Import-Module (Join-Path $repo 'ops\tools\evidence\Evidence.psm1') -Force -DisableNameChecking
$Script = (Resolve-Path $Script).Path
$Exe = (Resolve-Path $Exe).Path
$name = [IO.Path]::GetFileNameWithoutExtension($Script)
if (-not $Out) { $Out = Join-Path 'C:\WAVEE\scratch\rec' ($name + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
function Say([string]$s) { Write-Host ('[rec] ' + $s) }

Add-Type -Namespace RecFeature -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields; public int x, y; public int dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency; }
public delegate bool MonitorEnumProc(IntPtr m, IntPtr dc, ref RECT r, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX i);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
[DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
'@ -ErrorAction SilentlyContinue

# ── the monitor: highest refresh that holds the window ────────────────────────────────────────────────────────────────
$monitors = New-Object System.Collections.Generic.List[object]
$cb = [RecFeature.Win+MonitorEnumProc]{ param($m, $dc, [ref]$r, $l)
    $i = New-Object RecFeature.Win+MONITORINFOEX; $i.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($i)
    [void][RecFeature.Win]::GetMonitorInfo($m, [ref]$i)
    $d = New-Object RecFeature.Win+DEVMODE; $d.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($d)
    [void][RecFeature.Win]::EnumDisplaySettingsW($i.szDevice, -1, [ref]$d)
    $dx = 96; $dy = 96; [void][RecFeature.Win]::GetDpiForMonitor($m, 0, [ref]$dx, [ref]$dy)
    $monitors.Add([pscustomobject]@{ Device = $i.szDevice; Work = $i.rcWork; Hz = $d.dmDisplayFrequency; Scale = $dx / 96.0 })
    $true }
[void][RecFeature.Win]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero, $cb, [IntPtr]::Zero)
$fits = @($monitors | Where-Object { ($_.Work.R - $_.Work.L) -ge [int]($Width * $_.Scale) -and ($_.Work.B - $_.Work.T) -ge [int]($Height * $_.Scale) } | Sort-Object Hz -Descending)
if ($fits.Count -eq 0) { throw "no monitor holds a ${Width}x${Height} DIP window: $(($monitors | ForEach-Object { "$($_.Device) $($_.Hz) Hz x$($_.Scale)" }) -join '; ')" }
$mon = $fits[0]
if ($Fps -le 0) { $Fps = $mon.Hz }
Say "monitor $($mon.Device) $($mon.Hz) Hz scale $($mon.Scale); recording at $Fps fps"

# ── the verify Wavee ───────────────────────────────────────────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force $ProfileDir | Out-Null
$ownerDir = Join-Path $env:LOCALAPPDATA 'Wavee'
$pp = Join-Path $ProfileDir 'playplay'
if (-not (Test-Path $pp) -and (Test-Path (Join-Path $ownerDir 'playplay'))) {
    Copy-Item -Recurse (Join-Path $ownerDir 'playplay') $pp   # local playback (the previews) needs it
}
$firstRun = -not (Test-Path (Join-Path $ProfileDir 'store.json'))
$startArgs = @{ ProfileDir = $ProfileDir; Exe = $Exe; X = $mon.Work.L; Y = $mon.Work.T; Width = [int]($Width * $mon.Scale); Height = [int]($Height * $mon.Scale) }
if ($firstRun) { $startArgs.CopyLibrary = $true }
$v = & (Join-Path $repo 'ops\tools\evidence\Start-VerifyWavee.ps1') @startArgs
$hwnd = [IntPtr]$v.Hwnd
$ev = $v.EvidenceRoot
$startedPid = if ($v.Reused) { 0 } else { $v.Pid }
Say "verify Wavee pid $($v.Pid) $(if ($v.Reused) { '(reused: left running)' } else { '(started here: closed at the end)' })"

function Diag([string]$q, [int]$timeoutMs = 20000) {
    $r = Invoke-WaveeDiag -Hwnd $hwnd -Uri ('wavee://diag?' + $q) -EvidenceRoot $ev -TimeoutMs $timeoutMs
    if ($r.Result -ne 'ok') { throw "wavee://diag?$q -> $($r.Result) $($r.Path)" }
    $r
}
function Drive([string[]]$lines, [string]$tag, [int]$timeoutMs) {
    $f = Join-Path $Out "$tag.drive.txt"
    [IO.File]::WriteAllLines($f, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Diag ("cmd=drive&tag=$tag&file=" + [Uri]::EscapeDataString($f)) $timeoutMs
}
function Script-Ms([string[]]$lines) {
    # an upper bound on the take's length: every @ms, dwell, wait, frames and steps, plus 20 s for ready waits
    $ms = 20000
    foreach ($l in $lines) {
        foreach ($m in [regex]::Matches($l, '(?:@|dwell=|ms=|max=)(\d+)')) { $ms += [int]$m.Groups[1].Value }
        foreach ($m in [regex]::Matches($l, '(?:steps=|frames )(\d+)')) { $ms += 17 * [int]$m.Groups[1].Value }
    }
    $ms
}

$all = Get-Content -LiteralPath $Script -Encoding UTF8
$cut = [Array]::IndexOf([string[]]$all, ($all | Where-Object { $_.Trim() -eq '#rec' } | Select-Object -First 1))
$setup = if ($cut -gt 0) { $all[0..($cut - 1)] } else { @() }
$take = if ($cut -ge 0) { $all[($cut + 1)..($all.Count - 1)] } else { $all }

$ff = $null; $aud = $null
try {
    Diag 'cmd=present&on=1' | Out-Null
    Diag "cmd=theme&mode=$Theme" | Out-Null
    Start-Sleep -Milliseconds 1500
    Drive @("resize $Width $Height") 'size' 10000 | Out-Null
    $r = New-Object RecFeature.Win+RECT; [void][RecFeature.Win]::GetWindowRect($hwnd, [ref]$r)
    [void][RecFeature.Win]::MoveWindow($hwnd, $mon.Work.L, $mon.Work.T, $r.R - $r.L, $r.B - $r.T, $true)
    if ($Route) { Send-WaveeUri -Hwnd $hwnd -Uri "wavee://open?route=$Route"; Start-Sleep -Milliseconds 2500 }
    if ($setup.Count -gt 0) { Say 'setup'; Drive $setup 'setup' (Script-Ms $setup) | Out-Null }

    # ── the take ───────────────────────────────────────────────────────────────────────────────────────────────────────
    [void][RecFeature.Win]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0013)   # topmost, no move/size/activation
    Start-Sleep -Milliseconds 400
    $mkv = Join-Path $Out 'take.mkv'
    $ffArgs = @('-hide_banner', '-loglevel', 'error', '-y', '-filter_complex',
        "gfxcapture=hwnd=$([int64]$hwnd):capture_cursor=0:capture_border=0:max_framerate=${Fps}:resize_mode=crop,hwdownload,format=bgra",
        '-fps_mode', 'passthrough', '-c:v', 'libx264rgb', '-preset', 'ultrafast', '-qp', '0', $mkv)
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'ffmpeg', ([string]::Join(' ', ($ffArgs | ForEach-Object { if ($_ -match '[\s;,]') { '"' + $_ + '"' } else { $_ } })))
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $ff = [System.Diagnostics.Process]::Start($psi)
    $ffErr = $ff.StandardError.ReadToEndAsync()
    if ($Audio) {
        $tool = Join-Path $PSScriptRoot 'Record-AppAudio.cs'
        $wav = Join-Path $Out 'take.wav'
        $apsi = New-Object System.Diagnostics.ProcessStartInfo 'dotnet', ("run `"$tool`" -- $($v.Pid) `"$wav`"")
        $apsi.UseShellExecute = $false; $apsi.RedirectStandardInput = $true; $apsi.CreateNoWindow = $true
        $aud = [System.Diagnostics.Process]::Start($apsi)
    }
    Start-Sleep -Milliseconds 900   # the capture's first frames: a still lead-in
    if ($ff.HasExited) { throw "ffmpeg stopped at once: $($ffErr.Result)" }
    Say "take ($($take.Count) lines)"
    $d = Drive $take 'take' (Script-Ms $take)
    Start-Sleep -Milliseconds $TailMs
}
finally {
    if ($ff -and -not $ff.HasExited) { $ff.StandardInput.Write('q'); $ff.StandardInput.Close(); if (-not $ff.WaitForExit(30000)) { Write-Warning 'ffmpeg did not stop' } }
    if ($aud -and -not $aud.HasExited) { $aud.StandardInput.WriteLine('q'); $aud.StandardInput.Close(); [void]$aud.WaitForExit(15000) }
    [void][RecFeature.Win]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0013)
    try { Diag 'cmd=present&on=0' 5000 | Out-Null } catch { }
    if ($startedPid -and -not $KeepOpen) {
        [void][RecFeature.Win]::PostMessageW($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE: the app's own exit
        $p = Get-Process -Id $startedPid -ErrorAction SilentlyContinue
        if ($p -and -not $p.WaitForExit(20000)) { Write-Warning "verify Wavee $startedPid did not exit on close; stopping it (it is ours)"; Stop-Process -Id $startedPid -Force }
        Say "closed verify Wavee $startedPid"
    }
}

# ── the files ────────────────────────────────────────────────────────────────────────────────────────────────────────
$driveDir = $d.Path
if ($driveDir -and (Test-Path $driveDir)) { Copy-Item -Recurse -Force $driveDir (Join-Path $Out 'drive') }
& python -I (Join-Path $PSScriptRoot 'record_feature_post.py') --out $Out --fps $Fps --scale $mon.Scale --width $Width --height $Height
if ($LASTEXITCODE -ne 0) { throw "post-processing failed ($LASTEXITCODE)" }
Get-Content (Join-Path $Out 'report.txt')
