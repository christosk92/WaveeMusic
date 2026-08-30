<#
.SYNOPSIS
  Publish Wavee as a NativeAOT single-file native exe.

.EXAMPLE
  ops\build\publish-wavee-aot.cmd
  pwsh ops/build/publish-wavee-aot.ps1
  pwsh ops/build/publish-wavee-aot.ps1 -Arch x64
  pwsh ops/build/publish-wavee-aot.ps1 -Diag     # diagnostics build (ScrollTrace + RenderBudget + FG_OPAQUE_WINDOW armed)

.NOTES
  -Diag defines FLUENTGPU_DIAG solution-wide (src/Directory.Build.props + src/apps/Directory.Build.props). It is a
  DIFFERENT BINARY from the shipping one: BindContract and BackwardsWriteGuard become default-ON once compiled in, so a
  feel-measurement session must clear them explicitly (FG_BIND_CONTRACT=0 FG_BACKWARDS_WRITE=0) - ops/diag does this.
  See ops/diag/README.md.
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
  # Build the public-only variant (no PlayPlay sources), the same switch pack-wavee-msix.ps1 takes.
  [switch]$PublicOnly
)
$ErrorActionPreference = 'Stop'

# Script lives at ops/build/ - repo root is two levels up.
$root   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $PSScriptRoot 'Wavee.Build.psm1') -Force -DisableNameChecking
$csproj = Join-Path $root 'src\apps\Wavee\Wavee.csproj'
$rid    = "win-$Arch"
$outDir = if ($Symbols) {
  Join-Path $root "src\apps\Wavee\bin\publish-aot-symbols"
} elseif ($Diag) {
  # Its own tree: a diag exe must never silently replace the shipping publish an operator then measures as "Release".
  Join-Path $root "src\apps\Wavee\bin\publish-aot-diag\$rid"
} else {
  Join-Path $root "src\apps\Wavee\bin\$Configuration\net10.0\$rid\publish"
}
$exe    = Join-Path $outDir 'Wavee.exe'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ILC needs link.exe via vswhere on PATH.
$vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path "$vsInstaller\vswhere.exe") -and ($env:PATH -notlike "*$vsInstaller*")) {
  $env:PATH = "$vsInstaller;$env:PATH"
}

# Keep MSBuild/VBCSCompiler temp under the repo (short path, no roaming-profile locks).
$tmp = Join-Path $root '.tmp-msbuild'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TEMP = $tmp
$env:TMP  = $tmp

# Version identity from src/apps/Wavee/Wavee.Version.props. This is a LOOSE publish, never a release: the channel is
# pinned to 'dev' and InformationalVersion keeps the '-dev' suffix, so About / the crash header / the update checker
# all report a development build and AppVersion.IsDev suppresses any update comparison. Only the commit and the build
# date are stamped for real, so a hand-shared exe can still be traced back to a tree.
$props = Get-WaveeVersionProps (Join-Path $root 'src\apps\Wavee\Wavee.Version.props')
$commit = ''
$g = Invoke-Native 'git' @('-C', $root, 'rev-parse', '--short=7', 'HEAD') -AllowFailure
if ($g.ExitCode -eq 0 -and $g.Output.Count -gt 0) { $commit = "$($g.Output[0])".Trim() }
$buildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')

Step "Publishing Wavee NativeAOT ($rid, $Configuration, OptimizationPreference=Speed$(if ($Symbols) { ', NativeDebugSymbols' })$(if ($Diag) { ', FLUENTGPU_DIAG' })$(if ($PublicOnly) { ', public-only' }))"
$pubArgs = @(
  $csproj, '-c', $Configuration, '-r', $rid,
  '/p:NuGetAudit=false', '/p:OptimizationPreference=Speed',
  "/p:InformationalVersion=$($props.Version)-dev",
  '/p:WaveeChannel=dev',
  "/p:WaveeCommit=$commit",
  "/p:WaveeBuildDate=$buildDate",
  '-o', $outDir, '--nologo'
)
if ($Symbols) {
  $pubArgs += '/p:NativeDebugSymbols=true', '/p:DebugType=portable', '/p:IlcGenerateMapFile=true'
}
if ($Diag) {
  $pubArgs += '/p:FluentGpuDiag=true'
}
if ($PublicOnly) {
  $pubArgs += '-p:WaveeSkipPrivateSources=true'
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
& (Join-Path $PSScriptRoot 'generate-third-party-notices.ps1') -OutFile (Join-Path $outDir 'THIRD-PARTY-NOTICES.txt')

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
if ($Symbols) {
  $pdb = Join-Path $outDir 'Wavee.pdb'
  if (Test-Path $pdb) {
    $pdbInfo = Get-Item $pdb
    Write-Host "      PDB: $($pdbInfo.FullName)  $([math]::Round($pdbInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "WinDbg: .sympath+ $outDir" -ForegroundColor DarkGray
  }
}
