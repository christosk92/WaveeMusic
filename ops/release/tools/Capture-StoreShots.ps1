#requires -Version 5.1
<#
.SYNOPSIS
  Shoot the Microsoft Store listing's source captures from a VERIFY Wavee, scene by scene, the same way every time.

.DESCRIPTION
  Dev-box tool. The listing is never shot from the owner's own Wavee: start a verify instance first
  (ops\tools\evidence\Start-VerifyWavee.ps1 -ProfileDir <dir> -CopyLibrary; its own profile, signed in from a copy of
  store.json, developer mode on). This script then drives THAT window only (found by -Exe path) with wavee:// verbs over
  WM_COPYDATA (Send-WaveeDiag.ps1) and takes each capture with wavee://diag?cmd=shot (Screens/StoreShot.cs):
  presentation mode on (no account name, picture or badge, no developer surfaces), the frame read back with its alpha,
  and shot.json with every shown keyed element's rect, which New-StoreImage.py places its spotlights and lenses on.

  Each scene: navigation / playback / stage verbs, a wait, then one or more shots (a zoom pass, cmd=shot&zoom=…, is the
  same frame's elements at 2-3x the pixels, the source of a lens). The shot folders are copied to -Out\<name>.

  The scenes play real tracks on the signed-in account: the verify instance becomes the account's active Spotify
  Connect device while it runs (its volume comes from its own settings.json). Never stops a process.

.PARAMETER Out     Destination of the shot folders (default artifacts\store-shots\shots).
.PARAMETER Only    Scene names to shoot (default: all).
.EXAMPLE
  .\Capture-StoreShots.ps1 -Exe C:\wavee\store\WaveeMusic\src\apps\Wavee\bin\verify\evidence\Wavee.exe -EvidenceRoot C:\wavee\store-profile\logs\evidence
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$Out,
    [string[]]$Only = @()
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Out) { $Out = Join-Path $repo 'artifacts\store-shots\shots' }
New-Item -ItemType Directory -Force $Out | Out-Null
$send = Join-Path $repo 'ops\tools\evidence\Send-WaveeDiag.ps1'
$Exe = (Resolve-Path $Exe).Path
$proc = Get-Process Wavee -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "no verify Wavee window running from $Exe - start it with Start-VerifyWavee.ps1" }

Add-Type -Namespace StoreShots -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint f);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int p);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
'@ -ErrorAction SilentlyContinue
[void][StoreShots.Win]::SetProcessDpiAwarenessContext([IntPtr](-4))

# A pointer resting over the verify window paints a hover (the caption's red close button, a hovered row) into the shot.
# The owner's cursor is never moved: the verify WINDOW steps aside instead, to the side of the screen the cursor is not on.
function Clear-Hover {
    $p = New-Object StoreShots.Win+POINT; [void][StoreShots.Win]::GetCursorPos([ref]$p)
    $r = New-Object StoreShots.Win+RECT; [void][StoreShots.Win]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
    if ($p.X -lt $r.L -or $p.X -ge $r.R -or $p.Y -lt $r.T -or $p.Y -ge $r.B) { return }
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $x = if ($p.X - $r.L -gt $w / 2) { $p.X - $w - 40 } else { $p.X + 40 }
    [void][StoreShots.Win]::MoveWindow($proc.MainWindowHandle, $x, $r.T, $w, $h, $true)
    Start-Sleep -Milliseconds 600
}

# The own-window video (Video.SurfacePlacement.Detached) is a second, always-on-top window the frame capture does not
# reach: copy it off the screen by its DWM frame (on top, so never covered) and record where it sits relative to the
# main window's client area, so the composer can lay the two out as the desktop showed them.
Add-Type -AssemblyName System.Drawing
function Grab-PopOut([string]$name) {
    $main = $proc.MainWindowHandle
    $script:pop = [IntPtr]::Zero
    $cb = [StoreShots.Win+EnumProc]{ param($h, $l)
        $p = 0; [void][StoreShots.Win]::GetWindowThreadProcessId($h, [ref]$p)
        if ($p -eq $proc.Id -and $h -ne $main -and [StoreShots.Win]::IsWindowVisible($h)) { $script:pop = $h; return $false }
        $true }
    [void][StoreShots.Win]::EnumWindows($cb, [IntPtr]::Zero)
    if ($script:pop -eq [IntPtr]::Zero) { throw 'no pop-out video window' }
    $r = New-Object StoreShots.Win+RECT; [void][StoreShots.Win]::DwmGetWindowAttribute($script:pop, 9, [ref]$r, 16)
    $m = New-Object StoreShots.Win+RECT; [void][StoreShots.Win]::DwmGetWindowAttribute($main, 9, [ref]$m, 16)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h))); $g.Dispose()
    $dst = Join-Path $Out $name
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Force $dst | Out-Null
    $bmp.Save((Join-Path $dst 'shot.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    # shot.json in the capture's shape (scale 1: these are screen pixels), plus the offset to the main frame
    $json = '{ "tag": "' + $name + '", "scale": 1, "widthPx": ' + $w + ', "heightPx": ' + $h + ', "offsetX": ' + ($r.L - $m.L) + ', "offsetY": ' + ($r.T - $m.T) + ', "keys": [] }'
    [System.IO.File]::WriteAllText((Join-Path $dst 'shot.json'), $json)
    Write-Host "  $name  <- screen $($r.L),$($r.T) ${w}x${h}"
}

function Verb([string]$uri) {
    $r = & $send -ProcessId $proc.Id -Uri $uri -EvidenceRoot $EvidenceRoot -TimeoutMs 20000
    if ($uri -like 'wavee://diag*' -and $r.Result -ne 'ok') { throw "$uri -> $($r.Result)" }
    $r
}
function Shot([string]$name, [string]$query = '') {
    Clear-Hover
    $r = Verb ("wavee://diag?cmd=shot&tag=$name" + $(if ($query) { "&$query" } else { '' }))
    $dst = Join-Path $Out $name
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    Copy-Item -Recurse $r.Path $dst
    Write-Host "  $name  <- $($r.Path)"
}
function Wait([int]$ms) { Start-Sleep -Milliseconds $ms }

# ── the scenes ─────────────────────────────────────────────────────────────────────────────────────────────────────
# Track ids carry cached word timings (the AI lyrics results in the profile) where a scene shows lyrics.
$scenes = [ordered]@{
    'home' = {
        Verb 'wavee://diag?cmd=stage&open=0'
        Verb 'wavee://play?ctx=spotify:track:1nInOsHbtotAmEOQhtvnzP' ; Wait 6000          # Stronger: word-timed lyrics
        Verb 'wavee://diag?cmd=seek&ms=34600&pause=1'
        Verb 'wavee://diag?cmd=rail&mode=lyrics'
        Verb 'wavee://open?route=home' ; Wait 3500
        Shot 'home' 'settle=1200'
        Shot 'home-zoom' 'zoom=200&settle=1800&keys=ll1,lyrics,ai-lyrics'
    }
    'playlist' = {
        Verb 'wavee://diag?cmd=rail&mode=queue'
        Verb 'wavee://open?route=pl&arg=spotify:playlist:37i9dQZF1DXcBWIGoYBM5M' ; Wait 4500
        Shot 'playlist' 'settle=1200'
    }
    'search' = {
        Verb 'wavee://diag?cmd=rail&mode=off'
        Verb 'wavee://open?route=search&arg=jason%20mraz' ; Wait 4500
        Shot 'search' 'settle=1200'
    }
    'album' = {
        Verb 'wavee://diag?cmd=rail&mode=off'
        Verb 'wavee://play?ctx=spotify:album:7aJuG4TFXa2hmE4z1yxc3n' ; Wait 6000
        Verb 'wavee://diag?cmd=seek&ms=60000&pause=1'
        Verb 'wavee://open?route=album&arg=spotify:album:7aJuG4TFXa2hmE4z1yxc3n' ; Wait 4000
        Shot 'album' 'settle=1200'
        # the same track full screen: its quality chip (stage:chips, "FLAC 24-bit") is the lossless lens
        Verb 'wavee://diag?cmd=stage&mode=lyrics&open=1' ; Wait 3500
        Shot 'album-stage' 'settle=1200&keys=stage:'
        # 160 %, not more: a zoom that flips the title bar to its icon-only search while full screen covers it leaves
        # the re-mounted field laid out as a stub once full screen closes (an open engine layout defect)
        Shot 'album-stage-zoom' 'zoom=160&settle=2000&keys=stage:'
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'ai' = {
        Verb 'wavee://diag?cmd=rail&mode=off'                                            # the page's full width for the card
        Verb 'wavee://open?route=settings&arg=appearance' ; Wait 3000
        Verb 'wavee://diag?cmd=reveal&vp=settings:appearance&keys=appearance.ai-lyrics' ; Wait 1200
        Shot 'ai-settings' 'settle=1200'
        # the AI card at 1.6x the pixels (the lens source): the zoom reflows the page, so the card is revealed again
        Shot 'ai-settings-zoom' 'zoom=160&settle=2400&vp=settings:appearance&reveal=appearance.ai-lyrics&keys=appearance.ai,ai:'
    }
    'lyrics' = {
        Verb 'wavee://play?ctx=spotify:track:1nInOsHbtotAmEOQhtvnzP' ; Wait 6000
        Verb 'wavee://diag?cmd=seek&ms=34600&pause=1'
        Verb 'wavee://diag?cmd=stage&mode=lyrics&open=1' ; Wait 6000                   # the lines glide to the active one
        Shot 'lyrics' 'settle=1500'
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'visualizer' = {
        Verb 'wavee://play?ctx=spotify:track:1nInOsHbtotAmEOQhtvnzP' ; Wait 6000
        Verb 'wavee://diag?cmd=seek&ms=52000'
        Verb 'wavee://diag?cmd=stage&mode=visualizer&face=aurora&gallery=1&open=1' ; Wait 5000
        Shot 'visualizer' 'settle=300'
        Verb 'wavee://diag?cmd=stage&gallery=0' ; Wait 1500
        Shot 'visualizer-clean' 'settle=300'
        Verb 'wavee://diag?cmd=stage&open=0'
    }
    'video' = {
        # A music video in each placement. Its picture is DRM-protected and never reaches a capture: the shots keep the
        # surface as a transparent hole (the own window's screen grab as black), New-StoreImage.py --panel fills it with
        # a frame of the same video.
        Verb 'wavee://diag?cmd=stage&open=0'
        Verb 'wavee://play?ctx=spotify:track:6dOtVTDdiauQNBQEDOtlAB' ; Wait 6000          # BIRDS OF A FEATHER (has a video)
        Verb 'wavee://open?route=album&arg=spotify:album:7aJuG4TFXa2hmE4z1yxc3n' ; Wait 3000
        Verb 'wavee://diag?cmd=rail&mode=queue'
        Verb 'wavee://diag?cmd=video&mode=docked' ; Wait 4000
        Shot 'video-docked' 'settle=1200'
        Verb 'wavee://diag?cmd=video&mode=floating' ; Wait 3000
        Shot 'video-floating' 'settle=1200'
        Verb 'wavee://diag?cmd=video&mode=fullscreen' ; Wait 3000
        Shot 'video-full' 'settle=1200'
        Verb 'wavee://diag?cmd=video&mode=detached' ; Wait 5000
        Shot 'video-detached' 'settle=1200'
        Grab-PopOut 'video-popout'
        Verb 'wavee://diag?cmd=video&mode=off'
        Verb 'wavee://pause'
    }
    'artist' = {
        Verb 'wavee://diag?cmd=rail&mode=lyrics'
        Verb 'wavee://open?route=artist&arg=spotify:artist:4phGZZrJZRo4ElhRtViYdl' ; Wait 4500
        Shot 'artist' 'settle=1200'
    }
    'podcast' = {
        Verb 'wavee://diag?cmd=rail&mode=queue'
        Verb 'wavee://open?route=show&arg=spotify:show:79CkJF3UJTHFV8Dse3Oy0P' ; Wait 4500
        Shot 'podcast' 'settle=1200'
    }
}

foreach ($name in $scenes.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    Write-Host "scene $name"
    & $scenes[$name] | Out-Null
}
Verb 'wavee://diag?cmd=present&on=0' | Out-Null
Write-Host "shots in $Out"
