#requires -Version 5.1
<#
.SYNOPSIS
  Drive the verify Wavee through one evidence scenario and collect its bundles
  (docs/plans/evidence-diagnostics-implementation.md §C.1–§C.5, in ..\fluent-gpu).

.DESCRIPTION
  Scenarios (every one runs against the VERIFY instance Start-VerifyWavee.ps1 started — never the owner's):
    band      §C.1  issues #1 #2 #3a #3b — the artist band's reveal: bundles at rest, every 4 DIP of the 44-DIP ramp, and
                    pinned; pixel queries down the band and the magazine's top under it. Positioning is the engine's own
                    ScrollHandle (cmd=scroll … move=immediate), no input.
    edgecue   §C.3  issue #5 — a long playlist scrolled to 800 DIP; a pixel column under the list's top edge.
    soak      §C.4  issue #6 — the artist page: 20 s of wheel notches at 4 Hz, 20 s of hi-res bursts, 20 s of rested
                    notch pairs; one bundle at the end (its scroll.csv carries the turn_cost rows). Input needs the owner
                    idle (it waits) and takes the foreground for the bursts, handing it back.
    lyrics    §C.2  issue #4 — REFUSES unless -AllowPlayback: playing a track in the verify instance transfers the
                    ACCOUNT's playback away from the owner's device (Spotify plays on one device per account).
    backsteps §C.5  issue #7 — Read-Bundle.py backsteps over the OWNER's existing scroll-*.csv exports (read-only).
    load      2026-09-25 RCA A–E — cold-open the artist page (-Artist) and then a search (-Search), wait out the chart's
                    10 s recheck, bundle each at rest, and cut the run's own always-on lines (fetch.send/answer/miss/seal/
                    reseal/xm, artist.chart, ui.action, ui.spy, frame.slack, [render.pace], [scroll.engaged…]) into
                    load-<stamp>.log beside the bundles. Then runs the xm probe over every uri the fetch.miss lines named.
    xm        2026-09-25 RCA A/B — the server's own extended-metadata answer (kinds -Kinds, default 10,99,182,185) for
                    -Uris (comma-separated spotify uris): xm-<stamp>-<tag>.tsv, one row per (kind, uri) with the payload hex.
  Results land in the verify profile's logs\evidence\; the scenario prints the Read-Bundle.py analysis.

.PARAMETER Instance  The object Start-VerifyWavee.ps1 returned (Pid, Hwnd, EvidenceRoot). Started when omitted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('band', 'edgecue', 'soak', 'lyrics', 'backsteps', 'load', 'xm', 'drag')][string]$Scenario,
    [object]$Instance,
    [string]$Artist = 'spotify:artist:5JZ7CnR6gTvEMKX4g70Amv',
    [string]$Search = 'my favorite muze',
    [string]$Uris,
    [string]$Kinds = '10,99,182,185',
    [string]$Playlist = 'spotify:playlist:2pnt79m93NytfAj2lByLlQ',
    [string]$LyricsTrack,
    # drag: the list's band line under its pinned prefix (ItemClipTopInset, DIP) — where the pixel column starts.
    [double]$Inset = 93,
    [switch]$AllowPlayback,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Evidence.psm1') -Force -DisableNameChecking
$reader = Join-Path $PSScriptRoot 'Read-Bundle.py'
$ci = [System.Globalization.CultureInfo]::InvariantCulture

if ($Scenario -eq 'backsteps') {
    $csvs = Join-Path $env:LOCALAPPDATA 'Wavee\logs\scroll-*.csv'
    & $Python -X utf8 $reader backsteps $csvs
    return
}
if ($Scenario -eq 'lyrics' -and -not $AllowPlayback) {
    throw 'lyrics needs playback, and playing in the verify instance takes the account''s playback from the owner''s device — pass -AllowPlayback only with the owner''s go-ahead'
}
if (-not $Instance) { $Instance = & (Join-Path $PSScriptRoot 'Start-VerifyWavee.ps1') }
$h = [IntPtr]$Instance.Hwnd
$root = $Instance.EvidenceRoot

function Diag([string]$uri, [int]$timeoutMs = 10000) { Invoke-WaveeDiag -Hwnd $h -Uri $uri -EvidenceRoot $root -TimeoutMs $timeoutMs }
function Open([string]$route, [string]$arg) {
    Send-WaveeUri -Hwnd $h -Uri ("wavee://open?route=$route&arg=" + [Uri]::EscapeDataString($arg))
    Start-Sleep -Seconds 6   # navigation + hydration settle (the page mounts, the overview lands)
}
function Bundle([string]$tag) { (Diag "wavee://diag?cmd=bundle&tag=$tag" 15000).Path }
function Pixel([double]$x, [double]$y) {
    [void](Diag ("wavee://diag?cmd=pixel&dip=1&x=" + $x.ToString('0.#', $ci) + "&y=" + $y.ToString('0.#', $ci)))
}
function LogLines {
    # The run's own always-on lines, by pid, from the verify profile's dated log(s) — the evidence the RCA quotes.
    $files = @(Get-ChildItem -LiteralPath $Instance.LogFolder -Filter 'wavee-*.log' -File | Sort-Object Name)
    $pidTok = 'pid=' + $Instance.Pid.ToString($ci) + ' '
    foreach ($f in $files) { Get-Content -LiteralPath $f.FullName -Encoding UTF8 | Where-Object { $_.Contains($pidTok) } }
}
function Xm([string]$uriList, [string]$tag) {
    $r = Diag ("wavee://diag?cmd=xm&tag=$tag&kinds=" + [Uri]::EscapeDataString($Kinds) + "&uris=" + [Uri]::EscapeDataString($uriList)) 30000
    $r.Path
}
function ScrollTo([string]$vp, [double]$to) {
    [void](Diag ("wavee://diag?cmd=scroll&vp=$vp&to=" + $to.ToString('0.###', $ci) + "&move=immediate"))
    Start-Sleep -Milliseconds 300   # a few presents: the plan moves on the next one, the band/clip posers follow
}

switch ($Scenario) {
    'band' {
        [void](Diag 'wavee://diag?cmd=probe&level=trace')
        Open 'artist' $Artist
        $vpSpec = 'artist:' + $Artist   # the viewport key of THIS artist page (another artist tab may be kept alive)
        ScrollTo $vpSpec 0
        $rest = Bundle 'band-rest'
        $geo = Get-BandGeometry -Keyed @(Read-EvidenceTsv (Join-Path $rest 'keyed.tsv')) -ArtistUri $Artist
        if (-not $geo) { throw "keyed.tsv of $rest has no artist-scroll:artist:$Artist / artist-under-band — is the artist page open?" }
        $vx = $geo.ViewportX; $vy = $geo.ViewportY; $vw = $geo.ViewportW
        "collapse distance cd=$($geo.CollapseDistance)  ramp=[$($geo.RampStart), $($geo.CollapseDistance)]  viewport y=$vy w=$vw"
        $folders = @($rest)
        foreach ($o in (Get-BandSweepOffsets -CollapseDistance $geo.CollapseDistance)) {
            ScrollTo $vpSpec $o
            # the band's pivot lane (tab glyphs), its underline row, and the magazine's top rows under the 56-DIP line
            foreach ($fx in 0.30, 0.38, 0.46) { foreach ($dy in 28, 52, 54) { Pixel ($vx + $vw * $fx) ($vy + $dy) } }
            foreach ($dy in 57, 60, 64, 70, 80) { Pixel ($vx + 60) ($vy + $dy) }
            $folders += Bundle ('band-' + [math]::Round($o).ToString($ci))
        }
        & $Python -X utf8 $reader band --viewport $vpSpec --clip-top-dip ($vy + 56) @folders
    }
    'edgecue' {
        Open 'pl' $Playlist
        $v = Diag 'wavee://diag?cmd=vps'
        $vps = @(Read-EvidenceTsv $v.Path)
        $list = $vps | Where-Object { $_.key -and [double]::Parse($_.extent, $ci) -gt 10000 } | Select-Object -First 1
        if (-not $list) { throw 'no long list viewport found (vps.tsv)' }
        $key = ($list.key -split '/', 2)[-1]
        ScrollTo $key 800
        $b0 = Bundle 'edgecue-probe'
        $k = @(Read-EvidenceTsv (Join-Path $b0 'nodes.tsv'))
        $meta = Get-Content (Join-Path $b0 'meta.json') -Raw | ConvertFrom-Json
        $vpRow = $meta.viewports | Where-Object { $_.key -eq $list.key } | Select-Object -First 1
        # the list's top edge in window DIP: the viewport node's own rect (nodes.tsv carries every node's window box)
        $vpNode = $k | Where-Object { $vpRow -and [int]$_.index -eq [int]$vpRow.node } | Select-Object -First 1
        if (-not $vpNode) { throw "the list viewport node is not in nodes.tsv of $b0" }
        $y0 = [double]::Parse($vpNode.y, $ci)
        $x0 = [double]::Parse($vpNode.x, $ci) + 120
        foreach ($d in 1, 4, 8, 16, 24, 32, 40) { Pixel $x0 ($y0 + $d) }
        $b = Bundle 'edgecue-800'
        & $Python -X utf8 $reader edgecue $b
    }
    'soak' {
        [void](Diag 'wavee://diag?cmd=probe&level=trace')
        Open 'artist' $Artist
        $b0 = Bundle 'soak-start'
        $geo = Get-BandGeometry -Keyed @(Read-EvidenceTsv (Join-Path $b0 'keyed.tsv')) -ArtistUri $Artist
        if (-not $geo) { throw "keyed.tsv of $b0 has no artist-scroll:artist:$Artist — is the artist page open?" }
        $cx = $geo.ViewportX + $geo.ViewportW / 2; $cy = $geo.ViewportY + 300
        # 20 s of detented notches at 4 Hz (down 40, up 40)
        Send-WheelBurst -Hwnd $h -X $cx -Y $cy -Delta -120 -Count 40 -IntervalMs 250
        Send-WheelBurst -Hwnd $h -X $cx -Y $cy -Delta 120 -Count 40 -IntervalMs 250
        # 20 s of hi-res bursts (an eighth of a notch each, 8 ms apart, in bursts of 16)
        for ($i = 0; $i -lt 20; $i++) { Send-WheelBurst -Hwnd $h -X $cx -Y $cy -Delta ($(if ($i % 2) { 15 } else { -15 })) -Count 16 -IntervalMs 8 -IdleMs 0; Start-Sleep -Milliseconds 850 }
        # 20 s of the "blocked" pattern: a notch after 2 s of rest
        for ($i = 0; $i -lt 8; $i++) { Start-Sleep -Seconds 2; Send-WheelBurst -Hwnd $h -X $cx -Y $cy -Delta -120 -Count 1 -IntervalMs 0 }
        $b = Bundle 'soak-end'
        & $Python -X utf8 $reader turns $b
    }
    'load' {
        [void](Diag 'wavee://diag?cmd=probe&level=trace')
        $mark = @(LogLines).Count
        Open 'artist' $Artist
        Start-Sleep -Seconds 12   # the chart's 10 s recheck deadline (Artist.UI.Chart.cs RecheckMs) and a miss's two backoffs
        $vpSpec = 'artist:' + $Artist
        ScrollTo $vpSpec 0
        $artistBundle = Bundle 'load-artist'
        Open 'search' $Search
        Start-Sleep -Seconds 8
        $searchBundle = Bundle 'load-search'
        $lines = @(LogLines)
        $lines = if ($lines.Count -gt $mark) { $lines[$mark..($lines.Count - 1)] } else { @() }
        $keep = '\] (fetch\.(send|answer|miss|seal|reseal|xm)|artist\.chart|ui\.action|ui\.spy|nav\.route|frame\.slack)|\[render\.pace\]|\[scroll\.engaged'
        $cut = Join-Path $root ('load-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
        $lines | Where-Object { $_ -match $keep } | Set-Content -LiteralPath $cut -Encoding UTF8
        "bundles: $artistBundle ; $searchBundle"
        "lines:   $cut"
        $missed = @($lines | Where-Object { $_ -match '\] fetch\.(miss|seal) ' } | ForEach-Object { if ($_ -match ' uri=(spotify:\S+)') { $Matches[1] } }) | Select-Object -Unique
        if ($missed.Count -gt 0) { "xm probe of the $($missed.Count) missed uri(s): " + (Xm ($missed -join ',') 'load-missed') }
        else { 'no fetch.miss lines in this run' }
    }
    'drag' {
        # Item G: fast NON-WHEEL scrolling of a long playlist — real thumb drags down and up the Q-top 1500 list. The app's
        # auto evidence bundle (first coverage clamp of a burst) captures the blank; this collects the lines around it.
        [void](Diag 'wavee://diag?cmd=probe&level=trace')
        $mark = @(LogLines).Count
        $startedAt = Get-Date
        Open 'pl' $Playlist
        Start-Sleep -Seconds 4
        $v = Diag 'wavee://diag?cmd=vps'
        $vps = @(Read-EvidenceTsv $v.Path)
        $list = $vps | Where-Object { $_.horizontal -eq '0' -and [double]::Parse($_.extent, $ci) -gt 10000 } |
            Sort-Object { [double]::Parse($_.extent, $ci) } -Descending | Select-Object -First 1
        if (-not $list) { throw 'no long vertical list viewport found (vps.tsv)' }
        "list: key=$($list.key) extent=$($list.extent) viewport=$($list.viewport) rect=$($list.x),$($list.y),$($list.w)x$($list.h) items=$($list.items) prefix=$($list.prefix) cover=[$($list.coverStart),$($list.coverEnd)] realized=[$($list.first),$($list.last))"
        $x = [double]::Parse($list.x, $ci); $y = [double]::Parse($list.y, $ci)
        $w = [double]::Parse($list.w, $ci); $hh = [double]::Parse($list.h, $ci); $ext = [double]::Parse($list.extent, $ci)
        "window dpi=$([EvidenceWin32]::GetDpiForWindow($h)) (client DIP -> px x$([EvidenceWin32]::GetDpiForWindow($h) / 96.0))"
        $tailKey = ($list.key -split '/', 2)[-1]
        # A column of pixel queries down the rows' band (window DIP), 60 DIP inside the list's left edge — below the band
        # line (-Inset) + its feather; issued before a bundle they land IN it (the reply files join the next bundle).
        $column = { for ($py = $y + $Inset + 30; $py -lt $y + $hh - 8; $py += 48) { Pixel ($x + 60) $py } }
        $folders = @()
        $legs = @(@{ From = 0.0; To = 0.85 }, @{ From = 0.85; To = 0.15 }, @{ From = 0.15; To = 0.95 }, @{ From = 0.95; To = 0.0 })
        foreach ($leg in $legs) {
            $cur = @(Read-EvidenceTsv (Diag 'wavee://diag?cmd=vps').Path) | Where-Object { $_.key -eq $list.key } | Select-Object -First 1
            $off = [double]::Parse($cur.offset, $ci)
            $g = Get-ThumbGeometry -X $x -Y $y -W $w -H $hh -Offset $off -Extent $ext
            $toY = $g.TrackTop + $g.Length / 2 + $leg.To * $g.Travel
            $tag = 'drag-' + [math]::Round($leg.From * 100).ToString($ci) + '-' + [math]::Round($leg.To * 100).ToString($ci)
            # MID-DRAG, the button still held and the thumb captured: pixel column + a bundle of the frame being dragged.
            $mid = { & $column; $script:midBundle = Bundle ($tag + '-mid') }
            Send-ThumbDrag -Hwnd $h -X $g.X -Y0 $g.Y -Y1 $toY -Steps 20 -StepMs 10 -Midway $mid -MidwayStep 10 -Verbose
            Start-Sleep -Milliseconds 400
            & $column
            # Bundle AT ONCE: the trace rings hold ~10 s of render rows; the drag's pose / coverage / input rows must be in it.
            $legBundle = Bundle $tag
            $folders += @($script:midBundle, $legBundle)
            "  bundles: $($script:midBundle) ; $legBundle"
            Start-Sleep -Milliseconds 1000
            $after = @(Read-EvidenceTsv (Diag 'wavee://diag?cmd=vps').Path) | Where-Object { $_.key -eq $list.key } | Select-Object -First 1
            $moved = [math]::Abs([double]::Parse($after.offset, $ci) - $off)
            "drag $($leg.From)->$($leg.To): offset $off -> $($after.offset)  (thumb at y=$([math]::Round($g.Y,1)) -> $([math]::Round($toY,1)), len=$([math]::Round($g.Length,1)))"
            if ($moved -lt 0.25 * [math]::Abs($leg.To - $leg.From) * ($ext - $hh)) { "  WARNING: the drag did not move the list as far as the thumb travelled — the press missed the thumb?" }
        }
        $autos = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -like '*-clamp-auto' -and $_.LastWriteTime -gt $startedAt } | ForEach-Object FullName)
        "auto clamp bundles this run: $($autos.Count)"
        & $Python -X utf8 $reader drag @folders @autos --key $tailKey --inset $Inset
        Start-Sleep -Seconds 2
        $lines = @(LogLines)
        $lines = if ($lines.Count -gt $mark) { $lines[$mark..($lines.Count - 1)] } else { @() }
        $keep = '\[evidence\]|\] (scroll\.frames|scroll\.burst|frame\.slow|frame\.slack|nav\.route|nav\.frames)|\[render\.pace\]|\[alloc'
        $cut = Join-Path $root ('drag-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
        $lines | Where-Object { $_ -match $keep } | Set-Content -LiteralPath $cut -Encoding UTF8
        "lines: $cut"
    }
    'xm' {
        if (-not $Uris) { throw 'pass -Uris spotify:track:a,spotify:track:b' }
        Xm $Uris 'xm'
    }
    'lyrics' {
        if (-not $LyricsTrack) { throw 'pass -LyricsTrack spotify:track:<id with synced lyrics>' }
        [void](Diag 'wavee://diag?cmd=probe&level=trace')
        Send-WaveeUri -Hwnd $h -Uri $LyricsTrack
        Start-Sleep -Seconds 8
        $folders = @()
        for ($i = 0; $i -lt 45; $i++) { $folders += Bundle ('lyrics-' + $i.ToString('00', $ci)); Start-Sleep -Seconds 2 }
        & $Python -X utf8 $reader lyrics @folders
    }
}
