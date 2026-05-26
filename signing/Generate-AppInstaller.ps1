#requires -Version 7.0
<#
.SYNOPSIS
    Generates Wavee.appinstaller from the template, substituting the release
    version + tag.

.DESCRIPTION
    Reads signing/Wavee.appinstaller.template, replaces ${VERSION} (4-part
    numeric, e.g. 0.1.0.0) and ${TAG} (the GitHub tag without 'v', e.g.
    0.1.0-beta), and writes the resulting Wavee.appinstaller to the
    OutputDirectory.

    Designed to be called from the release pipeline after MSIX signing.

.PARAMETER Tag
    The git tag, with or without the leading 'v' (e.g. 'v0.1.0-beta' or
    '0.1.0-beta'). The 4-part numeric Version is derived from this.

.PARAMETER OutputDirectory
    Where to write the resulting Wavee.appinstaller. Created if missing.

.EXAMPLE
    pwsh signing/Generate-AppInstaller.ps1 -Tag v0.1.0-beta -OutputDirectory ./artifacts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$templatePath = Join-Path $PSScriptRoot 'Wavee.appinstaller.template'
if (-not (Test-Path $templatePath)) {
    throw "Template not found at $templatePath"
}

# Normalize tag — strip leading 'v', drop any '-prerelease' suffix when
# deriving the 4-part numeric Version (MSIX manifests reject non-numeric).
$cleanTag = $Tag.TrimStart('v','V')
$numericTag = ($cleanTag -split '-')[0]   # e.g. 0.1.0-beta -> 0.1.0

# Pad to 4 parts for MSIX-compatible Version: 0.1.0 -> 0.1.0.0
$parts = $numericTag.Split('.')
while ($parts.Count -lt 4) { $parts += '0' }
$version = ($parts[0..3] -join '.')

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$content = Get-Content -Raw -Path $templatePath
$content = $content.Replace('${VERSION}', $version)
$content = $content.Replace('${TAG}', $cleanTag)

$outputPath = Join-Path $OutputDirectory 'Wavee.appinstaller'
Set-Content -Path $outputPath -Value $content -Encoding utf8

Write-Host "Generated $outputPath (Version=$version, Tag=$cleanTag)"
