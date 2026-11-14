#requires -Version 5.1
<#
.SYNOPSIS
  Shoot the 0.3.9 Microsoft Store set from a VERIFY Wavee: the stills (window back buffers with their keyed rects) and the
  trailer's clips (the screen under the window, recorded with Desktop Duplication), scene by scene, the same way every time.

.DESCRIPTION
  Dev-box tool, the successor of Capture-StoreShots.ps1 for the three themes, the mini player and the trailer. The listing
  is never shot from the owner's own Wavee: the owner starts a verify instance first (ops\tools\evidence\Start-VerifyWavee.ps1
  -Exe <a build with the theme/mini diag verbs> -CopyLibrary; its own profile, signed in from a copy of store.json,
  developer mode on). This script drives THAT window only (found by -Exe path) with wavee:// verbs over WM_COPYDATA, sent to
  the main window's handle (it stays the target while the mini player hides it).

  Stills: wavee://diag?cmd=shot (Screens/StoreShot.cs): presentation mode on, the frame read back with its alpha, shot.json
  with every shown keyed element's rect. The mini player is its own window: it is copied off the screen by its DWM frame.
  Clips (-Clips): ffmpeg ddagrab of the window's rectangle on its monitor, 60 fps, no pointer, lossless-ish x264, while the
  scene's verbs play; each clip writes <name>.mp4 and <name>.json (its rect on the screen and the verb times, for the edit).

  The scenes play real tracks on the signed-in account (the verify instance becomes its active Spotify Connect device while
  it runs). Never stops a process; never moves the owner's pointer (a pointer over the window makes the WINDOW step aside).

.PARAMETER Out     Destination (default artifacts\store-0.3.9): shots\<name>\ and clips\<name>.mp4.
.PARAMETER Only    Scene names to run (default: all).
.PARAMETER Clips   Record the trailer clips instead of the stills.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$Out,
    [string[]]$Only = @(),
    [switch]$Clips
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Out) { $Out = Join-Path $repo 'artifacts\store-0.3.9' }
$shots = Join-Path $Out 'shots'; $clipDir = Join-Path $Out 'clips'
New-Item -ItemType Directory -Force $shots, $clipDir | Out-Null
$send = Join-Path $repo 'ops\tools\evidence\Send-WaveeDiag.ps1'
$Exe = (Resolve-Path $Exe).Path
$proc = Get-Process Wavee -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "no verify Wavee window running from $Exe - start it with Start-VerifyWavee.ps1" }
$main = $proc.MainWindowHandle

Add-Type -Namespace StoreSet -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX i);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int p);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
'@ -ErrorAction SilentlyContinue
[void][StoreSet.Win]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Drawing

function Frame([IntPtr]$h) { $r = New-Object StoreSet.Win+RECT; [void][StoreSet.Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16); $r }

# A pointer resting over the window paints a hover into the frame: the WINDOW steps aside, the owner's pointer never moves.
function Clear-Hover([IntPtr]$h = $main) {
    $p = New-Object StoreSet.Win+POINT; [void][StoreSet.Win]::GetCursorPos([ref]$p)
    $r = New-Object StoreSet.Win+RECT; [void][StoreSet.Win]::GetWindowRect($h, [ref]$r)
    if ($p.X -lt $r.L -or $p.X -ge $r.R -or $p.Y -lt $r.T -or $p.Y -ge $r.B) { return }
    $w = $r.R - $r.L; $hh = $r.B - $r.T
    $x = if ($p.X - $r.L -gt $w / 2) { $p.X - $w - 40 } else { $p.X + 40 }
    [void][StoreSet.Win]::MoveWindow($h, $x, $r.T, $w, $hh, $true)
    Start-Sleep -Milliseconds 600
}

# The process's other visible top-level window: the mini player (or the video pop-out).
function Find-Other {
    $script:other = [IntPtr]::Zero
    $cb = [StoreSet.Win+EnumProc]{ param($h, $l)
        $p = 0; [void][StoreSet.Win]::GetWindowThreadProcessId($h, [ref]$p)
        if ($p -eq $proc.Id -and $h -ne $main -and [StoreSet.Win]::IsWindowVisible($h)) { $script:other = $h; return $false }
        $true }
    [void][StoreSet.Win]::EnumWindows($cb, [IntPtr]::Zero)
    $script:other
}

function Verb([string]$uri) {
    $r = & $send -Hwnd $main -Uri $uri -EvidenceRoot $EvidenceRoot -TimeoutMs 20000
    if ($uri -like 'wavee://diag*' -and $r.Result -ne 'ok') { throw "$uri -> $($r.Result)" }
    if ($script:clip) { $script:clip.Marks.Add(('{0:0.000}' -f $script:clip.Watch.Elapsed.TotalSeconds) + "`t" + $uri) }
    $r
}
function Wait([int]$ms) { Start-Sleep -Milliseconds $ms }

function Shot([string]$name, [string]$query = '') {
    if ($Clips) { return }
    Clear-Hover
    $r = Verb ("wavee://diag?cmd=shot&tag=$name" + $(if ($query) { "&$query" } else { '' }))
    $dst = Join-Path $shots $name
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    Copy-Item -Recurse $r.Path $dst
    Write-Host "  $name  <- $($r.Path)"
}

# The mini player (or pop-out) copied off the screen by its DWM frame, at a size given in DIP (the window's own scale).
function Grab-Other([string]$name, [int]$wDip = 0, [int]$hDip = 0) {
    $h = Find-Other
    if ($h -eq [IntPtr]::Zero) { throw "no second window for $name" }
    if ($wDip -gt 0) {
        $r = New-Object StoreSet.Win+RECT; [void][StoreSet.Win]::GetWindowRect($h, [ref]$r)
        $mon = Monitor $h
        [void][StoreSet.Win]::MoveWindow($h, $mon.Work.L + 160, $mon.Work.T + 160, [int]($wDip * $mon.Scale), [int]($hDip * $mon.Scale), $true)
        Wait 3000   # the window re-forms (a shape change cross-fades) before its frame is read
    }
    if ($Clips) { return }
    Clear-Hover $h
    $f = Frame $h; $w = $f.R - $f.L; $hh = $f.B - $f.T
    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($f.L, $f.T, 0, 0, (New-Object System.Drawing.Size($w, $hh))); $g.Dispose()
    $dst = Join-Path $shots $name
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Force $dst | Out-Null
    $bmp.Save((Join-Path $dst 'shot.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $json = '{ "tag": "' + $name + '", "scale": ' + (Monitor $h).Scale + ', "widthPx": ' + $w + ', "heightPx": ' + $hh + ', "screen": true, "keys": [] }'
    [System.IO.File]::WriteAllText((Join-Path $dst 'shot.json'), $json)
    Write-Host "  $name  <- screen $($f.L),$($f.T) ${w}x${hh}"
}

# The monitor a window is on: its rect, work area, ddagrab output index (by the order of [Screen]::AllScreens, which matches
# DXGI's on this box: checked once with a 1-frame grab per output) and its scale (the window's DPI over 96).
Add-Type -AssemblyName System.Windows.Forms
function Monitor([IntPtr]$h) {
    $m = [StoreSet.Win]::MonitorFromWindow($h, 2)
    $i = New-Object StoreSet.Win+MONITORINFOEX; $i.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($i)
    [void][StoreSet.Win]::GetMonitorInfo($m, [ref]$i)
    $idx = 0; $all = [System.Windows.Forms.Screen]::AllScreens
    for ($k = 0; $k -lt $all.Length; $k++) { if ($all[$k].DeviceName -eq $i.szDevice) { $idx = $k } }
    Add-Type -Namespace StoreSet -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);' -ErrorAction SilentlyContinue
    [pscustomobject]@{ Rect = $i.rcMonitor; Work = $i.rcWork; Output = $idx; Scale = [StoreSet.Dpi]::GetDpiForWindow($h) / 96.0 }
}

# ── clips: ddagrab of a window's rectangle while a scene plays ─────────────────────────────────────────────────────────
# A clip records the SCREEN: the window goes topmost for the recording (no activation, the owner's focus stays where it is), so
# nothing the owner has open on that monitor is ever in the picture; Stop-Clip puts it back.
function Set-Topmost([IntPtr]$h, [bool]$on) {
    [void][StoreSet.Win]::SetWindowPos($h, [IntPtr]$(if ($on) { -1 } else { -2 }), 0, 0, 0, 0, 0x0013)   # NOSIZE|NOMOVE|NOACTIVATE
}
function Start-Clip([string]$name, [IntPtr]$h = $main, [int]$pad = 0) {
    if (-not $Clips) { return }
    Verb 'wavee://diag?cmd=present&on=1' | Out-Null   # presentation mode: no account name, picture or badge in the picture
    Set-Topmost $h $true; Start-Sleep -Milliseconds 900
    if ($h -eq $main) {
        # a clip needs the whole window on its monitor: put it back at the work area's corner and WAIT for the owner's
        # pointer to leave it (moving the window aside would push it off the screen)
        $mon = Monitor $h; $r = New-Object StoreSet.Win+RECT; [void][StoreSet.Win]::GetWindowRect($h, [ref]$r)
        [void][StoreSet.Win]::MoveWindow($h, $mon.Work.L, $mon.Work.T, $r.R - $r.L, $r.B - $r.T, $true)
        $deadline = (Get-Date).AddMinutes(5); $said = $false
        while ((Get-Date) -lt $deadline) {
            $p = New-Object StoreSet.Win+POINT; [void][StoreSet.Win]::GetCursorPos([ref]$p)
            [void][StoreSet.Win]::GetWindowRect($h, [ref]$r)
            if ($p.X -lt $r.L -or $p.X -ge $r.R -or $p.Y -lt $r.T -or $p.Y -ge $r.B) { break }
            if (-not $said) { Write-Host '  waiting for the pointer to leave the window...'; $said = $true }
            Start-Sleep -Milliseconds 300
        }
        Start-Sleep -Milliseconds 600
    } else { Clear-Hover $h }
    $mon = Monitor $h; $f = Frame $h
    $x = [Math]::Max(0, $f.L - $mon.Rect.L - $pad); $y = [Math]::Max(0, $f.T - $mon.Rect.T - $pad)
    $w = [Math]::Min($mon.Rect.R - $mon.Rect.L - $x, $f.R - $f.L + 2 * $pad); $hh = [Math]::Min($mon.Rect.B - $mon.Rect.T - $y, $f.B - $f.T + 2 * $pad)
    $w -= $w % 2; $hh -= $hh % 2
    $file = Join-Path $clipDir "$name.mp4"
    $args = @('-y', '-loglevel', 'error', '-filter_complex',
        "ddagrab=output_idx=$($mon.Output):draw_mouse=0:framerate=60:offset_x=${x}:offset_y=${y}:video_size=${w}x${hh},hwdownload,format=bgra",
        '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '10', '-pix_fmt', 'yuv444p', $file)
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'ffmpeg', ([string]::Join(' ', ($args | ForEach-Object { if ($_ -match '[\s;]') { '"' + $_ + '"' } else { $_ } })))
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.CreateNoWindow = $true
    $ff = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Milliseconds 700   # ddagrab's first frame
    $script:clip = [pscustomobject]@{ Name = $name; Ff = $ff; Hwnd = $h; File = $file; Watch = [Diagnostics.Stopwatch]::StartNew(); Marks = New-Object System.Collections.Generic.List[string]
        Rect = "$x,$y,$w,$hh"; Scale = $mon.Scale }
    Write-Host "  clip $name  rec ${w}x${hh} @ $x,$y output $($mon.Output)"
}
function Stop-Clip {
    if (-not $script:clip) { return }
    $c = $script:clip; $script:clip = $null
    $c.Ff.StandardInput.Write('q'); $c.Ff.StandardInput.Close()
    if ($c.Hwnd -eq $main) { Set-Topmost $main $false }
    if (-not $c.Ff.WaitForExit(15000)) { Write-Warning "ffmpeg for $($c.Name) did not stop" }
    $marks = ($c.Marks | ForEach-Object { '"' + ($_ -replace '\\', '\\' -replace '"', '\"' -replace "`t", '\u0009') + '"' }) -join ', '
    $json = '{ "name": "' + $c.Name + '", "rect": [' + $c.Rect + '], "scale": ' + $c.Scale + ', "lead": 0.7, "marks": [' + $marks + '] }'
    [System.IO.File]::WriteAllText((Join-Path $clipDir "$($c.Name).json"), $json)
    Write-Host "  clip $($c.Name) -> $($c.File)"
}

# ── the scenes ─────────────────────────────────────────────────────────────────────────────────────────────────────────
# Tracks: Stronger (word-timed lyrics cached in the profile), BIRDS OF A FEATHER (a music video and a Canvas),
# HIT ME HARD AND SOFT (album credits). A scene starts from Classic, no stage, no mini player.
$stronger = 'spotify:track:1nInOsHbtotAmEOQhtvnzP'; $birds = 'spotify:track:6dOtVTDdiauQNBQEDOtlAB'
$hmhas = 'spotify:album:7aJuG4TFXa2hmE4z1yxc3n'; $billie = 'spotify:artist:6qqNVTkY8uBg9cP3Jd7DAH'
function Reset-Scene([string]$theme = 'classic') {
    Verb 'wavee://diag?cmd=here' | Out-Null ; Wait 1500   # playback on THIS instance, never on another device of the account
    Verb 'wavee://diag?cmd=mini&open=0' | Out-Null
    Verb 'wavee://diag?cmd=stage&open=0' | Out-Null
    Verb "wavee://diag?cmd=theme&mode=$theme" | Out-Null ; Wait 2500
}

$scenes = [ordered]@{
    'classic-home' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=52000&pause=1'
        Verb 'wavee://diag?cmd=rail&mode=lyrics'
        Verb 'wavee://open?route=home' ; Wait 3500
        Shot 'classic-home' 'settle=1200'
        Shot 'classic-home-zoom' 'zoom=200&settle=1800&keys=ll1,lyrics,ai-lyrics'
    }
    'zune' = {
        Reset-Scene 'zune'
        Verb "wavee://play?ctx=$hmhas" ; Wait 6000
        Verb 'wavee://diag?cmd=seek&ms=60000&pause=1'
        Verb 'wavee://open?route=home' ; Wait 3500
        Shot 'zune-home' 'settle=1500'
        Verb "wavee://open?route=album&arg=$hmhas" ; Wait 4500
        Shot 'zune-album' 'settle=1500'
    }
    'winui' = {
        Reset-Scene 'winui'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=seek&ms=52000&pause=1'
        Verb 'wavee://diag?cmd=rail&mode=queue'
        Verb 'wavee://open?route=home' ; Wait 3500
        Shot 'winui-home' 'settle=1500'
        Verb "wavee://open?route=artist&arg=$billie" ; Wait 4500
        Shot 'winui-artist' 'settle=1500'
        Verb 'wavee://diag?cmd=rail&mode=off'
    }
    'lyrics' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=52000&pause=1'
        Verb 'wavee://diag?cmd=stage&mode=lyrics&open=1' ; Wait 6000
        Shot 'lyrics' 'settle=1500'
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'visualizer' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=60000'
        Verb 'wavee://diag?cmd=stage&mode=visualizer&face=aurora&gallery=1&open=1' ; Wait 5000
        Shot 'visualizer' 'settle=300'
        Verb 'wavee://diag?cmd=stage&gallery=0' ; Wait 1500
        Shot 'visualizer-clean' 'settle=300'
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'video' = {
        # The picture is DRM-protected and never reaches a capture: the shot keeps the surface as a transparent hole,
        # which the compositor fills with a frame of the same video (artifacts\store-shots\frames).
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb "wavee://open?route=album&arg=$hmhas" ; Wait 3000
        Verb 'wavee://diag?cmd=rail&mode=queue'
        Verb 'wavee://diag?cmd=video&mode=docked' ; Wait 4000
        Shot 'video-docked' 'settle=1200'
        Verb 'wavee://diag?cmd=video&mode=fullscreen' ; Wait 4000
        Shot 'video-full' 'settle=1200'
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://pause'
    }
    'mini' = {
        # big enough to draw at native pixels in a 3840 image; colour from BIRDS OF A FEATHER (cover, Canvas, visualizer)
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$stronger" ; Wait 6000
        Verb 'wavee://diag?cmd=seek&ms=34600&pause=1'
        Verb 'wavee://diag?cmd=mini&face=1&open=1' ; Wait 2500
        Grab-Other 'mini-lyrics' 640 360
        Verb 'wavee://diag?cmd=mini&open=0' ; Wait 1500
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=60000'
        Verb 'wavee://diag?cmd=mini&face=2&open=1' ; Wait 4000
        Grab-Other 'mini-canvas' 340 600
        Verb 'wavee://diag?cmd=mini&face=0' ; Wait 1500
        Grab-Other 'mini-cover' 420 420
        Verb 'wavee://diag?cmd=theme&mode=zune' ; Wait 3000
        Verb 'wavee://diag?cmd=mini&face=3' ; Wait 2500
        Grab-Other 'mini-zune' 640 360
        Verb 'wavee://diag?cmd=theme&mode=winui' ; Wait 3000
        Verb 'wavee://diag?cmd=mini&face=4' ; Wait 2500
        Grab-Other 'mini-winui' 400 560
        Verb 'wavee://diag?cmd=mini&face=0' ; Wait 1500
        Grab-Other 'mini-strip' 560 64
        Verb 'wavee://diag?cmd=mini&open=0' ; Wait 1500
        Verb 'wavee://diag?cmd=theme&mode=classic' ; Wait 2500
    }
}

# The trailer's clips: real motion, recorded as it happens on the screen.
$clipScenes = [ordered]@{
    'themes' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$stronger" ; Wait 6000
        Verb 'wavee://diag?cmd=rail&mode=lyrics'
        Verb 'wavee://open?route=home' ; Wait 3000
        Start-Clip 'themes'
        Wait 2500
        Verb 'wavee://diag?cmd=theme&mode=zune' ; Wait 4000
        Verb 'wavee://diag?cmd=theme&mode=winui' ; Wait 4000
        Verb 'wavee://diag?cmd=theme&mode=classic' ; Wait 3500
        Stop-Clip
    }
    'speed' = {
        # Real-time page hopping and a fling through Liked Songs: the trailer's "fast" act. Every page's time to its first
        # frame is in the app log ([ui] nav.frames firstFrameMs); the trailer prints those numbers, so nothing is sped up.
        Reset-Scene 'classic'
        Verb 'wavee://diag?cmd=rail&mode=off'
        Verb 'wavee://open?route=home' ; Wait 2500
        Start-Clip 'speed'
        Wait 900
        foreach ($u in "wavee://open?route=album&arg=$hmhas", "wavee://open?route=artist&arg=$billie", 'wavee://open?route=albums',
                       'wavee://open?route=artists', 'wavee://open?route=recents', 'wavee://open?route=browse', 'wavee://open?route=liked') {
            Verb $u ; Wait 1100
        }
        Verb 'wavee://diag?cmd=scroll&vp=tab2/liked&to=20500&move=glide' ; Wait 2600
        Verb 'wavee://diag?cmd=scroll&vp=tab2/liked&to=0&move=glide' ; Wait 2300
        Stop-Clip
    }
    'browse' = {
        Reset-Scene 'classic'
        Verb 'wavee://diag?cmd=rail&mode=off'
        Verb 'wavee://open?route=home' ; Wait 2500
        Start-Clip 'browse'
        Wait 1500
        Verb "wavee://open?route=album&arg=$hmhas" ; Wait 3000
        Verb "wavee://open?route=artist&arg=$billie" ; Wait 4000
        Verb 'wavee://open?route=liked' ; Wait 3500
        Stop-Clip
    }
    'lyrics' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=44000'
        Verb 'wavee://diag?cmd=stage&mode=lyrics&open=1' ; Wait 3000   # full screen: the clip's rect is the whole monitor
        Start-Clip 'lyrics'
        Wait 11000
        Stop-Clip
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'visualizer' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=60000'
        Verb 'wavee://diag?cmd=stage&mode=visualizer&face=aurora&gallery=0&open=1' ; Wait 3000
        Start-Clip 'visualizer'
        Wait 7000
        Stop-Clip
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'canvas' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=stage&mode=lyrics&open=1' ; Wait 3000
        Start-Clip 'canvas'
        Wait 8000
        Stop-Clip
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'mini' = {
        Reset-Scene 'classic'
        Verb "wavee://play?ctx=$birds" ; Wait 6000
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://diag?cmd=seek&ms=50000'
        Verb 'wavee://diag?cmd=mini&face=1&open=1' ; Wait 2500
        Grab-Other 'mini' 640 360
        $h = Find-Other
        Start-Clip 'mini' $h 0
        Wait 5000
        Verb 'wavee://diag?cmd=mini&face=3' ; Wait 4000
        Verb 'wavee://diag?cmd=mini&face=4' ; Wait 4000
        Verb 'wavee://diag?cmd=mini&face=0' ; Wait 3500
        Stop-Clip
        Verb 'wavee://diag?cmd=mini&open=0' ; Wait 1500
    }
}

$list = if ($Clips) { $clipScenes } else { $scenes }
try {
    foreach ($name in $list.Keys) {
        if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
        Write-Host "scene $name"
        & $list[$name] | Out-Null
    }
} finally {
    Stop-Clip
    Verb 'wavee://diag?cmd=present&on=0' | Out-Null
    Verb 'wavee://pause' | Out-Null
}
Write-Host "done: $Out"
