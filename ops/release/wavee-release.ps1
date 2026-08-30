#requires -Version 5.1
<#
.SYNOPSIS
  Cut, sign, publish and verify one Wavee release from this machine.

.DESCRIPTION
  Wavee releases are made by hand on the developer's arm64 box - there is no CI release job, because a CI checkout
  has no Wavee.PlayPlay junction and would silently ship the public-only variant.

  The run is a ledger of phases. Every phase records itself in <staging>\release-state.json, so -Resume restarts
  exactly where a failed run stopped, and -Abort unwinds an un-pushed run completely.

      0  preflight     hard/soft gate table (versions, git, tools, signing, gh, feed monotonicity)
      1a bump          WaveeBuild + 1, CHANGELOG "unreleased" -> today (UTC)
      2  notes         Wavee.ReleaseTool validate -> <staging>\notes (whatsnew.json, index, RELEASE_BODY.md, media)
      1b tag           commit the two hand-edited files, annotated tag (local only)
      3  packArm64     pack-wavee-msix.ps1 -Arch arm64 -NoSign
      4  packX64       pack-wavee-msix.ps1 -Arch x64 -NoSign   (or -X64Msix <path> to adopt a prebuilt package)
      5  sign          ONE Azure Trusted Signing signtool call over every .msix, then verify each
      6  appinstaller  one .appinstaller per architecture, pointing at this release's msix and at the rolling feed
      7  stage         flatten assets into <staging>, write MANIFEST.txt (sha256sum format)
      8  push          push the branch then the tag (origin/<branch> must equal HEAD~1)
      9  release       gh release: draft -> upload -> publish
      10 feed          repoint the rolling feed release(s) - ALWAYS LAST, it is what clients poll
      11 verify        assets + sizes vs staged, feed root Version live, msix Content-Length, optional install

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 -DryRun -SkipTests
  Builds and signs everything into artifacts\release\<semver>-dryrun\ and stops. Nothing is committed or uploaded.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1
  The real thing: bump, tag, build, sign, push, publish, repoint the feed, verify.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 -Resume
  Finish a run that died. Staging is hash-verified first; nothing is rebuilt and the build counter is not bumped again.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 `
      -FeedRelease wavee-stable-test -TagPrefix wavee-test-v -Branch release-test -Force -SkipTests -InstallFromFeed
  The end-to-end rehearsal against a throwaway namespace; the real feed is untouched.

.LINK
  docs\guide\releasing-wavee.md
#>
[CmdletBinding()]
param(
    # There is deliberately no -Channel: the channel is DERIVED from the semver in Wavee.Version.props
    # (M.m.p = stable, M.m.p-beta.N = beta), so a switch could only ever agree with it or lie about it.
    [string[]]$Arch = @('arm64', 'x64'),
    [ValidateSet('arm64', 'x64')][string]$SkipArch,
    [string]$X64Msix,
    [switch]$PublicOnly,
    [switch]$DryRun,
    [switch]$NoUpload,
    [switch]$Resume,
    [switch]$Abort,
    [string]$RepointFeed,
    [switch]$AllowDowngrade,
    [switch]$SkipTests,
    [switch]$NoSign,
    [switch]$NoNotes,
    [switch]$InstallFromFeed,
    [switch]$Force,
    [string]$Repo = 'christosk92/WaveeMusic',
    [string]$FeedRelease = 'wavee-stable',
    [string]$TagPrefix = 'wavee-v',
    [string]$Branch = 'main',
    [string]$IdentityName = 'cproducts.Wavee',
    # The basename of the .appinstaller assets: "<AssetPrefix>.<arch>.appinstaller". It is part of the URL every
    # installed client has baked into its own .appinstaller, so it may only change together with a new package
    # identity - which is exactly what a beta build would be: a beta channel ships as 'Wavee.Beta'.
    [string]$AssetPrefix = 'Wavee',
    [string]$Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL',
    [string]$Subscription = 'Azure subscription 1',
    [string]$Metadata,
    [string]$Configuration = 'Release',
    [string]$OutputDir = 'artifacts/release')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $root 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Wavee.Release.psm1') -Force -DisableNameChecking

if (-not $Metadata) { $Metadata = Join-Path $root 'ops\build\signing\metadata.json' }

$EmDash = [char]0x2014

# ===============================================================================================================
# Console + process helpers
# ===============================================================================================================

function Step { param([string]$Message) Write-Host "`n== $Message" -ForegroundColor Cyan }
function Note { param([string]$Message) Write-Host "   $Message" -ForegroundColor DarkGray }
function Warn { param([string]$Message) Write-Host "   warn: $Message" -ForegroundColor Yellow }
function Good { param([string]$Message) Write-Host "   $Message" -ForegroundColor Green }

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [switch]$AllowFailure)
    $r = Invoke-Native 'git' (@('-C', $root) + $Arguments) -AllowFailure
    $lines = @()
    foreach ($o in $r.Output) {
        $s = "$o".Trim()
        if ($s.Length -gt 0) { $lines += $s }
    }
    if ($r.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed (exit $($r.ExitCode)):`n$($lines -join "`n")"
    }
    [pscustomobject]@{ ExitCode = $r.ExitCode; Lines = $lines; Text = ($lines -join "`n") }
}

$script:Tools = $null
function Get-Tools {
    if (-not $script:Tools) {
        $script:Tools = Get-WindowsSdkTools
        Add-VsInstallerToPath
    }
    $script:Tools
}

# ===============================================================================================================
# release-state.json ledger
# ===============================================================================================================

$script:State = $null
$script:StatePath = $null

function ConvertTo-StateHashtable {
    param($Object)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Management.Automation.PSCustomObject]) {
        $h = @{}
        foreach ($p in $Object.PSObject.Properties) { $h[$p.Name] = ConvertTo-StateHashtable $p.Value }
        return $h
    }
    if ($Object -is [object[]]) {
        $a = @()
        foreach ($i in $Object) { $a += , (ConvertTo-StateHashtable $i) }
        return $a
    }
    $Object
}

function Save-State {
    if ($script:StatePath) { Set-ReleaseState $script:StatePath $script:State }
}

function Test-PhaseDone {
    param([Parameter(Mandatory = $true)][string]$Name)
    if (-not $script:State) { return $false }
    if (-not $script:State.phases) { return $false }
    ($script:State.phases[$Name] -eq 'done')
}

function Complete-Phase {
    param([Parameter(Mandatory = $true)][string]$Name)
    $script:State.phases[$Name] = 'done'
    Save-State
}

function Reset-Phase {
    param([Parameter(Mandatory = $true)][string]$Name)
    if ($script:State -and $script:State.phases -and $script:State.phases.ContainsKey($Name)) {
        $script:State.phases.Remove($Name)
        Save-State
    }
}

# ===============================================================================================================
# Preflight gate table
# ===============================================================================================================

$script:Checks = @()

function Add-Check {
    <#  A check returns a detail string, returns "SKIP: <why>" to be recorded as skipped, or throws to fail. #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('hard', 'soft')][string]$Severity,
        [Parameter(Mandatory = $true)][scriptblock]$Test)

    $status = 'ok'
    $detail = ''
    try {
        $out = & $Test
        if ($null -ne $out) { $detail = (@($out) -join '; ') }
        if ($detail.StartsWith('SKIP:')) {
            $status = 'skip'
            $detail = $detail.Substring(5).Trim()
        }
    }
    catch {
        $status = 'FAIL'
        $detail = ($_.Exception.Message -replace "`r?`n", ' / ')
    }
    $script:Checks += [pscustomobject]@{ Check = $Name; Severity = $Severity; Status = $status; Detail = $detail }
}

function Assert-Checks {
    $script:Checks | Format-Table -Property Check, Severity, Status, Detail -AutoSize | Out-String -Width 200 | Write-Host
    foreach ($c in $script:Checks) {
        if ($c.Status -eq 'FAIL' -and $c.Severity -eq 'soft') { Warn "$($c.Check): $($c.Detail)" }
    }
    $hard = @($script:Checks | Where-Object { $_.Status -eq 'FAIL' -and $_.Severity -eq 'hard' })
    if ($hard.Count -gt 0) {
        $msg = ($hard | ForEach-Object { "  $($_.Check): $($_.Detail)" }) -join "`n"
        throw "preflight failed:`n$msg"
    }
}

# ===============================================================================================================
# Mutual exclusion
# ===============================================================================================================

if ($DryRun -and ($NoUpload -or $Resume -or $Abort)) { throw '-DryRun excludes -NoUpload / -Resume / -Abort' }
if ($Abort -and ($Resume -or $RepointFeed)) { throw '-Abort excludes -Resume / -RepointFeed' }
if ($RepointFeed -and ($Resume -or $NoUpload)) { throw '-RepointFeed excludes -Resume / -NoUpload' }
if ($NoSign -and -not ($DryRun -or $NoUpload)) { throw '-NoSign requires -DryRun or -NoUpload (an unsigned package must never reach the feed)' }
if ($X64Msix -and $SkipArch -eq 'x64') { throw '-X64Msix excludes -SkipArch x64' }
if ($InstallFromFeed -and ($DryRun -or $NoUpload)) { throw '-InstallFromFeed needs a published feed, so it excludes -DryRun / -NoUpload' }

$arches = @($Arch | Where-Object { $_ -ne $SkipArch })
if ($arches.Count -eq 0) { throw 'no architectures left after -SkipArch' }

# ===============================================================================================================
# Paths and identity of this run
# ===============================================================================================================

$propsPath = Join-Path $root 'src\apps\Wavee\Wavee.Version.props'
$changelogPathRepo = Join-Path $root 'CHANGELOG.md'
$packScript = Join-Path $root 'ops\build\pack-wavee-msix.ps1'
$appInstallerTemplate = Join-Path $root 'ops\build\Wavee.AppInstaller.template.xml'
$feedBodyFile = Join-Path $PSScriptRoot 'feed-release-body.md'
$releaseToolProject = Join-Path $root 'src\apps\Wavee.ReleaseTool'
$playPlayProbe = Join-Path $root 'src\apps\Wavee.PlayPlay\Client\InProcessPlayPlayKeyDeriver.cs'

$props = Get-WaveeVersionProps $propsPath
$semver = $props.Version
$codename = $props.Codename
$sv = Test-WaveeSemver $semver
$Channel = $sv.Channel

$tag = "$TagPrefix$semver"
$stageRoot = $OutputDir
if (-not [IO.Path]::IsPathRooted($stageRoot)) { $stageRoot = Join-Path $root ($OutputDir -replace '/', '\') }
$stageSuffix = ''
if ($DryRun) { $stageSuffix = '-dryrun' }
$stage = Join-Path $stageRoot ($semver + $stageSuffix)
$script:StatePath = Join-Path $stage 'release-state.json'

function Get-MsixName { param([string]$Quad, [string]$A) "Wavee_${Quad}_$A.msix" }
function Get-AppInstallerName { param([string]$A) "$AssetPrefix.$A.appinstaller" }
function Get-FeedUri { param([string]$A) "https://github.com/$Repo/releases/download/$FeedRelease/$(Get-AppInstallerName $A)" }
function Get-MsixUri { param([string]$Quad, [string]$A) "https://github.com/$Repo/releases/download/$tag/$(Get-MsixName $Quad $A)" }

# ===============================================================================================================
# -Abort
# ===============================================================================================================

function Invoke-Abort {
    Step "Abort $tag"
    $st = Get-ReleaseState $script:StatePath
    if (-not $st) { throw "no release-state.json under $stage - nothing to abort" }
    # branchPushed is set between the two pushes, so a run that published the branch and then failed on the tag is
    # still refused: the commit is already on origin and resetting it here would rewrite published history.
    if ($st.pushed -or $st.branchPushed) {
        throw 'this run already pushed to origin; -Abort will not rewrite published history. Use -RepointFeed <previous semver> -AllowDowngrade to move the feed back, then cut a forward patch.'
    }

    $tagged = ($st.phases -and $st.phases.tag -eq 'done')
    if ($tagged) {
        $expected = "release: Wavee $($st.semver) $($st.codename) (build $($st.quad))"
        $headSha = (Invoke-Git @('rev-parse', 'HEAD')).Text
        $headMsg = (Invoke-Git @('log', '-1', '--pretty=%s')).Text
        if ($headMsg -ne $expected) { throw "HEAD message is '$headMsg', expected '$expected' - refusing to reset a commit this run did not make" }
        $tagRef = Invoke-Git @('rev-list', '-n', '1', $st.tag) -AllowFailure
        if ($tagRef.ExitCode -ne 0) { throw "tag $($st.tag) not found locally; resolve by hand" }
        if ($tagRef.Text -ne $headSha) { throw "tag $($st.tag) points at $($tagRef.Text) but HEAD is $headSha - refusing to reset" }
        Note "deleting tag $($st.tag)"
        Invoke-Git @('tag', '-d', $st.tag) | Out-Null
        Note 'git reset --hard HEAD~1'
        Invoke-Git @('reset', '--hard', 'HEAD~1') | Out-Null
    }
    else {
        Note 'no release commit was made; restoring the two hand-edited files'
        Invoke-Git @('checkout', '--', 'CHANGELOG.md', 'src/apps/Wavee/Wavee.Version.props') -AllowFailure | Out-Null
    }

    if (Test-Path $stage) {
        Note "removing $stage"
        Remove-Item $stage -Recurse -Force
    }
    Good 'aborted; the tree is back to where it started'
}

# ===============================================================================================================
# -RepointFeed
# ===============================================================================================================

function Invoke-Repoint {
    $targetTag = "$TagPrefix$RepointFeed"
    Step "Repoint $FeedRelease at $targetTag"
    Test-WaveeSemver $RepointFeed | Out-Null

    $rel = Get-GhRelease $Repo $targetTag
    if (-not $rel) { throw "release $targetTag not found in $Repo" }
    # A draft's assets are not publicly downloadable, so a feed pointed at one hands every client a 404 on update.
    if ($rel.isDraft) { throw "release $targetTag is still a draft; its assets are not public, so the feed cannot point at it" }

    $quad = ''
    $found = @()
    foreach ($a in $rel.assets) {
        $m = [regex]::Match("$($a.name)", '^Wavee_(?<q>\d+\.\d+\.\d+\.\d+)_(?<a>arm64|x64)\.msix$')
        if ($m.Success) {
            if ($quad -and $quad -ne $m.Groups['q'].Value) { throw "release $targetTag carries more than one quad ($quad and $($m.Groups['q'].Value))" }
            $quad = $m.Groups['q'].Value
            $found += $m.Groups['a'].Value
        }
    }
    if (-not $quad) { throw "release $targetTag has no Wavee_<quad>_<arch>.msix asset to point at" }
    $found = @($found | Sort-Object -Unique)
    Note "quad $quad, architectures $($found -join ', ')"

    if ($AllowDowngrade) {
        Warn "monotonic gate inverted by -AllowDowngrade: clients on a newer build WILL be moved back to $quad"
    }
    else {
        Test-FeedMonotonic $Repo @($FeedRelease) $quad $RepointFeed $found $AssetPrefix |
            Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    }

    $work = Join-Path $stageRoot ("repoint-$RepointFeed")
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    $assets = @()
    foreach ($a in $found) {
        $out = Join-Path $work (Get-AppInstallerName $a)
        New-WaveeAppInstaller -Template $appInstallerTemplate -OutFile $out -Arch $a -Quad $quad `
            -Publisher $Publisher -IdentityName $IdentityName `
            -FeedUri (Get-FeedUri $a) -MsixUri "https://github.com/$Repo/releases/download/$targetTag/$(Get-MsixName $quad $a)" | Out-Null
        $assets += $out
        Note "wrote $(Split-Path -Leaf $out)"
    }

    Update-WaveeFeed -Repo $Repo -FeedRelease $FeedRelease -FeedBodyFile $feedBodyFile -Assets $assets -Target $Branch

    foreach ($a in $found) {
        $wantMsix = "https://github.com/$Repo/releases/download/$targetTag/$(Get-MsixName $quad $a)"
        $live = Test-WaveeFeedLive -Repo $Repo -FeedRelease $FeedRelease -Arch $a -AssetPrefix $AssetPrefix `
            -ExpectedQuad $quad -ExpectedMsixUri $wantMsix
        if (-not $live) { throw "feed $FeedRelease/$a did not come up at $quad pointing at $wantMsix" }
        Good "$FeedRelease/$a is live at $quad"
    }
    Note 'whatsnew-index.json is deliberately left alone: the index is cumulative history, not a pointer.'
}

if ($Abort) { Invoke-Abort; return }
if ($RepointFeed) { Invoke-Repoint; return }

# ===============================================================================================================
# State: load (-Resume) or initialise
# ===============================================================================================================

if ($Resume) {
    $loaded = Get-ReleaseState $script:StatePath
    if (-not $loaded) { throw "nothing to resume in $stage" }
    $script:State = ConvertTo-StateHashtable $loaded
    if (-not $script:State.phases) { $script:State.phases = @{} }
    $semver = "$($script:State.semver)"
    $codename = "$($script:State.codename)"
    $quad = "$($script:State.quad)"
    $build = [int]$script:State.build
    $commit = "$($script:State.commit)"
    $buildDate = "$($script:State.buildDate)"
    $tag = "$($script:State.tag)"
    $Channel = "$($script:State.channel)"
    $arches = @($script:State.arches)
    $feeds = @($script:State.feeds)
    Step "Resume $tag (build $quad)"
    Note "staging $stage"
    if (Test-PhaseDone 'stage') {
        if (-not (Test-ReleaseManifest $stage (Join-Path $stage 'MANIFEST.txt'))) {
            throw "staged assets in $stage do not match MANIFEST.txt; delete the folder and cut the release again (-Abort first if the tag is local)"
        }
        Good 'staging hash-verified against MANIFEST.txt'
    }
}
else {
    $build = $props.Build + 1
    $quad = ConvertTo-WaveeQuad $semver $build
    $commit = ''
    $buildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $feeds = @($FeedRelease)
    $script:State = @{
        schema       = 1
        semver       = $semver
        codename     = $codename
        channel      = $Channel
        build        = $build
        quad         = $quad
        tag          = $tag
        repo         = $Repo
        branch       = $Branch
        feedRelease  = $FeedRelease
        feeds        = $feeds
        arches       = $arches
        commit       = $commit
        buildDate    = $buildDate
        dryRun       = [bool]$DryRun
        branchPushed = $false
        pushed       = $false
        phases       = @{}
    }
}

$notesSrc = Join-Path $root "ops\release\wavee\$semver"
$notesOut = Join-Path $stage 'notes'
$changelogForTool = $changelogPathRepo
if ($DryRun) { $changelogForTool = Join-Path $stage 'CHANGELOG.md' }

# ===============================================================================================================
# 0  preflight
# ===============================================================================================================

if (-not (Test-PhaseDone 'preflight')) {
    Step "Preflight $tag (quad $quad, channel $Channel, branch $Branch)"

    Add-Check 'version props' 'hard' { "$semver / $codename / build $($props.Build) -> $build" }
    Add-Check 'semver + quad' 'hard' {
        if ($Channel -eq 'beta') { throw 'the beta channel needs its own package identity and is phase 2; tag a stable semver' }
        "quad $quad"
    }
    Add-Check 'staging folder' 'hard' {
        # A previous rehearsal's folder is disposable (it carries no commit/tag/upload state), so -DryRun replaces it.
        if ((Test-Path $stage) -and -not $Force -and -not $DryRun) { throw "$stage already exists (use -Force to replace it, or -Resume to continue it)" }
        $stage
    }
    Add-Check 'working tree clean' 'hard' {
        if ($DryRun) { return 'SKIP: -DryRun does not touch the tree' }
        $dirty = (Invoke-Git @('status', '--porcelain')).Lines
        if ($dirty.Count -gt 0) { throw "$($dirty.Count) modified path(s), first: $($dirty[0])" }
        'clean'
    }
    Add-Check "branch is $Branch" 'hard' {
        $cur = (Invoke-Git @('branch', '--show-current')).Text
        if ($cur -ne $Branch) {
            if ($Force) { return "SKIP: on '$cur', allowed by -Force" }
            throw "on '$cur', expected '$Branch' (use -Force)"
        }
        $cur
    }
    Add-Check "HEAD == origin/$Branch" 'hard' {
        Invoke-Git @('fetch', 'origin', '--tags') -AllowFailure | Out-Null
        $remote = Invoke-Git @('rev-parse', "origin/$Branch") -AllowFailure
        if ($remote.ExitCode -ne 0) {
            if ($Force) { return "SKIP: origin/$Branch does not exist yet, allowed by -Force" }
            throw "origin/$Branch does not exist (use -Force to publish a new branch)"
        }
        $head = (Invoke-Git @('rev-parse', 'HEAD')).Text
        if ($head -ne $remote.Text) {
            if ($Force) { return "SKIP: HEAD $($head.Substring(0,7)) != origin/$Branch $($remote.Text.Substring(0,7)), allowed by -Force" }
            throw "HEAD $($head.Substring(0,7)) != origin/$Branch $($remote.Text.Substring(0,7)) (use -Force)"
        }
        $head.Substring(0, 7)
    }
    Add-Check 'tag is free' 'hard' {
        if ($DryRun) { return 'SKIP: -DryRun never tags' }
        if ((Invoke-Git @('tag', '-l', $tag)).Text) { throw "tag $tag already exists locally" }
        if ((Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")).Text) { throw "tag $tag already exists on origin" }
        if (-not ($DryRun -or $NoUpload)) {
            if (Get-GhRelease $Repo $tag) { throw "release $tag already exists in $Repo" }
        }
        $tag
    }
    Add-Check 'CHANGELOG entry' 'hard' {
        $cl = Get-Content $changelogPathRepo -Raw
        $rx = "(?m)^## \[$([regex]::Escape($semver))\] - (\d{4}-\d{2}-\d{2}|unreleased)\s*$"
        $m = [regex]::Match($cl, $rx)
        if (-not $m.Success) { throw "CHANGELOG.md has no '## [$semver] - <date|unreleased>' heading" }
        $m.Groups[1].Value
    }
    Add-Check 'release notes' 'hard' {
        if ($NoNotes) {
            if (-not $Force) { throw '-NoNotes requires -Force (a release without notes is a degraded release)' }
            return 'SKIP: -NoNotes -Force'
        }
        if (-not (Test-Path (Join-Path $notesSrc 'whatsnew.json'))) { throw "missing $notesSrc\whatsnew.json" }
        $notesSrc
    }
    Add-Check 'PlayPlay junction' 'hard' {
        if ($PublicOnly) { return 'SKIP: -PublicOnly' }
        if (-not (Test-Path $playPlayProbe)) { throw 'src\apps\Wavee.PlayPlay junction missing; a release build is PlayPlay-inclusive (or pass -PublicOnly)' }
        'present'
    }
    Add-Check 'Windows SDK tools' 'hard' { "$((Get-Tools).Version)" }
    Add-Check 'x64 cross toolchain' 'hard' {
        if ($arches -notcontains 'x64') { return 'SKIP: x64 not requested' }
        if ($X64Msix) {
            if (-not (Test-Path $X64Msix)) { throw "-X64Msix path not found: $X64Msix" }
            return "SKIP: adopting $X64Msix"
        }
        $x = Test-X64CrossToolchain
        if (-not $x.Ok) { throw "$($x.Reason) (use -SkipArch x64, or -X64Msix <prebuilt.msix>)" }
        "$($x.LinkExe)"
    }
    Add-Check 'Trusted Signing' 'hard' {
        if ($NoSign) { return 'SKIP: -NoSign' }
        if (-not (Test-Path $Metadata)) { throw "signing metadata not found: $Metadata (copy ops\build\signing\metadata.template.json)" }
        Invoke-Native 'az' @('account', 'set', '--subscription', $Subscription) | Out-Null
        $t = Invoke-Native 'az' @('account', 'get-access-token', '--scope', 'https://codesigning.azure.net/.default', '--query', 'expiresOn', '-o', 'tsv')
        "token to $(($t.Output -join ' ').Trim())"
    }
    Add-Check 'gh auth' 'hard' {
        if ($DryRun -or $NoUpload) { return 'SKIP: nothing will be uploaded' }
        Invoke-Gh @('auth', 'status') | Out-Null
        Invoke-Gh @('repo', 'view', $Repo, '--json', 'nameWithOwner') | Out-Null
        $Repo
    }
    Add-Check 'beta feed present' 'soft' {
        if ($DryRun -or $NoUpload) { return 'SKIP: offline run' }
        if ($Channel -ne 'stable') { return 'SKIP: not a stable release' }
        if (Get-GhRelease $Repo 'wavee-beta') {
            $script:BetaFeedPresent = $true
            return 'wavee-beta will be repointed too'
        }
        'no wavee-beta feed'
    }
    Add-Check 'feed monotonic' 'hard' {
        $f = @($FeedRelease)
        if ($script:BetaFeedPresent) { $f += 'wavee-beta' }
        $script:FeedList = $f
        try {
            $rows = Test-FeedMonotonic $Repo $f $quad $semver $arches $AssetPrefix
        }
        catch {
            # A gate failure (the feed IS ahead) must stay hard everywhere - it is the whole point of the check, and
            # it is decided from data we already read. Only an unreachable feed is downgraded, and only when this run
            # will not upload anything: a rehearsal on a plane is a legitimate thing to do, a real release is not.
            if ("$($_.Exception.Message)" -like 'feed monotonic gate failed*') { throw }
            if (-not ($DryRun -or $NoUpload)) { throw }
            $why = ($_.Exception.Message -replace "`r?`n", ' / ')
            return "SKIP: feed unreachable and nothing will be uploaded ($why)"
        }
        ($rows | ForEach-Object { "$($_.Feed)/$($_.Arch): $($_.Current) -> $($_.New)" }) -join '; '
    }
    Add-Check 'gates' 'hard' {
        if ($SkipTests) { return 'SKIP: -SkipTests' }
        # The app repo's gates: Wavee.slnx in BOTH configurations (the engine's diag-gate arms differ per
        # configuration, and TreatWarningsAsErrors makes a Release-only warning a Release-only break), the app
        # tests, and the release tooling's own Pester suite. The engine's VerticalSlice is the engine repo's gate.
        Invoke-Native 'dotnet' @('build', (Join-Path $root 'Wavee.slnx'), '-c', 'Debug', '--nologo', '-v', 'q') | Out-Null
        Invoke-Native 'dotnet' @('build', (Join-Path $root 'Wavee.slnx'), '-c', 'Release', '--nologo', '-v', 'q') | Out-Null
        Invoke-Native 'dotnet' @('test', (Join-Path $root 'src\apps\Wavee.Tests\Wavee.Tests.csproj'), '--no-build', '--nologo', '-v', 'q') | Out-Null
        $pester = Invoke-Pester -Path (Join-Path $root 'ops\release\tests') -PassThru -Quiet
        if ($pester.FailedCount -gt 0) { throw ('Pester: ' + $pester.FailedCount + ' failed') }
        'build Debug+Release, Wavee.Tests, Pester ' + $pester.PassedCount + '/0'
    }

    Assert-Checks

    if ($script:FeedList) { $feeds = @($script:FeedList) }
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    if (-not $commit) { $commit = (Invoke-Git @('rev-parse', '--short=7', 'HEAD')).Text }
    $script:State.commit = $commit
    $script:State.feeds = $feeds
    $script:State.arches = $arches
    Complete-Phase 'preflight'
}
else {
    Note "preflight already done for $tag"
}

Get-Tools | Out-Null

# ===============================================================================================================
# 1a  bump + CHANGELOG date
# ===============================================================================================================

function Invoke-Bump {
    Step "Bump to build $build and date the CHANGELOG"
    $today = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    # (?=\r?$): CHANGELOG.md is CRLF on disk and .NET's multiline `$` only sees `\n`, so the CR must be tolerated
    # (a lookahead - the CR itself stays in place).
    $rxHead = "(?m)^(## \[$([regex]::Escape($semver))\] - )unreleased[ \t]*(?=\r?$)"

    if ($DryRun) {
        # A dry run must leave `git status` clean, so it dates a COPY that only the release tool sees.
        $cl = [IO.File]::ReadAllText($changelogPathRepo)
        $dated = [regex]::Replace($cl, $rxHead, ('${1}' + $today))
        [IO.File]::WriteAllText($changelogForTool, $dated, (New-Object System.Text.UTF8Encoding $false))
        Note "dated a copy at $changelogForTool (the working tree is untouched)"
        Complete-Phase 'bump'
        return
    }

    Set-WaveeBuild $propsPath $build
    Note "WaveeBuild -> $build"

    $cl = [IO.File]::ReadAllText($changelogPathRepo)
    $dated = [regex]::Replace($cl, $rxHead, ('${1}' + $today))
    $changelogChanged = ($dated -ne $cl)
    if ($changelogChanged) {
        [IO.File]::WriteAllText($changelogPathRepo, $dated, (New-Object System.Text.UTF8Encoding $false))
        Note "CHANGELOG [$semver] dated $today"
    }
    else {
        Note "CHANGELOG [$semver] was already dated"
    }

    $expected = @('src/apps/Wavee/Wavee.Version.props')
    if ($changelogChanged) { $expected += 'CHANGELOG.md' }
    # .Lines carries stderr too, and git's CRLF advice ("warning: in the working copy of '...'") is stderr - it
    # once made this check report the warning text as a touched file. Only path lines count.
    $actual = @((Invoke-Git @('diff', '--name-only')).Lines | Where-Object { $_ -and $_ -notmatch '^(warning|hint|fatal):' } | Sort-Object)
    $want = @($expected | Sort-Object)
    if (($actual -join '|') -ne ($want -join '|')) {
        Invoke-Git @('checkout', '--', 'CHANGELOG.md', 'src/apps/Wavee/Wavee.Version.props') -AllowFailure | Out-Null
        throw "the bump touched [$($actual -join ', ')] but should only touch [$($want -join ', ')]; the tree was restored"
    }
    Good "modified: $($actual -join ', ')"
    Complete-Phase 'bump'
}

if (-not (Test-PhaseDone 'bump')) { Invoke-Bump } else { Note 'bump already done' }

# ===============================================================================================================
# 2  validate release notes  (Wavee.ReleaseTool)
# ===============================================================================================================

function Get-PreviousIndex {
    <#
        The feed's whatsnew-index.json, so the tool can prepend this release to it.

        A 404 means "there is no index yet" and is the legitimate first-release state. ANY other failure is fatal:
        the index is the cumulative history of every release, phase 10 uploads it with --clobber, and continuing
        without it would replace the published history with a one-entry file. That is unrecoverable from here (the
        old entries only exist on the feed), so the run stops instead.
    #>
    $dst = Join-Path $stage 'previous-index.json'
    $url = "https://github.com/$Repo/releases/download/$FeedRelease/whatsnew-index.json"
    Set-WaveeTls12
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $url -MaximumRedirection 5 -OutFile $dst
    }
    catch {
        $resp = $null
        if ($_.Exception.PSObject.Properties['Response']) { $resp = $_.Exception.Response }
        if ($resp -and ([int]$resp.StatusCode) -eq 404) {
            Note 'no whatsnew-index.json on the feed yet; this release starts the index'
            return ''
        }
        throw ("could not read the published whatsnew-index.json ($($_.Exception.Message)). " +
            "It is the cumulative release history and phase 10 clobbers it, so the run stops rather than " +
            "publishing a one-entry index over it. Fix the network/permissions and -Resume, or download " +
            "$url by hand to $dst.")
    }
    Note "previous index -> $dst"
    $dst
}

function Invoke-Notes {
    Step 'Validate release notes'
    if ($NoNotes) {
        New-Item -ItemType Directory -Force -Path $notesOut | Out-Null
        $body = "# Wavee $semver $EmDash $codename`n`nBuild $quad.`n"
        [IO.File]::WriteAllText((Join-Path $notesOut 'RELEASE_BODY.md'), $body, (New-Object System.Text.UTF8Encoding $false))
        Warn 'running with -NoNotes: no whatsnew.json, no index, a placeholder release body'
        Complete-Phase 'notes'
        return
    }

    # EVERYTHING that can fail in this phase is inside the one try: the previous-index fetch and the token read both
    # throw now (an unreadable published index, or gh printing prose where a token belongs), and phase 1a has already
    # bumped the counter and dated the CHANGELOG. Any failure here has to leave the tree as it was found.
    $previousToken = $env:GITHUB_TOKEN
    try {
        $prev = Get-PreviousIndex
        # Recorded in the ledger so a -Resume that skips this phase still knows whether the index it is about to
        # clobber was built on top of the published one. See Assert-IndexSafeToPublish.
        $script:State.previousIndexRead = [bool]$prev
        Save-State

        $toolArgs = @('run', '--project', $releaseToolProject, '-c', 'Release', '--',
            'validate',
            '--semver', $semver,
            '--quad', $quad,
            '--codename', $codename,
            '--channel', $Channel,
            '--changelog', $changelogForTool,
            '--notes', $notesSrc,
            '--out', $notesOut,
            '--repo', $Repo)
        if ($prev) { $toolArgs += @('--previous-index', $prev) }

        # The token reaches the child process through the environment only: it never lands in a command line, a
        # transcript, or release-state.json. Get-GhAuthToken owns the parsing (gh prints notices on stderr) and
        # throws rather than handing prose on as a credential.
        $token = Get-GhAuthToken
        if (-not $token) { Warn 'no gh token available; issue/PR titles and the generated commit list will be omitted' }

        if ($token) { $env:GITHUB_TOKEN = $token }
        Invoke-Native 'dotnet' $toolArgs | Out-Null
    }
    catch {
        if (-not $DryRun) {
            Warn 'notes validation failed; restoring CHANGELOG.md and Wavee.Version.props'
            Invoke-Git @('checkout', '--', 'CHANGELOG.md', 'src/apps/Wavee/Wavee.Version.props') -AllowFailure | Out-Null
            Reset-Phase 'bump'
        }
        throw
    }
    finally {
        $env:GITHUB_TOKEN = $previousToken
    }

    foreach ($f in @('whatsnew.json', 'whatsnew-index.json', 'RELEASE_BODY.md')) {
        if (-not (Test-Path (Join-Path $notesOut $f))) { throw "Wavee.ReleaseTool did not write $f into $notesOut" }
    }
    Good "notes -> $notesOut"
    Complete-Phase 'notes'
}

if (-not (Test-PhaseDone 'notes')) { Invoke-Notes } else { Note 'notes already validated' }

# ===============================================================================================================
# 1b  commit + annotated tag (local only)
# ===============================================================================================================

function Invoke-Tag {
    Step "Commit and tag $tag"
    if ($DryRun) {
        Note '-DryRun: nothing is committed or tagged'
        return
    }
    $message = "release: Wavee $semver $codename (build $quad)"
    Invoke-Git @('add', '--', 'CHANGELOG.md', 'src/apps/Wavee/Wavee.Version.props') | Out-Null
    Invoke-Git @('commit', '-m', $message) | Out-Null
    Invoke-Git @('tag', '-a', $tag, '-m', $message) | Out-Null
    $sha = (Invoke-Git @('rev-parse', '--short=7', 'HEAD')).Text
    $script:State.commit = $sha
    $script:State.branchPushed = $false
    $script:State.pushed = $false
    Good "$tag -> $sha"
    Complete-Phase 'tag'
}

if (-not (Test-PhaseDone 'tag')) { Invoke-Tag } else { Note "tag $tag already created" }
if ($script:State.commit) { $commit = "$($script:State.commit)" }

# ===============================================================================================================
# 3 / 4  pack
# ===============================================================================================================

function Invoke-Pack {
    param([Parameter(Mandatory = $true)][string]$A)

    $msix = Join-Path $stage (Get-MsixName $quad $A)

    if ($A -eq 'x64' -and $X64Msix) {
        Step "Adopt the prebuilt x64 package"
        Copy-Item $X64Msix $msix -Force
    }
    else {
        Step "Pack $A"
        $packArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $packScript,
            '-Arch', $A,
            '-Quad', $quad,
            '-Semver', $semver,
            '-Channel', $Channel,
            '-Codename', $codename,
            '-IdentityName', $IdentityName,
            '-Commit', $commit,
            '-BuildDate', $buildDate,
            '-NotesDir', $notesOut,
            '-FeedRelease', $FeedRelease,
            '-Publisher', $Publisher,
            '-OutputDir', $stage,
            '-NoSign',
            '-Configuration', $Configuration)
        if ($PublicOnly) { $packArgs += '-PublicOnly' }
        Invoke-Native 'powershell' $packArgs | Out-Null
    }

    if (-not (Test-Path $msix)) { throw "pack did not produce $msix" }
    $id = Get-MsixIdentity $msix
    if ($id.Version -ne $quad -or $id.ProcessorArchitecture -ne $A -or $id.Name -ne $IdentityName) {
        throw "package identity mismatch in $(Split-Path -Leaf $msix): Name=$($id.Name) Version=$($id.Version) Arch=$($id.ProcessorArchitecture); expected $IdentityName / $quad / $A"
    }
    if ($id.Publisher -ne $Publisher) {
        throw "publisher mismatch in $(Split-Path -Leaf $msix): '$($id.Publisher)' != '$Publisher' (signing would fail with 0x8007000B)"
    }
    Good "$(Split-Path -Leaf $msix)  $([math]::Round((Get-Item $msix).Length / 1MB, 2)) MB"
}

foreach ($a in $arches) {
    $phase = 'pack' + $a.Substring(0, 1).ToUpper() + $a.Substring(1)
    if (-not (Test-PhaseDone $phase)) {
        Invoke-Pack $a
        Complete-Phase $phase
    }
    else {
        Note "$a already packed"
    }
}

if (-not (Test-Path (Join-Path $stage 'THIRD-PARTY-NOTICES.txt'))) {
    throw "pack-wavee-msix.ps1 did not copy THIRD-PARTY-NOTICES.txt into $stage"
}

# ===============================================================================================================
# 5  sign (one signtool call over every package) + verify
# ===============================================================================================================

function Invoke-Sign {
    Step 'Sign'
    $tools = Get-Tools
    $toSign = @()
    foreach ($a in $arches) {
        $msix = Join-Path $stage (Get-MsixName $quad $a)
        # A freshly packed package is -NoSign, so it never verifies and always lands here. Only a package this run
        # adopted (-X64Msix) or already signed on an earlier attempt can be skipped.
        if (Test-MsixSignature $msix $tools.SignTool) {
            Note "$(Split-Path -Leaf $msix) already carries a trusted signature; leaving it alone"
            continue
        }
        $toSign += $msix
    }

    if ($NoSign) {
        Warn '-NoSign: packages are UNSIGNED and cannot be published'
        return
    }
    if ($toSign.Count -eq 0) {
        Note 'nothing to sign'
        return
    }

    Invoke-TrustedSigning -Path $toSign -Metadata $Metadata -Subscription $Subscription -SignTool $tools.SignTool
    foreach ($a in $arches) {
        $msix = Join-Path $stage (Get-MsixName $quad $a)
        if (-not (Test-MsixSignature $msix $tools.SignTool)) { throw "signature did not verify: $msix" }
        Good "$(Split-Path -Leaf $msix) verified"
    }
}

if (-not (Test-PhaseDone 'sign')) {
    Invoke-Sign
    Complete-Phase 'sign'
}
else {
    Note 'already signed'
}

# ===============================================================================================================
# 6  .appinstaller per architecture
# ===============================================================================================================

function Invoke-AppInstaller {
    Step 'Write the .appinstaller feed documents'
    foreach ($a in $arches) {
        $out = Join-Path $stage (Get-AppInstallerName $a)
        New-WaveeAppInstaller -Template $appInstallerTemplate -OutFile $out -Arch $a -Quad $quad `
            -Publisher $Publisher -IdentityName $IdentityName `
            -FeedUri (Get-FeedUri $a) -MsixUri (Get-MsixUri $quad $a) | Out-Null
        Good "$(Split-Path -Leaf $out)  Version=$quad  ->  $(Get-MsixUri $quad $a)"
    }
}

if (-not (Test-PhaseDone 'appinstaller')) {
    Invoke-AppInstaller
    Complete-Phase 'appinstaller'
}
else {
    Note 'appinstaller documents already written'
}

# ===============================================================================================================
# 7  stage + MANIFEST.txt
# ===============================================================================================================

$script:ReleaseAssets = @()
$script:FeedAssets = @()

function Assert-IndexSafeToPublish {
    <#
        whatsnew-index.json is CUMULATIVE history and it is uploaded with --clobber, so publishing one that was not
        built on top of the published one destroys every older entry. Phase 2 records whether it read the previous
        index; this refuses to stage or upload when it did not AND the feed release still carries one.
    #>
    param([Parameter(Mandatory = $true)][string]$Phase)

    if ($NoNotes) { return }
    if ($script:State.previousIndexRead) { return }
    if ($DryRun -or $NoUpload) {
        Warn "$Phase : the previous whatsnew-index.json was not read; a real run would check the feed before clobbering it"
        return
    }
    $frel = Get-GhRelease $Repo $FeedRelease
    if (-not $frel) { return }
    $hit = @($frel.assets | Where-Object { $_.name -eq 'whatsnew-index.json' })
    if ($hit.Count -gt 0) {
        throw ("$Phase refuses to touch whatsnew-index.json: $FeedRelease already publishes one, but phase 2 could " +
            "not read it, so the staged index carries only this release. Delete <staging>\release-state.json's " +
            "'notes' phase and re-run phase 2 with the feed reachable.")
    }
}

function Copy-MediaAssets {
    <#  Release assets have flat names, so whatsnew.json's "media/foo.webp" is uploaded as "foo.webp". #>
    $names = @()
    $doc = Join-Path $notesOut 'whatsnew.json'
    if (Test-Path $doc) {
        $json = Get-Content $doc -Raw | ConvertFrom-Json
        if ($json.media) {
            foreach ($m in $json.media) {
                $src = "$($m.src)"
                if (-not $src) { continue }
                $from = Join-Path $notesOut ($src -replace '/', '\')
                if (-not (Test-Path $from)) { throw "whatsnew.json references media that is not in $notesOut : $src" }
                $leaf = Split-Path -Leaf $from
                Copy-Item $from (Join-Path $stage $leaf) -Force
                $names += $leaf
            }
        }
    }
    if ($names.Count -eq 0) {
        $mediaDir = Join-Path $notesOut 'media'
        if (Test-Path $mediaDir) {
            foreach ($f in (Get-ChildItem $mediaDir -File -Recurse)) {
                Copy-Item $f.FullName (Join-Path $stage $f.Name) -Force
                $names += $f.Name
            }
        }
    }
    @($names | Sort-Object -Unique)
}

function Invoke-Stage {
    Step 'Stage assets'
    Assert-IndexSafeToPublish 'stage'
    foreach ($f in @('whatsnew.json', 'whatsnew-index.json', 'RELEASE_BODY.md')) {
        $from = Join-Path $notesOut $f
        if (Test-Path $from) { Copy-Item $from (Join-Path $stage $f) -Force }
    }
    $media = Copy-MediaAssets
    if ($media.Count -gt 0) { Note "media: $($media -join ', ')" }

    $release = @()
    foreach ($a in $arches) { $release += (Get-MsixName $quad $a) }
    $release += 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path (Join-Path $stage 'whatsnew.json')) { $release += 'whatsnew.json' }
    $release += $media

    $feed = @()
    foreach ($a in $arches) { $feed += (Get-AppInstallerName $a) }
    if (Test-Path (Join-Path $stage 'whatsnew-index.json')) { $feed += 'whatsnew-index.json' }

    # MANIFEST.txt covers every asset but itself; RELEASE_BODY.md is the release description, not an asset.
    Write-ReleaseManifest -Dir $stage -Files (@($release + $feed) | Sort-Object -Unique) -OutFile (Join-Path $stage 'MANIFEST.txt') | Out-Null
    $release += 'MANIFEST.txt'

    $script:State.releaseAssets = @($release | Sort-Object -Unique)
    $script:State.feedAssets = @($feed | Sort-Object -Unique)
    Complete-Phase 'stage'
}

if (-not (Test-PhaseDone 'stage')) { Invoke-Stage } else { Note 'assets already staged' }

$script:ReleaseAssets = @($script:State.releaseAssets)
$script:FeedAssets = @($script:State.feedAssets)
$releasePaths = @($script:ReleaseAssets | ForEach-Object { Join-Path $stage $_ })
$feedPaths = @($script:FeedAssets | ForEach-Object { Join-Path $stage $_ })
$bodyFile = Join-Path $stage 'RELEASE_BODY.md'
$title = "Wavee $semver $EmDash $codename"

Step 'Staged'
Get-ChildItem $stage -File | Sort-Object Name |
    Select-Object @{ n = 'Asset'; e = { $_.Name } }, @{ n = 'Bytes'; e = { $_.Length } } |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Note "folder: $stage"

if ($DryRun -or $NoUpload) {
    Step 'Stopping before upload'
    Write-Host ''
    Write-Host '   Everything below is what a real run would do next:' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host "   git push origin HEAD:refs/heads/$Branch"
    Write-Host "   git push origin refs/tags/$tag"
    Write-Host "   gh release create $tag --repo $Repo --draft --verify-tag --title `"$title`" --notes-file `"$bodyFile`""
    Write-Host "   gh release upload $tag --repo $Repo --clobber $(($releasePaths | ForEach-Object { '"' + $_ + '"' }) -join ' ')"
    # --latest=false, exactly as Publish-WaveeRelease issues it: `releases/latest` is the GALLERY's update feed and a
    # Wavee release must never claim it. The preview has to mirror the real command or it teaches the wrong thing.
    $editPreview = "   gh release edit $tag --repo $Repo --draft=false --title `"$title`" --notes-file `"$bodyFile`""
    if ($Channel -eq 'beta') { $editPreview += ' --prerelease' }
    Write-Host "$editPreview --latest=false"
    foreach ($f in $feeds) {
        Write-Host "   gh release upload $f --repo $Repo --clobber $(($feedPaths | ForEach-Object { '"' + $_ + '"' }) -join ' ')"
    }
    Write-Host ''
    Good "dry run complete: $stage"
    return
}

# ===============================================================================================================
# 8  push
# ===============================================================================================================

function Invoke-Push {
    Step "Push $Branch and $tag"
    Invoke-Git @('fetch', 'origin', '--tags') | Out-Null
    $head = (Invoke-Git @('rev-parse', 'HEAD')).Text
    $parent = (Invoke-Git @('rev-parse', 'HEAD~1')).Text
    $remote = Invoke-Git @('rev-parse', "origin/$Branch") -AllowFailure
    if ($remote.ExitCode -eq 0) {
        if ($remote.Text -ne $parent) {
            throw "origin/$Branch is $($remote.Text.Substring(0,7)) but HEAD~1 is $($parent.Substring(0,7)); someone pushed while this release was building. Rebase the release commit and -Resume."
        }
    }
    elseif (-not $Force) {
        throw "origin/$Branch does not exist (use -Force to create it)"
    }
    Invoke-Git @('push', 'origin', "HEAD:refs/heads/$Branch") | Out-Null
    # Recorded BETWEEN the two pushes: the branch push is already published history, so if the tag push then fails,
    # -Abort must still refuse to reset the commit. Only both together make the release "pushed".
    $script:State.branchPushed = $true
    Save-State
    Invoke-Git @('push', 'origin', "refs/tags/$tag") | Out-Null
    $script:State.pushed = $true
    Save-State
    Good "$Branch -> $($head.Substring(0,7)), tag $tag"
}

if (-not (Test-PhaseDone 'push')) {
    Invoke-Push
    Complete-Phase 'push'
}
else {
    Note "$tag already pushed"
}

# ===============================================================================================================
# 9  GitHub release
# ===============================================================================================================

if (-not (Test-PhaseDone 'release')) {
    Step "Publish $tag"
    Publish-WaveeRelease -Repo $Repo -Tag $tag -Title $title -BodyFile $bodyFile -Assets $releasePaths `
        -Prerelease:($Channel -eq 'beta')
    Good "https://github.com/$Repo/releases/tag/$tag"
    Complete-Phase 'release'
}
else {
    Note "release $tag already published"
}

# ===============================================================================================================
# 10  feed (LAST: this is the moment installed clients see the new version)
# ===============================================================================================================

if (-not (Test-PhaseDone 'feed')) {
    Step 'Repoint the rolling feed'
    Assert-IndexSafeToPublish 'feed'
    foreach ($f in $feeds) {
        Update-WaveeFeed -Repo $Repo -FeedRelease $f -FeedBodyFile $feedBodyFile -Assets $feedPaths -Target $Branch
        Good "$f updated"
    }
    Complete-Phase 'feed'
}
else {
    Note 'feed already repointed'
}

# ===============================================================================================================
# 11  verify
# ===============================================================================================================

$script:FeedRows = @()

function Invoke-Verify {
    Step 'Verify'
    $rel = Get-GhRelease $Repo $tag
    if (-not $rel) { throw "release $tag is not visible" }
    if ($rel.isDraft) { throw "release $tag is still a draft" }

    foreach ($name in $script:ReleaseAssets) {
        $local = Get-Item (Join-Path $stage $name)
        $hit = @($rel.assets | Where-Object { $_.name -eq $name })
        if ($hit.Count -eq 0) { throw "release $tag is missing asset $name" }
        if ([long]$hit[0].size -ne $local.Length) {
            throw "asset $name is $($hit[0].size) bytes on the release but $($local.Length) bytes locally"
        }
    }
    Good "$($script:ReleaseAssets.Count) release assets match the staged bytes"

    foreach ($f in $feeds) {
        $frel = Get-GhRelease $Repo $f
        if (-not $frel) { throw "feed release $f is not visible" }
        foreach ($name in $script:FeedAssets) {
            $local = Get-Item (Join-Path $stage $name)
            $hit = @($frel.assets | Where-Object { $_.name -eq $name })
            if ($hit.Count -eq 0) { throw "feed $f is missing asset $name" }
            if ([long]$hit[0].size -ne $local.Length) {
                throw "feed asset $name is $($hit[0].size) bytes on $f but $($local.Length) bytes locally"
            }
        }
        foreach ($a in $arches) {
            # The quad alone is not proof the feed points at THIS release: a document whose root Version moved but
            # whose MainPackage/@Uri still names the previous tag would hand every client the old package.
            $live = Test-WaveeFeedLive -Repo $Repo -FeedRelease $f -Arch $a -AssetPrefix $AssetPrefix `
                -ExpectedQuad $quad -ExpectedMsixUri (Get-MsixUri $quad $a)
            if (-not $live) { throw "feed $f/$a did not come up at $quad pointing at $(Get-MsixUri $quad $a)" }
            $script:FeedRows += [pscustomobject]@{ Feed = $f; Arch = $a; Version = $quad }
            Good "$f/$a is live at $quad"
        }
    }

    foreach ($a in $arches) {
        $name = Get-MsixName $quad $a
        $url = Get-GhReleaseAssetUrl -Repo $Repo -Tag $tag -Name $name -Release $rel
        $bytes = (Get-Item (Join-Path $stage $name)).Length
        if (-not (Test-AssetContentLength -Url $url -ExpectedBytes $bytes)) {
            throw "the published $name does not report $bytes bytes; App Installer needs an exact Content-Length"
        }
        Good "$name serves $bytes bytes"
    }

    if ($InstallFromFeed) {
        $hostArch = 'arm64'
        $pa = $env:PROCESSOR_ARCHITEW6432
        if (-not $pa) { $pa = $env:PROCESSOR_ARCHITECTURE }
        if ("$pa" -notmatch 'ARM64') { $hostArch = 'x64' }
        if ($arches -notcontains $hostArch) { throw "-InstallFromFeed needs the $hostArch package, which this run did not build" }
        $feedUrl = Get-FeedUri $hostArch
        Step "Install from $feedUrl"
        Add-AppxPackage -AppInstallerFile $feedUrl
        Good 'installed with the App Installer association'
    }
}

if (-not (Test-PhaseDone 'verify')) {
    Invoke-Verify
    Complete-Phase 'verify'
}
else {
    Note 'already verified'
}

# ===============================================================================================================
# Summary
# ===============================================================================================================

Step 'Summary'
[pscustomobject]@{
    Tag      = $tag
    Version  = "$semver $EmDash $codename"
    Quad     = $quad
    Channel  = $Channel
    Branch   = $Branch
    Commit   = $commit
    Staging  = $stage
    Release  = "https://github.com/$Repo/releases/tag/$tag"
} | Format-List | Out-String -Width 200 | Write-Host

Write-Host '   Release assets' -ForegroundColor DarkGray
@($script:ReleaseAssets | ForEach-Object { [pscustomobject]@{ Asset = $_; Bytes = (Get-Item (Join-Path $stage $_)).Length } }) |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host '   Feed assets' -ForegroundColor DarkGray
@($script:FeedAssets | ForEach-Object { [pscustomobject]@{ Asset = $_; Bytes = (Get-Item (Join-Path $stage $_)).Length } }) |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host '   Feed heads' -ForegroundColor DarkGray
if ($script:FeedRows.Count -eq 0) {
    foreach ($f in $feeds) {
        foreach ($a in $arches) { $script:FeedRows += [pscustomobject]@{ Feed = $f; Arch = $a; Version = $quad } }
    }
}
$script:FeedRows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Good "Wavee $semver $EmDash $codename is live."
