<#
.SYNOPSIS
  Generate THIRD-PARTY-NOTICES.txt from the solution's PackageReferences plus the vendored/system entries in
  notices-extra.json.

.DESCRIPTION
  Walks the csproj files that contribute redistributed code to a Wavee build, reads each PackageReference's nuspec out
  of the local NuGet cache (%USERPROFILE%\.nuget\packages) for its license / authors / project URL, merges the
  hand-maintained notices-extra.json entries (vendored source and system components that have no package), and writes a
  plain-text notices file.

  Called by pack-wavee-msix.ps1 and publish-wavee-aot.ps1 so the file ships NEXT TO Wavee.exe -- Settings > About reads
  it from AppContext.BaseDirectory. A committed copy also lives at the repo root so the GitHub link in the About tab
  resolves for people who never install the app.

  Build-time-only packages (PrivateAssets=all, e.g. Grpc.Tools) are INCLUDED and marked "build-time": they are still
  tooling whose license governs the generated code in the shipped binary.

.EXAMPLE
  powershell -File ops\build\generate-third-party-notices.ps1 -OutFile THIRD-PARTY-NOTICES.txt

.NOTES
  Windows PowerShell 5.1 compatible. ASCII ONLY inside string literals: this file has no BOM, so 5.1 decodes it as ANSI
  and a non-ASCII character in a QUOTED STRING is a parse error that kills the whole script (comments survive it).
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$OutFile,
  [string]$Root = '',
  # The FluentGpu engine is a SIBLING checkout since the repo split (Directory.Build.props $(EngineRoot)), not a
  # subtree: the engine csproj globs below resolve against this root, never against $Root.
  [string]$EngineRoot = ''
)
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside a param() default under `powershell -File` on 5.1, so the defaults resolve here.
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Root) { $Root = Join-Path $scriptDir '..\..' }
$Root = (Resolve-Path $Root).Path
if (-not $EngineRoot) { $EngineRoot = Join-Path $Root '..\fluent-gpu' }
if (-not (Test-Path $EngineRoot)) { throw "Engine checkout not found at $EngineRoot (clone christosk92/fluent-gpu beside this repo, or pass -EngineRoot)." }
$EngineRoot = (Resolve-Path $EngineRoot).Path
$extraFile = Join-Path $scriptDir 'notices-extra.json'
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'

# The projects whose PackageReferences end up in a shipped Wavee build. Wavee.Core is deliberately dependency-free and
# Wavee.Sdk / the modules ride on the SDK alone, but they are listed so a future package reference is picked up here
# instead of silently escaping the notices.
$projectGlobs = @(
  'src\apps\Wavee\Wavee.csproj',
  'src\apps\Wavee.Core\Wavee.Core.csproj',
  'src\apps\Wavee.Sdk\Wavee.Sdk.csproj',
  'src\apps\modules\*\*.csproj'
)
# Engine projects that carry redistributed packages (TerraFX.Interop.Windows lives here). Relative to $EngineRoot.
$engineProjectGlobs = @(
  'src\FluentGpu.Windows\FluentGpu.Windows.csproj',
  'src\FluentGpu.WindowsApi\FluentGpu.WindowsApi.csproj'
)

function Get-ProjectFiles {
  $files = New-Object System.Collections.Generic.List[string]
  $pairs = @()
  foreach ($g in $projectGlobs) { $pairs += ,@($Root, $g) }
  foreach ($g in $engineProjectGlobs) { $pairs += ,@($EngineRoot, $g) }
  foreach ($pair in $pairs) {
    $full = Join-Path $pair[0] $pair[1]
    $found = @(Get-ChildItem -Path $full -File -ErrorAction SilentlyContinue)
    # A glob that matches nothing is a silent hole in the notices (the post-split engine paths were exactly that), so a
    # literal project path that is missing fails the run instead of dropping its packages.
    if ($found.Count -eq 0 -and $pair[1].IndexOf('*') -lt 0) { throw "Notices project not found: $full" }
    foreach ($f in $found) {
      if (-not $files.Contains($f.FullName)) { $files.Add($f.FullName) }
    }
  }
  return $files
}

function Get-PackageReferences([string]$csproj) {
  $text = Get-Content -Path $csproj -Raw -ErrorAction SilentlyContinue
  if (-not $text) { return @() }
  $pattern = '<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"\s*(?:/>|>(.*?)</PackageReference>)'
  $refs = New-Object System.Collections.Generic.List[object]
  foreach ($m in [regex]::Matches($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
    $body = $m.Groups[3].Value
    $refs.Add([pscustomobject]@{
      Id        = $m.Groups[1].Value
      Version   = $m.Groups[2].Value
      BuildOnly = ($body -match '<PrivateAssets>\s*all\s*</PrivateAssets>')
    })
  }
  return $refs
}

function Find-NuspecPath([string]$id, [string]$version) {
  $dir = Join-Path $nugetRoot ($id.ToLowerInvariant())
  if (-not (Test-Path $dir)) { return $null }
  $verDir = Join-Path $dir $version
  if (-not (Test-Path $verDir)) {
    # The restored folder is the NORMALIZED version (4.0.0.0 -> 4.0.0), so an exact miss falls back to the newest
    # restored version of that id rather than dropping the component from the notices entirely.
    $candidates = Get-ChildItem -Path $dir -Directory -ErrorAction SilentlyContinue | Sort-Object Name
    if (-not $candidates) { return $null }
    $verDir = $candidates[-1].FullName
  }
  $nuspec = Get-ChildItem -Path $verDir -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $nuspec) { return $null }
  return $nuspec.FullName
}

function Read-Nuspec([string]$path) {
  $text = Get-Content -Path $path -Raw -ErrorAction SilentlyContinue
  if (-not $text) { return $null }
  $license = ''
  $m = [regex]::Match($text, '<license[^>]*type="expression"[^>]*>([^<]*)</license>')
  if ($m.Success) { $license = $m.Groups[1].Value.Trim() }
  if (-not $license) {
    $m = [regex]::Match($text, '<license[^>]*type="file"[^>]*>([^<]*)</license>')
    if ($m.Success) { $license = 'see ' + $m.Groups[1].Value.Trim() + ' in the package' }
  }
  if (-not $license) {
    $m = [regex]::Match($text, '<licenseUrl>([^<]*)</licenseUrl>')
    if ($m.Success) { $license = $m.Groups[1].Value.Trim() }
  }
  $authors = ''
  $m = [regex]::Match($text, '<authors>([^<]*)</authors>')
  if ($m.Success) { $authors = $m.Groups[1].Value.Trim() }
  $project = ''
  $m = [regex]::Match($text, '<projectUrl>([^<]*)</projectUrl>')
  if ($m.Success) { $project = $m.Groups[1].Value.Trim() }
  $repo = ''
  $m = [regex]::Match($text, '<repository[^>]*url="([^"]*)"')
  if ($m.Success) { $repo = $m.Groups[1].Value.Trim() }
  return [pscustomobject]@{ License = $license; Authors = $authors; ProjectUrl = $project; RepositoryUrl = $repo }
}

# ---- collect ---------------------------------------------------------------------------------------------------

$components = New-Object System.Collections.Generic.List[object]
$seen = New-Object System.Collections.Generic.HashSet[string]

foreach ($proj in (Get-ProjectFiles)) {
  foreach ($ref in (Get-PackageReferences $proj)) {
    $key = ($ref.Id + '/' + $ref.Version).ToLowerInvariant()
    if (-not $seen.Add($key)) { continue }

    $license = 'see package'
    $authors = ''
    $url = ''
    $nuspec = Find-NuspecPath $ref.Id $ref.Version
    if ($nuspec) {
      $info = Read-Nuspec $nuspec
      if ($info) {
        if ($info.License) { $license = $info.License }
        $authors = $info.Authors
        if ($info.ProjectUrl) { $url = $info.ProjectUrl } elseif ($info.RepositoryUrl) { $url = $info.RepositoryUrl }
      }
    }
    if (-not $url) { $url = 'https://www.nuget.org/packages/' + $ref.Id + '/' + $ref.Version }

    $note = ''
    if ($ref.BuildOnly) { $note = 'build-time (not redistributed as a binary; governs generated code)' }

    $components.Add([pscustomobject]@{
      Name    = $ref.Id
      Version = $ref.Version
      License = $license
      Url     = $url
      Authors = $authors
      Note    = $note
    })
  }
}

if (Test-Path $extraFile) {
  $extra = Get-Content -Path $extraFile -Raw | ConvertFrom-Json
  foreach ($e in $extra) {
    $components.Add([pscustomobject]@{
      Name    = $e.name
      Version = $e.version
      License = $e.license
      Url     = $e.url
      Authors = ''
      Note    = $e.note
    })
  }
}

# ---- render ----------------------------------------------------------------------------------------------------

$sb = New-Object System.Text.StringBuilder
function Line([string]$s) { [void]$sb.AppendLine($s) }

Line 'THIRD-PARTY NOTICES'
Line '==================='
Line ''
Line 'Wavee is licensed under the MIT License (see LICENSE). Wavee is an independent client and is not'
Line 'affiliated with, endorsed by, or sponsored by Spotify AB. Spotify and the Spotify logo are'
Line 'trademarks of Spotify AB.'
Line ''
Line 'This build incorporates the third-party components listed below. Each remains under its own license;'
Line 'the summary here names the license, not its full text -- follow the URL for the authoritative terms.'
Line ''
Line ('Generated ' + (Get-Date).ToString('yyyy-MM-dd') + ' by ops/build/generate-third-party-notices.ps1.')
Line ''
Line '-------------------------------------------------------------------------------'
Line ''

foreach ($c in ($components | Sort-Object Name)) {
  Line ($c.Name + '  ' + $c.Version)
  Line ('  license: ' + $c.License)
  if ($c.Authors) { Line ('  authors: ' + $c.Authors) }
  if ($c.Url)     { Line ('  url:     ' + $c.Url) }
  if ($c.Note)    { Line ('  note:    ' + $c.Note) }
  Line ''
}

Line '-------------------------------------------------------------------------------'
Line ''
Line 'Wavee also talks to third-party services at runtime (Spotify, lyrics providers, GitHub for update'
Line 'checks). Those are services, not bundled code; see PRIVACY.md for what is sent where.'

$outDirPath = Split-Path -Parent $OutFile
if ($outDirPath -and -not (Test-Path $outDirPath)) { New-Item -ItemType Directory -Force -Path $outDirPath | Out-Null }
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), $utf8NoBom)
Write-Host ("==> third-party notices: " + $OutFile + " (" + $components.Count + " components)") -ForegroundColor Cyan
