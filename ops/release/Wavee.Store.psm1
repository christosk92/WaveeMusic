#requires -Version 5.1
<#
    Wavee.Store.psm1 - the pure helpers behind ops\release\wavee-store-submit.ps1 (the Microsoft Store
    submission runbook).

    Everything here is either a pure decision (the Store version quad, the .msixupload container, the submission
    JSON patch, the submission-status classification) or a thin, testable wrapper over one external tool
    (msstore-cli). The orchestrator owns the phase sequencing and the store-state ledger; this module owns the
    decisions.

    Style rules: PowerShell 5.1 only, ASCII-only string literals (an em dash is [char]0x2014), UTF-8 without a BOM
    on every file this module writes, no && / || / ternary.
#>

$script:ReleaseModulePath = Join-Path $PSScriptRoot 'Wavee.Release.psm1'
if (-not (Test-Path $script:ReleaseModulePath)) {
    throw "Wavee.Release.psm1 not found next to this module (expected $($script:ReleaseModulePath))."
}
$script:BuildModulePath = Join-Path $PSScriptRoot '..\build\Wavee.Build.psm1'
if (-not (Test-Path $script:BuildModulePath)) {
    throw "Wavee.Build.psm1 not found next to this module (expected $($script:BuildModulePath)). ops\build and ops\release ship together."
}
# -Global: these nested -Force imports would otherwise unload the modules from any caller that imported them first.
# The release module supplies Test-WaveeSemver (ONE semver rule for the feed and the Store); the build module
# supplies Get-MsixIdentity / Invoke-Native and is imported LAST so its exports survive the release module's own
# nested -Force import of it.
Import-Module $script:ReleaseModulePath -Force -DisableNameChecking -Global
Import-Module $script:BuildModulePath -Force -DisableNameChecking -Global

# ---------------------------------------------------------------------------------------------------------------
# Store versioning
# ---------------------------------------------------------------------------------------------------------------

function ConvertTo-WaveeStoreQuad {
    <#
    .SYNOPSIS
      stable semver + build counter -> the Store package quad: (M+1).m.(p*100+build).0. Same rule as the retired
      0.2.x app's src\apps\_old\Wavee.Core\Versioning\StoreVersion.Quad (0.3 has no live app-side twin yet - this
      module is the one place the rule is enforced today) and the -Channel store branch of
      ops\build\pack-wavee-msix.ps1: the Store owns the 4th part (must be 0 on upload) and refuses a major of 0,
      so the build counter folds into the 3rd part and the major is lifted by one. 0.2.1 build 2 -> 1.2.102.0.
    .DESCRIPTION
      Beta semvers are refused outright (the Store channel ships stable releases only), and the fold overflows the
      3rd part once the patch reaches 655 (655 * 100 + build > 65535) - that throws here rather than at upload.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][int]$Build)

    $s = Test-WaveeSemver $Semver
    if ($s.Channel -ne 'stable') { throw "the Store ships stable releases only: $Semver is on the $($s.Channel) channel" }
    if ($Build -lt 0) { throw "negative build counter: $Build" }
    $major = $s.Major + 1
    $third = $s.Patch * 100 + $Build
    foreach ($p in @($major, $s.Minor, $third)) {
        if ($p -gt 65535) { throw "Store quad part greater than 65535 for $Semver build $Build (the fold is patch * 100 + build, so it overflows at patch 655)" }
    }
    "$major.$($s.Minor).$third.0"
}

function Test-WaveeStoreQuad {
    <#
    .SYNOPSIS
      Validate a quad against the Store's shape: four numeric parts, each 0..65535, major >= 1 and the 4th part 0
      (the Store owns it). Throws on anything else; returns the quad so a caller can chain it.
    #>
    param([Parameter(Mandatory = $true)][string]$Quad)

    $m = [regex]::Match($Quad, '^(?<a>\d{1,5})\.(?<b>\d{1,5})\.(?<c>\d{1,5})\.(?<d>\d{1,5})$')
    if (-not $m.Success) { throw "bad Store quad: '$Quad' (expected four numeric parts, M.m.p.0)" }
    $parts = @([int]$m.Groups['a'].Value, [int]$m.Groups['b'].Value, [int]$m.Groups['c'].Value, [int]$m.Groups['d'].Value)
    foreach ($p in $parts) {
        if ($p -gt 65535) { throw "Store quad part greater than 65535 (Windows rejects it): $Quad" }
    }
    if ($parts[0] -lt 1) { throw "Store quad major must be >= 1 (the Store refuses 0.x): $Quad" }
    if ($parts[3] -ne 0) { throw "Store quad 4th part must be 0 (the Store owns it): $Quad" }
    $Quad
}

# ---------------------------------------------------------------------------------------------------------------
# The .msixupload container
# ---------------------------------------------------------------------------------------------------------------

function Assert-WaveeStorePackageIdentity {
    param($Identity, [string]$IdentityName, [string]$Publisher, [string]$Quad)
    if ("$($Identity.Name)" -cne $IdentityName -or "$($Identity.Publisher)" -cne $Publisher -or
        "$($Identity.Version)" -ne $Quad) { throw 'Store package identity/version mismatch.' }
}

function Get-WaveeZipXml {
    param($Zip, [string]$Name)
    $entries = @($Zip.Entries | Where-Object { $_.FullName -ceq $Name })
    if ($entries.Count -ne 1) { throw "Archive must contain exactly one $Name." }
    $reader = New-Object IO.StreamReader($entries[0].Open())
    try { [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-WaveeStreamHash {
    param($Stream)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($hash.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Assert-WaveeStoreBundle {
    param(
        [Parameter(Mandatory = $true)][string]$Bundle,
        [Parameter(Mandatory = $true)][string]$IdentityName,
        [Parameter(Mandatory = $true)][string]$Publisher,
        [Parameter(Mandatory = $true)][string]$Quad)
    Test-WaveeStoreQuad $Quad | Out-Null
    if ([IO.Path]::GetExtension($Bundle) -ine '.msixbundle') { throw 'Store upload requires one .msixbundle.' }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Bundle).Path)
    try {
        $manifest = Get-WaveeZipXml $zip 'AppxMetadata/AppxBundleManifest.xml'
        Assert-WaveeStorePackageIdentity $manifest.Bundle.Identity $IdentityName $Publisher $Quad
        $packages = @($manifest.Bundle.Packages.Package)
        if ($packages.Count -ne 2) { throw 'Store bundle must contain exactly arm64 and x64 application packages.' }
        $seen = @{}
        $inventory = @()
        foreach ($package in $packages) {
            $arch = "$($package.Architecture)".ToLowerInvariant()
            if ($arch -notin @('arm64', 'x64') -or $seen.ContainsKey($arch)) { throw "Missing or duplicate Store architecture: $arch" }
            if ("$($package.Type)" -ne 'application' -or "$($package.Version)" -ne $Quad -or
                "$($package.FileName)" -match '[/\\]' -or [IO.Path]::GetExtension("$($package.FileName)") -ine '.msix') {
                throw 'Bundle must contain full root-level application MSIX packages at the release version.'
            }
            $entries = @($zip.Entries | Where-Object { $_.FullName -ceq "$($package.FileName)" })
            if ($entries.Count -ne 1) { throw 'Bundle child package missing or duplicated.' }
            $memory = New-Object IO.MemoryStream
            $stream = $entries[0].Open()
            try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
            try {
                $memory.Position = 0
                $sha = Get-WaveeStreamHash $memory
                $memory.Position = 0
                $child = New-Object IO.Compression.ZipArchive($memory, [IO.Compression.ZipArchiveMode]::Read, $true)
                try {
                    $inner = Get-WaveeZipXml $child 'AppxManifest.xml'
                    Assert-WaveeStorePackageIdentity $inner.Package.Identity $IdentityName $Publisher $Quad
                    if ("$($inner.Package.Identity.ProcessorArchitecture)" -ine $arch) { throw 'Bundle and child architecture mismatch.' }
                    if (@($child.Entries | Where-Object { $_.FullName -ceq 'Wavee.exe' -and $_.Length -gt 0 }).Count -ne 1) {
                        throw 'Bundle contains a missing or empty Wavee executable.'
                    }
                } finally { $child.Dispose() }
            } finally { $memory.Dispose() }
            $seen[$arch] = $true
            $inventory += [pscustomobject]@{ Architecture = $arch; FileName = "$($package.FileName)"; Sha256 = $sha; Version = $Quad }
        }
        $contained = @($zip.Entries | Where-Object { $_.FullName -match '\.(msix|appx|msixbundle|appxbundle)$' })
        if ($contained.Count -ne 2) { throw 'Unexpected unlisted packages inside Store bundle.' }
        $inventory | Sort-Object Architecture
    } finally { $zip.Dispose() }
}

function New-WaveeMsixBundle {
    param(
        [Parameter(Mandatory = $true)][string[]]$Msix,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [Parameter(Mandatory = $true)][string]$MakeAppx,
        [Parameter(Mandatory = $true)][string]$IdentityName,
        [Parameter(Mandatory = $true)][string]$Publisher,
        [Parameter(Mandatory = $true)][string]$Quad)
    Test-WaveeStoreQuad $Quad | Out-Null
    if ($Msix.Count -ne 2) { throw 'Store releases require both arm64 and x64.' }
    $seen = @{}
    foreach ($path in $Msix) {
        $id = Get-MsixIdentity $path
        Assert-WaveeStorePackageIdentity $id $IdentityName $Publisher $Quad
        $arch = "$($id.ProcessorArchitecture)".ToLowerInvariant()
        if ($arch -notin @('arm64', 'x64') -or $seen.ContainsKey($arch)) { throw 'Store releases require distinct arm64 and x64 packages.' }
        $seen[$arch] = $path
    }
    $output = [IO.Path]::GetFullPath($OutFile)
    if ([IO.Path]::GetExtension($output) -ine '.msixbundle') { throw 'Bundle output must end in .msixbundle.' }
    $parent = Split-Path -Parent $output
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $stage = Join-Path $parent ('bundle-input-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        foreach ($arch in @('arm64', 'x64')) { Copy-Item -LiteralPath $seen[$arch] -Destination (Join-Path $stage "Wavee_${Quad}_${arch}.msix") }
        $arguments = @('bundle', '/d', $stage, '/bv', $Quad, '/p', $output, '/o')
        $commandLine = ($arguments | ForEach-Object { ConvertTo-Win32QuotedArgument $_ }) -join ' '
        $result = Invoke-CapturedNative -FileName $MakeAppx -Arguments $commandLine -TimeoutSeconds 300
        if ($result.ExitCode -ne 0) { throw "MakeAppx bundle failed: $($result.Output)" }
        $inventory = @(Assert-WaveeStoreBundle $output $IdentityName $Publisher $Quad)
        foreach ($item in $inventory) {
            if ($item.Sha256 -ine (Get-FileHash -LiteralPath $seen[$item.Architecture] -Algorithm SHA256).Hash) { throw 'Bundling changed a release package.' }
        }
    } finally {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if ((Split-Path -Parent $resolvedStage) -ne $parent -or (Split-Path -Leaf $resolvedStage) -notlike 'bundle-input-*') { throw 'Unsafe bundle staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
    $OutFile
}

function Get-WaveeStorePackageEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Msix,
        [Parameter(Mandatory = $true)][string]$SymbolsDir,
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
        [Parameter(Mandatory = $true)][string]$IdentityName,
        [Parameter(Mandatory = $true)][string]$Publisher)
    Test-WaveeStoreQuad $Quad | Out-Null
    $id = Get-MsixIdentity $Msix
    Assert-WaveeStorePackageIdentity $id $IdentityName $Publisher $Quad
    if ("$($id.ProcessorArchitecture)" -ine $Architecture) { throw 'Adopted package architecture mismatch.' }
    $stamp = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $SymbolsDir 'SYMBOLS.txt'))) {
        if ($line -match '^([A-Za-z]+)=(.*)$') {
            if ($stamp.ContainsKey($Matches[1])) { throw 'Duplicate symbol stamp field.' }
            $stamp[$Matches[1]] = $Matches[2]
        }
    }
    if ($Commit -notmatch '^[a-fA-F0-9]{40}$' -or "$($stamp.commit)" -notmatch '^[a-fA-F0-9]{7,40}$' -or
        -not $Commit.StartsWith("$($stamp.commit)", [StringComparison]::OrdinalIgnoreCase)) { throw 'Symbols do not match the pinned release commit.' }
    if ($stamp.quad -ne $Quad -or $stamp.channel -ne 'store' -or $stamp.arch -ine $Architecture -or
        $stamp.rid -ine "win-$Architecture" -or $stamp.configuration -ne 'Release' -or $stamp.aot -ne 'True') { throw 'Symbols are not a matching Release AOT Store build.' }
    if ($stamp.exe -notmatch '^Wavee\.exe size=(\d+) sha256=([a-fA-F0-9]{64})$') { throw 'Missing executable hash in symbol stamp.' }
    $expectedSize = [long]$Matches[1]
    $expectedHash = $Matches[2]
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Msix).Path)
    try {
        $executables = @($zip.Entries | Where-Object { $_.FullName -ceq 'Wavee.exe' })
        if ($executables.Count -ne 1 -or $executables[0].Length -ne $expectedSize) { throw 'Packaged executable does not match symbol stamp size.' }
        $stream = $executables[0].Open()
        try { $exeHash = Get-WaveeStreamHash $stream } finally { $stream.Dispose() }
        if ($exeHash -ine $expectedHash) { throw 'Packaged executable hash does not match symbols.' }
        # Read only the bounded DOS/COFF headers from a fresh non-seekable ZIP entry stream.
        # A correct manifest and matching symbol stamp do not establish the native executable's ABI.
        $stream = $executables[0].Open()
        $reader = New-Object IO.BinaryReader($stream)
        try {
            $dos = $reader.ReadBytes(64)
            if ($dos.Length -ne 64 -or $dos[0] -ne 0x4d -or $dos[1] -ne 0x5a) { throw 'Packaged executable has no valid DOS header.' }
            $peOffset = [BitConverter]::ToUInt32($dos, 60)
            if ($peOffset -lt 64 -or $peOffset -gt 1048576 -or ([long]$peOffset + 24) -gt $executables[0].Length) {
                throw 'Packaged executable has an invalid or excessive PE header offset.'
            }
            $remaining = [int]$peOffset - 64
            while ($remaining -gt 0) {
                $chunk = $reader.ReadBytes([Math]::Min($remaining, 4096))
                if ($chunk.Length -eq 0) { throw 'Packaged executable PE header is truncated.' }
                $remaining -= $chunk.Length
            }
            if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Packaged executable has no valid PE signature.' }
            $machine = $reader.ReadUInt16()
            $expectedMachine = 0x8664
            if ($Architecture -ieq 'arm64') { $expectedMachine = 0xaa64 }
            if ($machine -ne $expectedMachine) { throw "Packaged executable PE machine does not match $Architecture." }
        } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
    $map = Join-Path $SymbolsDir 'Wavee.map.xml'
    $hasPlayPlay = $false
    foreach ($line in [IO.File]::ReadLines($map)) {
        if ($line.Contains('InProcessPlayPlayKeyDeriver')) { $hasPlayPlay = $true; break }
    }
    if (-not $hasPlayPlay) { throw 'Archived native map does not prove PlayPlay is included.' }
    [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $Msix).Path; Architecture = $Architecture
        Sha256 = (Get-FileHash -LiteralPath $Msix -Algorithm SHA256).Hash.ToLowerInvariant()
        ExeSha256 = $exeHash; Commit = $Commit; Quad = $Quad; PlayPlayIncluded = $true }
}

function New-WaveeMsixUpload {
    <#
    .SYNOPSIS
      Put exactly one verified dual-architecture .msixbundle into the .msixupload container.
    .DESCRIPTION
      Both nested application manifests are checked, not merely the bundle's Neutral identity. Two standalone
      MSIX files in one upload are not a substitute for a real architecture-aware bundle.

      ZipFile, not Compress-Archive: the 5.1 cmdlet re-encodes entry names and has a 2 GB ceiling (the same
      reasoning as the symbols zip in pack-wavee-msix.ps1). A pre-existing OutFile is deleted first so a resumed
      run never appends into a stale container.
    .OUTPUTS
      The OutFile path.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Bundle,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [Parameter(Mandatory = $true)][string]$IdentityName,
        [Parameter(Mandatory = $true)][string]$Publisher,
        [Parameter(Mandatory = $true)][string]$Quad)

    Test-WaveeStoreQuad $Quad | Out-Null
    Assert-WaveeStoreBundle $Bundle $IdentityName $Publisher $Quad | Out-Null
    if ([IO.Path]::GetExtension($OutFile) -ine '.msixupload') { throw 'Upload output must end in .msixupload.' }

    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($OutFile, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($p in @($Bundle)) {
            $name = [IO.Path]::GetFileName($p)
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path $p).Path, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    $OutFile
}

# ---------------------------------------------------------------------------------------------------------------
# msstore-cli output parsing
# ---------------------------------------------------------------------------------------------------------------

function Get-BalancedJsonPrefix {
    <#
      The leading balanced JSON object/array of a text that starts (after whitespace) with { or [ - found by a
      string-aware bracket walk, so trailing prose after the payload never reaches the parser. $null when the
      brackets never balance.

      Also repairs a msstore-cli/Spectre.Console defect confirmed against the live API (2026-09-01): when msstore's
      stdout is captured non-interactively - exactly how Invoke-MsStore reads it, and exactly what
      wavee-store-submit.ps1's submission-get/update round trip does - Spectre cannot query a real console width
      and falls back to wrapping at a fixed column, inserting a BARE newline wherever it breaks a line. JSON syntax
      never allows a raw, unescaped control character inside a string (a real line break in JSON text content is
      always the two-character escape \n); a literal CR/LF found while inString is therefore always wrap damage,
      never legitimate content. It is collapsed to a single space - the whitespace Spectre wrapped at - rather than
      left in place, which would otherwise round-trip as invalid syntax to the real API on `submission update`, or
      worse, silently splice a paragraph break into the middle of a sentence in the live Store listing. Private.
    #>
    param([string]$Text)

    $start = -1
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $c = $Text[$i]
        if ($c -eq '{' -or $c -eq '[') { $start = $i; break }
        if (-not [char]::IsWhiteSpace($c)) { return $null }
    }
    if ($start -lt 0) { return $null }

    $sb = New-Object System.Text.StringBuilder
    $depth = 0
    $inString = $false
    $escaped = $false
    $pendingSpace = $false
    for ($i = $start; $i -lt $Text.Length; $i++) {
        $c = $Text[$i]
        if ($inString -and ($c -eq "`r" -or $c -eq "`n")) { $pendingSpace = $true; continue }
        if ($inString -and $pendingSpace -and $c -eq ' ') { continue }
        if ($pendingSpace) {
            $alreadySpaced = ($sb.Length -gt 0 -and $sb[$sb.Length - 1] -eq ' ')
            if (-not $alreadySpaced) { [void]$sb.Append(' ') }
            $pendingSpace = $false
        }
        [void]$sb.Append($c)
        if ($inString) {
            if ($escaped) { $escaped = $false }
            elseif ($c -eq '\') { $escaped = $true }
            elseif ($c -eq '"') { $inString = $false }
            continue
        }
        if ($c -eq '"') { $inString = $true; continue }
        if ($c -eq '{' -or $c -eq '[') { $depth++; continue }
        if ($c -eq '}' -or $c -eq ']') {
            $depth--
            if ($depth -eq 0) { return $sb.ToString() }
        }
    }
    $null
}

function ConvertFrom-MsStoreJson {
    <#
    .SYNOPSIS
      msstore-cli wraps its JSON payload in notice and spinner prose - before AND (unlike gh) sometimes after it.
      Find the payload line-by-line the way ConvertFrom-GhJson does, cut it at the matching close bracket so
      trailing prose is dropped, and parse it. $null when the text carries no JSON at all.
    .DESCRIPTION
      A spinner line can itself start with '[' (progress markers), so a candidate that does not parse as JSON is
      skipped and the scan continues on the next line - prose is never mistaken for the payload.
    #>
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $lines = $Text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].TrimStart()
        if (-not ($t.StartsWith('{') -or $t.StartsWith('['))) { continue }
        $json = Get-BalancedJsonPrefix (($lines[$i..($lines.Count - 1)]) -join "`n")
        if ($null -eq $json) { continue }
        try { return ($json | ConvertFrom-Json) } catch { continue }
    }
    $null
}

# ---------------------------------------------------------------------------------------------------------------
# The submission body
# ---------------------------------------------------------------------------------------------------------------

function Get-StoreJsonProperty {
    <#
      The first property of $Object whose name matches any of $Name case-insensitively, as @{ Name; Value }, or
      $null when none is present. The submission API's casing is whatever msstore-cli returned that day, so every
      lookup in this module goes through here. Private.
    #>
    param($Object, [string[]]$Name)

    if ($null -eq $Object) { return $null }
    foreach ($n in $Name) {
        $hit = @($Object.PSObject.Properties | Where-Object { $_.Name -ieq $n })
        if ($hit.Count -gt 0 -and $null -ne $hit[0].Value) {
            return [pscustomobject]@{ Name = $hit[0].Name; Value = $hit[0].Value }
        }
    }
    $null
}

function Get-StoreReleaseNotesText {
    <#
    .SYNOPSIS
      Read the Store "What's new" text: UTF-8, trimmed, non-empty and inside Partner Center's 1500-character
      ReleaseNotes ceiling. Throws here rather than letting a too-long paste fail deep inside submission update.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) { throw "store release notes not found: $Path" }
    $t = [IO.File]::ReadAllText($Path).TrimStart([char]0xFEFF).Trim()
    if ($t.Length -eq 0) { throw "store release notes are empty: $Path" }
    if ($t.Length -gt 1500) { throw "store release notes are $($t.Length) characters; Partner Center caps ReleaseNotes at 1500: $Path" }
    $t
}

function Set-StoreSubmissionReleaseNotes {
    <#
    .SYNOPSIS
      Patch ONE field of a Store submission body: listings.<language>.baseListing.releaseNotes. Everything else is
      returned unchanged - `msstore submission update` sends the full body back, so any other difference would
      silently edit the live listing.
    .DESCRIPTION
      -Submission accepts the parsed object or the raw JSON text msstore printed. Property names and the language
      key are matched case-insensitively (Listings/listings, BaseListing/baseListing, en-us/en-US): the casing is
      the CLI's business, not ours. Throws when the language listing or its BaseListing is absent - release notes
      must land on the real listing, never on a block this function invented.
    .OUTPUTS
      The whole submission as compact JSON text, ready for `msstore submission update`.
    #>
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)][string]$ReleaseNotes,
        [string]$Language = 'en-us')

    $obj = $Submission
    if ($Submission -is [string]) { $obj = ConvertFrom-MsStoreJson $Submission }
    if ($null -eq $obj) { throw 'Set-StoreSubmissionReleaseNotes: no submission JSON to patch' }

    $listingsHit = Get-StoreJsonProperty $obj @('listings')
    if ($null -eq $listingsHit) { throw 'submission has no Listings block' }

    $langHit = Get-StoreJsonProperty $listingsHit.Value @($Language)
    if ($null -eq $langHit) {
        $have = (@($listingsHit.Value.PSObject.Properties | ForEach-Object { $_.Name }) -join ', ')
        throw "submission has no '$Language' listing (has: $have)"
    }

    $baseHit = Get-StoreJsonProperty $langHit.Value @('baseListing')
    if ($null -eq $baseHit) { throw "the '$($langHit.Name)' listing has no BaseListing" }

    $notesProp = @($baseHit.Value.PSObject.Properties | Where-Object { $_.Name -ieq 'releaseNotes' })
    if ($notesProp.Count -gt 0) { $notesProp[0].Value = $ReleaseNotes }
    else { $baseHit.Value | Add-Member -MemberType NoteProperty -Name 'releaseNotes' -Value $ReleaseNotes }

    ($obj | ConvertTo-Json -Depth 100 -Compress)
}

function Get-StoreSubmissionState {
    <#
    .SYNOPSIS
      ONE classification of `msstore submission status` (or `get`) output, shared by the pending-submission gate,
      -Status, -Abort and the poll loop, so nobody re-derives what "in flight" means.
    .DESCRIPTION
      The submission API's status taxonomy: None / Canceled / PendingCommit / CommitStarted / CommitFailed /
      PendingPublication / Publishing / Published / PublishFailed / PreProcessing / PreProcessingFailed /
      Certification / CertificationFailed / Release / ReleaseFailed.

        Pending  = an in-flight submission exists (PendingCommit through Release; anything not terminal)
        Terminal = Published / Canceled / None, or any *Failed
        Failed   = any *Failed
        Errors   = the flattened statusDetails.errors entries ("code: details"), when present

      Output that carries no submission at all (prose, or JSON without a status) classifies as None - terminal and
      not pending - because "there is nothing in flight" is a normal answer, not an error.
    .OUTPUTS
      pscustomobject @{ SubmissionId; Status; Pending; Terminal; Failed; Errors }
    #>
    param([string]$StatusJson)

    $o = ConvertFrom-MsStoreJson $StatusJson
    $id = $null
    $status = ''
    $idHit = Get-StoreJsonProperty $o @('id', 'submissionId')
    if ($null -ne $idHit) { $id = "$($idHit.Value)" }
    $statusHit = Get-StoreJsonProperty $o @('status')
    if ($null -ne $statusHit) { $status = "$($statusHit.Value)".Trim() }
    if (-not $status) { $status = 'None' }

    $failed = ($status -like '*Failed')
    $terminal = ($failed -or $status -eq 'Published' -or $status -eq 'Canceled' -or $status -eq 'None')

    $errors = @()
    $detailsHit = Get-StoreJsonProperty $o @('statusDetails')
    if ($null -ne $detailsHit) {
        $errsHit = Get-StoreJsonProperty $detailsHit.Value @('errors')
        if ($null -ne $errsHit) {
            foreach ($e in @($errsHit.Value)) {
                if ($null -eq $e) { continue }
                $code = ''
                $msg = ''
                $codeHit = Get-StoreJsonProperty $e @('code')
                if ($null -ne $codeHit) { $code = "$($codeHit.Value)" }
                $msgHit = Get-StoreJsonProperty $e @('details', 'message')
                if ($null -ne $msgHit) { $msg = "$($msgHit.Value)" }
                if ($code -and $msg) { $errors += "${code}: $msg" }
                elseif ($code) { $errors += $code }
                elseif ($msg) { $errors += $msg }
            }
        }
    }

    [pscustomobject]@{
        SubmissionId = $id
        Status       = $status
        Pending      = (-not $terminal)
        Terminal     = $terminal
        Failed       = $failed
        Errors       = $errors
    }
}

function Test-StoreAppIdentity {
    <#
    .SYNOPSIS
      Assert that an `msstore apps get` payload is OUR app: the Store product id, the package family name and the
      package identity name all match. The gate that stops a mistyped -ProductId from drafting a submission on
      someone else's listing.
    .DESCRIPTION
      msstore-cli is a preview tool, so each value is probed under every plausible property name,
      case-insensitively. A payload that carries NONE of the expected properties throws loudly (capture the real
      payload and extend the probes) rather than waving the gate through.
    .OUTPUTS
      A short detail string for the gate table. Throws on any mismatch.
    #>
    param(
        [Parameter(Mandatory = $true)]$AppJson,
        [Parameter(Mandatory = $true)][string]$ProductId,
        [Parameter(Mandatory = $true)][string]$IdentityName,
        [Parameter(Mandatory = $true)][string]$Pfn)

    $obj = $AppJson
    if ($AppJson -is [string]) { $obj = ConvertFrom-MsStoreJson $AppJson }
    if ($null -eq $obj) { throw 'Test-StoreAppIdentity: no app JSON to check' }

    $errs = @()
    $idHit = Get-StoreJsonProperty $obj @('id', 'storeId', 'bigId', 'productId')
    if ($null -eq $idHit) { $errs += 'payload carries no product id (looked for id/storeId/bigId/productId)' }
    elseif ("$($idHit.Value)" -ne $ProductId) { $errs += "$($idHit.Name) = '$($idHit.Value)' (want $ProductId)" }

    $pfnHit = Get-StoreJsonProperty $obj @('packageFamilyName', 'pfn', 'appxPackageFamilyName')
    if ($null -eq $pfnHit) { $errs += 'payload carries no package family name (looked for packageFamilyName/pfn/appxPackageFamilyName)' }
    elseif ("$($pfnHit.Value)" -ne $Pfn) { $errs += "$($pfnHit.Name) = '$($pfnHit.Value)' (want $Pfn)" }

    $nameHit = Get-StoreJsonProperty $obj @('packageIdentityName', 'identityName', 'packageIdentity')
    if ($null -eq $nameHit) { $errs += 'payload carries no package identity name (looked for packageIdentityName/identityName/packageIdentity)' }
    elseif ("$($nameHit.Value)" -ne $IdentityName) { $errs += "$($nameHit.Name) = '$($nameHit.Value)' (want $IdentityName)" }

    if ($errs.Count -gt 0) { throw ("store app identity mismatch:`n  " + ($errs -join "`n  ")) }
    "$($idHit.Name)=$($idHit.Value) $($pfnHit.Name)=$($pfnHit.Value) $($nameHit.Name)=$($nameHit.Value)"
}

# ---------------------------------------------------------------------------------------------------------------
# msstore-cli
# ---------------------------------------------------------------------------------------------------------------

function ConvertTo-Win32QuotedArgument {
    <#
      One argument, quoted exactly the way CommandLineToArgvW (and every .NET/native argv parser, including
      msstore-cli's) expects: N backslashes immediately before a '"' become 2N+1 backslashes then a literal '"'
      (the extra one escapes it); N backslashes at the very end (right before the closing quote this function
      adds) become 2N, so they cannot accidentally escape that closing quote; every other character is copied
      verbatim. Skips quoting entirely for an argument that needs none (no space/tab/quote) - both forms parse
      identically, but leaving simple tokens (ids, flags) unquoted keeps them exactly as every other caller
      already expects them. Verified round-trip byte-for-byte against a real external process (2026-09-01),
      including a JSON value that itself already contained an escaped quote. Private.
    #>
    param([string]$Argument)

    if ($Argument.Length -gt 0 -and $Argument.IndexOfAny(@(' ', "`t", '"')) -lt 0) { return $Argument }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $i = 0
    while ($i -lt $Argument.Length) {
        $backslashes = 0
        while ($i -lt $Argument.Length -and $Argument[$i] -eq '\') { $backslashes++; $i++ }
        if ($i -eq $Argument.Length) {
            [void]$sb.Append('\' * ($backslashes * 2))
        }
        elseif ($Argument[$i] -eq '"') {
            [void]$sb.Append('\' * ($backslashes * 2 + 1))
            [void]$sb.Append('"')
            $i++
        }
        else {
            [void]$sb.Append('\' * $backslashes)
            [void]$sb.Append($Argument[$i])
            $i++
        }
    }
    [void]$sb.Append('"')
    $sb.ToString()
}

function Invoke-CapturedNative {
    <#
      Start $FileName with a pre-built Win32 command line and capture stdout+stderr without deadlocking.
      Confirmed against a live `msstore publish -v` of a 61 MB .msixupload (2026-09-01): the previous
      sequential `$stdout = ReadToEnd(); $stderr = ReadToEnd()` wedges the child the moment verbose
      progress fills the ~4 KB stderr pipe, because this caller is still blocked on stdout. Both
      ReadToEndAsync calls MUST be in flight before WaitForExit. -TimeoutSeconds 0 waits forever.
      Returns @{ ExitCode; Output }. Private-ish - exported so the pipe-drain regression is testable
      without talking to msstore or the network.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [string]$Arguments = '',
        [int]$TimeoutSeconds = 0)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    try {
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()
        if ($TimeoutSeconds -gt 0) {
            if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
                try { $proc.Kill() } catch { }
                throw "$FileName timed out after ${TimeoutSeconds}s"
            }
            # .NET Framework: after the timeout overload returns true, drain async readers with the
            # parameterless WaitForExit so redirected output is not truncated.
            $proc.WaitForExit()
        }
        else {
            $proc.WaitForExit()
        }
        $stdout = $outTask.GetAwaiter().GetResult()
        $stderr = $errTask.GetAwaiter().GetResult()
        $text = (@($stdout, $stderr) | Where-Object { $_.Length -gt 0 }) -join "`n"
        @{ ExitCode = $proc.ExitCode; Output = $text }
    }
    finally {
        $proc.Dispose()
    }
}

function Invoke-MsStore {
    <#
    .SYNOPSIS
      Run msstore-cli (must be on PATH: winget install "Microsoft Store Developer CLI") and hand back its combined
      output text. Throws on a non-zero exit unless -AllowFailure - the Invoke-Gh idiom, one native tool per
      wrapper. Callers parse the text with ConvertFrom-MsStoreJson.
    .DESCRIPTION
      Invokes msstore.exe directly through System.Diagnostics.Process rather than PowerShell 5.1's `& exe @array`
      splat (the Invoke-Native idiom every OTHER wrapper in this repo safely uses). That path is provably broken
      for this module's one large, complex argument: confirmed against the live API (2026-09-01), it first
      silently stripped every '"' out of the JSON body `submission update` needs, and - after a first escaping
      attempt - broke a different way, splitting the same JSON on whitespace into dozens of bogus arguments.
      ConvertTo-Win32QuotedArgument builds the exact command line a correct argv parser expects; Process.Start
      with UseShellExecute=$false sends it unmodified. Both stdout and stderr are drained concurrently
      (Invoke-CapturedNative) - sequential ReadToEnd deadlocks `msstore publish -v` on the blob-upload
      progress bar.
    #>
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure,
        [int]$TimeoutSeconds = 0)

    $commandLine = ($Arguments | ForEach-Object { ConvertTo-Win32QuotedArgument $_ }) -join ' '
    $r = Invoke-CapturedNative -FileName 'msstore' -Arguments $commandLine -TimeoutSeconds $TimeoutSeconds
    if ($r.ExitCode -ne 0 -and -not $AllowFailure) {
        # Arguments can contain the full submission body; verbose output can contain SAS upload credentials.
        # Never put either in an exception that is printed to the console or stored in the release ledger.
        $command = @($Arguments | Select-Object -First 2 | ForEach-Object {
            if ($_ -match '^[a-zA-Z][a-zA-Z-]*$') { $_ } else { '[redacted]' }
        }) -join ' '
        throw "msstore $command failed (exit $($r.ExitCode)); sensitive arguments and output omitted."
    }
    $r.Output
}

Export-ModuleMember -Function @(
    'ConvertTo-WaveeStoreQuad',
    'Test-WaveeStoreQuad',
    'New-WaveeMsixUpload',
    'New-WaveeMsixBundle',
    'Assert-WaveeStoreBundle',
    'Get-WaveeStorePackageEvidence',
    'ConvertFrom-MsStoreJson',
    'Get-StoreReleaseNotesText',
    'Set-StoreSubmissionReleaseNotes',
    'Get-StoreSubmissionState',
    'Test-StoreAppIdentity',
    'Invoke-MsStore',
    'Invoke-CapturedNative',
    'ConvertTo-Win32QuotedArgument')
