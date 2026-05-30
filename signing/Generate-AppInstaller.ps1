#requires -Version 7.0
<#
.SYNOPSIS
    Generates per-architecture Wavee .appinstaller files from the template.

.DESCRIPTION
    Emits ONE .appinstaller PER ARCHITECTURE (x64 + arm64) so that each arch's install
    auto-updates from its own arch's MSIX — the previous single x64-only file left ARM64
    installs unable to update.

    Reads signing/Wavee.appinstaller.template and substitutes ${VERSION} (4-part numeric),
    ${ARCH}, ${APPINSTALLER_URI} (the stable self-URL Windows polls), and ${MSIX_URI}.

    Stable channel polls the /latest alias; experimental polls a fixed `experimental-latest`
    rolling release whose assets the pipeline re-uploads each build (GitHub has no
    "latest pre-release" alias). Designed to be called from the release pipeline after signing.

.PARAMETER Tag
    The git tag, with or without the leading 'v' (e.g. 'v0.1.0-alpha.1'). The 4-part numeric
    version is derived from it via ConvertTo-WaveePackageVersion.

.PARAMETER OutputDirectory
    Where to write the resulting .appinstaller files. Created if missing.

.PARAMETER Channel
    'stable' -> Wavee.<arch>.appinstaller tracking the latest production release.
    'experimental' -> Wavee.Experimental.<arch>.appinstaller tracking the rolling pre-release.

.EXAMPLE
    pwsh signing/Generate-AppInstaller.ps1 -Tag v0.1.0-alpha.1 -Channel experimental -OutputDirectory ./artifacts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [ValidateSet('stable', 'experimental')] [string] $Channel = 'stable'
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
$version  = $releaseVersion.PackageVersion

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$template = Get-Content -Raw -Path $templatePath
$repo = 'https://github.com/christosk92/WaveeMusic/releases'

foreach ($arch in @('x64', 'arm64')) {
    $msixName = "Wavee.UI.WinUI_${version}_${arch}.msix"

    if ($Channel -eq 'experimental') {
        # GitHub has no "latest pre-release" download alias, so experimental polls a fixed
        # rolling release whose assets are re-uploaded each build (stable URL, moving content).
        $fileName        = "Wavee.Experimental.$arch.appinstaller"
        $appInstallerUri = "$repo/download/experimental-latest/$fileName"
        $msixUri         = "$repo/download/experimental-latest/$msixName"
    }
    else {
        # Stable polls the /latest alias (always the newest published production release).
        $fileName        = "Wavee.$arch.appinstaller"
        $appInstallerUri = "$repo/latest/download/$fileName"
        $msixUri         = "$repo/download/v$cleanTag/$msixName"
    }

    # .NET strings are immutable, so each Replace returns a new string and $template
    # stays pristine for the next architecture in the loop.
    $content = $template
    $content = $content.Replace('${VERSION}', $version)
    $content = $content.Replace('${ARCH}', $arch)
    $content = $content.Replace('${APPINSTALLER_URI}', $appInstallerUri)
    $content = $content.Replace('${MSIX_URI}', $msixUri)

    $outputPath = Join-Path $OutputDirectory $fileName
    Set-Content -Path $outputPath -Value $content -Encoding utf8
    Write-Host "Generated $outputPath (Version=$version, Arch=$arch, Channel=$Channel)"
}
