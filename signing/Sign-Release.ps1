<#
.SYNOPSIS
  End-to-end local signing for a Wavee MSIX. Builds, signs with Azure
  Artifact Signing, verifies the chain, and (optionally) installs.

.DESCRIPTION
  Reproduces the procedure last verified on 2026-05-04 against the
  `cproducts` Public Trust certificate profile. Every step here was
  individually proven before being baked into this script — see
  signing/README.md for the proof points and a step-by-step walkthrough.

  The source Package.appxmanifest stays at the developer publisher
  (CN=ckara) so F5 sideload deploys keep working with self-signed
  certs. This script swaps the manifest to the production publisher
  just-in-time around msbuild, then restores it via `git checkout`.
  No state leaks across runs: a Ctrl-C in the middle is recovered by
  the finally block.

  Invariants that must hold:
    1. Package.appxmanifest Publisher MUST equal the cert profile
       Subject Name during build. SignTool fails 0x8007000B if not.
    2. msbuild MUST be invoked WITHOUT /p:RuntimeIdentifier — that
       flag triggers WMC9999 in the WinUI XAML compiler. Pass only
       /p:Platform.
    3. UapAppxPackageBuildMode=SideloadOnly + AppxBundle=Never +
       AppxPackageSigningEnabled=false. The first two pin packaging
       behaviour; the third leaves the .msix unsigned for signtool.

.PARAMETER Platform
  Target architecture. Defaults to ARM64 (matches the developer machine).
  Pass x64 for the Intel build.

.PARAMETER OutputDir
  Where to drop the signed .msix. Defaults to .signed-msix\ under the
  repo root (gitignored).

.PARAMETER ReleasePublisherSubject
  Subject Name from the cert profile in Azure Artifact Signing. Defaults
  to the cproducts profile that issued the existing certs.

.PARAMETER SkipInstall
  Skip the final Add-AppxPackage step. Default: install the freshly
  signed package locally for smoke testing.

.PARAMETER ClientSecret
  Azure SP client secret. If omitted, falls back to AZURE_CLIENT_SECRET
  env var, then to interactive `az login` credentials.

.EXAMPLE
  ./signing/Sign-Release.ps1
  # Build + sign + verify + install ARM64 MSIX

.EXAMPLE
  ./signing/Sign-Release.ps1 -Platform x64 -SkipInstall
  # Build + sign + verify x64 MSIX, leave it on disk

.NOTES
  Pre-reqs (winget, all approved by user 2026-05-04):
    - Microsoft.AzureCLI                              -- az.exe
    - Microsoft.Azure.ArtifactSigningClientTools      -- Azure.CodeSigning.Dlib.dll
    - Inkscape.Inkscape                               -- only if regenerating tile assets

  Plus Windows SDK signtool.exe — auto-installed by the bundled
  winsdksetup.exe in ArtifactSigningClientTools with /features
  OptionId.SigningTools.

  Auth precedence used by the dlib's DefaultAzureCredential:
    1. AZURE_CLIENT_ID + AZURE_TENANT_ID + AZURE_CLIENT_SECRET (env)
    2. existing `az login` session

  Do NOT commit a populated metadata.json (gitignored). The CI
  workflow synthesises it from secrets on every run.
#>
[CmdletBinding()]
param(
  [ValidateSet('ARM64','x64','x86')]
  [string]$Platform = 'ARM64',
  [string]$OutputDir,
  [string]$ReleasePublisherSubject = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL',
  [switch]$SkipInstall,
  [string]$ClientSecret
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot '.signed-msix' }
$logDir = Join-Path $OutputDir 'logs'
New-Item -ItemType Directory -Force -Path $OutputDir, $logDir | Out-Null

Write-Host "==========================================================="
Write-Host "Wavee local signing — Platform=$Platform"
Write-Host "==========================================================="

# --- Resolve toolchain paths ----------------------------------------------
$msbuild = (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if (-not $msbuild) { throw "MSBuild not found. Install Visual Studio with the MSBuild component." }

$signtool = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\signtool.exe' -ErrorAction SilentlyContinue |
             Sort-Object { [Version]((Split-Path -Parent $_.FullName) | Split-Path -Leaf | Split-Path -Leaf) } -Descending |
             Select-Object -First 1).FullName
if (-not $signtool) {
  $signtool = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\signtool.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $signtool) { throw "signtool.exe not found. Run %LOCALAPPDATA%\Microsoft\MicrosoftArtifactSigningClientTools\winsdksetup.exe /features OptionId.SigningTools /q /norestart" }

$dlib = "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll"
if (-not (Test-Path $dlib)) { throw "Azure.CodeSigning.Dlib.dll not found at $dlib. Run: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools" }

$metadata = Join-Path $PSScriptRoot 'metadata.json'
if (-not (Test-Path $metadata)) { throw "$metadata missing. Copy metadata.template.json to metadata.json and fill in your Endpoint/Account/Profile." }

$proj = Join-Path $repoRoot 'src\Wavee.UI.WinUI\Wavee.UI.WinUI.csproj'
$manifestPath = Join-Path $repoRoot 'src\Wavee.UI.WinUI\Package.appxmanifest'

Write-Host "  msbuild   : $msbuild"
Write-Host "  signtool  : $signtool"
Write-Host "  dlib      : $dlib"
Write-Host "  metadata  : $metadata"
Write-Host "  publisher : $ReleasePublisherSubject"
Write-Host ""

# --- Step 1: capture original publisher and swap to release subject -------
Write-Host "[1/6] Swapping manifest publisher for release..."
[xml]$manifestXml = Get-Content -Raw -Path $manifestPath
$originalPublisher = $manifestXml.Package.Identity.Publisher
Write-Host "      original = $originalPublisher"
Write-Host "      release  = $ReleasePublisherSubject"

try {
  if ($originalPublisher -ne $ReleasePublisherSubject) {
    $manifestXml.Package.Identity.Publisher = $ReleasePublisherSubject
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.Encoding = New-Object System.Text.UTF8Encoding($true)
    $writer = [System.Xml.XmlWriter]::Create($manifestPath, $settings)
    try { $manifestXml.Save($writer) } finally { $writer.Close() }
  }

  # --- Step 2: build unsigned MSIX ---------------------------------------
  Write-Host "[2/6] Building unsigned MSIX (Release|$Platform)..."
  $buildLog = Join-Path $logDir "build-$Platform.log"
  $pkgDir = Join-Path $OutputDir 'AppPackages\'
  if (Test-Path $pkgDir) { Remove-Item -Recurse -Force $pkgDir }

  # IMPORTANT: do NOT pass /p:RuntimeIdentifier — triggers WMC9999.
  & $msbuild $proj `
    /restore /m /nologo /v:m `
    /p:Configuration=Release `
    /p:Platform=$Platform `
    /p:GenerateAppxPackageOnBuild=true `
    /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:AppxBundle=Never `
    /p:AppxPackageSigningEnabled=false `
    /p:AppxPackageDir=$pkgDir 2>&1 |
    Tee-Object -FilePath $buildLog | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit $LASTEXITCODE). See $buildLog."
    throw "MSBuild failed"
  }

  $msix = Get-ChildItem -Path $pkgDir -Filter "Wavee.UI.WinUI_*.msix" -Recurse -File |
          Where-Object { $_.FullName -notlike '*\Dependencies\*' } |
          Select-Object -First 1
  if (-not $msix) { throw "Built package not found under $pkgDir" }
  Write-Host "      -> $($msix.FullName) ($([math]::Round($msix.Length/1MB,2)) MB)"

  # --- Step 3: configure SP credentials ---------------------------------
  Write-Host "[3/6] Authenticating to Artifact Signing..."
  if ($ClientSecret) { $env:AZURE_CLIENT_SECRET = $ClientSecret }
  if (-not $env:AZURE_TENANT_ID -or -not $env:AZURE_CLIENT_ID) {
    Write-Warning "AZURE_TENANT_ID / AZURE_CLIENT_ID env vars not set; relying on existing 'az login' session."
  } else {
    Write-Host "      Using SP $($env:AZURE_CLIENT_ID) in tenant $($env:AZURE_TENANT_ID)"
  }

  # --- Step 4: sign with the dlib ---------------------------------------
  Write-Host "[4/6] Signing $($msix.Name)..."
  & $signtool sign /v `
    /fd SHA256 `
    /tr 'http://timestamp.acs.microsoft.com' /td SHA256 `
    /dlib $dlib `
    /dmdf $metadata `
    $msix.FullName | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "SignTool failed (exit $LASTEXITCODE)" }

  # --- Step 5: verify chain ---------------------------------------------
  Write-Host "[5/6] Verifying signature chain..."
  $verifyLog = Join-Path $logDir "verify-$Platform.log"
  & $signtool verify /pa /v $msix.FullName 2>&1 | Tee-Object -FilePath $verifyLog | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Error "Signature verification failed. See $verifyLog."
    throw "Verify failed"
  }
  $sha256 = (Get-FileHash -Algorithm SHA256 $msix.FullName).Hash
  Write-Host "      OK. SHA256 = $sha256"

  # --- Step 6: install (optional) ---------------------------------------
  if ($SkipInstall) {
    Write-Host "[6/6] Skipping install (-SkipInstall)."
  } else {
    Write-Host "[6/6] Installing locally (Add-AppxPackage)..."
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
    $pkg = Get-AppxPackage -Name '*Wavee*' | Select-Object -First 1
    if ($pkg) {
      Write-Host "      Installed: $($pkg.PackageFullName)"
      Write-Host "      Launch:    explorer.exe shell:AppsFolder\$($pkg.PackageFamilyName)!App"
    }
  }

  Write-Host ""
  Write-Host "Done. MSIX at: $($msix.FullName)"
}
finally {
  # Restore the source manifest to the developer publisher even on Ctrl-C
  # so the working tree is never left in the release state.
  if ($originalPublisher -and ($manifestXml.Package.Identity.Publisher -ne $originalPublisher)) {
    Write-Host ""
    Write-Host "Restoring manifest publisher to '$originalPublisher'..."
    [xml]$cleanup = Get-Content -Raw -Path $manifestPath
    $cleanup.Package.Identity.Publisher = $originalPublisher
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.Encoding = New-Object System.Text.UTF8Encoding($true)
    $writer = [System.Xml.XmlWriter]::Create($manifestPath, $settings)
    try { $cleanup.Save($writer) } finally { $writer.Close() }
  }
}
