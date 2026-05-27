#requires -Version 7.0
<#
.SYNOPSIS
  Stamps the WinUI project and MSIX manifest for a release tag.

.DESCRIPTION
  MSIX requires a four-part numeric package version, while GitHub releases
  use SemVer tags such as v0.1.0-alpha.1. This script keeps both in sync:

    v0.1.0-alpha.1 -> Version/InformationalVersion 0.1.0-alpha.1
                      Package.appxmanifest 0.1.0.1001

  The fourth MSIX part uses prerelease bands so stable releases with the same
  major.minor.patch can update forward:
    alpha.N = 1000 + N, beta.N = 2000 + N, rc.N = 3000 + N, stable = 10000.

.PARAMETER Tag
  Git tag to stamp, with or without leading v.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Tag,

  [string]$ProjectPath = (Join-Path $PSScriptRoot '..\src\Wavee.UI.WinUI\Wavee.UI.WinUI.csproj'),
  [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\Wavee.UI.WinUI\Package.appxmanifest')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Versioning.ps1')

$version = Set-WaveeVersion -Tag $Tag -ProjectPath $ProjectPath -ManifestPath $ManifestPath

Write-Host "Stamped Wavee version:"
Write-Host "  tag/version : $($version.InformationalVersion)"
Write-Host "  msix        : $($version.PackageVersion)"
