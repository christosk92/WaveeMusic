<#
.SYNOPSIS
  Build a signed MSIX for Wavee (NativeAOT packaged Win32 full-trust).

.DESCRIPTION
  Pipeline:  dotnet publish -> stage layout + tile logos -> makepri -> makeappx pack -> verify -> signtool sign -> .msix
  Same-OS NativeAOT cross-arch (ARM64 host -> win-x64) is supported if the VS C++ x64/x86 build tools are installed.

  Version identity comes from src/apps/Wavee/Wavee.Version.props, so a plain dev pack needs NO flags at all:
    Identity/@Version (the MSIX quad) = <WaveeVersion core>.<WaveeBuild>       -Quad overrides
    InformationalVersion              = <semver>+build.<N>.sha.<sha7>          -Semver / -Commit override
    channel                           = dev                                     -Channel overrides
  ops/release/wavee-release.ps1 passes every value explicitly for a real release; nothing here bumps the counter.

.EXAMPLE
  powershell -File ops\build\pack-wavee-msix.ps1
  powershell -File ops\build\pack-wavee-msix.ps1 -Arch x64 -NoSign -OutputDir artifacts\x64-probe
  powershell -File ops\build\pack-wavee-msix.ps1 -Arch x64 -NoAot            # self-contained JIT if AOT cannot target this arch
  powershell -File ops\build\pack-wavee-msix.ps1 -Quad 0.2.0.7 -Semver 0.2.0 -Channel stable -TrustedSigning
  powershell -File ops\build\pack-wavee-msix.ps1 -FeedRelease wavee-local -UpdateBaseUrl http://127.0.0.1:8099/   # local E2E
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [ValidateSet('arm64','x64')]
  [string]$Arch = $(
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    if ("$a" -match 'ARM64') { 'arm64' } else { 'x64' }),
  # M.m.p.N. Default: the props semver core + the props WaveeBuild counter (a dev pack).
  [string]$Quad = '',
  # M.m.p or M.m.p-beta.N. Default: the props WaveeVersion.
  [string]$Semver = '',
  [ValidateSet('stable','beta','dev')]
  [string]$Channel = 'dev',
  [string]$Codename = '',
  [string]$IdentityName = 'cproducts.Wavee',
  [string]$DisplayName = 'Wavee',
  [string]$Protocol = 'wavee',
  [string]$Commit = '',
  [string]$BuildDate = '',
  # Release-notes payload (whatsnew.json + media) copied to layout\Assets\whatsnew\ so the running app can show the
  # notes for its OWN version with no network.
  [string]$NotesDir = '',
  # The rolling GitHub release carrying the .appinstaller feed this package polls. Build-time metadata (stamped into
  # the assembly), never a runtime switch - an E2E test package is packed with wavee-stable-test.
  [string]$FeedRelease = 'wavee-stable',
  # The root the update feed and the release-notes documents hang off: <base><feed-release>/Wavee.<arch>.appinstaller
  # and <base>wavee-v<semver>/whatsnew.json. Build-time metadata like -FeedRelease, never a runtime switch - the local
  # end-to-end harness packs against http://127.0.0.1:8099/ and the produced package genuinely polls loopback.
  [string]$UpdateBaseUrl = 'https://github.com/christosk92/WaveeMusic/releases/download/',
  [switch]$PublicOnly,
  [string]$Configuration = 'Release',
  [string]$Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL',
  [string]$OutputDir = 'artifacts',
  [switch]$NoAot,
  [switch]$NoSign,
  [switch]$Install,
  [switch]$TrustedSigning,
  [string]$Metadata,
  [string]$Subscription = 'Azure subscription 1'
)
$ErrorActionPreference = 'Stop'

$buildDir = $PSScriptRoot
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $buildDir 'Wavee.Build.psm1') -Force -DisableNameChecking

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------------------------------------------
# Version identity
# ---------------------------------------------------------------------------------------------------------------
$props = Get-WaveeVersionProps (Join-Path $root 'src\apps\Wavee\Wavee.Version.props')
if (-not $Semver) { $Semver = $props.Version }
if (-not $Codename) { $Codename = $props.Codename }
if (-not $Quad) { $Quad = ($Semver -replace '-.*$','') + '.' + $props.Build }
if ($Quad -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Quad must be 4 numeric parts (e.g. 0.2.0.7); got '$Quad'." }
foreach ($part in $Quad.Split('.')) { if ([int]$part -gt 65535) { throw "Quad part > 65535 (Windows rejects it): $Quad" } }
if (-not $Commit) {
  $g = Invoke-Native 'git' @('-C',$root,'rev-parse','--short=7','HEAD') -AllowFailure
  if ($g.ExitCode -eq 0 -and $g.Output.Count -gt 0) { $Commit = "$($g.Output[0])".Trim() }
}
if (-not $BuildDate) { $BuildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }

# The update base URL is baked into the assembly, so a malformed value ships a package that can never find its feed
# (and a plain-http value shipped to real users would download an update over an unauthenticated channel). Absolute
# http(s) only; plain http only when the host is loopback, which is exactly the local end-to-end harness.
$defaultUpdateBaseUrl = 'https://github.com/christosk92/WaveeMusic/releases/download/'
$baseUri = $UpdateBaseUrl -as [Uri]
if ($null -eq $baseUri -or -not $baseUri.IsAbsoluteUri) {
  throw "-UpdateBaseUrl must be an absolute URL (e.g. $defaultUpdateBaseUrl); got '$UpdateBaseUrl'."
}
if ($baseUri.Scheme -ne 'http' -and $baseUri.Scheme -ne 'https') {
  throw "-UpdateBaseUrl must be http or https; got scheme '$($baseUri.Scheme)' in '$UpdateBaseUrl'."
}
if ($baseUri.Scheme -eq 'http' -and -not $baseUri.IsLoopback) {
  throw "-UpdateBaseUrl may only use plain http for a loopback host (127.0.0.1 / localhost, the local E2E feed); got '$UpdateBaseUrl'."
}
# Trailing slash is part of the contract: the app concatenates <base><feed>/<asset>. WaveeVersionInfo normalizes it
# too, but normalizing here means the banner, the summary and the stamped metadata all agree.
if (-not $UpdateBaseUrl.EndsWith('/')) { $UpdateBaseUrl = $UpdateBaseUrl + '/' }

# InformationalVersion is what the app reports as its own version and what About / the crash header / the release
# notes store read back. The build metadata after '+' is ignored by the semver comparison, so it is free to carry the
# quad's 4th part and the commit.
$infoVersion = "$Semver+build.$($Quad.Split('.')[3])"
if ($Commit) { $infoVersion = "$infoVersion.sha.$Commit" }

if ($TrustedSigning -and -not $PSBoundParameters.ContainsKey('Publisher')) {
  $Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL'
}
if (-not $Metadata) { $Metadata = Join-Path $buildDir 'signing\metadata.json' }

$csproj = Join-Path $root 'src\apps\Wavee\Wavee.csproj'
$iconSource = Join-Path $root 'src\apps\Wavee\assets\AppIcon\appicon-source.png'
$manifestTemplate = Join-Path $buildDir 'Wavee.AppxManifest.xml'
$rid = "win-$Arch"
$stamp = "Wavee_${Quad}_${Arch}"
$work = Join-Path $root ".msix-build\wavee-$Arch"
$pubDir = Join-Path $work 'publish'
$layout = Join-Path $work 'layout'
# -OutputDir may be absolute (the release orchestrator stages into artifacts\release\<semver>) or repo-relative.
$outRoot = $OutputDir
if (-not [IO.Path]::IsPathRooted($outRoot)) { $outRoot = Join-Path $root $OutputDir }
$outMsix = Join-Path $outRoot "$stamp.msix"
$cerPath = Join-Path $outRoot "$stamp.cer"
# The symbols that make a crash report from THIS package resolvable: the native PDB (+ the ILC map) for exactly the
# Wavee.exe inside the .msix. Folder for a local WinDbg .sympath, zip as the release asset next to the package.
$symDir = Join-Path $outRoot "symbols\$Quad\$rid"
$symZip = Join-Path $outRoot "Wavee-$Quad-$rid-symbols.zip"

$baseNote = ''
if ($UpdateBaseUrl -ne $defaultUpdateBaseUrl) { $baseNote = ", base $UpdateBaseUrl" }
Step "Wavee $Semver `"$Codename`" -> $Quad ($Channel, $Arch, feed $FeedRelease$baseNote)"

$tools = Get-WindowsSdkTools
$makeappx = $tools.MakeAppx
$makepri = $tools.MakePri
$signtool = $tools.SignTool
Step "SDK $($tools.Version)"
Add-VsInstallerToPath | Out-Null

$useAot = -not $NoAot
if ($useAot -and $Arch -eq 'x64' -and ("$env:PROCESSOR_ARCHITEW6432$env:PROCESSOR_ARCHITECTURE" -match 'ARM64')) {
  $cross = Test-X64CrossToolchain
  if (-not $cross.Ok) { throw $cross.Reason }
  Write-Host "    x64 cross link.exe: $($cross.LinkExe)"
}

# ---------------------------------------------------------------------------------------------------------------
# 1. publish
# ---------------------------------------------------------------------------------------------------------------
Step "Publishing $rid ($(if ($useAot) { 'NativeAOT' } else { 'self-contained JIT' }))"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pubDir, $outRoot | Out-Null
$pubArgs = @($csproj, '-c', $Configuration, '-r', $rid, '-o', $pubDir, '--nologo', '-v', 'm', '/p:NuGetAudit=false',
             "/p:InformationalVersion=$infoVersion",
             "/p:WaveeChannel=$Channel",
             "/p:WaveePackageVersion=$Quad",
             "/p:WaveeCommit=$Commit",
             "/p:WaveeBuildDate=$BuildDate",
             "/p:WaveeCodename=$Codename",
             "/p:WaveeFeedRelease=$FeedRelease",
             "/p:WaveeUpdateBaseUrl=$UpdateBaseUrl",
             # ALWAYS with symbols. A shipped build has StackTraceSupport=false, so a crash report carries nothing but
             # Wavee!<BaseAddress>+0x... offsets; without the PDB of the exact linked exe nothing can ever resolve them
             # (0.2.0.1 shipped that way and its NullReferenceException stayed a list of numbers). NativeDebugSymbols
             # adds ILC -g + link.exe /DEBUG - the ILC targets still pass /OPT:REF /OPT:ICF, so code layout is the
             # same as a symbol-less link and only a debug directory is added to the PE. The PDB and the map never
             # enter the package: they are moved out below, before the recursive layout copy.
             '/p:NativeDebugSymbols=true', '/p:DebugType=portable', '/p:IlcGenerateMapFile=true')
if ($PublicOnly) { $pubArgs += '-p:WaveeSkipPrivateSources=true' }
if (-not $useAot) { $pubArgs += @('-p:PublishAot=false', '--self-contained', 'true') }
& dotnet publish @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $pubDir 'Wavee.exe'))) { throw "Wavee.exe missing from $pubDir" }

# ---------------------------------------------------------------------------------------------------------------
# 1b. symbols -> $symDir + $symZip (BEFORE the recursive layout copy, so nothing of this can ship in the package)
#
#   Wavee.pdb      the native PDB link.exe /DEBUG wrote for THIS Wavee.exe. The ILC targets' _CopyAotSymbols target
#                  copies it from bin\<cfg>\net10.0\<rid>\native\ into the publish folder (deleting the managed IL
#                  PDB of the same name first); the bin\...\native\ copy is the fallback.
#   Wavee.map.xml  the ILC --map output. Written to obj\<cfg>\net10.0\<rid>\native\ (NativeIntermediateOutputPath),
#                  never to the publish folder. Names + lengths + content hashes of every emitted node; it carries
#                  NO addresses - the PDB is what resolves an RVA, the map is what says which methods changed.
#   SYMBOLS.txt    the build stamp + the sha256 of the exe the PDB belongs to, so a zip can be matched to a crash
#                  report's commit= / quad= lines without opening anything else.
# ---------------------------------------------------------------------------------------------------------------
Step "Collecting symbols -> $symDir"
Remove-Item $symDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $symZip -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $symDir | Out-Null
$objNative = Join-Path $root "src\apps\Wavee\obj\$Configuration\net10.0\$rid\native"
$binNative = Join-Path $root "src\apps\Wavee\bin\$Configuration\net10.0\$rid\native"

function Find-WaveeBuildArtifact {
  <# The expected locations first; otherwise the newest file of that name anywhere under obj\ and bin\ whose path
     names this RID (a target-path override in a future SDK must not silently drop the symbols). $null if none. #>
  param([string]$Name, [string[]]$Expected)
  foreach ($p in $Expected) { if ($p -and (Test-Path $p)) { return (Get-Item $p) } }
  $hits = @()
  foreach ($treeRoot in @((Join-Path $root 'src\apps\Wavee\obj'), (Join-Path $root 'src\apps\Wavee\bin'))) {
    if (-not (Test-Path $treeRoot)) { continue }
    $hits += @(Get-ChildItem $treeRoot -Recurse -File -Filter $Name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like "*\$rid\*" })
  }
  if ($hits.Count -eq 0) { return $null }
  $hits | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
}

$pdb = Find-WaveeBuildArtifact 'Wavee.pdb' @((Join-Path $pubDir 'Wavee.pdb'), (Join-Path $binNative 'Wavee.pdb'))
if ($null -eq $pdb) {
  if ($useAot) { throw "Wavee.pdb was not produced (looked in $pubDir, $binNative and under obj\bin for $rid); a package without symbols is what made the 0.2.0.1 crash unresolvable - not shipping one" }
  Write-Warning "no Wavee.pdb for the JIT layout; the symbols zip will carry the stamp only"
}
else {
  # MOVE out of the publish folder (the layout copy below is recursive); a copy from bin\ leaves the build tree alone.
  if ($pdb.DirectoryName -eq (Get-Item $pubDir).FullName) { Move-Item $pdb.FullName (Join-Path $symDir 'Wavee.pdb') -Force }
  else { Copy-Item $pdb.FullName (Join-Path $symDir 'Wavee.pdb') -Force }
  Write-Host "    Wavee.pdb      <- $($pdb.FullName)  ($([Math]::Round($pdb.Length / 1MB, 1)) MB)"
}
# Any *.pdb still in the publish folder is a managed one that came along for the ride (a module, a JIT layout);
# the layout purge below would drop it anyway - keep it with the symbols instead, it costs nothing.
Get-ChildItem $pubDir -Recurse -Include *.pdb -ErrorAction SilentlyContinue | ForEach-Object {
  $rel = $_.FullName.Substring((Get-Item $pubDir).FullName.Length).TrimStart('\')
  $dst = Join-Path $symDir $rel
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
  Move-Item $_.FullName $dst -Force
}

$map = $null
if ($useAot) {
  $map = Find-WaveeBuildArtifact 'Wavee.map.xml' @((Join-Path $objNative 'Wavee.map.xml'), (Join-Path $pubDir 'Wavee.map.xml'))
  if ($null -eq $map) { $map = Find-WaveeBuildArtifact 'Wavee.map' @((Join-Path $objNative 'Wavee.map'), (Join-Path $pubDir 'Wavee.map')) }
  if ($null -eq $map) { Write-Warning "IlcGenerateMapFile=true but no Wavee.map.xml was found under $objNative (or anywhere under obj\bin for $rid); the zip ships without the map" }
  else {
    if ($map.DirectoryName -eq (Get-Item $pubDir).FullName) { Move-Item $map.FullName (Join-Path $symDir $map.Name) -Force }
    else { Copy-Item $map.FullName (Join-Path $symDir $map.Name) -Force }
    Write-Host "    $($map.Name)  <- $($map.FullName)  ($([Math]::Round($map.Length / 1MB, 1)) MB)"
  }
}

$exeItem = Get-Item (Join-Path $pubDir 'Wavee.exe')
$exeHash = (Get-FileHash $exeItem.FullName -Algorithm SHA256).Hash.ToLower()
$stampLines = @(
  "Wavee symbols",
  "=============",
  "quad=$Quad",
  "semver=$Semver",
  "informationalVersion=$infoVersion",
  "channel=$Channel",
  "commit=$Commit",
  "buildDate=$BuildDate",
  "rid=$rid",
  "arch=$Arch",
  "configuration=$Configuration",
  "aot=$useAot",
  "package=$stamp.msix",
  "exe=Wavee.exe size=$($exeItem.Length) sha256=$exeHash",
  "",
  "Resolve a crash report's 'Frames (RVA)' lines against Wavee.pdb (docs/guide/releasing-wavee.md, 'Symbolicating a crash report'):",
  "  extract Wavee.exe from the .msix (it is a zip), put Wavee.pdb next to it, then",
  "  cdb -z Wavee.exe -y <this folder> -c `"ln Wavee+0x<rva>; q`"",
  "The PDB matches ONLY the Wavee.exe with the sha256 above; a report's commit= and quad= lines say which zip to take."
)
$utf8NoBomStamp = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $symDir 'SYMBOLS.txt'), (($stampLines -join "`r`n") + "`r`n"), $utf8NoBomStamp)

# ZipFile, not Compress-Archive: the 5.1 cmdlet re-encodes entry names and has a 2 GB ceiling; the map is ~100 MB of
# XML and the folder must round-trip byte-for-byte.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($symDir, $symZip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "    $symZip  ($([Math]::Round((Get-Item $symZip).Length / 1MB, 1)) MB)" -ForegroundColor Green

# Bundled playback modules into $pubDir\modules\<id>\ BEFORE the layout copy below, which is recursive - so the
# child-process modules ship inside the package (a packaged full-trust Win32 app may launch exes from its own
# package, which is exactly what the module host does). Shared with publish-wavee-aot.ps1 so the two layouts
# cannot drift. See docs/guide/playback-modules.md.
$modulePublish = @{ OutDir = $pubDir; Rid = $rid; Configuration = $Configuration }
if (-not $useAot) { $modulePublish['NoAot'] = $true }
& (Join-Path $PSScriptRoot 'publish-wavee-modules.ps1') @modulePublish

# Third-party notices next to Wavee.exe (Settings > About reads it from AppContext.BaseDirectory). Generated AFTER the
# modules so a module package reference is in scope; staged before the recursive layout copy below so it ships.
& (Join-Path $PSScriptRoot 'generate-third-party-notices.ps1') -OutFile (Join-Path $pubDir 'THIRD-PARTY-NOTICES.txt')

# ---------------------------------------------------------------------------------------------------------------
# 2. stage the package layout
# ---------------------------------------------------------------------------------------------------------------
Step "Staging package layout"
New-Item -ItemType Directory -Force -Path $layout | Out-Null
Copy-Item "$pubDir\*" $layout -Recurse -Force
Get-ChildItem $layout -Recurse -Include *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
if (-not (Test-Path $iconSource)) { throw "missing Wavee icon source: $iconSource" }
Add-Type -AssemblyName System.Drawing
function New-WaveeLogo([int]$w, [int]$h, [string]$path) {
  $src = [System.Drawing.Image]::FromFile($script:iconSource)
  try {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    # The source carries REAL alpha in its rounded corners (srcpps\Waveessets\AppIconppicon-source.png is
    # un-matted; the corners are transparent, not white). Keep them transparent: a solid clear here used to be the
    # navy behind a WHITE-cornered source, and the taskbar showed those white corners. The .ico is the one surface
    # that wants a full-bleed square (generate-appicon.ps1 fills the corners itself).
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [Math]::Min($w, $h)
    $dx = [int](($w - $side) / 2)
    $dy = [int](($h - $side) / 2)
    $g.DrawImage($src, $dx, $dy, $side, $side)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
  }
  finally { $src.Dispose() }
}
New-WaveeLogo 50 50 (Join-Path $assets 'StoreLogo.png')
New-WaveeLogo 44 44 (Join-Path $assets 'Square44x44Logo.png')
New-WaveeLogo 71 71 (Join-Path $assets 'Square71x71Logo.png')
New-WaveeLogo 150 150 (Join-Path $assets 'Square150x150Logo.png')
New-WaveeLogo 310 310 (Join-Path $assets 'Square310x310Logo.png')
New-WaveeLogo 310 150 (Join-Path $assets 'Wide310x150Logo.png')
# The taskbar / Start / Alt+Tab icon. Without targetsize-*_altform-unplated assets Windows draws Square44x44Logo on a
# PLATE (the manifest BackgroundColor, or the accent when that is "transparent") - which is exactly the blue square
# behind the W that showed up the moment the corners became transparent. The targetsize set is the plated form for
# surfaces that want one; the two altforms are what the taskbar and dark/light Start actually pick.
foreach ($ts in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
  New-WaveeLogo $ts $ts (Join-Path $assets ("Square44x44Logo.targetsize-{0}.png" -f $ts))
  New-WaveeLogo $ts $ts (Join-Path $assets ("Square44x44Logo.targetsize-{0}_altform-unplated.png" -f $ts))
  New-WaveeLogo $ts $ts (Join-Path $assets ("Square44x44Logo.targetsize-{0}_altform-lightunplated.png" -f $ts))
}

# Release notes for THIS version, embedded so the What's new page and the after-update dialog render offline.
if ($NotesDir) {
  if (-not (Test-Path $NotesDir)) { throw "-NotesDir not found: $NotesDir" }
  Step "Embedding release notes from $NotesDir"
  $notesDst = Join-Path $layout 'Assets\whatsnew'
  New-Item -ItemType Directory -Force -Path $notesDst | Out-Null
  Copy-Item (Join-Path $NotesDir '*') $notesDst -Recurse -Force
}

# ReadAllText with an explicit UTF-8 decoder, NOT Get-Content -Raw: under Windows PowerShell 5.1 Get-Content
# defaults to the ANSI codepage, which turns the em dash in the manifest Description into mojibake that then
# ships as the package's store description.
$mf = [IO.File]::ReadAllText($manifestTemplate, [Text.Encoding]::UTF8).
        Replace('__PUBLISHER__', $Publisher).
        Replace('__VERSION__', $Quad).
        Replace('__ARCH__', $Arch).
        Replace('__IDENTITY__', $IdentityName).
        Replace('__DISPLAY__', $DisplayName).
        Replace('__PROTOCOL__', $Protocol)
# Checked BEFORE the write, like New-WaveeAppInstaller: a manifest carrying a live __TOKEN__ packs into an MSIX
# Windows will happily install under the wrong identity.
$leftover = [regex]::Matches($mf, '__[A-Z0-9_]+__')
if ($leftover.Count -gt 0) {
  $names = (($leftover | ForEach-Object { $_.Value }) | Sort-Object -Unique) -join ', '
  throw "AppxManifest template has unsubstituted placeholders ($names): $manifestTemplate"
}
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $mf, $utf8NoBom)

# ---------------------------------------------------------------------------------------------------------------
# 3. resources + pack
# ---------------------------------------------------------------------------------------------------------------
Step "Generating resources.pri"
# Every native tool goes through Invoke-Native. A bare `& $exe ... 2>&1` under $ErrorActionPreference = Stop
# turns any line the tool writes to stderr into a terminating NativeCommandError, so a package that built fine
# fails the script on a warning; Invoke-Native captures both streams and judges only the exit code.
$priConfig = Join-Path $work 'priconfig.xml'
Invoke-Native $makepri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/pv', '10.0.0', '/o') | Out-Null
Push-Location $layout
try { Invoke-Native $makepri @('new', '/pr', $layout, '/cf', $priConfig, '/of', (Join-Path $layout 'resources.pri'), '/o') | Out-Null }
finally { Pop-Location }

Step "Packing $outMsix"
Remove-Item $outMsix -Force -ErrorAction SilentlyContinue
$pack = Invoke-Native $makeappx @('pack', '/o', '/d', $layout, '/p', $outMsix) -AllowFailure
if ($pack.ExitCode -ne 0) { throw ("makeappx pack failed ($($pack.ExitCode)):`n" + ($pack.Output -join "`n")) }

# ---------------------------------------------------------------------------------------------------------------
# 4. verify what was actually produced (a cross-compile that quietly fell back to the host arch installs, then
#    fails to launch; a manifest substitution miss ships a package Windows will not treat as an update)
# ---------------------------------------------------------------------------------------------------------------
Step "Verifying payload architecture + package identity"
Get-ChildItem $layout -Recurse -Include *.exe,*.dll | ForEach-Object {
  $machine = Get-PeMachine $_.FullName
  # 0x014C (I386) is what a managed IL-only AnyCPU assembly reports, and a -NoAot self-contained layout is full of
  # them; only a NATIVE image can be built for the wrong machine, so those are what this sweep judges.
  if ($null -ne $machine -and $machine -ne 0x014C -and -not (Test-PeMachine $_.FullName $Arch)) {
    throw "wrong machine type for $Arch : $($_.FullName)"
  }
}
$id = Get-MsixIdentity $outMsix
if ($id.Version -ne $Quad -or $id.ProcessorArchitecture -ne $Arch -or $id.Name -ne $IdentityName) {
  throw "package identity mismatch: $($id | ConvertTo-Json -Compress)"
}
# Publisher is part of the identity Windows compares on update, and it must equal the signing certificate
# subject or signtool fails later with 0x8007000B - catch it here, before a whole signing round trip.
if ($id.Publisher -ne $Publisher) {
  throw "publisher mismatch: '$($id.Publisher)' != '$Publisher' (signing would fail with 0x8007000B)"
}
Copy-Item (Join-Path $pubDir 'THIRD-PARTY-NOTICES.txt') $outRoot -Force

# ---------------------------------------------------------------------------------------------------------------
# 5. sign
# ---------------------------------------------------------------------------------------------------------------
if (-not $NoSign) {
  if ($TrustedSigning) {
    Step "Signing with Azure Trusted Signing"
    Invoke-TrustedSigning -Path @($outMsix) -Metadata $Metadata -Subscription $Subscription -SignTool $signtool
    Write-Host "    signed via Azure Trusted Signing (publicly trusted - no cert import needed)" -ForegroundColor Green
    if ($Install) { Add-AppxPackage -Path $outMsix }
  }
  else {
    Step "Signing with a self-signed cert ($Publisher)"
    Invoke-DevCertSigning -Path @($outMsix) -Publisher $Publisher -FriendlyName 'Wavee Dev Signing' -SignTool $signtool | Out-Null
    if ($Install) {
      try { Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null }
      catch { Write-Warning "Could not trust the cert (run elevated). Sideload may prompt." }
      Add-AppxPackage -Path $outMsix
    }
  }
}

$size = [Math]::Round((Get-Item $outMsix).Length / 1MB, 1)
Step "Done"
Write-Host "    $outMsix  (${size} MB, $Arch, $Quad$(if ($useAot) { ', AOT' } else { ', JIT' })$(if ($NoSign) { ', UNSIGNED' }))" -ForegroundColor Green
Write-Host "    identity $($id.Name)  informational $infoVersion  channel $Channel  feed $FeedRelease"
Write-Host "    symbols: $symZip  (folder $symDir; keep it - a crash report from this package resolves against nothing else)"
if ($UpdateBaseUrl -ne $defaultUpdateBaseUrl) { Write-Host "    base $UpdateBaseUrl" -ForegroundColor Yellow }
if (Test-Path $cerPath) {
  Write-Host "    cert: $cerPath"
  Write-Host "    On another machine (elevated once):"
  Write-Host "      Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
  Write-Host "      Add-AppxPackage -Path '$outMsix'"
}
