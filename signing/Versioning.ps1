#requires -Version 7.0

function ConvertTo-WaveePackageVersion {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)]
    [string]$Tag
  )

  $cleanTag = $Tag.Trim()
  if ($cleanTag.StartsWith('refs/tags/', [StringComparison]::OrdinalIgnoreCase)) {
    $cleanTag = $cleanTag.Substring('refs/tags/'.Length)
  }
  $cleanTag = $cleanTag.TrimStart('v', 'V')

  $withoutBuildMetadata = ($cleanTag -split '\+', 2)[0]
  $versionParts = $withoutBuildMetadata -split '-', 2
  $core = $versionParts[0]
  $prerelease = if ($versionParts.Count -gt 1) { $versionParts[1] } else { $null }

  $coreParts = $core.Split('.')
  if ($coreParts.Count -lt 3 -or $coreParts.Count -gt 4) {
    throw "Tag '$Tag' must look like v0.1.0, v0.1.0-alpha.1, or v0.1.0-beta.1."
  }

  $major = [int]$coreParts[0]
  $minor = [int]$coreParts[1]
  $patch = [int]$coreParts[2]

  foreach ($part in @($major, $minor, $patch)) {
    if ($part -lt 0 -or $part -gt 65535) {
      throw "MSIX version part '$part' is outside the 0..65535 range."
    }
  }

  if ($prerelease) {
    $identifiers = $prerelease.Split('.')
    $label = $identifiers[0].ToLowerInvariant()
    $sequence = 1

    foreach ($identifier in $identifiers) {
      $parsed = 0
      if ([int]::TryParse($identifier, [ref]$parsed)) {
        $sequence = $parsed
      }
    }

    $band = switch -Regex ($label) {
      '^(alpha|a)$' { 1000; break }
      '^(beta|b)$' { 2000; break }
      '^(rc|pre|preview)$' { 3000; break }
      default { 4000; break }
    }

    $revision = $band + $sequence
  } elseif ($coreParts.Count -eq 4) {
    $revision = [int]$coreParts[3]
  } else {
    # Keep stable vX.Y.Z higher than alpha/beta/rc releases with the same X.Y.Z.
    $revision = 10000
  }

  if ($revision -lt 0 -or $revision -gt 65535) {
    throw "MSIX revision '$revision' is outside the 0..65535 range."
  }

  [pscustomobject]@{
    CleanTag             = $cleanTag
    InformationalVersion = $cleanTag
    PackageVersion       = "$major.$minor.$patch.$revision"
  }
}

function Get-WaveeReleaseTag {
  [CmdletBinding()]
  param(
    [string]$DefaultTag = 'v0.1.0-alpha.1'
  )

  try {
    $tag = (& git describe --tags --exact-match 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
      return $tag.Trim()
    }
  } catch {
    # Fall through to the explicit alpha default for local dry runs.
  }

  $DefaultTag
}

function Set-ProjectElementText {
  param(
    [Parameter(Mandatory = $true)] [string]$Content,
    [Parameter(Mandatory = $true)] [string]$ElementName,
    [Parameter(Mandatory = $true)] [string]$Value
  )

  $pattern = "(?s)<$ElementName>.*?</$ElementName>"
  if ($Content -notmatch $pattern) {
    return $Content
  }

  [regex]::Replace($Content, $pattern, "<$ElementName>$Value</$ElementName>", 1)
}

function Set-WaveeVersion {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string]$Tag,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\src\Wavee.UI.WinUI\Wavee.UI.WinUI.csproj'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\Wavee.UI.WinUI\Package.appxmanifest')
  )

  $releaseVersion = ConvertTo-WaveePackageVersion -Tag $Tag

  $projectContent = Get-Content -Raw -Path $ProjectPath
  $projectContent = Set-ProjectElementText -Content $projectContent -ElementName 'Version' -Value $releaseVersion.InformationalVersion
  if ($projectContent -match '<InformationalVersion>.*?</InformationalVersion>') {
    $projectContent = Set-ProjectElementText -Content $projectContent -ElementName 'InformationalVersion' -Value $releaseVersion.InformationalVersion
  } else {
    $projectContent = [regex]::Replace(
      $projectContent,
      '(?s)(<Version>.*?</Version>\s*)',
      "`$1`t`t<InformationalVersion>$($releaseVersion.InformationalVersion)</InformationalVersion>`r`n",
      1)
  }
  $projectContent = Set-ProjectElementText -Content $projectContent -ElementName 'FileVersion' -Value $releaseVersion.PackageVersion
  Set-Content -Path $ProjectPath -Value $projectContent -Encoding utf8NoBOM

  [xml]$manifest = Get-Content -Raw -Path $ManifestPath
  $manifest.Package.Identity.Version = $releaseVersion.PackageVersion
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

  $releaseVersion
}
