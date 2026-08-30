#requires -Version 5.1
<#
.SYNOPSIS
  Shared build helpers for the MSIX pack scripts (pack-wavee-msix.ps1, pack-msix.ps1), publish-wavee-aot.ps1 and the
  local release orchestrator (ops/release/wavee-release.ps1).

.DESCRIPTION
  Everything here is Windows PowerShell 5.1 compatible and ASCII-only inside string literals: this file is saved
  WITHOUT a BOM, so PS 5.1 decodes it as ANSI and a non-ASCII character inside a QUOTED STRING is a parse error that
  kills the whole module (comments survive it, which is why comments may carry punctuation a string literal cannot).

  Import with:  Import-Module (Join-Path $PSScriptRoot 'Wavee.Build.psm1') -Force -DisableNameChecking
#>

# ---------------------------------------------------------------------------------------------------------------
# Toolchain discovery
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Locate the highest installed Windows 10 SDK that carries the x64 packaging tools.
.OUTPUTS
  @{ Version; ToolDir; MakeAppx; MakePri; SignTool }
#>
function Get-WindowsSdkTools {
  [CmdletBinding()]
  param([string]$KitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin')

  $sdk = Get-ChildItem $KitsBin -Directory -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } |
         Sort-Object { [version]$_.Name } | Select-Object -Last 1
  if (-not $sdk) { throw "No Windows SDK with makeappx.exe found under $KitsBin. Install the Windows SDK." }

  # The x64 tools run fine on an arm64 host (emulation) and are arch-agnostic in what they produce.
  $toolDir = Join-Path $sdk.FullName 'x64'
  [pscustomobject]@{
    Version  = $sdk.Name
    ToolDir  = $toolDir
    MakeAppx = Join-Path $toolDir 'makeappx.exe'
    MakePri  = Join-Path $toolDir 'makepri.exe'
    SignTool = Join-Path $toolDir 'signtool.exe'
  }
}

<#
.SYNOPSIS
  Put the Visual Studio Installer directory (vswhere.exe) on PATH once, so NativeAOT's ILC can find MSVC link.exe.
.OUTPUTS
  The full path to vswhere.exe (which may not exist).
#>
function Add-VsInstallerToPath {
  [CmdletBinding()]
  param()

  $vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
  if ((Test-Path (Join-Path $vsInstaller 'vswhere.exe')) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH = "$vsInstaller;$env:PATH"
  }
  Join-Path $vsInstaller 'vswhere.exe'
}

<#
.SYNOPSIS
  Probe whether this machine can NativeAOT cross-compile to win-x64.
.DESCRIPTION
  ILC shells out to the MSVC linker for the TARGET architecture, hosted on THIS machine's architecture: an arm64 host
  targeting x64 needs VC\Tools\MSVC\<ver>\bin\HostARM64\x64\link.exe (an x64 host needs HostX64\x64). The Windows
  filesystem is case-insensitive, so HostArm64 and HostARM64 name the same directory.
  The ILC runtime pack (runtime.win-x64.microsoft.dotnet.ilcompiler) is reported for information only: dotnet restores
  it on demand, so its absence is not a failure.
.OUTPUTS
  @{ Ok; LinkExe; IlcPack; Reason }
#>
function Test-X64CrossToolchain {
  [CmdletBinding()]
  param()

  $vswhere = Add-VsInstallerToPath
  if (-not (Test-Path $vswhere)) {
    return [pscustomobject]@{ Ok = $false; LinkExe = $null; IlcPack = $null
      Reason = "vswhere.exe not found at $vswhere. Install Visual Studio (or the Build Tools)." }
  }

  $probe = Invoke-Native $vswhere @('-all','-prerelease','-property','installationPath') -AllowFailure
  $roots = @($probe.Output | Where-Object { $_ -and (Test-Path $_) })
  if ($roots.Count -eq 0) {
    return [pscustomobject]@{ Ok = $false; LinkExe = $null; IlcPack = $null
      Reason = 'vswhere reported no Visual Studio installation.' }
  }

  $isArmHost = ("$env:PROCESSOR_ARCHITEW6432$env:PROCESSOR_ARCHITECTURE" -match 'ARM64')
  $hostDir = 'HostX64'
  if ($isArmHost) { $hostDir = 'HostARM64' }

  $link = $null
  foreach ($root in $roots) {
    $msvc = Join-Path $root 'VC\Tools\MSVC'
    if (-not (Test-Path $msvc)) { continue }
    $found = Get-ChildItem $msvc -Directory -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending |
             ForEach-Object { Join-Path $_.FullName "bin\$hostDir\x64\link.exe" } |
             Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { $link = $found; break }
  }

  $ilc = $null
  $ilcRoot = Join-Path $env:USERPROFILE '.nuget\packages\runtime.win-x64.microsoft.dotnet.ilcompiler'
  if (Test-Path $ilcRoot) {
    $ilcVer = Get-ChildItem $ilcRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($ilcVer) { $ilc = $ilcVer.FullName } else { $ilc = $ilcRoot }
  }

  $reason = ''
  if (-not $link) {
    $reason = "MSVC x64 cross tools not found ($hostDir\x64\link.exe under VC\Tools\MSVC). Install the " +
              "'MSVC v143 - VS 2022 C++ x64/x86 build tools' component, or pass -NoAot."
  }
  [pscustomobject]@{ Ok = [bool]$link; LinkExe = $link; IlcPack = $ilc; Reason = $reason }
}

# ---------------------------------------------------------------------------------------------------------------
# Wavee.Version.props
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Read src/apps/Wavee/Wavee.Version.props - the single source of the semver, codename and release counter.
.OUTPUTS
  @{ Version; Codename; Build; Path }
#>
function Get-WaveeVersionProps([string]$Path) {
  $t = [IO.File]::ReadAllText($Path)
  $m = [regex]::Match($t, '<WaveeVersion>([^<]+)</WaveeVersion>')
  $c = [regex]::Match($t, '<WaveeCodename>([^<]+)</WaveeCodename>')
  $b = [regex]::Match($t, '<WaveeBuild>(\d+)</WaveeBuild>')
  if (-not ($m.Success -and $c.Success -and $b.Success)) { throw "Wavee.Version.props is missing WaveeVersion/WaveeCodename/WaveeBuild: $Path" }
  [pscustomobject]@{ Version = $m.Groups[1].Value.Trim(); Codename = $c.Groups[1].Value.Trim(); Build = [int]$b.Groups[1].Value; Path = $Path }
}

<#
.SYNOPSIS
  Rewrite the <WaveeBuild> counter in place (UTF-8, no BOM). Only ops/release/wavee-release.ps1 calls this.
#>
function Set-WaveeBuild([string]$Path, [int]$Build) {
  if ($Build -lt 0 -or $Build -gt 65535) { throw "WaveeBuild out of range: $Build" }
  $t = [IO.File]::ReadAllText($Path)
  $rx = [regex]'<WaveeBuild>\d+</WaveeBuild>'
  if ($rx.Matches($t).Count -ne 1) { throw "expected exactly one <WaveeBuild> in $Path" }
  [IO.File]::WriteAllText($Path, $rx.Replace($t, "<WaveeBuild>$Build</WaveeBuild>"), (New-Object System.Text.UTF8Encoding $false))
}

# ---------------------------------------------------------------------------------------------------------------
# Native process invocation
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Run a native executable, capture stdout+stderr, and throw on a non-zero exit unless -AllowFailure.
.DESCRIPTION
  ErrorActionPreference is softened to 'Continue' for the duration: under 'Stop', merging a native command's stderr
  (2>&1) in PS 5.1 raises NativeCommandError and kills the script even when the exe returned 0.

  $LASTEXITCODE is a SESSION variable, so it survives from whatever ran last. It is cleared before the call, because
  an exe that starts and produces no exit code of its own would otherwise inherit a stale non-zero one and be
  reported as failed. An executable that is not on PATH raises CommandNotFoundException, which is reported as exit
  127 (the shell convention) so a caller can -AllowFailure a probe for an optional tool.
.OUTPUTS
  @{ ExitCode; Output }
#>
function Invoke-Native([string]$FilePath, [string[]]$ArgumentList, [switch]$AllowFailure) {
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $out = $null
  $code = 0
  $global:LASTEXITCODE = 0
  try {
    try {
      $out = & $FilePath @ArgumentList 2>&1
      if ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }
    }
    catch [System.Management.Automation.CommandNotFoundException] {
      $code = 127
      $out = @("$FilePath : command not found")
    }
  }
  finally { $ErrorActionPreference = $prev }
  if ($code -ne 0 -and -not $AllowFailure) { throw "$FilePath exited $code`n$($out -join "`n")" }
  [pscustomobject]@{ ExitCode = $code; Output = @($out | ForEach-Object { "$_" }) }
}

# ---------------------------------------------------------------------------------------------------------------
# Signing
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Sign every given file with Azure Trusted Signing in ONE signtool invocation, then verify each signature.
.DESCRIPTION
  One invocation matters: each signtool run re-authenticates against the signing account, and a release signs two
  packages plus their .appinstaller siblings. The manifest Publisher MUST equal the certificate profile's subject
  name or signtool fails with 0x8007000B.
#>
function Invoke-TrustedSigning([string[]]$Path, [string]$Metadata, [string]$Subscription = 'Azure subscription 1', [string]$SignTool) {
  if (-not (Test-Path $Metadata)) { throw "Trusted Signing metadata not found: $Metadata (copy ops/build/signing/metadata.template.json -> metadata.json)" }
  $dlib = @("$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
            'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
            'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
            'C:\Program Files (x86)\Microsoft\TrustedSigningClientTools\bin\Azure.CodeSigning.Dlib.dll') |
          Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $dlib) { throw "Azure.CodeSigning.Dlib.dll not found. winget install -e --id Microsoft.Azure.ArtifactSigningClientTools" }
  if (-not ($env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_SECRET)) {
    # No SPN env vars: rely on an existing 'az login' session, with the subscription that holds the signing account
    # selected so DefaultAzureCredential's token has access. Tolerated failure - signtool reports the real problem.
    Invoke-Native 'az' @('account','set','--subscription',$Subscription) -AllowFailure | Out-Null
  }
  Invoke-Native $SignTool (@('sign','/v','/fd','SHA256','/tr','http://timestamp.acs.microsoft.com','/td','SHA256','/dlib',$dlib,'/dmdf',$Metadata) + $Path) | Out-Null
  foreach ($p in $Path) { if (-not (Test-MsixSignature $p $SignTool)) { throw "signature did not verify: $p" } }
}

<#
.SYNOPSIS
  Sign every given file with a reusable self-signed dev cert, and export the .cer next to the FIRST file.
.OUTPUTS
  The X509Certificate2 that was used.
#>
function Invoke-DevCertSigning([string[]]$Path, [string]$Publisher, [string]$FriendlyName, [string]$SignTool) {
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher `
              -KeyUsage DigitalSignature -FriendlyName $FriendlyName `
              -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
              -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
  }
  foreach ($p in $Path) {
    Invoke-Native $SignTool @('sign','/fd','SHA256','/sha1',$cert.Thumbprint,'/tr','http://timestamp.digicert.com','/td','SHA256',$p) | Out-Null
  }
  Export-Certificate -Cert $cert -FilePath ([IO.Path]::ChangeExtension($Path[0], '.cer')) | Out-Null
  $cert
}

<#
.SYNOPSIS
  True when signtool verify /pa accepts the signature chain. A self-signed dev cert legitimately fails this until its
  .cer is imported into LocalMachine\TrustedPeople, so it is informational on the dev path and a gate on the TS path.
#>
function Test-MsixSignature([string]$Path, [string]$SignTool) {
  (Invoke-Native $SignTool @('verify','/pa','/q',$Path) -AllowFailure).ExitCode -eq 0
}

# ---------------------------------------------------------------------------------------------------------------
# Package inspection
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Read Identity out of a packed .msix (its AppxManifest.xml entry) - the ground truth Windows compares on update.
.OUTPUTS
  @{ Name; Publisher; Version; ProcessorArchitecture }
#>
function Get-MsixIdentity([string]$Path) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [IO.Compression.ZipFile]::OpenRead($Path)
  try {
    $e = $zip.GetEntry('AppxManifest.xml')
    if (-not $e) { throw "AppxManifest.xml not found inside $Path" }
    $sr = New-Object IO.StreamReader($e.Open())
    try { [xml]$x = $sr.ReadToEnd() } finally { $sr.Dispose() }
  }
  finally { $zip.Dispose() }
  $id = $x.Package.Identity
  [pscustomobject]@{ Name = $id.Name; Publisher = $id.Publisher; Version = $id.Version; ProcessorArchitecture = $id.ProcessorArchitecture }
}

<#
.SYNOPSIS
  The COFF machine type of a PE file (0x8664 = AMD64, 0xAA64 = ARM64, 0x014C = I386), or $null if it is not a PE.
.DESCRIPTION
  0x014C is also what a managed IL-only (AnyCPU) assembly reports, which is most of a self-contained JIT layout - only
  a NATIVE image can be built for the wrong machine, so callers sweeping a layout skip 0x014C.
#>
function Get-PeMachine([string]$Path) {
  $fs = [IO.File]::OpenRead($Path)
  try {
    if ($fs.Length -lt 0x40) { return $null }
    $br = New-Object IO.BinaryReader($fs)
    if ($br.ReadUInt16() -ne 0x5A4D) { return $null }        # 'MZ'
    $fs.Position = 0x3C
    $pe = $br.ReadInt32()
    if ($pe -lt 0 -or ($pe + 6) -gt $fs.Length) { return $null }
    $fs.Position = $pe
    if ($br.ReadUInt32() -ne 0x00004550) { return $null }    # PE signature
    return $br.ReadUInt16()
  }
  finally { $fs.Dispose() }
}

<#
.SYNOPSIS
  True when the PE machine type of a file matches the requested architecture (0x8664 = AMD64, 0xAA64 = ARM64).
.DESCRIPTION
  The cheap guard against a cross-compile that quietly produced host-arch binaries: an arm64 payload inside a package
  stamped x64 installs and then fails to launch.
#>
function Test-PeMachine([string]$Path, [ValidateSet('arm64','x64')][string]$Arch) {
  $m = Get-PeMachine $Path
  if ($null -eq $m) { return $false }
  if ($Arch -eq 'x64') { return ($m -eq 0x8664) }
  ($m -eq 0xAA64)
}

Export-ModuleMember -Function *
