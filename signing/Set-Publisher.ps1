<#
.SYNOPSIS
  Rewrites the Publisher attribute in Package.appxmanifest to match the
  Subject Name issued by an Azure Artifact Signing certificate profile.

.DESCRIPTION
  Local development uses a self-signed cert with Publisher="CN=ckara". For
  signed release builds, the manifest Publisher MUST EXACTLY match the
  Subject Name on the signing certificate or SignTool refuses with
  SignerSign() failed (-2147024885 / 0x8007000B "publisher name does not
  match certificate"). This script does the swap in-place so the source
  copy keeps the dev publisher.

.PARAMETER ManifestPath
  Path to Package.appxmanifest. Defaults to the WinUI app manifest.

.PARAMETER PublisherSubject
  The full Subject Name from the cert profile. Example:
    "CN=Christos Karapasias, O=Christos Karapasias, L=..., S=..., C=NL"

.EXAMPLE
  .\Set-Publisher.ps1 -PublisherSubject $env:RELEASE_PUBLISHER_SUBJECT

.NOTES
  Intended to run in CI just before signing, OR locally when producing a
  release build by hand. Do not commit the rewritten manifest — the dev
  Publisher must stay in source.
#>
[CmdletBinding()]
param(
  [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\Wavee.UI.WinUI\Package.appxmanifest'),
  [Parameter(Mandatory = $true)]
  [string]$PublisherSubject
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ManifestPath)) {
  throw "Manifest not found at $ManifestPath"
}

[xml]$manifest = Get-Content -Raw -Path $ManifestPath
$identity = $manifest.Package.Identity
if ($null -eq $identity) {
  throw "No <Identity> element found in $ManifestPath"
}

$old = $identity.Publisher
$identity.Publisher = $PublisherSubject

# Preserve UTF-8 with BOM (MSIX tooling is picky about manifest encoding).
$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.Encoding = New-Object System.Text.UTF8Encoding($true)
$writer = [System.Xml.XmlWriter]::Create($ManifestPath, $settings)
try {
  $manifest.Save($writer)
} finally {
  $writer.Close()
}

Write-Host "Publisher rewritten:"
Write-Host "  old: $old"
Write-Host "  new: $PublisherSubject"
