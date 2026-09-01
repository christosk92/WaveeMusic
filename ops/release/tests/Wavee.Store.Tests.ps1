#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1 - Describe / Context / It / Should Be /
    Should Throw).  Run with:

        Invoke-Pester ops\release\tests

    These tests cover the Store-submission decisions that would otherwise only be discovered by a bad submission:
    the Store quad mapping and shape rules, the .msixupload container (against fake .msix zips carrying a real
    AppxManifest.xml), the msstore-cli JSON extraction, the release-notes rules, the one-field submission patch,
    and the submission-status classification.

    They must never touch the network or run msstore: everything under test is a pure function, and every file the
    tests write goes into a per-run temp folder.

    fixtures\store-submission.sample.json is the REAL Submission 1 - `msstore submission get 9NJPVWTQPT9H`, captured
    2026-09-01 once the app went live, and repaired for a defect discovered during that capture: when msstore-cli's
    stdout is redirected to a file (never an interactive terminal), Spectre.Console still wraps long lines at ~80
    columns and, because it does not escape the break, the wrap lands as a literal, unescaped newline inside JSON
    string values (JSON grammar never allows a raw control character inside a string - this is not the same issue
    as microsoft/msstore-cli#15, which is about message/JSON interleaving). Left alone, that corruption round-trips
    as invalid syntax to the real API on `submission update`, or worse, splices a paragraph break into the middle
    of a sentence in the live Store listing. Get-BalancedJsonPrefix now repairs it (collapses the bare break back to
    the single space Spectre wrapped at) before the payload ever reaches ConvertFrom-Json - see its own comment and
    the "repairs a Spectre.Console line-wrap" tests below. The fixture is this repaired capture, reformatted to
    2-space indent; every string value in it survived exactly this pipeline, so it doubles as a regression check for
    the repair as well as a real-shaped sample. Its ReleaseNotes is genuinely empty (Submission 1 was authored by
    hand in Partner Center with no "What's new" text set).
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path

# The store module imports the release module and (LAST, so its exports survive the nested -Force import) the
# build module, both -Global - importing it alone puts all three in scope.
Import-Module (Join-Path $repoRoot 'ops\release\Wavee.Store.psm1') -Force -DisableNameChecking

$script:TmpRoot = Join-Path $env:TEMP ('wavee-store-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:TmpRoot | Out-Null

$script:FixturePath = Join-Path $here 'fixtures\store-submission.sample.json'
$script:StoreIdentityName = 'cproducts.Wavee'
$script:StorePublisher = 'CN=88D90E00-BEC4-41D6-8623-9F49F1AE2E9E'

function New-TmpDir {
    param([string]$Name)
    $p = Join-Path $script:TmpRoot $Name
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    $p
}

function New-FakeMsix {
    <#  A zip whose only entry is an AppxManifest.xml in the exact shape Get-MsixIdentity parses
        ($x.Package.Identity with Name/Publisher/Version/ProcessorArchitecture attributes). #>
    param(
        [string]$Path,
        [string]$Name = 'cproducts.Wavee',
        [string]$Publisher = 'CN=88D90E00-BEC4-41D6-8623-9F49F1AE2E9E',
        [string]$Version = '1.2.102.0',
        [string]$Arch = 'x64')

    $xml = '<?xml version="1.0" encoding="utf-8"?>' + "`n" +
        '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">' + "`n" +
        "  <Identity Name=`"$Name`" Publisher=`"$Publisher`" Version=`"$Version`" ProcessorArchitecture=`"$Arch`" />" + "`n" +
        '</Package>' + "`n"

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $Path) { Remove-Item $Path -Force }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('AppxManifest.xml')
        $sw = New-Object IO.StreamWriter($entry.Open())
        try { $sw.Write($xml) } finally { $sw.Dispose() }
    }
    finally { $zip.Dispose() }
    $Path
}

function Get-ZipEntryNames {
    param([string]$Path)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object) }
    finally { $zip.Dispose() }
}

function New-StatusJson {
    param([string]$Status, [string]$Id = '1152921505699999999', $Errors = @())
    ([pscustomobject]@{
        id            = $Id
        status        = $Status
        statusDetails = [pscustomobject]@{ errors = @($Errors); warnings = @(); certificationReports = @() }
    } | ConvertTo-Json -Depth 10)
}

# ===================================================================================================================

Describe 'ConvertTo-WaveeStoreQuad' {

    It 'folds the build into the 3rd part and lifts the major (0.2.1 build 2 -> 1.2.102.0)' {
        ConvertTo-WaveeStoreQuad '0.2.1' 2 | Should Be '1.2.102.0'
    }

    It 'maps a zero patch to the bare build counter (0.2.0 build 17 -> 1.2.17.0)' {
        ConvertTo-WaveeStoreQuad '0.2.0' 17 | Should Be '1.2.17.0'
    }

    It 'lifts a nonzero major too' {
        ConvertTo-WaveeStoreQuad '1.0.0' 1 | Should Be '2.0.1.0'
    }

    It 'agrees with StoreVersion.Quad on build 0' {
        ConvertTo-WaveeStoreQuad '0.2.1' 0 | Should Be '1.2.100.0'
    }

    It 'allows a large MINOR - only the patch is folded' {
        ConvertTo-WaveeStoreQuad '0.655.36' 1 | Should Be '1.655.3601.0'
    }

    It 'rejects the fold overflowing the 3rd part (patch 655, build 36 -> 65536)' {
        { ConvertTo-WaveeStoreQuad '0.0.655' 36 } | Should Throw
    }

    It 'still allows the 3rd part at exactly 65535' {
        ConvertTo-WaveeStoreQuad '0.0.655' 35 | Should Be '1.0.65535.0'
    }

    It 'rejects a beta semver (the Store channel ships stable only)' {
        { ConvertTo-WaveeStoreQuad '0.4.0-beta.2' 3 } | Should Throw
    }

    It 'rejects a negative build counter' {
        { ConvertTo-WaveeStoreQuad '0.2.1' (-1) } | Should Throw
    }

    It 'rejects a bad semver' {
        { ConvertTo-WaveeStoreQuad '0.2' 1 } | Should Throw
    }

    It 'produces something Test-WaveeStoreQuad accepts' {
        Test-WaveeStoreQuad (ConvertTo-WaveeStoreQuad '0.2.1' 2) | Should Be '1.2.102.0'
    }
}

# ===================================================================================================================

Describe 'Test-WaveeStoreQuad' {

    It 'accepts a store-shaped quad and returns it' {
        Test-WaveeStoreQuad '1.2.102.0' | Should Be '1.2.102.0'
    }

    It 'accepts the maximum part values' {
        Test-WaveeStoreQuad '65535.65535.65535.0' | Should Be '65535.65535.65535.0'
    }

    It 'rejects a major of 0 (the Store refuses it)' {
        { Test-WaveeStoreQuad '0.2.102.0' } | Should Throw
    }

    It 'rejects a nonzero 4th part (the Store owns it)' {
        { Test-WaveeStoreQuad '1.2.102.5' } | Should Throw
    }

    It 'rejects three parts' {
        { Test-WaveeStoreQuad '1.2.102' } | Should Throw
    }

    It 'rejects five parts' {
        { Test-WaveeStoreQuad '1.2.102.0.0' } | Should Throw
    }

    It 'rejects a part above 65535' {
        { Test-WaveeStoreQuad '1.2.65536.0' } | Should Throw
    }

    It 'rejects non-numeric parts' {
        { Test-WaveeStoreQuad 'a.b.c.d' } | Should Throw
    }

    It 'rejects an empty string' {
        { Test-WaveeStoreQuad '' } | Should Throw
    }
}

# ===================================================================================================================

Describe 'New-WaveeMsixUpload' {

    $dir = New-TmpDir 'msixupload'
    $x64 = New-FakeMsix -Path (Join-Path $dir 'Wavee_1.2.102.0_x64.msix') -Arch 'x64'
    $arm64 = New-FakeMsix -Path (Join-Path $dir 'Wavee_1.2.102.0_arm64.msix') -Arch 'arm64'
    $out = Join-Path $dir 'Wavee_1.2.102.0_store.msixupload'

    It 'zips two verified packages into one container' {
        New-WaveeMsixUpload -Msix @($x64, $arm64) -OutFile $out -IdentityName $script:StoreIdentityName `
            -Publisher $script:StorePublisher -Quad '1.2.102.0' | Should Be $out
        Test-Path $out | Should Be $true
    }

    It 'stores exactly the two entries, flat at the root, under their original file names' {
        $names = Get-ZipEntryNames $out
        $names.Count | Should Be 2
        $names[0] | Should Be 'Wavee_1.2.102.0_arm64.msix'
        $names[1] | Should Be 'Wavee_1.2.102.0_x64.msix'
        @($names | Where-Object { $_ -match '[/\\]' }).Count | Should Be 0
    }

    It 'replaces a pre-existing container instead of appending into it' {
        New-WaveeMsixUpload -Msix @($x64) -OutFile $out -IdentityName $script:StoreIdentityName `
            -Publisher $script:StorePublisher -Quad '1.2.102.0' | Out-Null
        (Get-ZipEntryNames $out).Count | Should Be 1
    }

    It 'rejects a version that does not match the quad' {
        $wrongVer = New-FakeMsix -Path (Join-Path $dir 'wrong-version.msix') -Arch 'arm64' -Version '1.2.103.0'
        { New-WaveeMsixUpload -Msix @($x64, $wrongVer) -OutFile (Join-Path $dir 'never1.msixupload') `
                -IdentityName $script:StoreIdentityName -Publisher $script:StorePublisher -Quad '1.2.102.0' } | Should Throw
    }

    It 'rejects a duplicate architecture' {
        $alsoX64 = New-FakeMsix -Path (Join-Path $dir 'also-x64.msix') -Arch 'x64'
        { New-WaveeMsixUpload -Msix @($x64, $alsoX64) -OutFile (Join-Path $dir 'never2.msixupload') `
                -IdentityName $script:StoreIdentityName -Publisher $script:StorePublisher -Quad '1.2.102.0' } | Should Throw
    }

    It 'rejects the wrong Publisher (a Trusted-Signing-subject package is not a Store package)' {
        $wrongPub = New-FakeMsix -Path (Join-Path $dir 'wrong-pub.msix') -Arch 'arm64' `
            -Publisher 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL'
        { New-WaveeMsixUpload -Msix @($x64, $wrongPub) -OutFile (Join-Path $dir 'never3.msixupload') `
                -IdentityName $script:StoreIdentityName -Publisher $script:StorePublisher -Quad '1.2.102.0' } | Should Throw
    }

    It 'rejects the wrong identity name' {
        $wrongName = New-FakeMsix -Path (Join-Path $dir 'wrong-name.msix') -Arch 'arm64' -Name 'someone.Else'
        { New-WaveeMsixUpload -Msix @($wrongName) -OutFile (Join-Path $dir 'never4.msixupload') `
                -IdentityName $script:StoreIdentityName -Publisher $script:StorePublisher -Quad '1.2.102.0' } | Should Throw
    }

    It 'rejects a non-store quad before touching any package' {
        { New-WaveeMsixUpload -Msix @($x64) -OutFile (Join-Path $dir 'never5.msixupload') `
                -IdentityName $script:StoreIdentityName -Publisher $script:StorePublisher -Quad '0.2.0.17' } | Should Throw
    }

    It 'rejects a missing input file' {
        { New-WaveeMsixUpload -Msix @((Join-Path $dir 'ghost.msix')) -OutFile (Join-Path $dir 'never6.msixupload') `
                -IdentityName $script:StoreIdentityName -Publisher $script:StorePublisher -Quad '1.2.102.0' } | Should Throw
    }
}

# ===================================================================================================================

Describe 'ConvertFrom-MsStoreJson' {

    It 'parses a payload behind a notice line' {
        $o = ConvertFrom-MsStoreJson ("Welcome to msstore-cli!`n" + '{"id": "123", "status": "PendingCommit"}')
        $o.id | Should Be '123'
        $o.status | Should Be 'PendingCommit'
    }

    It 'parses a payload with trailing prose after it' {
        $o = ConvertFrom-MsStoreJson ('{"id": "123"}' + "`nDone. Run msstore submission status to poll.")
        $o.id | Should Be '123'
    }

    It 'skips a spinner line that starts with a bracket' {
        $o = ConvertFrom-MsStoreJson ("[1/4] Uploading packages...`n" + '{"id": "456"}')
        $o.id | Should Be '456'
    }

    It 'parses an array payload' {
        $o = ConvertFrom-MsStoreJson ("notice`n" + '[{"id": "1"}, {"id": "2"}]')
        @($o).Count | Should Be 2
        @($o)[0].id | Should Be '1'
    }

    It 'parses a multi-line (pretty-printed) payload' {
        $o = ConvertFrom-MsStoreJson ("notice`n{`n  `"id`": `"789`",`n  `"status`": `"Published`"`n}`ntrailing")
        $o.status | Should Be 'Published'
    }

    It 'returns null for pure prose' {
        $o = ConvertFrom-MsStoreJson 'Could not find the submission. Please check the product id.'
        ($null -eq $o) | Should Be $true
    }

    It 'returns null for an empty string' {
        ($null -eq (ConvertFrom-MsStoreJson '')) | Should Be $true
    }

    It 'returns null when the brackets never balance' {
        ($null -eq (ConvertFrom-MsStoreJson '{"id": "truncated...')) | Should Be $true
    }

    It 'is not fooled by brackets inside JSON strings' {
        $o = ConvertFrom-MsStoreJson '{"note": "a } inside a string"}'
        $o.note | Should Be 'a } inside a string'
    }

    It 'repairs a Spectre.Console line-wrap that left a bare newline inside a string value' {
        # Real captured msstore output: word-wrap breaks a long field at ~80 columns and leaves a raw LF where a
        # real terminal would have just wrapped the display, never touching the underlying text. JSON grammar
        # never allows a raw control character inside a string, so this has to be repaired, not merely tolerated.
        $wrapped = "{`"description`": `"a long sentence that got wrapped`nright in the middle of it`"}"
        $o = ConvertFrom-MsStoreJson $wrapped
        $o.description | Should Be 'a long sentence that got wrapped right in the middle of it'
    }

    It 'collapses a wrap newline even when a space already sits on either side of it' {
        $wrapped = "{`"description`": `"one `nline`"}"
        $o = ConvertFrom-MsStoreJson $wrapped
        $o.description | Should Be 'one line'
    }

    It 'repairs a wrapped payload so re-serializing it leaves no raw CR/LF byte anywhere in the JSON' {
        $wrapped = "{`"a`": `"first part`nsecond part`", `"b`": `"third`r`nfourth`"}"
        $o = ConvertFrom-MsStoreJson $wrapped
        $compact = $o | ConvertTo-Json -Compress
        $rawBreaks = @($compact.ToCharArray() | Where-Object { $_ -eq [char]10 -or $_ -eq [char]13 })
        $rawBreaks.Count | Should Be 0
    }

    It 'leaves a real between-token newline (ordinary pretty-printing) alone' {
        $o = ConvertFrom-MsStoreJson "{`n  `"id`": `"1`",`n  `"note`": `"unaffected`"`n}"
        $o.id | Should Be '1'
        $o.note | Should Be 'unaffected'
    }
}

# ===================================================================================================================

Describe 'Get-StoreReleaseNotesText' {

    $dir = New-TmpDir 'notes'

    It 'reads and trims the text' {
        $p = Join-Path $dir 'ok.txt'
        [IO.File]::WriteAllText($p, "  What is new in 0.2.1: faster search.  `n")
        Get-StoreReleaseNotesText $p | Should Be 'What is new in 0.2.1: faster search.'
    }

    It 'throws on a missing file' {
        { Get-StoreReleaseNotesText (Join-Path $dir 'missing.txt') } | Should Throw
    }

    It 'throws on an empty file' {
        $p = Join-Path $dir 'empty.txt'
        [IO.File]::WriteAllText($p, '')
        { Get-StoreReleaseNotesText $p } | Should Throw
    }

    It 'throws on whitespace only' {
        $p = Join-Path $dir 'blank.txt'
        [IO.File]::WriteAllText($p, "   `n`n  ")
        { Get-StoreReleaseNotesText $p } | Should Throw
    }

    It 'accepts exactly 1500 characters' {
        $p = Join-Path $dir 'max.txt'
        [IO.File]::WriteAllText($p, ('a' * 1500))
        (Get-StoreReleaseNotesText $p).Length | Should Be 1500
    }

    It 'throws on 1501 characters (Partner Center caps ReleaseNotes at 1500)' {
        $p = Join-Path $dir 'over.txt'
        [IO.File]::WriteAllText($p, ('a' * 1501))
        { Get-StoreReleaseNotesText $p } | Should Throw
    }
}

# ===================================================================================================================

Describe 'Set-StoreSubmissionReleaseNotes' {

    $fixtureText = [IO.File]::ReadAllText($script:FixturePath)
    # Submission 1 was authored by hand in Partner Center with no "What's new" text set, so the real capture's
    # ReleaseNotes is genuinely empty - the round-trip/patch tests below don't care what the original value was,
    # only that patching changes nothing else.
    $originalNotes = ''
    $newNotes = 'What is new in 0.2.2: the sidebar customizer.'

    It 'the fixture parses and carries the expected original notes' {
        $o = $fixtureText | ConvertFrom-Json
        $o.listings.'en-us'.baseListing.releaseNotes | Should Be $originalNotes
    }

    It 'patches the en-us release notes from JSON text input' {
        $after = Set-StoreSubmissionReleaseNotes -Submission $fixtureText -ReleaseNotes $newNotes
        ($after | ConvertFrom-Json).listings.'en-us'.baseListing.releaseNotes | Should Be $newNotes
    }

    It 'returns compact JSON text' {
        $after = Set-StoreSubmissionReleaseNotes -Submission $fixtureText -ReleaseNotes $newNotes
        ($after -is [string]) | Should Be $true
        ($after -match "`n") | Should Be $false
    }

    It 'changes NOTHING but the release notes (round-trip compare)' {
        $before = $fixtureText | ConvertFrom-Json
        $after = (Set-StoreSubmissionReleaseNotes -Submission $fixtureText -ReleaseNotes $newNotes) | ConvertFrom-Json
        $after.listings.'en-us'.baseListing.releaseNotes = $originalNotes
        ($after | ConvertTo-Json -Depth 100 -Compress) | Should Be ($before | ConvertTo-Json -Depth 100 -Compress)
    }

    It 'finds the listing case-insensitively (en-US asked, en-us in the body)' {
        $after = Set-StoreSubmissionReleaseNotes -Submission $fixtureText -ReleaseNotes $newNotes -Language 'en-US'
        ($after | ConvertFrom-Json).listings.'en-us'.baseListing.releaseNotes | Should Be $newNotes
    }

    It 'accepts an already-parsed submission object' {
        $obj = $fixtureText | ConvertFrom-Json
        $after = Set-StoreSubmissionReleaseNotes -Submission $obj -ReleaseNotes $newNotes
        ($after | ConvertFrom-Json).listings.'en-us'.baseListing.releaseNotes | Should Be $newNotes
    }

    It 'survives msstore prose around the JSON text' {
        $wrapped = "Fetching submission...`n" + $fixtureText + "`nDone."
        $after = Set-StoreSubmissionReleaseNotes -Submission $wrapped -ReleaseNotes $newNotes
        ($after | ConvertFrom-Json).id | Should Be '1152921505701771860'
    }

    It 'throws when the language listing is missing' {
        { Set-StoreSubmissionReleaseNotes -Submission '{"listings": {"de-de": {"baseListing": {}}}}' `
                -ReleaseNotes $newNotes } | Should Throw
    }

    It 'throws when the listing has no BaseListing' {
        { Set-StoreSubmissionReleaseNotes -Submission '{"listings": {"en-us": {"platformOverrides": {}}}}' `
                -ReleaseNotes $newNotes } | Should Throw
    }

    It 'throws when there is no Listings block at all' {
        { Set-StoreSubmissionReleaseNotes -Submission '{"id": "123"}' -ReleaseNotes $newNotes } | Should Throw
    }
}

# ===================================================================================================================

Describe 'Get-StoreSubmissionState' {

    It 'classifies CommitStarted as pending' {
        $s = Get-StoreSubmissionState (New-StatusJson 'CommitStarted')
        $s.Pending | Should Be $true
        $s.Terminal | Should Be $false
        $s.Failed | Should Be $false
        $s.Status | Should Be 'CommitStarted'
        $s.SubmissionId | Should Be '1152921505699999999'
    }

    It 'classifies PreProcessing as pending' {
        (Get-StoreSubmissionState (New-StatusJson 'PreProcessing')).Pending | Should Be $true
    }

    It 'classifies Certification as pending' {
        (Get-StoreSubmissionState (New-StatusJson 'Certification')).Pending | Should Be $true
    }

    It 'classifies Published as terminal and not failed' {
        $s = Get-StoreSubmissionState (New-StatusJson 'Published')
        $s.Pending | Should Be $false
        $s.Terminal | Should Be $true
        $s.Failed | Should Be $false
    }

    It 'classifies Canceled as terminal' {
        (Get-StoreSubmissionState (New-StatusJson 'Canceled')).Terminal | Should Be $true
    }

    It 'classifies CertificationFailed as failed and surfaces the error messages' {
        $errs = @([pscustomobject]@{ code = 'CertificationFailed'; details = 'The package failed the WACK run.' })
        $s = Get-StoreSubmissionState (New-StatusJson 'CertificationFailed' -Errors $errs)
        $s.Failed | Should Be $true
        $s.Terminal | Should Be $true
        $s.Pending | Should Be $false
        @($s.Errors).Count | Should Be 1
        @($s.Errors)[0] | Should Be 'CertificationFailed: The package failed the WACK run.'
    }

    It 'classifies every *Failed status as failed' {
        foreach ($st in @('CommitFailed', 'PublishFailed', 'PreProcessingFailed', 'ReleaseFailed')) {
            (Get-StoreSubmissionState (New-StatusJson $st)).Failed | Should Be $true
        }
    }

    It 'classifies prose (no submission) as not pending' {
        $s = Get-StoreSubmissionState 'Could not find an in-progress submission.'
        $s.Pending | Should Be $false
        $s.Terminal | Should Be $true
        $s.Status | Should Be 'None'
        ($null -eq $s.SubmissionId) | Should Be $true
    }

    It 'classifies empty output as not pending' {
        (Get-StoreSubmissionState '').Pending | Should Be $false
    }

    It 'classifies JSON without a status as not pending' {
        (Get-StoreSubmissionState '{"message": "no submission"}').Pending | Should Be $false
    }

    It 'still reads the fixture (a real, published submission) as terminal and not failed' {
        # The fixture is the real Submission 1 as it stands today: already Published, not a pending draft.
        $s = Get-StoreSubmissionState ([IO.File]::ReadAllText($script:FixturePath))
        $s.Pending | Should Be $false
        $s.Terminal | Should Be $true
        $s.Failed | Should Be $false
        $s.Status | Should Be 'Published'
        $s.SubmissionId | Should Be '1152921505701771860'
    }
}

# ===================================================================================================================

Describe 'Test-StoreAppIdentity' {

    $app = [pscustomobject]@{
        id                  = '9NJPVWTQPT9H'
        packageFamilyName   = 'cproducts.Wavee_abcdef123456'
        packageIdentityName = 'cproducts.Wavee'
    }

    It 'passes on a matching payload and returns a detail string' {
        $detail = Test-StoreAppIdentity -AppJson $app -ProductId '9NJPVWTQPT9H' `
            -IdentityName 'cproducts.Wavee' -Pfn 'cproducts.Wavee_abcdef123456'
        ($detail -like '*9NJPVWTQPT9H*') | Should Be $true
    }

    It 'accepts JSON text with prose around it' {
        $text = "notice`n" + ($app | ConvertTo-Json)
        $detail = Test-StoreAppIdentity -AppJson $text -ProductId '9NJPVWTQPT9H' `
            -IdentityName 'cproducts.Wavee' -Pfn 'cproducts.Wavee_abcdef123456'
        ($detail -like '*cproducts.Wavee*') | Should Be $true
    }

    It 'matches property names case-insensitively' {
        $pascal = [pscustomobject]@{
            Id                  = '9NJPVWTQPT9H'
            PackageFamilyName   = 'cproducts.Wavee_abcdef123456'
            PackageIdentityName = 'cproducts.Wavee'
        }
        $detail = Test-StoreAppIdentity -AppJson $pascal -ProductId '9NJPVWTQPT9H' `
            -IdentityName 'cproducts.Wavee' -Pfn 'cproducts.Wavee_abcdef123456'
        ($detail -like '*9NJPVWTQPT9H*') | Should Be $true
    }

    It 'throws on the wrong product id' {
        { Test-StoreAppIdentity -AppJson $app -ProductId '9NOTOURAPP00' `
                -IdentityName 'cproducts.Wavee' -Pfn 'cproducts.Wavee_abcdef123456' } | Should Throw
    }

    It 'throws on the wrong package family name' {
        { Test-StoreAppIdentity -AppJson $app -ProductId '9NJPVWTQPT9H' `
                -IdentityName 'cproducts.Wavee' -Pfn 'someone.Else_000000000000' } | Should Throw
    }

    It 'throws on the wrong identity name' {
        { Test-StoreAppIdentity -AppJson $app -ProductId '9NJPVWTQPT9H' `
                -IdentityName 'someone.Else' -Pfn 'cproducts.Wavee_abcdef123456' } | Should Throw
    }

    It 'throws loudly when the payload carries none of the expected properties' {
        { Test-StoreAppIdentity -AppJson '{"unrelated": true}' -ProductId '9NJPVWTQPT9H' `
                -IdentityName 'cproducts.Wavee' -Pfn 'cproducts.Wavee_abcdef123456' } | Should Throw
    }

    It 'throws on pure prose' {
        { Test-StoreAppIdentity -AppJson 'Sign in to Partner Center first.' -ProductId '9NJPVWTQPT9H' `
                -IdentityName 'cproducts.Wavee' -Pfn 'cproducts.Wavee_abcdef123456' } | Should Throw
    }
}
