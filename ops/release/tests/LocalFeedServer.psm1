#requires -Version 5.1
<#
    LocalFeedServer.psm1 - a loopback HTTP file server for the fully local Wavee update end-to-end test.

    Why this exists: the in-app update checker and the release-notes store both use the app's shared
    SocketsHttpHandler client, which speaks http/https only (a file: URI throws NotSupportedException and lands the
    updater in Failed > Network). Serving the feed over 127.0.0.1 exercises the REAL production code path with zero
    test-only branches, and the deployment engine (AppXSvc) is equally happy to download an .appinstaller and its
    .msix from loopback.

    What it gives the harness that a static file share cannot: a per-request log. That log is the evidence for the
    two claims the E2E makes - that the IN-APP checker fetched the .appinstaller (User-Agent "Wavee/..."), and that
    the DEPLOYMENT ENGINE (a different User-Agent) downloaded the .msix, in Range slices.

    Style rules (shared with the rest of ops\): PowerShell 5.1 only, ASCII-only string literals, UTF-8 without a BOM
    on everything written, no && / || / ternary.

    This module also owns the PURE helpers the harness's evidence checks are built on. They live here, next to the
    request-log writer whose output two of them read, because a pure function is the only part of the harness Pester
    can cover: everything else needs an elevated session, a real package, and a real update.

    Exports:
      Get-RangeSlice        pure: "bytes=a-b" + entity length -> @{ Status; Start; End; Count }
      Get-FeedContentType   pure: path -> Content-Type
      Resolve-FeedFile      pure: root + url path -> a full path inside root, or $null (traversal-safe)
      ConvertTo-FeedPath    pure: a feed URL or a logged RawUrl -> the comparable '/a/b.appinstaller' form
      Get-FeedAssociationRequests
                            pure: parsed request rows + a mark + a feed path -> the deployment-engine GETs of that
                            document after the mark (the App Installer association, proved over the wire)
      Test-AppInstallerUpdateEvent
                            pure: one AppXDeploymentServer 603 message -> is it UpdateUsingAppInstallerOperation for
                            this package (i.e. the association firing), rather than any other deployment operation
      Compare-LogSnapshot   pure: two {Path;Length} snapshots -> did every file survive, and at least its size
      ConvertTo-QuadString  pure: '0.2.0' / '0.2.0.9002' / [Version] -> the normalized 4-part string ('' if not one)
      Test-QuadMatch        pure: two version-ish values -> do they name the same quad
      Test-AnyQuadMatch     pure: a set of version-ish values + one expected quad -> does ANY of them match
      Start-LocalFeedServer binds the listener on THIS thread (so a bind failure throws here, with the netsh hint)
                            and runs the accept loop in a background runspace
      Stop-LocalFeedServer  stops the listener, drains the loop, disposes the runspace
#>

# ===================================================================================================================
# Pure helpers (Pester covers these without ever binding a port)
# ===================================================================================================================

function Get-RangeSlice {
    <#
    .SYNOPSIS
      Resolve one RFC 7233 Range header against an entity length.
    .DESCRIPTION
      Deliberately supports the SINGLE-range forms only - "bytes=a-b", "bytes=a-", "bytes=-n" - because that is what
      every real client of this feed sends (App Installer / BITS / HttpClient). A multi-range or malformed header is
      answered with the whole entity (status 200), which is always a correct answer; inventing a multipart/byteranges
      body would be more code and more ways to be subtly wrong.
    .OUTPUTS
      pscustomobject @{ Status = 200 | 206 | 416; Start; End; Count }   (416 carries Start 0 / End -1 / Count 0)
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][string]$RangeHeader,
        [Parameter(Mandatory = $true)][long]$Length)

    $whole = [pscustomobject]@{ Status = 200; Start = [long]0; End = [long]($Length - 1); Count = [long]$Length }
    $unsatisfiable = [pscustomobject]@{ Status = 416; Start = [long]0; End = [long](-1); Count = [long]0 }

    # A zero-length entity has no satisfiable range at all; 200 with an empty body is the honest answer.
    if ($Length -le 0) { return [pscustomobject]@{ Status = 200; Start = [long]0; End = [long](-1); Count = [long]0 } }

    $h = "$RangeHeader".Trim()
    if ($h.Length -eq 0) { return $whole }
    if ($h -notmatch '^bytes\s*=') { return $whole }           # an unknown range unit is ignorable per the RFC

    $spec = $h.Substring($h.IndexOf('=') + 1).Trim()
    if ($spec.Contains(',')) { return $whole }                 # multi-range -> whole entity
    $m = [regex]::Match($spec, '^(\d*)-(\d*)$')
    if (-not $m.Success) { return $whole }                     # malformed -> whole entity

    $first = $m.Groups[1].Value
    $last = $m.Groups[2].Value
    if ($first.Length -eq 0 -and $last.Length -eq 0) { return $whole }

    $start = [long]0
    $end = [long]0
    if ($first.Length -eq 0) {
        # Suffix form: the LAST n bytes. "bytes=-0" asks for nothing, which is unsatisfiable, not empty.
        $n = [long]0
        if (-not [long]::TryParse($last, [ref]$n)) { return $whole }
        if ($n -le 0) { return $unsatisfiable }
        if ($n -gt $Length) { $n = $Length }
        $start = $Length - $n
        $end = $Length - 1
    }
    else {
        if (-not [long]::TryParse($first, [ref]$start)) { return $whole }
        if ($start -ge $Length) { return $unsatisfiable }      # first-byte-pos past the end
        if ($last.Length -eq 0) {
            $end = $Length - 1
        }
        else {
            if (-not [long]::TryParse($last, [ref]$end)) { return $whole }
            if ($end -lt $start) { return $unsatisfiable }     # inverted range
        }
        if ($end -gt ($Length - 1)) { $end = $Length - 1 }     # clamp, do not 416: "bytes=0-999" of 100 bytes is legal
    }
    [pscustomobject]@{ Status = 206; Start = $start; End = $end; Count = ($end - $start + 1) }
}

function Get-FeedContentType {
    <#
    .SYNOPSIS
      Content-Type for a feed file. App Installer does not require application/appinstaller, but serving the right
      type keeps the loopback feed indistinguishable from the GitHub one when something goes wrong.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path)

    $ext = ''
    try { $ext = [IO.Path]::GetExtension("$Path") } catch { $ext = '' }
    switch ("$ext".ToLowerInvariant()) {
        '.appinstaller' { return 'application/appinstaller' }
        '.msix'         { return 'application/msix' }
        '.msixbundle'   { return 'application/msixbundle' }
        '.appx'         { return 'application/appx' }
        '.appxbundle'   { return 'application/appxbundle' }
        '.json'         { return 'application/json' }
        '.xml'          { return 'application/xml' }
        '.cer'          { return 'application/x-x509-ca-cert' }
        '.txt'          { return 'text/plain' }
        '.log'          { return 'text/plain' }
        '.md'           { return 'text/markdown' }
        '.html'         { return 'text/html' }
        '.png'          { return 'image/png' }
        '.jpg'          { return 'image/jpeg' }
        '.jpeg'         { return 'image/jpeg' }
        '.webp'         { return 'image/webp' }
        '.gif'          { return 'image/gif' }
        '.mp4'          { return 'video/mp4' }
        default         { return 'application/octet-stream' }
    }
}

function Resolve-FeedFile {
    <#
    .SYNOPSIS
      Map a request path onto a real file UNDER the feed root, or $null.
    .DESCRIPTION
      The order matters: strip the query, url-decode (so "%2e%2e" is caught by the same check as ".."), then reject
      any traversal / empty / rooted segment BEFORE combining, and finally re-check that the resolved path is still
      inside the root. Belt and braces, because this listener runs elevated.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$UrlPath)

    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not $rootFull.EndsWith([string][IO.Path]::DirectorySeparatorChar)) {
        $rootFull = $rootFull + [IO.Path]::DirectorySeparatorChar
    }

    $p = "$UrlPath"
    $q = $p.IndexOf('?')
    if ($q -ge 0) { $p = $p.Substring(0, $q) }
    try { $p = [Uri]::UnescapeDataString($p) } catch { return $null }
    $p = $p.Replace('/', '\').TrimStart('\')
    if ($p.Length -eq 0) { return $null }
    if ($p.Contains(':')) { return $null }                     # drive letter, scheme, or an NTFS alternate stream
    if ($p.Contains([string][char]0)) { return $null }

    foreach ($seg in ($p -split '\\')) {
        if ($seg.Length -eq 0) { return $null }                # '//' or a UNC prefix
        if ($seg -eq '.' -or $seg -eq '..') { return $null }
    }

    $full = ''
    try { $full = [IO.Path]::GetFullPath((Join-Path $rootFull $p)) } catch { return $null }
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
    $full
}

function ConvertTo-FeedPath {
    <#
    .SYNOPSIS
      A feed URL, or a logged RawUrl, reduced to the one comparable form: '/wavee-local/Wavee.arm64.appinstaller'.
    .DESCRIPTION
      The harness knows the feed as an absolute URL ($script:FeedUri); the listener logs RawUrl, which is a path,
      may carry a query string, and is percent-encoded. Comparing those two directly is how a request that DID
      happen gets reported as one that did not, so both sides go through here first.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string]$Value)

    $s = "$Value".Trim()
    if ($s.Length -eq 0) { return '' }
    if ($s -match '^[A-Za-z][A-Za-z0-9+.\-]*://') {
        $u = $null
        try { $u = [Uri]$s } catch { $u = $null }
        if ($null -ne $u) { $s = $u.AbsolutePath }
    }
    $q = $s.IndexOf('?')
    if ($q -ge 0) { $s = $s.Substring(0, $q) }
    $h = $s.IndexOf('#')
    if ($h -ge 0) { $s = $s.Substring(0, $h) }
    try { $s = [Uri]::UnescapeDataString($s) } catch { }
    $s = $s.Replace('\', '/').Trim()
    if ($s.Length -eq 0) { return '' }
    if (-not $s.StartsWith('/')) { $s = '/' + $s }
    $s
}

function Get-FeedAssociationRequests {
    <#
    .SYNOPSIS
      The deployment engine's GETs of ONE feed document, after a mark. The App Installer association, proved over
      the wire instead of asked for.
    .DESCRIPTION
      Get-AppxPackageAutoUpdateSettings answers $null both when there is no association AND when the build cannot
      answer, and it has been observed answering "no association" for a package Windows was demonstrably updating
      through its .appinstaller on every launch. The wire cannot lie the same way: when the association exists,
      AppXSvc GETs the .appinstaller at each activation, and the listener logs it with the deployment engine's
      User-Agent ("App Virt Client/1.0"), which is never the app's own ("Wavee/...").

      A row qualifies when ALL of these hold:
        - its User-Agent CONTAINS $UserAgentContains (default 'App Virt Client'), case-insensitively
        - its method is GET, HEAD, or absent (the listener only ever serves those two)
        - its path, normalized by ConvertTo-FeedPath, equals $FeedPath normalized the same way
        - its timestamp parses AND is at or after $Mark

      A row whose timestamp does not parse is DROPPED, never counted: a proof that rests on an unreadable stamp is
      not a proof. $Mark is converted to UTC, because the listener stamps its log in UTC.
    .OUTPUTS
      The matching rows, in log order. Always an array.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$Rows,
        [Parameter(Mandatory = $true)][datetime]$Mark,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$FeedPath,
        [string]$UserAgentContains = 'App Virt Client')

    $want = ConvertTo-FeedPath $FeedPath
    $markUtc = $Mark.ToUniversalTime()
    $styles = [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal
    $culture = [Globalization.CultureInfo]::InvariantCulture

    $out = New-Object System.Collections.ArrayList
    foreach ($r in @($Rows)) {
        if ($null -eq $r) { continue }

        $ua = "$($r.UserAgent)"
        if ("$UserAgentContains".Length -gt 0) {
            if ($ua.IndexOf($UserAgentContains, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
        }

        $m = "$($r.Method)".Trim().ToUpperInvariant()
        if ($m.Length -gt 0 -and $m -ne 'GET' -and $m -ne 'HEAD') { continue }

        if ($want.Length -gt 0) {
            $p = ConvertTo-FeedPath "$($r.Path)"
            if (-not $p.Equals($want, [StringComparison]::OrdinalIgnoreCase)) { continue }
        }

        $t = [datetime]::MinValue
        if (-not [datetime]::TryParse("$($r.Time)", $culture, $styles, [ref]$t)) { continue }
        if ($t -lt $markUtc) { continue }

        [void]$out.Add($r)
    }
    $out.ToArray()
}

function Test-AppInstallerUpdateEvent {
    <#
    .SYNOPSIS
      Does ONE AppXDeploymentServer event message prove an App Installer AUTO-UPDATE association? Pure: a string in,
      a bool out. No event log is read here.
    .DESCRIPTION
      Microsoft-Windows-AppXDeploymentServer/Operational id 603 is "Started deployment <Operation> operation on
      package <full name>" - and <Operation> is ANY deployment operation. A plain Add-AppxPackage of the very package
      the harness is installing writes a 603 too (AddPackageOperation), so counting 603s alone made P6's
      "no auto-update association (Windows cannot preempt the app)" FAIL against its own bare install: the harness
      installed A, saw 1 AppXDeploymentServer 603, and reported "proved by event-603".

      Only UpdateUsingAppInstallerOperation proves the .appinstaller association fired. The operation name must
      therefore appear in the message; $PackageMatch (the package identity name), when given, must appear too.
      Both comparisons are ordinal case-insensitive; a null/empty message is never a proof.
    .OUTPUTS
      [bool]
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][string]$Message,
        [AllowEmptyString()][string]$PackageMatch = '',
        [AllowEmptyString()][string]$OperationContains = 'UpdateUsingAppInstallerOperation')

    $m = "$Message"
    if ($m.Length -eq 0) { return $false }

    if ("$PackageMatch".Length -gt 0) {
        if ($m.IndexOf($PackageMatch, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $false }
    }
    if ("$OperationContains".Length -gt 0) {
        if ($m.IndexOf($OperationContains, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $false }
    }
    $true
}

function Compare-LogSnapshot {
    <#
    .SYNOPSIS
      Did the app's own data survive? Two already-taken {Path;Length} snapshots in, a verdict out.
    .DESCRIPTION
      Pins an observed bug: the package's LocalCache log directory was RESET between the P9 mark and P10 - every
      file the harness had marked was replaced by one fresh 7 KB file - which silently invalidated every log wait
      keyed to that mark AND meant the update had thrown the user's app data away.

      A file is a regression when it is GONE, or when it is SMALLER than it was: an append-only log can only ever
      grow, so a shrink is a truncation or a re-create, never normal operation. Paths compare case-insensitively.
    .OUTPUTS
      pscustomobject @{ Ok; Missing; Shrunk; Detail }
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyCollection()][object[]]$Before,
        [AllowNull()][AllowEmptyCollection()][object[]]$After)

    $now = @{}
    foreach ($a in @($After)) {
        if ($null -eq $a) { continue }
        $p = "$($a.Path)"
        if ($p.Length -eq 0) { continue }
        $now[$p.ToLowerInvariant()] = [long]$a.Length
    }

    $missing = New-Object System.Collections.ArrayList
    $shrunk = New-Object System.Collections.ArrayList
    $seen = 0
    foreach ($b in @($Before)) {
        if ($null -eq $b) { continue }
        $p = "$($b.Path)"
        if ($p.Length -eq 0) { continue }
        $seen++
        $k = $p.ToLowerInvariant()
        if (-not $now.ContainsKey($k)) { [void]$missing.Add($p); continue }
        $was = [long]$b.Length
        $is = [long]$now[$k]
        if ($is -lt $was) { [void]$shrunk.Add(($p + ' ' + $was + ' -> ' + $is)) }
    }

    $parts = @()
    foreach ($m in $missing) { $parts += ('GONE ' + $m) }
    foreach ($s in $shrunk) { $parts += ('SHRANK ' + $s) }
    $detail = ('all ' + $seen + ' file(s) still present and not smaller')
    if ($seen -eq 0) { $detail = 'nothing was snapshotted to compare' }
    if ($parts.Count -gt 0) { $detail = $parts -join ' ; ' }

    [pscustomobject]@{
        Ok      = ($parts.Count -eq 0)
        Missing = @($missing.ToArray())
        Shrunk  = @($shrunk.ToArray())
        Detail  = $detail
    }
}

function ConvertTo-QuadString {
    <#
    .SYNOPSIS
      Any version-ish value -> the normalized four-part string, or '' when it is not a version at all.
    .DESCRIPTION
      Get-AppxPackage hands back a version that stringifies as '0.2.0.9002' on one build and prints through a
      [Version] object on another, and a three-part '0.2.0' has Revision -1, not 0. Comparing those raw against a
      harness parameter is how a package that HAS flipped gets reported as one that has not.
    #>
    [CmdletBinding()]
    param([AllowNull()]$Value)

    $s = "$Value".Trim()
    if ($s.Length -eq 0) { return '' }
    $v = $null
    try { $v = [Version]$s } catch { $v = $null }
    if ($null -eq $v) { return '' }
    $b = $v.Build
    if ($b -lt 0) { $b = 0 }
    $r = $v.Revision
    if ($r -lt 0) { $r = 0 }
    ('' + $v.Major + '.' + $v.Minor + '.' + $b + '.' + $r)
}

function Test-QuadMatch {
    <#  True when both values are versions AND they name the same quad. Two unparseable values are NOT a match. #>
    [CmdletBinding()]
    param([AllowNull()]$Actual, [AllowNull()]$Expected)

    $a = ConvertTo-QuadString $Actual
    $e = ConvertTo-QuadString $Expected
    if ($a.Length -eq 0) { return $false }
    if ($e.Length -eq 0) { return $false }
    ($a -eq $e)
}

function Test-AnyQuadMatch {
    <#
      True when ANY of the versions currently visible is the expected quad. The plural matters: while a deferred
      update is settling, BOTH the outgoing and the incoming version are registered, so a poll that looked at only
      the first package Windows happened to enumerate could watch the whole apply and never see the new one.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyCollection()][object[]]$Versions, [AllowNull()]$Expected)

    foreach ($v in @($Versions)) {
        if (Test-QuadMatch $v $Expected) { return $true }
    }
    $false
}

function Write-FeedLog {
    <#  One tab-separated line, appended with FileShare.ReadWrite so the harness can tail it live. Never throws. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LogPath, [Parameter(Mandatory = $true)][string]$Line)

    for ($i = 0; $i -lt 6; $i++) {
        try {
            $fs = New-Object IO.FileStream($LogPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
            try {
                $sw = New-Object IO.StreamWriter($fs, (New-Object Text.UTF8Encoding $false))
                $sw.WriteLine($Line)
                $sw.Flush()
                $sw.Dispose()
            }
            finally { try { $fs.Dispose() } catch { } }
            return
        }
        catch { Start-Sleep -Milliseconds 25 }
    }
}

# ===================================================================================================================
# The listener
# ===================================================================================================================

function Start-LocalFeedServer {
    <#
    .SYNOPSIS
      Serve $Root over http://<BindHost>:<Port>/ until Stop-LocalFeedServer.
    .DESCRIPTION
      The HttpListener is created and STARTED on the calling thread, so an "Access is denied" bind failure throws
      here - with the exact netsh reservation to run - instead of dying invisibly inside a background runspace. Only
      the accept loop runs in the runspace, over the already-bound listener.
    .OUTPUTS
      pscustomobject @{ Prefix; Port; BindHost; Root; LogPath; Listener; Runspace; PowerShell; Handle; Running }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [int]$Port = 8099,
        [string]$BindHost = '127.0.0.1',
        [string]$LogPath = '')

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "feed root not found: $Root" }
    $rootFull = (Resolve-Path -LiteralPath $Root).Path

    if (-not $LogPath) { $LogPath = Join-Path $rootFull 'feed-requests.log' }
    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
    if (-not (Test-Path -LiteralPath $LogPath)) {
        [IO.File]::WriteAllText($LogPath, '', (New-Object Text.UTF8Encoding $false))
    }

    $prefix = 'http://' + $BindHost + ':' + $Port + '/'
    $listener = New-Object System.Net.HttpListener
    $listener.Prefixes.Add($prefix)
    $listener.IgnoreWriteExceptions = $true
    try {
        $listener.Start()
    }
    catch {
        $who = "$env:USERDOMAIN\$env:USERNAME"
        try { $listener.Close() } catch { }
        $nl = [Environment]::NewLine
        throw ("could not bind $prefix - $($_.Exception.Message)$nl" +
               "Run this elevated, or reserve the URL once (from an elevated prompt):$nl" +
               "  netsh http add urlacl url=http://" + $BindHost + ":" + $Port + "/ user=$who")
    }

    # The accept loop needs the three pure helpers plus the logger; hand it their source rather than importing the
    # module again inside the runspace (one definition, no second copy to drift).
    $defs = @()
    foreach ($n in @('Get-RangeSlice', 'Get-FeedContentType', 'Resolve-FeedFile', 'Write-FeedLog')) {
        $defs += ('function ' + $n + ' {' + (Get-Item ('function:' + $n)).Definition + '}')
    }

    $loopBody = @'
param($Listener, $RootFull, $LogPath)
$ErrorActionPreference = 'Continue'
while ($true) {
    if (-not $Listener.IsListening) { break }
    $ctx = $null
    try { $ctx = $Listener.GetContext() }
    catch { break }
    if ($null -eq $ctx) { break }

    $req = $ctx.Request
    $res = $ctx.Response
    $method = 'GET'
    $rawUrl = '/'
    $rangeText = '-'
    $ua = '-'
    $status = 500
    $sent = [long]0
    $r = ''
    try {
        $method = "$($req.HttpMethod)".ToUpperInvariant()
        $rawUrl = "$($req.RawUrl)"
        $r = "$($req.Headers['Range'])"
        if ($r.Length -gt 0) { $rangeText = $r }
        $u = "$($req.UserAgent)"
        if ($u.Length -gt 0) { $ua = $u.Replace([string][char]9, ' ') }

        $res.AddHeader('Accept-Ranges', 'bytes')
        $res.AddHeader('Cache-Control', 'no-cache')

        if ($method -ne 'GET' -and $method -ne 'HEAD') {
            $body = [Text.Encoding]::UTF8.GetBytes("405 method not allowed: $method")
            $res.StatusCode = 405
            $res.AddHeader('Allow', 'GET, HEAD')
            $res.ContentType = 'text/plain'
            $res.ContentLength64 = $body.Length
            $res.OutputStream.Write($body, 0, $body.Length)
            $status = 405
            $sent = $body.Length
        }
        else {
            $file = Resolve-FeedFile -Root $RootFull -UrlPath "$($req.Url.AbsolutePath)"
            if ($null -eq $file) {
                $body = [Text.Encoding]::UTF8.GetBytes("404 not found: $($req.Url.AbsolutePath)")
                $res.StatusCode = 404
                $res.ContentType = 'text/plain'
                $res.ContentLength64 = $body.Length
                if ($method -eq 'GET') {
                    $res.OutputStream.Write($body, 0, $body.Length)
                    $sent = $body.Length
                }
                $status = 404
            }
            else {
                $fi = New-Object IO.FileInfo $file
                $len = [long]$fi.Length
                $slice = Get-RangeSlice -RangeHeader $r -Length $len
                $res.ContentType = (Get-FeedContentType $file)
                $res.AddHeader('Last-Modified', $fi.LastWriteTimeUtc.ToString('r', [Globalization.CultureInfo]::InvariantCulture))
                if ($slice.Status -eq 416) {
                    $body = [Text.Encoding]::UTF8.GetBytes('416 range not satisfiable')
                    $res.StatusCode = 416
                    $res.AddHeader('Content-Range', "bytes */$len")
                    $res.ContentType = 'text/plain'
                    $res.ContentLength64 = $body.Length
                    if ($method -eq 'GET') {
                        $res.OutputStream.Write($body, 0, $body.Length)
                        $sent = $body.Length
                    }
                    $status = 416
                }
                else {
                    $res.StatusCode = $slice.Status
                    $res.ContentLength64 = $slice.Count
                    if ($slice.Status -eq 206) {
                        $res.AddHeader('Content-Range', "bytes $($slice.Start)-$($slice.End)/$len")
                    }
                    $status = $slice.Status
                    if ($method -eq 'GET' -and $slice.Count -gt 0) {
                        $fs = New-Object IO.FileStream($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                        try {
                            $fs.Position = $slice.Start
                            $buf = New-Object byte[] 65536
                            $left = [long]$slice.Count
                            while ($left -gt 0) {
                                $want = [int][Math]::Min([long]$buf.Length, $left)
                                $n = $fs.Read($buf, 0, $want)
                                if ($n -le 0) { break }
                                $res.OutputStream.Write($buf, 0, $n)
                                $left = $left - $n
                                $sent = $sent + $n
                            }
                        }
                        finally { try { $fs.Dispose() } catch { } }
                    }
                }
            }
        }
    }
    catch {
        try { $res.StatusCode = 500 } catch { }
        $status = 500
    }
    finally {
        try { $res.OutputStream.Close() } catch { }
        try { $res.Close() } catch { }
        $tab = [string][char]9
        $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture)
        Write-FeedLog -LogPath $LogPath -Line ($stamp + $tab + $method + $tab + $rawUrl + $tab + $status + $tab + $rangeText + $tab + $sent + $tab + $ua)
    }
}
'@

    $rs = [runspacefactory]::CreateRunspace()
    $rs.ApartmentState = 'MTA'
    $rs.ThreadOptions = 'ReuseThread'
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    # param(...) must be the FIRST statement of the runspace script: the helper definitions go AFTER it, otherwise the
    # script fails to parse, the accept loop never runs, and every request dies with "status 0" while the bind looks fine.
    $nl = [Environment]::NewLine
    $paramLine = 'param($Listener, $RootFull, $LogPath)'
    $bodyNoParam = $loopBody.Substring($loopBody.IndexOf($nl) + $nl.Length)
    if (-not $loopBody.StartsWith('param(')) { throw 'loop body must start with its param block' }
    $ps.AddScript($paramLine + $nl + ($defs -join $nl) + $nl + $bodyNoParam) | Out-Null
    $ps.AddArgument($listener) | Out-Null
    $ps.AddArgument($rootFull) | Out-Null
    $ps.AddArgument($LogPath) | Out-Null
    $handle = $ps.BeginInvoke()

    [pscustomobject]@{
        Prefix     = $prefix
        Port       = $Port
        BindHost   = $BindHost
        Root       = $rootFull
        LogPath    = $LogPath
        Listener   = $listener
        Runspace   = $rs
        PowerShell = $ps
        Handle     = $handle
        Running    = $true
    }
}

function Stop-LocalFeedServer {
    <#  Idempotent: safe to call in a finally block whether or not the server ever started. #>
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)]$Server)

    process {
        if ($null -eq $Server) { return }
        # Closing the listener is what makes the blocked GetContext() throw and the loop break.
        try { if ($Server.Listener) { $Server.Listener.Stop() } } catch { }
        try { if ($Server.Listener) { $Server.Listener.Close() } } catch { }

        if ($Server.PowerShell) {
            $deadline = (Get-Date).AddSeconds(5)
            while ($Server.Handle -and -not $Server.Handle.IsCompleted -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 50
            }
            try { if ($Server.Handle -and -not $Server.Handle.IsCompleted) { $Server.PowerShell.Stop() } } catch { }
            try { $Server.PowerShell.Dispose() } catch { }
        }
        try { if ($Server.Runspace) { $Server.Runspace.Close(); $Server.Runspace.Dispose() } } catch { }
        $Server.Running = $false
    }
}

Export-ModuleMember -Function @(
    'Get-RangeSlice',
    'Get-FeedContentType',
    'Resolve-FeedFile',
    'ConvertTo-FeedPath',
    'Get-FeedAssociationRequests',
    'Test-AppInstallerUpdateEvent',
    'Compare-LogSnapshot',
    'ConvertTo-QuadString',
    'Test-QuadMatch',
    'Test-AnyQuadMatch',
    'Start-LocalFeedServer',
    'Stop-LocalFeedServer')
