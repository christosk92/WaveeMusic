#requires -Version 7.0
<#
.SYNOPSIS
    Generates Wavee.appinstaller from the template, substituting the release
    version + tag.

.DESCRIPTION
    Reads signing/Wavee.appinstaller.template, replaces ${VERSION} (4-part
    numeric, e.g. 0.1.0.1001), ${TAG} (the GitHub tag without 'v', e.g.
    0.1.0-alpha.1), and ${APPINSTALLER_URI}, then writes the resulting
    Wavee.appinstaller to the OutputDirectory.

    Designed to be called from the release pipeline after MSIX signing.

.PARAMETER Tag
    The git tag, with or without the leading 'v' (e.g. 'v0.1.0-alpha.1' or
    '0.1.0-alpha.1'). The 4-part numeric Version is derived from this.

.PARAMETER OutputDirectory
    Where to write the resulting Wavee.appinstaller. Created if missing.

.EXAMPLE
    pwsh signing/Generate-AppInstaller.ps1 -Tag v0.1.0-alpha.1 -OutputDirectory ./artifacts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Versioning.ps1')

$templatePath = Join-Path $PSScriptRoot 'Wavee.appinstaller.template'
if (-not (Test-Path $templatePath)) {
    throw "Template not found at $templatePath"
}

$releaseVersion = ConvertTo-WaveePackageVersion -Tag $Tag
$cleanTag = $releaseVersion.CleanTag
$version = $releaseVersion.PackageVersion
$appInstallerUri = if ($cleanTag.Contains('-')) {
    "https://github.com/christosk92/WaveeMusic/releases/download/v$cleanTag/Wavee.appinstaller"
} else {
    "https://github.com/christosk92/WaveeMusic/releases/latest/download/Wavee.appinstaller"
}

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$content = Get-Content -Raw -Path $templatePath
$content = $content.Replace('${VERSION}', $version)
$content = $content.Replace('${TAG}', $cleanTag)
$content = $content.Replace('${APPINSTALLER_URI}', $appInstallerUri)

$outputPath = Join-Path $OutputDirectory 'Wavee.appinstaller'
Set-Content -Path $outputPath -Value $content -Encoding utf8

Write-Host "Generated $outputPath (Version=$version, Tag=$cleanTag)"
