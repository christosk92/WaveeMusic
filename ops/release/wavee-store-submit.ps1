#requires -Version 5.1
<#
.SYNOPSIS
  Submit one already-released Wavee version to the Microsoft Store from this machine.

.DESCRIPTION
  The Store leg always FOLLOWS a feed release: ops\release\wavee-release.ps1 has already bumped the counter,
  tagged wavee-v<semver> and published the feed, so this script never touches the tree or the counter. It packs
  the two store-channel packages from the tag commit, folds them into one .msixupload, creates a draft submission
  through msstore-cli (the Microsoft Store Developer CLI over the DevCenter submission API), patches the en-US
  "What's new" text, commits the submission and polls until the Store has taken it into certification.

  The run is a ledger of phases. Every phase records itself in <staging>\store-state.json, so -Resume restarts
  exactly where a failed run stopped, and -Abort unwinds an uncommitted draft completely.

      0  preflight   hard gate table (versions, tag, tree, quad, notes, msstore, toolchains, Pester)
      1  packArm64   pack-wavee-msix.ps1 -Channel store -Arch arm64 (unsigned: the Store signs)
         packX64     pack-wavee-msix.ps1 -Channel store -Arch x64   (or -X64Msix <path> to adopt a prebuilt
                     STORE-channel package; identity is re-verified either way)
      2  msixupload  both .msix flat into one Wavee_<quad>_store.msixupload
      3  notes       store-listing.txt (adopted from the feed release's notes, or rendered by Wavee.ReleaseTool)
      4  draft       msstore publish --noCommit - the FIRST mutating msstore call; record submissionId
      5  metadata    patch en-US BaseListing.ReleaseNotes, submission update, re-get, assert the round-trip
      6  commit      msstore submission publish (the point of no return: a committed submission cannot be recalled)
      7  poll        submission status every 30 s up to -PollMinutes; certification itself takes 1-3 business days

  -DryRun stops after phase 3 and prints the exact msstore commands a real run would issue next. An API-created
  submission must NEVER be opened or edited in Partner Center: a hand-edit desyncs the API's view and the next
  update call would clobber it. Screenshots, description and trailer stay manual (Partner Center, between
  submissions); each automated submission updates the two packages and the en-US release notes only.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-store-submit.ps1 -DryRun -SkipTests
  Packs both store packages, builds the .msixupload, resolves the listing text and stops. No msstore calls.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-store-submit.ps1
  The real thing: pack, upload, patch the notes, commit the submission, poll until certification starts.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-store-submit.ps1 -Status
  Report the current submission state (and the last published one when nothing is pending).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-store-submit.ps1 -Abort
  Delete this run's uncommitted draft submission and remove the staging folder. Refused once committed.

.LINK
  docs\guide\microsoft-store-onboarding.md

.LINK
  docs\guide\releasing-wavee.md
#>
[CmdletBinding()]
param(
    # Defaults to the props semver; a value that disagrees with Wavee.Version.props is refused in preflight,
    # because the store quad folds the props BUILD COUNTER and only HEAD's props carry the released one.
    [string]$Semver = '',
    [string[]]$Arch = @('arm64', 'x64'),
    [ValidateSet('arm64', 'x64')][string]$SkipArch,
    [string]$X64Msix,
    [switch]$DryRun,
    [switch]$Resume,
    [switch]$Abort,
    [switch]$Status,
    [switch]$SkipTests,
    [switch]$Force,
    [string]$ProductId = '9NJPVWTQPT9H',
    [string]$IdentityName = 'cproducts.Wavee',
    # Partner Center's package identity publisher ("View product identity"), never our Trusted Signing subject:
    # the Store re-signs every package under this CN.
    [string]$StorePublisher = 'CN=88D90E00-BEC4-41D6-8623-9F49F1AE2E9E',
    [string]$Pfn = 'cproducts.Wavee_thwr6bfjtcshw',
    # Where the feed release's notes live (store-listing.txt + whatsnew.json). Default: the feed release's own
    # staging output, artifacts\release\<semver>\notes.
    [string]$NotesDir = '',
    [string]$Configuration = 'Release',
    [string]$OutputDir = 'artifacts/store',
    [int]$PollMinutes = 15)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $root 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Wavee.Release.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Wavee.Store.psm1') -Force -DisableNameChecking

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

function Get-JsonProperty {
    <#  Case-insensitive property read: msstore-cli's JSON casing is a documented open risk, so nothing in this
        script ever dots into a submission object directly. #>
    param($Object, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq $Object) { return $null }
    foreach ($p in $Object.PSObject.Properties) {
        if ($p.Name -ieq $Name) { return $p.Value }
    }
    $null
}

# ===============================================================================================================
# store-state.json ledger
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

if ($DryRun -and ($Resume -or $Abort -or $Status)) { throw '-DryRun excludes -Resume / -Abort / -Status' }
if ($Status -and ($Resume -or $Abort -or $SkipTests -or $Force -or $X64Msix -or $SkipArch)) { throw '-Status excludes every other switch' }
if ($Abort -and ($Resume -or $Status -or $SkipTests -or $Force -or $X64Msix -or $SkipArch)) { throw '-Abort excludes every other switch' }
if ($X64Msix -and $SkipArch -eq 'x64') { throw '-X64Msix excludes -SkipArch x64' }

$arches = @($Arch | Where-Object { $_ -ne $SkipArch })
if ($arches.Count -eq 0) { throw 'no architectures left after -SkipArch' }

# ===============================================================================================================
# Paths and identity of this run
# ===============================================================================================================

$propsPath = Join-Path $root 'src\apps\Wavee\Wavee.Version.props'
$packScript = Join-Path $root 'ops\build\pack-wavee-msix.ps1'
$releaseToolProject = Join-Path $root 'src\apps\Wavee.ReleaseTool'
$playPlayProbe = Join-Path $root 'src\apps\Wavee.PlayPlay\Client\InProcessPlayPlayKeyDeriver.cs'
$onboardingDoc = 'docs\guide\microsoft-store-onboarding.md'

$props = Get-WaveeVersionProps $propsPath
if (-not $Semver) { $Semver = $props.Version }
$semver = $Semver
$codename = $props.Codename

$tag = "wavee-v$semver"
$stageRoot = $OutputDir
if (-not [IO.Path]::IsPathRooted($stageRoot)) { $stageRoot = Join-Path $root ($OutputDir -replace '/', '\') }
$stageSuffix = ''
if ($DryRun) { $stageSuffix = '-dryrun' }
$stage = Join-Path $stageRoot ($semver + $stageSuffix)
$script:StatePath = Join-Path $stage 'store-state.json'

function Get-StoreMsixName { param([string]$Quad, [string]$A) "Wavee_${Quad}_$A.msix" }
function Get-MsixUploadName { param([string]$Quad) "Wavee_${Quad}_store.msixupload" }

$script:MsStoreVersion = ''
function Assert-MsStoreCli {
    $r = Invoke-Native 'msstore' @('--version') -AllowFailure
    if ($r.ExitCode -ne 0) {
        throw ('msstore CLI not found or not runnable: winget install "Microsoft Store Developer CLI" ' +
            '(it also needs the .NET 9 Desktop Runtime), then msstore reconfigure - see ' + $onboardingDoc + ' section 6')
    }
    $script:MsStoreVersion = (@($r.Output) -join ' ').Trim()
    $script:MsStoreVersion
}

function Get-CurrentSubmissionState {
    $text = Invoke-MsStore -Arguments @('submission', 'status', $ProductId) -AllowFailure
    Get-StoreSubmissionState -StatusJson $text
}

# ===============================================================================================================
# -Status
# ===============================================================================================================

function Invoke-Status {
    Step "Store status $EmDash product $ProductId"
    Note "msstore $(Assert-MsStoreCli)"

    $st = Get-CurrentSubmissionState
    if ($st.SubmissionId -or $st.Status) {
        [pscustomobject]@{
            SubmissionId = $st.SubmissionId
            Status       = $st.Status
            Pending      = $st.Pending
            Terminal     = $st.Terminal
            Failed       = $st.Failed
        } | Format-List | Out-String -Width 200 | Write-Host
        foreach ($e in @($st.Errors)) { if ($e) { Warn "$e" } }
    }
    else {
        Note 'msstore reported no submission status'
    }

    $ledger = Get-ReleaseState $script:StatePath
    if ($ledger -and $ledger.submissionId) {
        Note "this machine's ledger: submission $($ledger.submissionId), committed=$($ledger.committed) ($script:StatePath)"
    }

    if (-not $st.Pending) {
        $app = ConvertFrom-MsStoreJson -Text (Invoke-MsStore -Arguments @('apps', 'get', $ProductId) -AllowFailure)
        $last = Get-JsonProperty $app 'lastPublishedApplicationSubmission'
        if ($last) {
            Good "nothing pending; last published submission: $(Get-JsonProperty $last 'id')"
        }
        else {
            Note 'nothing pending and nothing published yet'
        }
    }

    if ($st.Failed -or "$($st.Status)" -match 'Failed') {
        Warn 'recovery: fix what the errors above name (listing content in Partner Center, packages here), then'
        Note "  msstore submission delete $ProductId     # a failed submission blocks a new one until deleted"
        Note '  powershell -File ops\release\wavee-store-submit.ps1   # re-pack and re-submit'
    }
}

if ($Status) { Invoke-Status; return }

# ===============================================================================================================
# -Abort
# ===============================================================================================================

function Invoke-StoreAbort {
    Step "Abort the store submission for $tag"
    $st = Get-ReleaseState $script:StatePath
    if (-not $st) { throw "no store-state.json under $stage - nothing to abort" }
    if ($st.committed) {
        throw ('this run already committed its submission; a committed submission cannot be recalled from here. ' +
            'Wait for certification and use -Status; a CertificationFailed submission is deleted with ' +
            "'msstore submission delete $($st.productId)' and re-run")
    }
    if ($st.productId) { $ProductId = "$($st.productId)" }

    if ($st.submissionId) {
        Assert-MsStoreCli | Out-Null
        $cur = Get-CurrentSubmissionState
        if (-not $cur.Pending) {
            Note "no pending submission on the Store; nothing to delete"
        }
        elseif ("$($cur.SubmissionId)" -ne "$($st.submissionId)") {
            throw "the pending submission is $($cur.SubmissionId) but this run created $($st.submissionId); refusing to delete a submission this run did not make"
        }
        else {
            Note "deleting draft submission $($st.submissionId)"
            Invoke-MsStore -Arguments @('submission', 'delete', $ProductId) | Out-Null
        }
    }
    else {
        Note 'no draft submission was created; only staging is removed'
    }

    if (Test-Path $stage) {
        Note "removing $stage"
        Remove-Item $stage -Recurse -Force
    }
    Good 'aborted; the Store and the tree are back to where they started'
}

if ($Abort) { Invoke-StoreAbort; return }

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
    $storeQuad = "$($script:State.quad)"
    $commit = "$($script:State.commit)"
    $buildDate = "$($script:State.buildDate)"
    $tag = "$($script:State.tag)"
    $ProductId = "$($script:State.productId)"
    $arches = @($script:State.arches)
    Step "Resume store submission $tag (quad $storeQuad)"
    Note "staging $stage"
}
else {
    $storeQuad = ''
    $commit = ''
    $buildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $script:State = @{
        schema           = 1
        semver           = $semver
        codename         = $codename
        quad             = $storeQuad
        tag              = $tag
        commit           = $commit
        buildDate        = $buildDate
        productId        = $ProductId
        arches           = $arches
        submissionId     = ''
        packagesUploaded = $false
        committed        = $false
        dryRun           = [bool]$DryRun
        phases           = @{}
    }
}

if (-not $NotesDir) { $NotesDir = Join-Path $root ("artifacts\release\$semver\notes") }
elseif (-not [IO.Path]::IsPathRooted($NotesDir)) { $NotesDir = Join-Path $root ($NotesDir -replace '/', '\') }

# ===============================================================================================================
# 0  preflight
# ===============================================================================================================

if (-not (Test-PhaseDone 'preflight')) {
    Step "Preflight store submission $tag (product $ProductId)"

    Add-Check 'version props' 'hard' {
        if ($semver -ne $props.Version) {
            throw "-Semver $semver != Wavee.Version.props $($props.Version); the store quad folds the props build counter, so only the props semver can be submitted"
        }
        "$semver / $codename / build $($props.Build)"
    }
    Add-Check 'stable semver' 'hard' {
        $sv = Test-WaveeSemver $semver
        if ($sv.Channel -ne 'stable') { throw 'the Store listing carries the stable channel only; a beta semver is refused' }
        $sv.Channel
    }
    Add-Check "tag $tag" 'hard' {
        # The Store submission always follows the feed release: the tag must exist here AND on origin, and the
        # working copy must BE that commit so the packed bytes are the released bytes. -Force downgrades only the
        # HEAD equality (pack from HEAD anyway) - never the tag's existence.
        Invoke-Git @('fetch', 'origin', '--tags') -AllowFailure | Out-Null
        $local = Invoke-Git @('rev-list', '-n', '1', $tag) -AllowFailure
        if ($local.ExitCode -ne 0 -or -not $local.Text) { throw "tag $tag does not exist locally; run ops\release\wavee-release.ps1 first (the feed release cuts the tag)" }
        if (-not (Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")).Text) { throw "tag $tag is not on origin; push it first" }
        $head = (Invoke-Git @('rev-parse', 'HEAD')).Text
        if ($head -ne $local.Text) {
            if ($Force) { return "SKIP: HEAD $($head.Substring(0,7)) != $tag $($local.Text.Substring(0,7)), packing from HEAD by -Force" }
            throw "HEAD $($head.Substring(0,7)) != $tag $($local.Text.Substring(0,7)); check out the tag commit (or -Force to pack from HEAD)"
        }
        "$tag @ $($local.Text.Substring(0,7)), HEAD matches"
    }
    Add-Check 'working tree clean' 'hard' {
        if ($Force) { return 'SKIP: -Force' }
        $dirty = (Invoke-Git @('status', '--porcelain')).Lines
        if ($dirty.Count -gt 0) { throw "$($dirty.Count) modified path(s), first: $($dirty[0]) (use -Force to pack a dirty tree)" }
        'clean'
    }
    Add-Check 'store quad' 'hard' {
        $q = ConvertTo-WaveeStoreQuad -Semver $semver -Build $props.Build
        Test-WaveeStoreQuad -Quad $q
        $script:PreflightQuad = $q
        "quad $q"
    }
    Add-Check 'staging folder' 'hard' {
        # A previous rehearsal's folder is disposable (it carries no submission state), so -DryRun replaces it.
        if ((Test-Path $stage) -and -not $Force -and -not $DryRun -and -not $Resume) { throw "$stage already exists (use -Force to replace it, or -Resume to continue it)" }
        $stage
    }
    Add-Check 'store notes' 'hard' {
        $listing = Join-Path $NotesDir 'store-listing.txt'
        if (Test-Path $listing) {
            $t = Get-StoreReleaseNotesText -Path $listing
            return "store-listing.txt ($($t.Length) chars)"
        }
        if (Test-Path (Join-Path $NotesDir 'whatsnew.json')) {
            return "no store-listing.txt; will render from $NotesDir\whatsnew.json via Wavee.ReleaseTool"
        }
        throw "$NotesDir has neither store-listing.txt nor whatsnew.json; run the feed release's notes phase first (or pass -NotesDir)"
    }
    Add-Check 'msstore cli' 'hard' {
        if ($DryRun) { return 'SKIP: -DryRun makes no msstore calls' }
        "msstore $(Assert-MsStoreCli) (preview CLI: this is the version this run was proven against)"
    }
    Add-Check 'msstore app' 'hard' {
        if ($DryRun) { return 'SKIP: -DryRun makes no msstore calls' }
        $raw = Invoke-MsStore -Arguments @('apps', 'get', $ProductId) -AllowFailure
        $app = ConvertFrom-MsStoreJson -Text $raw
        if (-not $app) {
            throw ("'msstore apps get $ProductId' returned no JSON - the CLI is unconfigured or its client secret " +
                "expired: msstore reconfigure with the Entra app recorded in $onboardingDoc section 6")
        }
        $script:AppObject = $app
        "$(Test-StoreAppIdentity -AppJson $app -ProductId $ProductId -IdentityName $IdentityName -Pfn $Pfn); msstore $script:MsStoreVersion"
    }
    Add-Check 'first submission published' 'hard' {
        if ($DryRun) { return 'SKIP: -DryRun makes no msstore calls' }
        $last = Get-JsonProperty $script:AppObject 'lastPublishedApplicationSubmission'
        if (-not $last) { throw 'automation needs one completed manual submission: nothing has published yet (Submission 1 is still in certification, or was never completed)' }
        "last published submission $(Get-JsonProperty $last 'id')"
    }
    Add-Check 'no pending submission' 'hard' {
        if ($DryRun) { return 'SKIP: -DryRun makes no msstore calls' }
        $st = Get-CurrentSubmissionState
        if ($st.Pending) {
            if ($Resume -and $st.SubmissionId -and ("$($st.SubmissionId)" -eq "$($script:State.submissionId)")) {
                return "resuming this run's pending submission $($st.SubmissionId)"
            }
            throw ("a pending submission already exists ($($st.SubmissionId), $($st.Status)); 'msstore publish' would " +
                'silently delete it. -Resume the run that made it, -Abort it, or delete it by hand first')
        }
        'none pending'
    }
    Add-Check 'Windows SDK tools' 'hard' { "$((Get-Tools).Version)" }
    Add-Check 'x64 cross toolchain' 'hard' {
        if ($arches -notcontains 'x64') { return 'SKIP: x64 not requested' }
        if ($X64Msix) {
            if (-not (Test-Path $X64Msix)) { throw "-X64Msix path not found: $X64Msix" }
            return "SKIP: adopting $X64Msix"
        }
        $x = Test-X64CrossToolchain
        if (-not $x.Ok) { throw "$($x.Reason) (use -SkipArch x64, or -X64Msix <prebuilt store .msix>)" }
        "$($x.LinkExe)"
    }
    Add-Check 'PlayPlay junction' 'hard' {
        if (-not (Test-Path $playPlayProbe)) { throw 'src\apps\Wavee.PlayPlay junction missing; a Store package is PlayPlay-inclusive like every release build' }
        'present'
    }
    Add-Check 'gates' 'hard' {
        # Only the release tooling's own Pester suite: the tagged commit already passed the full Debug+Release
        # build and Wavee.Tests gate when the feed release was cut, and this run rebuilds those same sources.
        if ($SkipTests) { return 'SKIP: -SkipTests' }
        $pester = Invoke-Pester -Path (Join-Path $root 'ops\release\tests') -PassThru -Quiet
        if ($pester.FailedCount -gt 0) { throw ('Pester: ' + $pester.FailedCount + ' failed') }
        'Pester ' + $pester.PassedCount + '/0'
    }

    Assert-Checks

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    $storeQuad = $script:PreflightQuad
    if (-not $commit) { $commit = (Invoke-Git @('rev-parse', '--short=7', 'HEAD')).Text }
    $script:State.quad = $storeQuad
    $script:State.commit = $commit
    $script:State.arches = $arches
    Complete-Phase 'preflight'
}
else {
    Note "preflight already done for $tag"
}

Get-Tools | Out-Null

$upload = Join-Path $stage (Get-MsixUploadName $storeQuad)
$listingPath = Join-Path $stage 'store-listing.txt'

# ===============================================================================================================
# 1  pack (per architecture, store channel)
# ===============================================================================================================

function Invoke-StorePack {
    param([Parameter(Mandatory = $true)][string]$A)

    $msix = Join-Path $stage (Get-StoreMsixName $storeQuad $A)

    if ($A -eq 'x64' -and $X64Msix) {
        Step 'Adopt the prebuilt x64 store package'
        Copy-Item $X64Msix $msix -Force
    }
    else {
        Step "Pack $A (store channel)"
        # The store channel forces -NoSign itself (the Store re-signs) and defaults the Publisher to the Partner
        # Center identity; both are still passed explicitly so the identity check below cannot be right by luck.
        # -FeedRelease is left at the pack script's default: it is baked update metadata a Store install never
        # polls (the Store owns updates), and the default keeps the stamp identical to a dev pack's.
        $packArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $packScript,
            '-Arch', $A,
            '-Quad', $storeQuad,
            '-Semver', $semver,
            '-Channel', 'store',
            '-Codename', $codename,
            '-IdentityName', $IdentityName,
            '-Commit', $commit,
            '-BuildDate', $buildDate,
            '-Publisher', $StorePublisher,
            '-OutputDir', $stage,
            '-Configuration', $Configuration)
        if (Test-Path (Join-Path $NotesDir 'whatsnew.json')) {
            $packArgs += @('-NotesDir', $NotesDir)
        }
        else {
            Warn "no whatsnew.json under $NotesDir; the packaged What's New page will have no embedded notes"
        }
        Invoke-Native 'powershell' $packArgs | Out-Null
    }

    if (-not (Test-Path $msix)) { throw "pack did not produce $msix" }
    # An adopted package must be a STORE-channel package: the quad is the store mapping and the manifest Publisher
    # is Partner Center's, or the Store rejects the upload after the slow part is already done.
    $id = Get-MsixIdentity $msix
    if ($id.Version -ne $storeQuad -or $id.ProcessorArchitecture -ne $A -or $id.Name -ne $IdentityName) {
        throw "package identity mismatch in $(Split-Path -Leaf $msix): Name=$($id.Name) Version=$($id.Version) Arch=$($id.ProcessorArchitecture); expected $IdentityName / $storeQuad / $A (a feed-channel package carries the feed quad - pack with -Channel store)"
    }
    if ($id.Publisher -ne $StorePublisher) {
        throw "publisher mismatch in $(Split-Path -Leaf $msix): '$($id.Publisher)' != '$StorePublisher' (that is a feed-channel package; the Store needs Partner Center's identity)"
    }
    Good "$(Split-Path -Leaf $msix)  $([math]::Round((Get-Item $msix).Length / 1MB, 2)) MB"
}

foreach ($a in $arches) {
    $phase = 'pack' + $a.Substring(0, 1).ToUpper() + $a.Substring(1)
    if (-not (Test-PhaseDone $phase)) {
        Invoke-StorePack $a
        Complete-Phase $phase
    }
    else {
        Note "$a already packed"
    }
}

# ===============================================================================================================
# 2  msixupload (both packages in one container)
# ===============================================================================================================

if (-not (Test-PhaseDone 'msixupload')) {
    Step 'Assemble the .msixupload'
    $msixPaths = @($arches | ForEach-Object { Join-Path $stage (Get-StoreMsixName $storeQuad $_) })
    New-WaveeMsixUpload -Msix $msixPaths -OutFile $upload -IdentityName $IdentityName -Publisher $StorePublisher -Quad $storeQuad | Out-Null
    Good "$(Split-Path -Leaf $upload)  $([math]::Round((Get-Item $upload).Length / 1MB, 2)) MB ($($arches -join ' + '))"
    Complete-Phase 'msixupload'
}
else {
    Note 'msixupload already assembled'
}

# ===============================================================================================================
# 3  notes (the en-US "What's new" text)
# ===============================================================================================================

function Invoke-StoreNotes {
    Step 'Resolve the store release notes'
    $srcListing = Join-Path $NotesDir 'store-listing.txt'
    if (Test-Path $srcListing) {
        Copy-Item $srcListing $listingPath -Force
        Note "adopted $srcListing"
    }
    else {
        $whatsnew = Join-Path $NotesDir 'whatsnew.json'
        if (-not (Test-Path $whatsnew)) { throw "$NotesDir has neither store-listing.txt nor whatsnew.json" }
        Note "rendering from $whatsnew"
        # The listing is the tool's STDOUT; stderr carries only usage/errors and must not land in the file, so
        # this cannot go through Invoke-Native (which merges the two streams).
        $toolArgs = @('run', '--project', $releaseToolProject, '-c', 'Release', '--',
            'render', '--notes', $whatsnew, '--store-listing')
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        $out = & dotnet @toolArgs 2>$null
        $code = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($code -ne 0) { throw "Wavee.ReleaseTool render exited $code (run it by hand to see stderr: dotnet $($toolArgs -join ' '))" }
        $text = ((@($out) | ForEach-Object { "$_" }) -join "`n").TrimEnd()
        [IO.File]::WriteAllText($listingPath, $text + "`n", (New-Object System.Text.UTF8Encoding $false))
    }
    $final = Get-StoreReleaseNotesText -Path $listingPath
    Good "store-listing.txt ($($final.Length) chars, limit 1500)"
}

if (-not (Test-PhaseDone 'notes')) {
    Invoke-StoreNotes
    Complete-Phase 'notes'
}
else {
    Note 'notes already resolved'
}

$releaseNotes = Get-StoreReleaseNotesText -Path $listingPath

Step 'Staged'
Get-ChildItem $stage -File | Sort-Object Name |
    Select-Object @{ n = 'Asset'; e = { $_.Name } }, @{ n = 'Bytes'; e = { $_.Length } } |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Note "folder: $stage"

if ($DryRun) {
    Step 'Stopping before the Store'
    Write-Host ''
    Write-Host '   Everything below is what a real run would do next:' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host "   msstore publish `"$upload`" -id $ProductId --noCommit -v"
    Write-Host "   msstore submission status $ProductId                  # record the draft's submissionId"
    Write-Host "   msstore submission get $ProductId                     # saved as submission-before.json"
    Write-Host "   msstore submission update $ProductId `"<the full submission JSON with en-us BaseListing.ReleaseNotes patched, passed as ONE argument; saved as submission-after.json>`""
    Write-Host "   msstore submission publish $ProductId                 # the point of no return"
    Write-Host "   msstore submission status $ProductId                  # then poll every 30 s, up to $PollMinutes min"
    Write-Host ''
    Good "dry run complete: $stage"
    return
}

# ===============================================================================================================
# 4  draft (msstore publish --noCommit: the first mutating call of the run)
# ===============================================================================================================

function Invoke-Draft {
    Step 'Create the draft submission and upload the packages'
    # 'msstore publish' clones a new pending submission from the last published one and deletes any existing
    # pending draft first - which is why the no-pending-submission gate ran, and why nothing before this line may
    # write to the Store.
    Invoke-MsStore -Arguments @('publish', $upload, '-id', $ProductId, '--noCommit', '-v') | Out-Null

    $st = Get-CurrentSubmissionState
    if (-not $st.SubmissionId) { throw "msstore publish succeeded but 'submission status' reports no submission id; inspect with -Status before retrying" }
    $raw = Invoke-MsStore -Arguments @('submission', 'get', $ProductId)
    [IO.File]::WriteAllText((Join-Path $stage 'submission-before.json'), $raw, (New-Object System.Text.UTF8Encoding $false))

    $script:State.submissionId = "$($st.SubmissionId)"
    $script:State.packagesUploaded = $true
    Save-State
    Good "draft submission $($st.SubmissionId) holds $(Split-Path -Leaf $upload)"
    Warn 'this submission is API-owned from here on: NEVER open or edit it in Partner Center (a hand-edit desyncs the API and the next update call clobbers it)'
}

if (-not (Test-PhaseDone 'draft')) {
    Invoke-Draft
    Complete-Phase 'draft'
}
else {
    Note "draft submission $($script:State.submissionId) already created"
}

# ===============================================================================================================
# 5  metadata (en-US release notes)
# ===============================================================================================================

function Invoke-Metadata {
    Step 'Patch the en-US release notes'
    # Always patch the submission the Store holds NOW (a -Resume may land here hours later), never the
    # submission-before.json snapshot.
    $sub = ConvertFrom-MsStoreJson -Text (Invoke-MsStore -Arguments @('submission', 'get', $ProductId))
    if (-not $sub) { throw "'msstore submission get $ProductId' returned no JSON" }

    $updated = Set-StoreSubmissionReleaseNotes -Submission $sub -ReleaseNotes $releaseNotes
    [IO.File]::WriteAllText((Join-Path $stage 'submission-after.json'), $updated, (New-Object System.Text.UTF8Encoding $false))
    # The whole submission body travels as ONE argv element; Invoke-MsStore hands the array straight to the exe,
    # so no quoting layer can split it.
    Invoke-MsStore -Arguments @('submission', 'update', $ProductId, $updated) | Out-Null

    $verify = ConvertFrom-MsStoreJson -Text (Invoke-MsStore -Arguments @('submission', 'get', $ProductId))
    $listing = Get-JsonProperty (Get-JsonProperty $verify 'listings') 'en-us'
    $roundTrip = Get-JsonProperty (Get-JsonProperty $listing 'baseListing') 'releaseNotes'
    if ("$roundTrip" -ne $releaseNotes) {
        throw "the release notes did not round-trip: the Store holds $([int]"$roundTrip".Length) chars, sent $($releaseNotes.Length). The submission is untouched otherwise; fix Wavee.Store.psm1's patching against $stage\submission-before.json and -Resume"
    }
    Good 'en-US ReleaseNotes round-tripped'
}

if (-not (Test-PhaseDone 'metadata')) {
    Invoke-Metadata
    Complete-Phase 'metadata'
}
else {
    Note 'metadata already patched'
}

# ===============================================================================================================
# 6  commit
# ===============================================================================================================

if (-not (Test-PhaseDone 'commit')) {
    Step 'Commit the submission'
    Invoke-MsStore -Arguments @('submission', 'publish', $ProductId) | Out-Null
    $script:State.committed = $true
    Save-State
    Good "submission $($script:State.submissionId) committed to the Store (-Abort is refused from here on)"
    Complete-Phase 'commit'
}
else {
    Note 'submission already committed'
}

# ===============================================================================================================
# 7  poll (until the Store takes it past PreProcessing)
# ===============================================================================================================

function Invoke-Poll {
    Step "Poll the submission (every 30 s, up to $PollMinutes min)"
    $statusCmd = "powershell -File ops\release\wavee-store-submit.ps1 -Status"
    $waiting = @('CommitStarted', 'PendingCommit', 'PreProcessing')
    $deadline = [DateTime]::UtcNow.AddMinutes($PollMinutes)
    while ($true) {
        $st = Get-CurrentSubmissionState
        if ($st.Failed) {
            $errs = (@($st.Errors) | Where-Object { $_ }) -join '; '
            throw "submission $($st.SubmissionId) failed: $($st.Status)$(if ($errs) { " $EmDash $errs" }). Fix it, 'msstore submission delete $ProductId', re-run"
        }
        if ($st.Terminal -or ($st.Status -and $waiting -notcontains $st.Status)) {
            Good "submission $($st.SubmissionId) is $($st.Status)"
            Note "certification takes 1-3 business days; check later with: $statusCmd"
            return $true
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            Warn "still $($st.Status) after $PollMinutes minutes; the Store gets there on its own"
            Note "check later with: $statusCmd"
            return $false
        }
        Note "$($st.Status) $EmDash waiting"
        Start-Sleep -Seconds 30
    }
}

$polled = $true
if (-not (Test-PhaseDone 'poll')) {
    $polled = Invoke-Poll
    if ($polled) { Complete-Phase 'poll' }
}
else {
    Note 'already past PreProcessing'
}

# ===============================================================================================================
# Summary
# ===============================================================================================================

Step 'Summary'
[pscustomobject]@{
    Tag          = $tag
    Version      = "$semver $EmDash $codename"
    StoreQuad    = $storeQuad
    Commit       = $commit
    ProductId    = $ProductId
    SubmissionId = "$($script:State.submissionId)"
    Committed    = [bool]$script:State.committed
    Staging      = $stage
    Listing      = "https://apps.microsoft.com/detail/$ProductId"
} | Format-List | Out-String -Width 200 | Write-Host

if ($polled) {
    Good "Wavee $semver $EmDash $codename ($storeQuad) is submitted; the Store takes it from here."
}
else {
    Note "the submission is committed; only the poll timed out. Re-check with -Status (or -Resume to poll again)."
}
