<#
.SYNOPSIS
  Signs an .msix / .msixbundle with Azure Artifact Signing (formerly
  Trusted Signing).

.DESCRIPTION
  Wraps SignTool with the Azure.CodeSigning.Dlib plugin shipped by the
  Microsoft.Azure.ArtifactSigningClientTools package.

  Auth precedence (first match wins):
    1. AZURE_CLIENT_ID + AZURE_TENANT_ID + AZURE_CLIENT_SECRET env vars
       (service-principal flow — used by CI)
    2. Existing `az login` credentials (interactive local flow)

  Requires:
    - Microsoft.Azure.ArtifactSigningClientTools installed (winget)
    - signing/metadata.json populated (copy from metadata.template.json)
    - Manifest Publisher already rewritten to the cert profile Subject Name
      (run Set-Publisher.ps1 first)

.PARAMETER Path
  Path to a single .msix/.msixbundle file, or a folder containing them.

.PARAMETER MetadataPath
  Path to the Artifact Signing metadata.json. Defaults to ./metadata.json.

.PARAMETER DlibPath
  Override the Azure.CodeSigning.Dlib.dll location. Auto-detected by default.

.EXAMPLE
  .\Sign-Msix.ps1 -Path ..\Wavee.UI.WinUI\AppPackages\Wavee.UI.WinUI_0.1.0.0_x64.msix

.EXAMPLE
  .\Sign-Msix.ps1 -Path ..\Wavee.UI.WinUI\AppPackages
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Path,
  [string]$MetadataPath = (Join-Path $PSScriptRoot 'metadata.json'),
  [string]$DlibPath,
  [string]$SignToolPath,
  [string[]]$IncludeExtensions = @('*.msix','*.msixbundle')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $MetadataPath)) {
  throw @"
metadata.json not found at $MetadataPath.
Copy signing/metadata.template.json to signing/metadata.json and fill in
your Endpoint, CodeSigningAccountName, and CertificateProfileName.
"@
}

# Locate the Artifact Signing dlib.
if (-not $DlibPath) {
  $candidates = @(
    # Current installer (per-user under LOCALAPPDATA).
    "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
    # Older Program Files layouts (pre-rename / earlier MSIs).
    'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
    'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
    'C:\Program Files (x86)\Microsoft\TrustedSigningClientTools\bin\Azure.CodeSigning.Dlib.dll'
  )
  $DlibPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $DlibPath -or -not (Test-Path $DlibPath)) {
  throw @"
Azure.CodeSigning.Dlib.dll not found.
Install with: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
Or pass -DlibPath explicitly.
"@
}

# Locate SignTool. The current Artifact Signing client tools no longer
# bundle signtool.exe — they ship a stub batch that delegates to the
# Windows SDK's signing tools. We pick the highest-version x64 SDK
# signtool available (>= 10.0.22621 is required by the dlib).
if (-not $SignToolPath) {
  $signCandidates = @()
  foreach ($root in @('C:\Program Files (x86)\Windows Kits\10\bin','C:\Program Files\Windows Kits\10\bin')) {
    if (Test-Path $root) {
      $signCandidates += Get-ChildItem -Path $root -Directory |
        Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
        Sort-Object {[Version]$_.Name} -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path $_ }
    }
  }
  $SignToolPath = $signCandidates | Select-Object -First 1
  if (-not $SignToolPath) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { $SignToolPath = $cmd.Source }
  }
}
if (-not $SignToolPath -or -not (Test-Path $SignToolPath)) {
  throw @"
signtool.exe (Windows SDK) not found.
Run the bundled installer to add the SDK signing tools:
  & '$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\winsdksetup.exe' /features OptionId.SigningTools /q /norestart
Or pass -SignToolPath explicitly.
"@
}

# Resolve target files.
$resolved = Resolve-Path -Path $Path
$item = Get-Item $resolved
if ($item.PSIsContainer) {
  $files = Get-ChildItem -Path $item.FullName -Include $IncludeExtensions -Recurse -File
} else {
  $files = @($item)
}
if ($files.Count -eq 0) {
  throw "No matching files (filter: $($IncludeExtensions -join ',')) found at $Path"
}

# Validate auth env vars are set (or rely on az login).
$hasSpn = ($env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_SECRET)
if (-not $hasSpn) {
  Write-Host "AZURE_CLIENT_ID/AZURE_TENANT_ID/AZURE_CLIENT_SECRET not all set — relying on existing 'az login' session." -ForegroundColor Yellow
}

Write-Host "Using SignTool : $signtool"
Write-Host "Using Dlib     : $DlibPath"
Write-Host "Metadata       : $MetadataPath"
Write-Host ""

foreach ($f in $files) {
  Write-Host "Signing $($f.FullName)..." -ForegroundColor Cyan
  & $signtool sign `
    /v `
    /fd SHA256 `
    /tr "https://timestamp.acs.microsoft.com" `
    /td SHA256 `
    /dlib $DlibPath `
    /dmdf $MetadataPath `
    $f.FullName
  if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed for $($f.FullName) (exit $LASTEXITCODE)"
  }
}

Write-Host ""
Write-Host "Signed $($files.Count) file(s)." -ForegroundColor Green
