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

<#
.SYNOPSIS
  vcvarsall.bat argument for this host targeting $Arch (arm64 / amd64 / arm64_amd64 / amd64_arm64).
#>
function Get-VcVarsAllName {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('arm64', 'x64')]
    [string]$Arch,
    [switch]$HostIsArm
  )
  if ($HostIsArm) {
    if ($Arch -eq 'x64') { 'arm64_amd64' } else { 'arm64' }
  } else {
    if ($Arch -eq 'arm64') { 'amd64_arm64' } else { 'amd64' }
  }
}

<#
.SYNOPSIS
  Load MSVC link.exe + LIB/INCLUDE into this process, then NativeAOT can skip ILC's findvcvarsall.bat.
.DESCRIPTION
  ILC's findvcvarsall.bat calls vcvarsall.bat while MSBuild pipes stdout (ConsoleToMSBuild). That probe exits 1
  with "Platform linker not found" even when HostARM64\arm64\link.exe and the Windows SDK are installed.
  This helper runs vcvarsall with its stdout on a file (not a pipe), then imports the dumped environment.
  Pair with /p:IlcUseEnvironmentalTools=true on dotnet publish.
#>
function Import-MsvcEnvironment {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('arm64', 'x64')]
    [string]$Arch
  )

  $vswhere = Add-VsInstallerToPath
  if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at $vswhere. Install Visual Studio (or the Build Tools)."
  }

  $probe = Invoke-Native $vswhere @('-latest', '-prerelease', '-products', '*', '-property', 'installationPath') -AllowFailure
  $vsBase = @($probe.Output | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1)[0]
  if (-not $vsBase) { throw 'vswhere reported no Visual Studio installation.' }

  $vcvars = Join-Path $vsBase 'VC\Auxiliary\Build\vcvarsall.bat'
  if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat not found at $vcvars." }

  $hostIsArm = ("$env:PROCESSOR_ARCHITEW6432$env:PROCESSOR_ARCHITECTURE" -match 'ARM64')
  $vcEnv = Get-VcVarsAllName -Arch $Arch -HostIsArm:$hostIsArm

  $work = Join-Path ([IO.Path]::GetTempPath()) ('wavee-vcvars-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $work | Out-Null
  $wrapper = Join-Path $work 'run.bat'
  $envFile = Join-Path $work 'env.txt'
  $logFile = Join-Path $work 'vcvars.log'
  try {
    # Redirect vcvarsall onto a file so a piped parent stdout cannot break it the way findvcvarsall.bat does.
    @(
      '@echo off'
      "call `"$vcvars`" $vcEnv > `"$logFile`" 2>&1"
      'if errorlevel 1 exit /b 1'
      "set > `"$envFile`""
    ) | Set-Content -Path $wrapper -Encoding ASCII

    $run = Invoke-Native 'cmd.exe' @('/c', $wrapper) -AllowFailure
    if ($run.ExitCode -ne 0 -or -not (Test-Path $envFile)) {
      $tail = ''
      if (Test-Path $logFile) { $tail = (Get-Content $logFile -Raw) }
      throw "vcvarsall.bat $vcEnv failed (exit $($run.ExitCode)). Install the C++ $Arch build tools. $tail"
    }

    Get-Content $envFile | ForEach-Object {
      $eq = $_.IndexOf('=')
      if ($eq -lt 1) { return }
      Set-Item -LiteralPath ("Env:" + $_.Substring(0, $eq)) -Value $_.Substring($eq + 1)
    }
  }
  finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
  }

  $linkOnPath = $false
  foreach ($dir in ($env:Path -split ';')) {
    if ($dir -and (Test-Path (Join-Path $dir 'link.exe'))) { $linkOnPath = $true; break }
  }
  if (-not $linkOnPath) {
    throw "link.exe not on PATH after vcvarsall $vcEnv. Install the C++ $Arch build tools."
  }
  if ([string]::IsNullOrEmpty($env:LIB)) {
    throw "LIB is empty after vcvarsall $vcEnv. Install the Windows SDK."
  }
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

# ---------------------------------------------------------------------------------------------------------------
# PE export table
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Minimal PE export-directory reader. Returns the plain export name list out of a PE image's raw bytes.
.DESCRIPTION
  Reads the COFF/optional headers and the export directory by hand (no dumpbin dependency - not guaranteed on
  every release box). An image with no export table, or that is not a PE at all, returns @() rather than
  throwing: the caller (Test-PeExport, or a release check) decides what an empty/missing export list means.
.OUTPUTS
  string[] - the exported names, in export-table order.
#>
function Get-PeExportedNames {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    if ($Bytes.Length -lt 0x40 -or $Bytes[0] -ne 0x4D -or $Bytes[1] -ne 0x5A) { return @() }   # 'MZ'
    $u16 = { param($o) [BitConverter]::ToUInt16($Bytes, $o) }
    $u32 = { param($o) [BitConverter]::ToUInt32($Bytes, $o) }

    $peOffset = & $u32 0x3C
    if ($Bytes.Length -lt ($peOffset + 24) -or $Bytes[$peOffset] -ne 0x50 -or $Bytes[$peOffset + 1] -ne 0x45) { return @() }   # 'PE'
    $numSections = & $u16 ($peOffset + 6)
    $optHeaderSize = & $u16 ($peOffset + 20)
    $optHeaderStart = $peOffset + 24
    $magic = & $u16 $optHeaderStart
    $dataDirOffset = $optHeaderStart + $(if ($magic -eq 0x20B) { 112 } else { 96 })   # PE32+ vs PE32
    $exportRva = & $u32 $dataDirOffset
    $exportSize = & $u32 ($dataDirOffset + 4)
    if ($exportRva -eq 0 -or $exportSize -eq 0) { return @() }

    $sections = @()
    $sectionStart = $optHeaderStart + $optHeaderSize
    for ($i = 0; $i -lt $numSections; $i++) {
        $so = $sectionStart + ($i * 40)
        $sections += [pscustomobject]@{
            VirtualSize      = & $u32 ($so + 8)
            VirtualAddress   = & $u32 ($so + 12)
            SizeOfRawData    = & $u32 ($so + 16)
            PointerToRawData = & $u32 ($so + 20)
        }
    }
    $rvaToOffset = {
        param([uint32]$Rva)
        foreach ($s in $sections) {
            $size = [Math]::Max($s.VirtualSize, $s.SizeOfRawData)
            if ($Rva -ge $s.VirtualAddress -and $Rva -lt ($s.VirtualAddress + $size)) {
                return [uint32]($Rva - $s.VirtualAddress + $s.PointerToRawData)
            }
        }
        $null
    }

    $expOff = & $rvaToOffset $exportRva
    if ($null -eq $expOff) { return @() }
    $numNames = & $u32 ($expOff + 24)
    $namesRva = & $u32 ($expOff + 32)
    $namesOff = & $rvaToOffset $namesRva
    if ($null -eq $namesOff -or $numNames -eq 0) { return @() }

    $names = @()
    for ($i = 0; $i -lt $numNames; $i++) {
        $nameOff = & $rvaToOffset (& $u32 ($namesOff + ($i * 4)))
        if ($null -eq $nameOff) { continue }
        $end = $nameOff
        while ($end -lt $Bytes.Length -and $Bytes[$end] -ne 0) { $end++ }
        $names += [System.Text.Encoding]::ASCII.GetString($Bytes, $nameOff, $end - $nameOff)
    }
    $names
}

<#
.SYNOPSIS
  True when a PE image's bytes export the given symbol name (case-insensitive, via PowerShell's default -contains
  comparer - the same comparison the inline check this replaced already used).
.DESCRIPTION
  The release gate for G-152/G-205: a native DLL that fails to export the entry point the managed side
  P/Invokes into would silently degrade DRM video for every install of that architecture until the next
  release. Kept as a one-line predicate over Get-PeExportedNames so a release check and a Pester fact read the
  same rule.
#>
function Test-PeExport {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes, [Parameter(Mandatory = $true)][string]$Name)
    (Get-PeExportedNames $Bytes) -contains $Name
}

# ---------------------------------------------------------------------------------------------------------------
# Engine root resolution (decision D1 / G-022)
# ---------------------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Resolve $(EngineRoot) exactly the way Directory.Build.props resolves it, so the release script (and anything
  else that needs the engine checkout off-MSBuild) never disagrees with what actually got built.
.DESCRIPTION
  Precedence, matching Directory.Build.props:36-39 exactly:
    1. -Override (the command-line/env override: MSBuild's "-p:EngineRoot=..." wins over everything else there,
       so a caller that has an equivalent override - e.g. $env:EngineRoot - passes it here first).
    2. EngineRoot.local.props next to RepoRoot, if present (git-ignored per-worktree pin, decision D1) - read the
       same way MSBuild would import it, by regexing the <EngineRoot>...</EngineRoot> element text.
    3. The sibling checkout, "<RepoRoot>..\fluent-gpu".

  LITERAL-PATH RULE (G-205 caveat, deliberate - not a parser bug): step 2 is a regex over the element text, not an
  MSBuild evaluation. It reads whatever text sits between <EngineRoot ...> and </EngineRoot> verbatim, so:
    - A <PropertyGroup Condition="..."> wrapping the element, or a Condition on the <EngineRoot> element itself,
      is IGNORED - the text is taken unconditionally, same as every EngineRoot.local.props this repo's tooling
      writes (a single unconditional pin: `<EngineRoot Condition="'$(EngineRoot)' == ''">C:\...\</EngineRoot>`,
      whose Condition is a tautology at the point nothing upstream has already set EngineRoot).
    - An MSBuild property function or property reference inside the text (e.g. "$(SomeVar)" or
      "$([MSBuild]::NormalizeDirectory(...))") comes back UNEXPANDED, as the literal characters. This function
      never expands MSBuild expressions - only a real MSBuild evaluation could - so a hand-edited
      EngineRoot.local.props that uses one silently resolves to a path MSBuild would never actually build.
  A caller that writes EngineRoot.local.props by hand (rather than through the tooling that generates it) must
  keep it to one unconditional <EngineRoot>literal\path</EngineRoot> element for this function and MSBuild to
  agree. See Resolve-EngineRoot's Pester facts (ops/release/tests/Wavee.Build.Tests.ps1) for the exact contract.
.OUTPUTS
  The resolved engine root path as a string (not guaranteed to exist - callers check for
  src\FluentGpu.Engine\FluentGpu.Engine.csproj under it, same as the MSBuild target does).
#>
function Resolve-EngineRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$Override
    )
    if ($Override) { return $Override }

    $localProps = Join-Path $RepoRoot 'EngineRoot.local.props'
    if (Test-Path $localProps) {
        $t = [IO.File]::ReadAllText($localProps)
        $m = [regex]::Match($t, '<EngineRoot\b[^>]*>([^<]+)</EngineRoot>')
        if ($m.Success -and $m.Groups[1].Value.Trim()) { return $m.Groups[1].Value.Trim() }
    }

    Join-Path $RepoRoot '..\fluent-gpu'
}

Export-ModuleMember -Function *
