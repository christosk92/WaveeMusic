#requires -Version 5.1
<#
    Wavee.Release.psm1 - the pure helpers behind ops\release\wavee-release.ps1.

    Everything here is either a pure function (semver / quad / manifest / .appinstaller substitution) or a thin,
    testable wrapper over one external tool (gh) or one HTTP GET (the rolling feed). The orchestrator owns the
    phase sequencing and the release-state ledger; this module owns the decisions.

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
    try {
        # -DisableKeepAlive: Windows PowerShell 5.1 reuses the ServicePoint connection across calls, and an error
        # response it did not dispose (the 404 below) leaves that connection half-closed - the NEXT feed GET (the
        # other arch) then dies with "The connection was closed unexpectedly". One connection per call, closed.
        $r = Invoke-WebRequest -UseBasicParsing -Uri $url -MaximumRedirection 5 -DisableKeepAlive
    }
    catch {
        $resp = $null
        if ($_.Exception.PSObject.Properties['Response']) { $resp = $_.Exception.Response }
        if ($resp) {
            $status = [int]$resp.StatusCode
            try { $resp.Close() } catch { }
            if ($status -eq 404) { return $null }
        }
        throw
    }
    $txt = "$($r.Content)".TrimStart([char]0xFEFF)
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
    'Publish-WaveeRelease',
    'Update-WaveeFeed',
    'Get-ReleaseState',
    'Set-ReleaseState')
