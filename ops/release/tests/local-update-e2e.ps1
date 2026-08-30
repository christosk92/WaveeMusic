<#
.SYNOPSIS
  The fully local Wavee auto-update end-to-end test. No GitHub, no tag, no release, no gh.

.DESCRIPTION
  Packages A (an older quad) and B (a newer one) are built with -UpdateBaseUrl http://127.0.0.1:<port>/, dropped into
  a loopback feed served by ops\release\tests\LocalFeedServer.psm1, and driven through the REAL update path.

  There are TWO update paths, and they RACE each other, so the harness runs exactly one per invocation:

    -Scenario inapp  (default)  A is installed from the BARE .msix, so there is no App Installer association and
                                Windows cannot preempt anything. The app's own checker finds B, install-on-quit hands
                                the work to PackageUpdater, and APPLYING the update is what creates the association.
                                This is the only way to observe "update available" and "install-on-quit: staging".

    -Scenario os                A is installed THROUGH the .appinstaller (Add-AppxPackage -AppInstallerFile), which
                                creates the association. The template carries OnLaunch HoursBetweenUpdateChecks="0",
                                so the NEXT activation is intercepted by App Installer: the deployment engine
                                downloads and registers B before Wavee's own code runs. The app never says "update
                                available" - it opens already updated. This is what most users will experience.

  The phases:

      pack A, pack B                     both packed with -FeedRelease wavee-local -UpdateBaseUrl http://127.0.0.1:<port>/
      install A                          bare .msix (inapp) or -AppInstallerFile (os)
      launch A                           the in-app checker GETs the .appinstaller (User-Agent "Wavee/...")
      publish B into the same feed       same file name, higher root Version, MainPackage/@Uri -> B
      drive                              inapp: install-on-quit (or About > Update now); os: just relaunch
      Windows downloads + installs B     the DEPLOYMENT engine GETs the .msix over loopback, in Range slices
      relaunch                           "updated: <A> -> <B>", the after-update plate, previousVersion == A

  The loopback listener's request log is the evidence for the two claims that a package version alone cannot prove:
  that the IN-APP checker fetched the feed, and that the DEPLOYMENT ENGINE (a different User-Agent) downloaded the
  package. Nothing here is a production branch: http/https is all the app's shared HTTP client speaks, so serving the
  feed on 127.0.0.1 exercises exactly the code that ships.

  Every phase is recorded into a PASS / WARN / FAIL table printed at the end; the exit code is the FAIL count. The
  finally block always stops the app, copies the logs, stops the listener and unloads the settings hive.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1

.EXAMPLE
  # The OS silent path: App Installer applies B at the next activation, before Wavee's code runs.
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1 -Scenario os

.EXAMPLE
  # Iterate without repacking (both msix already in artifacts\local-e2e\A and ...\B)
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1 -SkipPackA -SkipPackB -KeepFeed

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1 -Driver ui -Drill snooze
#>
#requires -Version 5.1 -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64')]
    [string]$Arch = $(
        $a = $env:PROCESSOR_ARCHITEW6432
        if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
        if ("$a" -match 'ARM64') { 'arm64' } else { 'x64' }),
    [int]$Port = 8099,
    # A throwaway feed name: the packages are stamped with it, so they poll it and nothing else.
    [string]$FeedRelease = 'wavee-local',
    [string]$QuadA = '0.2.0.9001',
    [string]$QuadB = '0.2.0.9002',
    [string]$SemverA = '0.2.0',
    [string]$SemverB = '0.2.0',
    [string]$Codename = 'Breaker',
    # Release-notes folders embedded into A and B (default ops\release\wavee\<semver>). Point -NotesB at a synthetic
    # 0.2.1 folder to exercise stacked notes in the after-update plate.
    [string]$NotesA = '',
    [string]$NotesB = '',
    # Which of the two update paths to exercise. They race, so exactly one runs per invocation. See the header.
    [ValidateSet('inapp', 'os')]
    [string]$Scenario = 'inapp',
    [ValidateSet('quit', 'ui')]
    [string]$Driver = 'quit',
    # Drills are variations on the IN-APP path; -Scenario os ignores them (the app is not the actor there).
    [ValidateSet('none', 'snooze', 'network', 'downgrade')]
    [string]$Drill = 'none',
    [string]$FeedDir = 'C:\wavee-feed',
    [string]$OutDir = 'artifacts\local-e2e',
    [switch]$KeepFeed,
    [switch]$SkipPackA,
    [switch]$SkipPackB,
    [switch]$RemoveCert,
    [switch]$NoAot,
    [string]$Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL',
    [string]$IdentityName = 'cproducts.Wavee',
    [ValidateSet('shell', 'explorer')]
    [string]$LaunchVia = 'shell',
    [int]$CheckTimeoutSec = 120,
    [int]$ApplyTimeoutSec = 600,
    # How long to wait for a Wavee process after activating it. Generous by default: on the OS path the activation
    # itself downloads and registers the new package first, and nothing appears until that is done.
    [int]$LaunchTimeoutSec = 120,
    # Skip the Wavee.ReleaseTool pass and embed / publish the AUTHORED notes folders instead.
    [switch]$NoNotes,
    # Where the run's transcript goes (everything the console shows, including the pack output). Default:
    # artifacts\local-e2e-<Scenario>.log under the repo root. The harness writes this ITSELF so the console stays
    # live - redirecting the whole run with *> left an elevated window showing nothing but Remove-AppxPackage's
    # last progress bar for the eight minutes the NativeAOT packs take, which reads as "stuck".
    [string]$LogPath = ''
)
$ErrorActionPreference = 'Stop'
# Add-/Remove-AppxPackage draw a console progress bar that OUTLIVES the operation when the next thing to print takes
# minutes (a NativeAOT publish). It carries no information the event rows do not, and it has been read as a hang
# more than once. Off for the whole run.
$ProgressPreference = 'SilentlyContinue'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path
Import-Module (Join-Path $repoRoot 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $repoRoot 'ops\release\Wavee.Release.psm1') -Force -DisableNameChecking -Global
Import-Module (Join-Path $here 'LocalFeedServer.psm1') -Force -DisableNameChecking

if (-not $NotesA) { $NotesA = Join-Path $repoRoot ('ops\release\wavee\' + $SemverA) }
if (-not $NotesB) { $NotesB = Join-Path $repoRoot ('ops\release\wavee\' + $SemverB) }

$script:FeedDirFull = $FeedDir
if (-not [IO.Path]::IsPathRooted($script:FeedDirFull)) { $script:FeedDirFull = Join-Path $repoRoot $FeedDir }
$script:OutDirFull = $OutDir
if (-not [IO.Path]::IsPathRooted($script:OutDirFull)) { $script:OutDirFull = Join-Path $repoRoot $OutDir }

if (-not $LogPath) { $LogPath = Join-Path $repoRoot ('artifacts\local-e2e-' + $Scenario + '.log') }
elseif (-not [IO.Path]::IsPathRooted($LogPath)) { $LogPath = Join-Path $repoRoot $LogPath }
$logDir = Split-Path -Parent $LogPath
if ($logDir -and -not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
try { Start-Transcript -Path $LogPath -Force | Out-Null; Write-Host ("transcript: $LogPath") }
catch { Write-Host ("note: no transcript ($($_.Exception.Message)); console only") }

$script:BaseUrl = 'http://127.0.0.1:' + $Port + '/'
$script:FeedUri = $script:BaseUrl + $FeedRelease + '/Wavee.' + $Arch + '.appinstaller'
$script:FeedLog = Join-Path $script:FeedDirFull 'feed-requests.log'
$script:Server = $null
$script:HiveMounted = $false
$script:SettingsRoot = ''
$script:MsixA = ''
$script:MsixB = ''
$script:BaselineLuma = -1
$script:PackScript = Join-Path $repoRoot 'ops\build\pack-wavee-msix.ps1'
$script:Template = Join-Path $repoRoot 'ops\build\Wavee.AppInstaller.template.xml'
$script:HiveName = 'WaveeE2E'
$script:Tab = [string][char]9
$script:ReleaseTool = Join-Path $repoRoot 'src\apps\Wavee.ReleaseTool'
$script:Changelog = Join-Path $repoRoot 'CHANGELOG.md'
# The EMITTED notes folders (Wavee.ReleaseTool output), or the authored ones when the tool could not run. These are
# what gets embedded into the packages AND what gets published into the feed, so the app reads exactly what a real
# release would serve it: a dated whatsnew.json with a packageVersion and the CHANGELOG sections folded in.
$script:NotesOutA = ''
$script:NotesOutB = ''
$script:IndexA = ''
$script:IndexB = ''
# The ONE log mark the update is measured from. Taken in P9, immediately before the launch that triggers the update,
# and read again in P10/P11 - never re-taken, because the lines that prove the update (the "updated:" line above all)
# are written by a process that may have come and gone before the next phase starts.
$script:UpdateMark = $null
# The WALL CLOCK of that same mark, and what the app's own storage looked like at it. The event logs and the feed
# request log are keyed by time, not by byte offset, so every "since the update started" query needs this; the two
# snapshots are what P10 compares against to prove the update did not throw the app's data away.
$script:UpdateMarkTime = [DateTime]::MinValue
$script:LogSnapAtMark = @()
$script:HeliumAtMark = ''

# Drills describe the app's own update path. On the OS path App Installer is the actor, so there is nothing for them
# to vary; silently running one there would report on a code path the run never touched.
if ($Scenario -eq 'os' -and $Drill -ne 'none') {
    Write-Warning ("-Drill $Drill is an in-app drill; -Scenario os ignores it.")
    $Drill = 'none'
}

# ===================================================================================================================
# Results table
# ===================================================================================================================

$script:Results = New-Object System.Collections.ArrayList

function Record {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Name,
        [ValidateSet('PASS', 'WARN', 'FAIL', 'SKIP', 'INFO')][string]$Status = 'PASS',
        [string]$Detail = '')

    [void]$script:Results.Add([pscustomobject]@{ Phase = $Phase; Check = $Name; Status = $Status; Detail = $Detail })
    $color = 'Gray'
    if ($Status -eq 'PASS') { $color = 'Green' }
    if ($Status -eq 'WARN') { $color = 'Yellow' }
    if ($Status -eq 'FAIL') { $color = 'Red' }
    $suffix = ''
    if ($Detail) { $suffix = '  - ' + $Detail }
    Write-Host ("    [{0}] {1}{2}" -f $Status, $Name, $suffix) -ForegroundColor $color
}

function Assert-True {
    <#  Records PASS/FAIL (or PASS/WARN with -Soft) and RETURNS the condition so a phase can branch on it. #>
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Name,
        [bool]$Condition,
        [string]$Detail = '',
        [switch]$Soft)

    $status = 'PASS'
    if (-not $Condition) {
        $status = 'FAIL'
        if ($Soft) { $status = 'WARN' }
    }
    Record -Phase $Phase -Name $Name -Status $status -Detail $Detail
    $Condition
}

function Invoke-Phase {
    <#  A phase never aborts the run: it records its own failure and the next phase decides what that means. #>
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][scriptblock]$Body)

    Write-Host ''
    Write-Host ("== $Phase  $Title") -ForegroundColor Cyan
    try { & $Body }
    catch {
        Record -Phase $Phase -Name $Title -Status 'FAIL' -Detail "$($_.Exception.Message)"
    }
}

# ===================================================================================================================
# Package + process
# ===================================================================================================================

function Get-WaveePackages {
    <#
      EVERY registered Wavee package for this user, freshly queried. The plural is the point: while a deferred
      update settles, Windows keeps the OUTGOING version registered until the last process of the package exits,
      so "the installed version" is a set, not a value, for the whole length of an apply.
    #>
    @(Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue)
}

function Get-WaveePackagesAllUsers {
    <#
      The same query with -AllUsers, which needs elevation (the harness has it) and additionally lists packages
      that are STAGED but not yet registered for the user - the state B sits in between "Windows accepted the
      update" and "the old process finally exited". Empty, never an error, when the query is refused.
    #>
    try { return @(Get-AppxPackage -Name $IdentityName -AllUsers -ErrorAction SilentlyContinue) }
    catch { return @() }
}

function Get-WaveeVersionsNow {
    <#
      Every Wavee version Windows can currently see, per-user AND (elevated) all-users, as normalized quad strings
      plus a one-line description for the results table.

      Returns @{ User; All; Detail } - User is what a user's own session would see, All is the union.
    #>
    $user = @()
    foreach ($p in (Get-WaveePackages)) {
        $q = ConvertTo-QuadString "$($p.Version)"
        if ($q) { $user += $q }
    }
    $allUsers = @()
    $union = @()
    foreach ($q in $user) { if (-not ($union -contains $q)) { $union += $q } }
    foreach ($p in (Get-WaveePackagesAllUsers)) {
        $q = ConvertTo-QuadString "$($p.Version)"
        if (-not $q) { continue }
        $st = "$($p.Status)".Trim()
        $tag = $q
        if ($st.Length -gt 0 -and $st -ne 'Ok') { $tag = $q + '[' + $st + ']' }
        $allUsers += $tag
        if (-not ($union -contains $q)) { $union += $q }
    }
    $detail = 'per-user: '
    if ($user.Count -gt 0) { $detail = $detail + ($user -join ', ') } else { $detail = $detail + '(none)' }
    $detail = $detail + '; all-users: '
    if ($allUsers.Count -gt 0) { $detail = $detail + ($allUsers -join ', ') } else { $detail = $detail + '(none)' }
    [pscustomobject]@{ User = @($user); All = @($union); Detail = $detail }
}

function Get-WaveePackage {
    <#
      The NEWEST registered package, not the first one Windows happens to enumerate. Enumeration order is not a
      contract, and during an apply the set holds both the outgoing and the incoming version.
    #>
    $best = $null
    $bestV = $null
    foreach ($p in (Get-WaveePackages)) {
        $v = $null
        try { $v = [Version](ConvertTo-QuadString "$($p.Version)") } catch { $v = $null }
        if ($null -eq $v) { if ($null -eq $best) { $best = $p }; continue }
        if ($null -eq $bestV -or $v -gt $bestV) { $bestV = $v; $best = $p }
    }
    $best
}

function Get-WaveeQuad {
    $p = Get-WaveePackage
    if ($null -eq $p) { return '' }
    "$($p.Version)"
}

function Get-WaveePfn {
    $p = Get-WaveePackage
    if ($null -eq $p) { return '' }
    "$($p.PackageFamilyName)"
}

function Get-WaveeProcess {
    Get-Process -Name 'Wavee' -ErrorAction SilentlyContinue
}

function Get-AutoUpdateSettings {
    <#
      The App Installer association AS THE CMDLET SEES IT - one of the three proofs, never the verdict on its own.
      Use Get-AssociationEvidence.

      Get-AppxPackageAutoUpdateSettings is not on every Windows build, and "the cmdlet is missing" and "there is no
      association" both come back as $null - so a bare-install assertion written against $null passes on a machine
      that could not have answered. Supported says which of the two it is.

      BOTH selector forms are tried. Run 3 had this cmdlet answer "no association" by -Name for a package the
      AppXDeploymentServer log showed being updated through its .appinstaller on every single launch; the
      -PackageFullName form is the one the deployment engine's own state is keyed by, so it is asked first.

      Returns @{ Supported; Associated; Uri; Probe; Detail }.
    #>
    $cmd = Get-Command 'Get-AppxPackageAutoUpdateSettings' -ErrorAction SilentlyContinue
    if (-not $cmd) {
        return [pscustomobject]@{ Supported = $false; Associated = $false; Uri = ''; Probe = 'none'
            Detail = 'Get-AppxPackageAutoUpdateSettings is not available on this Windows build' }
    }

    $tried = @()
    $pkg = Get-WaveePackage
    $pfn = ''
    if ($null -ne $pkg) { $pfn = "$($pkg.PackageFullName)" }

    if ($pfn) {
        $auto = $null
        try { $auto = Get-AppxPackageAutoUpdateSettings -PackageFullName $pfn -ErrorAction SilentlyContinue } catch { $auto = $null }
        $uri = ''
        if ($null -ne $auto) { $uri = "$($auto.AppInstallerUri)" }
        if ($uri) {
            return [pscustomobject]@{ Supported = $true; Associated = $true; Uri = $uri; Probe = '-PackageFullName'
                Detail = ('-PackageFullName ' + $pfn + ' -> ' + $uri) }
        }
        $tried += ('-PackageFullName ' + $pfn + ' -> none')
    }
    else {
        $tried += '-PackageFullName (no package installed)'
    }

    $auto = $null
    try { $auto = Get-AppxPackageAutoUpdateSettings -Name $IdentityName -ErrorAction SilentlyContinue } catch { $auto = $null }
    $uri = ''
    if ($null -ne $auto) { $uri = "$($auto.AppInstallerUri)" }
    if ($uri) {
        return [pscustomobject]@{ Supported = $true; Associated = $true; Uri = $uri; Probe = '-Name'
            Detail = ('-Name ' + $IdentityName + ' -> ' + $uri) }
    }
    $tried += ('-Name ' + $IdentityName + ' -> none')

    [pscustomobject]@{ Supported = $true; Associated = $false; Uri = ''; Probe = 'both'
        Detail = ('no association (' + ($tried -join '; ') + ')') }
}

function Get-AssociationEvidence {
    <#
    .SYNOPSIS
      Is this package associated with the .appinstaller? Three INDEPENDENT proofs; ANY one of them is enough.
    .DESCRIPTION
      1. cmdlet     Get-AppxPackageAutoUpdateSettings, both selector forms (Get-AutoUpdateSettings).
      2. feed log   an "App Virt Client" GET of the feed document after $Since - the deployment engine fetching the
                    .appinstaller, which it only ever does for a package that is associated with one.
      3. event log  AppXDeploymentServer/Operational id 603 for this package family after $Since, AND ONLY where the
                    message names UpdateUsingAppInstallerOperation. A 603 is written for every deployment operation
                    (a bare Add-AppxPackage writes AddPackageOperation), so an unfiltered 603 count is not evidence
                    of anything - it made P6 "prove" an association from the bare install it had just performed.

      Proof 1 has been observed answering "no" while 2 and 3 both said yes on the same launch, which is exactly why
      the verdict is an OR and the detail names WHICH proofs held. The caller records the two that did not as INFO
      rows rather than losing them.
    .OUTPUTS
      pscustomobject @{ Associated; Proofs; Cmdlet; FeedHits; EventHits; Detail }
    #>
    param([Parameter(Mandatory = $true)][datetime]$Since)

    $auto = Get-AutoUpdateSettings

    $feedHits = @()
    try {
        $feedHits = @(Get-FeedAssociationRequests -Rows (Get-FeedRequests) -Mark $Since -FeedPath $script:FeedUri)
    }
    catch { $feedHits = @() }

    $evHits = @()
    # -OperationContains is load-bearing, not decoration. Without it this counted ANY 603, and a 603 is written for
    # EVERY deployment operation - including the bare Add-AppxPackage of A that P6 performs precisely to prove there
    # is NO association. P6 duly failed with "proved by event-603: 1 AppXDeploymentServer" against its own install.
    try { $evHits = @(Get-AppxDeploymentEvents -Since $Since -Ids @(603) -OperationContains 'UpdateUsingAppInstallerOperation') }
    catch { $evHits = @() }

    $proofs = @()
    if ($auto.Associated) { $proofs += 'cmdlet' }
    if ($feedHits.Count -gt 0) { $proofs += 'feed-log' }
    # The 603 is CORROBORATION, never proof on its own: Windows runs an UpdateUsingAppInstallerOperation check right
    # after a bare Add-AppxPackage as well (observed at P6 with zero feed GETs - the check found nothing to fetch), so
    # only an event that is accompanied by the wire fetch, or by the cmdlet, counts.
    if ($evHits.Count -gt 0 -and $proofs.Count -gt 0) { $proofs += 'event-603' }

    $detail = 'no proof: ' + $auto.Detail + '; 0 App Virt Client GET(s) of ' + (ConvertTo-FeedPath $script:FeedUri) +
              '; 0 AppXDeploymentServer UpdateUsingAppInstallerOperation (603) event(s)'
    if ($proofs.Count -gt 0) {
        $bits = @()
        if ($auto.Associated) { $bits += ('cmdlet ' + $auto.Probe + ' ' + $auto.Uri) }
        if ($feedHits.Count -gt 0) { $bits += ('' + $feedHits.Count + ' App Virt Client GET(s) of ' + (ConvertTo-FeedPath $script:FeedUri)) }
        if ($evHits.Count -gt 0) { $bits += ('' + $evHits.Count + ' AppXDeploymentServer UpdateUsingAppInstallerOperation (603) event(s)') }
        $detail = 'proved by ' + ($proofs -join ' + ') + ': ' + ($bits -join '; ')
    }

    [pscustomobject]@{
        Associated = ($proofs.Count -gt 0)
        Proofs     = @($proofs)
        Cmdlet     = $auto
        FeedHits   = @($feedHits)
        EventHits  = @($evHits)
        Detail     = $detail
    }
}

# ===================================================================================================================
# Windows event logs - the evidence the app's own log cannot carry (it is not running when WER kills it)
# ===================================================================================================================

function Format-EventLine {
    <#  time + id + the first 160 characters of the message, flattened onto one line for the results table. #>
    param($Event, [int]$MaxChars = 160)
    if ($null -eq $Event) { return '' }
    $msg = ''
    try { $msg = "$($Event.Message)" } catch { $msg = '' }
    $msg = $msg -replace '[\r\n\t]+', ' '
    $msg = $msg -replace '\s{2,}', ' '
    $msg = $msg.Trim()
    if ($msg.Length -gt $MaxChars) { $msg = $msg.Substring(0, $MaxChars) + '...' }
    $when = ''
    try { $when = $Event.TimeCreated.ToString('HH:mm:ss') } catch { $when = '?' }
    ($when + ' id ' + $Event.Id + ' ' + $msg)
}

function Get-AppxDeploymentEvents {
    <#
      Microsoft-Windows-AppXDeploymentServer/Operational, filtered to this package family and this time window.

      603 = "Started deployment <Operation> operation on package <full name>" - for ANY operation, which is the
      trap: a bare Add-AppxPackage writes a 603 (AddPackageOperation) exactly like an auto-update does
      (UpdateUsingAppInstallerOperation). Pass -OperationContains to keep only the operation that proves what you
      are asserting; leave it empty to see every deployment operation for this package. 400/401/404 are the
      begin / end / failure records of a deployment operation. Get-WinEvent answers a non-terminating "no events
      were found" when the filter matches nothing, so everything here is SilentlyContinue and an empty result is a
      fact, not a failure. Needs elevation to read the log; the harness has it.
    #>
    param(
        [Parameter(Mandatory = $true)][datetime]$Since,
        [int[]]$Ids = @(603, 400, 401, 404),
        [string]$Match = '',
        [AllowEmptyString()][string]$OperationContains = '')

    if (-not $Match) { $Match = $IdentityName }
    $events = @()
    try {
        $filter = @{ LogName = 'Microsoft-Windows-AppXDeploymentServer/Operational'; StartTime = $Since }
        if ($Ids -and $Ids.Count -gt 0) { $filter['Id'] = $Ids }
        $events = @(Get-WinEvent -FilterHashtable $filter -ErrorAction SilentlyContinue)
    }
    catch { $events = @() }

    $out = @()
    foreach ($e in $events) {
        $msg = ''
        try { $msg = "$($e.Message)" } catch { $msg = '' }
        # ONE pure predicate for both filters (LocalFeedServer.psm1, Pester-covered): the package must be named, and
        # - when asked - the operation too.
        if (-not (Test-AppInstallerUpdateEvent -Message $msg -PackageMatch $Match -OperationContains $OperationContains)) { continue }
        $out += $e
    }
    ,@($out)
}

function Get-WaveeFaultEvents {
    <#
      Application-log crash / hang / WER records that mention Wavee, in one window.

      "Application Hang" is the one that matters here: a quit-time apply that blocks the UI thread stops the
      message pump, Windows declares the window unresponsive, and WER kills the process with MoAppHang + event
      1002 - which looks, from the package's point of view, exactly like a clean exit that never finished the
      update. Nothing in the app's own log can record that; only this can.
    #>
    param([Parameter(Mandatory = $true)][datetime]$Since, [string]$Match = 'Wavee')

    $events = @()
    try {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName      = 'Application'
            ProviderName = @('Application Hang', 'Application Error', 'Windows Error Reporting')
            StartTime    = $Since
        } -ErrorAction SilentlyContinue)
    }
    catch { $events = @() }

    $out = @()
    foreach ($e in $events) {
        $msg = ''
        try { $msg = "$($e.Message)" } catch { $msg = '' }
        if ($Match -and ($msg.IndexOf($Match, [StringComparison]::OrdinalIgnoreCase) -lt 0)) { continue }
        $out += $e
    }
    ,@($out)
}

function Record-EventRows {
    <#  One INFO row per event, capped so a noisy machine cannot bury the table. Returns the events it was given. #>
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Label,
        [AllowNull()][object[]]$Events,
        [int]$Max = 8)

    $all = @($Events | Where-Object { $null -ne $_ })
    if ($all.Count -eq 0) {
        Record -Phase $Phase -Name $Label -Status 'INFO' -Detail 'none'
        return $all
    }
    $i = 0
    foreach ($e in $all) {
        if ($i -ge $Max) {
            Record -Phase $Phase -Name $Label -Status 'INFO' -Detail ('' + ($all.Count - $Max) + ' more not listed')
            break
        }
        $who = ''
        try { $who = "$($e.ProviderName)" } catch { $who = '' }
        Record -Phase $Phase -Name $Label -Status 'INFO' -Detail ($who + ' ' + (Format-EventLine $e))
        $i++
    }
    $all
}

function Get-ProcessDetail {
    <#
      Who this process is and who started it. The parent is the interesting half: a Wavee started by explorer.exe
      is a user activation, one started by svchost.exe is Windows relaunching it after an update, and one started
      by WerFault.exe is a restart after a crash - three completely different stories that a bare pid cannot tell
      apart. Win32_Process is the only place the parent and the command line live.
    #>
    param($Process)

    if ($null -eq $Process) { return '(no process)' }
    $procId = 0
    try { $procId = [int]$Process.Id } catch { $procId = 0 }
    $started = '?'
    try { $started = $Process.StartTime.ToString('HH:mm:ss.fff') } catch { $started = '?' }

    $ppid = '?'
    $parentName = '?'
    $cmd = ''
    try {
        $ci = @(Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = " + $procId) -ErrorAction SilentlyContinue) |
              Select-Object -First 1
        if ($null -ne $ci) {
            $ppid = "$($ci.ParentProcessId)"
            $cmd = "$($ci.CommandLine)"
            $par = @(Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = " + $ci.ParentProcessId) -ErrorAction SilentlyContinue) |
                   Select-Object -First 1
            if ($null -ne $par) { $parentName = "$($par.Name)" }
        }
    }
    catch { }

    $cmd = ($cmd -replace '[\r\n\t]+', ' ').Trim()
    if ($cmd.Length -gt 220) { $cmd = $cmd.Substring(0, 220) + '...' }
    if ($cmd.Length -eq 0) { $cmd = '(command line unavailable)' }

    ('pid ' + $procId + ' started ' + $started + ' parent ' + $ppid + ' (' + $parentName + ') cmd ' + $cmd)
}

function Stop-WaveeHard {
    param([int]$TimeoutSec = 20)
    foreach ($p in @(Get-WaveeProcess)) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    Wait-WaveeExit -TimeoutSec $TimeoutSec | Out-Null
    foreach ($p in @(Get-WaveeProcess)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    # Every process that runs FROM the package folder holds the package: the playback modules (Wavee.Module.*.exe)
    # are package-identity children that outlive a killed Wavee.exe, and Remove-AppxPackage then sits in
    # "Deployment operation progress ... Processing" until they are gone. Match on the image path, not the name.
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        $path = ''
        try { $path = "$($p.Path)" } catch { $path = '' }
        if ($path -like ('*\WindowsApps\' + $IdentityName + '_*')) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    Wait-WaveeExit -TimeoutSec 10 | Out-Null
}

function Wait-WaveeExit {
    param([int]$TimeoutSec = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-WaveeProcess)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    -not (Get-WaveeProcess)
}

function Start-Wavee {
    <#
      A packaged full-trust app declares no allowElevation, so activating it from this elevated shell still starts it
      at medium integrity - the same token a user's double-click gives it. -LaunchVia explorer goes through
      explorer.exe instead, which is the fallback when shell: activation is blocked by policy.

      A process that is ALREADY running is adopted, not an error: after an update Windows relaunches the app itself
      (RegisterApplicationRestart), and a harness that insisted on starting its own would either fail or open a
      second window. -Phase records that adoption so the table says which of the two happened.
    #>
    param([int]$TimeoutSec = 0, [string]$Phase = '')

    if ($TimeoutSec -le 0) { $TimeoutSec = $LaunchTimeoutSec }

    $existing = Get-WaveeProcess | Select-Object -First 1
    if ($existing) {
        if ($Phase) {
            Record -Phase $Phase -Name 'adopted the Wavee process already running' -Status 'INFO' `
                -Detail (Get-ProcessDetail $existing)
        }
        return $existing
    }

    $pfn = Get-WaveePfn
    if (-not $pfn) { throw 'Wavee is not installed; cannot launch it.' }
    $aumid = $pfn + '!Wavee'
    if ($LaunchVia -eq 'explorer') {
        Invoke-Native 'explorer.exe' @('shell:AppsFolder\' + $aumid) -AllowFailure | Out-Null
    }
    else {
        Start-Process ('shell:AppsFolder\' + $aumid)
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Get-WaveeProcess
        if ($p) {
            $first = $p | Select-Object -First 1
            if ($Phase) {
                # Give the process a moment to be fully created before asking Win32_Process for its command line;
                # a process caught in its first milliseconds can answer with a null CommandLine.
                Start-Sleep -Milliseconds 300
                Record -Phase $Phase -Name 'launched Wavee' -Status 'INFO' -Detail (Get-ProcessDetail $first)
            }
            return $first
        }
        Start-Sleep -Milliseconds 400
    }
    $null
}

function Wait-WaveeUptime {
    <#
      RegisterApplicationRestart will not relaunch a process that has been alive for less than about 60 seconds, and
      the update path relies on that relaunch. Hold here until the running instance clears the floor.
    #>
    param([int]$Seconds = 65)
    $p = Get-WaveeProcess | Select-Object -First 1
    if ($null -eq $p) { return }
    $started = $p.StartTime
    while ($true) {
        $up = ([DateTime]::Now - $started).TotalSeconds
        if ($up -ge $Seconds) { return }
        Start-Sleep -Seconds 2
        if (-not (Get-WaveeProcess)) { return }
    }
}

# ===================================================================================================================
# Logs
# ===================================================================================================================

function Get-WaveeLogDirs {
    <#  Packaged writes into the package container; an unpackaged build writes into the real LocalAppData. #>
    $dirs = @()
    $pfn = Get-WaveePfn
    if ($pfn) { $dirs += (Join-Path $env:LOCALAPPDATA ('Packages\' + $pfn + '\LocalCache\Local\Wavee\logs')) }
    $dirs += (Join-Path $env:LOCALAPPDATA 'Wavee\logs')
    $dirs
}

function Get-WaveeLogFiles {
    $files = @()
    foreach ($d in (Get-WaveeLogDirs)) {
        if (Test-Path -LiteralPath $d) {
            $files += @(Get-ChildItem -LiteralPath $d -Filter 'wavee*.log' -ErrorAction SilentlyContinue)
        }
    }
    $files
}

function Read-SharedText {
    <#  The app holds its log open for append; a plain Get-Content can lose the race. FileShare.ReadWrite cannot. #>
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $sr = New-Object IO.StreamReader($fs)
            $t = $sr.ReadToEnd()
            $sr.Dispose()
            return $t
        }
        finally { try { $fs.Dispose() } catch { } }
    }
    catch { return '' }
}

function Get-LogMark {
    <#
      Where every log file ends right now, so a later wait only ever reads lines this run produced.

      Keyed by FULL PATH, never by file name. Two directories are searched - the package container's LocalCache and
      the real %LOCALAPPDATA% - and they hold identically named files (wavee-<date>.log). Keyed by name, a mark
      taken on the container's copy is satisfied by the unpackaged copy sitting at a different length, and the wait
      then reads either the wrong bytes or none at all.
    #>
    $m = @{}
    foreach ($f in (Get-WaveeLogFiles)) { $m[$f.FullName] = (Read-SharedText $f.FullName).Length }
    $m
}

function Get-MarkOffset {
    <#  The marked length of ONE full path, 0 when it was not marked. The single place a mark is ever looked up. #>
    param($Mark, [Parameter(Mandatory = $true)][string]$FullPath)
    if ($null -eq $Mark) { return 0 }
    if (-not $Mark.ContainsKey($FullPath)) { return 0 }
    [int]$Mark[$FullPath]
}

function Format-LogMark {
    <#
      "which file, from which byte, and how big is it now" - the answer to the only question a failed wait raises.
      A wait that times out is almost never "the app did not log it"; it is "the harness looked in the wrong place,
      or from too late an offset", or "the file the mark was taken on is not there any more".

      EVERY file is listed by FULL PATH, including files that were marked and have since VANISHED (mark=... now=GONE)
      - which is how the reset of the package LocalCache log directory was finally visible instead of merely fatal.
    #>
    param($Mark)
    $parts = @()
    $seen = @{}
    foreach ($f in (Get-WaveeLogFiles)) {
        $full = $f.FullName
        $seen[$full] = $true
        $off = Get-MarkOffset $Mark $full
        $now = (Read-SharedText $full).Length
        $parts += ($full + ' mark=' + $off + ' now=' + $now)
    }
    if ($null -ne $Mark) {
        foreach ($k in @($Mark.Keys)) {
            if ($seen.ContainsKey($k)) { continue }
            $parts += ("$k" + ' mark=' + (Get-MarkOffset $Mark "$k") + ' now=GONE')
        }
    }
    if ($parts.Count -eq 0) { return '(no wavee*.log under ' + ((Get-WaveeLogDirs) -join ' or ') + ')' }
    $parts -join ' ; '
}

function Get-LogTextSince {
    param($Mark)
    $sb = New-Object Text.StringBuilder
    foreach ($f in (Get-WaveeLogFiles)) {
        $t = Read-SharedText $f.FullName
        $off = Get-MarkOffset $Mark $f.FullName
        if ($off -gt $t.Length) { $off = 0 }   # the file rolled, or was re-created, since the mark
        [void]$sb.AppendLine($t.Substring($off))
    }
    $sb.ToString()
}

function Wait-LogLine {
    <#
    .SYNOPSIS
      Wait for $Pattern to appear in the app log after $Mark, or for $FailPattern to appear first.
    .DESCRIPTION
      -Mark is MANDATORY and is never taken here. A wait that marks the log itself cannot see a line the app wrote
      between the action and the wait - which is exactly how a run can log "updated: A -> B" and still report that it
      never happened. The phase takes ONE mark before the action that should produce the line, and every wait for
      that action reads from it.
    .OUTPUTS
      pscustomobject @{ Ok; Failed; Line; Text; Where }
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][AllowNull()]$Mark,
        [int]$TimeoutSec = 90,
        [string]$FailPattern = '')

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $text = Get-LogTextSince $Mark
        if ($FailPattern) {
            $fm = [regex]::Match($text, $FailPattern)
            if ($fm.Success) {
                return [pscustomobject]@{ Ok = $false; Failed = $true; Line = $fm.Value; Text = $text; Where = (Format-LogMark $Mark) }
            }
        }
        $hit = [regex]::Match($text, $Pattern)
        if ($hit.Success) {
            return [pscustomobject]@{ Ok = $true; Failed = $false; Line = $hit.Value; Text = $text; Where = (Format-LogMark $Mark) }
        }
        if ((Get-Date) -ge $deadline) {
            return [pscustomobject]@{ Ok = $false; Failed = $false; Line = ''; Text = $text; Where = (Format-LogMark $Mark) }
        }
        Start-Sleep -Milliseconds 500
    }
}

function Get-WaitDetail {
    <#
      The Detail column for a Wait-LogLine result: the matched line, or - for EVERY failure, the fail-pattern one
      included - where the harness looked. .Where is the full-path listing from Format-LogMark: every file scanned,
      its size at the mark and its size now. A failure detail without that is unactionable.
    #>
    param($Hit, [string]$Pattern = '')
    if ($null -eq $Hit) { return '' }
    if ($Hit.Ok) { return "$($Hit.Line)" }
    if ($Hit.Failed) { return ('fail pattern matched: ' + $Hit.Line + '; searched ' + $Hit.Where) }
    $d = 'no match'
    if ($Pattern) { $d = $d + " for /$Pattern/" }
    $d + '; searched ' + $Hit.Where
}

function Test-LogAbsent {
    <#  True when $Pattern is NOT in the log after $Mark. Evidence of a path NOT taken (the OS scenario needs it). #>
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][AllowNull()]$Mark)
    $m = [regex]::Match((Get-LogTextSince $Mark), $Pattern)
    [pscustomobject]@{ Absent = (-not $m.Success); Line = $m.Value; Where = (Format-LogMark $Mark) }
}

function Copy-WaveeLog {
    <#
      Remove-AppxPackage deletes LocalCache with the logs in it, so every log is copied out before that happens.

      The copy carries WHICH DIRECTORY it came from in its name. Both searched directories hold identically named
      files, and a destination keyed on the bare name silently overwrote one with the other - so the artefact that
      survived a run was whichever of the two the enumeration happened to reach last.
    #>
    param([Parameter(Mandatory = $true)][string]$Tag)
    $n = 0
    foreach ($f in (Get-WaveeLogFiles)) {
        $where = 'local'
        if ("$($f.FullName)" -like '*\Packages\*') { $where = 'pkg' }
        $dst = Join-Path $script:OutDirFull ('wavee-' + $Tag + '-' + $where + '-' + $f.Name)
        try {
            [IO.File]::WriteAllText($dst, (Read-SharedText $f.FullName), (New-Object Text.UTF8Encoding $false))
            $n++
        }
        catch { }
    }
    $n
}

function Get-PackageLogDir {
    <#  The package container's log directory, or '' when nothing is installed. #>
    $pfn = Get-WaveePfn
    if (-not $pfn) { return '' }
    Join-Path $env:LOCALAPPDATA ('Packages\' + $pfn + '\LocalCache\Local\Wavee\logs')
}

function Get-FileSnapshot {
    <#
      Every file in one directory as {Path; Name; Length}, for Compare-LogSnapshot. An empty array when the
      directory is not there - which is itself the answer when the question is "did the update keep the app data".
    #>
    param([string]$Dir)
    $rows = @()
    if (-not $Dir) { return ,$rows }
    if (-not (Test-Path -LiteralPath $Dir)) { return ,$rows }
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -File -ErrorAction SilentlyContinue)) {
        $rows += [pscustomobject]@{ Path = $f.FullName; Name = $f.Name; Length = [long]$f.Length }
    }
    ,$rows
}

function Format-FileSnapshot {
    <#  "name (bytes)" per file, so two snapshots can be eyeballed against each other in the results table. #>
    param([AllowNull()][object[]]$Rows, [string]$Dir = '')
    $list = @($Rows | Where-Object { $null -ne $_ })
    $head = ''
    if ($Dir) { $head = $Dir + ' : ' }
    if ($list.Count -eq 0) { return ($head + '(no files)') }
    $parts = @()
    foreach ($r in $list) { $parts += ("$($r.Name)" + ' (' + [long]$r.Length + ')') }
    $head + ($parts -join ', ')
}

function Get-HeliumStamp {
    <#
      The package container's registry hive (User.dat) - when it was last written, and how big it is. Every setting
      the app persists lands there, so a hive whose mtime jumps back or whose size collapses across the update is
      the app's settings being thrown away, which no log line reports.
    #>
    $pfn = Get-WaveePfn
    if (-not $pfn) { return '(no package installed)' }
    $dat = Join-Path $env:LOCALAPPDATA ('Packages\' + $pfn + '\SystemAppData\Helium\User.dat')
    if (-not (Test-Path -LiteralPath $dat)) { return ($dat + ' : missing') }
    $fi = New-Object IO.FileInfo $dat
    ($dat + ' : mtime ' + $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + ' size ' + [long]$fi.Length)
}

function Get-LastLogMatch {
    <#
      The LAST line matching $Pattern in the NEWEST wavee*.log, or ''. Used for the always-on lines that describe
      the run rather than mark a moment in it ("identity: ..."), where the newest occurrence is the only one worth
      reporting and a mark would be meaningless.
    #>
    param([Parameter(Mandatory = $true)][string]$Pattern)
    $newest = @(Get-WaveeLogFiles) | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $newest) { return '' }
    $last = ''
    foreach ($line in ((Read-SharedText $newest.FullName) -split "`r?`n")) {
        if ([regex]::IsMatch("$line", $Pattern)) { $last = "$line".Trim() }
    }
    $last
}

# ===================================================================================================================
# Settings (reg.exe over the package's Helium hive; never while Wavee runs)
# ===================================================================================================================

function Mount-WaveeSettings {
    <#
      A packaged process's HKCU writes land in the package container's Helium hive, not in the real HKCU. The hive is
      a file (User.dat) that can only be loaded while NO process of the package is running, and it stays locked for a
      moment after the last one exits - hence the retry.
    #>
    if ($script:SettingsRoot) { return $script:SettingsRoot }

    $pfn = Get-WaveePfn
    if (-not $pfn) {
        $script:SettingsRoot = 'HKCU\Software\Wavee\Wavee\Settings'
        return $script:SettingsRoot
    }
    if (Get-WaveeProcess) { throw 'refusing to load the package hive while Wavee is running (it would fail or corrupt).' }

    $dat = Join-Path $env:LOCALAPPDATA ('Packages\' + $pfn + '\SystemAppData\Helium\User.dat')
    if (-not (Test-Path -LiteralPath $dat)) {
        # The package has never run, so it has no container hive yet; the unpackaged key is the only thing there is.
        $script:SettingsRoot = 'HKCU\Software\Wavee\Wavee\Settings'
        return $script:SettingsRoot
    }

    $deadline = (Get-Date).AddSeconds(20)
    $loaded = $false
    $last = ''
    while (-not $loaded) {
        $r = Invoke-Native 'reg.exe' @('load', ('HKU\' + $script:HiveName), $dat) -AllowFailure
        if ($r.ExitCode -eq 0) { $loaded = $true; break }
        $last = ($r.Output -join ' ')
        if ((Get-Date) -ge $deadline) { throw ('reg load of the Wavee hive failed: ' + $last) }
        Start-Sleep -Seconds 1
    }
    $script:HiveMounted = $true

    # The container path under the hive is an implementation detail of the OS; find the key rather than assume it.
    $found = ''
    $q = Invoke-Native 'reg.exe' @('query', ('HKU\' + $script:HiveName), '/s', '/f', 'Settings', '/k') -AllowFailure
    foreach ($l in $q.Output) {
        $s = "$l".Trim()
        if ($s -like '*\Software\Wavee\Wavee\Settings') { $found = $s; break }
    }
    if (-not $found) { $found = 'HKU\' + $script:HiveName + '\Software\Wavee\Wavee\Settings' }
    $script:SettingsRoot = $found
    $script:SettingsRoot
}

function Dismount-WaveeSettings {
    if (-not $script:HiveMounted) { $script:SettingsRoot = ''; return }
    # reg.exe holds nothing, but this process may still hold a stale handle from a query; force it to let go.
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        $r = Invoke-Native 'reg.exe' @('unload', ('HKU\' + $script:HiveName)) -AllowFailure
        if ($r.ExitCode -eq 0) { break }
        if ((Get-Date) -ge $deadline) {
            Write-Warning ('reg unload failed: ' + ($r.Output -join ' '))
            break
        }
        Start-Sleep -Seconds 1
    }
    $script:HiveMounted = $false
    $script:SettingsRoot = ''
}

function Get-WaveeSetting {
    <#  $null when the value is not there. .Value is the raw reg.exe text (REG_DWORD/QWORD print as 0x...). #>
    param([Parameter(Mandatory = $true)][string]$Name)
    $root = Mount-WaveeSettings
    $r = Invoke-Native 'reg.exe' @('query', $root, '/v', $Name) -AllowFailure
    if ($r.ExitCode -ne 0) { return $null }
    foreach ($l in $r.Output) {
        $m = [regex]::Match("$l", '^\s*' + [regex]::Escape($Name) + '\s+(REG_[A-Z_]+)\s+(.*)$')
        if ($m.Success) {
            return [pscustomobject]@{ Name = $Name; Type = $m.Groups[1].Value; Value = $m.Groups[2].Value.Trim() }
        }
    }
    $null
}

function Get-WaveeSettingText {
    param([Parameter(Mandatory = $true)][string]$Name, [string]$Fallback = '')
    $v = Get-WaveeSetting $Name
    if ($null -eq $v) { return $Fallback }
    "$($v.Value)"
}

function Set-WaveeSetting {
    <#  AppDataStore writes string -> REG_SZ, bool/int -> REG_DWORD, long/double -> REG_QWORD. Match it exactly. #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('REG_SZ', 'REG_DWORD', 'REG_QWORD')][string]$Type,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    $root = Mount-WaveeSettings
    Invoke-Native 'reg.exe' @('add', $root, '/v', $Name, '/t', $Type, '/d', $Value, '/f') | Out-Null
}

function Remove-WaveeSetting {
    param([Parameter(Mandatory = $true)][string]$Name)
    $root = Mount-WaveeSettings
    Invoke-Native 'reg.exe' @('delete', $root, '/v', $Name, '/f') -AllowFailure | Out-Null
}

# ===================================================================================================================
# Screens
# ===================================================================================================================

$script:ShotTypeReady = $false
function Initialize-ShotType {
    if ($script:ShotTypeReady) { return $true }
    try {
        Add-Type -AssemblyName System.Drawing
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WaveeE2EWin
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
'@ -ErrorAction Stop
        $script:ShotTypeReady = $true
    }
    catch {
        # Add-Type is idempotent-hostile: a second definition in the same session throws. Treat "already there" as ok.
        $script:ShotTypeReady = ($null -ne ('WaveeE2EWin' -as [type]))
    }
    $script:ShotTypeReady
}

function Save-WindowShot {
    <#  The Wavee window, rendered by the window itself (PrintWindow with PW_RENDERFULLCONTENT) - NOT a screen copy.
        The elevated console this harness runs in sits in front of the app more often than not, and a screen copy
        then captured the console (the first baseline of the day was a PowerShell window). PrintWindow asks DWM for
        the window's own composed content, so occlusion does not matter. Returns the PNG path, or '' when there is
        no window. #>
    param([Parameter(Mandatory = $true)][string]$Name)
    if (-not (Initialize-ShotType)) { return '' }
    $p = Get-WaveeProcess | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($null -eq $p) { return '' }
    try { [WaveeE2EWin]::SetForegroundWindow($p.MainWindowHandle) | Out-Null } catch { }
    Start-Sleep -Milliseconds 700
    $rect = New-Object 'WaveeE2EWin+RECT'
    if (-not [WaveeE2EWin]::GetWindowRect($p.MainWindowHandle, [ref]$rect)) { return '' }
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return '' }
    $out = Join-Path $script:OutDirFull $Name
    $bmp = New-Object Drawing.Bitmap($w, $h)
    try {
        $g = [Drawing.Graphics]::FromImage($bmp)
        try {
            $hdc = $g.GetHdc()
            try { [WaveeE2EWin]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null }   # 2 = PW_RENDERFULLCONTENT
            finally { $g.ReleaseHdc($hdc) }
        }
        finally { $g.Dispose() }
        $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bmp.Dispose() }
    $out
}

function Get-ShotMeanLuma {
    <#
      A cheap "is a modal plate up?" signal: the after-update dialog dims the whole shell behind a scrim, so the mean
      luminance of the window drops. Sampled on a grid - this is a soft signal, and the PNG is kept for human eyes.
    #>
    param([string]$Path)
    if (-not $Path) { return -1 }
    if (-not (Test-Path -LiteralPath $Path)) { return -1 }
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap($Path)
    try {
        $stepX = [Math]::Max(1, [int]($bmp.Width / 120))
        $stepY = [Math]::Max(1, [int]($bmp.Height / 120))
        $sum = 0.0
        $n = 0
        for ($y = 0; $y -lt $bmp.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bmp.Width; $x += $stepX) {
                $c = $bmp.GetPixel($x, $y)
                $sum = $sum + (0.2126 * $c.R) + (0.7152 * $c.G) + (0.0722 * $c.B)
                $n++
            }
        }
        if ($n -eq 0) { return -1 }
        [Math]::Round(($sum / $n), 2)
    }
    finally { $bmp.Dispose() }
}

# ===================================================================================================================
# HTTP + the request log
# ===================================================================================================================

function Invoke-LocalHttp {
    <#  A proxy-free request against the loopback feed. Never throws: a failure comes back as Status 0. #>
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [string]$Method = 'GET',
        [string]$UserAgent = 'WaveeE2E/1.0',
        [string]$Range = '',
        [int]$TimeoutMs = 15000)

    try {
        $req = [Net.HttpWebRequest]::Create($Url)
        $req.Proxy = $null
        $req.Method = $Method
        $req.UserAgent = $UserAgent
        $req.Timeout = $TimeoutMs
        if ($Range) {
            $parts = $Range.Split('-')
            $req.AddRange([int]$parts[0], [int]$parts[1])
        }
        $resp = $req.GetResponse()
        $text = ''
        $bytes = 0
        if ($Method -eq 'GET') {
            $ms = New-Object IO.MemoryStream
            $resp.GetResponseStream().CopyTo($ms)
            $bytes = $ms.Length
            $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
            $ms.Dispose()
        }
        $out = [pscustomobject]@{
            Status      = [int]$resp.StatusCode
            Length      = [long]$resp.ContentLength
            ContentRange = "$($resp.Headers['Content-Range'])"
            AcceptRanges = "$($resp.Headers['Accept-Ranges'])"
            Bytes       = $bytes
            Text        = $text
        }
        $resp.Close()
        return $out
    }
    catch [Net.WebException] {
        $code = 0
        if ($_.Exception.Response) {
            $code = [int]$_.Exception.Response.StatusCode
            try { $_.Exception.Response.Close() } catch { }
        }
        return [pscustomobject]@{ Status = $code; Length = -1; ContentRange = ''; AcceptRanges = ''; Bytes = 0; Text = '' }
    }
    catch {
        return [pscustomobject]@{ Status = 0; Length = -1; ContentRange = ''; AcceptRanges = ''; Bytes = 0; Text = '' }
    }
}

function Get-FeedRequests {
    <#  Parse the listener's tab-separated request log: time / method / path / status / range / bytes / user-agent. #>
    param([string]$LogPath = '')
    if (-not $LogPath) { $LogPath = $script:FeedLog }
    if (-not (Test-Path -LiteralPath $LogPath)) { return @() }
    $rows = @()
    foreach ($l in ((Read-SharedText $LogPath) -split "`r?`n")) {
        $s = "$l"
        if ($s.Trim().Length -eq 0) { continue }
        $c = $s -split $script:Tab
        if ($c.Count -lt 7) { continue }
        $rows += [pscustomobject]@{
            Time      = $c[0]
            Method    = $c[1]
            Path      = $c[2]
            Status    = [int]$c[3]
            Range     = $c[4]
            Bytes     = [long]$c[5]
            UserAgent = $c[6]
        }
    }
    ,$rows
}

# ===================================================================================================================
# The feed
# ===================================================================================================================

function Write-WhatsNewIndex {
    <#
      whatsnew-index.json in the SAME shape and casing Wavee.ReleaseTool writes: schema 1, product "wavee", a
      newest-first "releases" array of { version, packageVersion, name, date, channel }. camelCase, because
      ReleaseNotesJsonContext declares JsonKnownNamingPolicy.CamelCase; hand-built rather than ConvertTo-Json so a
      one-element array can never collapse into an object.

      DEDUPED BY VERSION, first wins - the same rule ReleaseNotesValidation.MergeIndex applies. A release is one
      semver: two entries for 0.2.0 make the What's-new rail draw "0.2.0 - YOU" twice. The harness is the only thing
      that can produce that (A and B deliberately share a semver so only the quad moves); a real release never
      collides, so the newer of the two - B - simply replaces A.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Releases)

    $seen = @()
    $unique = New-Object System.Collections.ArrayList
    foreach ($r in $Releases) {
        $v = "$($r.Version)"
        if ($seen -contains $v) { continue }
        $seen += $v
        [void]$unique.Add($r)
    }
    $Releases = @($unique.ToArray())

    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine('{')
    [void]$sb.AppendLine('  "schema": 1,')
    [void]$sb.AppendLine('  "product": "wavee",')
    [void]$sb.AppendLine('  "releases": [')
    for ($i = 0; $i -lt $Releases.Count; $i++) {
        $r = $Releases[$i]
        $comma = ','
        if ($i -eq ($Releases.Count - 1)) { $comma = '' }
        [void]$sb.AppendLine('    {')
        [void]$sb.AppendLine('      "version": "' + $r.Version + '",')
        [void]$sb.AppendLine('      "packageVersion": "' + $r.PackageVersion + '",')
        [void]$sb.AppendLine('      "name": "' + $r.Name + '",')
        [void]$sb.AppendLine('      "date": "' + $r.Date + '",')
        [void]$sb.AppendLine('      "channel": "' + $r.Channel + '"')
        [void]$sb.AppendLine('    }' + $comma)
    }
    [void]$sb.AppendLine('  ]')
    [void]$sb.AppendLine('}')
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($Path, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
    $Path
}

function New-IndexEntry {
    param([string]$Semver, [string]$Quad, [string]$Name = '', [string]$Channel = 'stable')
    if (-not $Name) { $Name = $Codename }
    [pscustomobject]@{
        Version        = $Semver
        PackageVersion = $Quad
        Name           = $Name
        Date           = ([DateTime]::UtcNow.ToString('yyyy-MM-dd'))
        Channel        = $Channel
    }
}

function Publish-LocalFeed {
    <#
    .SYNOPSIS
      Lay one release into the loopback feed and prove over the wire that the feed now serves it.
    .DESCRIPTION
      Layout (mirrors the GitHub one exactly, with <base> = http://127.0.0.1:<port>/):
        pkg\Wavee_<quad>_<arch>.msix            <base>pkg/...            MainPackage/@Uri
        <feed>\Wavee.<arch>.appinstaller        <base><feed>/...         root Uri  (App Installer's redirect rule:
                                                                          the root Uri MUST equal the served URL)
        <feed>\whatsnew-index.json              <base><feed>/whatsnew-index.json
        wavee-v<semver>\whatsnew.json + media\  <base>wavee-v<semver>/whatsnew.json
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][string]$Msix,
        [string]$NotesDir = '',
        [object[]]$Index = @(),
        # An emitted whatsnew-index.json (Wavee.ReleaseTool output). Preferred over -Index: it is the exact bytes a
        # real release publishes, already merged with the previous index and already deduped by version.
        [string]$IndexFile = '')

    if (-not (Test-Path -LiteralPath $Msix)) { throw "package not found: $Msix" }

    $pkgDir = Join-Path $script:FeedDirFull 'pkg'
    $feedDir = Join-Path $script:FeedDirFull $FeedRelease
    New-Item -ItemType Directory -Force -Path $pkgDir, $feedDir | Out-Null

    $msixName = Split-Path -Leaf $Msix
    Copy-Item -LiteralPath $Msix -Destination (Join-Path $pkgDir $msixName) -Force

    $msixUri = $script:BaseUrl + 'pkg/' + $msixName
    $outFile = Join-Path $feedDir ('Wavee.' + $Arch + '.appinstaller')
    New-WaveeAppInstaller -Template $script:Template -OutFile $outFile -Arch $Arch -Quad $Quad `
        -Publisher $Publisher -IdentityName $IdentityName -FeedUri $script:FeedUri -MsixUri $msixUri | Out-Null

    if ($NotesDir -and (Test-Path -LiteralPath $NotesDir)) {
        # wavee-v<semver>\ holds ONE release's document. A and B share a semver on purpose, so B's notes replace A's
        # here exactly the way a re-cut of the same version would - and the index above keeps one entry for it.
        $notesDst = Join-Path $script:FeedDirFull ('wavee-v' + $Semver)
        New-Item -ItemType Directory -Force -Path $notesDst | Out-Null
        foreach ($item in @(Get-ChildItem -LiteralPath $NotesDir -Force)) {
            # whatsnew-index.json belongs beside the .appinstaller, not in the per-release folder; RELEASE_BODY.md
            # and store-listing.txt are GitHub-release text the client never fetches.
            if ($item.Name -in @('whatsnew-index.json', 'RELEASE_BODY.md', 'store-listing.txt')) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $notesDst -Recurse -Force
        }
    }

    $indexPath = Join-Path $feedDir 'whatsnew-index.json'
    if ($IndexFile -and (Test-Path -LiteralPath $IndexFile)) {
        Copy-Item -LiteralPath $IndexFile -Destination $indexPath -Force
    }
    else {
        if ($Index.Count -eq 0) { $Index = @((New-IndexEntry -Semver $Semver -Quad $Quad)) }
        Write-WhatsNewIndex -Path $indexPath -Releases $Index | Out-Null
    }

    # Over the wire, not off the disk: this is the only check that proves the listener is serving what we just wrote.
    $r = Invoke-LocalHttp -Url $script:FeedUri
    if ($r.Status -ne 200) { throw "feed GET returned $($r.Status) for $($script:FeedUri)" }
    [xml]$x = $r.Text.TrimStart([char]0xFEFF)
    if ("$($x.AppInstaller.Version)" -ne $Quad) {
        throw "served feed root Version is '$($x.AppInstaller.Version)', expected $Quad"
    }
    if ("$($x.AppInstaller.MainPackage.Uri)" -ne $msixUri) {
        throw "served feed MainPackage/@Uri is '$($x.AppInstaller.MainPackage.Uri)', expected $msixUri"
    }
    [pscustomobject]@{ FeedUri = $script:FeedUri; MsixUri = $msixUri; MsixName = $msixName; Quad = $Quad }
}

function New-DatedChangelogCopy {
    <#
      A COPY of CHANGELOG.md with "## [<semver>] - unreleased" dated to today (UTC), which is the one edit
      wavee-release.ps1's bump phase makes before handing the file to the release tool. A copy, because the harness
      must leave `git status` exactly as it found it. Returns the path, or '' when there is nothing to date.

      (?=\r?$): CHANGELOG.md is CRLF on disk and .NET's multiline `$` only sees `\n`, so the CR is matched by a
      lookahead and left in place.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][string]$OutFile)

    if (-not (Test-Path -LiteralPath $script:Changelog)) { return '' }
    $today = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    $rx = '(?m)^(## \[' + [regex]::Escape($Semver) + '\] - )unreleased[ \t]*(?=\r?$)'
    $text = [IO.File]::ReadAllText($script:Changelog)
    $dated = [regex]::Replace($text, $rx, ('${1}' + $today))
    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($OutFile, $dated, (New-Object Text.UTF8Encoding $false))
    $OutFile
}

function Invoke-ReleaseNotes {
    <#
    .SYNOPSIS
      Run Wavee.ReleaseTool over the AUTHORED notes folder and return the EMITTED folder.
    .DESCRIPTION
      Run 2 embedded and served the authored ops\release\wavee\<semver>\whatsnew.json straight from disk. That file is
      an INPUT: "sections" is empty, "date" and "packageVersion" are empty strings, and the links are blank - so the
      app showed a plate with no changelog sections and no version pill, and the harness was rehearsing a document
      shape that never ships. The real pipeline runs the notes through the release tool first, and so does this.

      No token is passed. The tool warns that issue lookups are unauthenticated and continues; the authored notes
      carry no issue references, so nothing is unresolved.

      Returns a pscustomobject @{ Ok; Dir; Index; Detail }. Ok=$false means the caller falls back to the authored
      folder - a degraded rehearsal is still worth running, and the Detail says why it was degraded.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$AuthoredNotes,
        [string]$PreviousIndex = '')

    $fail = { param($why) [pscustomobject]@{ Ok = $false; Dir = $AuthoredNotes; Index = ''; Detail = $why } }

    if (-not (Test-Path -LiteralPath $script:ReleaseTool)) { return (& $fail 'no src\apps\Wavee.ReleaseTool') }
    if (-not (Test-Path -LiteralPath $AuthoredNotes)) { return (& $fail ("no authored notes at " + $AuthoredNotes)) }

    $changelog = New-DatedChangelogCopy -Semver $Semver -OutFile (Join-Path $script:OutDirFull ('CHANGELOG.' + $Semver + '.md'))
    if (-not $changelog) { return (& $fail 'no CHANGELOG.md to date') }

    $out = Join-Path $script:OutDirFull ('notes-' + $Label)
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    $toolArgs = @('validate',
        '--semver', $Semver,
        '--quad', $Quad,
        '--codename', $Codename,
        '--channel', 'stable',
        '--changelog', $changelog,
        '--notes', $AuthoredNotes,
        '--out', $out,
        '--repo', 'christosk92/WaveeMusic')
    if ($PreviousIndex -and (Test-Path -LiteralPath $PreviousIndex)) { $toolArgs += @('--previous-index', $PreviousIndex) }

    # --no-build first: the harness is not a build gate, and a solution that is already built makes this a
    # sub-second call. A tool that was never built fails here with "you must build" - retry once WITH the build.
    $base = @('run', '--project', $script:ReleaseTool, '-c', 'Release')
    $r = Invoke-Native 'dotnet' ($base + @('--no-build', '--') + $toolArgs) -AllowFailure
    if ($r.ExitCode -ne 0) {
        Write-Host '    (--no-build failed; rebuilding Wavee.ReleaseTool and retrying)' -ForegroundColor DarkGray
        $r = Invoke-Native 'dotnet' ($base + @('--') + $toolArgs) -AllowFailure
    }
    if ($r.ExitCode -ne 0) {
        return (& $fail ('Wavee.ReleaseTool exit ' + $r.ExitCode + ': ' + (($r.Output | Select-Object -Last 3) -join ' | ')))
    }

    $doc = Join-Path $out 'whatsnew.json'
    if (-not (Test-Path -LiteralPath $doc)) { return (& $fail ('the tool wrote no whatsnew.json into ' + $out)) }
    $index = Join-Path $out 'whatsnew-index.json'
    if (-not (Test-Path -LiteralPath $index)) { $index = '' }

    [pscustomobject]@{ Ok = $true; Dir = $out; Index = $index; Detail = $out }
}

function Get-NotesShape {
    <#  A one-line "what is actually in this document", so the table shows whether the plate had anything to draw. #>
    param([string]$Dir)
    $p = Join-Path $Dir 'whatsnew.json'
    if (-not (Test-Path -LiteralPath $p)) { return 'no whatsnew.json' }
    try {
        $j = (Read-SharedText $p) | ConvertFrom-Json
        $items = 0
        foreach ($s in @($j.sections)) { $items += @($s.items).Count }
        ('pkg ' + $j.packageVersion + ' date ' + $j.date + '; ' + @($j.highlights).Count + ' highlight(s), ' +
            @($j.sections).Count + ' section(s), ' + $items + ' item(s)')
    }
    catch { return ('unreadable: ' + $_.Exception.Message) }
}

function Invoke-PackWavee {
    <#  One pack-wavee-msix.ps1 run, stamped for the loopback feed. Returns the .msix path. #>
    param(
        [Parameter(Mandatory = $true)][string]$Quad,
        [Parameter(Mandatory = $true)][string]$Semver,
        [Parameter(Mandatory = $true)][string]$NotesDir,
        [Parameter(Mandatory = $true)][string]$OutputDir)

    $packArgs = @{
        Arch          = $Arch
        Quad          = $Quad
        Semver        = $Semver
        Channel       = 'stable'
        Codename      = $Codename
        FeedRelease   = $FeedRelease
        UpdateBaseUrl = $script:BaseUrl
        Publisher     = $Publisher
        IdentityName  = $IdentityName
        OutputDir     = $OutputDir
    }
    if ($NotesDir -and (Test-Path -LiteralPath $NotesDir)) { $packArgs['NotesDir'] = $NotesDir }
    if ($NoAot) { $packArgs['NoAot'] = $true }
    # The pack script WRITES its progress to the output stream; forward it to the host so it does not become part of
    # this function's return value (a path plus forty lines of build output is not a path).
    & $script:PackScript @packArgs | ForEach-Object { Write-Host "$_" }
    $msix = Join-Path $OutputDir ('Wavee_' + $Quad + '_' + $Arch + '.msix')
    if (-not (Test-Path -LiteralPath $msix)) { throw "pack produced no package at $msix" }
    $msix
}

# ===================================================================================================================
# Run
# ===================================================================================================================

$script:StartedAt = Get-Date
Write-Host ''
Write-Host "Wavee local update E2E" -ForegroundColor White
Write-Host ("  scenario $Scenario  arch $Arch  feed $FeedRelease  base $($script:BaseUrl)")
Write-Host ("  A $QuadA ($SemverA)  B $QuadB ($SemverB)  driver $Driver  drill $Drill")
Write-Host ("  feed dir $($script:FeedDirFull)")
Write-Host ("  out dir  $($script:OutDirFull)")

try {

    # -- P0 preflight ------------------------------------------------------------------------------------------------
    Invoke-Phase 'P0' 'preflight' {
        $wi = [Security.Principal.WindowsIdentity]::GetCurrent()
        $elevated = (New-Object Security.Principal.WindowsPrincipal $wi).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        Assert-True -Phase 'P0' -Name 'elevated' -Condition $elevated | Out-Null
        Assert-True -Phase 'P0' -Name 'Windows PowerShell 5.1' -Condition ($PSVersionTable.PSVersion.Major -eq 5) `
            -Detail "$($PSVersionTable.PSVersion)" -Soft | Out-Null

        $sdkOk = $false
        try { $t = Get-WindowsSdkTools; $sdkOk = (Test-Path $t.MakeAppx) } catch { $sdkOk = $false }
        Assert-True -Phase 'P0' -Name 'Windows SDK (makeappx/signtool)' -Condition $sdkOk | Out-Null

        $dotnet = Invoke-Native 'dotnet' @('--version') -AllowFailure
        Assert-True -Phase 'P0' -Name 'dotnet on PATH' -Condition ($dotnet.ExitCode -eq 0) `
            -Detail (($dotnet.Output | Select-Object -First 1)) | Out-Null

        $inUse = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
                   Where-Object { $_.Port -eq $Port })
        Assert-True -Phase 'P0' -Name "port $Port is free" -Condition ($inUse.Count -eq 0) | Out-Null

        $unlock = $null
        try {
            $unlock = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
                -Name 'AllowAllTrustedApps' -ErrorAction SilentlyContinue
        }
        catch { $unlock = $null }
        $sideload = ($null -eq $unlock) -or ([int]$unlock.AllowAllTrustedApps -eq 1)
        Assert-True -Phase 'P0' -Name 'sideloading allowed' -Condition $sideload -Soft `
            -Detail 'AllowAllTrustedApps is 0; Windows 11 allows sideloading by default, so this was turned off' | Out-Null

        Assert-True -Phase 'P0' -Name 'notes dir A' -Condition (Test-Path -LiteralPath $NotesA) -Detail $NotesA -Soft | Out-Null
        Assert-True -Phase 'P0' -Name 'notes dir B' -Condition (Test-Path -LiteralPath $NotesB) -Detail $NotesB -Soft | Out-Null
        # The notes pass turns the AUTHORED folder into the EMITTED document that actually ships. Both halves are
        # soft: a missing tool or an already-dated CHANGELOG heading degrades the rehearsal, it does not invalidate
        # the update path itself.
        if ($NoNotes) {
            Record -Phase 'P0' -Name 'Wavee.ReleaseTool' -Status 'SKIP' -Detail '-NoNotes'
        }
        else {
            Assert-True -Phase 'P0' -Name 'Wavee.ReleaseTool project' -Condition (Test-Path -LiteralPath $script:ReleaseTool) `
                -Detail $script:ReleaseTool -Soft | Out-Null
            $clOk = $false
            if (Test-Path -LiteralPath $script:Changelog) {
                $clText = [IO.File]::ReadAllText($script:Changelog)
                $clOk = [regex]::IsMatch($clText, '(?m)^## \[' + [regex]::Escape($SemverA) + '\] - (\d{4}-\d{2}-\d{2}|unreleased)')
            }
            Assert-True -Phase 'P0' -Name "CHANGELOG has a [$SemverA] heading" -Condition $clOk -Soft `
                -Detail 'the release tool refuses a semver with no CHANGELOG section' | Out-Null
        }
        Assert-True -Phase 'P0' -Name 'appinstaller template' -Condition (Test-Path -LiteralPath $script:Template) | Out-Null
        Assert-True -Phase 'P0' -Name 'pack script' -Condition (Test-Path -LiteralPath $script:PackScript) | Out-Null
        Assert-True -Phase 'P0' -Name 'Add-Type / System.Drawing' -Condition (Initialize-ShotType) -Soft | Out-Null
    }

    # -- P1 clean ----------------------------------------------------------------------------------------------------
    Invoke-Phase 'P1' 'clean slate' {
        Stop-WaveeHard
        $removed = 0
        foreach ($p in @(Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue)) {
            try { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop; $removed++ } catch { }
        }
        Record -Phase 'P1' -Name 'packages removed' -Status 'INFO' -Detail "$removed"

        # The REAL %LOCALAPPDATA%\Wavee (an unpackaged dev run writes there) must not exist when the packaged app
        # starts. Observed: with a same-day wavee-<date>.log already sitting in the real folder, the packaged app
        # appended to THAT file (and its settings went to the real HKCU), while a launch that found no real folder
        # wrote inside the package container - two stores, and the after-update evidence read the wrong one
        # (lastRun='' on B; "updated:" in a log the container never saw). Clean slate means the real folder too.
        $realAppData = Join-Path $env:LOCALAPPDATA 'Wavee'
        if (Test-Path -LiteralPath $realAppData) {
            $n = @(Get-ChildItem -LiteralPath $realAppData -Recurse -File -ErrorAction SilentlyContinue).Count
            Remove-Item -LiteralPath $realAppData -Recurse -Force -ErrorAction SilentlyContinue
            Record -Phase 'P1' -Name 'real %LOCALAPPDATA%\Wavee removed (unpackaged leftovers)' -Status 'INFO' -Detail "$n file(s)"
        }
        Assert-True -Phase 'P1' -Name 'no real %LOCALAPPDATA%\Wavee left behind' -Condition (-not (Test-Path -LiteralPath $realAppData)) | Out-Null
        # The unvirtualized key only exists if an unpackaged dev build ever ran here; a stale lastRunVersion in it
        # would make the packaged app's first launch claim an update that never happened.
        try { Remove-Item -Path 'HKCU:\Software\Wavee\Wavee\Settings' -Recurse -Force -ErrorAction SilentlyContinue } catch { }

        if (-not $KeepFeed) {
            if (Test-Path -LiteralPath $script:FeedDirFull) { Remove-Item -LiteralPath $script:FeedDirFull -Recurse -Force }
        }
        New-Item -ItemType Directory -Force -Path $script:FeedDirFull | Out-Null
        New-Item -ItemType Directory -Force -Path $script:OutDirFull | Out-Null
        if (Test-Path -LiteralPath $script:FeedLog) { Remove-Item -LiteralPath $script:FeedLog -Force }
        Assert-True -Phase 'P1' -Name 'no Wavee package installed' -Condition ($null -eq (Get-WaveePackage)) | Out-Null
    }

    # -- P2 notes + pack ---------------------------------------------------------------------------------------------
    Invoke-Phase 'P2' 'release notes, then pack A and B' {
        # The notes come FIRST, because what gets embedded into a package is the EMITTED document, not the authored
        # one. Run 2 shipped the authored file (empty sections, empty date, empty packageVersion) into both packages
        # and into the feed, so the after-update plate had nothing to draw and no version pill to draw it under.
        if ($NoNotes) {
            $script:NotesOutA = $NotesA
            $script:NotesOutB = $NotesB
            Record -Phase 'P2' -Name 'release notes' -Status 'SKIP' -Detail '-NoNotes: embedding the AUTHORED folders'
        }
        else {
            $rA = Invoke-ReleaseNotes -Semver $SemverA -Quad $QuadA -Label 'A' -AuthoredNotes $NotesA
            $script:NotesOutA = $rA.Dir
            $script:IndexA = $rA.Index
            Assert-True -Phase 'P2' -Name 'notes A emitted by Wavee.ReleaseTool' -Condition $rA.Ok -Soft `
                -Detail $rA.Detail | Out-Null
            if ($rA.Ok) { Record -Phase 'P2' -Name 'notes A shape' -Status 'INFO' -Detail (Get-NotesShape $rA.Dir) }

            # B's index is merged onto A's, which is what makes ONE 0.2.0 entry out of two same-semver releases:
            # MergeIndex drops any previous entry whose version equals the one being published.
            $rB = Invoke-ReleaseNotes -Semver $SemverB -Quad $QuadB -Label 'B' -AuthoredNotes $NotesB -PreviousIndex $script:IndexA
            $script:NotesOutB = $rB.Dir
            $script:IndexB = $rB.Index
            Assert-True -Phase 'P2' -Name 'notes B emitted by Wavee.ReleaseTool' -Condition $rB.Ok -Soft `
                -Detail $rB.Detail | Out-Null
            if ($rB.Ok) { Record -Phase 'P2' -Name 'notes B shape' -Status 'INFO' -Detail (Get-NotesShape $rB.Dir) }
        }

        $dirA = Join-Path $script:OutDirFull 'A'
        $dirB = Join-Path $script:OutDirFull 'B'
        New-Item -ItemType Directory -Force -Path $dirA, $dirB | Out-Null
        $script:MsixA = Join-Path $dirA ('Wavee_' + $QuadA + '_' + $Arch + '.msix')
        $script:MsixB = Join-Path $dirB ('Wavee_' + $QuadB + '_' + $Arch + '.msix')

        if ($SkipPackA) {
            Assert-True -Phase 'P2' -Name 'reusing package A' -Condition (Test-Path -LiteralPath $script:MsixA) -Detail $script:MsixA | Out-Null
        }
        else {
            $script:MsixA = Invoke-PackWavee -Quad $QuadA -Semver $SemverA -NotesDir $script:NotesOutA -OutputDir $dirA
            Record -Phase 'P2' -Name 'packed A' -Status 'PASS' -Detail $script:MsixA
        }
        if ($SkipPackB) {
            Assert-True -Phase 'P2' -Name 'reusing package B' -Condition (Test-Path -LiteralPath $script:MsixB) -Detail $script:MsixB | Out-Null
        }
        else {
            $script:MsixB = Invoke-PackWavee -Quad $QuadB -Semver $SemverB -NotesDir $script:NotesOutB -OutputDir $dirB
            Record -Phase 'P2' -Name 'packed B' -Status 'PASS' -Detail $script:MsixB
        }

        foreach ($pair in @(@($script:MsixA, $QuadA), @($script:MsixB, $QuadB))) {
            $id = Get-MsixIdentity $pair[0]
            $ok = ("$($id.Version)" -eq $pair[1]) -and ("$($id.ProcessorArchitecture)" -eq $Arch) -and
                  ("$($id.Name)" -eq $IdentityName) -and ("$($id.Publisher)" -eq $Publisher)
            Assert-True -Phase 'P2' -Name ('identity ' + $pair[1]) -Condition $ok `
                -Detail "$($id.Name) $($id.Version) $($id.ProcessorArchitecture)" | Out-Null
        }

        # Both packages must be signed by the SAME dev cert, or Windows refuses the update as a publisher change.
        $cerA = [IO.Path]::ChangeExtension($script:MsixA, '.cer')
        $cerB = [IO.Path]::ChangeExtension($script:MsixB, '.cer')
        if ((Test-Path -LiteralPath $cerA) -and (Test-Path -LiteralPath $cerB)) {
            $tA = (Get-PfxCertificate -FilePath $cerA).Thumbprint
            $tB = (Get-PfxCertificate -FilePath $cerB).Thumbprint
            Assert-True -Phase 'P2' -Name 'A and B share one signing cert' -Condition ($tA -eq $tB) -Detail "$tA / $tB" | Out-Null
        }
        else {
            Record -Phase 'P2' -Name 'A and B share one signing cert' -Status 'SKIP' -Detail 'no .cer beside the packages'
        }
    }

    # -- P3 trust ----------------------------------------------------------------------------------------------------
    Invoke-Phase 'P3' 'trust the dev certificate' {
        $cerA = [IO.Path]::ChangeExtension($script:MsixA, '.cer')
        if (-not (Test-Path -LiteralPath $cerA)) {
            Record -Phase 'P3' -Name 'certificate import' -Status 'SKIP' -Detail 'no .cer (Trusted Signing build?)'
            return
        }
        $thumb = (Get-PfxCertificate -FilePath $cerA).Thumbprint
        $already = Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue |
                   Where-Object { $_.Thumbprint -eq $thumb }
        if ($already) {
            Record -Phase 'P3' -Name 'certificate already trusted' -Status 'PASS' -Detail $thumb
        }
        else {
            Import-Certificate -FilePath $cerA -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
            $now = Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' | Where-Object { $_.Thumbprint -eq $thumb }
            Assert-True -Phase 'P3' -Name 'certificate imported into LocalMachine\TrustedPeople' `
                -Condition ($null -ne $now) -Detail $thumb | Out-Null
        }
    }

    # -- P4 server ---------------------------------------------------------------------------------------------------
    Invoke-Phase 'P4' 'start the loopback feed' {
        $script:Server = Start-LocalFeedServer -Root $script:FeedDirFull -Port $Port -BindHost '127.0.0.1' -LogPath $script:FeedLog
        Record -Phase 'P4' -Name 'listening' -Status 'PASS' -Detail $script:Server.Prefix

        $probe = Join-Path $script:FeedDirFull 'probe.bin'
        $bytes = New-Object byte[] 1000
        for ($i = 0; $i -lt 1000; $i++) { $bytes[$i] = [byte]($i % 251) }
        [IO.File]::WriteAllBytes($probe, $bytes)
        try {
            $head = Invoke-LocalHttp -Url ($script:BaseUrl + 'probe.bin') -Method 'HEAD'
            Assert-True -Phase 'P4' -Name 'HEAD carries Content-Length' -Condition ($head.Status -eq 200 -and $head.Length -eq 1000) `
                -Detail "status $($head.Status) length $($head.Length)" | Out-Null
            $rng = Invoke-LocalHttp -Url ($script:BaseUrl + 'probe.bin') -Range '0-9'
            Assert-True -Phase 'P4' -Name 'Range 0-9 answers 206' -Condition ($rng.Status -eq 206 -and $rng.Bytes -eq 10) `
                -Detail "status $($rng.Status) $($rng.ContentRange)" | Out-Null
            $missing = Invoke-LocalHttp -Url ($script:BaseUrl + 'no-such-file.json')
            Assert-True -Phase 'P4' -Name 'unknown path answers 404' -Condition ($missing.Status -eq 404) `
                -Detail "status $($missing.Status)" | Out-Null
        }
        finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }
    }

    # -- P5 feed A ---------------------------------------------------------------------------------------------------
    Invoke-Phase 'P5' 'publish A into the feed' {
        $pub = Publish-LocalFeed -Quad $QuadA -Semver $SemverA -Msix $script:MsixA -NotesDir $script:NotesOutA `
            -IndexFile $script:IndexA -Index @((New-IndexEntry -Semver $SemverA -Quad $QuadA))
        Record -Phase 'P5' -Name 'feed head is A' -Status 'PASS' -Detail "$($pub.FeedUri) -> $($pub.MsixUri)"
        $idx = Invoke-LocalHttp -Url ($script:BaseUrl + $FeedRelease + '/whatsnew-index.json')
        Assert-True -Phase 'P5' -Name 'whatsnew-index.json is served' -Condition ($idx.Status -eq 200 -and $idx.Text -like '*"packageVersion"*') | Out-Null
        $doc = Invoke-LocalHttp -Url ($script:BaseUrl + 'wavee-v' + $SemverA + '/whatsnew.json')
        Assert-True -Phase 'P5' -Name "wavee-v$SemverA/whatsnew.json is served" -Condition ($doc.Status -eq 200) -Soft `
            -Detail "status $($doc.Status)" | Out-Null
    }

    # -- P6 install A ------------------------------------------------------------------------------------------------
    Invoke-Phase 'P6' ('install A (' + $Scenario + ')') {
        $p6Since = (Get-Date).AddSeconds(-5)
        if ($Scenario -eq 'inapp') {
            # BARE install, on purpose. Installing through the .appinstaller creates the App Installer association,
            # and the template carries OnLaunch HoursBetweenUpdateChecks="0" - so once the feed has moved, the very
            # next activation is intercepted by the deployment engine and B is registered before Wavee's own code
            # runs. That is a real path (it is what -Scenario os measures), but it makes the app's own checker
            # unobservable: run 2 saw "up to date A" then "updated: A -> B" with no "update available" in between.
            Add-AppxPackage -Path $script:MsixA
            # Three proofs, and the absence of ALL THREE is what "bare" means here. The cmdlet alone is not enough
            # in either direction: it has answered "no association" for a package that demonstrably had one.
            $ev = Get-AssociationEvidence -Since $p6Since
            Record -Phase 'P6' -Name 'association probe: cmdlet' -Status 'INFO' -Detail $ev.Cmdlet.Detail
            Record -Phase 'P6' -Name 'association probe: feed log' -Status 'INFO' `
                -Detail ('' + $ev.FeedHits.Count + ' App Virt Client GET(s) of ' + (ConvertTo-FeedPath $script:FeedUri))
            Record -Phase 'P6' -Name 'association probe: AppXDeploymentServer 603' -Status 'INFO' `
                -Detail ('' + $ev.EventHits.Count + ' UpdateUsingAppInstallerOperation (603) event(s)' +
                         ' - other 603 operations (AddPackageOperation and friends) are deliberately not counted')
            Assert-True -Phase 'P6' -Name 'no auto-update association (Windows cannot preempt the app)' `
                -Condition (-not $ev.Associated) -Detail $ev.Detail | Out-Null
        }
        else {
            # Installed THROUGH the feed: this is what a user gets from a download link, and it is what arms App
            # Installer's on-launch check.
            Add-AppxPackage -AppInstallerFile $script:FeedUri
            $ev = Get-AssociationEvidence -Since $p6Since
            Record -Phase 'P6' -Name 'association probe: cmdlet' -Status 'INFO' -Detail $ev.Cmdlet.Detail
            Record -Phase 'P6' -Name 'association probe: feed log' -Status 'INFO' `
                -Detail ('' + $ev.FeedHits.Count + ' App Virt Client GET(s) of ' + (ConvertTo-FeedPath $script:FeedUri))
            Record -Phase 'P6' -Name 'association probe: AppXDeploymentServer 603' -Status 'INFO' `
                -Detail ('' + $ev.EventHits.Count + ' UpdateUsingAppInstallerOperation (603) event(s)' +
                         ' - other 603 operations (AddPackageOperation and friends) are deliberately not counted')
            Assert-True -Phase 'P6' -Name 'AppInstallerUri association created' `
                -Condition $ev.Associated -Soft -Detail $ev.Detail | Out-Null

            $reqs = Get-FeedRequests
            $pkgHit = @($reqs | Where-Object { $_.Path -like ('*/pkg/Wavee_' + $QuadA + '_*') -and ($_.Status -eq 200 -or $_.Status -eq 206) })
            Assert-True -Phase 'P6' -Name 'the deployment engine downloaded A over loopback' -Condition ($pkgHit.Count -gt 0) `
                -Detail "$($pkgHit.Count) request(s)" | Out-Null
        }
        Assert-True -Phase 'P6' -Name 'installed version is A' -Condition (Test-QuadMatch (Get-WaveeQuad) $QuadA) `
            -Detail (Get-WaveeVersionsNow).Detail | Out-Null
    }

    # -- P7 launch A -------------------------------------------------------------------------------------------------
    Invoke-Phase 'P7' 'launch A and let it check' {
        $mark = Get-LogMark
        $p = Start-Wavee -Phase 'P7'
        Assert-True -Phase 'P7' -Name 'Wavee started' -Condition ($null -ne $p) | Out-Null

        # The container proof. The packaged app's log must appear under the package's LocalCache and NOTHING may
        # appear in the real %LOCALAPPDATA%\Wavee - otherwise every later settings/log read in this run is looking at
        # a store the app is not using (see P1). The log file is created at startup; it need not have content yet.
        Start-Sleep -Seconds 3
        $pkgLogs = Get-PackageLogDir
        $realLogs = Join-Path $env:LOCALAPPDATA 'Wavee\logs'
        $inContainer = ($pkgLogs -and (Test-Path -LiteralPath $pkgLogs)) -and (-not (Test-Path -LiteralPath $realLogs))
        Assert-True -Phase 'P7' -Name 'the app writes inside its package container (LocalCache, not the real %LOCALAPPDATA%)' `
            -Condition $inContainer -Detail ("LocalCache logs: " + (Test-Path -LiteralPath $pkgLogs) + "; real logs dir: " + (Test-Path -LiteralPath $realLogs)) | Out-Null

        $pattern = 'up to date: feed ' + [regex]::Escape($QuadA) + ', running ' + [regex]::Escape($QuadA)
        $hit = Wait-LogLine -Pattern $pattern -Mark $mark -TimeoutSec $CheckTimeoutSec
        Assert-True -Phase 'P7' -Name 'in-app check says up to date' -Condition $hit.Ok `
            -Detail (Get-WaitDetail $hit $pattern) | Out-Null

        $reqs = Get-FeedRequests
        $inApp = @($reqs | Where-Object { $_.Path -like ('/' + $FeedRelease + '/*.appinstaller') -and $_.UserAgent -like 'Wavee/*' })
        Assert-True -Phase 'P7' -Name 'the in-app checker fetched the feed (UA Wavee/...)' -Condition ($inApp.Count -gt 0) `
            -Detail (($inApp | Select-Object -First 1 | ForEach-Object { $_.UserAgent })) | Out-Null

        $offSite = @()
        foreach ($line in ($hit.Text -split "`r?`n")) {
            if ($line -match '\[(update|whatsnew)\]' -and $line -match 'github\.com') { $offSite += $line }
        }
        Assert-True -Phase 'P7' -Name 'no github.com in the update / whatsnew lines' -Condition ($offSite.Count -eq 0) `
            -Detail (($offSite | Select-Object -First 1)) | Out-Null

        $shot = Save-WindowShot '01-baseline.png'
        $script:BaselineLuma = Get-ShotMeanLuma $shot
        Record -Phase 'P7' -Name 'baseline screenshot' -Status 'INFO' -Detail "$shot (luma $($script:BaselineLuma))"
    }

    # -- P8 feed B ---------------------------------------------------------------------------------------------------
    Invoke-Phase 'P8' 'publish B into the same feed' {
        $pub = Publish-LocalFeed -Quad $QuadB -Semver $SemverB -Msix $script:MsixB -NotesDir $script:NotesOutB `
            -IndexFile $script:IndexB `
            -Index @((New-IndexEntry -Semver $SemverB -Quad $QuadB), (New-IndexEntry -Semver $SemverA -Quad $QuadA))
        Record -Phase 'P8' -Name 'feed head is B' -Status 'PASS' -Detail "$($pub.MsixUri)"

        # One entry per VERSION. A and B share a semver here on purpose, so the served index must carry ONE 0.2.0
        # row - two is what made the What's-new rail draw "0.2.0 - YOU" twice in run 2.
        $idx = Invoke-LocalHttp -Url ($script:BaseUrl + $FeedRelease + '/whatsnew-index.json')
        $dupes = 0
        try {
            $seen = @()
            foreach ($e in @(($idx.Text | ConvertFrom-Json).releases)) {
                $v = "$($e.version)"
                if ($seen -contains $v) { $dupes++ } else { $seen += $v }
            }
            Assert-True -Phase 'P8' -Name 'the served index has one entry per version' -Condition ($dupes -eq 0) `
                -Detail ("versions: " + ($seen -join ', ')) | Out-Null
        }
        catch {
            Record -Phase 'P8' -Name 'the served index has one entry per version' -Status 'WARN' `
                -Detail ('unparseable index: ' + $_.Exception.Message)
        }
    }

    # -- P9 drive the update -----------------------------------------------------------------------------------------
    #
    # ONE mark, taken immediately before the launch that triggers the update, and kept in $script:UpdateMark for P10
    # and P11. Run 2 lost four checks to a mark taken too late: the app logged "updated: A -> B" during the relaunch,
    # and P11 then marked the log AFTER that line and waited for it forever.
    #
    $failPattern = '(deployment failed 0x[0-9A-Fa-f]{8}.*|install-on-quit gave up.*)'

    if ($Scenario -eq 'inapp') {
        Invoke-Phase 'P9' ('drive the update in-app (' + $Driver + ')') {
            # The launch check has a cooldown; clearing lastCheckedMs is what makes the NEXT launch check again.
            # Every settings edit happens with the app stopped - the container hive cannot be loaded otherwise.
            Stop-WaveeHard
            Assert-True -Phase 'P9' -Name 'app stopped before touching settings' -Condition (-not (Get-WaveeProcess)) | Out-Null
            Mount-WaveeSettings | Out-Null
            Set-WaveeSetting -Name 'app.update.installOnQuit' -Type 'REG_DWORD' -Value '1'
            Remove-WaveeSetting -Name 'app.update.lastCheckedMs'
            if ($Drill -eq 'snooze') { Set-WaveeSetting -Name 'app.update.snoozedVersion' -Type 'REG_SZ' -Value $QuadB }
            Record -Phase 'P9' -Name 'settings armed' -Status 'PASS' -Detail ('root ' + $script:SettingsRoot)
            Dismount-WaveeSettings

            # THE mark. Everything the update produces - "update available", "install-on-quit: staging", "staged",
            # and the "updated: A -> B" that the NEXT process writes - is read from here.
            $script:UpdateMarkTime = Get-Date
            $script:UpdateMark = Get-LogMark
            Record -Phase 'P9' -Name 'log mark taken' -Status 'INFO' -Detail (Format-LogMark $script:UpdateMark)

            # What the app's own storage looks like at the mark. P10 compares against these: the update must not
            # reset the package's LocalCache log directory (observed: every marked file replaced by one fresh 7 KB
            # file, which both invalidated every log wait AND meant the user's data had been discarded).
            $script:LogSnapAtMark = @(Get-FileSnapshot (Get-PackageLogDir))
            $script:HeliumAtMark = Get-HeliumStamp
            Record -Phase 'P9' -Name 'package LocalCache logs at the mark' -Status 'INFO' `
                -Detail (Format-FileSnapshot $script:LogSnapAtMark (Get-PackageLogDir))
            Record -Phase 'P9' -Name 'Helium User.dat at the mark' -Status 'INFO' -Detail $script:HeliumAtMark

            $p = Start-Wavee -Phase 'P9'
            Assert-True -Phase 'P9' -Name 'Wavee relaunched' -Condition ($null -ne $p) | Out-Null

            $available = 'update available: ' + [regex]::Escape($QuadB) + ' \(running ' + [regex]::Escape($QuadA) + '\)'
            $hit = Wait-LogLine -Pattern $available -Mark $script:UpdateMark -TimeoutSec $CheckTimeoutSec
            Assert-True -Phase 'P9' -Name 'the checker offered B' -Condition $hit.Ok `
                -Detail (Get-WaitDetail $hit $available) | Out-Null
            Start-Sleep -Seconds 2
            Record -Phase 'P9' -Name 'update-available screenshot' -Status 'INFO' -Detail (Save-WindowShot '03-update-available.png')

            # RegisterApplicationRestart refuses a process younger than ~60 s, and the relaunch after the update is
            # what P11 observes. Wait the floor out rather than race it.
            Wait-WaveeUptime -Seconds 65
            Record -Phase 'P9' -Name 'restart floor cleared (>= 65 s uptime)' -Status 'PASS'

            if ($Drill -eq 'network') {
                Stop-LocalFeedServer $script:Server
                $script:Server = $null
                Record -Phase 'P9' -Name 'network drill: feed stopped mid-flight' -Status 'INFO'
            }

            if ($Driver -eq 'ui') {
                Write-Host ''
                Write-Host '    ACTION REQUIRED: in Wavee, open Settings > About and press "Update now".' -ForegroundColor Yellow
                Write-Host '    (this run is -Driver ui; use -Driver quit for the unattended path)' -ForegroundColor Yellow
            }
            else {
                foreach ($proc in @(Get-WaveeProcess)) { try { $proc.CloseMainWindow() | Out-Null } catch { } }
                Record -Phase 'P9' -Name 'close requested (install-on-quit)' -Status 'PASS'
            }

            # Two lines, two different writers. Program.cs announces the intent ("install-on-quit: staging <B>") and
            # the service announces the outcome ("staged <B>; restarting"); Program.cs then logs the settled state,
            # unless ForceTargetApplicationShutdown kills it first - which is why EITHER outcome line is a good ending.
            $staging = 'install-on-quit: staging ' + [regex]::Escape($QuadB)
            $begun = Wait-LogLine -Pattern $staging -Mark $script:UpdateMark -TimeoutSec $CheckTimeoutSec -FailPattern $failPattern
            if ($Drill -ne 'network') {
                Assert-True -Phase 'P9' -Name 'install-on-quit started staging B' -Condition $begun.Ok `
                    -Detail (Get-WaitDetail $begun $staging) | Out-Null
            }

            # THREE good endings, not two. The third is the one the deployment itself asks for: the Restart Manager
            # pass inside ForceTargetApplicationShutdown sends WM_QUERYENDSESSION/WM_ENDSESSION/WM_CLOSE and then waits
            # ~30 s for THIS process to exit before it can finish. Continuing to wait on ApplyAsync there is a
            # deadlock the OS resolves by killing us (WER MoAppHang + Application event 1002), so MessagePump.RunUntil
            # now reports ShutdownRequested and Program.cs logs
            #   "install-on-quit: Windows asked us to exit (the deployment is taking over); staged <B>"
            # and returns. That is a SUCCESS: the package is staged and RegisterApplicationRestart brings Wavee back.
            # The '.*' spans the parenthesised clause so nothing here has to escape it.
            $staged = '(install-on-quit finished: Installing' +
                      '|install-on-quit: Windows asked us to exit .*staged ' + [regex]::Escape($QuadB) +
                      '|staged ' + [regex]::Escape($QuadB) + '; restarting)'
            # ForceTargetApplicationShutdown may kill the process before it can write the "finished" line, so the decisive
            # signal is the PACKAGE VERSION flipping to B; poll that between short log waits instead of sitting out
            # the whole apply timeout for a line that can legitimately never appear.
            #
            # THE POLL THAT NEVER FIRED (run 3): it asked Get-WaveePackage, which was
            # "Get-AppxPackage -Name <name> | Select-Object -First 1", and compared THAT one package's raw .Version
            # string to $QuadB. While a deferred update settles, Windows keeps the OUTGOING version registered
            # until the last process of the package exits, so the query answers with TWO packages and -First 1
            # returns whichever the deployment store enumerates first - A. The loop watched A for the full 600 s
            # while B was already there; P10, which only ran after the old process was finally gone and A had been
            # unregistered, then saw B on its first try.
            #
            # So: re-query FRESH every iteration, look at EVERY version Windows can see (per-user AND, since the
            # harness is elevated, -AllUsers, which additionally lists a package that is staged but not yet
            # registered), and compare NORMALIZED quad strings rather than whatever .Version stringifies to.
            $applied = [pscustomobject]@{ Ok = $false; Line = ''; Failed = $false; Where = '' }
            $applyDeadline = (Get-Date).AddSeconds($ApplyTimeoutSec)
            $lastSeen = ''
            while ((Get-Date) -lt $applyDeadline) {
                $applied = Wait-LogLine -Pattern $staged -Mark $script:UpdateMark -TimeoutSec 15 -FailPattern $failPattern
                if ($applied.Ok -or $applied.Failed) { break }
                $seen = Get-WaveeVersionsNow
                $lastSeen = $seen.Detail
                if (Test-AnyQuadMatch -Versions $seen.All -Expected $QuadB) {
                    $applied = [pscustomobject]@{
                        Ok     = $true
                        Line   = ('package flipped to ' + $QuadB + ' (the process was shut down before it could log the finish); ' + $seen.Detail)
                        Failed = $false
                        Where  = 'Get-AppxPackage'
                    }
                    break
                }
            }
            if (-not $lastSeen) { $lastSeen = (Get-WaveeVersionsNow).Detail }
            Record -Phase 'P9' -Name 'packages visible while polling the apply' -Status 'INFO' -Detail $lastSeen
            if ($Drill -eq 'network') {
                Assert-True -Phase 'P9' -Name 'network drill: the apply failed loudly, as designed' -Condition $applied.Failed `
                    -Detail (Get-WaitDetail $applied $staged) -Soft | Out-Null
            }
            else {
                Assert-True -Phase 'P9' -Name 'the update staged' -Condition $applied.Ok `
                    -Detail (Get-WaitDetail $applied $staged) | Out-Null
            }
        }
    }
    else {
        Invoke-Phase 'P9' 'drive the update through App Installer (os)' {
            # Nothing to arm on this path: App Installer is the actor. installOnQuit is forced OFF so the app CANNOT
            # be the one that applied it, which is what makes P11's absence assertions mean something; lastCheckedMs
            # is cleared only so the app on B still performs its own (now redundant) check and logs "up to date".
            Stop-WaveeHard
            Assert-True -Phase 'P9' -Name 'app stopped before touching settings' -Condition (-not (Get-WaveeProcess)) | Out-Null
            Mount-WaveeSettings | Out-Null
            Set-WaveeSetting -Name 'app.update.installOnQuit' -Type 'REG_DWORD' -Value '0'
            Remove-WaveeSetting -Name 'app.update.lastCheckedMs'
            Record -Phase 'P9' -Name 'settings armed (installOnQuit OFF)' -Status 'PASS' -Detail ('root ' + $script:SettingsRoot)
            Dismount-WaveeSettings

            $script:UpdateMarkTime = Get-Date
            $script:UpdateMark = Get-LogMark
            Record -Phase 'P9' -Name 'log mark taken' -Status 'INFO' -Detail (Format-LogMark $script:UpdateMark)

            $script:LogSnapAtMark = @(Get-FileSnapshot (Get-PackageLogDir))
            $script:HeliumAtMark = Get-HeliumStamp
            Record -Phase 'P9' -Name 'package LocalCache logs at the mark' -Status 'INFO' `
                -Detail (Format-FileSnapshot $script:LogSnapAtMark (Get-PackageLogDir))
            Record -Phase 'P9' -Name 'Helium User.dat at the mark' -Status 'INFO' -Detail $script:HeliumAtMark

            # The activation is the CHECK, not the update. The template carries OnLaunch HoursBetweenUpdateChecks="0"
            # ShowPrompt="false" UpdateBlocksActivation="false": App Installer sees the association, sees the feed
            # has moved, STAGES B in the background while A runs, and its immediate RegisterByPackageFullName fails
            # with 0x80073D02 (package in use - A is the running package). The user is never prompted and never
            # blocked. The staged B is registered the next time the package is not in use: on A's exit or at the next
            # activation, before Wavee's own code runs. (With ShowPrompt="true" the OLD template blocked the launch
            # behind a blank App Installer window and registered B first - the very window this run retired.)
            $osTimeout = [Math]::Max(180, $LaunchTimeoutSec)
            $p = Start-Wavee -Phase 'P9' -TimeoutSec $osTimeout
            Assert-True -Phase 'P9' -Name 'Wavee relaunched (A; App Installer checks in the background)' -Condition ($null -ne $p) | Out-Null

            # 1. the check + the silent stage. Proof = the deployment engine fetched the feed on this activation and a
            #    StageByAppInstallerOperation for B finished (event 603 + 400 "Stage").
            $stageDeadline = (Get-Date).AddSeconds($osTimeout)
            $staged = @()
            while ((Get-Date) -lt $stageDeadline) {
                $staged = @(Get-AppxDeploymentEvents -Since $script:UpdateMarkTime -Ids @(400) -Match ($IdentityName + '_' + $QuadB) |
                            Where-Object { "$($_.Message)" -match 'Stage operation' })
                if ($staged.Count -gt 0) { break }
                Start-Sleep -Seconds 3
            }
            $checkHits = @(Get-FeedAssociationRequests -Rows (Get-FeedRequests) -Mark $script:UpdateMarkTime -FeedPath $script:FeedUri)
            Assert-True -Phase 'P9' -Name 'App Installer checked the feed on launch (App Virt Client GET)' -Condition ($checkHits.Count -gt 0) `
                -Detail ('' + $checkHits.Count + ' GET(s) since the mark') | Out-Null
            Assert-True -Phase 'P9' -Name 'App Installer staged B silently while A ran (Stage operation finished)' -Condition ($staged.Count -gt 0) `
                -Detail $(if ($staged.Count -gt 0) { '{0:HH:mm:ss} {1}' -f $staged[0].TimeCreated, (("$($staged[0].Message)" -replace '\s+', ' ').Substring(0, 120)) } else { 'no Stage 400 for B since the mark' }) | Out-Null
            Record-EventRows -Phase 'P9' -Label 'register attempt while A was running (0x80073D02 = in use, expected)' `
                -Events (Get-AppxDeploymentEvents -Since $script:UpdateMarkTime -Ids @(401, 404) -Match ($IdentityName + '_' + $QuadB)) -Max 3 | Out-Null

            # 2. the app was NOT touched: still A, still running, and it was never asked anything.
            $stillA = @(Get-WaveeProcess | Where-Object { "$($_.Path)" -like ('*' + $IdentityName + '_' + $QuadA + '_*') })
            Assert-True -Phase 'P9' -Name 'A kept running, uninterrupted, while B was staged' -Condition ($stillA.Count -gt 0) `
                -Detail ('' + $stillA.Count + ' A process(es)') | Out-Null

            # 3. close A and let the deferred registration land: on exit, or - failing that - on the next activation,
            #    which App Installer intercepts to register the staged package before the app runs.
            foreach ($proc in @(Get-WaveeProcess)) { try { $proc.CloseMainWindow() | Out-Null } catch { } }
            Wait-WaveeExit -TimeoutSec 60 | Out-Null
            Assert-True -Phase 'P9' -Name 'A closed on request' -Condition (-not (Get-WaveeProcess)) | Out-Null
            $regDeadline = (Get-Date).AddSeconds(90)
            $quadNow = Get-WaveeQuad
            while ($quadNow -ne $QuadB -and (Get-Date) -lt $regDeadline) { Start-Sleep -Seconds 3; $quadNow = Get-WaveeQuad }
            Record -Phase 'P9' -Name 'registered version after A exited' -Status 'INFO' -Detail ('' + $quadNow + $(if ($quadNow -eq $QuadB) { ' (registered on exit)' } else { ' (still A: registration lands at the next activation)' }))

            $p = Start-Wavee -Phase 'P9' -TimeoutSec $osTimeout
            Assert-True -Phase 'P9' -Name "Wavee relaunched (up to ${osTimeout}s: the staged B registers first)" -Condition ($null -ne $p) | Out-Null
            $updated = 'updated: ' + [regex]::Escape($QuadA) + ' -> ' + [regex]::Escape($QuadB)
            $hit = Wait-LogLine -Pattern $updated -Mark $script:UpdateMark -TimeoutSec $osTimeout
            Assert-True -Phase 'P9' -Name 'the app opened already updated' -Condition $hit.Ok `
                -Detail (Get-WaitDetail $hit $updated) | Out-Null
        }
    }

    # -- P10 verify B ------------------------------------------------------------------------------------------------
    Invoke-Phase 'P10' 'verify Windows installed B' {
        # On the OS path the app is already running ON B; waiting for it to exit would just burn the timeout. On the
        # in-app path the same is true the moment Windows has relaunched B (RegisterApplicationRestart): only a
        # process still running FROM A's package folder is worth waiting for.
        if ($Scenario -eq 'inapp') {
            $stillA = @(Get-WaveeProcess | Where-Object { "$($_.Path)" -like ('*' + $IdentityName + '_' + $QuadA + '_*') })
            if ($stillA.Count -gt 0) { Wait-WaveeExit -TimeoutSec 120 | Out-Null }
            else { Record -Phase 'P10' -Name 'A has already exited' -Status 'INFO' -Detail 'no process runs from A''s package folder; not waiting' }
        }
        Copy-WaveeLog 'A-final' | Out-Null

        # The window every "what happened during the apply" query is asked over. MinValue when P9 never got to its
        # mark; fall back to the start of the run rather than asking Windows for every event since 0001-01-01.
        $since = $script:UpdateMarkTime
        if ($since -le [DateTime]::MinValue) { $since = $script:StartedAt }

        $deadline = (Get-Date).AddSeconds(120)
        $quad = Get-WaveeQuad
        while (-not (Test-QuadMatch $quad $QuadB) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $quad = Get-WaveeQuad
        }
        Record -Phase 'P10' -Name 'packages visible now' -Status 'INFO' -Detail (Get-WaveeVersionsNow).Detail

        # The app's own storage, after the update. The snapshot rows and the Helium stamp are recorded whatever the
        # verdict: they are the before/after pair a human needs when the assertion below fires.
        $snapNow = @(Get-FileSnapshot (Get-PackageLogDir))
        Record -Phase 'P10' -Name 'package LocalCache logs now' -Status 'INFO' `
            -Detail (Format-FileSnapshot $snapNow (Get-PackageLogDir))
        Record -Phase 'P10' -Name 'Helium User.dat now' -Status 'INFO' -Detail (Get-HeliumStamp)
        Record -Phase 'P10' -Name 'Helium User.dat at the P9 mark' -Status 'INFO' -Detail $script:HeliumAtMark

        # HARD. An append-only log can only grow, so a file that is gone or smaller than it was at the P9 mark
        # means the update discarded the app's data - which is also, silently, why every log wait keyed to that
        # mark stopped being able to see anything.
        $survived = Compare-LogSnapshot -Before $script:LogSnapAtMark -After $snapNow
        if (@($script:LogSnapAtMark).Count -eq 0) {
            Record -Phase 'P10' -Name 'app data survived the update' -Status 'SKIP' `
                -Detail 'nothing was snapshotted at the P9 mark'
        }
        else {
            Assert-True -Phase 'P10' -Name 'app data survived the update' -Condition $survived.Ok `
                -Detail $survived.Detail | Out-Null
        }

        # What the app says it is, and whether Windows relaunched it. Always-on app log lines, so they cost one
        # grep of the newest log and answer "which identity / which app data root was this run actually using".
        $identity = Get-LastLogMatch 'identity: '
        if (-not $identity) { $identity = '(no "identity: " line in the newest wavee*.log)' }
        Record -Phase 'P10' -Name 'app identity line' -Status 'INFO' -Detail $identity
        $relaunched = Get-LastLogMatch '\[app\] startup - relaunched by Windows after an update'
        if (-not $relaunched) { $relaunched = '(not present)' }
        Record -Phase 'P10' -Name 'relaunched-by-Windows line' -Status 'INFO' -Detail $relaunched

        # Windows' own account of the apply. The app cannot log its own hang, and a WER kill looks exactly like a
        # clean exit from inside the package.
        $faults = Record-EventRows -Phase 'P10' -Label 'Application-log fault event' -Events (Get-WaveeFaultEvents -Since $since)
        Record-EventRows -Phase 'P10' -Label 'AppXDeploymentServer event' `
            -Events (Get-AppxDeploymentEvents -Since $since -Ids @(603, 400, 401, 404)) | Out-Null

        # HARD, in-app only. The quit-time apply used to run on the UI thread: the message pump stopped, Windows
        # declared the window unresponsive, and WER killed the process mid-update (MoAppHang + Application 1002).
        # On the OS path the app is not the actor, so there is nothing here to assert.
        if ($Scenario -eq 'inapp') {
            $hangs = @($faults | Where-Object { "$($_.ProviderName)" -eq 'Application Hang' })
            $hangDetail = 'no Application Hang event mentions Wavee since the P9 mark'
            if ($hangs.Count -gt 0) { $hangDetail = ('' + $hangs.Count + ' hang(s): ' + (Format-EventLine $hangs[0])) }
            Assert-True -Phase 'P10' -Name 'no Application Hang for Wavee during the apply' `
                -Condition ($hangs.Count -eq 0) -Detail $hangDetail | Out-Null
        }

        if ($Drill -eq 'network') {
            Assert-True -Phase 'P10' -Name 'network drill: still on A (nothing half-installed)' `
                -Condition (Test-QuadMatch $quad $QuadA) -Detail $quad -Soft | Out-Null
            return
        }
        Assert-True -Phase 'P10' -Name 'installed version is B' -Condition (Test-QuadMatch $quad $QuadB) -Detail $quad | Out-Null

        $reqs = Get-FeedRequests
        $engine = @($reqs | Where-Object {
            $_.Path -like ('*/pkg/Wavee_' + $QuadB + '_*') -and ($_.Status -eq 200 -or $_.Status -eq 206) -and
            ($_.UserAgent -notlike 'Wavee/*')
        })
        Assert-True -Phase 'P10' -Name 'the deployment engine (not the app) downloaded B' -Condition ($engine.Count -gt 0) `
            -Detail (($engine | Select-Object -First 1 | ForEach-Object { $_.UserAgent })) | Out-Null
        $partial = @($engine | Where-Object { $_.Status -eq 206 })
        Assert-True -Phase 'P10' -Name 'at least one Range (206) response' -Condition ($partial.Count -gt 0) -Soft `
            -Detail "$($partial.Count) of $($engine.Count)" | Out-Null

        # The association is the other half of the in-app story: A was installed bare, so the ONLY thing that could
        # have created one is ApplyFromAppInstallerAsync(FeedUrl) - the app handing the feed URI to the deployment
        # engine. On the OS path it was already there and must have survived.
        #
        # THREE proofs, ANY of which is enough, and the detail says which held. Run 3 failed this on the cmdlet
        # alone while the AppXDeploymentServer log showed UpdateUsingAppInstallerOperation (603) on every launch of
        # B and the feed log showed an "App Virt Client" GET of the .appinstaller at each of them: the association
        # existed and the cmdlet simply would not say so.
        $ev = Get-AssociationEvidence -Since $since
        Record -Phase 'P10' -Name 'association probe: cmdlet' -Status 'INFO' -Detail $ev.Cmdlet.Detail
        $feedRow = '' + $ev.FeedHits.Count + ' App Virt Client GET(s) of ' + (ConvertTo-FeedPath $script:FeedUri)
        if ($ev.FeedHits.Count -gt 0) { $feedRow = $feedRow + '; first at ' + "$($ev.FeedHits[0].Time)" }
        Record -Phase 'P10' -Name 'association probe: feed log' -Status 'INFO' -Detail $feedRow
        $evRow = '' + $ev.EventHits.Count + ' UpdateUsingAppInstallerOperation (603) event(s)'
        if ($ev.EventHits.Count -gt 0) { $evRow = $evRow + '; ' + (Format-EventLine $ev.EventHits[0]) }
        Record -Phase 'P10' -Name 'association probe: AppXDeploymentServer 603' -Status 'INFO' -Detail $evRow

        $name = 'applying the update created the AppInstallerUri association'
        if ($Scenario -eq 'os') { $name = 'the AppInstallerUri association survived the update' }
        Assert-True -Phase 'P10' -Name $name -Condition $ev.Associated -Soft -Detail $ev.Detail | Out-Null
    }

    # -- P11 relaunch ------------------------------------------------------------------------------------------------
    Invoke-Phase 'P11' 'relaunch on B and read the after-update plate' {
        if ($Drill -eq 'network') {
            Record -Phase 'P11' -Name 'skipped for the network drill' -Status 'SKIP'
            return
        }
        # NO new mark. The "updated:" line is written in the constructor of the FIRST process to run on B, and on
        # both paths that process may already have come and gone (WER's RegisterApplicationRestart on the in-app
        # path; the OS path relaunches into B directly). Everything here reads from P9's mark.
        $mark = $script:UpdateMark
        if ($null -eq $mark) {
            Record -Phase 'P11' -Name 'no P9 log mark' -Status 'WARN' `
                -Detail 'P9 died before marking the log; searching every log file from byte 0'
        }
        Start-Wavee -Phase 'P11' -TimeoutSec 120 | Out-Null

        $updated = 'updated: ' + [regex]::Escape($QuadA) + ' -> ' + [regex]::Escape($QuadB)
        $hit = Wait-LogLine -Pattern $updated -Mark $mark -TimeoutSec 120
        Assert-True -Phase 'P11' -Name 'the app reports the update it just took' -Condition $hit.Ok `
            -Detail (Get-WaitDetail $hit $updated) | Out-Null

        $shot = Save-WindowShot '02-after-update.png'
        $luma = Get-ShotMeanLuma $shot
        Record -Phase 'P11' -Name 'after-update screenshot' -Status 'INFO' -Detail "$shot (luma $luma)"
        # The after-update plate is raised by AfterUpdateChrome, which is mounted INSIDE WaveeShell - the signed-in
        # shell. This harness never signs in (the app sits on the device-code page), so the plate cannot appear here
        # and its arming flag must stay ARMED for the next signed-in launch; that is the assertion below. The luma is
        # kept as an observation only.
        Record -Phase 'P11' -Name 'after-update plate' -Status 'SKIP' `
            -Detail ("needs the signed-in shell (AfterUpdateChrome mounts in WaveeShell); luma baseline " + $script:BaselineLuma + ' -> ' + $luma)

        # B's own launch check is OPTIONAL by design: AppUpdateScheduler skips it when the last check is under an
        # hour old (LaunchCheckCooldownMs), and on the in-app path A checked the feed seconds before Windows
        # relaunched B. So: wait a bounded 45 s for the line, and if it never comes, read app.update.lastCheckedMs
        # after the stop below - a check inside the cooldown window is the app behaving, not a missing check.
        $upToDate = 'up to date: feed ' + [regex]::Escape($QuadB) + ', running ' + [regex]::Escape($QuadB)
        $hit2 = Wait-LogLine -Pattern $upToDate -Mark $mark -TimeoutSec ([Math]::Min(45, $CheckTimeoutSec))
        $script:BCheckHit = $hit2

        # The same two Windows-side ledgers as P10, re-read now that B has actually been run: a hang or a WER kill
        # on the relaunch belongs to the relaunch, and a 603 here is the association firing on THIS activation.
        $since11 = $script:UpdateMarkTime
        if ($since11 -le [DateTime]::MinValue) { $since11 = $script:StartedAt }
        Record-EventRows -Phase 'P11' -Label 'Application-log fault event' `
            -Events (Get-WaveeFaultEvents -Since $since11) | Out-Null
        Record-EventRows -Phase 'P11' -Label 'AppXDeploymentServer event' `
            -Events (Get-AppxDeploymentEvents -Since $since11 -Ids @(603, 400, 401, 404)) | Out-Null

        if ($Scenario -eq 'os') {
            # The evidence that the OS - not the app - APPLIED this. Under the silent template the app keeps running
            # while B is staged, so its own 30-second checker legitimately sees B too and may say "update available";
            # that is an observation, not a failure. What must be absent is the app's own apply: install-on-quit.
            $noOffer = Test-LogAbsent -Pattern 'update available: ' -Mark $mark
            Record -Phase 'P11' -Name 'the app''s own checker also saw B (expected: it kept running while B staged)' -Status 'INFO' `
                -Detail $(if ($noOffer.Absent) { 'no "update available" line' } else { $noOffer.Line })
            $noQuit = Test-LogAbsent -Pattern 'install-on-quit' -Mark $mark
            Assert-True -Phase 'P11' -Name 'install-on-quit never ran' -Condition $noQuit.Absent -Detail $noQuit.Line | Out-Null
        }

        Stop-WaveeHard
        Mount-WaveeSettings | Out-Null
        $previous = Get-WaveeSettingText -Name 'app.whatsnew.previousVersion'
        $pending = Get-WaveeSettingText -Name 'app.whatsnew.pendingFrom'
        $lastRun = Get-WaveeSettingText -Name 'app.lastRunVersion'
        if ($script:BCheckHit.Ok) {
            Assert-True -Phase 'P11' -Name 'B checks the feed and is up to date' -Condition $true -Detail $script:BCheckHit.Line | Out-Null
        }
        else {
            $lastCheckedMs = 0
            try { $lastCheckedMs = [long](Get-WaveeSettingText -Name 'app.update.lastCheckedMs') } catch { $lastCheckedMs = 0 }
            $ageSec = -1
            if ($lastCheckedMs -gt 0) { $ageSec = [int](([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - $lastCheckedMs) / 1000) }
            $inCooldown = ($ageSec -ge 0 -and $ageSec -lt 3600)
            if ($inCooldown) {
                Record -Phase 'P11' -Name 'B skipped its launch check (1-hour cooldown: A checked ' + $ageSec + ' s ago) - by design' -Status 'INFO' -Detail 'AppUpdateScheduler.LaunchCheckCooldownMs'
            }
            else {
                Assert-True -Phase 'P11' -Name 'B checks the feed and is up to date' -Condition $false `
                    -Detail ((Get-WaitDetail $script:BCheckHit $upToDate) + '; lastCheckedMs age ' + $ageSec + ' s (outside the cooldown, so a check was due)') | Out-Null
            }
        }
        Assert-True -Phase 'P11' -Name 'previousVersion == A' -Condition ($previous -eq $QuadA) -Detail "'$previous'" | Out-Null
        Assert-True -Phase 'P11' -Name 'pendingFrom stays armed for the next signed-in launch (== A)' -Condition ($pending -eq $QuadA) -Detail "'$pending'" | Out-Null
        Assert-True -Phase 'P11' -Name 'lastRunVersion == B' -Condition ($lastRun -eq $QuadB) -Detail "'$lastRun'" | Out-Null
        Record -Phase 'P11' -Name 'lastSeenVersion' -Status 'INFO' -Detail (Get-WaveeSettingText -Name 'app.whatsnew.lastSeenVersion')
        Dismount-WaveeSettings
    }

    # -- P12 drills --------------------------------------------------------------------------------------------------
    Invoke-Phase 'P12' ('drill: ' + $Drill) {
        if ($Drill -eq 'downgrade') {
            # Point the feed back at A. The IN-APP checker must refuse to "update" backwards (it compares versions);
            # the OS path may still apply A on a launch, because ForceUpdateFromAnyVersion allows it - that is the
            # documented rollback mechanism, so it is a soft observation, not a failure.
            Publish-LocalFeed -Quad $QuadA -Semver $SemverA -Msix $script:MsixA -NotesDir $script:NotesOutA `
                -IndexFile $script:IndexA `
                -Index @((New-IndexEntry -Semver $SemverB -Quad $QuadB), (New-IndexEntry -Semver $SemverA -Quad $QuadA)) | Out-Null
            # The launch check has a cooldown and B has just used it up; clear it or the relaunch below checks nothing.
            Stop-WaveeHard
            Mount-WaveeSettings | Out-Null
            Remove-WaveeSetting -Name 'app.update.lastCheckedMs'
            Dismount-WaveeSettings
            $mark = Get-LogMark
            Start-Wavee -Phase 'P12' | Out-Null
            $refuse = 'up to date: feed ' + [regex]::Escape($QuadA) + ', running ' + [regex]::Escape($QuadB)
            $hit = Wait-LogLine -Pattern $refuse -Mark $mark -TimeoutSec $CheckTimeoutSec
            Assert-True -Phase 'P12' -Name 'the in-app checker refuses to go backwards' -Condition $hit.Ok `
                -Detail (Get-WaitDetail $hit $refuse) | Out-Null
            Stop-WaveeHard
            for ($i = 0; $i -lt 2; $i++) {
                Start-Wavee -Phase 'P12' -TimeoutSec 180 | Out-Null
                Start-Sleep -Seconds 20
                Stop-WaveeHard
            }
            Assert-True -Phase 'P12' -Name 'the OS rolled the package back to A' `
                -Condition (Test-QuadMatch (Get-WaveeQuad) $QuadA) -Soft -Detail (Get-WaveeVersionsNow).Detail | Out-Null
        }
        elseif ($Drill -eq 'snooze') {
            Record -Phase 'P12' -Name 'snooze drill armed in P9 (snoozedVersion == B)' -Status 'INFO'
        }
        elseif ($Drill -eq 'network') {
            Record -Phase 'P12' -Name 'network drill asserted in P9 and P10' -Status 'INFO'
        }
        elseif ($Scenario -eq 'os') {
            Record -Phase 'P12' -Name 'drills are in-app only' -Status 'SKIP' -Detail 'App Installer is the actor on -Scenario os'
        }
        else {
            Record -Phase 'P12' -Name 'no drill requested' -Status 'SKIP'
        }
    }
}
finally {

    # -- P13 cleanup -------------------------------------------------------------------------------------------------
    Write-Host ''
    Write-Host '== P13  cleanup' -ForegroundColor Cyan
    try { Stop-WaveeHard } catch { }
    try { Copy-WaveeLog 'B-final' | Out-Null } catch { }
    try { Dismount-WaveeSettings } catch { }
    try {
        if (-not $KeepFeed) {
            foreach ($p in @(Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue)) {
                try { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction SilentlyContinue } catch { }
            }
        }
    }
    catch { }
    try {
        if ($script:Server) { Stop-LocalFeedServer $script:Server }
        $script:Server = $null
    }
    catch { }
    try {
        if (Test-Path -LiteralPath $script:FeedLog) {
            Copy-Item -LiteralPath $script:FeedLog -Destination (Join-Path $script:OutDirFull 'feed-requests.log') -Force
        }
    }
    catch { }
    try {
        if (-not $KeepFeed -and (Test-Path -LiteralPath $script:FeedDirFull)) {
            Remove-Item -LiteralPath $script:FeedDirFull -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
    if ($RemoveCert) {
        try {
            $cerA = [IO.Path]::ChangeExtension($script:MsixA, '.cer')
            if (Test-Path -LiteralPath $cerA) {
                $thumb = (Get-PfxCertificate -FilePath $cerA).Thumbprint
                Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' | Where-Object { $_.Thumbprint -eq $thumb } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }

    # -- results -----------------------------------------------------------------------------------------------------
    # The last row says what was actually measured and where to look, so a pasted table is self-describing: the two
    # scenarios prove different things and a table without its scenario is unreadable a day later.
    $notesSaid = 'authored'
    if ($script:NotesOutA -and $script:NotesOutA -ne $NotesA) { $notesSaid = 'emitted' }
    $shots = @()
    foreach ($n in @('01-baseline.png', '02-after-update.png')) {
        if (Test-Path -LiteralPath (Join-Path $script:OutDirFull $n)) { $shots += $n }
    }
    $summary = ("scenario $Scenario / driver $Driver / drill $Drill; " +
        "A $QuadA ($SemverA) -> B $QuadB ($SemverB), $Arch; notes $notesSaid; " +
        "artefacts $($script:OutDirFull)")
    if ($shots.Count -gt 0) { $summary = $summary + ' (' + ($shots -join ', ') + ')' }
    Record -Phase 'P13' -Name 'run summary' -Status 'INFO' -Detail $summary

    $fails = @($script:Results | Where-Object { $_.Status -eq 'FAIL' })
    $warns = @($script:Results | Where-Object { $_.Status -eq 'WARN' })
    $passes = @($script:Results | Where-Object { $_.Status -eq 'PASS' })
    Write-Host ''
    Write-Host '================================================================================'
    $script:Results | Format-Table -AutoSize Phase, Status, Check, Detail | Out-String -Width 200 | Write-Host
    $elapsed = [int]((Get-Date) - $script:StartedAt).TotalSeconds
    $overall = 'PASS'
    if ($fails.Count -gt 0) { $overall = 'FAIL' }
    $color = 'Green'
    if ($overall -eq 'FAIL') { $color = 'Red' }
    Write-Host ("OVERALL: $overall   pass $($passes.Count)  warn $($warns.Count)  fail $($fails.Count)   ${elapsed}s") -ForegroundColor $color
    Write-Host ("artefacts: $($script:OutDirFull)")
    Write-Host ("transcript: $LogPath")
    try { Stop-Transcript | Out-Null } catch { }
    exit $fails.Count
}
