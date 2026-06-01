[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Command,

    [string] $Ref,
    [string] $VersionTag,

    [switch] $Production,
    [switch] $PreRelease,
    [switch] $Status,
    [switch] $Watch,
    [switch] $DryRun,
    [switch] $Yes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    # Windows PowerShell 5.1 turns a native command's stderr (e.g. git fetch's
    # benign "From github.com:..." progress) into a terminating NativeCommandError
    # under $ErrorActionPreference='Stop' when merged via 2>&1 — aborting before
    # the $LASTEXITCODE check. Capture under 'Continue' so the real exit code
    # drives success/failure (no-op under pwsh, where this never terminated).
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @Arguments 2>&1
    }
    finally {
        $ErrorActionPreference = $eap
    }
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $output"
    }

    return ($output | Out-String).Trim()
}

function Invoke-Gh {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    Write-Host "> gh $($Arguments -join ' ')"
    if ($DryRun) {
        return
    }

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Confirm-ReleaseAction {
    param([Parameter(Mandatory = $true)][string] $Message)

    if ($Yes -or $DryRun) {
        return
    }

    $answer = Read-Host "$Message Type YES to continue"
    if ($answer -ne "YES") {
        throw "Cancelled."
    }
}

function Get-CurrentBranch {
    return Invoke-Git -Arguments @("branch", "--show-current")
}

function Resolve-ReleaseRef {
    param([string] $Candidate)

    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        return $Candidate.Trim()
    }

    $current = Get-CurrentBranch
    if ($current -like "release/*") {
        return $current
    }

    throw "No release branch specified. Run from release/<X.Y.Z>-<label> or pass -Ref release/<X.Y.Z>-<label>."
}

function Parse-ReleaseRef {
    param([Parameter(Mandatory = $true)][string] $ReleaseRef)

    $match = [regex]::Match($ReleaseRef, "^release/(?<base>\d+\.\d+\.\d+)-(?<label>[A-Za-z][A-Za-z0-9.-]*)$")
    if (-not $match.Success) {
        throw "Invalid release ref '$ReleaseRef'. Expected release/<X.Y.Z>-<label>, e.g. release/0.1.0-alpha."
    }

    return [pscustomobject]@{
        Base = $match.Groups["base"].Value
        Label = $match.Groups["label"].Value
    }
}

function Get-NextPreReleaseTag {
    param(
        [Parameter(Mandatory = $true)][string] $Base,
        [Parameter(Mandatory = $true)][string] $Label
    )

    Invoke-Git -Arguments @("fetch", "--tags", "--force", "origin") | Out-Null
    $tags = & git tag -l "v$Base-$Label.*"
    if ($LASTEXITCODE -ne 0) {
        throw "git tag lookup failed."
    }

    $last = 0
    foreach ($tag in $tags) {
        $match = [regex]::Match($tag, "^v$([regex]::Escape($Base))-$([regex]::Escape($Label))\.(?<n>\d+)$")
        if ($match.Success) {
            $n = [int]$match.Groups["n"].Value
            if ($n -gt $last) {
                $last = $n
            }
        }
    }

    return "v$Base-$Label.$($last + 1)"
}

function Start-PreRelease {
    param([string] $ReleaseRef)

    $releaseRef = Resolve-ReleaseRef $ReleaseRef
    $parsed = Parse-ReleaseRef $releaseRef
    $nextTag = Get-NextPreReleaseTag -Base $parsed.Base -Label $parsed.Label

    Write-Host "Pre-release milestone:"
    Write-Host "  ref:      $releaseRef"
    Write-Host "  next tag: $nextTag"
    Write-Host "  channel:  experimental-latest"
    Confirm-ReleaseAction "This publishes $nextTag to testers."

    Invoke-Gh -Arguments @("workflow", "run", "release.yml", "--ref", $releaseRef)
    if ($DryRun) {
        Write-Host "Dry run complete; release workflow was not queued."
    }
    else {
        Write-Host "Queued release workflow for $releaseRef. Use './eng/release.ps1 status' or './eng/release.ps1 watch'."
    }
}

function Start-ProductionRelease {
    param([string] $Tag)

    if ([string]::IsNullOrWhiteSpace($Tag)) {
        throw "Production releases require -VersionTag v<X.Y.Z>, e.g. ./eng/release.ps1 production -VersionTag v0.1.0 -Yes"
    }

    if ($Tag -notmatch "^v\d+\.\d+\.\d+$") {
        throw "Production VersionTag must be stable semver, e.g. v0.1.0. Got '$Tag'."
    }

    Write-Host "Production release:"
    Write-Host "  ref:         master"
    Write-Host "  version_tag: $Tag"
    Write-Host "  release:     draft"
    Confirm-ReleaseAction "This queues production draft $Tag from master."

    Invoke-Gh -Arguments @("workflow", "run", "release.yml", "--ref", "master", "-f", "version_tag=$Tag")
    if ($DryRun) {
        Write-Host "Dry run complete; production workflow was not queued."
    }
    else {
        Write-Host "Queued production workflow for $Tag. Review the draft before publishing."
    }
}

function Show-ReleaseStatus {
    Invoke-Gh -Arguments @("run", "list", "--workflow", "release.yml", "--limit", "5")
}

function Watch-LatestReleaseRun {
    $runId = & gh run list --workflow release.yml --limit 1 --json databaseId --jq ".[0].databaseId"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($runId)) {
        throw "Could not find a release workflow run to watch."
    }

    Invoke-Gh -Arguments @("run", "watch", $runId.Trim(), "--exit-status")
}

$repoRoot = Invoke-Git -Arguments @("rev-parse", "--show-toplevel")
Set-Location $repoRoot

$text = (($Command | Where-Object { $_ }) -join " ").Trim()
$lower = $text.ToLowerInvariant()

if (-not $Ref -and $text -match "release/\d+\.\d+\.\d+-[A-Za-z][A-Za-z0-9.-]*") {
    $Ref = $matches[0]
}

if (-not $VersionTag -and $text -match "\bv\d+\.\d+\.\d+\b") {
    $VersionTag = $matches[0]
}

if ($lower -match "\b(watch|tail)\b") {
    $Watch = $true
}
elseif ($lower -match "\b(status|runs?|check)\b") {
    $Status = $true
}

if ($lower -match "\b(prod|production|stable|ship)\b") {
    $Production = $true
}
elseif ($lower -match "\b(pre|pre-release|prerelease|publish|release|drop|cut|milestone|alpha|beta|rc)\b") {
    $PreRelease = $true
}

if ($Watch) {
    Watch-LatestReleaseRun
    exit 0
}

if ($Status) {
    Show-ReleaseStatus
    exit 0
}

if ($Production) {
    Start-ProductionRelease -Tag $VersionTag
    exit 0
}

if ($PreRelease -or (Get-CurrentBranch) -like "release/*") {
    Start-PreRelease -ReleaseRef $Ref
    exit 0
}

@"
Wavee release runner

Examples:
  ./eng/release.ps1 publish -Yes
  ./eng/release.ps1 "cut milestone on release/0.1.0-alpha" -Yes
  ./eng/release.ps1 production -VersionTag v0.1.0 -Yes
  ./eng/release.ps1 status
  ./eng/release.ps1 watch

Safety:
  - Pre-releases must run from or specify release/<X.Y.Z>-<label>.
  - Production requires -VersionTag v<X.Y.Z> and creates a draft.
  - Without -Yes, publishing prompts for confirmation.
"@ | Write-Host
