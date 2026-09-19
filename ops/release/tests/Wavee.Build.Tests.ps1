#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1 - Describe / Context / It / Should Be /
    Should Throw / Mock -ModuleName). Run with:

        Invoke-Pester ops\release\tests

    Covers the release-script helpers that G-205 found untestable inside wavee-release.ps1 itself (a Pester file
    may not dot-source that script - it would run the build/sign/git/gh phases) and that now live in
    ops/build/Wavee.Build.psm1 instead: the PE export-directory reader used by the PlayReady native-DLL gate
    (G-152), and the engine-root resolution that must never disagree with Directory.Build.props's own precedence
    (G-022).

    Get-PeExportedNames was previously "only sanity-checked by hand against kernel32.dll" (G-205's own words) -
    this file replaces that by hand with a tiny synthetic PE built from bytes at test time, so the fact is
    deterministic and does not depend on what happens to be on the test box.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path

Import-Module (Join-Path $repoRoot 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking

$script:TmpRoot = Join-Path $env:TEMP ('wavee-build-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:TmpRoot | Out-Null

function New-TmpDir {
    param([string]$Name)
    $p = Join-Path $script:TmpRoot $Name
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    $p
}

# ===================================================================================================================
# A minimal PE32 image with a real IMAGE_EXPORT_DIRECTORY naming two exports: "FgPrRuntimeCreate" and
# "OtherExport". Every field Get-PeExportedNames actually reads is laid out exactly per the PE spec; fields it
# never reads (AddressOfFunctions, ordinals, section name, timestamps, ...) are left zero.
# ===================================================================================================================

function New-FixturePeBytes {
    param([switch]$NoExportDirectory)

    $bytes = New-Object byte[] 0x600

    function Set-U16([byte[]]$b, [int]$off, [int]$v) {
        $bs = [BitConverter]::GetBytes([uint16]$v)
        [Array]::Copy($bs, 0, $b, $off, 2)
    }
    function Set-U32([byte[]]$b, [int]$off, [long]$v) {
        $bs = [BitConverter]::GetBytes([uint32]$v)
        [Array]::Copy($bs, 0, $b, $off, 4)
    }
    function Set-Ascii([byte[]]$b, [int]$off, [string]$s) {
        $sb = [System.Text.Encoding]::ASCII.GetBytes($s)
        [Array]::Copy($sb, 0, $b, $off, $sb.Length)
        $b[$off + $sb.Length] = 0
    }

    # DOS header: 'MZ' + e_lfanew pointing at the PE header.
    $bytes[0] = 0x4D; $bytes[1] = 0x5A
    $peOffset = 0x80
    Set-U32 $bytes 0x3C $peOffset

    # PE signature + COFF file header.
    $bytes[$peOffset] = 0x50; $bytes[$peOffset + 1] = 0x45   # 'PE'; the other two signature bytes stay 0
    Set-U16 $bytes ($peOffset + 4) 0x8664                    # Machine: AMD64 (not read by the parser, informational)
    Set-U16 $bytes ($peOffset + 6) 1                         # NumberOfSections
    $optHeaderSize = 104
    Set-U16 $bytes ($peOffset + 20) $optHeaderSize            # SizeOfOptionalHeader

    # Optional header: PE32 magic, then the data-directory export entry at the fixed PE32 offset (96).
    $optHeaderStart = $peOffset + 24
    Set-U16 $bytes $optHeaderStart 0x10B                      # magic: PE32
    $dataDirOffset = $optHeaderStart + 96
    if ($NoExportDirectory) {
        Set-U32 $bytes $dataDirOffset 0
        Set-U32 $bytes ($dataDirOffset + 4) 0
    }
    else {
        Set-U32 $bytes $dataDirOffset 0x2000                  # export directory RVA
        Set-U32 $bytes ($dataDirOffset + 4) 0x100              # export directory size (informational)
    }

    # One section, ".edata"-shaped, covering everything the export directory points into.
    $sectionStart = $optHeaderStart + $optHeaderSize
    Set-Ascii $bytes $sectionStart '.edata'
    Set-U32 $bytes ($sectionStart + 8) 0x400                  # VirtualSize
    Set-U32 $bytes ($sectionStart + 12) 0x2000                # VirtualAddress
    Set-U32 $bytes ($sectionStart + 16) 0x400                 # SizeOfRawData
    Set-U32 $bytes ($sectionStart + 20) 0x200                 # PointerToRawData

    if (-not $NoExportDirectory) {
        # IMAGE_EXPORT_DIRECTORY at file offset 0x200 (RVA 0x2000, section starts there with no padding).
        $expOff = 0x200
        Set-U32 $bytes ($expOff + 20) 2                        # NumberOfFunctions
        Set-U32 $bytes ($expOff + 24) 2                        # NumberOfNames
        Set-U32 $bytes ($expOff + 32) 0x2040                   # AddressOfNames RVA

        # AddressOfNames: two name RVAs, at file offset 0x240 (RVA 0x2040).
        Set-U32 $bytes 0x240 0x2060
        Set-U32 $bytes 0x244 0x2090

        # The names themselves, at file offsets 0x260 (RVA 0x2060) and 0x290 (RVA 0x2090) - far enough apart that
        # "FgPrRuntimeCreate\0" (19 bytes) cannot run into the second name.
        Set-Ascii $bytes 0x260 'FgPrRuntimeCreate'
        Set-Ascii $bytes 0x290 'OtherExport'
    }

    , $bytes
}

# ===================================================================================================================

Describe 'Get-PeExportedNames' {

    It 'reads every export name out of a synthetic PE, in AddressOfNames order' {
        $names = Get-PeExportedNames (New-FixturePeBytes)
        $names.Count | Should Be 2
        $names[0] | Should Be 'FgPrRuntimeCreate'
        $names[1] | Should Be 'OtherExport'
    }

    It 'returns empty for a PE with no export directory' {
        $names = @(Get-PeExportedNames (New-FixturePeBytes -NoExportDirectory))
        $names.Count | Should Be 0
    }

    It 'returns empty for bytes with no MZ signature' {
        $names = @(Get-PeExportedNames ([System.Text.Encoding]::ASCII.GetBytes('not a PE at all, just text')))
        $names.Count | Should Be 0
    }

    It 'returns empty for a truncated buffer too short to hold a DOS header' {
        $names = @(Get-PeExportedNames ([byte[]](0x4D, 0x5A, 0, 0)))
        $names.Count | Should Be 0
    }

    It 'returns empty for a single-byte buffer (Mandatory rejects a truly empty array before this ever runs)' {
        $names = @(Get-PeExportedNames ([byte[]](0x00)))
        $names.Count | Should Be 0
    }
}

Describe 'Test-PeExport' {

    It 'is true for an export the image actually has' {
        Test-PeExport (New-FixturePeBytes) 'FgPrRuntimeCreate' | Should Be $true
    }

    It 'is false for a name the image does not export' {
        Test-PeExport (New-FixturePeBytes) 'FgSomethingElse' | Should Be $false
    }

    It 'compares case-insensitively, same as the -contains PowerShell already used before the move (G-205)' {
        Test-PeExport (New-FixturePeBytes) 'fgprruntimecreate' | Should Be $true
    }

    It 'is false when the image has no export directory at all' {
        Test-PeExport (New-FixturePeBytes -NoExportDirectory) 'FgPrRuntimeCreate' | Should Be $false
    }
}

# ===================================================================================================================

Describe 'Resolve-EngineRoot' {

    It 'an -Override always wins, even with a conflicting EngineRoot.local.props present' {
        $repo = New-TmpDir 'override-wins'
        Set-Content -Path (Join-Path $repo 'EngineRoot.local.props') -Value @'
<Project>
  <PropertyGroup>
    <EngineRoot Condition="'$(EngineRoot)' == ''">C:\WAVEE\fluent-gpu-pin\</EngineRoot>
  </PropertyGroup>
</Project>
'@
        Resolve-EngineRoot -RepoRoot $repo -Override 'D:\somewhere\else\' | Should Be 'D:\somewhere\else\'
    }

    It 'reads the pin out of EngineRoot.local.props when there is no override (decision D1)' {
        $repo = New-TmpDir 'local-props-pin'
        Set-Content -Path (Join-Path $repo 'EngineRoot.local.props') -Value @'
<Project>
  <!-- This worktree's engine pin (decision D1, gap G-022). Git-ignored: each checkout picks its own engine. -->
  <PropertyGroup>
    <EngineRoot Condition="'$(EngineRoot)' == ''">C:\WAVEE\fluent-gpu-pin\</EngineRoot>
  </PropertyGroup>
</Project>
'@
        Resolve-EngineRoot -RepoRoot $repo | Should Be 'C:\WAVEE\fluent-gpu-pin\'
    }

    It 'falls back to the sibling ..\fluent-gpu when there is no override and no local pin' {
        $repo = New-TmpDir 'sibling-fallback'
        Resolve-EngineRoot -RepoRoot $repo | Should Be (Join-Path $repo '..\fluent-gpu')
    }

    It 'falls back to the sibling when EngineRoot.local.props exists but has no EngineRoot element' {
        $repo = New-TmpDir 'local-props-empty'
        Set-Content -Path (Join-Path $repo 'EngineRoot.local.props') -Value '<Project><PropertyGroup></PropertyGroup></Project>'
        Resolve-EngineRoot -RepoRoot $repo | Should Be (Join-Path $repo '..\fluent-gpu')
    }

    # G-205's regex caveat, made a documented contract rather than a silent surprise: this reads the element text,
    # not an MSBuild evaluation. Two facts pin exactly how that differs from what MSBuild itself would do.

    It 'ignores a Condition on the EngineRoot element itself and takes the text unconditionally (literal-path rule)' {
        $repo = New-TmpDir 'local-props-condition-ignored'
        # A Condition that would be FALSE once something upstream had already set EngineRoot - MSBuild would then
        # skip this element entirely. The regex has no notion of "already set", so it still returns the text.
        Set-Content -Path (Join-Path $repo 'EngineRoot.local.props') -Value @'
<Project>
  <PropertyGroup>
    <EngineRoot Condition="'$(EngineRoot)' != ''">C:\WAVEE\fluent-gpu-pin\</EngineRoot>
  </PropertyGroup>
</Project>
'@
        Resolve-EngineRoot -RepoRoot $repo | Should Be 'C:\WAVEE\fluent-gpu-pin\'
    }

    It 'returns an MSBuild property function unexpanded (literal-path rule - only a real MSBuild pass expands it)' {
        $repo = New-TmpDir 'local-props-unexpanded-function'
        Set-Content -Path (Join-Path $repo 'EngineRoot.local.props') -Value @'
<Project>
  <PropertyGroup>
    <EngineRoot>$([MSBuild]::NormalizeDirectory('C:\WAVEE\fluent-gpu-pin'))</EngineRoot>
  </PropertyGroup>
</Project>
'@
        # This is the caveat itself: a hand-written pin using a property function does NOT resolve to
        # C:\WAVEE\fluent-gpu-pin\ here, even though MSBuild would expand it that way. Every
        # EngineRoot.local.props this repo's own tooling writes is a plain literal path, so real pins are
        # unaffected - but a hand edit using one would silently disagree with the build.
        Resolve-EngineRoot -RepoRoot $repo | Should Be "`$([MSBuild]::NormalizeDirectory('C:\WAVEE\fluent-gpu-pin'))"
    }
}

Remove-Item -Recurse -Force $script:TmpRoot -ErrorAction SilentlyContinue
