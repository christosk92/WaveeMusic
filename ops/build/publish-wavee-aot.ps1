<#
.SYNOPSIS
  Publish Wavee as a NativeAOT single-file native exe.

.EXAMPLE
  ops\build\publish-wavee-aot.cmd
  pwsh ops/build/publish-wavee-aot.ps1
  pwsh ops/build/publish-wavee-aot.ps1 -Arch x64
  pwsh ops/build/publish-wavee-aot.ps1 -Diag     # diagnostics build (ScrollTrace + RenderBudget + FG_OPAQUE_WINDOW armed)
  pwsh ops/build/publish-wavee-aot.ps1 -CrashService
                                                 # crash-reporting verify build (#165): stamped for crash.cproducts.dev,
                                                 # quad 0.0.1.0, symbols, stamp proven, Wavee.symmap uploaded to R2
  pwsh ops/build/publish-wavee-aot.ps1 -Channel stable -Quad 0.2.0.1
                                                 # update-checker verify build: polls the real wavee-stable feed and
                                                 # finds the live release newer than 0.2.0.1
  pwsh ops/build/publish-wavee-aot.ps1 -Channel stable -Quad 0.2.0.1 -UpdateBaseUrl http://127.0.0.1:8099/ -FeedRelease wavee-local
                                                 # the same, against a loopback feed (ops\release\tests\LocalFeedServer.psm1)

.NOTES
  Crash service (-CrashService). The ingest key is resolved -CrashIngestKey, then INGEST_KEY in
  ops\crash\worker\.prod.vars (gitignored), then 1Password (Resolve-CrashIngestKey); it is never printed. A
  stamped build always carries a quad (the crash service refuses a report without one) and symbols, and the map
  is uploaded as symbols/<quad>/win-<arch>.symmap: the Worker resolves frames AT INGEST, so a report sent before the
  map exists stays <unresolved> forever. Uploads are limited to 0.0.x.y verify quads, because a real release's map is
  owned by wavee-release.ps1's symbols phase and a mismatched upload would break every report from that release.
  -NoSymbolUpload builds the map and keeps it local.

  Update checker (-Channel/-UpdateBaseUrl/-FeedRelease). A loose exe is unpackaged: the checker polls the
  feed exactly like a packaged build and shows "update available", but "Update now" opens the release page
  (NullPackageUpdater). Only an MSIX installs an update: for that whole path use
  ops\release\tests\local-update-e2e.ps1 (-Scenario inapp / os, elevated). A -Channel stable/beta build needs a
  quad (no quad = IsDev = no checker) and writes the real %LOCALAPPDATA%\Wavee like any unpackaged run.

  -Diag passes /p:FluentGpuDiag=true, which this repo's (single) root Directory.Build.props turns into the
  FLUENTGPU_DIAG define for the Wavee app csproj - the CALLING assembly for the engine's [Conditional("FLUENTGPU_DIAG")]
  diagnostics (ScrollTrace, RenderBudget, FG_OPAQUE_WINDOW), so this is what actually makes those call sites live even
  though the engine itself is built from its own sibling checkout unchanged. It is a DIFFERENT BINARY from the shipping
  one: BindContract and BackwardsWriteGuard become default-ON once compiled in, so a feel-measurement session must
  clear them explicitly (FG_BIND_CONTRACT=0 FG_BACKWARDS_WRITE=0) before launching the diag exe.
  See docs/plans/wavee-scroll-feel-diagnostics-plan.md for what each diag facility does and how it is gated.
#>
[CmdletBinding()]
param(
  # Machine architecture from the ENVIRONMENT. RuntimeInformation.OSArchitecture is unreliable here: under Windows
  # PowerShell 5.1 (.NET Framework) an x64-emulated host on an ARM64 machine reports X64 for the OS, so publishing
  # from an emulated shell would quietly produce a win-x64 build on an ARM64 box. PROCESSOR_ARCHITEW6432 exists only
  # inside an emulated/WOW process and always names the REAL machine, so it wins when present.
  [ValidateSet('arm64', 'x64')]
  [string]$Arch = $(
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    if ("$a" -match 'ARM64') { 'arm64' } else { 'x64' }),
  [string]$Configuration = 'Release',
  [switch]$Symbols,
  [switch]$Diag,
  # Compile for THIS machine's CPU (ILC --instruction-set native): every AdvSimd/dotprod/LSE extension the box has is
  # used, at the price of a binary that only runs on CPUs with the same features. For a local install, never for the
  # store/feed publish.
  [switch]$Fast,
  # Build the public-only variant (no PlayPlay sources), the same switch pack-wavee-msix.ps1 takes.
  [switch]$PublicOnly,
  # Crash-reporting verify builds (#165). -Quad stamps WaveePackageVersion (a report without a quad is refused by the
  # crash service); -CrashIngestUrl/-CrashIngestKey stamp the ingest endpoint and key exactly like
  # pack-wavee-msix.ps1 does, and come as a pair. The key is never printed. -OutDir replaces the default output
  # folder (repo-relative unless rooted). All four default to empty = the unchanged loose dev publish.
  [string]$Quad = '',
  [string]$CrashIngestUrl = '',
  [string]$CrashIngestKey = '',
  [string]$OutDir = '',
  # The one-switch crash verify build (see .NOTES): stamps https://crash.cproducts.dev (unless -CrashIngestUrl) with
  # the resolved key, implies -Symbols, defaults -Quad to 0.0.1.0 and -OutDir to artifacts\crash-verify\win-<arch>.
  [switch]$CrashService,
  # Build Wavee.symmap for a crash-stamped build but do not upload it.
  [switch]$NoSymbolUpload,
  # Update-checker verify builds (see .NOTES). 'dev' (the default) never checks for updates; stable/beta need -Quad.
  [ValidateSet('dev', 'stable', 'beta')]
  [string]$Channel = 'dev',
  # Feed location stamped like pack-wavee-msix.ps1's: empty = the csproj defaults (GitHub, wavee-stable).
  [string]$UpdateBaseUrl = '',
  [string]$FeedRelease = ''
)
$ErrorActionPreference = 'Stop'

# Script lives at ops/build/ - repo root is two levels up.
$root   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $PSScriptRoot 'Wavee.Build.psm1') -Force -DisableNameChecking
$csproj = Join-Path $root 'src\apps\Wavee\Wavee.csproj'
$rid    = "win-$Arch"

$Quad = "$Quad".Trim()
$CrashIngestUrl = "$CrashIngestUrl".Trim()
$CrashIngestKey = "$CrashIngestKey".Trim()
$UpdateBaseUrl = "$UpdateBaseUrl".Trim()
$FeedRelease = "$FeedRelease".Trim()
if ($CrashService) {
  if (-not $CrashIngestUrl) { $CrashIngestUrl = 'https://crash.cproducts.dev' }
  if (-not $Quad) { $Quad = '0.0.1.0' }
  if (-not $OutDir) { $OutDir = "artifacts\crash-verify\win-$Arch" }
  if (-not $CrashIngestKey) {
    $vars = Join-Path $root 'ops\crash\worker\.prod.vars'
    if (Test-Path -LiteralPath $vars) {
      $line = @(Get-Content -LiteralPath $vars | Where-Object { $_ -like 'INGEST_KEY=*' }) | Select-Object -First 1
      if ($line) { $CrashIngestKey = ($line -replace '^INGEST_KEY=', '').Trim() }
    }
  }
}
if ($CrashIngestUrl) {
  # Resolve-CrashIngestKey (1Password), Assert-CrashIngestStamp, Publish-WaveeSymbolMap, Get-WranglerPath.
  Import-Module (Join-Path $root 'ops\release\Wavee.Release.psm1') -Force -DisableNameChecking
  if (-not $CrashIngestKey -and $CrashService) {
    $resolved = Resolve-CrashIngestKey
    $CrashIngestKey = $resolved.Key
    Write-Host "crash ingest key: $($resolved.Source)"
  }
}
if ([bool]$CrashIngestUrl -and -not $Quad) {
  throw '-CrashIngestUrl needs -Quad: the crash service refuses a report from a build without a quad.'
}
if ($Channel -ne 'dev' -and -not $Quad) {
  throw "-Channel $Channel needs -Quad: a build without a quad is a dev build and never checks for updates."
}
if ($UpdateBaseUrl) {
  $baseUri = $UpdateBaseUrl -as [Uri]
  if ($null -eq $baseUri -or -not $baseUri.IsAbsoluteUri -or ($baseUri.Scheme -ne 'http' -and $baseUri.Scheme -ne 'https')) {
    throw "-UpdateBaseUrl must be an absolute http(s) URL; got '$UpdateBaseUrl'."
  }
  if ($baseUri.Scheme -eq 'http' -and -not $baseUri.IsLoopback) {
    throw "-UpdateBaseUrl may only use plain http for a loopback host; got '$UpdateBaseUrl'."
  }
  if (-not $UpdateBaseUrl.EndsWith('/')) { $UpdateBaseUrl = $UpdateBaseUrl + '/' }
}
if ($Quad) {
  if ($Quad -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "-Quad must be 4 numeric parts (e.g. 0.0.1.0); got '$Quad'." }
  foreach ($part in $Quad.Split('.')) { if ([int]$part -gt 65535) { throw "-Quad part > 65535: $Quad" } }
}
if ([bool]$CrashIngestUrl -ne [bool]$CrashIngestKey) {
  throw '-CrashIngestUrl and -CrashIngestKey come as a pair: an exe stamped with only one of them can never send a report.'
}
$wrangler = ''
if ($CrashIngestUrl) {
  # A crash-stamped build always gets the symbols its map is built from.
  $Symbols = $true
  if (-not $NoSymbolUpload) {
    if ($Quad -notmatch '^0\.0\.\d+\.\d+$') {
      throw "Symbol upload is limited to 0.0.x.y verify quads (got $Quad): a real release's map belongs to wavee-release.ps1. Pass -NoSymbolUpload."
    }
    # Fail before a multi-minute AOT publish, not after it.
    $wrangler = Get-WranglerPath -RepoRoot $root
  }
}

# -OutDir is resolved in place: $OutDir and $outDir are ONE variable (PowerShell names are case-insensitive).
$outDir = if ($OutDir) {
  if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
} elseif ($Symbols) {
  Join-Path $root "src\apps\Wavee\bin\publish-aot-symbols"
} elseif ($Diag) {
  # Its own tree: a diag exe must never silently replace the shipping publish an operator then measures as "Release".
  Join-Path $root "src\apps\Wavee\bin\publish-aot-diag\$rid"
} else {
  Join-Path $root "src\apps\Wavee\bin\$Configuration\net10.0\$rid\publish"
}
$exe    = Join-Path $outDir 'Wavee.exe'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ILC's findvcvarsall.bat misses an installed ARM64 link.exe when MSBuild pipes stdout; load vcvars ourselves.
Import-MsvcEnvironment -Arch $Arch

# Keep MSBuild/VBCSCompiler temp under the repo (short path, no roaming-profile locks).
$tmp = Join-Path $root '.tmp-msbuild'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TEMP = $tmp
$env:TMP  = $tmp

# Version identity from src/apps/Wavee/Wavee.Version.props. This is a LOOSE publish, never a release: by default the
# channel is 'dev' and InformationalVersion keeps the '-dev' suffix, so About / the crash header / the update checker
# all report a development build and IsDev suppresses any update comparison. -Channel stable/beta (with -Quad) stamps
# pack-wavee-msix.ps1's "<semver>+build.<N>.sha.<sha7>" instead, so the checker runs. The commit and build date are
# always stamped for real, so a hand-shared exe can still be traced back to a tree.
$props = Get-WaveeVersionProps (Join-Path $root 'src\apps\Wavee\Wavee.Version.props')
$commit = ''
$g = Invoke-Native 'git' @('-C', $root, 'rev-parse', '--short=7', 'HEAD') -AllowFailure
if ($g.ExitCode -eq 0 -and $g.Output.Count -gt 0) { $commit = "$($g.Output[0])".Trim() }
$buildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$infoVersion = "$($props.Version)-dev"
if ($Channel -ne 'dev') {
  $infoVersion = "$($props.Version)+build.$($Quad.Split('.')[3])"
  if ($commit) { $infoVersion = "$infoVersion.sha.$commit" }
}

Step "Publishing Wavee NativeAOT ($rid, $Configuration, OptimizationPreference=Speed$(if ($Fast) { ', IlcInstructionSet=native' })$(if ($Symbols) { ', NativeDebugSymbols' })$(if ($Diag) { ', FLUENTGPU_DIAG' })$(if ($PublicOnly) { ', public-only' })$(if ($Quad) { ", quad $Quad" })$(if ($CrashIngestUrl) { ", crash ingest $CrashIngestUrl" })$(if ($Channel -ne 'dev') { ", channel $Channel" })$(if ($UpdateBaseUrl) { ", feed base $UpdateBaseUrl" })$(if ($FeedRelease) { ", feed $FeedRelease" }))"
$pubArgs = @(
  $csproj, '-c', $Configuration, '-r', $rid,
  '/p:NuGetAudit=false', '/p:OptimizationPreference=Speed',
  "/p:InformationalVersion=$infoVersion",
  "/p:WaveeChannel=$Channel",
  "/p:WaveeCommit=$commit",
  "/p:WaveeBuildDate=$buildDate",
  '/p:IlcUseEnvironmentalTools=true',
  '-o', $outDir, '--nologo'
)
if ($Symbols) {
  $pubArgs += '/p:NativeDebugSymbols=true', '/p:DebugType=portable', '/p:IlcGenerateMapFile=true'
}
if ($Diag) {
  $pubArgs += '/p:FluentGpuDiag=true'
}
if ($Fast) {
  $pubArgs += '/p:IlcInstructionSet=native'
}
if ($PublicOnly) {
  $pubArgs += '-p:WaveeSkipPrivateSources=true'
}
if ($Quad) {
  $pubArgs += "/p:WaveePackageVersion=$Quad"
}
if ($CrashIngestUrl) {
  $pubArgs += "/p:WaveeCrashIngestUrl=$CrashIngestUrl", "/p:WaveeCrashIngestKey=$CrashIngestKey"
}
if ($UpdateBaseUrl) {
  $pubArgs += "/p:WaveeUpdateBaseUrl=$UpdateBaseUrl"
}
if ($FeedRelease) {
  $pubArgs += "/p:WaveeFeedRelease=$FeedRelease"
}
& dotnet publish @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path $exe)) { throw "Expected output not found: $exe" }

# Bundled playback modules: one self-contained exe per module under $outDir\modules\<id>\, next to its
# wavee-module.json (whose entry is that .exe). The app discovers them there; a dev build instead gets the
# framework-dependent copy staged by Wavee.csproj's CopyBundledModules target. The same helper is called by
# pack-wavee-msix.ps1, so the loose publish and the MSIX layout cannot drift. See docs/guide/playback-modules.md.
& (Join-Path $PSScriptRoot 'publish-wavee-modules.ps1') -OutDir $outDir -Rid $rid -Configuration $Configuration

# Third-party notices next to Wavee.exe: Settings > About reads THIRD-PARTY-NOTICES.txt from AppContext.BaseDirectory,
# so a loose publish gets the same file the MSIX layout does (pack-wavee-msix.ps1 makes the identical call).
# -EngineRoot resolved like the build itself (G-237): -Override, then this worktree's EngineRoot.local.props pin,
# then the sibling checkout - never blind to a pin the way a bare `..\fluent-gpu` default would be.
& (Join-Path $PSScriptRoot 'generate-third-party-notices.ps1') -OutFile (Join-Path $outDir 'THIRD-PARTY-NOTICES.txt') `
  -EngineRoot (Resolve-EngineRoot -RepoRoot $root -Override $env:EngineRoot)

$info = Get-Item $exe
Write-Host ""
# Read the version the binary actually carries (InformationalVersion -> ProductVersion). The csproj no longer holds a
# literal version at all - Wavee.Version.props does - so the fallback reads that instead of grepping the csproj.
$ver = $info.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($ver)) { $ver = "$($props.Version)-dev" }
$plus = $ver.IndexOf('+')
if ($plus -gt 0) { $ver = $ver.Substring(0, $plus) }
Write-Host "Done: $($info.FullName)" -ForegroundColor Green
Write-Host "      v$ver  $([math]::Round($info.Length / 1MB, 2)) MB"
if ($Diag) {
  # ASCII only, everywhere in this file: it has no BOM, so Windows PowerShell 5.1 decodes it as ANSI and a non-ASCII
  # character inside a QUOTED STRING is a parse error that kills the whole script. Comments survive it, but they are
  # kept ASCII too so a copy/paste out of one can never reintroduce the break.
  Write-Host "      FLUENTGPU_DIAG build - NOT the shipping binary. Clear FG_BIND_CONTRACT/FG_BACKWARDS_WRITE when measuring." -ForegroundColor Yellow
}
if ($CrashIngestUrl) {
  # Proof from the bytes that ship, then the map the Worker resolves this build's report RVAs against (at ingest).
  Write-Host "      $(Assert-CrashIngestStamp -ExePath $exe -Url $CrashIngestUrl -Key $CrashIngestKey)" -ForegroundColor Green
  $sym = Publish-WaveeSymbolMap -ExePath $exe -SymbolsDir $outDir -Quad $Quad -Arch $Arch `
    -ReleaseToolProject (Join-Path $root 'src\apps\Wavee.ReleaseTool\Wavee.ReleaseTool.csproj') `
    -Wrangler $wrangler -SkipUpload:$NoSymbolUpload -SkipReason '-NoSymbolUpload'
  if ($sym.Uploaded) {
    Write-Host "      symbol map uploaded: wavee-crash/$($sym.Key)" -ForegroundColor Green
  } else {
    Write-Host "      symbol map NOT uploaded ($($sym.Reason)): reports from this exe stay <unresolved>" -ForegroundColor Yellow
  }
}
if ($Symbols) {
  $pdb = Join-Path $outDir 'Wavee.pdb'
  if (Test-Path $pdb) {
    $pdbInfo = Get-Item $pdb
    Write-Host "      PDB: $($pdbInfo.FullName)  $([math]::Round($pdbInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "WinDbg: .sympath+ $outDir" -ForegroundColor DarkGray
  }
}
