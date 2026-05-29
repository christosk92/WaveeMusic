#requires -Version 7.0
<#
.SYNOPSIS
  Bundle the gitignored Spotify-playback source files into a base64 zip and
  push it to the GitHub Actions secret consumed by
  .github/workflows/release.yml.

.DESCRIPTION
  CI checks out only the PUBLIC repository, which deliberately omits the
  proprietary playback sources (they are gitignored). Without them the build
  defines WAVEE_SPOTIFY_PLAYBACK_STUBS and produces a player that cannot
  decrypt audio (SpotifyPlaybackCapabilities.DefaultLocalSpotifyPlaybackEnabled
  flips to false). This script zips the real files and stores them in an
  encrypted Actions secret; release.yml restores them on the runner BEFORE
  msbuild so the released MSIX is audio-enabled.

  The bundle NEVER enters git history. This .ps1 only references file paths, so
  it is safe to commit. Re-run with -SetSecret whenever any bundled file
  changes; the next tagged release then picks up the update.

  Keep $relPaths in sync with .gitignore and the Exists() swaps in
  src/Wavee/Wavee.csproj + src/Wavee.AudioHost/Wavee.AudioHost.csproj.

.PARAMETER SetSecret
  Push the bundle to the GitHub Actions secret (requires `gh auth login` and
  repo admin). Without it the script only reports the bundle size / hash.

.PARAMETER OutFile
  Optional path to also write the raw base64 (e.g. for a manual `gh secret set`).

.EXAMPLE
  ./signing/Pack-PlaybackSources.ps1
  # Dry run: list the files that would be bundled and the resulting size.

.EXAMPLE
  ./signing/Pack-PlaybackSources.ps1 -SetSecret
  # Bundle + upload to the PLAYBACK_SOURCES_ZIP_B64 Actions secret.
#>
[CmdletBinding()]
param(
  [string]$SecretName = 'PLAYBACK_SOURCES_ZIP_B64',
  [string]$Repo = 'christosk92/WaveeMusic',
  [switch]$SetSecret,
  [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# The gitignored build inputs needed for a functional (audio-enabled) build.
$relPaths = @(
  'src/Wavee/Core/Crypto/AudioDecryptStream.cs'
  'src/Wavee/Core/Audio/PlayPlayConstants.cs'
  'src/Wavee/Core/Audio/AudioRuntimeProvisioner.cs'
  'src/Wavee/Core/Audio/RuntimeManifest.cs'
  'src/Wavee.AudioHost/PlayPlay/PlayPlayKeyEmulator.cs'
)

Write-Host "Bundling playback sources from $repoRoot"
Add-Type -AssemblyName System.IO.Compression | Out-Null
$ms = [System.IO.MemoryStream]::new()
$zip = [System.IO.Compression.ZipArchive]::new($ms, [System.IO.Compression.ZipArchiveMode]::Create, $true)
try {
  foreach ($rel in $relPaths) {
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path $full)) {
      throw "Required playback source not found: $rel`nThis machine is missing a proprietary file; cannot build a functional bundle."
    }
    # Entry name uses the repo-relative path so Expand-Archive on the runner
    # restores each file to its correct location.
    $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
    $es = $entry.Open()
    try {
      $bytes = [System.IO.File]::ReadAllBytes($full)
      $es.Write($bytes, 0, $bytes.Length)
    } finally { $es.Dispose() }
    Write-Host ("  + {0,7:N0} B  {1}" -f ([System.IO.FileInfo]::new($full)).Length, $rel)
  }
} finally { $zip.Dispose() }

$zipBytes = $ms.ToArray()
$b64 = [Convert]::ToBase64String($zipBytes)
$sha = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($zipBytes)).Replace('-', '').ToLowerInvariant()

Write-Host ""
Write-Host ("  zip bytes    : {0:N0}" -f $zipBytes.Length)
Write-Host ("  base64 chars : {0:N0}  (GitHub single-secret cap 65,536)" -f $b64.Length)
Write-Host ("  zip sha256   : {0}" -f $sha)

if ($b64.Length -ge 65536) {
  throw "base64 length $($b64.Length) exceeds the 65,536-char single-secret limit. Switch the carrier to a private repo + deploy key."
}

if ($OutFile) {
  Set-Content -Path $OutFile -Value $b64 -NoNewline -Encoding ascii
  Write-Host "  wrote base64 -> $OutFile"
}

if ($SetSecret) {
  Write-Host ""
  Write-Host "Pushing secret '$SecretName' to $Repo ..."
  $b64 | gh secret set $SecretName --repo $Repo
  if ($LASTEXITCODE -ne 0) { throw "gh secret set failed (exit $LASTEXITCODE)" }
  Write-Host "Done. release.yml restores these sources on the runner before msbuild."
} else {
  Write-Host ""
  Write-Host "Dry run only. Re-run with -SetSecret to upload to GitHub Actions."
}
