#requires -Version 5.1
<#
.SYNOPSIS
  Render a framed Wavee release image (1200x675 JPEG, <=150 KB) from a raw screenshot.

.DESCRIPTION
  Substitutes ops/release/tools/frame.html with the given shot(s) + tint, renders it at 2x with
  headless Edge, then downscales once with ffmpeg (lanczos) stepping the quality until the file
  fits under -MaxBytes.

  Dev-box tool: it needs Microsoft Edge and ffmpeg on the machine. Source PNGs live in
  artifacts\media-src\ (not in git); only the encoded JPEG belongs under
  ops\release\wavee\<semver>\media\.

.EXAMPLE
  .\New-ReleaseImage.ps1 -Shot artifacts\media-src\home.png -Out ops\release\wavee\0.2.0\media\redesigned.jpg

.EXAMPLE
  .\New-ReleaseImage.ps1 -Shot artifacts\media-src\home.png -Out artifacts\media-src\detail.jpg -Variant detail -Zoom 1.35 -Cx 240 -Cy 180

.EXAMPLE
  .\New-ReleaseImage.ps1 -Shot artifacts\media-src\home.png -Shot2 artifacts\media-src\settings.png -Out artifacts\media-src\twoup.jpg -Variant twoup
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Shot,
  [Parameter(Mandatory)][string]$Out,
  [ValidateSet('card','detail','twoup')][string]$Variant = 'card',
  [string]$Shot2,
  [string]$TintA = '#3d5a8f',
  [string]$TintB = '#6b3a63',
  [double]$Scale = 0.88,
  [switch]$NoSharpen,
  [int]$Radius = 14,
  [double]$Zoom = 1.0,   # detail: 1.0 = one capture pixel per output pixel (pixel-exact); >1.5 visibly enlarges
  [int]$Cx = 0,
  [int]$Cy = 0,
  [int]$MaxBytes = 150000,
  [int]$Width = 1200,
  [int]$Height = 675,
  [ValidateSet('jpg','webp')][string]$Format = 'jpg',
  [switch]$Keep2x
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------------------

# Run a native exe under $ErrorActionPreference='Stop' without letting its stderr become a
# terminating error. Same shape as Invoke-Native in ops/build/Wavee.Build.psm1 (kept local on
# purpose: this tool stays standalone - it must run from a bare checkout).
function Invoke-Native([string]$FilePath, [string[]]$ArgumentList, [switch]$AllowFailure) {
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $out = $null
  $code = 0
  $global:LASTEXITCODE = 0
  try {
    try {
      $out = & $FilePath @ArgumentList 2>&1
      if ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }
    }
    catch [System.Management.Automation.CommandNotFoundException] {
      $code = 127
      $out = @("$FilePath : command not found")
    }
  }
  finally { $ErrorActionPreference = $prev }
  if ($code -ne 0 -and -not $AllowFailure) { throw "$FilePath exited $code`n$($out -join "`n")" }
  [pscustomobject]@{ ExitCode = $code; Output = @($out | ForEach-Object { "$_" }) }
}

function Find-Edge {
  $candidates = @(
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
  )
  foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
  $cmd = Get-Command msedge -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  throw ("Microsoft Edge was not found. Looked for:`n  " + ($candidates -join "`n  ") +
         "`n  msedge on PATH`nEdge ships with Windows 11; reinstall it or put msedge.exe on PATH.")
}

function Find-Ffmpeg {
  $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  throw "ffmpeg was not found on PATH. Install it (winget install Gyan.FFmpeg) and reopen the shell."
}

# Width/height straight out of the PNG IHDR (bytes 16..23, big-endian). No image library needed.
function Get-PngSize([string]$Path) {
  $head = New-Object byte[] 24
  $fs = [System.IO.File]::OpenRead($Path)
  try {
    $read = $fs.Read($head, 0, 24)
    if ($read -lt 24) { throw "Not a PNG (shorter than a PNG header): $Path" }
  }
  finally { $fs.Dispose() }
  $sig = @(137,80,78,71,13,10,26,10)
  for ($i = 0; $i -lt 8; $i++) {
    if ($head[$i] -ne $sig[$i]) { throw "Not a PNG (bad signature): $Path" }
  }
  $w = ([int]$head[16] -shl 24) -bor ([int]$head[17] -shl 16) -bor ([int]$head[18] -shl 8) -bor [int]$head[19]
  $h = ([int]$head[20] -shl 24) -bor ([int]$head[21] -shl 16) -bor ([int]$head[22] -shl 8) -bor [int]$head[23]
  [pscustomobject]@{ Width = $w; Height = $h }
}

function ConvertTo-FileUrl([string]$Path) {
  return ([System.Uri](Resolve-Path -LiteralPath $Path).ProviderPath).AbsoluteUri
}

function Write-Utf8NoBom([string]$Path, [string]$Text) {
  $enc = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

function Format-Num($Value) {
  return ([double]$Value).ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

# ---------------------------------------------------------------------------------------------------
# inputs
# ---------------------------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $Shot)) { throw "Shot not found: $Shot" }
$shotPath = (Resolve-Path -LiteralPath $Shot).ProviderPath
$shotSize = Get-PngSize $shotPath

$shot2Path = $shotPath
if ($Shot2) {
  if (-not (Test-Path -LiteralPath $Shot2)) { throw "Shot2 not found: $Shot2" }
  $shot2Path = (Resolve-Path -LiteralPath $Shot2).ProviderPath
  $null = Get-PngSize $shot2Path
}
elseif ($Variant -eq 'twoup') {
  throw "-Variant twoup needs a second screenshot: pass -Shot2 <png>."
}

# card: the recipe sizes the shot by WIDTH (--scale of the canvas). A capture that is taller than 16:9
# would then overflow the frame and eat its own margins/shadow, so lower --scale until the shot also
# fits --scale of the canvas HEIGHT. The CSS stays the recipe; only the injected --scale moves.
if ($Variant -eq 'card') {
  $shotW = $Width * $Scale
  $shotH = $shotW * ($shotSize.Height / $shotSize.Width)
  $maxH = $Height * $Scale
  if ($shotH -gt $maxH) {
    $Scale = [math]::Round($Scale * ($maxH / $shotH), 4)
    Write-Host ("note: shot is $($shotSize.Width)x$($shotSize.Height) (taller than the frame) - " +
                "fitting to height, scale -> $(Format-Num $Scale)")
  }
  # NEVER upscale the capture: the frame renders at 2x, so a shot wider than (source px / 2) in CSS px
  # would be enlarged first and shrunk later - that double resample is what reads as "not anti-aliased".
  # Cap --scale so the shot sits at <= 1:1 source pixels inside the 2x render (a 100%-DPI 1582 px capture
  # therefore lands at ~66% of the canvas; capture at 200% DPI to fill more of it).
  $maxNoUpscale = [math]::Floor(($shotSize.Width / (2.0 * $Width)) * 10000) / 10000
  if ($Scale -gt $maxNoUpscale) {
    $Scale = $maxNoUpscale
    Write-Host ("note: capping scale -> $(Format-Num $Scale) so the $($shotSize.Width) px capture is never upscaled " +
                "(pass a 2x-DPI capture for a larger shot)")
  }
}

if ($Variant -eq 'detail' -and $Zoom -gt 1.5) {
  Write-Host ("note: -Zoom $(Format-Num $Zoom) enlarges the capture $(Format-Num ($Zoom))x in the output; the capture " +
              "carries the display's DPI (150% on a 144-dpi screen), so anything past ~1.5 goes soft. Crop tighter instead.")
}
$outLeaf = Split-Path -Leaf $Out
$outDir = Split-Path -Parent $Out
if (-not $outDir) { $outDir = (Get-Location).ProviderPath }
if (-not (Test-Path -LiteralPath $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
$outPath = Join-Path (Resolve-Path -LiteralPath $outDir).ProviderPath $outLeaf

$ext = [System.IO.Path]::GetExtension($outPath).ToLowerInvariant()
if ($ext -eq '.webp') {
  if ($PSBoundParameters.ContainsKey('Format') -and $Format -ne 'webp') {
    throw "-Format $Format contradicts the .webp extension of -Out."
  }
  $Format = 'webp'
}
elseif ($ext -eq '.jpg' -or $ext -eq '.jpeg') {
  if ($PSBoundParameters.ContainsKey('Format') -and $Format -ne 'jpg') {
    throw "-Format $Format contradicts the $ext extension of -Out."
  }
  $Format = 'jpg'
}
else {
  throw ("-Out must end in .jpg or .webp (got '$ext'). JPEG is the default: the release validator " +
         "accepts .webp, but the app only DECODES WebP when the Store's WebP codec is installed.")
}

$edge = Find-Edge
$ffmpeg = Find-Ffmpeg

$template = Join-Path $PSScriptRoot 'frame.html'
if (-not (Test-Path -LiteralPath $template)) { throw "Template not found: $template" }

# ---------------------------------------------------------------------------------------------------
# 1. materialise the frame
# ---------------------------------------------------------------------------------------------------

$work = Join-Path $env:TEMP ('wavee-frame\' + [guid]::NewGuid().ToString('n'))
$null = New-Item -ItemType Directory -Path $work -Force

try {
  $html = Get-Content -LiteralPath $template -Raw
  $map = [ordered]@{
    '__VARIANT__' = $Variant
    '__SHOT2__'   = (ConvertTo-FileUrl $shot2Path)
    '__SHOT__'    = (ConvertTo-FileUrl $shotPath)
    '__W__'       = "$Width"
    '__H__'       = "$Height"
    '__BASE__'    = '#202020'
    '__TINT_A__'  = $TintA
    '__TINT_B__'  = $TintB
    '__RADIUS__'  = "$Radius"
    '__SCALE__'   = (Format-Num $Scale)
    '__SRCW__'    = "$($shotSize.Width)"
    '__ZOOM__'    = (Format-Num $Zoom)
    '__CX__'      = "$Cx"
    '__CY__'      = "$Cy"
  }
  foreach ($k in $map.Keys) { $html = $html.Replace($k, [string]$map[$k]) }
  $leftover = [regex]::Match($html, '__[A-Z0-9_]+__')
  if ($leftover.Success) { throw "frame.html still has an unsubstituted token: $($leftover.Value)" }

  $framePath = Join-Path $work 'frame.html'
  Write-Utf8NoBom $framePath $html
  $frameUrl = ([System.Uri]$framePath).AbsoluteUri

  # -------------------------------------------------------------------------------------------------
  # 2. render at 2x with headless Edge
  # -------------------------------------------------------------------------------------------------

  $png2x = Join-Path $work 'card@2x.png'
  $edgeArgs = @(
    '--headless=new',
    '--disable-gpu',
    '--hide-scrollbars',
    '--no-first-run',
    '--no-default-browser-check',
    "--user-data-dir=$work\profile",
    '--force-device-scale-factor=2',
    "--window-size=$Width,$Height",
    '--default-background-color=00000000',
    '--virtual-time-budget=2000',
    "--screenshot=$png2x",
    $frameUrl
  )
  # msedge.exe re-launches itself, so `& $edge` returns before the screenshot is written:
  # Start-Process -Wait is the only reliable way to know it finished.
  $edgeOut = Join-Path $work 'edge.out.txt'
  $edgeErr = Join-Path $work 'edge.err.txt'
  $proc = Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -PassThru -NoNewWindow `
                        -RedirectStandardOutput $edgeOut -RedirectStandardError $edgeErr
  if (-not (Test-Path -LiteralPath $png2x)) {
    $log = @()
    foreach ($f in @($edgeOut, $edgeErr)) {
      if (Test-Path -LiteralPath $f) { $log += (Get-Content -LiteralPath $f -Raw) }
    }
    throw ("Edge wrote no screenshot (exit $($proc.ExitCode)).`n" + ($log -join "`n"))
  }

  $got = Get-PngSize $png2x
  $wantW = 2 * $Width
  $wantH = 2 * $Height
  if ($got.Width -ne $wantW -or $got.Height -ne $wantH) {
    throw ("Edge rendered $($got.Width)x$($got.Height), expected ${wantW}x${wantH}. " +
           "--force-device-scale-factor=2 did not take effect.")
  }

  # -------------------------------------------------------------------------------------------------
  # 3. downscale once + encode under the byte budget
  # -------------------------------------------------------------------------------------------------

  # lanczos + accurate rounding, then a light unsharp to restore the micro-contrast the downscale removes.
  # -NoSharpen leaves the plain lanczos result.
  $vf = "scale=${Width}:${Height}:flags=lanczos+accurate_rnd+full_chroma_int"
  if (-not $NoSharpen) { $vf = $vf + ",unsharp=5:5:0.45:5:5:0.0" }
  $chosen = -1
  $bytes = 0

  if ($Format -eq 'jpg') {
    for ($q = 2; $q -le 15; $q++) {
      # yuvj444p: full-resolution chroma. The default 4:2:0 halves colour resolution and is the single biggest
      # source of "blurry icon edges" in a UI screenshot; it costs ~20-30% more bytes, well inside the budget.
      $null = Invoke-Native $ffmpeg @('-y','-loglevel','error','-i',$png2x,'-vf',$vf,'-pix_fmt','yuvj444p','-q:v',"$q",$outPath)
      $bytes = (Get-Item -LiteralPath $outPath).Length
      if ($bytes -le $MaxBytes) { $chosen = $q; break }
    }
    if ($chosen -lt 0) {
      throw ("Could not fit $outPath under $MaxBytes bytes even at q=15 ($bytes bytes). The shot is " +
             "too busy: crop it (-Variant detail) or raise -MaxBytes deliberately.")
    }
  }
  else {
    for ($q = 90; $q -ge 60; $q -= 5) {
      $null = Invoke-Native $ffmpeg @('-y','-loglevel','error','-i',$png2x,'-vf',$vf,'-c:v','libwebp','-quality',"$q",$outPath)
      $bytes = (Get-Item -LiteralPath $outPath).Length
      if ($bytes -le $MaxBytes) { $chosen = $q; break }
    }
    if ($chosen -lt 0) {
      throw "Could not fit $outPath under $MaxBytes bytes even at quality 60 ($bytes bytes)."
    }
  }

  if ($Keep2x) {
    $kept = [System.IO.Path]::Combine(
      [System.IO.Path]::GetDirectoryName($outPath),
      [System.IO.Path]::GetFileNameWithoutExtension($outPath) + '@2x.png')
    Copy-Item -LiteralPath $png2x -Destination $kept -Force
    Write-Host "$kept  $((Get-Item -LiteralPath $kept).Length) B  ${wantW}x${wantH}"
  }

  Write-Host "$outPath  $bytes B  q=$chosen  ${Width}x${Height}"
}
finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
