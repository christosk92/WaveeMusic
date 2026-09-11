#requires -Version 5.1
Import-Module (Join-Path $PSScriptRoot '..\Wavee.Store.psm1') -Force -DisableNameChecking
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script:BundleTmp = Join-Path $env:TEMP ('wavee-bundle-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:BundleTmp | Out-Null
$script:BundleArgs = @{ IdentityName = 'cproducts.Wavee'; Publisher = 'CN=Store'; Quad = '1.2.809.0' }

function Add-BundleTestEntry {
    param($Zip, [string]$Name, [string]$Text)
    $writer = New-Object IO.StreamWriter($Zip.CreateEntry($Name).Open())
    try { $writer.Write($Text) } finally { $writer.Dispose() }
}

function New-BundleTestMsix {
    param([string]$Arch, [string]$Version = '1.2.809.0', [string]$Publisher = 'CN=Store', [string]$Name = 'cproducts.Wavee', [switch]$NoExe, [switch]$WrongMachine)
    $path = Join-Path $script:BundleTmp ([guid]::NewGuid().ToString('N') + '.msix')
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-BundleTestEntry $zip 'AppxManifest.xml' "<Package><Identity Name=`"$Name`" Publisher=`"$Publisher`" Version=`"$Version`" ProcessorArchitecture=`"$Arch`" /></Package>"
        if (-not $NoExe) {
            $bytes = New-Object byte[] 256
            $bytes[0] = 0x4d
            $bytes[1] = 0x5a
            [BitConverter]::GetBytes([uint32]128).CopyTo($bytes, 60)
            [BitConverter]::GetBytes([uint32]0x00004550).CopyTo($bytes, 128)
            $machine = 0x8664
            if ($Arch -eq 'arm64') { $machine = 0xaa64 }
            if ($WrongMachine) { $machine = 0x014c }
            [BitConverter]::GetBytes([uint16]$machine).CopyTo($bytes, 132)
            [Text.Encoding]::ASCII.GetBytes('synthetic native payload').CopyTo($bytes, 160)
            $stream = $zip.CreateEntry('Wavee.exe').Open()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $path
}

function New-BundleTestBundle {
    param([string[]]$Architectures = @('arm64', 'x64'), [string]$ChildVersion = '1.2.809.0', [string]$ChildPublisher = 'CN=Store', [switch]$NoExe, [switch]$LieAboutArch, [switch]$Unlisted)
    $path = Join-Path $script:BundleTmp ([guid]::NewGuid().ToString('N') + '.msixbundle')
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $rows = ''
        $index = 0
        foreach ($arch in $Architectures) {
            $childArch = $arch
            if ($LieAboutArch) { $childArch = 'x86' }
            $child = New-BundleTestMsix -Arch $childArch -Version $ChildVersion -Publisher $ChildPublisher -NoExe:$NoExe
            $fileName = "child$index.msix"
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $child, $fileName) | Out-Null
            $rows += "<Package Type=`"application`" Architecture=`"$arch`" Version=`"1.2.809.0`" FileName=`"$fileName`" />"
            $index++
        }
        if ($Unlisted) { Add-BundleTestEntry $zip 'extra.msix' 'unlisted package' }
        Add-BundleTestEntry $zip 'AppxMetadata/AppxBundleManifest.xml' "<Bundle><Identity Name=`"cproducts.Wavee`" Publisher=`"CN=Store`" Version=`"1.2.809.0`" /><Packages>$rows</Packages></Bundle>"
    } finally { $zip.Dispose() }
    $path
}

Describe 'Store dual-architecture bundle contract' {
    It 'accepts both architectures and returns nested package hashes' {
        $bundle = New-BundleTestBundle
        $inventory = @(Assert-WaveeStoreBundle -Bundle $bundle @script:BundleArgs)
        $inventory.Count | Should Be 2
        $inventory[0].Architecture | Should Be 'arm64'
        $inventory[1].Architecture | Should Be 'x64'
        $inventory[0].Sha256 | Should Match '^[a-f0-9]{64}$'
    }
    It 'accepts reverse input ordering' {
        @(Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -Architectures @('x64', 'arm64')) @script:BundleArgs).Count | Should Be 2
    }
    It 'rejects ARM64-only and x64-only uploads' {
        foreach ($arch in @('arm64', 'x64')) {
            { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -Architectures @($arch)) @script:BundleArgs } | Should Throw
        }
    }
    It 'rejects duplicate architecture and a lying outer manifest' {
        { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -Architectures @('x64', 'x64')) @script:BundleArgs } | Should Throw
        { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -LieAboutArch) @script:BundleArgs } | Should Throw
    }
    It 'rejects wrong child version and publisher' {
        { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -ChildVersion '1.2.102.0') @script:BundleArgs } | Should Throw
        { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -ChildPublisher 'CN=Other') @script:BundleArgs } | Should Throw
    }
    It 'rejects empty application payload and unlisted packages' {
        { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -NoExe) @script:BundleArgs } | Should Throw
        { Assert-WaveeStoreBundle -Bundle (New-BundleTestBundle -Unlisted) @script:BundleArgs } | Should Throw
    }
    It 'puts exactly one verified bundle inside the upload and replaces stale uploads' {
        $bundle = New-BundleTestBundle
        $out = Join-Path $script:BundleTmp 'release.msixupload'
        foreach ($attempt in @(1, 2)) {
            New-WaveeMsixUpload -Bundle $bundle -OutFile $out @script:BundleArgs | Should Be $out
            $zip = [IO.Compression.ZipFile]::OpenRead($out)
            try {
                $zip.Entries.Count | Should Be 1
                $zip.Entries[0].FullName | Should Be ([IO.Path]::GetFileName($bundle))
            } finally { $zip.Dispose() }
        }
    }
    It 'rejects the previous standalone MSIX upload contract' {
        $package = New-BundleTestMsix 'x64'
        { New-WaveeMsixUpload -Bundle $package -OutFile (Join-Path $script:BundleTmp 'bad.msixupload') @script:BundleArgs } | Should Throw
    }
    It 'rejects incomplete SDK inputs before executing the SDK' {
        { New-WaveeMsixBundle -Msix @((New-BundleTestMsix 'x64')) -OutFile (Join-Path $script:BundleTmp 'bad.msixbundle') -MakeAppx 'nonexistent.exe' @script:BundleArgs } | Should Throw
    }
}

Describe 'Archived package provenance' {
    function New-PackageEvidenceFixture {
        param([switch]$NoPlayPlay, [switch]$WrongHash, [string]$Channel = 'store', [switch]$WrongMachine)
        $msix = New-BundleTestMsix 'x64' -WrongMachine:$WrongMachine
        $symbols = Join-Path $script:BundleTmp ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $symbols | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($msix)
        try {
            $entry = $zip.GetEntry('Wavee.exe')
            $size = $entry.Length
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
            finally { $stream.Dispose(); $sha.Dispose() }
        } finally { $zip.Dispose() }
        if ($WrongHash) { $hash = '0' * 64 }
        [IO.File]::WriteAllText((Join-Path $symbols 'SYMBOLS.txt'), "quad=1.2.809.0`nchannel=$Channel`ncommit=38df212`nrid=win-x64`narch=x64`nconfiguration=Release`naot=True`nexe=Wavee.exe size=$size sha256=$hash`n")
        $map = '<NativeMap>InProcessPlayPlayKeyDeriver</NativeMap>'
        if ($NoPlayPlay) { $map = '<NativeMap />' }
        [IO.File]::WriteAllText((Join-Path $symbols 'Wavee.map.xml'), $map)
        @{ Msix = $msix; SymbolsDir = $symbols; Commit = '38df2120150311b685696c22ea4b919c089f5555'; Architecture = 'x64' }
    }
    It 'verifies the shipped executable hash, pinned commit and PlayPlay inclusion' {
        $fixture = New-PackageEvidenceFixture
        $evidence = Get-WaveeStorePackageEvidence @fixture @script:BundleArgs
        $evidence.PlayPlayIncluded | Should Be $true
        $evidence.Sha256 | Should Match '^[a-f0-9]{64}$'
    }
    It 'rejects missing PlayPlay evidence and altered executable hashes' {
        $fixture = New-PackageEvidenceFixture -NoPlayPlay
        { Get-WaveeStorePackageEvidence @fixture @script:BundleArgs } | Should Throw
        $fixture = New-PackageEvidenceFixture -WrongHash
        { Get-WaveeStorePackageEvidence @fixture @script:BundleArgs } | Should Throw
    }
    It 'rejects wrong channel and wrong source commit' {
        $fixture = New-PackageEvidenceFixture -Channel 'stable'
        { Get-WaveeStorePackageEvidence @fixture @script:BundleArgs } | Should Throw
        $fixture = New-PackageEvidenceFixture
        $fixture.Commit = '1111111111111111111111111111111111111111'
        { Get-WaveeStorePackageEvidence @fixture @script:BundleArgs } | Should Throw
    }
    It 'rejects a wrong native machine despite matching manifest and executable stamp hash' {
        $fixture = New-PackageEvidenceFixture -WrongMachine
        { Get-WaveeStorePackageEvidence @fixture @script:BundleArgs } | Should Throw 'PE machine'
    }
}

Describe 'Store command failure redaction' {
    InModuleScope Wavee.Store {
        It 'does not expose submission JSON or captured SAS output' {
            Mock Invoke-CapturedNative { [pscustomobject]@{ ExitCode = 9; Output = 'https://upload.example/?sig=SECRET_OUTPUT' } }
            $failure = ''
            try { Invoke-MsStore -Arguments @('submission', 'update', 'product', '{"secret":"SECRET_ARGUMENT"}') }
            catch { $failure = $_.Exception.Message }
            $failure | Should Match 'msstore submission update failed'
            $failure | Should Not Match 'SECRET'
            $failure | Should Not Match 'https:'
        }
    }
}
