#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1). Run with:

        Invoke-Pester ops\release\tests

    Covers LocalFeedServer.psm1 - the loopback feed the local update end-to-end harness serves. The three pure
    helpers (range arithmetic, content types, path resolution) are covered without ever binding a port, because
    binding http://127.0.0.1:<port>/ needs elevation or a urlacl reservation. The ONE live test that does bind a
    listener self-skips when the session is not elevated, and says so.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here 'LocalFeedServer.psm1') -Force -DisableNameChecking

$script:IsElevated = $false
try {
    $wi = [Security.Principal.WindowsIdentity]::GetCurrent()
    $script:IsElevated = (New-Object Security.Principal.WindowsPrincipal $wi).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}
catch { $script:IsElevated = $false }

$script:TmpRoot = Join-Path $env:TEMP ('wavee-feedserver-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:TmpRoot | Out-Null

# ===================================================================================================================

Describe 'Get-RangeSlice' {

    # Every case below is against a 100-byte entity unless it says otherwise.

    It 'resolves a closed range' {
        $s = Get-RangeSlice -RangeHeader 'bytes=0-9' -Length 100
        $s.Status | Should Be 206
        $s.Start | Should Be 0
        $s.End | Should Be 9
        $s.Count | Should Be 10
    }

    It 'resolves an open-ended range to the last byte' {
        $s = Get-RangeSlice -RangeHeader 'bytes=90-' -Length 100
        $s.Status | Should Be 206
        $s.Start | Should Be 90
        $s.End | Should Be 99
        $s.Count | Should Be 10
    }

    It 'resolves a suffix range to the LAST n bytes' {
        $s = Get-RangeSlice -RangeHeader 'bytes=-10' -Length 100
        $s.Status | Should Be 206
        $s.Start | Should Be 90
        $s.End | Should Be 99
        $s.Count | Should Be 10
    }

    It 'clamps a last-byte-pos past the end instead of rejecting it' {
        $s = Get-RangeSlice -RangeHeader 'bytes=0-999' -Length 100
        $s.Status | Should Be 206
        $s.Start | Should Be 0
        $s.End | Should Be 99
        $s.Count | Should Be 100
    }

    It 'clamps a suffix longer than the entity' {
        $s = Get-RangeSlice -RangeHeader 'bytes=-500' -Length 100
        $s.Status | Should Be 206
        $s.Start | Should Be 0
        $s.Count | Should Be 100
    }

    It 'rejects a first-byte-pos at or past the end (416)' {
        $s = Get-RangeSlice -RangeHeader 'bytes=100-' -Length 100
        $s.Status | Should Be 416
        $s.Count | Should Be 0
    }

    It 'rejects an inverted range (416)' {
        $s = Get-RangeSlice -RangeHeader 'bytes=5-3' -Length 100
        $s.Status | Should Be 416
        $s.Count | Should Be 0
    }

    It 'rejects a zero-length suffix (416)' {
        $s = Get-RangeSlice -RangeHeader 'bytes=-0' -Length 100
        $s.Status | Should Be 416
        $s.Count | Should Be 0
    }

    It 'answers a multi-range request with the whole entity (200)' {
        $s = Get-RangeSlice -RangeHeader 'bytes=0-9,20-29' -Length 100
        $s.Status | Should Be 200
        $s.Start | Should Be 0
        $s.End | Should Be 99
        $s.Count | Should Be 100
    }

    It 'answers a malformed range with the whole entity (200)' {
        $s = Get-RangeSlice -RangeHeader 'bytes=abc' -Length 100
        $s.Status | Should Be 200
        $s.Count | Should Be 100
    }

    It 'answers an unknown range unit with the whole entity (200)' {
        $s = Get-RangeSlice -RangeHeader 'items=0-9' -Length 100
        $s.Status | Should Be 200
        $s.Count | Should Be 100
    }

    It 'answers no Range header with the whole entity (200)' {
        $s = Get-RangeSlice -RangeHeader '' -Length 100
        $s.Status | Should Be 200
        $s.Start | Should Be 0
        $s.End | Should Be 99
        $s.Count | Should Be 100
    }

    It 'answers a null Range header with the whole entity (200)' {
        $s = Get-RangeSlice -RangeHeader $null -Length 100
        $s.Status | Should Be 200
        $s.Count | Should Be 100
    }

    It 'tolerates whitespace around the spec' {
        $s = Get-RangeSlice -RangeHeader ' bytes = 0-9 ' -Length 100
        $s.Status | Should Be 206
        $s.Count | Should Be 10
    }

    It 'serves a zero-length file as an empty 200, never a 416' {
        $s = Get-RangeSlice -RangeHeader 'bytes=0-9' -Length 0
        $s.Status | Should Be 200
        $s.Count | Should Be 0
    }
}

# ===================================================================================================================

Describe 'Resolve-FeedFile' {

    $root = Join-Path $script:TmpRoot 'feed'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pkg') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'pkg\Wavee 1.msix'), 'payload')
    [IO.File]::WriteAllText((Join-Path $root 'top.json'), '{}')
    # %TEMP% is an 8.3 path on some profiles and Resolve-FeedFile normalizes it, so compare normalized to normalized.
    function Expect-Path { param([string]$Relative) [IO.Path]::GetFullPath((Join-Path $root $Relative)) }

    It 'resolves a real file at the root' {
        Resolve-FeedFile -Root $root -UrlPath '/top.json' | Should Be (Expect-Path 'top.json')
    }

    It 'resolves a real file in a subfolder' {
        Resolve-FeedFile -Root $root -UrlPath '/pkg/Wavee%201.msix' | Should Be (Expect-Path 'pkg\Wavee 1.msix')
    }

    It 'url-decodes %20 before touching the filesystem' {
        (Resolve-FeedFile -Root $root -UrlPath '/pkg/Wavee%201.msix') -like '*Wavee 1.msix' | Should Be $true
    }

    It 'ignores a query string' {
        Resolve-FeedFile -Root $root -UrlPath '/top.json?cachebust=7' | Should Be (Expect-Path 'top.json')
    }

    It 'returns null for a file that is not there' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/nope.json')) | Should Be $true
    }

    It 'returns null for the root itself' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/')) | Should Be $true
    }

    It 'returns null for a directory' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/pkg')) | Should Be $true
    }

    It 'rejects a .. traversal' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/../../Windows/win.ini')) | Should Be $true
    }

    It 'rejects a .. traversal hidden inside the path' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/pkg/../../Windows/win.ini')) | Should Be $true
    }

    It 'rejects a percent-encoded .. traversal' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/pkg/%2e%2e/%2e%2e/Windows/win.ini')) | Should Be $true
    }

    It 'rejects a drive letter' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/C:/Windows/win.ini')) | Should Be $true
    }

    It 'rejects a percent-encoded drive letter' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/C%3A/Windows/win.ini')) | Should Be $true
    }

    It 'rejects an alternate data stream' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '/top.json:hidden')) | Should Be $true
    }

    It 'rejects an empty segment (a UNC prefix would start that way)' {
        ($null -eq (Resolve-FeedFile -Root $root -UrlPath '//server/share/x.msix')) | Should Be $true
    }
}

# ===================================================================================================================

Describe 'Get-FeedContentType' {

    It 'types the .appinstaller feed document' {
        Get-FeedContentType 'C:\feed\Wavee.arm64.appinstaller' | Should Be 'application/appinstaller'
    }

    It 'types the package' {
        Get-FeedContentType 'C:\feed\pkg\Wavee_0.2.0.9001_arm64.msix' | Should Be 'application/msix'
    }

    It 'types the release-notes documents' {
        Get-FeedContentType 'whatsnew-index.json' | Should Be 'application/json'
    }

    It 'types the poster media' {
        Get-FeedContentType 'media\redesigned.jpg' | Should Be 'image/jpeg'
    }

    It 'is case-insensitive about the extension' {
        Get-FeedContentType 'Wavee.X64.APPINSTALLER' | Should Be 'application/appinstaller'
    }

    It 'falls back to octet-stream for anything else' {
        Get-FeedContentType 'x.zzz' | Should Be 'application/octet-stream'
    }

    It 'falls back to octet-stream for a file with no extension' {
        Get-FeedContentType 'LICENSE' | Should Be 'application/octet-stream'
    }
}

# ===================================================================================================================
# The evidence helpers the end-to-end harness reasons with. All pure: rows in, verdict out - no listener, no
# registry, no processes. They exist because the cmdlet-shaped answers they replace were observed to be WRONG
# (Get-AppxPackageAutoUpdateSettings said "no association" for a package Windows was updating through its
# .appinstaller on every launch), so the harness now proves the same facts from logs it already has.
# ===================================================================================================================

function New-FeedRow {
    param([string]$Time, [string]$Method = 'GET', [string]$Path = '/wavee-local/Wavee.arm64.appinstaller',
          [int]$Status = 200, [string]$UserAgent = 'App Virt Client/1.0')
    [pscustomobject]@{ Time = $Time; Method = $Method; Path = $Path; Status = $Status
        Range = ''; Bytes = [long]512; UserAgent = $UserAgent }
}

Describe 'ConvertTo-FeedPath' {

    It 'reduces an absolute feed URL to its path' {
        ConvertTo-FeedPath 'http://127.0.0.1:8099/wavee-local/Wavee.arm64.appinstaller' |
            Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'leaves an already-reduced path alone' {
        ConvertTo-FeedPath '/wavee-local/Wavee.arm64.appinstaller' | Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'roots a path that arrives without a leading slash' {
        ConvertTo-FeedPath 'wavee-local/Wavee.arm64.appinstaller' | Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'drops a query string' {
        ConvertTo-FeedPath '/wavee-local/Wavee.arm64.appinstaller?cb=17' | Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'drops a fragment' {
        ConvertTo-FeedPath '/wavee-local/Wavee.arm64.appinstaller#top' | Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'percent-decodes, so an encoded request matches the plain feed URL' {
        ConvertTo-FeedPath '/wavee%2Dlocal/Wavee.arm64.appinstaller' | Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'normalizes backslashes' {
        ConvertTo-FeedPath '\wavee-local\Wavee.arm64.appinstaller' | Should Be '/wavee-local/Wavee.arm64.appinstaller'
    }

    It 'answers empty for empty input' {
        ConvertTo-FeedPath '' | Should Be ''
    }

    It 'answers empty for null input' {
        ConvertTo-FeedPath $null | Should Be ''
    }
}

# ===================================================================================================================

Describe 'Get-FeedAssociationRequests' {

    $feed = 'http://127.0.0.1:8099/wavee-local/Wavee.arm64.appinstaller'
    $mark = [datetime]::Parse('2026-08-29T21:38:00Z').ToUniversalTime()

    It 'counts a deployment-engine GET of the feed after the mark' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:38:25.100Z')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 1
    }

    It 'ignores a GET from BEFORE the mark' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:37:59.000Z')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 0
    }

    It 'ignores the app own poll (the association is the OS agent, never Wavee)' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:38:25.100Z' -UserAgent 'Wavee/0.2.0.9001')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 0
    }

    It 'ignores a request for a different document' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:38:25.100Z' -Path '/pkg/Wavee_0.2.0.9002_arm64.msix')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 0
    }

    It 'matches the feed through a query string and percent-encoding' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:38:25.100Z' -Path '/wavee%2Dlocal/Wavee.arm64.appinstaller?cb=1')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 1
    }

    It 'accepts HEAD as well as GET' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:38:25.100Z' -Method 'HEAD')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 1
    }

    It 'drops a row whose timestamp cannot be parsed rather than counting it' {
        $rows = @(New-FeedRow -Time 'not-a-time')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 0
    }

    It 'is case-insensitive about the user agent' {
        $rows = @(New-FeedRow -Time '2026-08-29T21:38:25.100Z' -UserAgent 'app virt client/1.0')
        @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed).Count | Should Be 1
    }

    It 'keeps every qualifying row, in log order' {
        $rows = @(
            (New-FeedRow -Time '2026-08-29T21:38:25.100Z'),
            (New-FeedRow -Time '2026-08-29T21:39:02.000Z'),
            (New-FeedRow -Time '2026-08-29T21:37:00.000Z'))
        $hits = @(Get-FeedAssociationRequests -Rows $rows -Mark $mark -FeedPath $feed)
        $hits.Count | Should Be 2
        $hits[0].Time | Should Be '2026-08-29T21:38:25.100Z'
    }

    It 'answers an empty array for no rows at all' {
        (Get-FeedAssociationRequests -Rows @() -Mark $mark -FeedPath $feed).Count | Should Be 0
    }
}

# ===================================================================================================================

Describe 'Test-AppInstallerUpdateEvent' {

    # Real AppXDeploymentServer/Operational id 603 message shapes. The bare-Add one is the whole point: the harness
    # counted ANY 603 as proof of an auto-update association, so P6 - which installs A bare precisely to prove there
    # is NO association - failed against its own install ("proved by event-603: 1 AppXDeploymentServer").
    $pkg = 'cproducts.Wavee'
    $update = 'Started deployment UpdateUsingAppInstallerOperation operation on package cproducts.Wavee_0.2.0.9002_arm64__8wekyb3d8bbwe. See http://go.microsoft.com/fwlink/?LinkId=235160 for help diagnosing app deployment issues.'
    $add = 'Started deployment AddPackageOperation operation on package cproducts.Wavee_0.2.0.9001_arm64__8wekyb3d8bbwe. See http://go.microsoft.com/fwlink/?LinkId=235160 for help diagnosing app deployment issues.'

    It 'accepts an UpdateUsingAppInstallerOperation for this package' {
        Test-AppInstallerUpdateEvent -Message $update -PackageMatch $pkg | Should Be $true
    }

    It 'rejects the bare AddPackageOperation of the same package (the false positive it exists to kill)' {
        Test-AppInstallerUpdateEvent -Message $add -PackageMatch $pkg | Should Be $false
    }

    It 'rejects an update operation for a DIFFERENT package' {
        Test-AppInstallerUpdateEvent -Message $update -PackageMatch 'Contoso.Other' | Should Be $false
    }

    It 'rejects an empty message' {
        Test-AppInstallerUpdateEvent -Message '' -PackageMatch $pkg | Should Be $false
    }

    It 'rejects a null message' {
        Test-AppInstallerUpdateEvent -Message $null -PackageMatch $pkg | Should Be $false
    }

    It 'is case-insensitive about both the package and the operation' {
        Test-AppInstallerUpdateEvent -Message $update.ToLowerInvariant() -PackageMatch 'CPRODUCTS.WAVEE' | Should Be $true
    }

    It 'checks the package alone when no operation is asked for (the 400/401/404 rows)' {
        Test-AppInstallerUpdateEvent -Message $add -PackageMatch $pkg -OperationContains '' | Should Be $true
    }

    It 'checks the operation alone when no package is asked for' {
        Test-AppInstallerUpdateEvent -Message $update -PackageMatch '' | Should Be $true
        Test-AppInstallerUpdateEvent -Message $add -PackageMatch '' | Should Be $false
    }
}

# ===================================================================================================================

Describe 'Compare-LogSnapshot' {

    $before = @(
        [pscustomobject]@{ Path = 'C:\lc\wavee-20260829.log'; Length = [long]41000 },
        [pscustomobject]@{ Path = 'C:\lc\wavee-20260828.log'; Length = [long]12000 })

    It 'passes when every marked file is still there and has only grown' {
        $after = @(
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260829.log'; Length = [long]52000 },
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260828.log'; Length = [long]12000 })
        (Compare-LogSnapshot -Before $before -After $after).Ok | Should Be $true
    }

    It 'fails when a marked file is gone' {
        $after = @([pscustomobject]@{ Path = 'C:\lc\wavee-20260829.log'; Length = [long]52000 })
        $r = Compare-LogSnapshot -Before $before -After $after
        $r.Ok | Should Be $false
        $r.Missing.Count | Should Be 1
    }

    It 'fails when a marked file SHRANK (the observed LocalCache reset)' {
        $after = @(
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260829.log'; Length = [long]7000 },
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260828.log'; Length = [long]12000 })
        $r = Compare-LogSnapshot -Before $before -After $after
        $r.Ok | Should Be $false
        $r.Shrunk.Count | Should Be 1
    }

    It 'compares paths case-insensitively' {
        $after = @(
            [pscustomobject]@{ Path = 'c:\LC\WAVEE-20260829.LOG'; Length = [long]41000 },
            [pscustomobject]@{ Path = 'c:\LC\WAVEE-20260828.LOG'; Length = [long]12000 })
        (Compare-LogSnapshot -Before $before -After $after).Ok | Should Be $true
    }

    It 'ignores files that only appeared after the mark' {
        $after = @(
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260829.log'; Length = [long]41000 },
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260828.log'; Length = [long]12000 },
            [pscustomobject]@{ Path = 'C:\lc\wavee-20260830.log'; Length = [long]900 })
        (Compare-LogSnapshot -Before $before -After $after).Ok | Should Be $true
    }

    It 'says so, rather than passing silently, when nothing was snapshotted' {
        $r = Compare-LogSnapshot -Before @() -After @()
        $r.Ok | Should Be $true
        $r.Detail | Should Be 'nothing was snapshotted to compare'
    }
}

# ===================================================================================================================

Describe 'ConvertTo-QuadString / Test-QuadMatch / Test-AnyQuadMatch' {

    It 'normalizes a four-part version string' {
        ConvertTo-QuadString '0.2.0.9002' | Should Be '0.2.0.9002'
    }

    It 'fills the missing parts of a three-part version with zero (Revision is -1, not 0)' {
        ConvertTo-QuadString '0.2.0' | Should Be '0.2.0.0'
    }

    It 'normalizes a [Version] object the same way as its string' {
        ConvertTo-QuadString ([Version]'0.2.0.9002') | Should Be '0.2.0.9002'
    }

    It 'answers empty for something that is not a version' {
        ConvertTo-QuadString 'wavee' | Should Be ''
    }

    It 'answers empty for null' {
        ConvertTo-QuadString $null | Should Be ''
    }

    It 'matches a [Version] object against the harness quad string' {
        Test-QuadMatch ([Version]'0.2.0.9002') '0.2.0.9002' | Should Be $true
    }

    It 'does not match a different quad' {
        Test-QuadMatch '0.2.0.9001' '0.2.0.9002' | Should Be $false
    }

    It 'refuses to call two unparseable values a match' {
        Test-QuadMatch 'x' 'x' | Should Be $false
    }

    It 'sees B while the outgoing A is still registered (the poll that never fired)' {
        Test-AnyQuadMatch -Versions @([Version]'0.2.0.9001', [Version]'0.2.0.9002') -Expected '0.2.0.9002' |
            Should Be $true
    }

    It 'is false when none of the visible versions is the expected one' {
        Test-AnyQuadMatch -Versions @([Version]'0.2.0.9001') -Expected '0.2.0.9002' | Should Be $false
    }

    It 'is false for no visible versions at all' {
        Test-AnyQuadMatch -Versions @() -Expected '0.2.0.9002' | Should Be $false
    }
}

# ===================================================================================================================
# The one test that binds a real listener. http.sys refuses http://127.0.0.1:<port>/ to a non-elevated process
# without a urlacl reservation, so this is skipped rather than failed when the session is not elevated.
# ===================================================================================================================

Describe 'Start-LocalFeedServer (live listener)' {

    It 'serves whole bodies, HEAD, single ranges and 404s, and logs every request' -Skip:(-not $script:IsElevated) {
        $root = Join-Path $script:TmpRoot 'live'
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'pkg') | Out-Null
        $bytes = New-Object byte[] 1000
        for ($i = 0; $i -lt 1000; $i++) { $bytes[$i] = [byte]($i % 251) }
        [IO.File]::WriteAllBytes((Join-Path $root 'pkg\probe.msix'), $bytes)
        $logPath = Join-Path $script:TmpRoot 'live-requests.log'

        $port = Get-Random -Minimum 20000 -Maximum 40000
        $srv = Start-LocalFeedServer -Root $root -Port $port -BindHost '127.0.0.1' -LogPath $logPath
        try {
            $url = $srv.Prefix + 'pkg/probe.msix'

            # 1. GET the whole body
            $req = [Net.HttpWebRequest]::Create($url)
            $req.Proxy = $null
            $req.UserAgent = 'PesterProbe/1.0'
            $resp = $req.GetResponse()
            [int]$resp.StatusCode | Should Be 200
            [long]$resp.ContentLength | Should Be 1000
            $ms = New-Object IO.MemoryStream
            $resp.GetResponseStream().CopyTo($ms)
            $resp.Close()
            $ms.Length | Should Be 1000
            $ms.Dispose()

            # 2. HEAD: the length, no body
            $req = [Net.HttpWebRequest]::Create($url)
            $req.Proxy = $null
            $req.Method = 'HEAD'
            $req.UserAgent = 'PesterProbe/1.0'
            $resp = $req.GetResponse()
            [int]$resp.StatusCode | Should Be 200
            [long]$resp.ContentLength | Should Be 1000
            $ms = New-Object IO.MemoryStream
            $resp.GetResponseStream().CopyTo($ms)
            $resp.Close()
            $ms.Length | Should Be 0
            $ms.Dispose()

            # 3. A single byte range -> 206 + Content-Range + exactly 10 bytes
            $req = [Net.HttpWebRequest]::Create($url)
            $req.Proxy = $null
            $req.UserAgent = 'PesterProbe/1.0'
            $req.AddRange(10, 19)
            $resp = $req.GetResponse()
            [int]$resp.StatusCode | Should Be 206
            "$($resp.Headers['Content-Range'])" | Should Be 'bytes 10-19/1000'
            $ms = New-Object IO.MemoryStream
            $resp.GetResponseStream().CopyTo($ms)
            $resp.Close()
            $ms.Length | Should Be 10
            $ms.Dispose()

            # 4. A missing file -> 404 with a body
            $status404 = 0
            try {
                $req = [Net.HttpWebRequest]::Create($srv.Prefix + 'pkg/missing.msix')
                $req.Proxy = $null
                $req.UserAgent = 'PesterProbe/1.0'
                $resp = $req.GetResponse()
                $resp.Close()
            }
            catch [Net.WebException] {
                if ($_.Exception.Response) {
                    $status404 = [int]$_.Exception.Response.StatusCode
                    $_.Exception.Response.Close()
                }
            }
            $status404 | Should Be 404
        }
        finally { Stop-LocalFeedServer $srv }

        # The request log is the harness's evidence; it must carry one line per request, tab separated.
        $lines = @(Get-Content $logPath | Where-Object { "$_".Trim().Length -gt 0 })
        $lines.Count | Should Be 4
        $cols = $lines[0] -split ([string][char]9)
        $cols.Count | Should Be 7
        $cols[1] | Should Be 'GET'
        $cols[2] | Should Be '/pkg/probe.msix'
        $cols[3] | Should Be '200'
        $cols[6] | Should Be 'PesterProbe/1.0'
        (($lines[1] -split ([string][char]9))[1]) | Should Be 'HEAD'
        (($lines[2] -split ([string][char]9))[3]) | Should Be '206'
        (($lines[2] -split ([string][char]9))[4]) | Should Be 'bytes=10-19'
        (($lines[3] -split ([string][char]9))[3]) | Should Be '404'
    }
}

# ===================================================================================================================

Remove-Item $script:TmpRoot -Recurse -Force -ErrorAction SilentlyContinue
