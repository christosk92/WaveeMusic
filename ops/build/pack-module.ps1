<#
.SYNOPSIS
  Pack one Wavee playback module directory into a distributable zip + sha256 sidecar.

.DESCRIPTION
  A module is distributed as a zip of its DIRECTORY CONTENTS (wavee-module.json at the zip root, next to the exe
  and any data files) plus a "<zip>.sha256" sidecar. The installer's hard gate is that sha256 over the
  decompressed bytes, so the sidecar travels with the zip. Used by this repo's bundled modules and by the
  playplay repo's Spotify module.

  Also prints the feed entry (id / version / protocolVersion / arch / sha256 / size) ready to paste into a
  modules.json - see docs/guide/playback-modules.md section 9.

.EXAMPLE
  powershell -File ops\build\pack-module.ps1 -ModuleDir src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\modules\wavee.youtube -OutputDir artifacts\modules -Arch arm64

.NOTES
  ASCII only inside string literals: this file has no BOM, so Windows PowerShell 5.1 decodes it as ANSI and a
  non-ASCII character in a QUOTED STRING is a parse error that kills the whole script.
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  # The module directory to pack; must contain wavee-module.json.
  [Parameter(Mandatory = $true)][string]$ModuleDir,
  # Where the .zip and .zip.sha256 are written (created if absent).
  [Parameter(Mandatory = $true)][string]$OutputDir,
  # Optional architecture tag folded into the file name and the feed entry.
  [ValidateSet('arm64', 'x64', '')][string]$Arch = ''
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ModuleDir)) { throw "Module directory not found: $ModuleDir" }
$ModuleDir = (Resolve-Path $ModuleDir).Path

$manifestPath = Join-Path $ModuleDir 'wavee-module.json'
if (-not (Test-Path $manifestPath)) { throw "wavee-module.json missing from $ModuleDir - that is not a module directory." }
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($field in 'id', 'version', 'protocolVersion', 'entry') {
  if ($manifest.PSObject.Properties.Name -notcontains $field) { throw "wavee-module.json has no '$field'." }
}

$entryPath = Join-Path $ModuleDir $manifest.entry
if (-not (Test-Path $entryPath)) { throw "Manifest entry '$($manifest.entry)' does not exist in $ModuleDir." }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$stamp = if ($Arch) { "$($manifest.id)-$($manifest.version)-$Arch" } else { "$($manifest.id)-$($manifest.version)" }
$zip = Join-Path $OutputDir "$stamp.zip"

Write-Host "==> Packing $($manifest.id) $($manifest.version) from $ModuleDir" -ForegroundColor Cyan
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Contents, not the folder: the manifest must sit at the zip root so an installer can unpack straight into
# %LOCALAPPDATA%\Wavee\modules\<id>\<version>\.
Compress-Archive -Path (Join-Path $ModuleDir '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
Set-Content -Path "$zip.sha256" -Value "$hash  $stamp.zip" -Encoding ascii -NoNewline

Write-Host "Done: $zip" -ForegroundColor Green
Write-Host "      $([math]::Round($size / 1MB, 2)) MB  sha256 $hash"
Write-Host ""
Write-Host "modules.json entry:" -ForegroundColor DarkGray
$entry = [ordered]@{
  id              = $manifest.id
  version         = $manifest.version
  protocolVersion = $manifest.protocolVersion
  arch            = $Arch
  urls            = @("https://REPLACE-ME/$stamp.zip")
  sha256          = $hash
  size            = $size
  compression     = 'none'
  publisher       = $manifest.publisher
}
$entry | ConvertTo-Json -Depth 4
