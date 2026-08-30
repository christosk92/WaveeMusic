#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1 - Describe / Context / It / Should Be /
    Should Throw / Mock -ModuleName).  Run with:

        Invoke-Pester ops\release\tests

    These tests cover the decisions that would otherwise only be discovered by a bad release: the semver and quad
    rules, the feed monotonic gate, .appinstaller substitution against the REAL template, the staging manifest, and
    the two functions that rewrite Wavee.Version.props.

    They must never touch the network or git: Get-WaveeFeedVersion is mocked, and every file the tests write goes
    into a per-run temp folder.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path

Import-Module (Join-Path $repoRoot 'ops\release\Wavee.Release.psm1') -Force -DisableNameChecking
# Build module LAST: the release module's nested -Force import would otherwise unload these exports from the test scope.
Import-Module (Join-Path $repoRoot 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking

$script:TmpRoot = Join-Path $env:TEMP ('wavee-release-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:TmpRoot | Out-Null

function New-TmpDir {
    param([string]$Name)
    $p = Join-Path $script:TmpRoot $Name
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    $p
}

function Get-FileBytes {
    param([string]$Path)
    [IO.File]::ReadAllBytes($Path)
}

# ===================================================================================================================

Describe 'Test-WaveeSemver' {

    It 'parses a stable release' {
        $s = Test-WaveeSemver '0.2.0'
        $s.Major | Should Be 0
        $s.Minor | Should Be 2
        $s.Patch | Should Be 0
        $s.Core | Should Be '0.2.0'
        $s.Channel | Should Be 'stable'
        ($null -eq $s.Beta) | Should Be $true
    }

    It 'parses a multi-digit version' {
        $s = Test-WaveeSemver '12.345.6789'
        $s.Major | Should Be 12
        $s.Minor | Should Be 345
        $s.Patch | Should Be 6789
        $s.Core | Should Be '12.345.6789'
    }

    It 'parses a beta and reports the beta channel' {
        $s = Test-WaveeSemver '0.4.0-beta.2'
        $s.Beta | Should Be 2
        $s.Channel | Should Be 'beta'
        $s.Core | Should Be '0.4.0'
    }

    It 'rejects a two-part version' {
        { Test-WaveeSemver '0.2' } | Should Throw
    }

    It 'rejects a four-part version' {
        { Test-WaveeSemver '0.2.0.1' } | Should Throw
    }

    It 'rejects a leading v' {
        { Test-WaveeSemver 'v0.2.0' } | Should Throw
    }

    It 'rejects a prerelease that is not -beta.N' {
        { Test-WaveeSemver '0.2.0-rc.1' } | Should Throw
    }

    It 'rejects -beta.0 (beta numbering starts at 1)' {
        { Test-WaveeSemver '0.2.0-beta.0' } | Should Throw
    }

    It 'rejects build metadata' {
        { Test-WaveeSemver '0.2.0+build.7' } | Should Throw
    }

    It 'rejects an empty string' {
        { Test-WaveeSemver '' } | Should Throw
    }
}

# ===================================================================================================================

Describe 'ConvertTo-WaveeQuad' {

    It 'appends the build counter to a stable semver' {
        ConvertTo-WaveeQuad '0.2.0' 17 | Should Be '0.2.0.17'
    }

    It 'strips the beta suffix (MSIX has no prerelease concept)' {
        ConvertTo-WaveeQuad '0.4.0-beta.2' 3 | Should Be '0.4.0.3'
    }

    It 'allows build 0' {
        ConvertTo-WaveeQuad '1.0.0' 0 | Should Be '1.0.0.0'
    }

    It 'allows the maximum part value' {
        ConvertTo-WaveeQuad '65535.65535.65535' 65535 | Should Be '65535.65535.65535.65535'
    }

    It 'rejects a build counter above 65535' {
        { ConvertTo-WaveeQuad '0.2.0' 65536 } | Should Throw
    }

    It 'rejects a semver part above 65535' {
        { ConvertTo-WaveeQuad '70000.0.0' 1 } | Should Throw
    }

    It 'rejects a bad semver' {
        { ConvertTo-WaveeQuad '0.2' 1 } | Should Throw
    }

    It 'produces something [version] accepts' {
        [version](ConvertTo-WaveeQuad '0.2.0' 17) | Should Be ([version]'0.2.0.17')
    }
}

# ===================================================================================================================

Describe 'Test-FeedMonotonic' {

    Context 'when the feed does not exist yet (404)' {
        Mock -ModuleName Wavee.Release Get-WaveeFeedVersion { return $null }

        It 'passes and reports a null current version' {
            $rows = Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.2.0.1' '0.2.0' @('arm64', 'x64')
            $rows.Count | Should Be 2
            ($null -eq $rows[0].Current) | Should Be $true
            "$($rows[0].New)" | Should Be '0.2.0.1'
        }
    }

    Context 'when the feed is behind' {
        Mock -ModuleName Wavee.Release Get-WaveeFeedVersion { return [version]'0.2.0.5' }

        It 'passes for a higher build of the same core' {
            $rows = Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.2.0.6' '0.2.0' @('arm64')
            "$($rows[0].Current)" | Should Be '0.2.0.5'
        }

        It 'passes for a higher core' {
            $rows = Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.3.0.6' '0.3.0' @('arm64')
            $rows.Count | Should Be 1
        }

        It 'covers every feed and every architecture' {
            $rows = Test-FeedMonotonic 'owner/repo' @('wavee-stable', 'wavee-beta') '0.2.0.6' '0.2.0' @('arm64', 'x64')
            $rows.Count | Should Be 4
        }
    }

    Context 'when the feed is ahead' {
        Mock -ModuleName Wavee.Release Get-WaveeFeedVersion { return [version]'0.3.0.9' }

        It 'fails on a lower quad' {
            { Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.3.0.8' '0.3.0' @('arm64') } | Should Throw
        }

        It 'fails on a lower semver core' {
            { Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.2.9.10' '0.2.9' @('arm64') } | Should Throw
        }

        It 'fails on an equal quad (a republish is not an update)' {
            { Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.3.0.9' '0.3.0' @('arm64') } | Should Throw
        }

        It 'names the offending feed and architecture' {
            $msg = ''
            try { Test-FeedMonotonic 'owner/repo' @('wavee-stable') '0.3.0.9' '0.3.0' @('x64') }
            catch { $msg = $_.Exception.Message }
            $msg -like '*wavee-stable/x64*' | Should Be $true
        }
    }
}

# ===================================================================================================================

Describe 'New-WaveeAppInstaller' {

    $template = Join-Path $repoRoot 'ops\build\Wavee.AppInstaller.template.xml'
    $dir = New-TmpDir 'appinstaller'
    $publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL'
    $feedUri = 'https://github.com/owner/repo/releases/download/wavee-stable/Wavee.arm64.appinstaller'
    $msixUri = 'https://github.com/owner/repo/releases/download/wavee-v0.2.0/Wavee_0.2.0.17_arm64.msix'
    $out = Join-Path $dir 'Wavee.arm64.appinstaller'

    It 'renders without throwing' {
        New-WaveeAppInstaller -Template $template -OutFile $out -Arch 'arm64' -Quad '0.2.0.17' `
            -Publisher $publisher -IdentityName 'cproducts.Wavee' -FeedUri $feedUri -MsixUri $msixUri | Out-Null
        Test-Path $out | Should Be $true
    }

    It 'leaves no __PLACEHOLDER__ behind' {
        ([IO.File]::ReadAllText($out) -match '__[A-Z0-9_]+__') | Should Be $false
    }

    It 'writes UTF-8 without a BOM' {
        $bytes = Get-FileBytes $out
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should Be $false
    }

    It 'sets the root Version, which is the only number the OS and the app compare' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.Version)" | Should Be '0.2.0.17'
    }

    It 'sets the root Uri to the rolling feed, not to this release' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.Uri)" | Should Be $feedUri
    }

    It 'sets MainPackage/@Name to the package identity' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.MainPackage.Name)" | Should Be 'cproducts.Wavee'
    }

    It 'sets MainPackage/@Publisher to the signing subject' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.MainPackage.Publisher)" | Should Be $publisher
    }

    It 'sets MainPackage/@Version to the same quad as the root' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.MainPackage.Version)" | Should Be '0.2.0.17'
    }

    It 'sets MainPackage/@ProcessorArchitecture' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.MainPackage.ProcessorArchitecture)" | Should Be 'arm64'
    }

    It 'sets MainPackage/@Uri to the immutable per-version release asset' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.MainPackage.Uri)" | Should Be $msixUri
    }

    It 'keeps the 2018 namespace' {
        [xml]$x = [IO.File]::ReadAllText($out)
        "$($x.AppInstaller.NamespaceURI)" | Should Be 'http://schemas.microsoft.com/appx/appinstaller/2018'
    }

    It 'keeps the UpdateSettings block the update path depends on' {
        [xml]$x = [IO.File]::ReadAllText($out)
        $null -eq $x.AppInstaller.UpdateSettings | Should Be $false
        "$($x.AppInstaller.UpdateSettings.OnLaunch.HoursBetweenUpdateChecks)" | Should Be '0'
        "$($x.AppInstaller.UpdateSettings.ForceUpdateFromAnyVersion)" | Should Be 'true'
    }

    It 'renders a loopback http feed verbatim - the local end-to-end harness serves 127.0.0.1' {
        $localOut = Join-Path $dir 'Wavee.local.appinstaller'
        $localFeed = 'http://127.0.0.1:8099/wavee-local/Wavee.arm64.appinstaller'
        $localMsix = 'http://127.0.0.1:8099/pkg/Wavee_0.2.0.9001_arm64.msix'
        New-WaveeAppInstaller -Template $template -OutFile $localOut -Arch 'arm64' -Quad '0.2.0.9001' `
            -Publisher $publisher -IdentityName 'cproducts.Wavee' -FeedUri $localFeed -MsixUri $localMsix | Out-Null
        [xml]$x = [IO.File]::ReadAllText($localOut)
        "$($x.AppInstaller.Uri)" | Should Be $localFeed
        "$($x.AppInstaller.MainPackage.Uri)" | Should Be $localMsix
        "$($x.AppInstaller.Version)" | Should Be '0.2.0.9001'
    }

    It 'leaves a UNC location alone, backslashes and all' {
        # The template substitutes text; nothing here may url-encode or normalize a path. A UNC feed is the
        # documented plan B if a machine-wide proxy ever breaks the loopback feed.
        $uncOut = Join-Path $dir 'Wavee.unc.appinstaller'
        $uncFeed = '\\build\drop\wavee-local\Wavee.x64.appinstaller'
        $uncMsix = '\\build\drop\pkg\Wavee_0.2.0.9001_x64.msix'
        New-WaveeAppInstaller -Template $template -OutFile $uncOut -Arch 'x64' -Quad '0.2.0.9001' `
            -Publisher $publisher -IdentityName 'cproducts.Wavee' -FeedUri $uncFeed -MsixUri $uncMsix | Out-Null
        [xml]$x = [IO.File]::ReadAllText($uncOut)
        "$($x.AppInstaller.Uri)" | Should Be $uncFeed
        "$($x.AppInstaller.MainPackage.Uri)" | Should Be $uncMsix
    }

    It 'renders x64 with the same shape' {
        $x64Out = Join-Path $dir 'Wavee.x64.appinstaller'
        New-WaveeAppInstaller -Template $template -OutFile $x64Out -Arch 'x64' -Quad '0.2.0.17' `
            -Publisher $publisher -IdentityName 'cproducts.Wavee' `
            -FeedUri 'https://example.invalid/f.appinstaller' -MsixUri 'https://example.invalid/m.msix' | Out-Null
        [xml]$x = [IO.File]::ReadAllText($x64Out)
        "$($x.AppInstaller.MainPackage.ProcessorArchitecture)" | Should Be 'x64'
    }

    It 'rejects an architecture the packages are never built for' {
        { New-WaveeAppInstaller -Template $template -OutFile (Join-Path $dir 'bad.appinstaller') -Arch 'x86' `
                -Quad '0.2.0.17' -Publisher $publisher -IdentityName 'cproducts.Wavee' `
                -FeedUri $feedUri -MsixUri $msixUri } | Should Throw
    }

    It 'throws when the template is missing' {
        { New-WaveeAppInstaller -Template (Join-Path $dir 'nope.xml') -OutFile (Join-Path $dir 'bad2.appinstaller') `
                -Arch 'arm64' -Quad '0.2.0.17' -Publisher $publisher -IdentityName 'cproducts.Wavee' `
                -FeedUri $feedUri -MsixUri $msixUri } | Should Throw
    }

    It 'leaves NO file behind when a placeholder survives (the check runs BEFORE the write)' {
        $badTemplate = Join-Path $dir 'grew-a-knob.xml'
        $xml = [IO.File]::ReadAllText($template).Replace('__VERSION__', '__VERSION__ __NEW_KNOB__')
        [IO.File]::WriteAllText($badTemplate, $xml, (New-Object System.Text.UTF8Encoding $false))
        $outFile = Join-Path $dir 'never-written.appinstaller'
        { New-WaveeAppInstaller -Template $badTemplate -OutFile $outFile -Arch 'arm64' -Quad '0.2.0.17' `
                -Publisher $publisher -IdentityName 'cproducts.Wavee' `
                -FeedUri $feedUri -MsixUri $msixUri } | Should Throw
        Test-Path $outFile | Should Be $false
    }
}

# ===================================================================================================================

Describe 'Get-GhAuthToken' {

    It 'takes the token from the last non-empty line, past a gh upgrade notice on stderr' {
        $raw = @('A new release of gh is available: 2.40.0 -> 2.41.0', '', 'gho_0123456789abcdefABCDEF')
        Get-GhAuthToken -RawOutput $raw | Should Be 'gho_0123456789abcdefABCDEF'
    }

    It 'accepts a classic personal access token' {
        Get-GhAuthToken -RawOutput @('ghp_abcDEF0123456789') | Should Be 'ghp_abcDEF0123456789'
    }

    It 'accepts a fine-grained token' {
        Get-GhAuthToken -RawOutput @('github_pat_11ABCDE0123_xyz') | Should Be 'github_pat_11ABCDE0123_xyz'
    }

    It 'trims the surrounding whitespace' {
        Get-GhAuthToken -RawOutput @('  ghs_abc123DEF  ') | Should Be 'ghs_abc123DEF'
    }

    It 'returns empty when gh printed nothing' {
        Get-GhAuthToken -RawOutput @('', '   ') | Should Be ''
    }

    It 'throws rather than passing prose on as a credential' {
        { Get-GhAuthToken -RawOutput @('You are not logged into any GitHub hosts.') } | Should Throw
    }

    It 'throws when a notice came LAST and the token did not' {
        { Get-GhAuthToken -RawOutput @('gho_realtoken0123', 'A new release of gh is available') } | Should Throw
    }

    It 'never echoes what it rejected, in case it really was a secret' {
        $msg = ''
        try { Get-GhAuthToken -RawOutput @('not-a-token-at-all') } catch { $msg = $_.Exception.Message }
        ($msg -like '*not-a-token-at-all*') | Should Be $false
    }
}

# ===================================================================================================================

Describe 'Test-WaveeFeedLive' {

    $thisRelease = 'https://github.com/o/r/releases/download/wavee-v0.2.0/Wavee_0.2.0.17_arm64.msix'

    Context 'when the feed carries the expected quad' {
        Mock -ModuleName Wavee.Release Get-WaveeFeedDocument {
            [pscustomobject]@{
                Version = [version]'0.2.0.17'
                MsixUri = 'https://github.com/o/r/releases/download/wavee-v0.2.0/Wavee_0.2.0.17_arm64.msix'
            }
        }

        It 'passes on the quad alone when no URI was given' {
            Test-WaveeFeedLive -Repo 'o/r' -FeedRelease 'wavee-stable' -Arch 'arm64' -ExpectedQuad '0.2.0.17' `
                -Retries 1 -DelaySeconds 0 | Should Be $true
        }

        It 'passes when MainPackage/@Uri is the package of this release' {
            Test-WaveeFeedLive -Repo 'o/r' -FeedRelease 'wavee-stable' -Arch 'arm64' -ExpectedQuad '0.2.0.17' `
                -ExpectedMsixUri $thisRelease -Retries 1 -DelaySeconds 0 | Should Be $true
        }

        It 'FAILS when the version moved but the package URI still names another tag' {
            Test-WaveeFeedLive -Repo 'o/r' -FeedRelease 'wavee-stable' -Arch 'arm64' -ExpectedQuad '0.2.0.17' `
                -ExpectedMsixUri 'https://github.com/o/r/releases/download/wavee-v0.1.9/Wavee_0.1.9.3_arm64.msix' `
                -Retries 1 -DelaySeconds 0 -WarningAction SilentlyContinue | Should Be $false
        }
    }

    Context 'when the feed is still on the old quad' {
        Mock -ModuleName Wavee.Release Get-WaveeFeedDocument {
            [pscustomobject]@{ Version = [version]'0.2.0.16'; MsixUri = 'https://example.invalid/old.msix' }
        }

        It 'fails' {
            Test-WaveeFeedLive -Repo 'o/r' -FeedRelease 'wavee-stable' -Arch 'arm64' -ExpectedQuad '0.2.0.17' `
                -Retries 1 -DelaySeconds 0 -WarningAction SilentlyContinue | Should Be $false
        }
    }

    Context 'when the feed asset does not exist yet' {
        Mock -ModuleName Wavee.Release Get-WaveeFeedDocument { return $null }

        It 'fails instead of throwing' {
            Test-WaveeFeedLive -Repo 'o/r' -FeedRelease 'wavee-stable' -Arch 'arm64' -ExpectedQuad '0.2.0.17' `
                -Retries 1 -DelaySeconds 0 | Should Be $false
        }
    }
}

# ===================================================================================================================

Describe 'Write-ReleaseManifest / Test-ReleaseManifest' {

    $dir = New-TmpDir 'manifest'
    [IO.File]::WriteAllText((Join-Path $dir 'a.txt'), 'alpha')
    [IO.File]::WriteAllText((Join-Path $dir 'b.bin'), 'bravo')
    $manifest = Join-Path $dir 'MANIFEST.txt'

    It 'writes one sha256sum line per file' {
        Write-ReleaseManifest -Dir $dir -Files @('a.txt', 'b.bin') -OutFile $manifest | Out-Null
        @(Get-Content $manifest).Count | Should Be 2
    }

    It 'uses the sha256sum format: lowercase hash, two spaces, name' {
        $line = @(Get-Content $manifest)[0]
        ($line -match '^[0-9a-f]{64}  [^ ].*$') | Should Be $true
    }

    It 'sorts the entries so the file is stable across runs' {
        $names = @(Get-Content $manifest | ForEach-Object { ($_ -split '  ', 2)[1] })
        ($names -join ',') | Should Be 'a.txt,b.bin'
    }

    It 'writes UTF-8 without a BOM' {
        $bytes = Get-FileBytes $manifest
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should Be $false
    }

    It 'round-trips: an untouched folder verifies' {
        Test-ReleaseManifest -Dir $dir -ManifestFile $manifest | Should Be $true
    }

    It 'detects a tampered file' {
        [IO.File]::WriteAllText((Join-Path $dir 'a.txt'), 'ALPHA')
        Test-ReleaseManifest -Dir $dir -ManifestFile $manifest | Should Be $false
        [IO.File]::WriteAllText((Join-Path $dir 'a.txt'), 'alpha')
        Test-ReleaseManifest -Dir $dir -ManifestFile $manifest | Should Be $true
    }

    It 'detects a missing file' {
        Remove-Item (Join-Path $dir 'b.bin') -Force
        Test-ReleaseManifest -Dir $dir -ManifestFile $manifest | Should Be $false
    }

    It 'returns false when the manifest itself is gone' {
        Test-ReleaseManifest -Dir $dir -ManifestFile (Join-Path $dir 'no-such-manifest.txt') | Should Be $false
    }

    It 'refuses to hash a file that is not there' {
        { Write-ReleaseManifest -Dir $dir -Files @('ghost.txt') -OutFile (Join-Path $dir 'M2.txt') } | Should Throw
    }
}

# ===================================================================================================================

Describe 'Get-WaveeVersionProps' {

    $dir = New-TmpDir 'props-read'

    $good = Join-Path $dir 'good.props'
    [IO.File]::WriteAllText($good, @"
<Project>
  <PropertyGroup>
    <!-- Hand-edited before a release. -->
    <WaveeVersion>0.2.0</WaveeVersion>
    <WaveeCodename>Breaker</WaveeCodename>
    <!-- NEVER hand-edited. -->
    <WaveeBuild>17</WaveeBuild>
  </PropertyGroup>
</Project>
"@)

    It 'reads the version, codename and build counter' {
        $p = Get-WaveeVersionProps $good
        $p.Version | Should Be '0.2.0'
        $p.Codename | Should Be 'Breaker'
        $p.Build | Should Be 17
    }

    It 'returns the build counter as an int, so +1 is arithmetic and not concatenation' {
        $p = Get-WaveeVersionProps $good
        ($p.Build + 1) | Should Be 18
    }

    It 'reports the path it read' {
        (Get-WaveeVersionProps $good).Path | Should Be $good
    }

    It 'throws when an element is missing' {
        $bad = Join-Path $dir 'bad.props'
        [IO.File]::WriteAllText($bad, "<Project><PropertyGroup><WaveeVersion>0.2.0</WaveeVersion></PropertyGroup></Project>")
        { Get-WaveeVersionProps $bad } | Should Throw
    }
}

# ===================================================================================================================

Describe 'Set-WaveeBuild' {

    $dir = New-TmpDir 'props-write'
    $original = @"
<Project>
  <PropertyGroup>
    <!-- Hand-edited before a release: semver M.m.p or M.m.p-beta.N. -->
    <WaveeVersion>0.2.0</WaveeVersion>
    <WaveeCodename>Breaker</WaveeCodename>
    <!-- NEVER hand-edited. Monotonic release counter (max 65535). -->
    <WaveeBuild>17</WaveeBuild>
  </PropertyGroup>
</Project>
"@

    function New-PropsFile {
        param([string]$Name, [string]$Text = $original)
        $p = Join-Path $dir $Name
        [IO.File]::WriteAllText($p, $Text, (New-Object System.Text.UTF8Encoding $false))
        $p
    }

    It 'replaces the counter' {
        $p = New-PropsFile 'a.props'
        Set-WaveeBuild $p 18
        (Get-WaveeVersionProps $p).Build | Should Be 18
    }

    It 'replaces exactly one occurrence and nothing else' {
        $p = New-PropsFile 'b.props'
        Set-WaveeBuild $p 18
        $t = [IO.File]::ReadAllText($p)
        ([regex]::Matches($t, '<WaveeBuild>')).Count | Should Be 1
        ($t -replace '<WaveeBuild>18</WaveeBuild>', '<WaveeBuild>17</WaveeBuild>') | Should Be $original
    }

    It 'leaves the version, the codename and every comment intact' {
        $p = New-PropsFile 'c.props'
        Set-WaveeBuild $p 99
        $t = [IO.File]::ReadAllText($p)
        ($t -like '*<WaveeVersion>0.2.0</WaveeVersion>*') | Should Be $true
        ($t -like '*<WaveeCodename>Breaker</WaveeCodename>*') | Should Be $true
        ($t -like '*NEVER hand-edited*') | Should Be $true
        ($t -like '*Hand-edited before a release*') | Should Be $true
    }

    It 'writes UTF-8 without a BOM' {
        $p = New-PropsFile 'd.props'
        Set-WaveeBuild $p 18
        $bytes = Get-FileBytes $p
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should Be $false
    }

    It 'accepts 0 and 65535' {
        $p = New-PropsFile 'e.props'
        Set-WaveeBuild $p 0
        (Get-WaveeVersionProps $p).Build | Should Be 0
        Set-WaveeBuild $p 65535
        (Get-WaveeVersionProps $p).Build | Should Be 65535
    }

    It 'rejects a counter above 65535 (MSIX would silently truncate it)' {
        $p = New-PropsFile 'f.props'
        { Set-WaveeBuild $p 65536 } | Should Throw
    }

    It 'rejects a negative counter' {
        $p = New-PropsFile 'g.props'
        $negative = -1
        { Set-WaveeBuild $p $negative } | Should Throw
    }

    It 'refuses a file with more than one WaveeBuild element' {
        $p = New-PropsFile 'h.props' "<Project><WaveeBuild>1</WaveeBuild><WaveeBuild>2</WaveeBuild></Project>"
        { Set-WaveeBuild $p 3 } | Should Throw
    }

    It 'refuses a file with no WaveeBuild element' {
        $p = New-PropsFile 'i.props' "<Project><WaveeVersion>0.2.0</WaveeVersion></Project>"
        { Set-WaveeBuild $p 3 } | Should Throw
    }
}

# ===================================================================================================================

Describe 'Get-ReleaseState / Set-ReleaseState' {

    $dir = New-TmpDir 'state'
    $path = Join-Path $dir 'release-state.json'

    It 'returns null when there is no ledger' {
        ($null -eq (Get-ReleaseState $path)) | Should Be $true
    }

    It 'round-trips the phase map and the pushed flag' {
        Set-ReleaseState $path @{ semver = '0.2.0'; quad = '0.2.0.17'; pushed = $false; phases = @{ preflight = 'done'; bump = 'done' } }
        $s = Get-ReleaseState $path
        $s.semver | Should Be '0.2.0'
        $s.quad | Should Be '0.2.0.17'
        $s.pushed | Should Be $false
        $s.phases.preflight | Should Be 'done'
        ($null -eq $s.phases.notes) | Should Be $true
    }

    It 'writes UTF-8 without a BOM' {
        $bytes = Get-FileBytes $path
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should Be $false
    }
}

# ===================================================================================================================

Describe 'ConvertFrom-GhJson' {

    It 'parses plain JSON' {
        (ConvertFrom-GhJson '{"tagName":"wavee-v0.2.0"}').tagName | Should Be 'wavee-v0.2.0'
    }

    It 'skips a notice line gh printed before the payload' {
        $t = "A new release of gh is available`n{`"tagName`":`"wavee-v0.2.0`"}"
        (ConvertFrom-GhJson $t).tagName | Should Be 'wavee-v0.2.0'
    }

    It 'returns null for empty output' {
        ($null -eq (ConvertFrom-GhJson '')) | Should Be $true
    }

    It 'returns null when there is no JSON at all' {
        ($null -eq (ConvertFrom-GhJson 'release not found')) | Should Be $true
    }
}

# ===================================================================================================================

Remove-Item $script:TmpRoot -Recurse -Force -ErrorAction SilentlyContinue
