#requires -Version 5.1
<#
    Wavee.Release.psm1 - the pure helpers behind ops\release\wavee-release.ps1.

    Everything here is either a pure function (semver / quad / manifest / .appinstaller substitution, the crash
    ingest gate, stamp evidence) or a thin, testable wrapper over one external tool (gh, op, wrangler, dotnet) or
    one HTTP GET (the rolling feed). The orchestrators (wavee-release.ps1, wavee-store-submit.ps1) own the phase
    sequencing and the ledgers; this module owns the decisions.

    Style rules: PowerShell 5.1 only, ASCII-only string literals (an em dash is [char]0x2014), UTF-8 without a BOM
    on every file this module writes, no && / || / ternary, and TLS 1.2 forced before every Invoke-WebRequest.
#>

$script:BuildModulePath = Join-Path $PSScriptRoot '..\build\Wavee.Build.psm1'
if (-not (Test-Path $script:BuildModulePath)) {
    throw "Wavee.Build.psm1 not found next to this module (expected $($script:BuildModulePath)). ops\build and ops\release ship together."
}
# -Global: this nested -Force import would otherwise unload the build module from any caller that imported it first.
Import-Module $script:BuildModulePath -Force -DisableNameChecking -Global

function Set-WaveeTls12 {
    <#  Windows PowerShell 5.1 still defaults ServicePointManager to SSL3/TLS1.0; github.com rejects both. #>
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

function New-Utf8NoBom {
    New-Object System.Text.UTF8Encoding $false
}

# ---------------------------------------------------------------------------------------------------------------
# Versioning
# ---------------------------------------------------------------------------------------------------------------

function Test-WaveeSemver {
    <#
    .SYNOPSIS
      Parse and validate a Wavee semver: M.m.p or M.m.p-beta.N (N >= 1). Throws on anything else.
    .OUTPUTS
      pscustomobject @{ Major; Minor; Patch; Beta; Channel; Core; Semver }
    #>
    param([Parameter(Mandatory = $true)][string]$Semver)

    $m = [regex]::Match($Semver, '^(?<M>\d+)\.(?<m>\d+)\.(?<p>\d+)(?:-beta\.(?<b>[1-9]\d*))?$')
    if (-not $m.Success) { throw "bad semver: $Semver (expected M.m.p or M.m.p-beta.N)" }

    $beta = $null
    $channel = 'stable'
    if ($m.Groups['b'].Success) {
        $beta = [int]$m.Groups['b'].Value
        $channel = 'beta'
    }
    [pscustomobject]@{
        Major   = [int]$m.Groups['M'].Value
        Minor   = [int]$m.Groups['m'].Value
        Patch   = [int]$m.Groups['p'].Value
        Beta    = $beta
        Channel = $channel
        Core    = "$($m.Groups['M'].Value).$($m.Groups['m'].Value).$($m.Groups['p'].Value)"
        Semver  = $Semver
    }
}

function ConvertTo-WaveeQuad {
    <#
    .SYNOPSIS
      semver + build counter -> the MSIX Identity/@Version quad. The prerelease suffix is stripped: MSIX has no
      notion of a prerelease, so a beta ships as its core version with the shared monotonic build counter.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][int]$Build)

    $s = Test-WaveeSemver $Semver
    foreach ($p in @($s.Major, $s.Minor, $s.Patch, $Build)) {
        if ($p -gt 65535) { throw "version part greater than 65535 in $Semver + build $Build (MSIX allows 0..65535 per part)" }
        if ($p -lt 0) { throw "version part below 0 in $Semver + build $Build" }
    }
    "$($s.Core).$Build"
}

# ---------------------------------------------------------------------------------------------------------------
# The rolling feed
# ---------------------------------------------------------------------------------------------------------------

function Get-WaveeFeedDocument {
    <#
    .SYNOPSIS
      GET one published .appinstaller and return what the release path actually compares against it. $null when the
      feed release (or the asset) does not exist yet - a 404 is a normal first-release state, not an error.
    .OUTPUTS
      pscustomobject @{ Version ([version]); MsixUri; Uri; Arch; Xml } or $null
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$FeedRelease,
        [Parameter(Mandatory = $true)][string]$Arch,
        [string]$AssetPrefix = 'Wavee')

    Set-WaveeTls12
    $url = "https://github.com/$Repo/releases/download/$FeedRelease/$AssetPrefix.$Arch.appinstaller"
    # -DisableKeepAlive: Windows PowerShell 5.1 reuses the ServicePoint connection across calls, and an error
    # response it did not dispose (the 404 below) leaves that connection half-closed - the NEXT feed GET (the
    # other arch) then dies with "The connection was closed unexpectedly". One connection per call, closed.
    # ONE retry on a response-less failure: the very first GET of a process regularly dies with "Unable to
    # connect" / "connection closed unexpectedly" (cold DNS/TLS) and the immediate second attempt gets the real
    # answer. A failure WITH a response (the 404 of a feed that does not exist yet) is an answer, not a retry.
    $r = $null
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri $url -MaximumRedirection 5 -DisableKeepAlive
            break
        }
        catch {
            $resp = $null
            if ($_.Exception.PSObject.Properties['Response']) { $resp = $_.Exception.Response }
            if ($resp) {
                $status = [int]$resp.StatusCode
                try { $resp.Close() } catch { }
                if ($status -eq 404) { return $null }
                throw
            }
            if ($attempt -ge 2) { throw }
            Start-Sleep -Seconds 2
        }
    }
    # GitHub serves the .appinstaller as application/octet-stream, so on 5.1 -UseBasicParsing hands back a byte[]
    # (a text content-type gives a string). Stringifying the array yields "60 63 120 ..." - decode it instead.
    $raw = $r.Content
    if ($raw -is [byte[]]) { $txt = [System.Text.Encoding]::UTF8.GetString($raw) } else { $txt = [string]$raw }
    $txt = $txt.TrimStart([char]0xFEFF)
    [xml]$x = $txt
    [pscustomobject]@{
        Version = [version]$x.AppInstaller.Version
        Uri     = "$($x.AppInstaller.Uri)"
        MsixUri = "$($x.AppInstaller.MainPackage.Uri)"
        Arch    = "$($x.AppInstaller.MainPackage.ProcessorArchitecture)"
        Xml     = $x
    }
}

function Get-WaveeFeedVersion {
    <#
    .SYNOPSIS
      The root Version attribute of a published .appinstaller. $null when the feed release (or the asset) does not
      exist yet. A thin projection of Get-WaveeFeedDocument, kept because it is what the runbook tells an operator
      to call by hand.
    .OUTPUTS
      [version] or $null
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$FeedRelease,
        [Parameter(Mandatory = $true)][string]$Arch,
        [string]$AssetPrefix = 'Wavee')

    $doc = Get-WaveeFeedDocument $Repo $FeedRelease $Arch $AssetPrefix
    if ($null -eq $doc) { return $null }
    $doc.Version
}

function Test-FeedMonotonic {
    <#
    .SYNOPSIS
      The gate that makes ForceUpdateFromAnyVersion safe: refuse to publish unless the new quad is strictly greater
      than every feed head we are about to repoint, and the new semver core is not behind any feed head's core.
      Throws listing every offending row; returns the rows on success so the caller can print them.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string[]]$FeedRelease,
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][string[]]$Arch,
        [string]$AssetPrefix = 'Wavee')

    $new = [version]$Quad
    $core = [version](Test-WaveeSemver $Semver).Core
    $bad = @()
    $rows = @()
    foreach ($f in $FeedRelease) {
        foreach ($a in $Arch) {
            $cur = Get-WaveeFeedVersion $Repo $f $a $AssetPrefix
            $rows += [pscustomobject]@{ Feed = $f; Arch = $a; Current = $cur; New = $new }
            if ($null -ne $cur) {
                if ($new -le $cur) { $bad += "$f/$a : $new is not greater than $cur" }
                $curCore = [version]"$($cur.Major).$($cur.Minor).$($cur.Build)"
                if ($core -lt $curCore) { $bad += "$f/$a : semver $core is behind feed core $curCore" }
            }
        }
    }
    if ($bad.Count -gt 0) { throw ("feed monotonic gate failed:`n  " + ($bad -join "`n  ")) }
    return ,$rows
}

function Test-WaveeFeedLive {
    <#
    .SYNOPSIS
      Poll the published feed until its root Version equals the quad we just shipped - and, when -ExpectedMsixUri is
      given, until MainPackage/@Uri is the package of THIS release. GitHub asset replacement is not instantaneous, so
      this retries rather than asserting once.
    .DESCRIPTION
      The version alone is not proof: a feed document whose root Version was bumped but whose MainPackage/@Uri still
      points at the previous tag hands every client an update that downloads the OLD msix. That is exactly the shape
      a half-finished upload leaves behind, so phase 11 passes the URI it wrote and this compares it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$FeedRelease,
        [Parameter(Mandatory = $true)][string]$Arch,
        [string]$AssetPrefix = 'Wavee',
        [Parameter(Mandatory = $true)][string]$ExpectedQuad,
        [string]$ExpectedMsixUri = '',
        [int]$Retries = 6,
        [int]$DelaySeconds = 10)

    $lastUri = ''
    for ($i = 0; $i -lt $Retries; $i++) {
        $doc = $null
        try { $doc = Get-WaveeFeedDocument $Repo $FeedRelease $Arch $AssetPrefix } catch { $doc = $null }
        if ($doc) {
            $lastUri = "$($doc.MsixUri)"
            if ("$($doc.Version)" -eq $ExpectedQuad) {
                if (-not $ExpectedMsixUri) { return $true }
                if ($lastUri -eq $ExpectedMsixUri) { return $true }
            }
        }
        if ($i -lt ($Retries - 1)) { Start-Sleep -Seconds $DelaySeconds }
    }
    if ($ExpectedMsixUri -and $lastUri -and $lastUri -ne $ExpectedMsixUri) {
        Write-Warning "$FeedRelease/$Arch MainPackage/@Uri is '$lastUri', expected '$ExpectedMsixUri'"
    }
    $false
}

function New-WaveeAppInstaller {
    <#
    .SYNOPSIS
      Render ops\build\Wavee.AppInstaller.template.xml for one architecture and verify the result by parsing it.
      Every placeholder must be gone: a surviving __TOKEN__ means the template grew a knob this function does not
      know about, and shipping that would point real clients at a literal placeholder URL.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Template,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Arch,
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][string]$Publisher,
        [Parameter(Mandatory = $true)][string]$IdentityName,
        [Parameter(Mandatory = $true)][string]$FeedUri,
        [Parameter(Mandatory = $true)][string]$MsixUri)

    if (-not (Test-Path $Template)) { throw "appinstaller template not found: $Template" }
    $t = [IO.File]::ReadAllText($Template)
    $t = $t.Replace('__VERSION__', $Quad).Replace('__ARCH__', $Arch).Replace('__PUBLISHER__', $Publisher)
    $t = $t.Replace('__IDENTITY__', $IdentityName).Replace('__APPINSTALLER_URI__', $FeedUri).Replace('__MSIX_URI__', $MsixUri)

    # Check BEFORE writing: a document with a live __TOKEN__ in it must never exist on disk, or a resumed run (or a
    # careless upload) can ship the placeholder as if it were a URL.
    $leftover = [regex]::Matches($t, '__[A-Z0-9_]+__')
    if ($leftover.Count -gt 0) {
        $names = (($leftover | ForEach-Object { $_.Value }) | Sort-Object -Unique) -join ', '
        throw "appinstaller template has unsubstituted placeholders ($names): $Template"
    }

    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($OutFile, $t, (New-Utf8NoBom))

    [xml]$x = [IO.File]::ReadAllText($OutFile).TrimStart([char]0xFEFF)
    $errs = @()
    if ("$($x.AppInstaller.Version)" -ne $Quad) { $errs += "root Version = '$($x.AppInstaller.Version)' (want $Quad)" }
    if ("$($x.AppInstaller.Uri)" -ne $FeedUri) { $errs += "root Uri = '$($x.AppInstaller.Uri)' (want $FeedUri)" }
    if ("$($x.AppInstaller.MainPackage.Name)" -ne $IdentityName) { $errs += "MainPackage/@Name = '$($x.AppInstaller.MainPackage.Name)' (want $IdentityName)" }
    if ("$($x.AppInstaller.MainPackage.Publisher)" -ne $Publisher) { $errs += "MainPackage/@Publisher = '$($x.AppInstaller.MainPackage.Publisher)' (want $Publisher)" }
    if ("$($x.AppInstaller.MainPackage.Version)" -ne $Quad) { $errs += "MainPackage/@Version = '$($x.AppInstaller.MainPackage.Version)' (want $Quad)" }
    if ("$($x.AppInstaller.MainPackage.ProcessorArchitecture)" -ne $Arch) { $errs += "MainPackage/@ProcessorArchitecture = '$($x.AppInstaller.MainPackage.ProcessorArchitecture)' (want $Arch)" }
    if ("$($x.AppInstaller.MainPackage.Uri)" -ne $MsixUri) { $errs += "MainPackage/@Uri = '$($x.AppInstaller.MainPackage.Uri)' (want $MsixUri)" }
    if ($errs.Count -gt 0) { throw ("appinstaller substitution failed for $OutFile :`n  " + ($errs -join "`n  ")) }
    $OutFile
}

# ---------------------------------------------------------------------------------------------------------------
# Staging manifest (sha256sum format, so `sha256sum -c MANIFEST.txt` works verbatim)
# ---------------------------------------------------------------------------------------------------------------

function Write-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,
        [Parameter(Mandatory = $true)][string[]]$Files,
        [Parameter(Mandatory = $true)][string]$OutFile)

    $lines = @()
    foreach ($f in ($Files | Sort-Object)) {
        $p = Join-Path $Dir $f
        if (-not (Test-Path $p)) { throw "cannot hash a missing file: $p" }
        $h = (Get-FileHash $p -Algorithm SHA256).Hash.ToLower()
        $lines += "$h  $f"
    }
    [IO.File]::WriteAllText($OutFile, (($lines -join "`n") + "`n"), (New-Utf8NoBom))
    $OutFile
}

function Test-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,
        [Parameter(Mandatory = $true)][string]$ManifestFile)

    if (-not (Test-Path $ManifestFile)) { return $false }
    foreach ($l in (Get-Content $ManifestFile)) {
        if ([string]::IsNullOrWhiteSpace($l)) { continue }
        $parts = $l -split '  ', 2
        if ($parts.Count -ne 2) { return $false }
        $h = $parts[0]
        $n = $parts[1]
        $p = Join-Path $Dir $n
        if (-not (Test-Path $p)) { return $false }
        if ((Get-FileHash $p -Algorithm SHA256).Hash.ToLower() -ne $h.ToLower()) { return $false }
    }
    $true
}

# ---------------------------------------------------------------------------------------------------------------
# GitHub (gh CLI)
# ---------------------------------------------------------------------------------------------------------------

function Invoke-Gh {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [switch]$AllowFailure)

    $r = Invoke-Native 'gh' $Arguments -AllowFailure
    if ($r.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "gh $($Arguments -join ' ') failed (exit $($r.ExitCode)):`n$($r.Output -join "`n")"
    }
    ($r.Output -join "`n")
}

function Get-GhAuthToken {
    <#
    .SYNOPSIS
      The GitHub token `gh auth token` prints, or '' when there is none. NEVER printed, logged or written to the
      release ledger - the caller passes it to a child process through the environment only.
    .DESCRIPTION
      `gh` writes upgrade notices and "gh: ..." diagnostics to STDERR, and Invoke-Native merges stdout+stderr, so the
      captured text is not a single line. The token is the LAST non-empty line, and it must look like a GitHub token
      (gho_/ghp_/ghs_/ghu_/ghr_ or github_pat_) - anything else means gh printed prose where a token was expected,
      and passing that prose on as credentials would fail deep inside the tool with an unrelated 401.
    .PARAMETER RawOutput
      Test seam: the already-captured lines to parse instead of running gh.
    .OUTPUTS
      The token string, or '' when gh has no token. Throws when gh produced output that is not a token.
    #>
    param([string[]]$RawOutput)

    $lines = $RawOutput
    if ($null -eq $lines) {
        $r = Invoke-Native 'gh' @('auth', 'token') -AllowFailure
        if ($r.ExitCode -ne 0) { return '' }
        $lines = $r.Output
    }

    $candidate = ''
    foreach ($l in @($lines)) {
        $s = "$l".Trim()
        if ($s.Length -gt 0) { $candidate = $s }
    }
    if ($candidate.Length -eq 0) { return '' }
    if ($candidate -notmatch '^(gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)$') {
        throw "gh auth token did not print a GitHub token (got $($candidate.Length) character(s) that do not match gh*_/github_pat_). Run 'gh auth status' and fix the CLI before releasing."
    }
    $candidate
}

function ConvertFrom-GhJson {
    <#  gh occasionally prefixes stdout with a notice line; keep only the JSON payload. #>
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $lines = $Text -split "`r?`n"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].TrimStart()
        if ($t.StartsWith('{') -or $t.StartsWith('[')) { $start = $i; break }
    }
    if ($start -lt 0) { return $null }
    (($lines[$start..($lines.Count - 1)]) -join "`n") | ConvertFrom-Json
}

function Get-GhRelease {
    <#  $null when the release does not exist (gh exits non-zero and says "release not found"). #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$Tag)

    $r = Invoke-Native 'gh' @('release', 'view', $Tag, '--repo', $Repo, '--json', 'tagName,isDraft,isPrerelease,assets') -AllowFailure
    if ($r.ExitCode -ne 0) { return $null }
    ConvertFrom-GhJson (($r.Output -join "`n"))
}

function Get-GhReleaseAssetUrl {
    <#  The browser download URL for one asset; falls back to the canonical releases/download form. #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Name,
        $Release)

    if (-not $Release) { $Release = Get-GhRelease $Repo $Tag }
    if ($Release -and $Release.assets) {
        $hit = @($Release.assets | Where-Object { $_.name -eq $Name })
        if ($hit.Count -gt 0 -and $hit[0].url) { return "$($hit[0].url)" }
    }
    "https://github.com/$Repo/releases/download/$Tag/$Name"
}

function Test-AssetContentLength {
    <#
    .SYNOPSIS
      HEAD a published asset and compare Content-Length with the bytes we staged. This is the check that proves the
      bytes on the CDN are the bytes we signed - a truncated upload otherwise only surfaces on a user's machine.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes,
        [int]$Retries = 5,
        [int]$DelaySeconds = 5)

    Set-WaveeTls12
    $last = -1
    for ($i = 0; $i -lt $Retries; $i++) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Method Head -Uri $Url -MaximumRedirection 5
            $len = -1
            $cl = $r.Headers['Content-Length']
            if ($cl) { $len = [long](@($cl)[0]) }
            $last = $len
            if ($len -eq $ExpectedBytes) { return $true }
        }
        catch { $last = -1 }
        if ($i -lt ($Retries - 1)) { Start-Sleep -Seconds $DelaySeconds }
    }
    Write-Warning "content-length mismatch for $Url : got $last, expected $ExpectedBytes"
    $false
}

# ---------------------------------------------------------------------------------------------------------------
# Crash reporting (#165): the ingest stamp every shipping build carries, and the symbol maps the crash Worker
# resolves report RVAs against. Shared by wavee-release.ps1 and wavee-store-submit.ps1.
#
# The ingest KEY is a credential: no function here ever prints it, puts it in an exception message, or returns it
# anywhere but Resolve-CrashIngestKey's .Key. Callers keep it in memory only (never in release-state.json).
# ---------------------------------------------------------------------------------------------------------------

$script:CrashDefaults = @{
    Url       = 'https://crash.cproducts.dev'
    KeyRef    = 'op://Personal/Wavee crash ingest key/credential'
    OpAccount = 'my.1password.eu'
}

function Get-CrashIngestGate {
    <#
    .SYNOPSIS
      The hard gate: a shipping build (stable / beta / store) must be stamped with an https ingest URL AND the
      ingest key, or its crashes can never reach the crash service. Only `dev` may ship unstamped.
    .DESCRIPTION
      Whitespace counts as empty. The message names what is missing and never contains the key (nor the URL, so a
      key pasted into the wrong parameter cannot leak through it either).
    .OUTPUTS
      pscustomobject @{ Fail = [bool]; Message = [string] }   (Message is '' when Fail is $false)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$CrashIngestUrl,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$CrashIngestKey)

    $shipping = @('stable', 'beta', 'store') -contains $Channel
    $missing = @()
    $u = "$CrashIngestUrl".Trim()
    if ($u.Length -eq 0) { $missing += 'the ingest URL (-CrashIngestUrl)' }
    elseif ($u -notmatch '^https://') { $missing += 'an https ingest URL' }
    if ("$CrashIngestKey".Trim().Length -eq 0) { $missing += 'the ingest key' }
    if (-not $shipping -or $missing.Count -eq 0) { return [pscustomobject]@{ Fail = $false; Message = '' } }
    [pscustomobject]@{
        Fail    = $true
        Message = "$Channel build without $($missing -join ' and '): its crashes could never reach the crash service"
    }
}

function Resolve-CrashIngestKey {
    <#
    .SYNOPSIS
      The crash ingest key: -Explicit when given (trimmed), otherwise `op read <Reference> --account <Account>`
      (the 1Password CLI; reading triggers a 1Password approval).
    .DESCRIPTION
      Invoke-Native merges stdout and stderr, so op's notices can surround the secret. The key is the LAST line that
      looks like one (16-256 characters of [A-Za-z0-9_-.=+/]). Every failure throws WITHOUT echoing op's output: if
      something unexpected was printed, it may well have been the secret.
    .OUTPUTS
      pscustomobject @{ Key = [string]; Source = [string] }   - print .Source, never .Key
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$Explicit = '',
        [string]$Reference = $script:CrashDefaults.KeyRef,
        [string]$Account = $script:CrashDefaults.OpAccount)

    $e = "$Explicit".Trim()
    if ($e) { return [pscustomobject]@{ Key = $e; Source = '-CrashIngestKey' } }

    $r = Invoke-Native 'op' @('read', $Reference, '--account', $Account) -AllowFailure
    if ($r.ExitCode -eq 127) {
        throw "could not read the crash ingest key: the 1Password CLI (op) is not installed. Install it, or pass -CrashIngestKey"
    }
    if ($r.ExitCode -ne 0) {
        throw "could not read the crash ingest key from 1Password ($Reference, account $Account; op exited $($r.ExitCode)). Unlock 1Password or pass -CrashIngestKey"
    }
    $key = @($r.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_\-\.=+/]{16,256}$' }) | Select-Object -Last 1
    if (-not $key) { throw "1Password returned no usable crash ingest key at $Reference (account $Account)" }
    [pscustomobject]@{ Key = "$key"; Source = "1Password ($Reference)" }
}

function Get-WranglerPath {
    <#
    .SYNOPSIS
      The crash Worker's own wrangler (ops\crash\worker\node_modules\.bin\wrangler.cmd) - the version the Worker is
      deployed with. There is deliberately no fallback to a global `wrangler`.
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $p = Join-Path $RepoRoot 'ops\crash\worker\node_modules\.bin\wrangler.cmd'
    if (-not (Test-Path -LiteralPath $p)) {
        throw "the crash Worker's wrangler is not installed ($p): run 'npm --prefix ops/crash/worker ci'"
    }
    $p
}

function Test-WranglerLogin {
    <#
    .SYNOPSIS
      Pure: read `wrangler whoami`'s exit code and output. Ok when it exited 0 and does not ask for a login; Detail
      is the account e-mail when wrangler printed one, else 'logged in', else what to do about it.
    .OUTPUTS
      pscustomobject @{ Ok = [bool]; Detail = [string] }
    #>
    param([int]$ExitCode, [AllowEmptyCollection()][string[]]$Output)

    $t = @($Output) -join "`n"
    if ($ExitCode -ne 0) { return [pscustomobject]@{ Ok = $false; Detail = "wrangler whoami exited $ExitCode" } }
    # The account line first: a logged-in whoami also prints advice text, which must not read as "not logged in".
    $m = [regex]::Match($t, 'associated with the email\s+(\S+?)\.?(\s|$)')
    if ($m.Success) { return [pscustomobject]@{ Ok = $true; Detail = $m.Groups[1].Value } }
    if ($t -match 'not authenticated|wrangler login') {
        return [pscustomobject]@{ Ok = $false; Detail = "wrangler is not logged in: run 'npx wrangler login' in ops/crash/worker" }
    }
    [pscustomobject]@{ Ok = $true; Detail = 'logged in' }
}

function Get-ZipSingleEntry {
    <#  Private: the one entry named exactly $Entry (ordinal, case-sensitive) in an open zip, or throw. #>
    param([Parameter(Mandatory = $true)]$Zip, [Parameter(Mandatory = $true)][string]$Entry, [string]$Path)
    $hits = @($Zip.Entries | Where-Object { $_.FullName -ceq $Entry })
    if ($hits.Count -ne 1) { throw "$(Split-Path -Leaf $Path) carries $($hits.Count) '$Entry' entries (expected exactly one)" }
    $hits[0]
}

function Get-ZipEntryBytes {
    <#
    .SYNOPSIS
      The bytes of one entry of a zip (an .msix is a plain zip), e.g. Wavee.exe out of a package.
    .OUTPUTS
      byte[] (returned whole, never unrolled into the pipeline)
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Entry)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $e = Get-ZipSingleEntry -Zip $zip -Entry $Entry -Path $Path
        if ($e.Length -gt [int]::MaxValue) { throw "$Entry in $(Split-Path -Leaf $Path) is larger than 2 GB" }
        $buf = New-Object byte[] ([int]$e.Length)
        $s = $e.Open()
        try {
            $off = 0
            while ($off -lt $buf.Length) {
                $n = $s.Read($buf, $off, $buf.Length - $off)
                if ($n -le 0) { throw "$Entry in $(Split-Path -Leaf $Path) is truncated" }
                $off += $n
            }
        }
        finally { $s.Dispose() }
    }
    finally { $zip.Dispose() }
    , $buf
}

function Export-ZipEntry {
    <#  Private: stream one zip entry to a file (overwriting it). #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Entry,
        [Parameter(Mandatory = $true)][string]$OutFile)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $e = Get-ZipSingleEntry -Zip $zip -Entry $Entry -Path $Path
        $in = $e.Open()
        try {
            $out = [IO.File]::Create($OutFile)
            try { $in.CopyTo($out) } finally { $out.Dispose() }
        }
        finally { $in.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Expand-ZipMissing {
    <#
      Private: extract every file of a zip into $Destination that is NOT already there. Never overwrites: a symbols
      folder whose SYMBOLS.txt / Wavee.map.xml are hash-registered evidence keeps exactly those bytes. Refuses an
      entry that would land outside $Destination.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Destination)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $destFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
    [IO.Directory]::CreateDirectory($destFull) | Out-Null
    # Normalised through GetFullPath like every entry below: Windows PowerShell's GetFullPath expands 8.3 short names
    # (C:\Users\CHRIST~1\...), so a root left un-normalised would never prefix-match its own entries.
    $destRoot = [IO.Path]::GetFullPath($destFull).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        foreach ($e in $zip.Entries) {
            if (-not $e.Name) { continue }   # a directory entry
            $dst = [IO.Path]::GetFullPath($destRoot + ($e.FullName -replace '/', '\'))
            if (-not $dst.StartsWith($destRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "zip entry escapes its destination: $($e.FullName)" }
            if ([IO.File]::Exists($dst)) { continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dst)) | Out-Null
            $in = $e.Open()
            try {
                $out = [IO.File]::Create($dst)
                try { $in.CopyTo($out) } finally { $out.Dispose() }
            }
            finally { $in.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function Find-ByteText {
    <#
    .SYNOPSIS
      Where $Text occurs in $Bytes as UTF-8 and as UTF-16LE. The haystack is decoded as Latin-1 (one char per byte,
      no byte sequence is invalid), so an ordinal IndexOf of the encoded needle is a byte search.
    .OUTPUTS
      pscustomobject @{ Utf8 = [int]; Utf16 = [int] }   (byte offsets; -1 when absent)
    #>
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$Text)

    $l1 = [Text.Encoding]::GetEncoding(28591)
    $hay = $l1.GetString($Bytes)
    [pscustomobject]@{
        Utf8  = $hay.IndexOf($l1.GetString([Text.Encoding]::UTF8.GetBytes($Text)), [StringComparison]::Ordinal)
        Utf16 = $hay.IndexOf($l1.GetString([Text.Encoding]::Unicode.GetBytes($Text)), [StringComparison]::Ordinal)
    }
}

function Assert-CrashIngestStamp {
    <#
    .SYNOPSIS
      Stamp evidence: the Wavee.exe inside -Msix (or the file -ExePath) must carry BOTH the ingest URL and the ingest
      key, as UTF-8 or UTF-16LE (the csproj stamps them as AssemblyMetadata). Throws naming what is missing.
    .DESCRIPTION
      This is what proves a package will report crashes - it reads the bytes that ship, so it holds for a freshly
      packed package and for an adopted prebuilt one alike. Neither the result nor any message contains the key.
    .OUTPUTS
      [string] "Wavee.exe carries <url> (utf8@0x...) and the ingest key (utf16)"
    #>
    [CmdletBinding(DefaultParameterSetName = 'Msix')]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Msix')][string]$Msix,
        [Parameter(Mandatory = $true, ParameterSetName = 'Exe')][string]$ExePath,
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Key)

    if ($PSCmdlet.ParameterSetName -eq 'Msix') {
        if (-not (Test-Path -LiteralPath $Msix)) { throw "stamp evidence: package not found: $Msix" }
        $what = "Wavee.exe in $(Split-Path -Leaf $Msix)"
        $bytes = Get-ZipEntryBytes -Path $Msix -Entry 'Wavee.exe'
    }
    else {
        if (-not (Test-Path -LiteralPath $ExePath)) { throw "stamp evidence: executable not found: $ExePath" }
        $what = $ExePath
        $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ExePath).Path)
    }

    $u = Find-ByteText -Bytes $bytes -Text $Url
    $k = Find-ByteText -Bytes $bytes -Text $Key
    $problems = @()
    if ($u.Utf8 -lt 0 -and $u.Utf16 -lt 0) { $problems += "the ingest URL $Url" }
    if ($k.Utf8 -lt 0 -and $k.Utf16 -lt 0) { $problems += 'the ingest key (not printed)' }
    if ($problems.Count -gt 0) {
        throw "$what does not carry $($problems -join ' or '): it was not built with this -CrashIngestUrl/-CrashIngestKey, so its crashes could never reach the crash service"
    }

    $uWhere = if ($u.Utf8 -ge 0) { 'utf8@0x{0:X}' -f $u.Utf8 } else { 'utf16@0x{0:X}' -f $u.Utf16 }
    $kEnc = if ($k.Utf8 -ge 0) { 'utf8' } else { 'utf16' }
    "Wavee.exe carries $Url ($uWhere) and the ingest key ($kEnc)"
}

function Invoke-SymbolsUpload {
    <#
    .SYNOPSIS
      Upload one architecture's .symmap to R2 with the crash Worker's wrangler (`r2 object put ... --remote`), or
      report why it was skipped.
    .DESCRIPTION
      The R2 key and the wrangler argument list are computed HERE so Wavee.Release.Tests.ps1 can assert on them (via
      a mocked Invoke-Native) without a real Cloudflare account. -Wrangler (Get-WranglerPath) is required unless
      -Skip; -Skip covers -DryRun / -NoUpload - the caller decides which and passes -SkipReason for the Warn text.
    .OUTPUTS
      pscustomobject @{ Uploaded = [bool]; Key = "symbols/<quad>/win-<arch>.symmap"; Reason = [string] }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SymmapPath,
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][string]$Arch,
        [string]$Wrangler = '',
        [string]$Bucket = 'wavee-crash',
        [switch]$Skip,
        [string]$SkipReason = 'nothing will be uploaded')

    $key = "symbols/$Quad/win-$Arch.symmap"
    if ($Skip) {
        return [pscustomobject]@{ Uploaded = $false; Key = $key; Reason = $SkipReason }
    }
    if (-not $Wrangler) { throw 'Invoke-SymbolsUpload: -Wrangler <the crash Worker''s wrangler.cmd, see Get-WranglerPath> is required unless -Skip' }
    if (-not (Test-Path -LiteralPath $SymmapPath)) { throw "Invoke-SymbolsUpload: symmap not found: $SymmapPath" }

    Invoke-Native $Wrangler @('r2', 'object', 'put', "$Bucket/$key", '--file', $SymmapPath, '--remote') | Out-Null
    [pscustomobject]@{ Uploaded = $true; Key = $key; Reason = '' }
}

function Publish-WaveeSymbolMap {
    <#
    .SYNOPSIS
      Build one architecture's Wavee.symmap with Wavee.ReleaseTool and upload it for the crash Worker.
    .DESCRIPTION
      1. Wavee.pdb must be in -SymbolsDir; when it is not (an adopted prebuilt package), -SymbolsZip is expanded
         there first (files already present are never overwritten).
      2. -Msix: Wavee.exe is extracted from the package into -SymbolsDir and removed again in a finally.
         -ExePath: that file is used as-is and never touched.
      3. dotnet run --project <ReleaseToolProject> -c Release -- symbol-map --pdb --exe --out <SymbolsDir>\Wavee.symmap
      4. Invoke-SymbolsUpload -Wrangler (skipped under -SkipUpload, which then needs no -Wrangler).
    .OUTPUTS
      pscustomobject @{ Symmap; Key; Uploaded; Reason; Bytes }
    #>
    [CmdletBinding(DefaultParameterSetName = 'Msix')]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Msix')][string]$Msix,
        [Parameter(Mandatory = $true, ParameterSetName = 'Exe')][string]$ExePath,
        [Parameter(Mandatory = $true)][string]$SymbolsDir,
        [string]$SymbolsZip = '',
        [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Quad,
        [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Arch,
        [Parameter(Mandatory = $true)][string]$ReleaseToolProject,
        [string]$Wrangler = '',
        [string]$Bucket = 'wavee-crash',
        [switch]$SkipUpload,
        [string]$SkipReason = '-SkipUpload')

    if (-not $SkipUpload -and -not $Wrangler) {
        throw 'Publish-WaveeSymbolMap: -Wrangler <the crash Worker''s wrangler.cmd, see Get-WranglerPath> is required unless -SkipUpload'
    }

    $pdb = Join-Path $SymbolsDir 'Wavee.pdb'
    if (-not (Test-Path -LiteralPath $pdb)) {
        if (-not $SymbolsZip -or -not (Test-Path -LiteralPath $SymbolsZip)) {
            $zipNote = ''
            if ($SymbolsZip) { $zipNote = " ($SymbolsZip does not exist)" }
            throw "symbols ($Arch): no Wavee.pdb in $SymbolsDir and no symbols zip to expand it from$zipNote"
        }
        Expand-ZipMissing -Path $SymbolsZip -Destination $SymbolsDir
        if (-not (Test-Path -LiteralPath $pdb)) { throw "symbols ($Arch): $SymbolsZip did not contain Wavee.pdb" }
    }

    $symmap = Join-Path $SymbolsDir 'Wavee.symmap'
    $extracted = $null
    try {
        if ($PSCmdlet.ParameterSetName -eq 'Msix') {
            if (-not (Test-Path -LiteralPath $Msix)) { throw "symbols ($Arch): package not found: $Msix" }
            # Wavee.exe never ships inside the symbols zip; the exe the PDB belongs to is the one inside the package.
            $extracted = Join-Path $SymbolsDir 'Wavee.exe'
            Export-ZipEntry -Path $Msix -Entry 'Wavee.exe' -OutFile $extracted
            $exe = $extracted
        }
        else {
            if (-not (Test-Path -LiteralPath $ExePath)) { throw "symbols ($Arch): executable not found: $ExePath" }
            $exe = (Resolve-Path -LiteralPath $ExePath).Path
        }
        if (Test-Path -LiteralPath $symmap) { Remove-Item -LiteralPath $symmap -Force }
        Invoke-Native 'dotnet' @('run', '--project', $ReleaseToolProject, '-c', 'Release', '--',
            'symbol-map', '--pdb', $pdb, '--exe', $exe, '--out', $symmap) | Out-Null
    }
    finally {
        if ($extracted) { Remove-Item -LiteralPath $extracted -Force -ErrorAction SilentlyContinue }
    }
    if (-not (Test-Path -LiteralPath $symmap)) { throw "symbols ($Arch): symbol-map did not write $symmap" }

    $upload = Invoke-SymbolsUpload -SymmapPath $symmap -Quad $Quad -Arch $Arch -Wrangler $Wrangler -Bucket $Bucket `
        -Skip:$SkipUpload -SkipReason $SkipReason
    [pscustomobject]@{
        Symmap   = $symmap
        Key      = $upload.Key
        Uploaded = $upload.Uploaded
        Reason   = $upload.Reason
        Bytes    = (Get-Item -LiteralPath $symmap).Length
    }
}

function Publish-WaveeRelease {
    <#
    .SYNOPSIS
      Create the version release as a DRAFT, upload every asset, then flip it public. Uploading into a draft means a
      killed script never leaves a half-populated public release; -Resume picks the same draft back up.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$BodyFile,
        [Parameter(Mandatory = $true)][string[]]$Assets,
        [bool]$Prerelease = $false)

    foreach ($a in $Assets) { if (-not (Test-Path $a)) { throw "release asset not found: $a" } }
    if (-not (Test-Path $BodyFile)) { throw "release body not found: $BodyFile" }

    $existing = Get-GhRelease $Repo $Tag
    if (-not $existing) {
        Invoke-Gh @('release', 'create', $Tag, '--repo', $Repo, '--draft', '--verify-tag', '--title', $Title, '--notes-file', $BodyFile) | Out-Null
    }
    Invoke-Gh (@('release', 'upload', $Tag, '--repo', $Repo, '--clobber') + $Assets) | Out-Null

    $edit = @('release', 'edit', $Tag, '--repo', $Repo, '--draft=false', '--title', $Title, '--notes-file', $BodyFile)
    # NEVER claim the repo-global `releases/latest`: that endpoint is the GALLERY's update feed
    # (releases/latest/download/FluentGpu.<arch>.appinstaller). Wavee's feed is the rolling release, so a Wavee
    # release is ALWAYS published with --latest=false. There is deliberately no -Latest knob: it could only ever be
    # set to the one value that breaks the gallery's feed.
    if ($Prerelease) { $edit += @('--prerelease', '--latest=false') }
    else { $edit += '--latest=false' }
    Invoke-Gh $edit | Out-Null
}

function Update-WaveeFeed {
    <#
    .SYNOPSIS
      Point the rolling feed release at the release we just published, by replacing its assets in place. The feed's
      tag is an ANCHOR: created once, never deleted and never moved, because every installed client's .appinstaller
      Uri is baked to it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$FeedRelease,
        [Parameter(Mandatory = $true)][string]$FeedBodyFile,
        [Parameter(Mandatory = $true)][string[]]$Assets,
        [string]$Target = 'main')

    foreach ($a in $Assets) { if (-not (Test-Path $a)) { throw "feed asset not found: $a" } }
    if (-not (Test-Path $FeedBodyFile)) { throw "feed release body not found: $FeedBodyFile" }

    if (-not (Get-GhRelease $Repo $FeedRelease)) {
        Invoke-Gh @('release', 'create', $FeedRelease, '--repo', $Repo, '--target', $Target,
            '--title', "Wavee update feed ($FeedRelease)", '--notes-file', $FeedBodyFile, '--latest=false') | Out-Null
    }
    Invoke-Gh (@('release', 'upload', $FeedRelease, '--repo', $Repo, '--clobber') + $Assets) | Out-Null
}

# ---------------------------------------------------------------------------------------------------------------
# release-state.json ledger
# ---------------------------------------------------------------------------------------------------------------

function Get-ReleaseState {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (Test-Path $Path) { return (Get-Content $Path -Raw | ConvertFrom-Json) }
    $null
}

function Set-ReleaseState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$State)

    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Path, ($State | ConvertTo-Json -Depth 8), (New-Utf8NoBom))
}

# ---------------------------------------------------------------------------------------------------------------
# Release <-> issue linkage (commits, PRs, issues cross-check; see docs/plans/wavee/*-implementation.md Part A)
#
# The record/field separators are the ASCII RS (0x1E) / US (0x1F) control characters `git log
# --format=%H%x1f%h%x1f%s%x1f%b%x1e` emits. Windows PowerShell 5.1 has no `` `u{...} `` escape (that is a
# PowerShell 7+ addition), so every occurrence here is [char]0x1e / [char]0x1f instead.
# ---------------------------------------------------------------------------------------------------------------

function ConvertFrom-GitLogRecords {
    <#
    .SYNOPSIS
      Parse `git log --format=%H%x1f%h%x1f%s%x1f%b%x1e` text into one record per commit. Issues = commit-body/
      subject closing keywords (Fixes/Closes/Resolves #n, case-insensitive); Prs = a squash-merge subject suffix
      `(#n)` or a bare `!n` anywhere in subject/body.
    .OUTPUTS
      Array of pscustomobject @{ Sha; Short; Subject; Issues [int[]]; Prs [int[]] }, always an array (even 0 or 1
      element) - the caller pipes/indexes it.
    #>
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $rs = [char]0x1e
    $us = [char]0x1f
    $records = @()
    foreach ($rec in ($Text -split $rs)) {
        if (-not $rec.Trim()) { continue }
        $f = $rec.TrimStart("`r", "`n") -split $us, 4
        $subject = $f[2].Trim()
        $body = if ($f.Count -gt 3) { $f[3] } else { '' }
        $hay = "$subject`n$body"

        $issues = @([regex]::Matches($hay, '(?im)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)

        $prs = @()
        $m = [regex]::Match($subject, '\(#(\d+)\)\s*$')
        if ($m.Success) { $prs += [int]$m.Groups[1].Value }
        $prs += @([regex]::Matches($hay, '(?<![\w/])!(\d+)\b') | ForEach-Object { [int]$_.Groups[1].Value })

        $records += [pscustomobject]@{
            Sha     = $f[0].Trim()
            Short   = $f[1].Trim()
            Subject = $subject
            Issues  = @($issues)
            Prs     = @($prs | Sort-Object -Unique)
        }
    }
    , $records
}

function Get-ChangelogEntryRefs {
    <#
    .SYNOPSIS
      The `## [<semver>]` CHANGELOG entry only. Same trailing-group rule as ChangelogParser.cs: a bullet's TRAILING
      parenthesised group `(#n, !m)` at line end counts; a mid-sentence `(#n)` is ignored by design.
    .OUTPUTS
      pscustomobject @{ Issues [int[]]; Prs [int[]]; Bullets [int]; Unreferenced [int] }. Throws when the CHANGELOG
      has no matching `## [<Semver>]` heading.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Changelog,
        [Parameter(Mandatory = $true)][string]$Semver)

    $rx = "(?ms)^## \[$([regex]::Escape($Semver))\][^\r\n]*\r?\n(?<body>.*?)(?=^## \[|\z)"
    $m = [regex]::Match($Changelog, $rx)
    if (-not $m.Success) { throw "CHANGELOG.md has no '## [$Semver]' entry" }

    $issues = @()
    $prs = @()
    $bullets = 0
    $unref = 0
    # A bullet is its `- ` line PLUS every indented continuation line after it (the CHANGELOG wraps at ~118 columns,
    # so the trailing ref usually sits on the LAST line of a bullet, not the first). Join the fragments with a space
    # exactly like ChangelogParser.cs does, then apply the trailing-group rule to the joined text.
    $joined = New-Object System.Collections.Generic.List[string]
    $current = $null
    foreach ($line in ($m.Groups['body'].Value -split "\r?\n")) {
        if ($line -match '^- ') {
            if ($null -ne $current) { $joined.Add($current) }
            $current = $line.TrimEnd()
        }
        elseif ($null -ne $current -and $line -match '^\s+\S') { $current = $current + ' ' + $line.Trim() }
        else {
            if ($null -ne $current) { $joined.Add($current) }
            $current = $null
        }
    }
    if ($null -ne $current) { $joined.Add($current) }

    foreach ($line in $joined) {
        $bullets++
        $g = [regex]::Match($line, '\s\((?<refs>(?:[#!]\d+(?:,\s*)?)+)\)\s*$')
        if (-not $g.Success) { $unref++; continue }
        foreach ($r in ($g.Groups['refs'].Value -split ',\s*')) {
            if ($r[0] -eq '#') { $issues += [int]$r.Substring(1) }
            else { $prs += [int]$r.Substring(1) }
        }
    }
    [pscustomobject]@{
        Issues       = @($issues | Sort-Object -Unique)
        Prs          = @($prs | Sort-Object -Unique)
        Bullets      = $bullets
        Unreferenced = $unref
    }
}

function Compare-ReleaseIssueRefs {
    <#
    .SYNOPSIS
      Cross-check CHANGELOG issue refs against git-log commits over the release range. Parity with the C# side
      (ReleaseCommits.Link): a CHANGELOG `#n` is satisfied by a commit that FIXES n OR whose squash-merge suffix
      names PR n; only closing-keyword issues (not bare PR refs) can be "missing in the changelog".
    .OUTPUTS
      pscustomobject @{
        Linked              # [pscustomobject]{ Issue; Commits } for every ChangelogIssue git can satisfy
        MissingInChangelog  # [pscustomobject]{ Issue; Commits } - a commit closes an issue the entry never cites
        MissingInGit        # [int[]] - the entry cites an issue no commit in range fixes or names as a PR
        Unlinked            # commits whose Issues array is empty ("Other changes")
      }
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][int[]]$ChangelogIssues,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Commits)

    $byRef = @{}
    $byIssue = @{}
    foreach ($c in $Commits) {
        foreach ($i in $c.Issues) {
            if (-not $byIssue.ContainsKey($i)) { $byIssue[$i] = @() }
            $byIssue[$i] += $c
        }
        foreach ($n in (@($c.Issues) + @($c.Prs))) {
            if (-not $byRef.ContainsKey($n)) { $byRef[$n] = @() }
            $byRef[$n] += $c
        }
    }

    $missingInChangelog = @($byIssue.Keys | Where-Object { $ChangelogIssues -notcontains $_ } | Sort-Object |
        ForEach-Object { [pscustomobject]@{ Issue = $_; Commits = @($byIssue[$_]) } })
    $missingInGit = @($ChangelogIssues | Where-Object { -not $byRef.ContainsKey($_) } | Sort-Object)
    $linked = @($ChangelogIssues | Where-Object { $byRef.ContainsKey($_) } | Sort-Object |
        ForEach-Object { [pscustomobject]@{ Issue = $_; Commits = @($byRef[$_]) } })

    [pscustomobject]@{
        Linked             = $linked
        MissingInChangelog = $missingInChangelog
        MissingInGit       = $missingInGit
        Unlinked           = @($Commits | Where-Object { $_.Issues.Count -eq 0 })
    }
}

function Format-IssueRefMismatch {
    <#
    .SYNOPSIS
      One line per problem in a Compare-ReleaseIssueRefs result - the gate's throw text and the tool's error
      parity. Empty array when there is nothing to report.
    #>
    param(
        [Parameter(Mandatory = $true)]$Comparison,
        [string]$Semver,
        [string]$Range)

    $lines = @()
    foreach ($m in $Comparison.MissingInChangelog) {
        $c = ($m.Commits | ForEach-Object { "$($_.Short) `"$($_.Subject)`"" }) -join ', '
        $lines += "issue #$($m.Issue) is fixed by $c but the CHANGELOG [$Semver] entry does not cite it"
    }
    foreach ($i in $Comparison.MissingInGit) {
        $lines += "CHANGELOG [$Semver] cites #$i but no commit in $Range carries 'Fixes #$i'"
    }
    # Unary comma: a 1-element $lines would otherwise unroll to a bare string on return, and the caller's
    # $lines[0] would then index a CHARACTER of that string instead of the one mismatch line.
    , $lines
}

function Write-ReleaseCommitsJson {
    <#  The `commits.json` file Wavee.ReleaseTool reads via --commits. camelCase keys, arrays even when empty. #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Commits,
        [Parameter(Mandatory = $true)][string]$Path)

    $rows = @($Commits | ForEach-Object {
        [ordered]@{
            sha     = $_.Sha
            short   = $_.Short
            subject = $_.Subject
            issues  = @($_.Issues)
            prs     = @($_.Prs)
        }
    })
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $rows -Depth 4), (New-Utf8NoBom))
}

function New-ShippedIssueComment {
    <#  The markdown body posted on an issue a released commit closed. #>
    param(
        [string]$Repo,
        [string]$Tag,
        [string]$Semver,
        [string]$Codename,
        [string]$Quad,
        [object[]]$Commits,
        [int[]]$Prs)

    $c = ($Commits | ForEach-Object { "[$($_.Short)](https://github.com/$Repo/commit/$($_.Sha))" }) -join ', '
    $p = if ($Prs.Count) { ' - PR ' + (($Prs | ForEach-Object { "#$_" }) -join ', ') } else { '' }
    @(
        "Shipped in **Wavee $Semver $([char]0x2014) $Codename** (build $Quad): https://github.com/$Repo/releases/tag/$Tag",
        "Commits: $c$p",
        'Sideloaded installs update automatically from the `wavee-stable` feed; Microsoft Store installs follow once the Store submission is certified.'
    ) -join "`n`n"
}

function Test-IssueAlreadyNotified {
    <#  Idempotency for -Resume: true when any existing comment already names this tag's release URL. #>
    param(
        [AllowEmptyCollection()][object[]]$Comments,
        [string]$Repo,
        [string]$Tag)

    $needle = "https://github.com/$Repo/releases/tag/$Tag"
    [bool]($Comments | Where-Object { "$($_.body)" -like "*$needle*" })
}

function Add-ShippedIssueComments {
    <#
    .SYNOPSIS
      gh-facing (untested, like Publish-WaveeRelease): comment on every issue in $Linked (a
      Compare-ReleaseIssueRefs .Linked array) with the body $BodyFor produces, skipping any issue already notified
      for this tag. Never closes or reopens an issue - GitHub's own closing-keyword handling already did that.
    .OUTPUTS
      pscustomobject @{ Posted [int[]]; Skipped [int[]] }
    #>
    param(
        [string]$Repo,
        [string]$Tag,
        [object[]]$Linked,
        [scriptblock]$BodyFor)

    $posted = @()
    $skipped = @()
    foreach ($l in $Linked) {
        $existing = ConvertFrom-GhJson (Invoke-Gh @('api', "repos/$Repo/issues/$($l.Issue)/comments", '--paginate'))
        if (Test-IssueAlreadyNotified -Comments $existing -Repo $Repo -Tag $Tag) { $skipped += $l.Issue; continue }
        Invoke-Gh @('issue', 'comment', "$($l.Issue)", '--repo', $Repo, '--body', (& $BodyFor $l)) | Out-Null
        $posted += $l.Issue
    }
    [pscustomobject]@{ Posted = @($posted); Skipped = @($skipped) }
}

Export-ModuleMember -Function @(
    'Set-WaveeTls12',
    'Test-WaveeSemver',
    'ConvertTo-WaveeQuad',
    'Get-WaveeFeedDocument',
    'Get-WaveeFeedVersion',
    'Test-FeedMonotonic',
    'Test-WaveeFeedLive',
    'New-WaveeAppInstaller',
    'Write-ReleaseManifest',
    'Test-ReleaseManifest',
    'Invoke-Gh',
    'Get-GhAuthToken',
    'ConvertFrom-GhJson',
    'Get-GhRelease',
    'Get-GhReleaseAssetUrl',
    'Test-AssetContentLength',
    'Get-CrashIngestGate',
    'Resolve-CrashIngestKey',
    'Get-WranglerPath',
    'Test-WranglerLogin',
    'Get-ZipEntryBytes',
    'Find-ByteText',
    'Assert-CrashIngestStamp',
    'Invoke-SymbolsUpload',
    'Publish-WaveeSymbolMap',
    'Publish-WaveeRelease',
    'Update-WaveeFeed',
    'Get-ReleaseState',
    'Set-ReleaseState',
    'ConvertFrom-GitLogRecords',
    'Get-ChangelogEntryRefs',
    'Compare-ReleaseIssueRefs',
    'Format-IssueRefMismatch',
    'Write-ReleaseCommitsJson',
    'New-ShippedIssueComment',
    'Test-IssueAlreadyNotified',
    'Add-ShippedIssueComments')
