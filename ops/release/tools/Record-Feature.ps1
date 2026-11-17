#requires -Version 5.1
<#
.SYNOPSIS
  Record a Wavee feature on REAL data: a verify Wavee on a copy of the owner's profile, driven by a script, filmed at the
  display's native refresh rate.

.DESCRIPTION
  Dev-box tool; run it from Windows PowerShell OUTSIDE any sandbox (the GPU, the window capture and ffmpeg are not visible
  inside one), and hold the GPU lock: bash C:/WAVEE/scratch/gpulock.sh powershell -File Record-Feature.ps1 ...

  1. Picks the monitor: -Monitor, the SECONDARY by default (owner rule 2026-11-17: a real-window test run goes on the secondary
     screen, never takes the foreground or the keyboard, and is never topmost). The app is launched with --window-monitor and
     --window-rect (Screens/Diagnostics.Probe.TestWindow.cs: placed there before its first frame, shown without activation,
     WS_EX_NOACTIVATE, no SetForegroundWindow on any verb). The take runs at that screen's refresh rate.
  2. Starts a VERIFY Wavee with ops\tools\evidence\Start-VerifyWavee.ps1 on -ProfileDir (its own profile, signed in from a
     copy of store.json minus the Connect device id; the owner's playplay folder copied in so previews can play locally).
     A verify Wavee already running from -Exe on that profile is reused and left running; one this script started is closed at
     the end. -Fake rehearses instead on the offline --fake demo (-FakeProfile, no account).
     It NEVER sends cmd=here (a Connect takeover would steal the owner's playback) and never plays or pauses anything.
  3. Sets the theme (in the profile's settings, before the launch), presentation mode (no account name or picture), the client
     size and the route, then plays the script's
     SETUP part (the lines before `#rec`: e.g. `#live ready key=...`), so the take never opens on a loading state.
  4. Records the window (ffmpeg gfxcapture = Windows.Graphics.Capture of THIS window only: nothing that covers it, nothing
     beside it, no OS cursor; a window that shrinks keeps the frame size with black bars) at -Fps (default 60 from a 120 Hz panel: every other presented frame) into a
     lossless intermediate while the script's TAKE part (after `#rec`) plays through wavee://diag?cmd=drive (the app's own
     input queue: no SendInput, the owner's pointer never moves; the app draws the driver's pointer). Never topmost: the
     capture films the window itself. -Encoder x264rgb (default) or mf (the GPU's H.264 encoder). -Audio adds the verify process's own output (WASAPI process loopback, Record-AppAudio.cs):
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
    # The take's frame rate. Default: half the panel's rate when that is above 60 (120 Hz -> 60 fps: every other presented frame,
    # picked on the GPU, measured loss-free on this box), else the panel's. -Fps <panel rate> films every frame (on a 120 Hz
    # Snapdragon X: about 1.4 % of the frames are lost by the capture, which the report counts).
    [int]$Fps = 0,
    [int]$TailMs = 600,
    [switch]$Audio,
    [switch]$KeepOpen,
    # Values for the script's {NAME} placeholders (a take written against a key scheme: -Set @{ SHELF = '...'; OPEN = '...' }).
    [hashtable]$Set = @{},
    # Discovery instead of a take: after the setup, list the keyed nodes whose key starts with this prefix (key, rect) and stop.
    [string]$ListKeys,
    # The scroller -ListKeys seeks along (a scroll-key prefix; Home's page is home:).
    [string]$ListVp = 'home:',
    # Owner rule 2026-11-17: a real-window test run goes on the SECONDARY monitor and never takes the foreground (the app's
    # --window-monitor / --window-rect; Screens/Diagnostics.Probe.TestWindow.cs). secondary | primary | <EnumDisplayMonitors index>.
    [string]$Monitor = 'secondary',
    # A rehearsal: the offline --fake demo on a scratch profile (no account, settings in memory) instead of a verify Wavee.
    [switch]$Fake,
    [string]$FakeProfile = 'C:/WAVEE/scratch/rec/fakeprof',
    # Extra engine switches for the run (--fg), e.g. a frame-cost probe.
    [string]$Fg = '',
    # The lossless intermediate's encoder: x264rgb (BGRA, libx264rgb qp 0, CPU) or mf (H.264 on the GPU's Media Foundation encoder at
    # 120 Mb/s: near-lossless, NV12).
    [ValidateSet('x264rgb', 'mf')][string]$Encoder = 'x264rgb',
    # Play the take without filming it (a frame-cost run): the drive's own summary is the report.
    [switch]$NoCapture
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
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
'@ -ErrorAction SilentlyContinue

# ── the monitor: -Monitor (the secondary by default: test runs stay off the person's work screen) ───────────────────────
$monitors = New-Object System.Collections.Generic.List[object]
$cb = [RecFeature.Win+MonitorEnumProc]{ param($m, $dc, [ref]$r, $l)
    $i = New-Object RecFeature.Win+MONITORINFOEX; $i.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($i)
    [void][RecFeature.Win]::GetMonitorInfo($m, [ref]$i)
    $d = New-Object RecFeature.Win+DEVMODE; $d.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($d)
    [void][RecFeature.Win]::EnumDisplaySettingsW($i.szDevice, -1, [ref]$d)
    $dx = 96; $dy = 96; [void][RecFeature.Win]::GetDpiForMonitor($m, 0, [ref]$dx, [ref]$dy)
    $monitors.Add([pscustomobject]@{ Device = $i.szDevice; Work = $i.rcWork; Hz = $d.dmDisplayFrequency; Scale = $dx / 96.0; Primary = ($i.dwFlags -band 1) -ne 0 })
    $true }
[void][RecFeature.Win]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero, $cb, [IntPtr]::Zero)
# EnumDisplayMonitors order, as the app's --window-monitor counts them
$mon = switch ($Monitor) {
    'secondary' { $monitors | Where-Object { -not $_.Primary } | Select-Object -First 1 }
    'primary' { $monitors | Where-Object { $_.Primary } | Select-Object -First 1 }
    default { $monitors[[int]$Monitor] }
}
if (-not $mon) { throw "no monitor '$Monitor': $(($monitors | ForEach-Object { "$($_.Device) $($_.Hz) Hz x$($_.Scale)$(if ($_.Primary) { ' primary' })" }) -join '; ')" }
if (($mon.Work.R - $mon.Work.L) -lt [int]($Width * $mon.Scale) -or ($mon.Work.B - $mon.Work.T) -lt [int]($Height * $mon.Scale)) {
    throw "monitor $($mon.Device) does not hold a ${Width}x${Height} DIP window at x$($mon.Scale)" }
# Default: 60 fps from a 120 Hz screen (every other presented frame, picked on the GPU: loss-free where measured); a screen at
# 100 Hz or less is filmed at its own rate (the 100 Hz secondary: 100 fps), and the share copy is 60 either way.
if ($Fps -le 0) { $Fps = if ($mon.Hz -ge 120 -and $mon.Hz % 2 -eq 0) { $mon.Hz / 2 } else { $mon.Hz } }
if ($mon.Hz % $Fps -ne 0) { throw "-Fps $Fps does not divide the panel's $($mon.Hz) Hz (every frame of the take must be a presented one)" }
$step = $mon.Hz / $Fps
Say "monitor $($mon.Device) ($Monitor) $($mon.Hz) Hz scale $($mon.Scale); recording at $Fps fps"

# ── the Wavee: a verify instance on the account, or (-Fake) the offline demo ──────────────────────────────────────────────
$placeArgs = @('--window-monitor', $Monitor, '--window-rect', "0,0,$Width,$Height") + $(if ($Fg) { @('--fg', $Fg) } else { @() })
if ($Fake) {
    New-Item -ItemType Directory -Force $FakeProfile | Out-Null
    # --fake keeps its settings in memory: the theme comes as --theme (honoured by a --fake run), never switched at run time
    $p = Start-Process -FilePath $Exe -ArgumentList (@('--fake', '--profile', $FakeProfile, '--theme', $Theme) + $placeArgs) -WorkingDirectory (Split-Path $Exe) -PassThru
    $hwnd = Wait-WindowByPid -ProcessId $p.Id -TimeoutSec 60
    $ev = Join-Path $FakeProfile 'logs\evidence'
    $v = [pscustomobject]@{ Pid = $p.Id; Hwnd = $hwnd; Reused = $false }
    Start-Sleep -Seconds 4
}
else {
    New-Item -ItemType Directory -Force $ProfileDir | Out-Null
    $ownerDir = Join-Path $env:LOCALAPPDATA 'Wavee'
    $pp = Join-Path $ProfileDir 'playplay'
    if (-not (Test-Path $pp) -and (Test-Path (Join-Path $ownerDir 'playplay'))) {
        Copy-Item -Recurse (Join-Path $ownerDir 'playplay') $pp   # local playback (the previews) needs it
    }
    $firstRun = -not (Test-Path (Join-Path $ProfileDir 'store.json'))
    # Settings the take must not show: Home's first-run teaching tip ("Make Home yours") counts as seen in every theme.
    $settingsPath = Join-Path $ProfileDir 'settings.json'
    $settings = if (Test-Path $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    function Set-Setting([string]$k, [string]$val) {
        if ($settings.PSObject.Properties.Name -contains $k) { $settings.$k = $val } else { $settings | Add-Member -NotePropertyName $k -NotePropertyValue $val }
    }
    foreach ($t in 'classic', 'winui', 'zune') { Set-Setting "theme.$t.state.homeTipSeen" 'true' }
    # The theme is set before the launch, not switched at run time (a runtime switch remounts the frame; a verb that arrived while
    # a covered window held that remount went unanswered until 0846691d8, and a switch is a needless remount on camera anyway).
    Set-Setting 'theme.id' $Theme
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
    $startArgs = @{ ProfileDir = $ProfileDir; Exe = $Exe; Monitor = $Monitor; WindowDip = "${Width}x${Height}"; FgSwitches = $(if ($Fg) { "gpu-timing,$Fg" } else { 'gpu-timing' }) }
    if ($firstRun) { $startArgs.CopyLibrary = $true }
    $v = & (Join-Path $repo 'ops\tools\evidence\Start-VerifyWavee.ps1') @startArgs
    $hwnd = [IntPtr]$v.Hwnd
    $ev = $v.EvidenceRoot
}
$hwnd = [IntPtr]$hwnd
$startedPid = if ($v.Reused) { 0 } else { $v.Pid }
Say "$(if ($Fake) { 'fake demo' } else { 'verify Wavee' }) pid $($v.Pid) on $Monitor, never activated $(if ($v.Reused) { '(reused: left running)' } else { '(started here: closed at the end)' })"

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

$all = @(Get-Content -LiteralPath $Script -Encoding UTF8 | ForEach-Object {
    $line = $_
    foreach ($k in $Set.Keys) { $line = $line.Replace('{' + $k + '}', [string]$Set[$k]) }
    $line })
if (-not $ListKeys) {
    $open = @($all | Where-Object { $_ -notmatch '^\s*#(?!live )' } | Select-String -Pattern '\{[A-Z0-9_]+\}' -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
    if ($open.Count -gt 0) { throw "the script has placeholders without a value: $($open -join ', ') (pass -Set @{ NAME = 'value' }; -ListKeys <prefix> lists the live keys)" }
}
$cut = [Array]::IndexOf([string[]]$all, ($all | Where-Object { $_.Trim() -eq '#rec' } | Select-Object -First 1))
$setup = if ($cut -gt 0) { $all[0..($cut - 1)] } else { @() }
$take = if ($cut -ge 0) { $all[($cut + 1)..($all.Count - 1)] } else { $all }

$ff = $null; $aud = $null; $d = $null; $takeError = $null
try {
    if ($Audio) {
        # built first (a file-based app compiles on its first run), started before the setup: it keeps its file on the wall
        # clock from its own start (startQpc100ns in take.wav.json), and the muxer lines that up with the take's t = 0
        $tool = Join-Path $PSScriptRoot 'Record-AppAudio.cs'
        & dotnet build $tool -o (Join-Path $Out 'audio-tool') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Record-AppAudio.cs did not build' }
        $apsi = New-Object System.Diagnostics.ProcessStartInfo (Join-Path $Out 'audio-tool\Record-AppAudio.exe'), ("$($v.Pid) `"$(Join-Path $Out 'take.wav')`"")
        $apsi.UseShellExecute = $false; $apsi.RedirectStandardInput = $true; $apsi.CreateNoWindow = $true
        $aud = [System.Diagnostics.Process]::Start($apsi)
    }
    Diag 'cmd=present&on=1' | Out-Null
    Drive @("resize $Width $Height") 'size' 10000 | Out-Null   # the client size exactly (--window-rect sized it at the launch's DPI)
    if ($Route) { Send-WaveeUri -Hwnd $hwnd -Uri "wavee://open?route=$Route"; Start-Sleep -Milliseconds 2500 }
    if ($setup.Count -gt 0) { Say 'setup'; Drive (@($setup | Where-Object { $_ -notmatch '\{[A-Z0-9_]+\}' })) 'setup' (Script-Ms $setup) | Out-Null }
    if ($ListKeys) {
        # discovery: the page glides on until a key under the prefix is realized (#live reveal prefix=), then every keyed node of
        # the live scene (the bundle's keyed.tsv) under the prefix, with its rect (DIP)
        Drive @("#live reveal prefix=$ListKeys vp=$ListVp top=150 ms=600") 'seek' 60000 | Out-Null
        Start-Sleep -Milliseconds 1500
        $b = Diag 'cmd=bundle&tag=keys' 30000
        $rows = @(Get-Content -LiteralPath (Join-Path $b.Path 'keyed.tsv') -Encoding UTF8 | Select-Object -Skip 1 | ForEach-Object { $c = $_.Split("`t"); if ($c[2].StartsWith($ListKeys)) { '{0}  {1},{2} {3}x{4}' -f $c[2], $c[4], $c[5], $c[6], $c[7] } })
        $rows | Set-Content -Encoding UTF8 (Join-Path $Out 'keys.txt')
        $rows
        return
    }

    # ── the take ───────────────────────────────────────────────────────────────────────────────────────────────────────
    # Never topmost (owner rule 2026-11-17): Windows.Graphics.Capture films the window itself, so what covers it is not filmed. A
    # COVERED window does park its frames, though: the secondary screen keeps it in the open.
    $mkv = Join-Path $Out 'take.mkv'
    # Windows.Graphics.Capture of this window (ffmpeg gfxcapture) as an INPUT with a deep queue: the capture runs on its own thread
    # and the read-back and encode cannot stall it (in a filter graph they did: frames were lost). Unthrottled (a cap at the
    # refresh rate drops frames that arrive a hair early); `framestep` keeps every Nth frame while it is still on the GPU, so
    # only the kept ones are read back. Lossless BGRA (libx264rgb, qp 0), each frame with its own capture time.
    $src = "gfxcapture=hwnd=$([int64]$hwnd):capture_cursor=0:capture_border=0:max_framerate=$(2 * $mon.Hz):resize_mode=crop" + $(if ($step -gt 1) { ",framestep=$step" } else { '' })
    $enc = if ($Encoder -eq 'mf') { @('-vf', 'hwdownload,format=bgra,format=nv12', '-c:v', 'h264_mf', '-hw_encoding', '1', '-b:v', '120M') }
           else { @('-vf', 'hwdownload,format=bgra', '-c:v', 'libx264rgb', '-preset', 'ultrafast', '-qp', '0') }
    $ffArgs = @('-hide_banner', '-loglevel', 'error', '-y', '-thread_queue_size', '256', '-f', 'lavfi', '-i', $src) + $enc + @('-fps_mode', 'passthrough', $mkv)
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'ffmpeg', ([string]::Join(' ', ($ffArgs | ForEach-Object { if ($_ -match '[\s;,]') { '"' + $_ + '"' } else { $_ } })))
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    if (-not $NoCapture) { $ff = [System.Diagnostics.Process]::Start($psi) }
    if ($ff) { try { $ff.PriorityClass = 'High' } catch { } }   # a capture that falls behind loses frames from Windows.Graphics.Capture's small pool
    if ($ff) {
        $ffErr = $ff.StandardError.ReadToEndAsync()
        Start-Sleep -Milliseconds 900   # the capture's first frames: a still lead-in
        if ($ff.HasExited) { throw "ffmpeg stopped at once: $($ffErr.Result)" }
    }
    Say "take ($($take.Count) lines)"
    try { $d = Drive $take 'take' (Script-Ms $take) } catch { $takeError = $_.Exception.Message }
    Start-Sleep -Milliseconds $TailMs
}
finally {
    if ($ff -and -not $ff.HasExited) { $ff.StandardInput.Write('q'); $ff.StandardInput.Close(); if (-not $ff.WaitForExit(30000)) { Write-Warning 'ffmpeg did not stop' } }
    if ($aud -and -not $aud.HasExited) { $aud.StandardInput.WriteLine('q'); $aud.StandardInput.Close(); [void]$aud.WaitForExit(15000) }
    try { Diag 'cmd=present&on=0' 5000 | Out-Null } catch { }
    if ($startedPid -and -not $KeepOpen) {
        [void][RecFeature.Win]::PostMessageW($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE: the app's own exit
        $p = Get-Process -Id $startedPid -ErrorAction SilentlyContinue
        if ($p -and -not $p.WaitForExit(20000)) { Write-Warning "Wavee $startedPid did not exit on close; stopping it (it is ours)"; Stop-Process -Id $startedPid -Force }
        Say "closed Wavee $startedPid"
    }
}

# ── the files (also for a take whose script failed: its frames show where) ─────────────────────────────────────────────
$driveDir = if ($d) { $d.Path } else { (Get-ChildItem (Join-Path $ev 'drive') -Directory | Where-Object Name -like '*-take' | Sort-Object Name | Select-Object -Last 1).FullName }
if ($driveDir -and (Test-Path $driveDir)) { Copy-Item -Recurse -Force $driveDir (Join-Path $Out 'drive') }
if ($NoCapture) {
    Get-Content (Join-Path $Out 'drive\summary.txt')
    if ($takeError) { throw "the take's script failed: $takeError" }
    return
}
& python -I (Join-Path $PSScriptRoot 'record_feature_post.py') --out $Out --fps $Fps --panel $mon.Hz --scale $mon.Scale --width $Width --height $Height
if ($LASTEXITCODE -ne 0) { throw "post-processing failed ($LASTEXITCODE)" }
Get-Content (Join-Path $Out 'report.txt')
if ($takeError) { throw "the take's script failed: $takeError" }
