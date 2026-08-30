<#
.SYNOPSIS
  Publish Wavee's bundled playback modules into an app publish directory.

.DESCRIPTION
  A playback module is an out-of-process exe that speaks the Wavee.Sdk JSON-RPC protocol over stdio. The app
  discovers them at "<app dir>\modules\<id>\wavee-module.json", so each module is published self-contained into
  "$OutDir\modules\<id>\". This script is the ONE place that layout is defined: both publish-wavee-aot.ps1 and
  pack-wavee-msix.ps1 call it, so the loose publish and the MSIX package cannot drift.

  A module whose csproj is absent is skipped with a warning (the app still builds and runs, it just cannot play
  that source). A module that publishes without a wavee-module.json is a hard error - the host would never load it.

.EXAMPLE
  powershell -File ops\build\publish-wavee-modules.ps1 -OutDir C:\path\to\publish -Rid win-arm64

.NOTES
  ASCII only inside string literals: this file has no BOM, so Windows PowerShell 5.1 decodes it as ANSI and a
  non-ASCII character in a QUOTED STRING is a parse error that kills the whole script.
  See docs/guide/playback-modules.md for the protocol, the manifest and the dev-vs-publish entry rule.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  # The app's publish directory. Modules land in "$OutDir\modules\<id>\".
  [Parameter(Mandatory = $true)][string]$OutDir,
  [Parameter(Mandatory = $true)][ValidatePattern('^win-(arm64|x64)$')][string]$Rid,
  [string]$Configuration = 'Release',
  # Publish self-contained JIT instead of NativeAOT (mirrors pack-wavee-msix.ps1 -NoAot).
  [switch]$NoAot
)
$ErrorActionPreference = 'Stop'

# Script lives at ops/build/ - repo root is two levels up.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# id -> project. The id is the manifest's "id" and owns both the modules\<id>\ folder and the
# wavee:module:<id>: uri namespace; keep this table in sync with each module's wavee-module.json.
$modules = @(
  @{ Id = 'wavee.youtube'; Project = 'src\apps\modules\Wavee.Module.YouTube\Wavee.Module.YouTube.csproj' },
  @{ Id = 'wavee.twitch';  Project = 'src\apps\modules\Wavee.Module.Twitch\Wavee.Module.Twitch.csproj' },
  @{ Id = 'wavee.radio';   Project = 'src\apps\modules\Wavee.Module.Radio\Wavee.Module.Radio.csproj' }
)

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

$published = 0
foreach ($m in $modules) {
  $csproj = Join-Path $root $m.Project
  if (-not (Test-Path $csproj)) {
    Write-Host "    skipping $($m.Id): $($m.Project) not present" -ForegroundColor DarkYellow
    continue
  }

  $dest = Join-Path $OutDir "modules\$($m.Id)"
  Step "Publishing module $($m.Id) ($Rid, $Configuration, $(if ($NoAot) { 'self-contained JIT' } else { 'NativeAOT' }))"
  $pubArgs = @($csproj, '-c', $Configuration, '-r', $Rid, '-o', $dest, '--nologo', '-v', 'm', '/p:NuGetAudit=false')
  if ($NoAot) { $pubArgs += @('-p:PublishAot=false', '--self-contained', 'true') }
  & dotnet publish @pubArgs
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($m.Id) ($LASTEXITCODE)." }

  $manifest = Join-Path $dest 'wavee-module.json'
  if (-not (Test-Path $manifest)) { throw "wavee-module.json missing from $dest - the host would never load $($m.Id)." }
  $published++
}

if ($published -eq 0) {
  Write-Host "No playback modules were published (none of the module projects exist yet)." -ForegroundColor Yellow
} else {
  Write-Host "Modules published: $published -> $OutDir\modules\" -ForegroundColor Green
}
