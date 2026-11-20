#requires -Version 5.1
<#
    ReleaseRunner.psm1 - the steps, the state, the preflight items and the git/gh helpers behind
    ops\release\release.ps1, Wavee's release runner.

    The rules (copied from ISOPlanner's production runner):
      - a plain run is a dry run: the preflight (read-only) and the commands of every step; nothing changes;
      - -Go acts; a step that reality reports as done is skipped, so a second -Go continues where the first stopped;
      - every step decides from reality first (git, GitHub, the feed, the staged files) and from the state second;
      - exit codes: 0 done, 1 usage or config, 2 preflight refused (nothing changed), 3 stopped on a condition,
        4 paused;
      - the state lives in %LOCALAPPDATA%\WaveeRelease\<version> (state.json, one log per step, runner.lock), never in
        %LOCALAPPDATA%\Wavee, which the app's E2E harness wipes.

    A -Go run started from any checkout makes (or reuses) the release worktree C:\WAVEE\release\<version>\WaveeMusic
    (a git worktree at origin/release/<version>) and C:\WAVEE\release\<version>\fluent-gpu (a detached engine worktree
    at engine.lock), links the PlayPlay junction and the signing config in, and runs the worktree's own release.ps1
    with -InWorktree. Every step runs inside the worktree.

    Style rules: Windows PowerShell 5.1 only, ASCII-only string literals (an em dash is [char]0x2014), UTF-8 without a
    BOM on every file this module writes, no && / || / ternary / ?? .
#>

$ErrorActionPreference = 'Stop'

# Wavee.Store.psm1 imports Wavee.Release.psm1 and Wavee.Build.psm1 (-Global), so all three are in scope after this.
Import-Module (Join-Path $PSScriptRoot 'Wavee.Store.psm1') -Force -DisableNameChecking -Global

$script:EmDash = [char]0x2014

$script:StepNames = @('preflight', 'changelog', 'bump', 'notes', 'tag', 'pack-arm64', 'pack-x64', 'symbols', 'sign',
    'stage', 'push', 'release', 'feed', 'open-next', 'merge-main', 'forward-port', 'store-submit', 'notify', 'store-poll')

# What each step needs done (or not applicable) before it may run. A full -Go runs the steps in order, so this only
# bites with -From / -Only, where it refuses to run a step whose ground is not there yet.
$script:Needs = @{
    'changelog'    = @()
    'bump'         = @('changelog')
    'notes'        = @('bump')
    'tag'          = @('notes')
    'pack-arm64'   = @('tag')
    'pack-x64'     = @('tag')
    'symbols'      = @('pack-arm64', 'pack-x64')
    'sign'         = @('pack-arm64', 'pack-x64')
    'stage'        = @('notes', 'sign')
    'push'         = @('tag', 'symbols', 'stage')
    'release'      = @('push', 'stage')
    'feed'         = @('release')
    'open-next'    = @('feed')
    'merge-main'   = @('feed', 'open-next')
    'forward-port' = @('merge-main')
    'store-submit' = @('feed')
    'notify'       = @('feed')
    'store-poll'   = @('store-submit')
}

$script:Cfg = @{
    Repo           = 'christosk92/WaveeMusic'
    EngineRepo     = 'christosk92/fluent-gpu'
    FeedRelease    = 'wavee-stable'
    BetaFeed       = 'wavee-beta'
    TagPrefix      = 'wavee-v'
    IdentityName   = 'cproducts.Wavee'
    AssetPrefix    = 'Wavee'
    Publisher      = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL'
    Subscription   = 'Azure subscription 1'
    Configuration  = 'Release'
    ReleaseRoot    = 'C:\WAVEE\release'
    OpAccount      = 'my.1password.eu'
    CrashIngestUrl = 'https://crash.cproducts.dev'
    StoreProductId = '9NJPVWTQPT9H'
    BodyCap        = 125000
}

# The paths `changelog assemble` may change; anything else in the tree after it ran stops the step uncommitted.
$script:ChangelogPaths = @('CHANGELOG.md', 'src/apps/Wavee/Theming/Contracts/ContractVersion.cs',
    'docs/themes/contract-history.md', 'CONTRIBUTORS.md')

$script:Ctx = $null
$script:CrashKey = $null

#region Output and logs

function Protect-RunnerText {
    param([AllowNull()][AllowEmptyString()][string]$Text)
    if (-not $Text) { return '' }
    $t = $Text
    if ($script:Ctx -and $script:Ctx.Secrets) {
        foreach ($s in $script:Ctx.Secrets) { if ($s) { $t = $t.Replace($s, '<redacted>') } }
    }
    $t
}

function Add-RunnerLog {
    param([AllowEmptyString()][string]$Text = '')
    $ctx = $script:Ctx
    if (-not $ctx -or -not $ctx.StepLog) { return }
    $dir = Split-Path -Parent $ctx.StepLog
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::AppendAllText($ctx.StepLog, (Protect-RunnerText $Text) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))
}

function Write-RunnerLine {
    param([AllowEmptyString()][string]$Text = '', [string]$Color = '')
    $t = Protect-RunnerText $Text
    Add-RunnerLog $t
    if ($script:Ctx -and $script:Ctx.Quiet) { return }
    if ($Color) { Write-Host $t -ForegroundColor $Color } else { Write-Host $t }
}

# Points the step log at <state>\NN-<step>.log (or <state>\<name>.log for the other modes). A dry run writes no log.
function Set-RunnerStepLog {
    param([Parameter(Mandatory = $true)][string]$Name)
    $ctx = $script:Ctx
    if (-not $ctx.Go -or -not $ctx.StateDir) { $ctx.StepLog = $null; return }
    $i = [array]::IndexOf($script:StepNames, $Name)
    if ($i -ge 0) { $leaf = '{0:00}-{1}.log' -f $i, $Name } else { $leaf = "$Name.log" }
    $ctx.StepLog = Join-Path $ctx.StateDir $leaf
}

function Format-RunnerCommand {
    param([string]$FilePath, [string[]]$ArgumentList = @())
    $parts = @($FilePath) + @($ArgumentList)
    $quoted = @($parts | ForEach-Object {
        $s = "$_"
        if ($s.Length -eq 0 -or $s -match '[\s"]') { '"' + ($s -replace '"', '\"') + '"' } else { $s }
    })
    Protect-RunnerText ($quoted -join ' ')
}

function Format-Utc {
    param([DateTime]$Time)
    $Time.ToUniversalTime().ToString('HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) + 'Z'
}

function Get-UtcStamp { [DateTime]::UtcNow.ToString('o') }

function Format-Sha {
    param([AllowNull()][AllowEmptyString()][string]$Sha)
    if (-not $Sha) { return '-' }
    if ($Sha.Length -gt 7) { return $Sha.Substring(0, 7) }
    $Sha
}

function Write-StepResult {
    param([string]$Step, [string]$Status, [DateTime]$Started, [DateTime]$Finished, [string]$Result)
    $seconds = [int][Math]::Round(($Finished - $Started).TotalSeconds)
    $color = 'Green'
    if ($Status -eq 'STOPPED') { $color = 'Red' } elseif ($Status -in @('PAUSED', 'WAITING')) { $color = 'Yellow' }
    Write-RunnerLine ''
    Write-RunnerLine ('[{0}] {1} {2} ({3} s)' -f $Step, $Status, (Format-Utc $Finished), $seconds) $color
    if ($Result) { Write-RunnerLine "  result : $Result" }
    if ($script:Ctx.StepLog) { Write-RunnerLine "  log    : $($script:Ctx.StepLog)" 'DarkGray' }
}

function Write-RunTable {
    param([System.Collections.IDictionary]$Outcomes, [int]$ExitCode)
    Write-RunnerLine ''
    Write-RunnerLine ('{0,-13} {1,-9} {2}' -f 'step', 'status', 'result')
    foreach ($name in $script:StepNames) {
        $o = $Outcomes[$name]
        $status = 'NOT RUN'
        $result = ''
        if ($o) { $status = $o.Status; $result = $o.Result }
        if ($result.Length -gt 150) { $result = $result.Substring(0, 147) + '...' }
        Write-RunnerLine ('{0,-13} {1,-9} {2}' -f $name, $status, $result)
    }
    Write-RunnerLine "exit code: $ExitCode"
}

#endregion

#region Native commands

# Runs one native command, logs the command line and its output to the step log, and throws on a non-zero exit
# unless -AllowFailure. -Echo also prints the command (every command that changes something is echoed). -Quiet keeps
# the output out of the log (op whoami, az tokens).
function Invoke-RunnerNative {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [switch]$AllowFailure,
        [switch]$Echo,
        [switch]$Quiet)

    $line = Format-RunnerCommand $FilePath $ArgumentList
    if ($Echo) { Write-RunnerLine "  > $line" 'DarkGray' } else { Add-RunnerLog "> $line" }
    $r = Invoke-Native $FilePath $ArgumentList -AllowFailure
    if (-not $Quiet) { foreach ($o in $r.Output) { Add-RunnerLog "    $o" } }
    if ($r.ExitCode -ne 0 -and -not $AllowFailure) {
        $tail = @($r.Output | Select-Object -Last 12) -join ' / '
        throw "$(Split-Path -Leaf $FilePath) exited $($r.ExitCode): $(Protect-RunnerText $tail)"
    }
    $r
}

# Runs a long child (a pack, the Store engine) and streams its output into the step log line by line; -Show also
# prints it. Returns @{ ExitCode; Tail } with the last 40 lines for the failure message.
function Invoke-RunnerChild {
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$ArgumentList = @(), [switch]$Show)

    Write-RunnerLine "  > $(Format-RunnerCommand $FilePath $ArgumentList)" 'DarkGray'
    $tail = New-Object System.Collections.Generic.List[string]
    $quiet = [bool]($script:Ctx -and $script:Ctx.Quiet)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0
    $code = 0
    try {
        & $FilePath @ArgumentList 2>&1 | ForEach-Object {
            $s = Protect-RunnerText ("$_")
            Add-RunnerLog "    $s"
            $tail.Add($s)
            if ($tail.Count -gt 40) { $tail.RemoveAt(0) }
            if ($Show -and -not $quiet) { Write-Host "    $s" -ForegroundColor DarkGray }
        }
        if ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }
    }
    catch [System.Management.Automation.CommandNotFoundException] {
        $code = 127
        $tail.Add("$FilePath : command not found")
    }
    finally { $ErrorActionPreference = $prev }
    @{ ExitCode = $code; Tail = $tail.ToArray() }
}

function Test-RunnerCommand {
    param([string]$Name)
    [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

#endregion

#region git

function Invoke-RepoGit {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure,
        [switch]$Echo,
        [switch]$DropAdvice)

    $r = Invoke-RunnerNative 'git' (@('-C', $Dir) + $Arguments) -AllowFailure -Echo:$Echo
    $lines = @()
    foreach ($o in $r.Output) {
        $s = "$o".Trim()
        if ($s.Length -eq 0) { continue }
        if ($DropAdvice -and $s -match '^(warning|hint):') { continue }
        $lines += $s
    }
    if ($r.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed in $Dir (exit $($r.ExitCode)): $($lines -join ' / ')"
    }
    [pscustomobject]@{ ExitCode = $r.ExitCode; Lines = $lines; Text = ($lines -join "`n") }
}

# git with stdout decoded as UTF-8 (file contents); stderr kept apart.
function Invoke-GitCapture {
    param([Parameter(Mandatory = $true)][string]$Dir, [Parameter(Mandatory = $true)][string[]]$Arguments)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = ((@('-C', $Dir) + $Arguments) | ForEach-Object { ConvertTo-Win32QuotedArgument $_ }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $out = $p.StandardOutput.ReadToEndAsync()
        $err = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        @{ ExitCode = $p.ExitCode; Out = $out.GetAwaiter().GetResult(); Err = $err.GetAwaiter().GetResult() }
    }
    finally { $p.Dispose() }
}

# The text of <Ref>:<Path>, or $null when the ref or the file does not exist.
function Read-GitBlob {
    param([string]$Dir, [string]$Ref, [string]$Path)
    if (-not $Dir -or -not $Ref) { return $null }
    $r = Invoke-GitCapture -Dir $Dir -Arguments @('cat-file', 'blob', "$($Ref):$Path")
    if ($r.ExitCode -ne 0) { return $null }
    $r.Out
}

function Test-GitPath {
    param([string]$Dir, [string]$Ref, [string]$Path)
    if (-not $Dir -or -not $Ref) { return $false }
    (Invoke-RepoGit -Dir $Dir -Arguments @('cat-file', '-e', "$($Ref):$Path") -AllowFailure).ExitCode -eq 0
}

function Test-GitCommit {
    param([string]$Dir, [string]$Sha)
    if (-not $Dir -or -not $Sha) { return $false }
    (Invoke-RepoGit -Dir $Dir -Arguments @('cat-file', '-e', "$Sha^{commit}") -AllowFailure).ExitCode -eq 0
}

function Test-GitAncestor {
    param([string]$Dir, [string]$Ancestor, [string]$Descendant)
    if (-not $Dir -or -not $Ancestor -or -not $Descendant) { return $false }
    (Invoke-RepoGit -Dir $Dir -Arguments @('merge-base', '--is-ancestor', $Ancestor, $Descendant) -AllowFailure).ExitCode -eq 0
}

function Get-GitTagCommit {
    param([string]$Dir, [string]$Tag)
    if (-not $Dir) { return $null }
    $r = Invoke-RepoGit -Dir $Dir -Arguments @('rev-parse', '--verify', '--quiet', "refs/tags/$Tag^{commit}") -AllowFailure
    if ($r.ExitCode -ne 0 -or -not $r.Text) { return $null }
    $r.Text
}

# ls-remote origin as a map: ref name -> sha, with an annotated tag mapped to the commit it peels to.
function Get-RemoteRefMap {
    param([Parameter(Mandatory = $true)][string]$Dir, [Parameter(Mandatory = $true)][string[]]$Patterns)
    $r = Invoke-RepoGit -Dir $Dir -Arguments (@('ls-remote', 'origin') + $Patterns) -AllowFailure
    if ($r.ExitCode -ne 0) { throw "git ls-remote origin failed in $Dir : $($r.Text)" }
    $map = @{}
    foreach ($l in $r.Lines) {
        $parts = $l -split '\s+', 2
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[0-9a-f]{40}$') { continue }
        $name = $parts[1]
        if ($name.EndsWith('^{}')) { $map[$name.Substring(0, $name.Length - 3)] = $parts[0] }
        elseif (-not $map.ContainsKey($name)) { $map[$name] = $parts[0] }
    }
    $map
}

# The 40-character engine sha of an engine.lock text: comment lines start with '#', the first other line is the sha.
function Get-EngineLockSha {
    param([AllowNull()][AllowEmptyString()][string]$Text)
    if (-not $Text) { return $null }
    foreach ($l in ($Text -split "\r?\n")) {
        $s = $l.Trim()
        if (-not $s -or $s.StartsWith('#')) { continue }
        if ($s -match '^[0-9a-f]{40}$') { return $s }
        return $null
    }
    $null
}

# The newest wavee-v<semver> tag below -Version that the ref contains: @{ Tag; Version } or $null.
function Get-PreviousReleaseTag {
    param([string]$Dir, [string]$Ref, [string]$Version)
    if (-not $Ref) { return $null }
    $want = [version]$Version
    $r = Invoke-RepoGit -Dir $Dir -Arguments @('tag', '--merged', $Ref, '--list', "$($script:Cfg.TagPrefix)*") -AllowFailure
    $best = $null
    foreach ($l in $r.Lines) {
        $m = [regex]::Match($l, '^' + [regex]::Escape($script:Cfg.TagPrefix) + '(\d+\.\d+\.\d+)$')
        if (-not $m.Success) { continue }
        $v = [version]$m.Groups[1].Value
        if ($v -ge $want) { continue }
        if ($null -eq $best -or $v -gt $best.Version) { $best = @{ Tag = $l; Version = $v } }
    }
    $best
}

function Get-FullPath {
    param([string]$Path)
    [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-SamePath {
    param([string]$A, [string]$B)
    if (-not $A -or -not $B) { return $false }
    [string]::Equals((Get-FullPath $A), (Get-FullPath $B), [StringComparison]::OrdinalIgnoreCase)
}

#endregion

#region State and lock

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
        return , $a
    }
    $Object
}

function Read-RunnerState {
    param([Parameter(Mandatory = $true)][string]$Dir)
    $path = Join-Path $Dir 'state.json'
    if (-not (Test-Path -LiteralPath $path)) { return @{} }
    $o = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    $h = ConvertTo-StateHashtable $o
    if (-not ($h -is [hashtable])) { throw "the state file is not readable: $path" }
    $h
}

# Writes the state to a temporary file and then replaces state.json, so an interrupted write leaves the old file.
# Only a -Go run writes it.
function Save-RunnerState {
    $ctx = $script:Ctx
    if (-not $ctx -or -not $ctx.Go -or -not $ctx.StateDir) { return }
    if (-not (Test-Path -LiteralPath $ctx.StateDir)) { New-Item -ItemType Directory -Force -Path $ctx.StateDir | Out-Null }
    $path = Join-Path $ctx.StateDir 'state.json'
    $tmp = "$path.tmp"
    [IO.File]::WriteAllText($tmp, (ConvertTo-Json -InputObject $ctx.State -Depth 20), (New-Object System.Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $path) { [IO.File]::Replace($tmp, $path, [NullString]::Value) }
    else { [IO.File]::Move($tmp, $path) }
}

function Get-StateSteps {
    $state = $script:Ctx.State
    if (-not ($state.steps -is [hashtable])) { $state.steps = @{} }
    $state.steps
}

function Get-StepRecord {
    param([string]$Name)
    $rec = (Get-StateSteps)[$Name]
    if ($rec -is [hashtable]) { return $rec }
    $null
}

# The pid in runner.lock while that process (with the same start time) still runs, else $null.
function Get-RunnerLockHolder {
    param([Parameter(Mandatory = $true)][string]$Dir)
    $path = Join-Path $Dir 'runner.lock'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $text = ([IO.File]::ReadAllText($path)).Trim()
    $m = [regex]::Match($text, '^(\d+)(?:\|(\d+))?')
    if (-not $m.Success) { return $null }
    $id = [int]$m.Groups[1].Value
    if ($id -eq $PID) { return $id }
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    if ($m.Groups[2].Success) {
        try { if ($p.StartTime.ToUniversalTime().Ticks -ne [long]$m.Groups[2].Value) { return $null } } catch { }
    }
    $id
}

# Takes runner.lock for this process. A lock whose process is gone is taken over; a live one refuses. -AdoptParent:
# the outer runner that relaunched this one in the release worktree holds it, and this process works under it.
function Enter-RunnerLock {
    param([Parameter(Mandatory = $true)][string]$Dir, [switch]$AdoptParent)
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $holder = Get-RunnerLockHolder -Dir $Dir
    if ($null -ne $holder -and $holder -ne $PID) {
        if ($AdoptParent) {
            $parent = $null
            try { $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId } catch { }
            if ($parent -and $holder -eq $parent) { return 'adopted' }
        }
        return $null
    }
    $path = Join-Path $Dir 'runner.lock'
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $ticks = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes("$PID|$ticks")
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
    }
    catch [System.IO.IOException] { return $null }
    'taken'
}

function Exit-RunnerLock {
    param([Parameter(Mandatory = $true)][string]$Dir)
    $path = Join-Path $Dir 'runner.lock'
    if ((Get-RunnerLockHolder -Dir $Dir) -eq $PID) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

#endregion

#region Context and facts

function Reset-RunnerFacts {
    $script:Ctx.Facts = $null
}

# Everything the steps judge from, read once per step boundary: origin refs, the release ref, the previous tag, the
# bump commit, the version numbers, the tags, the release commit and the engine pin.
function Get-RunnerFacts {
    $ctx = $script:Ctx
    if ($ctx.Facts) { return $ctx.Facts }
    $cfg = $script:Cfg
    $v = $ctx.Version
    $sv = Test-WaveeSemver $v
    $f = @{ Version = $v; Branch = "release/$v"; Tag = "$($cfg.TagPrefix)$v"; Notes = @() }
    $f.NextVersion = "$($sv.Major).$($sv.Minor).$($sv.Patch + 1)"
    $f.NextBranch = "release/$($f.NextVersion)"
    $f.WtExists = Test-Path -LiteralPath (Join-Path $ctx.Wt '.git')
    $f.EwtExists = Test-Path -LiteralPath (Join-Path $ctx.Ewt '.git')
    if ($f.WtExists) { $f.GitDir = $ctx.Wt } else { $f.GitDir = $ctx.Root }
    $f.EngineDir = $null
    if ($f.EwtExists) { $f.EngineDir = $ctx.Ewt }
    elseif ($ctx.EngineRepo -and (Test-Path -LiteralPath (Join-Path $ctx.EngineRepo '.git'))) { $f.EngineDir = $ctx.EngineRepo }

    $f.Remote = Get-RemoteRefMap -Dir $ctx.Root -Patterns @('refs/heads/main', 'refs/heads/release/*', "refs/tags/$($cfg.TagPrefix)*")
    $f.OriginBranchSha = $f.Remote["refs/heads/$($f.Branch)"]
    $f.OriginNextSha = $f.Remote["refs/heads/$($f.NextBranch)"]
    $f.OriginMainSha = $f.Remote['refs/heads/main']
    $f.OriginTagCommit = $f.Remote["refs/tags/$($f.Tag)"]

    $f.Ref = $null
    if ($f.WtExists) { $f.Ref = 'HEAD' }
    elseif ($f.OriginBranchSha -and (Test-GitCommit $ctx.Root $f.OriginBranchSha)) { $f.Ref = $f.OriginBranchSha }
    $f.HeadSha = $null
    if ($f.Ref) { $f.HeadSha = (Invoke-RepoGit -Dir $f.GitDir -Arguments @('rev-parse', "$($f.Ref)^{commit}")).Text }

    $prev = Get-PreviousReleaseTag -Dir $f.GitDir -Ref $f.Ref -Version $v
    $f.PrevTag = $null; $f.PrevVersion = $null; $f.PrevTagCommit = $null
    if ($prev) {
        $f.PrevTag = $prev.Tag
        $f.PrevVersion = "$($prev.Version)"
        $f.PrevTagCommit = Get-GitTagCommit -Dir $f.GitDir -Tag $prev.Tag
    }

    # The bump commit: the newest "release: Wavee <v> <codename> (build <quad>)" since the previous tag.
    $f.BumpCommit = $null
    $f.ChangelogCommit = $null
    if ($f.Ref) {
        $range = $f.Ref
        if ($f.PrevTag) { $range = "$($f.PrevTag)..$($f.Ref)" }
        $log = Invoke-RepoGit -Dir $f.GitDir -Arguments @('log', '--format=%H%x1f%s', '--max-count=3000', $range) -AllowFailure
        $rx = '^release: Wavee ' + [regex]::Escape($v) + ' (?<c>\S+) \(build (?<q>' + [regex]::Escape($sv.Core) + '\.(?<b>\d+))\)$'
        foreach ($l in $log.Lines) {
            $parts = $l -split [char]0x1f, 2
            if ($parts.Count -ne 2) { continue }
            $m = [regex]::Match($parts[1], $rx)
            if ($m.Success -and -not $f.BumpCommit) {
                $f.BumpCommit = @{ Sha = $parts[0]; Subject = $parts[1]; Codename = $m.Groups['c'].Value; Quad = $m.Groups['q'].Value; Build = [int]$m.Groups['b'].Value }
            }
            if (-not $f.ChangelogCommit -and $parts[1] -eq "release: CHANGELOG for $v") { $f.ChangelogCommit = $parts[0] }
        }
    }

    $f.Props = $null
    $propsText = Read-GitBlob -Dir $f.GitDir -Ref $f.Ref -Path 'src/apps/Wavee/Wavee.Version.props'
    if ($propsText) { $f.Props = Get-WaveeVersionProps -Text $propsText }

    if ($f.BumpCommit) {
        $f.Build = $f.BumpCommit.Build
        $f.Codename = $f.BumpCommit.Codename
    }
    elseif ($f.Props) {
        $f.Build = $f.Props.Build + 1
        if ($ctx.Codename) { $f.Codename = $ctx.Codename } else { $f.Codename = $f.Props.Codename }
    }
    else {
        $f.Build = $null
        $f.Codename = $ctx.Codename
    }
    $f.Quad = $null; $f.StoreQuad = $null
    if ($null -ne $f.Build) {
        $f.Quad = ConvertTo-WaveeQuad $v $f.Build
        $f.StoreQuad = ConvertTo-WaveeStoreQuad -Semver $v -Build $f.Build
    }

    # A hotfix: -Hotfix recorded it, or the next patch branch exists on origin and shares only the previous tag with
    # this branch (release/<v> was cut from the shipped tag while the work moved one patch up).
    $f.Hotfix = [bool]$ctx.State.hotfix
    if (-not $f.Hotfix -and $f.OriginNextSha -and $f.PrevTagCommit -and $f.Ref -and (Test-GitCommit $f.GitDir $f.OriginNextSha)) {
        $mb = Invoke-RepoGit -Dir $f.GitDir -Arguments @('merge-base', $f.Ref, $f.OriginNextSha) -AllowFailure
        if ($mb.ExitCode -eq 0 -and $mb.Text -eq $f.PrevTagCommit) { $f.Hotfix = $true }
    }

    $f.LocalTagCommit = Get-GitTagCommit -Dir $f.GitDir -Tag $f.Tag

    $f.ReleaseCommit = $null
    $f.ReleaseCommitSource = ''
    if ($f.OriginTagCommit) { $f.ReleaseCommit = $f.OriginTagCommit; $f.ReleaseCommitSource = "$($f.Tag) on origin" }
    elseif ($f.BumpCommit -and $f.HeadSha) {
        $f.ReleaseCommit = $f.HeadSha
        if ($f.WtExists) { $f.ReleaseCommitSource = 'the release worktree HEAD' } else { $f.ReleaseCommitSource = "origin/$($f.Branch)" }
    }

    $lockRef = $f.Ref
    if ($f.ReleaseCommit -and (Test-GitCommit $f.GitDir $f.ReleaseCommit)) { $lockRef = $f.ReleaseCommit }
    $f.EngineSha = Get-EngineLockSha (Read-GitBlob -Dir $f.GitDir -Ref $lockRef -Path 'engine.lock')

    $f.EngineOriginTagCommit = $null
    $f.EngineLocalTagCommit = $null
    if ($f.EngineDir) {
        try {
            $er = Get-RemoteRefMap -Dir $f.EngineDir -Patterns @("refs/tags/$($f.Tag)")
            $f.EngineOriginTagCommit = $er["refs/tags/$($f.Tag)"]
        }
        catch { $f.Notes += "could not read the engine's origin tags: $($_.Exception.Message)" }
        $f.EngineLocalTagCommit = Get-GitTagCommit -Dir $f.EngineDir -Tag $f.Tag
    }

    $f.Stage = Join-Path $ctx.Wt "artifacts\release\$v"
    $f.NotesOut = Join-Path $f.Stage 'notes'
    $f.StoreLedger = Join-Path $ctx.Wt "artifacts\store\$v\store-state.json"
    $ctx.Facts = $f
    return $f
}

function Get-RunnerFeeds {
    $ctx = $script:Ctx
    if ($null -eq $ctx.FeedList) {
        $list = @($script:Cfg.FeedRelease)
        if (Get-GhRelease $script:Cfg.Repo $script:Cfg.BetaFeed) { $list += $script:Cfg.BetaFeed }
        $ctx.FeedList = $list
    }
    $ctx.FeedList
}

function Get-RunnerOpenPrs {
    $f = Get-RunnerFacts
    if ($null -eq $f.OpenPrs) {
        $t = Invoke-Gh @('pr', 'list', '--repo', $script:Cfg.Repo, '--state', 'open', '--limit', '300', '--json',
            'number,title,baseRefName,headRefName,isCrossRepository,author,url')
        # ConvertFrom-Json (5.1) emits a JSON array as ONE object: assign first, then @() enumerates it.
        $j = ConvertFrom-GhJson $t
        $f.OpenPrs = @($j)
    }
    $f.OpenPrs
}

# A PR the runner itself opens (release/<v> -> main, a forward-port): its head is a release branch of this repo.
function Test-RunnerPr {
    param($Pr)
    (-not $Pr.isCrossRepository) -and ("$($Pr.headRefName)" -like 'release/*')
}

function Get-RunnerGhRelease {
    $f = Get-RunnerFacts
    if (-not $f.ContainsKey('GhRelease')) {
        $r = Invoke-Native 'gh' @('api', "repos/$($script:Cfg.Repo)/releases/tags/$($f.Tag)") -AllowFailure
        $f.GhRelease = $null
        if ($r.ExitCode -eq 0) { $f.GhRelease = ConvertFrom-GhJson ($r.Output -join "`n") }
    }
    $f.GhRelease
}

function Get-MsixName { param([string]$Quad, [string]$Arch) "Wavee_${Quad}_$Arch.msix" }
function Get-SymbolsZipName { param([string]$Quad, [string]$Arch) "Wavee-$Quad-win-$Arch-symbols.zip" }
function Get-AppInstallerName { param([string]$Arch) "$($script:Cfg.AssetPrefix).$Arch.appinstaller" }
function Get-FeedUri { param([string]$Arch, [string]$Feed = $script:Cfg.FeedRelease) "https://github.com/$($script:Cfg.Repo)/releases/download/$Feed/$(Get-AppInstallerName $Arch)" }
function Get-MsixUri { param([string]$Tag, [string]$Quad, [string]$Arch) "https://github.com/$($script:Cfg.Repo)/releases/download/$Tag/$(Get-MsixName $Quad $Arch)" }

# The asset names of the version release and the feed, from the staged files (the same rule the stage step wrote).
function Get-ReleaseAssetPlan {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $stage = $f.Stage
    $release = @()
    foreach ($a in $ctx.Arches) { $release += (Get-MsixName $f.Quad $a) }
    foreach ($a in $ctx.Arches) {
        $z = Get-SymbolsZipName $f.Quad $a
        if (Test-Path -LiteralPath (Join-Path $stage $z)) { $release += $z }
    }
    $release += 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath (Join-Path $stage 'whatsnew.json')) { $release += 'whatsnew.json' }
    if (Test-Path -LiteralPath $f.NotesOut) { $release += @(Get-WaveeReleaseMediaNames -NotesOut $f.NotesOut) }
    $feed = @()
    foreach ($a in $ctx.Arches) { $feed += (Get-AppInstallerName $a) }
    if (Test-Path -LiteralPath (Join-Path $stage 'whatsnew-index.json')) { $feed += 'whatsnew-index.json' }
    @{
        Manifest = @(($release + $feed) | Sort-Object -Unique)
        Release  = @(($release + 'MANIFEST.txt') | Sort-Object -Unique)
        Feed     = @($feed | Sort-Object -Unique)
    }
}

function Get-PendingChangeFiles {
    $f = Get-RunnerFacts
    if ($null -eq $f.PendingChanges) {
        $list = @()
        if ($f.Ref) {
            $r = Invoke-RepoGit -Dir $f.GitDir -Arguments @('ls-tree', '--name-only', $f.Ref, 'changes/unreleased/') -AllowFailure
            foreach ($l in $r.Lines) {
                $leaf = Split-Path -Leaf $l
                if ($l -match '\.md$' -and $leaf -ne 'README.md') { $list += $l }
            }
        }
        $f.PendingChanges = $list
    }
    $f.PendingChanges
}

function Get-RefChangelog {
    param([string]$Ref)
    $f = Get-RunnerFacts
    if (-not $Ref) { $Ref = $f.Ref }
    Read-GitBlob -Dir $f.GitDir -Ref $Ref -Path 'CHANGELOG.md'
}

function Test-ChangelogHasVersion {
    param([string]$Ref)
    $f = Get-RunnerFacts
    $cl = Get-RefChangelog $Ref
    if (-not $cl) { return $false }
    [regex]::IsMatch($cl, '(?m)^## \[' + [regex]::Escape($f.Version) + '\]')
}

# The issues the change files at the ref list in their front matter (issues: [225, 226]).
function Get-ChangeFileIssues {
    $f = Get-RunnerFacts
    $issues = @()
    foreach ($p in (Get-PendingChangeFiles)) {
        $text = Read-GitBlob -Dir $f.GitDir -Ref $f.Ref -Path $p
        if (-not $text) { continue }
        $m = [regex]::Match($text, '(?m)^issues:\s*\[(?<l>[^\]]*)\]')
        if (-not $m.Success) { continue }
        foreach ($n in [regex]::Matches($m.Groups['l'].Value, '\d+')) { $issues += [int]$n.Value }
    }
    @($issues | Sort-Object -Unique)
}

function Get-RangeCommits {
    param([string]$To)
    $f = Get-RunnerFacts
    if (-not $f.PrevTag -or -not $To) { return }
    $log = (Invoke-RepoGit -Dir $f.GitDir -Arguments @('log', '--format=%H%x1f%h%x1f%s%x1f%b%x1e', "$($f.PrevTag)..$To")).Text
    $recs = ConvertFrom-GitLogRecords $log
    $recs
}

# The issues this release closed: the CHANGELOG [v] entry's (#n) refs that a commit in range fixes (or names as a PR).
function Get-ReleaseLinkedIssues {
    $f = Get-RunnerFacts
    $to = $f.ReleaseCommit
    if (-not $to) { $to = $f.Ref }
    if (-not $f.PrevTag -or -not $to) { return $null }
    $cl = Get-RefChangelog $to
    if (-not $cl -or -not (Test-ChangelogHasVersion $to)) { return $null }
    $refs = Get-ChangelogEntryRefs -Changelog $cl -Semver $f.Version
    $commits = @(Get-RangeCommits $to)
    Compare-ReleaseIssueRefs -ChangelogIssues @($refs.Issues) -Commits $commits
}

function Get-RunnerCrashKey {
    if (-not $script:CrashKey) {
        $k = Resolve-CrashIngestKey -Account $script:Cfg.OpAccount
        $gate = Get-CrashIngestGate -Channel 'stable' -CrashIngestUrl $script:Cfg.CrashIngestUrl -CrashIngestKey $k.Key
        if ($gate.Fail) { throw $gate.Message }
        $script:CrashKey = $k
        $script:Ctx.Secrets = @($script:Ctx.Secrets) + @($k.Key)
    }
    $script:CrashKey
}

$script:SdkTools = $null
function Get-RunnerSdkTools {
    if (-not $script:SdkTools) {
        $script:SdkTools = Get-WindowsSdkTools
        Add-VsInstallerToPath | Out-Null
    }
    $script:SdkTools
}

function Get-SigningDlib {
    @("$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
        'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
        'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
        'C:\Program Files (x86)\Microsoft\TrustedSigningClientTools\bin\Azure.CodeSigning.Dlib.dll') |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Get-FileSha256 {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLower()
}

#endregion

#region The release worktree

# The folder src\apps\Wavee.PlayPlay of a checkout points at (a junction's target, or the folder itself), or $null.
function Get-PlayPlaySource {
    param([Parameter(Mandatory = $true)][string]$Root)
    $p = Join-Path $Root 'src\apps\Wavee.PlayPlay'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    $item = Get-Item -LiteralPath $p -Force
    $target = $p
    if ($item.LinkType -and $item.Target) {
        $t = @($item.Target)[0]
        if (-not [IO.Path]::IsPathRooted($t)) { $t = Join-Path (Split-Path -Parent $p) $t }
        $target = $t
    }
    if (-not (Test-Path -LiteralPath (Join-Path $target 'Client\PlayPlayHost.cs'))) { return $null }
    Get-FullPath $target
}

function Get-WranglerCandidate {
    $ctx = $script:Ctx
    foreach ($r in @($ctx.Root, $ctx.Wt)) {
        if (-not $r) { continue }
        $p = Join-Path $r 'ops\crash\worker\node_modules\.bin\wrangler.cmd'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    $null
}

# Creates or reuses C:\WAVEE\release\<v>\WaveeMusic (a worktree of this repo on release/<v>) and
# C:\WAVEE\release\<v>\fluent-gpu (a detached engine worktree at engine.lock), links the PlayPlay junction, copies the
# signing metadata and installs the crash Worker's wrangler. Run in place (from the worktree) it only fast-forwards and
# re-pins. Throws on anything it cannot do safely.
function Initialize-ReleaseWorktree {
    $ctx = $script:Ctx
    $v = $ctx.Version
    $branch = "release/$v"
    $src = $ctx.Root
    $wt = $ctx.Wt
    $ewt = $ctx.Ewt
    Write-RunnerLine ''
    Write-RunnerLine '[setup] the release worktree'
    Invoke-RepoGit -Dir $src -Arguments @('fetch', 'origin', '--tags', '--quiet') -AllowFailure | Out-Null
    $origin = (Invoke-RepoGit -Dir $src -Arguments @('rev-parse', '--verify', '--quiet', "refs/remotes/origin/$branch") -AllowFailure).Text
    if (-not $origin) { throw "origin/$branch is not known here after a fetch" }

    if (-not (Test-Path -LiteralPath (Join-Path $wt '.git'))) {
        if (Test-Path -LiteralPath $wt) {
            if (@(Get-ChildItem -LiteralPath $wt -Force).Count -gt 0) { throw "$wt exists but is not a git worktree; move it away" }
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $wt) | Out-Null
        $local = (Invoke-RepoGit -Dir $src -Arguments @('rev-parse', '--verify', '--quiet', "refs/heads/$branch") -AllowFailure).Text
        if ($local) {
            $list = (Invoke-RepoGit -Dir $src -Arguments @('worktree', 'list', '--porcelain')).Lines
            if ($list -contains "branch refs/heads/$branch") { throw "the local branch $branch is checked out in another worktree (git worktree list); remove that worktree or release from it" }
            if (-not (Test-GitAncestor $src $local $origin)) { throw "the local branch $branch has commits that are not on origin; push or delete it first" }
            Invoke-RepoGit -Dir $src -Arguments @('worktree', 'add', $wt, $branch) -Echo | Out-Null
            Invoke-RepoGit -Dir $wt -Arguments @('merge', '--ff-only', $origin) -Echo | Out-Null
        }
        else {
            Invoke-RepoGit -Dir $src -Arguments @('worktree', 'add', '--track', '-b', $branch, $wt, "origin/$branch") -Echo | Out-Null
        }
    }
    else {
        $cur = (Invoke-RepoGit -Dir $wt -Arguments @('branch', '--show-current')).Text
        if ($cur -ne $branch) { throw "$wt is on '$cur', not $branch" }
        $head = (Invoke-RepoGit -Dir $wt -Arguments @('rev-parse', 'HEAD')).Text
        $dirty = @((Invoke-RepoGit -Dir $wt -Arguments @('status', '--porcelain') -DropAdvice).Lines)
        if ($head -ne $origin -and (Test-GitAncestor $wt $head $origin) -and $dirty.Count -eq 0) {
            Invoke-RepoGit -Dir $wt -Arguments @('merge', '--ff-only', $origin) -Echo | Out-Null
        }
    }
    Write-RunnerLine "  worktree $wt on $branch at $(Format-Sha (Invoke-RepoGit -Dir $wt -Arguments @('rev-parse', 'HEAD')).Text)"

    $engineSha = Get-EngineLockSha (Read-GitBlob -Dir $wt -Ref 'HEAD' -Path 'engine.lock')
    if (-not $engineSha) { throw "$branch has no engine.lock with a 40-character fluent-gpu sha" }
    $engineRepo = $ctx.EngineRepo
    if (-not (Test-Path -LiteralPath (Join-Path $ewt '.git'))) {
        if (-not $engineRepo -or -not (Test-Path -LiteralPath (Join-Path $engineRepo '.git'))) { throw "no fluent-gpu checkout at $engineRepo to make the engine worktree from" }
        if (-not (Test-GitCommit $engineRepo $engineSha)) {
            Invoke-RepoGit -Dir $engineRepo -Arguments @('fetch', 'origin', '--tags', '--quiet') -AllowFailure | Out-Null
        }
        if (-not (Test-GitCommit $engineRepo $engineSha)) { throw "fluent-gpu has no commit $engineSha (engine.lock)" }
        Invoke-RepoGit -Dir $engineRepo -Arguments @('worktree', 'add', '--detach', $ewt, $engineSha) -Echo | Out-Null
    }
    else {
        Sync-EngineWorktree -EngineSha $engineSha
    }
    Write-RunnerLine "  engine $ewt at $(Format-Sha $engineSha)"

    $pp = Join-Path $wt 'src\apps\Wavee.PlayPlay'
    if (-not $ctx.PublicOnly -and -not (Test-Path -LiteralPath $pp)) {
        $target = Get-PlayPlaySource -Root $src
        if (-not $target) { throw "no PlayPlay junction in $src to link into the release worktree (or pass -PublicOnly)" }
        Write-RunnerLine "  > New-Item -ItemType Junction -Path $pp -Target $target" 'DarkGray'
        New-Item -ItemType Junction -Path $pp -Target $target | Out-Null
    }

    $metaSrc = Join-Path $src 'ops\build\signing\metadata.json'
    $metaDst = Join-Path $wt 'ops\build\signing\metadata.json'
    if (-not (Test-SamePath $metaSrc $metaDst) -and (Test-Path -LiteralPath $metaSrc)) {
        if (-not (Test-Path -LiteralPath $metaDst) -or ((Get-FileSha256 $metaSrc) -ne (Get-FileSha256 $metaDst))) {
            Write-RunnerLine "  > copy $metaSrc -> $metaDst" 'DarkGray'
            Copy-Item -LiteralPath $metaSrc -Destination $metaDst -Force
        }
    }

    $wrangler = Join-Path $wt 'ops\crash\worker\node_modules\.bin\wrangler.cmd'
    if (-not (Test-Path -LiteralPath $wrangler)) {
        $r = Invoke-RunnerChild 'npm' @('--prefix', (Join-Path $wt 'ops\crash\worker'), 'ci')
        if ($r.ExitCode -ne 0) { throw "npm ci in ops\crash\worker exited $($r.ExitCode): $(@($r.Tail | Select-Object -Last 5) -join ' / ')" }
    }
}

# Puts the engine worktree on the pinned sha (only when it is clean).
function Sync-EngineWorktree {
    param([Parameter(Mandatory = $true)][string]$EngineSha)
    $ewt = $script:Ctx.Ewt
    if (-not (Test-Path -LiteralPath (Join-Path $ewt '.git'))) { throw "no engine worktree at $ewt; a -Go from the invoking checkout makes it" }
    $head = (Invoke-RepoGit -Dir $ewt -Arguments @('rev-parse', 'HEAD')).Text
    if ($head -eq $EngineSha) { return }
    $dirty = @((Invoke-RepoGit -Dir $ewt -Arguments @('status', '--porcelain') -DropAdvice).Lines)
    if ($dirty.Count -gt 0) { throw "the engine worktree $ewt has uncommitted changes; it must sit clean at engine.lock ($EngineSha)" }
    if (-not (Test-GitCommit $ewt $EngineSha)) {
        Invoke-RepoGit -Dir $ewt -Arguments @('fetch', 'origin', '--tags', '--quiet') -AllowFailure | Out-Null
    }
    Invoke-RepoGit -Dir $ewt -Arguments @('checkout', '--detach', $EngineSha) -Echo | Out-Null
}

#endregion

#region Preflight

function New-PreflightItem {
    param([string]$Name, [string]$Status, [string]$Text, [string]$Fix = '')
    [pscustomobject]@{ name = $Name; status = $Status; text = $Text; fix = $Fix }
}

function Invoke-PreflightCheck {
    param([string]$Name, [scriptblock]$Check)
    try { $r = & $Check }
    catch { $r = New-PreflightItem $Name 'action' ('could not check: ' + ($_.Exception.Message -replace "`r?`n", ' / ')) }
    if ($r -is [array]) { $r = @($r)[-1] }
    if ($null -eq $r) { $r = New-PreflightItem $Name 'ok' '' }
    if ($r -is [string]) { $r = New-PreflightItem $Name 'ok' $r }
    $r.name = $Name
    $r
}

# The preflight items, read-only, 1Password first. -Setup: only what an outer -Go needs before it makes the release
# worktree (the full list runs again inside it).
function Get-PreflightItems {
    param([switch]$Setup)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $items = New-Object System.Collections.Generic.List[object]

    $items.Add((Invoke-PreflightCheck '1Password' {
        $r = Invoke-RunnerNative 'op' @('whoami', '--account', $cfg.OpAccount) -AllowFailure -Quiet
        if ($r.ExitCode -eq 127) { return New-PreflightItem '' 'action' 'the 1Password CLI (op) is not installed; the pack steps read the crash ingest key with it' 'winget install -e --id AgileBits.1Password.CLI' }
        if ($r.ExitCode -ne 0) { return New-PreflightItem '' 'action' "the 1Password CLI is not signed in to $($cfg.OpAccount); the pack steps read the crash ingest key with op read" "op signin --account $($cfg.OpAccount)" }
        "signed in to $($cfg.OpAccount)"
    }))

    if (-not $Setup) {
        $items.Add((Invoke-PreflightCheck 'Windows SDK tools' {
            $t = Get-RunnerSdkTools
            "$($t.Version) (makeappx, makepri, signtool)"
        }))
        $items.Add((Invoke-PreflightCheck 'x64 cross toolchain' {
            if ($ctx.Arches -notcontains 'x64') { return 'not needed (-SkipArch x64)' }
            if ($ctx.X64Msix) {
                if (-not (Test-Path -LiteralPath $ctx.X64Msix)) { return New-PreflightItem '' 'action' "-X64Msix not found: $($ctx.X64Msix)" }
                return "not needed: adopting $($ctx.X64Msix)"
            }
            $x = Test-X64CrossToolchain
            if (-not $x.Ok) { return New-PreflightItem '' 'action' $x.Reason '-SkipArch x64, or -X64Msix <prebuilt.msix>' }
            "$($x.LinkExe)"
        }))
        $items.Add((Invoke-PreflightCheck 'Trusted Signing' {
            $meta = Join-Path $ctx.Root 'ops\build\signing\metadata.json'
            if (-not (Test-Path -LiteralPath $meta)) {
                return New-PreflightItem '' 'action' "no signing metadata at $meta" "copy ops\build\signing\metadata.template.json to metadata.json in $($ctx.Root) and fill it in (a -Go copies it into the release worktree)"
            }
            if (-not (Get-SigningDlib)) { return New-PreflightItem '' 'action' 'the Trusted Signing client (Azure.CodeSigning.Dlib.dll) is not installed' 'winget install -e --id Microsoft.Azure.ArtifactSigningClientTools' }
            $t = Invoke-RunnerNative 'az' @('account', 'get-access-token', '--subscription', $cfg.Subscription, '--scope', 'https://codesigning.azure.net/.default', '--query', 'expiresOn', '-o', 'tsv') -AllowFailure -Quiet
            if ($t.ExitCode -ne 0) { return New-PreflightItem '' 'action' "no Trusted Signing token for '$($cfg.Subscription)'" 'az login (an identity with Artifact Signing Certificate Profile Signer on the Wavee account)' }
            $until = (@($t.Output | Where-Object { "$_" -match '^\d{4}-' }) | Select-Object -Last 1)
            "token until $until; metadata $meta"
        }))
    }

    $items.Add((Invoke-PreflightCheck 'gh auth' {
        $s = Invoke-RunnerNative 'gh' @('auth', 'status') -AllowFailure -Quiet
        if ($s.ExitCode -ne 0) { return New-PreflightItem '' 'action' 'gh is not signed in' 'gh auth login' }
        $parts = @()
        foreach ($repo in @($cfg.Repo, $cfg.EngineRepo)) {
            $p = Invoke-RunnerNative 'gh' @('repo', 'view', $repo, '--json', 'viewerPermission', '--jq', '.viewerPermission') -AllowFailure
            $perm = (@($p.Output | Where-Object { "$_" -match '^[A-Z]+$' }) | Select-Object -Last 1)
            if ($p.ExitCode -ne 0 -or @('ADMIN', 'MAINTAIN', 'WRITE') -notcontains $perm) {
                return New-PreflightItem '' 'action' "gh cannot write to $repo (permission '$perm')" 'gh auth login with the owner account'
            }
            $parts += "$repo $perm"
        }
        $parts -join ', '
    }))

    if (-not $Setup) {
        $items.Add((Invoke-PreflightCheck 'wrangler' {
            $w = $null
            if ($ctx.InWorktree) {
                $w = Join-Path $ctx.Wt 'ops\crash\worker\node_modules\.bin\wrangler.cmd'
                if (-not (Test-Path -LiteralPath $w)) { return New-PreflightItem '' 'action' "the crash Worker's wrangler is not installed in the release worktree" "npm --prefix $($ctx.Wt)\ops\crash\worker ci" }
            }
            else {
                $w = Get-WranglerCandidate
                if (-not $w) { return New-PreflightItem '' 'note' "the crash Worker's wrangler is not installed in $($ctx.Root); -Go runs npm ci in the release worktree and checks the login there" }
            }
            $who = Invoke-RunnerNative $w @('whoami') -AllowFailure -Quiet
            $login = Test-WranglerLogin -ExitCode $who.ExitCode -Output $who.Output
            if (-not $login.Ok) { return New-PreflightItem '' 'action' $login.Detail 'npx wrangler login (in ops\crash\worker)' }
            "logged in ($w)"
        }))
    }

    $items.Add((Invoke-PreflightCheck 'PlayPlay junction' {
        if ($ctx.PublicOnly) { return 'not needed (-PublicOnly builds the public variant)' }
        if ($ctx.InWorktree) {
            $target = Get-PlayPlaySource -Root $ctx.Wt
            if (-not $target) { return New-PreflightItem '' 'action' "src\apps\Wavee.PlayPlay is missing in the release worktree" 'run -Go from the checkout that has the PlayPlay junction (it links it in), or pass -PublicOnly' }
            return "linked: $target"
        }
        $target = Get-PlayPlaySource -Root $ctx.Root
        if (-not $target) { return New-PreflightItem '' 'action' "no src\apps\Wavee.PlayPlay junction in $($ctx.Root); a release build is PlayPlay-inclusive" 'create the junction to the PlayPlay worktree (CLAUDE.md), or pass -PublicOnly' }
        "$target (a -Go links it into the release worktree)"
    }))

    $items.Add((Invoke-PreflightCheck 'release branch' {
        $f = Get-RunnerFacts
        if (-not $f.OriginBranchSha) { return New-PreflightItem '' 'action' "origin has no $($f.Branch)" "check -Version; open-next makes release/<next> after each release, -Hotfix makes a hotfix branch" }
        $t = "origin/$($f.Branch) at $(Format-Sha $f.OriginBranchSha)"
        if ($f.WtExists -and $f.HeadSha -ne $f.OriginBranchSha) {
            $ahead = (Invoke-RepoGit -Dir $f.GitDir -Arguments @('rev-list', '--count', "$($f.OriginBranchSha)..HEAD") -AllowFailure).Text
            $t += "; the release worktree is $ahead commit(s) ahead"
        }
        $t
    }))

    $items.Add((Invoke-PreflightCheck 'engine.lock' {
        $f = Get-RunnerFacts
        if (-not $f.Ref) { return New-PreflightItem '' 'action' "no $($f.Branch) to read engine.lock from" }
        if (-not (Test-GitPath $f.GitDir $f.Ref 'engine.lock')) { return New-PreflightItem '' 'action' "$($f.Branch) has no engine.lock" "merge the PR that adds engine.lock (the fluent-gpu commit this release builds with) into $($f.Branch)" }
        if (-not $f.EngineSha) { return New-PreflightItem '' 'action' 'engine.lock names no 40-character fluent-gpu sha' }
        $dir = $f.EngineDir
        if (-not $dir) { return New-PreflightItem '' 'action' "no fluent-gpu checkout at $($ctx.EngineRepo)" 'clone christosk92/fluent-gpu beside WaveeMusic' }
        if (-not (Test-GitCommit $dir $f.EngineSha)) {
            Invoke-RepoGit -Dir $dir -Arguments @('fetch', 'origin', '--tags', '--quiet') -AllowFailure | Out-Null
        }
        if (-not (Test-GitCommit $dir $f.EngineSha)) {
            $gh = Invoke-Native 'gh' @('api', "repos/$($cfg.EngineRepo)/commits/$($f.EngineSha)", '--jq', '.sha') -AllowFailure
            if ($gh.ExitCode -ne 0) { return New-PreflightItem '' 'action' "fluent-gpu has no commit $($f.EngineSha) (engine.lock)" 'bump engine.lock to a commit on fluent-gpu main (or a wavee-hotfix/* branch)' }
            return New-PreflightItem '' 'note' "fluent-gpu $(Format-Sha $f.EngineSha) is on GitHub but not in $dir after a fetch"
        }
        "fluent-gpu $(Format-Sha $f.EngineSha) (in $dir)"
    }))

    $items.Add((Invoke-PreflightCheck 'runner on the branch' {
        $f = Get-RunnerFacts
        if ($ctx.InWorktree) { return "ops\release\release.ps1 of $($f.Branch)" }
        if (-not $f.Ref) { return New-PreflightItem '' 'action' "no $($f.Branch)" }
        if (-not (Test-GitPath $f.GitDir $f.Ref 'ops/release/release.ps1')) {
            return New-PreflightItem '' 'action' "$($f.Branch) has no ops/release/release.ps1; a -Go relaunches the branch's own runner in the release worktree" "merge the runner into $($f.Branch) first"
        }
        "$($f.Branch) carries ops/release/release.ps1"
    }))

    if ($Setup) {
        $items.Add((Invoke-PreflightCheck 'signing metadata' {
            $meta = Join-Path $ctx.Root 'ops\build\signing\metadata.json'
            if (-not (Test-Path -LiteralPath $meta)) { return New-PreflightItem '' 'action' "no signing metadata at $meta" 'copy ops\build\signing\metadata.template.json to metadata.json and fill it in' }
            "$meta (copied into the release worktree)"
        }))
        return (Resolve-PreflightNeeds $items.ToArray())
    }

    $items.Add((Invoke-PreflightCheck 'whatsnew.json' {
        $f = Get-RunnerFacts
        $p = "ops/release/wavee/$($f.Version)/whatsnew.json"
        if (-not (Test-GitPath $f.GitDir $f.Ref $p)) { return New-PreflightItem '' 'action' "$($f.Branch) has no $p" "author it (tagline and highlights; a hotfix needs no highlight) and merge it into $($f.Branch)" }
        $p
    }))

    $items.Add((Invoke-PreflightCheck 'change files' {
        $f = Get-RunnerFacts
        $pending = @(Get-PendingChangeFiles)
        if ($pending.Count -eq 0 -and (Test-ChangelogHasVersion)) { return "assembled into CHANGELOG [$($f.Version)]" }
        if (-not (Test-GitPath $f.GitDir $f.Ref 'changes/unreleased')) {
            return New-PreflightItem '' 'action' "$($f.Branch) has no changes/unreleased/: the change-file flow is not merged there yet" "merge it (changes/README.md, changes/unreleased/.gitkeep) into $($f.Branch)"
        }
        $toolRoot = $null
        $temp = $null
        try {
            if ($f.WtExists) { $toolRoot = $ctx.Wt }
            else {
                $temp = Join-Path ([IO.Path]::GetTempPath()) ('wavee-release-changes-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Force -Path $temp | Out-Null
                $zip = "$temp.zip"
                $a = Invoke-RepoGit -Dir $f.GitDir -Arguments @('archive', '--format=zip', '-o', $zip, $f.Ref, 'changes') -AllowFailure
                if ($a.ExitCode -eq 0) { Expand-Archive -LiteralPath $zip -DestinationPath $temp -Force }
                Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
                $toolRoot = $temp
            }
            $tool = Join-Path $ctx.Root 'src\apps\Wavee.ReleaseTool'
            $r = Invoke-RunnerNative 'dotnet' @('run', '--project', $tool, '-c', $cfg.Configuration, '--', 'changes', 'check', '--root', $toolRoot, '--json') -AllowFailure
            $json = $null
            try { $json = ConvertFrom-GhJson ($r.Output -join "`n") } catch { $json = $null }
            if ($r.ExitCode -eq 0) { return "$($pending.Count) change file(s) valid" }
            if ($r.ExitCode -eq 2) {
                $problems = @()
                if ($json) {
                    foreach ($cf in @($json.files)) { foreach ($pr in @($cf.problems)) { if ($pr) { $problems += "$($cf.path): $pr" } } }
                    foreach ($pr in @($json.problems)) { if ($pr) { $problems += "$pr" } }
                }
                if ($problems.Count -eq 0) { $problems = @($r.Output | Select-Object -Last 5) }
                return New-PreflightItem '' 'action' ("change files invalid: " + ($problems -join ' / ')) "fix them on $($f.Branch) (a PR per change) and run again"
            }
            return New-PreflightItem '' 'action' ("Wavee.ReleaseTool changes check exited $($r.ExitCode): " + (@($r.Output | Select-Object -Last 4) -join ' / '))
        }
        finally {
            if ($temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }))

    $items.Add((Invoke-PreflightCheck 'feed monotonic' {
        $f = Get-RunnerFacts
        if (-not $f.Quad) { return New-PreflightItem '' 'action' 'no build number to compare: the props of the release branch cannot be read' }
        $feedJudge = Test-StepFeed
        if ($feedJudge.Done) { return "the feed already serves $($f.Quad) (the feed step is done)" }
        $feeds = @(Get-RunnerFeeds)
        try {
            $rows = Test-FeedMonotonic $cfg.Repo $feeds $f.Quad $f.Version @($ctx.Arches) $cfg.AssetPrefix
        }
        catch {
            $msg = ($_.Exception.Message -replace "`r?`n", ' / ')
            if ($msg -like 'feed monotonic gate failed*') { return New-PreflightItem '' 'action' $msg 'something was published out of band: release a higher build, never sideways' }
            if ($ctx.Go) { return New-PreflightItem '' 'action' "could not read the feed: $msg" }
            return New-PreflightItem '' 'note' "could not read the feed: $msg"
        }
        (@($rows | ForEach-Object { "$($_.Feed)/$($_.Arch) $($_.Current) -> $($_.New)" }) -join '; ')
    }))

    $items.Add((Invoke-PreflightCheck 'tag free' {
        $f = Get-RunnerFacts
        $parts = @()
        if ($f.OriginTagCommit) {
            $ours = $false
            if (Test-GitCommit $f.GitDir $f.OriginTagCommit) {
                $range = $f.OriginTagCommit
                if ($f.PrevTag) { $range = "$($f.PrevTag)..$($f.OriginTagCommit)" }
                $subjects = (Invoke-RepoGit -Dir $f.GitDir -Arguments @('log', '--format=%s', '--max-count=3000', $range) -AllowFailure).Lines
                $ours = [bool](@($subjects | Where-Object { $_ -like "release: Wavee $($f.Version) *" }).Count)
            }
            if (-not $ours) { return New-PreflightItem '' 'action' "$($f.Tag) already exists on origin at $(Format-Sha $f.OriginTagCommit) and carries no 'release: Wavee $($f.Version)' commit" 'a pushed tag is never moved or deleted: release the next patch instead' }
            $parts += "$($f.Tag) pushed by this release at $(Format-Sha $f.OriginTagCommit)"
        }
        else {
            $parts += "$($f.Tag) is free on origin"
            if ($f.LocalTagCommit -and $f.ReleaseCommit -and $f.LocalTagCommit -ne $f.ReleaseCommit) { $parts += "the local tag at $(Format-Sha $f.LocalTagCommit) moves to the release commit in the tag step" }
        }
        if ($f.EngineOriginTagCommit -and $f.EngineSha -and $f.EngineOriginTagCommit -ne $f.EngineSha) {
            return New-PreflightItem '' 'action' "fluent-gpu already has $($f.Tag) at $(Format-Sha $f.EngineOriginTagCommit), engine.lock says $(Format-Sha $f.EngineSha)" 'a pushed tag is never moved: fix engine.lock or release the next patch'
        }
        if ($f.EngineOriginTagCommit) { $parts += "fluent-gpu $($f.Tag) at the pinned commit" } else { $parts += "fluent-gpu $($f.Tag) is free" }
        $parts -join '; '
    }))

    $items.Add((Invoke-PreflightCheck 'codename' {
        $f = Get-RunnerFacts
        $sv = Test-WaveeSemver $f.Version
        if ($f.BumpCommit) {
            if ($ctx.Codename -and $ctx.Codename -ne $f.BumpCommit.Codename) { return New-PreflightItem '' 'action' "the bump commit already named $($f.Version) '$($f.BumpCommit.Codename)'" 'drop -Codename' }
            return "$($f.BumpCommit.Codename) (from the bump commit)"
        }
        $newMinor = $true
        if ($f.PrevVersion) {
            $pv = [version]$f.PrevVersion
            $newMinor = ($pv.Major -ne $sv.Major -or $pv.Minor -ne $sv.Minor)
        }
        $propsCodename = ''
        if ($f.Props) { $propsCodename = $f.Props.Codename }
        if ($newMinor) {
            if ($ctx.Codename) { return "new minor $($sv.Major).$($sv.Minor): $($ctx.Codename)" }
            $prevCodename = ''
            if ($f.PrevTag) {
                $pt = Read-GitBlob -Dir $f.GitDir -Ref $f.PrevTag -Path 'src/apps/Wavee/Wavee.Version.props'
                if ($pt) { $prevCodename = (Get-WaveeVersionProps -Text $pt).Codename }
            }
            if ($propsCodename -and $prevCodename -and $propsCodename -ne $prevCodename) { return "new minor $($sv.Major).$($sv.Minor): $propsCodename (already in the props)" }
            return New-PreflightItem '' 'action' "$($f.Version) starts a new minor and needs its codename" '-Codename <Name> (the sea series: Abyss 0.1, Breaker 0.2, Crest 0.3, Drift 0.4, Ebb, Fetch, ...)'
        }
        if ($ctx.Codename -and $ctx.Codename -ne $propsCodename) { return New-PreflightItem '' 'action' "a patch keeps its minor's codename '$propsCodename'" 'drop -Codename' }
        "$propsCodename (a patch of $($sv.Major).$($sv.Minor))"
    }))

    $items.Add((Invoke-PreflightCheck 'PRs into main' {
        $prs = @(@(Get-RunnerOpenPrs) | Where-Object { $_.baseRefName -eq 'main' -and -not (Test-RunnerPr $_) })
        if ($prs.Count -eq 0) { return 'none' }
        $list = @($prs | ForEach-Object { "#$($_.number) $($_.headRefName) ($($_.author.login))" }) -join '; '
        New-PreflightItem '' 'note' "$($prs.Count) open PR(s) target main: $list" 'release.ps1 -RetargetPrs -Go moves them to the open release branch'
    }))

    $items.Add((Invoke-PreflightCheck 'issue refs' {
        $f = Get-RunnerFacts
        if (-not $f.PrevTag) { return 'no previous wavee-v tag: nothing to range over' }
        $to = $f.ReleaseCommit
        if (-not $to) { $to = $f.Ref }
        $range = "$($f.PrevTag)..$(Format-Sha $to)"
        $commits = @(Get-RangeCommits $to)
        $bad = @()
        if (Test-ChangelogHasVersion $to) {
            $refs = Get-ChangelogEntryRefs -Changelog (Get-RefChangelog $to) -Semver $f.Version
            $cmp = Compare-ReleaseIssueRefs -ChangelogIssues @($refs.Issues) -Commits $commits
            $mismatch = Format-IssueRefMismatch $cmp $f.Version $range
            $bad = @($mismatch)
            $source = "the CHANGELOG [$($f.Version)] entry"
        }
        else {
            $cmp = Compare-ReleaseIssueRefs -ChangelogIssues @(Get-ChangeFileIssues) -Commits $commits
            foreach ($m in $cmp.MissingInChangelog) {
                $c = ($m.Commits | ForEach-Object { "$($_.Short) `"$($_.Subject)`"" }) -join ', '
                $bad += "issue #$($m.Issue) is fixed by $c but no change file lists it in issues:"
            }
            foreach ($i in $cmp.MissingInGit) { $bad += "a change file lists #$i but no commit in $range carries 'Fixes #$i'" }
            $source = "the change files' issues:"
        }
        if ($bad.Count -gt 0) { return New-PreflightItem '' 'action' ($bad -join ' / ') "add 'Fixes #n' to the fixing PR's commit, or the issue to its change file (issues: [n])" }
        "$(@($cmp.Linked).Count) issue(s) linked against $source, $(@($cmp.Unlinked).Count) commit(s) without an issue, since $($f.PrevTag)"
    }))

    # Soft, as in the old script: a bullet with no issue is fine (not every change is a bugfix); this only says so.
    $items.Add((Invoke-PreflightCheck 'issue coverage' {
        $f = Get-RunnerFacts
        $to = $f.ReleaseCommit
        if (-not $to) { $to = $f.Ref }
        if (-not (Test-ChangelogHasVersion $to)) { return "judged once the CHANGELOG [$($f.Version)] section exists (after changelog)" }
        $refs = Get-ChangelogEntryRefs -Changelog (Get-RefChangelog $to) -Semver $f.Version
        if ($refs.Unreferenced) { return New-PreflightItem '' 'note' "$($refs.Unreferenced) of $($refs.Bullets) CHANGELOG bullet(s) cite no issue" }
        "all $($refs.Bullets) CHANGELOG bullet(s) cite an issue"
    }))

    $items.Add((Invoke-PreflightCheck 'release worktree' {
        $f = Get-RunnerFacts
        if ($ctx.InWorktree) {
            $dirty = @((Invoke-RepoGit -Dir $ctx.Wt -Arguments @('status', '--porcelain') -DropAdvice).Lines)
            if ($dirty.Count -gt 0) { return New-PreflightItem '' 'action' "the release worktree has $($dirty.Count) uncommitted change(s), first: $($dirty[0])" "commit or discard them in $($ctx.Wt)" }
            $cur = (Invoke-RepoGit -Dir $ctx.Wt -Arguments @('branch', '--show-current')).Text
            if ($cur -ne $f.Branch) { return New-PreflightItem '' 'action' "the release worktree is on '$cur', not $($f.Branch)" }
            if ($f.OriginBranchSha -and -not $f.OriginTagCommit -and -not (Test-GitAncestor $ctx.Wt $f.OriginBranchSha 'HEAD')) {
                $behind = (Invoke-RepoGit -Dir $ctx.Wt -Arguments @('rev-list', '--count', "HEAD..$($f.OriginBranchSha)") -AllowFailure).Text
                return New-PreflightItem '' 'action' "origin/$($f.Branch) has $behind commit(s) the release worktree lacks" "git -C $($ctx.Wt) merge origin/$($f.Branch), then -Go again"
            }
            $eh = (Invoke-RepoGit -Dir $ctx.Ewt -Arguments @('rev-parse', 'HEAD') -AllowFailure).Text
            $edirty = @((Invoke-RepoGit -Dir $ctx.Ewt -Arguments @('status', '--porcelain') -AllowFailure -DropAdvice).Lines)
            if ($edirty.Count -gt 0) { return New-PreflightItem '' 'action' "the engine worktree $($ctx.Ewt) has uncommitted changes" "discard them; it must sit clean at engine.lock" }
            if ($f.EngineSha -and $eh -ne $f.EngineSha) { return New-PreflightItem '' 'action' "the engine worktree is at $(Format-Sha $eh), engine.lock says $(Format-Sha $f.EngineSha)" "git -C $($ctx.Ewt) checkout --detach $($f.EngineSha)" }
            return "$($ctx.Wt) on $($f.Branch) at $(Format-Sha $f.HeadSha); engine at $(Format-Sha $eh)"
        }
        if ($f.WtExists) { return "exists: $($ctx.Wt) at $(Format-Sha $f.HeadSha)" }
        "a -Go makes $($ctx.Wt) (origin/$($f.Branch)) and $($ctx.Ewt) (fluent-gpu at engine.lock)"
    }))

    Resolve-PreflightNeeds $items.ToArray()
}

# An item that only some steps need refuses nothing once those steps are done: a -Go days later that only polls the
# Store or comments on issues does not need a signing token or a 1Password session any more.
$script:ItemNeededBy = @{
    '1Password'           = @('pack-arm64', 'pack-x64', 'store-submit')
    'Windows SDK tools'   = @('sign', 'store-submit')
    'x64 cross toolchain' = @('pack-x64', 'store-submit')
    'Trusted Signing'     = @('sign')
    'signing metadata'    = @('sign')
    'wrangler'            = @('symbols', 'store-submit')
    'PlayPlay junction'   = @('pack-arm64', 'pack-x64', 'store-submit')
    'whatsnew.json'       = @('notes')
}

function Resolve-PreflightNeeds {
    param([object[]]$Items)
    foreach ($i in $Items) {
        $steps = $script:ItemNeededBy[$i.name]
        if ($i.status -eq 'action' -and $steps) {
            $open = @($steps | Where-Object { -not (Get-StepJudge $_).Done })
            if ($open.Count -eq 0) {
                $i.status = 'ok'
                $i.text = "not needed any more ($($steps -join ', ') done); was: $($i.text)"
                $i.fix = ''
            }
        }
        $i
    }
}

function Show-PreflightTable {
    param([object[]]$Items)
    Write-RunnerLine ''
    Write-RunnerLine '[preflight]'
    Write-RunnerLine ('  {0,-21} {1,-7} {2}' -f 'item', 'status', 'detail')
    foreach ($i in $Items) {
        $color = ''
        if ($i.status -eq 'action') { $color = 'Yellow' } elseif ($i.status -eq 'note') { $color = 'DarkYellow' }
        Write-RunnerLine ('  {0,-21} {1,-7} {2}' -f $i.name, $i.status, $i.text) $color
        if ($i.fix -and $i.status -ne 'ok') { Write-RunnerLine ('  {0,-21} {1,-7} fix: {2}' -f '', '', $i.fix) $color }
    }
}

function Test-PreflightRefused {
    param([object[]]$Items)
    [bool](@($Items | Where-Object { $_.status -eq 'action' }).Count)
}

#endregion

#region Step judges (reality first, the state second)

function New-Judge {
    param([bool]$Done, [string]$Text, [string]$Status = '')
    if (-not $Status) { if ($Done) { $Status = 'DONE' } else { $Status = 'NOT DONE' } }
    @{ Done = $Done; Status = $Status; Text = $Text }
}

function Get-StepJudge {
    param([Parameter(Mandatory = $true)][string]$Name)
    try { $j = & $script:StepJudges[$Name] }
    catch { $j = New-Judge $false ('could not judge: ' + ($_.Exception.Message -replace "`r?`n", ' / ')) }
    $j
}

function Test-StepChangelog {
    $f = Get-RunnerFacts
    if (-not $f.Ref) { return New-Judge $false "origin has no $($f.Branch)" }
    $pending = @(Get-PendingChangeFiles)
    $has = Test-ChangelogHasVersion
    $t = "$($pending.Count) change file(s) in changes/unreleased; CHANGELOG.md "
    if ($has) { $t += "has [$($f.Version)]" } else { $t += "has no [$($f.Version)] yet" }
    New-Judge (($pending.Count -eq 0) -and $has) $t
}

function Test-StepBump {
    $f = Get-RunnerFacts
    if ($f.BumpCommit) { return New-Judge $true "$(Format-Sha $f.BumpCommit.Sha) $($f.BumpCommit.Subject)" }
    if (-not $f.Props) { return New-Judge $false "no Wavee.Version.props on $($f.Branch)" }
    New-Judge $false "the props say $($f.Props.Version) build $($f.Props.Build); the bump commits $($f.Version) $($f.Codename) build $($f.Quad)"
}

function Test-StepNotes {
    $f = Get-RunnerFacts
    if (-not $f.ReleaseCommit) { return New-Judge $false 'runs after the bump' }
    $rec = Get-StepRecord 'notes'
    if (-not $rec -or $rec.status -ne 'DONE') { return New-Judge $false 'not validated for this release yet' }
    if ($rec.commit -ne $f.ReleaseCommit) { return New-Judge $false "validated for $(Format-Sha $rec.commit); the release commit is now $(Format-Sha $f.ReleaseCommit)" }
    foreach ($n in @('whatsnew.json', 'whatsnew-index.json', 'RELEASE_BODY.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $f.NotesOut $n))) { return New-Judge $false "$n is missing from $($f.NotesOut)" }
    }
    $doc = Get-Content -LiteralPath (Join-Path $f.NotesOut 'whatsnew.json') -Raw | ConvertFrom-Json
    if ("$($doc.packageVersion)" -ne $f.Quad) { return New-Judge $false "the emitted whatsnew.json says $($doc.packageVersion), not $($f.Quad)" }
    New-Judge $true "$($f.NotesOut) (validated for $(Format-Sha $f.ReleaseCommit))"
}

function Test-StepTag {
    $f = Get-RunnerFacts
    if (-not $f.ReleaseCommit) { return New-Judge $false 'runs after the bump' }
    $appOk = ($f.LocalTagCommit -eq $f.ReleaseCommit) -or ($f.OriginTagCommit -eq $f.ReleaseCommit)
    $engOk = $f.EngineSha -and (($f.EngineLocalTagCommit -eq $f.EngineSha) -or ($f.EngineOriginTagCommit -eq $f.EngineSha))
    $t = "WaveeMusic $($f.Tag): "
    if ($appOk) { $t += "at $(Format-Sha $f.ReleaseCommit)" } elseif ($f.LocalTagCommit) { $t += "at $(Format-Sha $f.LocalTagCommit), not the release commit $(Format-Sha $f.ReleaseCommit)" } else { $t += 'none' }
    $t += "; fluent-gpu $($f.Tag): "
    if ($engOk) { $t += "at $(Format-Sha $f.EngineSha)" } elseif ($f.EngineLocalTagCommit) { $t += "at $(Format-Sha $f.EngineLocalTagCommit), not $(Format-Sha $f.EngineSha)" } else { $t += 'none' }
    New-Judge ([bool]($appOk -and $engOk)) $t
}

function Test-StepPack {
    param([Parameter(Mandatory = $true)][string]$Arch)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    if ($ctx.Arches -notcontains $Arch) { return New-Judge $true "-SkipArch $Arch" 'N/A' }
    if (-not $f.ReleaseCommit) { return New-Judge $false 'runs after the bump' }
    $msix = Join-Path $f.Stage (Get-MsixName $f.Quad $Arch)
    if (-not (Test-Path -LiteralPath $msix)) { return New-Judge $false "no $(Split-Path -Leaf $msix) staged" }
    $id = Get-MsixIdentity $msix
    if ($id.Version -ne $f.Quad -or $id.ProcessorArchitecture -ne $Arch -or $id.Name -ne $script:Cfg.IdentityName) {
        return New-Judge $false "the staged package says $($id.Name) $($id.Version) $($id.ProcessorArchitecture)"
    }
    $rec = Get-StepRecord "pack-$Arch"
    if (-not $rec -or $rec.status -ne 'DONE' -or $rec.commit -ne $f.ReleaseCommit) {
        $was = '-'
        if ($rec) { $was = Format-Sha $rec.commit }
        return New-Judge $false "staged from $was; the release commit is $(Format-Sha $f.ReleaseCommit)"
    }
    if (-not $rec.adopted) {
        $stamp = Join-Path $f.Stage "symbols\$($f.Quad)\win-$Arch\SYMBOLS.txt"
        if (-not (Test-Path -LiteralPath $stamp)) { return New-Judge $false "no SYMBOLS.txt beside the $Arch package" }
        $short = Format-Sha $f.ReleaseCommit
        if (-not (Select-String -LiteralPath $stamp -Pattern ("^commit=" + [regex]::Escape($short) + '$') -Quiet)) {
            return New-Judge $false "SYMBOLS.txt does not name commit $short"
        }
    }
    New-Judge $true "$(Split-Path -Leaf $msix) from $(Format-Sha $f.ReleaseCommit), stamped"
}

function Test-StepSymbols {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    if (-not $f.ReleaseCommit) { return New-Judge $false 'runs after the packs' }
    $rec = Get-StepRecord 'symbols'
    if (-not $rec -or $rec.status -ne 'DONE') { return New-Judge $false 'no symbol maps uploaded for this release yet' }
    if ($rec.quad -ne $f.Quad -or $rec.commit -ne $f.ReleaseCommit) { return New-Judge $false "uploaded for $($rec.quad) at $(Format-Sha $rec.commit)" }
    $byArch = $rec.arches
    foreach ($a in $ctx.Arches) {
        $r = $null
        if ($byArch -is [hashtable]) { $r = $byArch[$a] }
        if (-not ($r -is [hashtable])) { return New-Judge $false "no symbol map recorded for $a" }
        $dir = Join-Path $f.Stage "symbols\$($f.Quad)\win-$a"
        if ((Get-FileSha256 (Join-Path $dir 'Wavee.symmap')) -ne $r.symmap) { return New-Judge $false "the staged $a Wavee.symmap is not the one uploaded" }
        if ((Get-FileSha256 (Join-Path $dir 'SYMBOLS.txt')) -ne $r.symbolsTxt) { return New-Judge $false "the $a package was rebuilt after its symbols were uploaded" }
    }
    New-Judge $true "symbols/$($f.Quad)/win-<arch>.symmap uploaded for $($ctx.Arches -join ', ')"
}

function Test-StepSign {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    if (-not $f.Quad -or -not (Test-Path -LiteralPath $f.Stage)) { return New-Judge $false 'nothing staged yet' }
    $tools = Get-RunnerSdkTools
    foreach ($a in $ctx.Arches) {
        $msix = Join-Path $f.Stage (Get-MsixName $f.Quad $a)
        if (-not (Test-Path -LiteralPath $msix)) { return New-Judge $false "no $(Split-Path -Leaf $msix)" }
        if (-not (Test-MsixSignature $msix $tools.SignTool)) { return New-Judge $false "$(Split-Path -Leaf $msix) carries no trusted signature" }
    }
    New-Judge $true "signtool verify /pa passes on $($ctx.Arches.Count) package(s)"
}

function Test-StepStage {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $manifest = Join-Path $f.Stage 'MANIFEST.txt'
    if (-not $f.Quad -or -not (Test-Path -LiteralPath $manifest)) { return New-Judge $false 'no MANIFEST.txt staged' }
    $names = @(Get-Content -LiteralPath $manifest | Where-Object { $_ } | ForEach-Object { ($_ -split '  ', 2)[1] })
    $want = @()
    foreach ($a in $ctx.Arches) { $want += (Get-MsixName $f.Quad $a); $want += (Get-AppInstallerName $a) }
    foreach ($w in $want) { if ($names -notcontains $w) { return New-Judge $false "MANIFEST.txt does not list $w" } }
    if (-not (Test-ReleaseManifest $f.Stage $manifest)) { return New-Judge $false 'the staged files do not match MANIFEST.txt' }
    New-Judge $true "MANIFEST.txt matches $($names.Count) staged file(s)"
}

function Test-StepPush {
    $f = Get-RunnerFacts
    if (-not $f.ReleaseCommit) { return New-Judge $false 'runs after the bump' }
    $branchOk = $f.OriginBranchSha -and (Test-GitAncestor $f.GitDir $f.ReleaseCommit $f.OriginBranchSha)
    $tagOk = ($f.OriginTagCommit -eq $f.ReleaseCommit)
    $engOk = $f.EngineSha -and ($f.EngineOriginTagCommit -eq $f.EngineSha)
    $missing = @()
    if (-not $branchOk) { $missing += "origin/$($f.Branch) lacks $(Format-Sha $f.ReleaseCommit)" }
    if (-not $tagOk) { $missing += "origin has no $($f.Tag) at it" }
    if (-not $engOk) { $missing += "fluent-gpu origin has no $($f.Tag) at $(Format-Sha $f.EngineSha)" }
    if ($missing.Count -gt 0) { return New-Judge $false ($missing -join '; ') }
    New-Judge $true "origin/$($f.Branch) and $($f.Tag) at $(Format-Sha $f.ReleaseCommit); fluent-gpu $($f.Tag) at $(Format-Sha $f.EngineSha)"
}

# Problems between the published release's assets and the staged files (digest when GitHub gives one, else size).
function Compare-ReleaseAssets {
    param($Release, [string]$Stage, [string[]]$Names)
    $problems = @()
    foreach ($n in $Names) {
        $local = Join-Path $Stage $n
        $hit = @($Release.assets | Where-Object { $_.name -eq $n })
        if ($hit.Count -eq 0) { $problems += "$n is missing"; continue }
        if (-not (Test-Path -LiteralPath $local)) { continue }
        $digest = "$($hit[0].digest)"
        if ($digest -match '^sha256:([0-9a-f]{64})$') {
            if ($Matches[1] -ne (Get-FileSha256 $local)) { $problems += "$n differs from the staged bytes" }
        }
        elseif ([long]$hit[0].size -ne (Get-Item -LiteralPath $local).Length) { $problems += "$n is $($hit[0].size) bytes, staged $((Get-Item -LiteralPath $local).Length)" }
    }
    $problems
}

function Test-StepRelease {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    if (-not $f.Quad) { return New-Judge $false 'runs after the bump' }
    $rel = Get-RunnerGhRelease
    if (-not $rel) { return New-Judge $false "no GitHub release $($f.Tag)" }
    if ($rel.draft) { return New-Judge $false "$($f.Tag) is still a draft" }
    if (Test-Path -LiteralPath (Join-Path $f.Stage 'MANIFEST.txt')) {
        $names = (Get-ReleaseAssetPlan).Release
        $problems = @(Compare-ReleaseAssets -Release $rel -Stage $f.Stage -Names $names)
        if ($problems.Count -gt 0) { return New-Judge $false ($problems -join '; ') }
        return New-Judge $true "published; $($names.Count) asset(s) match the staged bytes"
    }
    foreach ($a in $ctx.Arches) {
        $n = Get-MsixName $f.Quad $a
        if (@($rel.assets | Where-Object { $_.name -eq $n }).Count -eq 0) { return New-Judge $false "$($f.Tag) has no $n" }
    }
    New-Judge $true "published with the packages of $($f.Quad) (no staging here to compare bytes)"
}

function Test-StepFeed {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    if (-not $f.Quad) { return New-Judge $false 'runs after the bump' }
    $parts = @()
    foreach ($feed in (Get-RunnerFeeds)) {
        foreach ($a in $ctx.Arches) {
            $doc = Get-WaveeFeedDocument $script:Cfg.Repo $feed $a $script:Cfg.AssetPrefix
            $want = Get-MsixUri $f.Tag $f.Quad $a
            if (-not $doc) { return New-Judge $false "$feed/$a has no feed document" }
            if ("$($doc.Version)" -ne $f.Quad -or $doc.MsixUri -ne $want) { return New-Judge $false "$feed/$a serves $($doc.Version)" }
            $parts += "$feed/$a"
        }
    }
    New-Judge $true "$($parts -join ', ') serve $($f.Quad) from $($f.Tag)"
}

function Get-PrsToMove {
    param([string[]]$Bases)
    @(Get-RunnerOpenPrs) | Where-Object { $Bases -contains $_.baseRefName -and -not (Test-RunnerPr $_) }
}

function Test-StepOpenNext {
    $f = Get-RunnerFacts
    $prs = @(Get-PrsToMove @($f.Branch, 'main'))
    $t = @()
    if ($f.OriginNextSha) { $t += "$($f.NextBranch) exists" } else { $t += "$($f.NextBranch) does not exist yet" }
    if ($prs.Count -gt 0) { $t += "$($prs.Count) open PR(s) target $($f.Branch) or main: " + (@($prs | ForEach-Object { "#$($_.number)" }) -join ', ') }
    else { $t += "no open PR targets $($f.Branch) or main" }
    New-Judge ([bool]($f.OriginNextSha -and $prs.Count -eq 0)) ($t -join '; ')
}

function Test-StepMergeMain {
    $f = Get-RunnerFacts
    if (-not $f.ReleaseCommit) { return New-Judge $false 'runs after the release' }
    if ($f.OriginMainSha -and -not (Test-GitCommit $f.GitDir $f.OriginMainSha)) {
        Invoke-RepoGit -Dir $f.GitDir -Arguments @('fetch', 'origin', 'main', '--quiet') -AllowFailure | Out-Null
    }
    if (Test-GitAncestor $f.GitDir $f.ReleaseCommit $f.OriginMainSha) { return New-Judge $true "$(Format-Sha $f.ReleaseCommit) is in origin/main ($(Format-Sha $f.OriginMainSha))" }
    New-Judge $false "origin/main ($(Format-Sha $f.OriginMainSha)) does not contain $(Format-Sha $f.ReleaseCommit)"
}

function Test-StepForwardPort {
    $f = Get-RunnerFacts
    if (-not $f.Hotfix) { return New-Judge $true 'not a hotfix' 'N/A' }
    if (-not $f.OriginNextSha) { return New-Judge $false "origin has no $($f.NextBranch) to forward-port into" }
    if (-not (Test-GitCommit $f.GitDir $f.OriginNextSha)) {
        Invoke-RepoGit -Dir $f.GitDir -Arguments @('fetch', 'origin', $f.NextBranch, '--quiet') -AllowFailure | Out-Null
    }
    if (Test-GitAncestor $f.GitDir $f.OriginBranchSha $f.OriginNextSha) { return New-Judge $true "$($f.Branch) is in $($f.NextBranch)" }
    New-Judge $false "$($f.NextBranch) does not contain $($f.Branch) ($(Format-Sha $f.OriginBranchSha))"
}

function Read-StoreLedger {
    $f = Get-RunnerFacts
    if (-not (Test-Path -LiteralPath $f.StoreLedger)) { return $null }
    Get-Content -LiteralPath $f.StoreLedger -Raw | ConvertFrom-Json
}

# msstore's view when there is no ledger on this machine: does the current or last submission hold this Store quad,
# and is it past PendingCommit? Only asked once the release is public; $null when it cannot tell.
function Get-StoreQuadSubmission {
    $f = Get-RunnerFacts
    if (-not $f.OriginTagCommit -or -not (Test-RunnerCommand 'msstore')) { return $null }
    if ($f.ContainsKey('StoreSubmission')) { return $f.StoreSubmission }
    $f.StoreSubmission = $null
    try {
        $text = Invoke-MsStore -Arguments @('submission', 'get', $script:Cfg.StoreProductId) -AllowFailure -TimeoutSeconds 90
        $o = ConvertFrom-MsStoreJson $text
        $st = Get-StoreSubmissionState -StatusJson $text
        $pk = @()
        foreach ($p in $o.PSObject.Properties) { if ($p.Name -ieq 'applicationPackages') { $pk = @($p.Value) } }
        $has = @($pk | Where-Object { "$($_.version)" -eq $f.StoreQuad -or "$($_.Version)" -eq $f.StoreQuad }).Count -gt 0
        if ($has) { $f.StoreSubmission = @{ Id = $st.SubmissionId; Status = $st.Status } }
    }
    catch { $f.StoreSubmission = $null }
    $f.StoreSubmission
}

function Test-StepStoreSubmit {
    $f = Get-RunnerFacts
    if (-not $f.StoreQuad) { return New-Judge $false 'runs after the release' }
    $l = Read-StoreLedger
    if ($l) {
        if ($l.committed -and "$($l.quad)" -eq $f.StoreQuad) { return New-Judge $true "submission $($l.submissionId) committed ($($f.StoreQuad))" }
        $done = @()
        if ($l.phases) { $done = @($l.phases.PSObject.Properties | Where-Object { $_.Value -eq 'done' } | ForEach-Object { $_.Name }) }
        return New-Judge $false "the Store ledger has done: $($done -join ', '); not committed"
    }
    $s = Get-StoreQuadSubmission
    if ($s -and $s.Status -ne 'PendingCommit') { return New-Judge $true "msstore: submission $($s.Id) holds $($f.StoreQuad) ($($s.Status))" }
    New-Judge $false "no committed Store submission for $($f.StoreQuad)"
}

function Test-StepNotify {
    $f = Get-RunnerFacts
    if (-not $f.OriginTagCommit) { return New-Judge $false 'runs after the release is public' }
    $cmp = Get-ReleaseLinkedIssues
    if ($null -eq $cmp) { return New-Judge $false "no CHANGELOG [$($f.Version)] entry at the release commit" }
    $linked = @($cmp.Linked)
    if ($linked.Count -eq 0) { return New-Judge $true 'no issue to comment on' }
    $url = "https://github.com/$($script:Cfg.Repo)/releases/tag/$($f.Tag)"
    $missing = @()
    foreach ($l in $linked) {
        $r = Invoke-Native 'gh' @('api', "repos/$($script:Cfg.Repo)/issues/$($l.Issue)/comments", '--paginate', '--jq', '.[].body') -AllowFailure
        if ($r.ExitCode -ne 0 -or -not (@($r.Output | Where-Object { "$_" -like "*$url*" }).Count)) { $missing += "#$($l.Issue)" }
    }
    if ($missing.Count -gt 0) { return New-Judge $false "not yet commented: $($missing -join ', ')" }
    New-Judge $true "$($linked.Count) issue(s) carry the release comment"
}

function Test-StepStorePoll {
    $f = Get-RunnerFacts
    if (-not $f.StoreQuad) { return New-Judge $false 'runs after the Store submission' }
    $l = Read-StoreLedger
    if ($l -and "$($l.quad)" -eq $f.StoreQuad) {
        if ($l.phases -and "$($l.phases.poll)" -eq 'done') { return New-Judge $true "submission $($l.submissionId) is Published" }
        if ($l.committed) { return New-Judge $false "submission $($l.submissionId) is committed; not Published yet" 'WAITING' }
    }
    $s = Get-StoreQuadSubmission
    if ($s -and $s.Status -eq 'Published') { return New-Judge $true "msstore: submission $($s.Id) is Published" }
    if ($s) { return New-Judge $false "msstore: submission $($s.Id) is $($s.Status)" 'WAITING' }
    New-Judge $false 'no committed Store submission yet'
}

$script:StepJudges = @{
    'changelog'    = { Test-StepChangelog }
    'bump'         = { Test-StepBump }
    'notes'        = { Test-StepNotes }
    'tag'          = { Test-StepTag }
    'pack-arm64'   = { Test-StepPack -Arch 'arm64' }
    'pack-x64'     = { Test-StepPack -Arch 'x64' }
    'symbols'      = { Test-StepSymbols }
    'sign'         = { Test-StepSign }
    'stage'        = { Test-StepStage }
    'push'         = { Test-StepPush }
    'release'      = { Test-StepRelease }
    'feed'         = { Test-StepFeed }
    'open-next'    = { Test-StepOpenNext }
    'merge-main'   = { Test-StepMergeMain }
    'forward-port' = { Test-StepForwardPort }
    'store-submit' = { Test-StepStoreSubmit }
    'notify'       = { Test-StepNotify }
    'store-poll'   = { Test-StepStorePoll }
}

#endregion

#region Step plans (what a dry run prints)

function Get-StepPlan {
    param([Parameter(Mandatory = $true)][string]$Name)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $v = $f.Version
    $wt = $ctx.Wt
    $ewt = $ctx.Ewt
    $stage = $f.Stage
    $q = $f.Quad
    if (-not $q) { $q = '<quad>' }
    $rc = '<release commit>'
    if ($f.ReleaseCommit) { $rc = Format-Sha $f.ReleaseCommit }
    $lines = New-Object System.Collections.Generic.List[string]
    switch ($Name) {
        'changelog' {
            $lines.Add("dotnet run --project src/apps/Wavee.ReleaseTool -c Release -- changelog assemble --root $wt --version $v --json")
            $lines.Add("git -C $wt add -A -- CHANGELOG.md changes/unreleased $(($script:ChangelogPaths | Select-Object -Skip 1) -join ' ')")
            $lines.Add("git -C $wt commit -m `"release: CHANGELOG for $v`"")
            $lines.Add("(then curate CHANGELOG [$v] and ops/release/wavee/$v/whatsnew.json in $wt, commit, -Go again)")
        }
        'bump' {
            $c = ''
            if ($ctx.Codename) { $c = " <WaveeCodename>$($ctx.Codename)</WaveeCodename>" }
            $lines.Add("src/apps/Wavee/Wavee.Version.props: <WaveeVersion>$v</WaveeVersion>$c <WaveeBuild>$($f.Build)</WaveeBuild>")
            $lines.Add("CHANGELOG.md: '## [$v] - unreleased' -> '## [$v] - $([DateTime]::UtcNow.ToString('yyyy-MM-dd'))'")
            $lines.Add("git -C $wt commit -m `"release: Wavee $v $($f.Codename) (build $q)`" (only those two files)")
        }
        'notes' {
            $lines.Add("GET https://github.com/$($cfg.Repo)/releases/download/$($cfg.FeedRelease)/whatsnew-index.json -> $stage\previous-index.json (404 = first index; anything else stops)")
            $lines.Add("git -C $wt log $($f.PrevTag)..HEAD + rev-list --first-parent -> $stage\commits.json (firstParent per commit)")
            $h = ''
            if ($f.Hotfix) { $h = ' --hotfix' }
            $lines.Add("dotnet run --project src/apps/Wavee.ReleaseTool -c Release -- validate --semver $v --quad $q --codename $($f.Codename) --channel stable --changelog $wt\CHANGELOG.md --notes $wt\ops\release\wavee\$v --out $stage\notes --repo $($cfg.Repo) --previous-index <it> --previous-tag $($f.PrevTag) --commits $stage\commits.json$h  (GITHUB_TOKEN from gh auth token, in the environment only)")
            $lines.Add("check $stage\notes\RELEASE_BODY.md is under $($cfg.BodyCap) characters")
        }
        'tag' {
            $lines.Add("git -C $wt tag -a $($f.Tag) -m `"release: Wavee $v $($f.Codename) (build $q)`" $rc")
            $lines.Add("git -C $ewt tag -a $($f.Tag) -m `"Wavee $v $($f.Codename) (build $q) ships this engine commit`" $(Format-Sha $f.EngineSha)")
        }
        { $_ -in @('pack-arm64', 'pack-x64') } {
            $a = $Name.Substring(5)
            if ($a -eq 'x64' -and $ctx.X64Msix) {
                $lines.Add("copy $($ctx.X64Msix) -> $stage\$(Get-MsixName $q $a) (and its symbols zip when it sits beside it)")
            }
            else {
                $lines.Add("op read `"op://Personal/Wavee crash ingest key/credential`" --account $($cfg.OpAccount)  (once per run; the key is never printed)")
                $pub = ''
                if ($ctx.PublicOnly) { $pub = ' -PublicOnly' }
                $lines.Add("powershell -File $wt\ops\build\pack-wavee-msix.ps1 -Arch $a -Quad $q -Semver $v -Channel stable -Codename $($f.Codename) -Commit $rc -NotesDir $stage\notes -FeedRelease $($cfg.FeedRelease) -OutputDir $stage -CrashIngestUrl $($cfg.CrashIngestUrl) -CrashIngestKey <redacted> -NoSign$pub")
            }
            $lines.Add('assert identity, publisher, crash stamp evidence, PlayReady export, symbols zip; record the commit and the package hash')
        }
        'symbols' {
            foreach ($a in $ctx.Arches) {
                $lines.Add("dotnet run --project src/apps/Wavee.ReleaseTool -c Release -- symbol-map --pdb $stage\symbols\$q\win-$a\Wavee.pdb --exe <Wavee.exe of the $a package> --out $stage\symbols\$q\win-$a\Wavee.symmap")
                $lines.Add("wrangler r2 object put wavee-crash/symbols/$q/win-$a.symmap (+ win-$a/Wavee.exe, win-$a/$(Get-SymbolsZipName $q $a)) --remote  (up to 4 tries each)")
            }
        }
        'sign' {
            $msix = @($ctx.Arches | ForEach-Object { "$stage\$(Get-MsixName $q $_)" }) -join ' '
            $lines.Add("signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib <Azure.CodeSigning.Dlib.dll> /dmdf $wt\ops\build\signing\metadata.json $msix")
            $lines.Add('signtool verify /pa /q <each package>')
        }
        'stage' {
            foreach ($a in $ctx.Arches) { $lines.Add("write $stage\$(Get-AppInstallerName $a) (Version $q, MainPackage $(Get-MsixUri $f.Tag $q $a))") }
            $lines.Add("copy whatsnew.json, whatsnew-index.json, RELEASE_BODY.md and the media flat into $stage; write $stage\MANIFEST.txt (sha256)")
        }
        'push' {
            $lines.Add("git -C $wt push origin HEAD:refs/heads/$($f.Branch)  (fast-forward only; stops when origin moved)")
            $lines.Add("git -C $wt push origin refs/tags/$($f.Tag)")
            $lines.Add("git -C $ewt push origin refs/tags/$($f.Tag)")
        }
        'release' {
            $lines.Add("gh release create $($f.Tag) --repo $($cfg.Repo) --draft --verify-tag --title `"Wavee $v $($script:EmDash) $($f.Codename)`" --notes-file $stage\RELEASE_BODY.md")
            $lines.Add("gh release upload $($f.Tag) --repo $($cfg.Repo) --clobber <every release asset of $stage>")
            $lines.Add("gh release edit $($f.Tag) --repo $($cfg.Repo) --draft=false --latest=false")
            $lines.Add('compare every asset with the staged bytes (digest), HEAD every package for its Content-Length')
        }
        'feed' {
            $feeds = @($cfg.FeedRelease)
            try { $feeds = @(Get-RunnerFeeds) } catch { }
            foreach ($fd in $feeds) {
                $lines.Add("gh release upload $fd --repo $($cfg.Repo) --clobber $(($ctx.Arches | ForEach-Object { Get-AppInstallerName $_ }) -join ' ') whatsnew-index.json")
            }
            $lines.Add("poll each feed document until it says $q and points at $($f.Tag)  (the moment installs see it)")
        }
        'open-next' {
            if (-not $f.OriginNextSha) {
                $lines.Add("gh api -X POST repos/$($cfg.Repo)/git/refs -f ref=refs/heads/$($f.NextBranch) -f sha=<head of $($f.Branch)>")
            }
            $prs = @()
            try { $prs = @(Get-PrsToMove @($f.Branch, 'main')) } catch { }
            $lines.Add("for each open PR on $($f.Branch) or main (now: $(if ($prs.Count) { @($prs | ForEach-Object { "#$($_.number)" }) -join ', ' } else { 'none' })):")
            $lines.Add("  gh api -X PATCH repos/$($cfg.Repo)/pulls/<n> -f base=$($f.NextBranch)")
            $lines.Add("  gh pr comment <n> --repo $($cfg.Repo) --body-file <`"Wavee $v shipped; this PR now targets ``$($f.NextBranch)``, the next release branch. Nothing else changes.`">")
        }
        'merge-main' {
            $lines.Add("gh pr create --repo $($cfg.Repo) --base main --head $($f.Branch) --title `"Wavee $v $($f.Codename)`" --body-file <file>  (or reuse the open one)")
            $lines.Add("gh pr merge <n> --repo $($cfg.Repo) --merge  (a merge commit; never --delete-branch)")
        }
        'forward-port' {
            if (-not $f.Hotfix) { $lines.Add('nothing: not a hotfix') }
            else {
                $lines.Add("gh pr create --repo $($cfg.Repo) --base $($f.NextBranch) --head $($f.Branch) --title `"Forward-port Wavee $v into $($f.NextBranch)`" --body-file <file>")
                $lines.Add("gh pr merge <n> --repo $($cfg.Repo) --merge  (a conflict stops the step)")
            }
        }
        'store-submit' {
            $r = ''
            if (Test-Path -LiteralPath $f.StoreLedger) { $r = ' -Resume' }
            $lines.Add("powershell -File $wt\ops\release\wavee-store-submit.ps1 -Semver $v -PollMinutes 0$r  (packs, uploads and commits the Store submission $($f.StoreQuad))")
        }
        'notify' {
            $cmp = $null
            try { $cmp = Get-ReleaseLinkedIssues } catch { }
            if ($null -eq $cmp) { $lines.Add("gh issue comment <n> --repo $($cfg.Repo) --body <shipped in Wavee $v> for every issue the CHANGELOG [$v] entry cites and a commit closes (known after changelog)") }
            elseif (@($cmp.Linked).Count -eq 0) { $lines.Add('nothing: no linked issue') }
            else { foreach ($l in $cmp.Linked) { $lines.Add("gh issue comment $($l.Issue) --repo $($cfg.Repo) --body <shipped in Wavee $v $($f.Codename) (build $q)>") } }
        }
        'store-poll' {
            $lines.Add("powershell -File $wt\ops\release\wavee-store-submit.ps1 -Semver $v -Resume -PollMinutes $($ctx.PollMinutes)  (not Published within the run = WAITING, not a failure)")
        }
    }
    $lines.ToArray()
}

#endregion

#region Step runs (-Go)

function Stop-RunnerStep {
    param([string]$Message)
    throw $Message
}

function Invoke-StepChangelog {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $wt = $ctx.Wt
    $tool = Join-Path $wt 'src\apps\Wavee.ReleaseTool'
    $r = Invoke-RunnerNative 'dotnet' @('run', '--project', $tool, '-c', $script:Cfg.Configuration, '--', 'changelog', 'assemble', '--root', $wt, '--version', $f.Version, '--json') -Echo -AllowFailure
    $json = $null
    try { $json = ConvertFrom-GhJson ($r.Output -join "`n") } catch { $json = $null }
    if ($r.ExitCode -eq 2) {
        $p = @()
        if ($json -and $json.problems) { $p = @($json.problems | ForEach-Object { "$_" }) }
        if ($p.Count -eq 0) { $p = @($r.Output | Select-Object -Last 6) }
        Stop-RunnerStep ('changelog assemble refused: ' + ($p -join ' / '))
    }
    if ($r.ExitCode -ne 0) { Stop-RunnerStep ("changelog assemble exited $($r.ExitCode): " + (@($r.Output | Select-Object -Last 6) -join ' / ')) }

    $named = @()
    if ($json) {
        foreach ($k in @('files', 'written', 'changed', 'deleted')) {
            if ($json.PSObject.Properties[$k]) { $named += @($json.$k | ForEach-Object { ("$_" -replace '\\', '/') }) }
        }
    }
    $changed = @((Invoke-RepoGit -Dir $wt -Arguments @('diff', '--name-only', 'HEAD') -DropAdvice).Lines) +
        @((Invoke-RepoGit -Dir $wt -Arguments @('ls-files', '--others', '--exclude-standard') -DropAdvice).Lines)
    $changed = @($changed | Where-Object { $_ } | Sort-Object -Unique)
    if ($changed.Count -eq 0) { Stop-RunnerStep 'changelog assemble changed nothing' }
    $stray = @($changed | Where-Object {
        $p = $_
        -not ($script:ChangelogPaths -contains $p -or $p -like 'changes/unreleased/*' -or $named -contains $p)
    })
    if ($stray.Count -gt 0) { Stop-RunnerStep ("changelog assemble changed paths it should not: $($stray -join ', '); nothing was committed (inspect $wt)") }
    Invoke-RepoGit -Dir $wt -Arguments (@('add', '-A', '--') + $changed) -Echo | Out-Null
    Invoke-RepoGit -Dir $wt -Arguments @('commit', '-m', "release: CHANGELOG for $($f.Version)") -Echo | Out-Null
    $sha = (Invoke-RepoGit -Dir $wt -Arguments @('rev-parse', 'HEAD')).Text
    $Extra.commitMade = $sha
    Write-RunnerLine ''
    Write-RunnerLine "  The CHANGELOG [$($f.Version)] section is assembled in $wt." 'Cyan'
    Write-RunnerLine "  Curate it and the What's new highlights (ops\release\wavee\$($f.Version)\whatsnew.json) there, commit, and -Go again." 'Cyan'
    "assembled $($changed.Count) path(s); committed $(Format-Sha $sha) release: CHANGELOG for $($f.Version)"
}

function Invoke-StepBump {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $wt = $ctx.Wt
    $props = Join-Path $wt 'src\apps\Wavee\Wavee.Version.props'
    $cl = Join-Path $wt 'CHANGELOG.md'
    $codename = $f.Codename
    $setCodename = ''
    if ($ctx.Codename -and $ctx.Codename -ne $f.Props.Codename) { $setCodename = $ctx.Codename }
    Set-WaveeVersionProps $props $f.Version $setCodename
    Set-WaveeBuild $props $f.Build
    Write-RunnerLine "  Wavee.Version.props -> $($f.Version) $codename build $($f.Build)"
    $today = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    if (Set-ChangelogReleaseDate -Path $cl -Semver $f.Version -Date $today) { Write-RunnerLine "  CHANGELOG [$($f.Version)] dated $today" }
    $want = @('CHANGELOG.md', 'src/apps/Wavee/Wavee.Version.props')
    $actual = @((Invoke-RepoGit -Dir $wt -Arguments @('diff', '--name-only') -DropAdvice).Lines | Sort-Object -Unique)
    $stray = @($actual | Where-Object { $want -notcontains $_ })
    if ($stray.Count -gt 0 -or $actual -notcontains 'src/apps/Wavee/Wavee.Version.props') {
        Invoke-RepoGit -Dir $wt -Arguments @('checkout', '--', 'CHANGELOG.md', 'src/apps/Wavee/Wavee.Version.props') -AllowFailure | Out-Null
        Stop-RunnerStep "the bump touched [$($actual -join ', ')] but should touch only [$($want -join ', ')]; the two files were restored"
    }
    $quad = ConvertTo-WaveeQuad $f.Version $f.Build
    $msg = "release: Wavee $($f.Version) $codename (build $quad)"
    Invoke-RepoGit -Dir $wt -Arguments (@('add', '--') + $actual) -Echo | Out-Null
    Invoke-RepoGit -Dir $wt -Arguments @('commit', '-m', $msg) -Echo | Out-Null
    $sha = (Invoke-RepoGit -Dir $wt -Arguments @('rev-parse', 'HEAD')).Text
    "committed $(Format-Sha $sha) $msg"
}

function Invoke-StepNotes {
    param($Extra)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $wt = $ctx.Wt
    $stage = $f.Stage
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    if (Test-Path -LiteralPath $f.NotesOut) { Remove-Item -LiteralPath $f.NotesOut -Recurse -Force }
    $prev = Get-WaveePreviousIndex -Repo $cfg.Repo -FeedRelease $cfg.FeedRelease -OutFile (Join-Path $stage 'previous-index.json')
    if (-not $prev) { Write-RunnerLine '  no whatsnew-index.json on the feed yet; this release starts the index' }
    $ctx.State.previousIndexRead = [bool]$prev
    $Extra.previousIndexRead = [bool]$prev
    Save-RunnerState

    $toolArgs = @('run', '--project', (Join-Path $wt 'src\apps\Wavee.ReleaseTool'), '-c', $cfg.Configuration, '--',
        'validate',
        '--semver', $f.Version,
        '--quad', $f.Quad,
        '--codename', $f.Codename,
        '--channel', 'stable',
        '--changelog', (Join-Path $wt 'CHANGELOG.md'),
        '--notes', (Join-Path $wt "ops\release\wavee\$($f.Version)"),
        '--out', $f.NotesOut,
        '--repo', $cfg.Repo)
    if ($prev) { $toolArgs += @('--previous-index', $prev) }
    if ($f.PrevTag) {
        $commits = @(Get-RangeCommits 'HEAD')
        $first = @{}
        foreach ($l in (Invoke-RepoGit -Dir $wt -Arguments @('rev-list', '--first-parent', "$($f.PrevTag)..HEAD")).Lines) { $first[$l] = $true }
        foreach ($c in $commits) { $c | Add-Member -NotePropertyName FirstParent -NotePropertyValue ([bool]$first[$c.Sha]) -Force }
        $commitsPath = Join-Path $stage 'commits.json'
        Write-ReleaseCommitsJson $commits $commitsPath
        $toolArgs += @('--previous-tag', $f.PrevTag, '--commits', $commitsPath)
        Write-RunnerLine "  commits.json: $($commits.Count) commit(s), $($first.Count) on the first-parent line"
    }
    if ($f.Hotfix) { $toolArgs += '--hotfix' }

    # The token reaches the tool through the environment only: never a command line, a log or the state.
    $previousToken = $env:GITHUB_TOKEN
    try {
        $token = Get-GhAuthToken
        if ($token) { $env:GITHUB_TOKEN = $token; $ctx.Secrets = @($ctx.Secrets) + @($token) }
        else { Write-RunnerLine '  warn: no gh token; issue/PR titles and the generated commit list are omitted' 'Yellow' }
        $r = Invoke-RunnerNative 'dotnet' $toolArgs -Echo -AllowFailure
    }
    finally { $env:GITHUB_TOKEN = $previousToken }
    if ($r.ExitCode -ne 0) {
        Stop-RunnerStep ("Wavee.ReleaseTool validate exited $($r.ExitCode) (fix whatsnew.json or CHANGELOG.md in $wt, commit, -Go again): " + (@($r.Output | Select-Object -Last 8) -join ' / '))
    }
    foreach ($n in @('whatsnew.json', 'whatsnew-index.json', 'RELEASE_BODY.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $f.NotesOut $n))) { Stop-RunnerStep "Wavee.ReleaseTool did not write $n into $($f.NotesOut)" }
    }
    $body = [IO.File]::ReadAllText((Join-Path $f.NotesOut 'RELEASE_BODY.md'))
    if ($body.Length -ge $cfg.BodyCap) { Stop-RunnerStep "RELEASE_BODY.md is $($body.Length) characters; GitHub caps a release body at $($cfg.BodyCap)" }
    "notes -> $($f.NotesOut) (body $($body.Length) characters)"
}

function Invoke-StepTag {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    if (-not $f.ReleaseCommit) { Stop-RunnerStep 'no release commit' }
    if (-not $f.EngineSha) { Stop-RunnerStep 'engine.lock names no engine commit' }
    $msg = "release: Wavee $($f.Version) $($f.Codename) (build $($f.Quad))"
    if ($f.OriginTagCommit -and $f.OriginTagCommit -ne $f.ReleaseCommit) { Stop-RunnerStep "$($f.Tag) is on origin at $(Format-Sha $f.OriginTagCommit); a pushed tag is never moved" }
    if ($f.LocalTagCommit -ne $f.ReleaseCommit) {
        if ($f.LocalTagCommit) { Invoke-RepoGit -Dir $ctx.Wt -Arguments @('tag', '-d', $f.Tag) -Echo | Out-Null }
        Invoke-RepoGit -Dir $ctx.Wt -Arguments @('tag', '-a', $f.Tag, '-m', $msg, $f.ReleaseCommit) -Echo | Out-Null
    }
    if ($f.EngineOriginTagCommit -and $f.EngineOriginTagCommit -ne $f.EngineSha) { Stop-RunnerStep "fluent-gpu has $($f.Tag) on origin at $(Format-Sha $f.EngineOriginTagCommit); a pushed tag is never moved" }
    if ($f.EngineLocalTagCommit -ne $f.EngineSha) {
        if ($f.EngineLocalTagCommit) { Invoke-RepoGit -Dir $ctx.Ewt -Arguments @('tag', '-d', $f.Tag) -Echo | Out-Null }
        Invoke-RepoGit -Dir $ctx.Ewt -Arguments @('tag', '-a', $f.Tag, '-m', "Wavee $($f.Version) $($f.Codename) (build $($f.Quad)) ships this engine commit", $f.EngineSha) -Echo | Out-Null
    }
    "$($f.Tag) -> $(Format-Sha $f.ReleaseCommit); fluent-gpu $($f.Tag) -> $(Format-Sha $f.EngineSha)"
}

function Invoke-StepPack {
    param([Parameter(Mandatory = $true)][string]$Arch, $Extra)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $q = $f.Quad
    $stage = $f.Stage
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Sync-EngineWorktree -EngineSha $f.EngineSha
    $crash = Get-RunnerCrashKey
    $msix = Join-Path $stage (Get-MsixName $q $Arch)
    $zip = Join-Path $stage (Get-SymbolsZipName $q $Arch)
    $symDir = Join-Path $stage "symbols\$q\win-$Arch"
    foreach ($p in @($msix, $zip)) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
    if (Test-Path -LiteralPath $symDir) { Remove-Item -LiteralPath $symDir -Recurse -Force }
    $adopted = $false
    if ($Arch -eq 'x64' -and $ctx.X64Msix) {
        Write-RunnerLine "  > copy $($ctx.X64Msix) -> $msix" 'DarkGray'
        Copy-Item -LiteralPath $ctx.X64Msix -Destination $msix -Force
        $prebuilt = Join-Path (Split-Path -Parent $ctx.X64Msix) (Get-SymbolsZipName $q $Arch)
        if (Test-Path -LiteralPath $prebuilt) { Copy-Item -LiteralPath $prebuilt -Destination $zip -Force }
        else { Write-RunnerLine "  warn: no $(Get-SymbolsZipName $q $Arch) next to $($ctx.X64Msix); a crash report from the x64 package cannot be symbolicated" 'Yellow' }
        $adopted = $true
    }
    else {
        $packArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $ctx.Wt 'ops\build\pack-wavee-msix.ps1'),
            '-Arch', $Arch,
            '-Quad', $q,
            '-Semver', $f.Version,
            '-Channel', 'stable',
            '-Codename', $f.Codename,
            '-IdentityName', $cfg.IdentityName,
            '-Commit', (Format-Sha $f.ReleaseCommit),
            '-BuildDate', [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'),
            '-NotesDir', $f.NotesOut,
            '-FeedRelease', $cfg.FeedRelease,
            '-Publisher', $cfg.Publisher,
            '-OutputDir', $stage,
            '-CrashIngestUrl', $cfg.CrashIngestUrl,
            '-CrashIngestKey', $crash.Key,
            '-NoSign',
            '-Configuration', $cfg.Configuration)
        if ($ctx.PublicOnly) { $packArgs += '-PublicOnly' }
        Write-RunnerLine "  packing $Arch (the output goes to the step log)"
        $r = Invoke-RunnerChild 'powershell' $packArgs
        if ($r.ExitCode -ne 0) { Stop-RunnerStep ("pack-wavee-msix.ps1 -Arch $Arch exited $($r.ExitCode): " + (@($r.Tail | Select-Object -Last 6) -join ' / ')) }
        if (-not (Test-Path -LiteralPath $zip)) { Stop-RunnerStep "the pack did not produce $zip (the PDB of exactly that exe)" }
    }
    if (-not (Test-Path -LiteralPath $msix)) { Stop-RunnerStep "the pack did not produce $msix" }
    $id = Get-MsixIdentity $msix
    if ($id.Version -ne $q -or $id.ProcessorArchitecture -ne $Arch -or $id.Name -ne $cfg.IdentityName) {
        Stop-RunnerStep "package identity mismatch in $(Split-Path -Leaf $msix): Name=$($id.Name) Version=$($id.Version) Arch=$($id.ProcessorArchitecture); expected $($cfg.IdentityName) / $q / $Arch"
    }
    if ($id.Publisher -ne $cfg.Publisher) { Stop-RunnerStep "publisher mismatch in $(Split-Path -Leaf $msix): '$($id.Publisher)' (signing would fail with 0x8007000B)" }
    $stamp = Assert-CrashIngestStamp -Msix $msix -Url $cfg.CrashIngestUrl -Key $crash.Key
    Write-RunnerLine "  stamp evidence ${Arch}: $stamp" 'Green'
    Assert-PlayReadyNativeExport -MsixPath $msix -Arch $Arch
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'THIRD-PARTY-NOTICES.txt'))) { Stop-RunnerStep "pack-wavee-msix.ps1 did not copy THIRD-PARTY-NOTICES.txt into $stage" }
    $Extra.adopted = $adopted
    $Extra.msixSha256 = Get-FileSha256 $msix
    $Extra.stamped = $true
    "$(Split-Path -Leaf $msix) $([math]::Round((Get-Item -LiteralPath $msix).Length / 1MB, 2)) MB from $(Format-Sha $f.ReleaseCommit)"
}

function Invoke-StepSymbols {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $q = $f.Quad
    $wrangler = Get-WranglerPath -RepoRoot $ctx.Wt
    $byArch = @{}
    foreach ($a in $ctx.Arches) {
        Write-RunnerLine "  symbol-map $a"
        $dir = Join-Path $f.Stage "symbols\$q\win-$a"
        $r = Publish-WaveeSymbolMap -Msix (Join-Path $f.Stage (Get-MsixName $q $a)) -SymbolsDir $dir `
            -SymbolsZip (Join-Path $f.Stage (Get-SymbolsZipName $q $a)) -Quad $q -Arch $a `
            -ReleaseToolProject (Join-Path $ctx.Wt 'src\apps\Wavee.ReleaseTool') -Wrangler $wrangler
        Write-RunnerLine "  uploaded -> wavee-crash/$($r.Key) ($([math]::Round($r.Bytes / 1KB, 1)) KB)" 'Green'
        if (@($r.BinaryKeys).Count -eq 0) { Write-RunnerLine "  warn: no symbols zip for ${a}: Wavee.exe and Wavee.pdb were not uploaded" 'Yellow' }
        else { foreach ($k in $r.BinaryKeys) { Write-RunnerLine "  uploaded -> wavee-crash/$k" 'Green' } }
        $byArch[$a] = @{ symmap = (Get-FileSha256 $r.Symmap); symbolsTxt = (Get-FileSha256 (Join-Path $dir 'SYMBOLS.txt')); key = $r.Key; binaries = @($r.BinaryKeys) }
    }
    $Extra.quad = $q
    $Extra.arches = $byArch
    "symbols/$q/win-<arch>.symmap uploaded for $($ctx.Arches -join ', ')"
}

function Invoke-StepSign {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $tools = Get-RunnerSdkTools
    $toSign = @()
    foreach ($a in $ctx.Arches) {
        $msix = Join-Path $f.Stage (Get-MsixName $f.Quad $a)
        if (Test-MsixSignature $msix $tools.SignTool) { Write-RunnerLine "  $(Split-Path -Leaf $msix) already carries a trusted signature"; continue }
        $toSign += $msix
    }
    if ($toSign.Count -gt 0) {
        Write-RunnerLine "  > signtool sign ... $($toSign -join ' ')" 'DarkGray'
        Invoke-TrustedSigning -Path $toSign -Metadata (Join-Path $ctx.Wt 'ops\build\signing\metadata.json') -Subscription $script:Cfg.Subscription -SignTool $tools.SignTool
    }
    foreach ($a in $ctx.Arches) {
        $msix = Join-Path $f.Stage (Get-MsixName $f.Quad $a)
        if (-not (Test-MsixSignature $msix $tools.SignTool)) { Stop-RunnerStep "signature did not verify: $msix" }
    }
    "signed $($toSign.Count), verified $($ctx.Arches.Count)"
}

function Invoke-StepStage {
    param($Extra)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $stage = $f.Stage
    try {
        Assert-WaveeIndexSafeToPublish -Repo $cfg.Repo -FeedRelease $cfg.FeedRelease -PreviousIndexRead ([bool]$ctx.State.previousIndexRead) -Phase 'stage'
    }
    catch {
        (Get-StateSteps).Remove('notes')
        Save-RunnerState
        Stop-RunnerStep "$($_.Exception.Message) The notes step runs again on the next -Go."
    }
    $template = Join-Path $ctx.Wt 'ops\build\Wavee.AppInstaller.template.xml'
    foreach ($a in $ctx.Arches) {
        $out = Join-Path $stage (Get-AppInstallerName $a)
        New-WaveeAppInstaller -Template $template -OutFile $out -Arch $a -Quad $f.Quad -Publisher $cfg.Publisher `
            -IdentityName $cfg.IdentityName -FeedUri (Get-FeedUri $a) -MsixUri (Get-MsixUri $f.Tag $f.Quad $a) | Out-Null
        Write-RunnerLine "  $(Split-Path -Leaf $out) Version=$($f.Quad) -> $(Get-MsixUri $f.Tag $f.Quad $a)"
    }
    foreach ($n in @('whatsnew.json', 'whatsnew-index.json', 'RELEASE_BODY.md')) {
        $from = Join-Path $f.NotesOut $n
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from (Join-Path $stage $n) -Force }
    }
    $media = @(Copy-WaveeReleaseMedia -NotesOut $f.NotesOut -Stage $stage)
    if ($media.Count -gt 0) { Write-RunnerLine "  media: $($media -join ', ')" }
    $plan = Get-ReleaseAssetPlan
    Write-ReleaseManifest -Dir $stage -Files $plan.Manifest -OutFile (Join-Path $stage 'MANIFEST.txt') | Out-Null
    $Extra.releaseAssets = $plan.Release
    $Extra.feedAssets = $plan.Feed
    "MANIFEST.txt over $($plan.Manifest.Count) file(s) in $stage"
}

function Invoke-StepPush {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $wt = $ctx.Wt
    Invoke-RepoGit -Dir $wt -Arguments @('fetch', 'origin', $f.Branch, '--quiet') -AllowFailure | Out-Null
    $head = (Invoke-RepoGit -Dir $wt -Arguments @('rev-parse', 'HEAD')).Text
    if ($head -ne $f.ReleaseCommit) { Stop-RunnerStep "the release worktree HEAD is $(Format-Sha $head), the release commit is $(Format-Sha $f.ReleaseCommit)" }
    $origin = $f.OriginBranchSha
    if ($origin -and -not (Test-GitAncestor $wt $origin $head)) {
        Stop-RunnerStep "origin/$($f.Branch) moved since the runner's commits: merge origin into the worktree branch (git -C $wt merge origin/$($f.Branch)) and -Go again"
    }
    if (-not ($origin -and (Test-GitAncestor $wt $head $origin))) {
        Invoke-RepoGit -Dir $wt -Arguments @('push', 'origin', "HEAD:refs/heads/$($f.Branch)") -Echo | Out-Null
    }
    if ($f.OriginTagCommit -ne $f.ReleaseCommit) { Invoke-RepoGit -Dir $wt -Arguments @('push', 'origin', "refs/tags/$($f.Tag)") -Echo | Out-Null }
    if ($f.EngineOriginTagCommit -ne $f.EngineSha) { Invoke-RepoGit -Dir $ctx.Ewt -Arguments @('push', 'origin', "refs/tags/$($f.Tag)") -Echo | Out-Null }
    "pushed $($f.Branch) and $($f.Tag) at $(Format-Sha $head); fluent-gpu $($f.Tag) at $(Format-Sha $f.EngineSha)"
}

function Invoke-StepRelease {
    param($Extra)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $plan = Get-ReleaseAssetPlan
    $paths = @($plan.Release | ForEach-Object { Join-Path $f.Stage $_ })
    $title = "Wavee $($f.Version) $($script:EmDash) $($f.Codename)"
    Write-RunnerLine "  > gh release create/upload/edit $($f.Tag) ($($paths.Count) assets, --latest=false)" 'DarkGray'
    Publish-WaveeRelease -Repo $cfg.Repo -Tag $f.Tag -Title $title -BodyFile (Join-Path $f.Stage 'RELEASE_BODY.md') -Assets $paths
    Reset-RunnerFacts
    $rel = Get-RunnerGhRelease
    if (-not $rel -or $rel.draft) { Stop-RunnerStep "$($f.Tag) is not published" }
    $problems = @(Compare-ReleaseAssets -Release $rel -Stage $f.Stage -Names $plan.Release)
    if ($problems.Count -gt 0) { Stop-RunnerStep ($problems -join '; ') }
    foreach ($a in $ctx.Arches) {
        $n = Get-MsixName $f.Quad $a
        $url = Get-GhReleaseAssetUrl -Repo $cfg.Repo -Tag $f.Tag -Name $n
        $bytes = (Get-Item -LiteralPath (Join-Path $f.Stage $n)).Length
        if (-not (Test-AssetContentLength -Url $url -ExpectedBytes $bytes)) { Stop-RunnerStep "the published $n does not report $bytes bytes; App Installer needs an exact Content-Length" }
        Write-RunnerLine "  $n serves $bytes bytes" 'Green'
    }
    "https://github.com/$($cfg.Repo)/releases/tag/$($f.Tag) ($($plan.Release.Count) assets)"
}

function Invoke-StepFeed {
    param($Extra)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    try {
        Assert-WaveeIndexSafeToPublish -Repo $cfg.Repo -FeedRelease $cfg.FeedRelease -PreviousIndexRead ([bool]$ctx.State.previousIndexRead) -Phase 'feed'
    }
    catch { Stop-RunnerStep $_.Exception.Message }
    $plan = Get-ReleaseAssetPlan
    $paths = @($plan.Feed | ForEach-Object { Join-Path $f.Stage $_ })
    $feeds = @(Get-RunnerFeeds)
    foreach ($fd in $feeds) {
        Write-RunnerLine "  > gh release upload $fd --clobber $($plan.Feed -join ' ')" 'DarkGray'
        Update-WaveeFeed -Repo $cfg.Repo -FeedRelease $fd -FeedBodyFile (Join-Path $ctx.Wt 'ops\release\feed-release-body.md') -Assets $paths -Target 'main'
        $frel = Get-GhRelease $cfg.Repo $fd
        foreach ($n in $plan.Feed) {
            $hit = @($frel.assets | Where-Object { $_.name -eq $n })
            $local = (Get-Item -LiteralPath (Join-Path $f.Stage $n)).Length
            if ($hit.Count -eq 0) { Stop-RunnerStep "feed $fd is missing $n" }
            if ([long]$hit[0].size -ne $local) { Stop-RunnerStep "feed asset $n is $($hit[0].size) bytes on $fd, staged $local" }
        }
        foreach ($a in $ctx.Arches) {
            $live = Test-WaveeFeedLive -Repo $cfg.Repo -FeedRelease $fd -Arch $a -AssetPrefix $cfg.AssetPrefix `
                -ExpectedQuad $f.Quad -ExpectedMsixUri (Get-MsixUri $f.Tag $f.Quad $a) -Retries 8 -DelaySeconds 10
            if (-not $live) { Stop-RunnerStep "feed $fd/$a did not come up at $($f.Quad) pointing at $($f.Tag) after 8 reads over ~70 s" }
            Write-RunnerLine "  $fd/$a is live at $($f.Quad)" 'Green'
        }
    }
    "$($feeds -join ', ') serve $($f.Quad)"
}

function Write-RunnerTempFile {
    param([string]$Name, [string]$Text)
    $dir = $script:Ctx.StateDir
    if (-not $dir) { $dir = [IO.Path]::GetTempPath() }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $p = Join-Path $dir $Name
    [IO.File]::WriteAllText($p, $Text, (New-Object System.Text.UTF8Encoding $false))
    $p
}

# Moves one PR to another base through the REST API (gh pr edit trips over the retired classic projects) and leaves
# one comment.
function Move-RunnerPr {
    param($Pr, [string]$Base, [string]$Comment)
    $repo = $script:Cfg.Repo
    Invoke-RunnerNative 'gh' @('api', '-X', 'PATCH', "repos/$repo/pulls/$($Pr.number)", '-f', "base=$Base") -Echo | Out-Null
    $file = Write-RunnerTempFile "pr-$($Pr.number)-comment.md" $Comment
    Invoke-RunnerNative 'gh' @('pr', 'comment', "$($Pr.number)", '--repo', $repo, '--body-file', $file) -Echo | Out-Null
}

function Invoke-StepOpenNext {
    param($Extra)
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    if (-not $f.OriginNextSha) {
        Invoke-RunnerNative 'gh' @('api', '-X', 'POST', "repos/$($cfg.Repo)/git/refs", '-f', "ref=refs/heads/$($f.NextBranch)", '-f', "sha=$($f.OriginBranchSha)") -Echo | Out-Null
        Write-RunnerLine "  $($f.NextBranch) created at $(Format-Sha $f.OriginBranchSha)" 'Green'
    }
    $moved = @()
    foreach ($pr in (Get-PrsToMove @($f.Branch, 'main'))) {
        $text = "Wavee $($f.Version) shipped; this PR now targets ``$($f.NextBranch)``, the next release branch. Nothing else changes."
        Move-RunnerPr -Pr $pr -Base $f.NextBranch -Comment $text
        $moved += $pr.number
    }
    $Extra.moved = @($moved)
    $list = 'none'
    if ($moved.Count) { $list = '#' + ($moved -join ', #') }
    "$($f.NextBranch) is open; moved $($moved.Count) PR(s): $list"
}

function Find-RunnerPr {
    param([string]$Head, [string]$Base)
    @(Get-RunnerOpenPrs) | Where-Object { $_.headRefName -eq $Head -and $_.baseRefName -eq $Base -and -not $_.isCrossRepository } | Select-Object -First 1
}

function New-RunnerPr {
    param([string]$Head, [string]$Base, [string]$Title, [string]$Body)
    $file = Write-RunnerTempFile ("pr-" + ($Head -replace '[^\w.-]', '_') + '-into-' + ($Base -replace '[^\w.-]', '_') + '.md') $Body
    $r = Invoke-RunnerNative 'gh' @('pr', 'create', '--repo', $script:Cfg.Repo, '--base', $Base, '--head', $Head, '--title', $Title, '--body-file', $file) -Echo
    $m = [regex]::Match(($r.Output -join "`n"), '/pull/(\d+)')
    if (-not $m.Success) { Stop-RunnerStep "gh pr create printed no pull request URL" }
    [int]$m.Groups[1].Value
}

function Invoke-StepMergeMain {
    param($Extra)
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $pr = Find-RunnerPr -Head $f.Branch -Base 'main'
    if ($pr) { $n = [int]$pr.number }
    else {
        $body = "Wavee $($f.Version) $($f.Codename) (build $($f.Quad)) shipped: https://github.com/$($cfg.Repo)/releases/tag/$($f.Tag)`n`n" +
            "The release runner merges ``$($f.Branch)`` into ``main`` with a merge commit, so ``main`` shows what users can install. The release branch stays."
        $n = New-RunnerPr -Head $f.Branch -Base 'main' -Title "Wavee $($f.Version) $($f.Codename)" -Body $body
    }
    $Extra.pr = $n
    $r = Invoke-RunnerNative 'gh' @('pr', 'merge', "$n", '--repo', $cfg.Repo, '--merge') -Echo -AllowFailure
    if ($r.ExitCode -ne 0) { Stop-RunnerStep ("gh pr merge $n refused (a review or a rule may block it; merge it with a merge commit by hand, never deleting the branch, then -Go again): " + (@($r.Output | Select-Object -Last 4) -join ' / ')) }
    Invoke-RepoGit -Dir $f.GitDir -Arguments @('fetch', 'origin', 'main', '--quiet') -AllowFailure | Out-Null
    "merged PR #$n $($f.Branch) -> main"
}

function Invoke-StepForwardPort {
    param($Extra)
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $pr = Find-RunnerPr -Head $f.Branch -Base $f.NextBranch
    if ($pr) { $n = [int]$pr.number }
    else {
        $body = "The hotfix Wavee $($f.Version) shipped from ``$($f.Branch)``; this merges it forward into ``$($f.NextBranch)`` (a merge commit), so the next release carries the fix."
        $n = New-RunnerPr -Head $f.Branch -Base $f.NextBranch -Title "Forward-port Wavee $($f.Version) into $($f.NextBranch)" -Body $body
    }
    $Extra.pr = $n
    $state = 'UNKNOWN'
    for ($i = 0; $i -lt 10; $i++) {
        $s = Invoke-RunnerNative 'gh' @('pr', 'view', "$n", '--repo', $cfg.Repo, '--json', 'mergeable', '--jq', '.mergeable') -AllowFailure
        $state = (@($s.Output | Where-Object { "$_" -match '^[A-Z_]+$' }) | Select-Object -Last 1)
        if ($state -ne 'UNKNOWN') { break }
        Start-Sleep -Seconds 3
    }
    if ($state -eq 'CONFLICTING') {
        Stop-RunnerStep "the forward-port PR #$n conflicts with $($f.NextBranch): resolve it in a PR into $($f.NextBranch) (merge $($f.Branch) into a branch from $($f.NextBranch), fix the conflicts), merge that, and -Go again"
    }
    $r = Invoke-RunnerNative 'gh' @('pr', 'merge', "$n", '--repo', $cfg.Repo, '--merge') -Echo -AllowFailure
    if ($r.ExitCode -ne 0) { Stop-RunnerStep ("gh pr merge $n refused: " + (@($r.Output | Select-Object -Last 4) -join ' / ')) }
    Invoke-RepoGit -Dir $f.GitDir -Arguments @('fetch', 'origin', $f.NextBranch, '--quiet') -AllowFailure | Out-Null
    "merged PR #$n $($f.Branch) -> $($f.NextBranch)"
}

function Invoke-StoreEngine {
    param([string[]]$Arguments)
    $ctx = $script:Ctx
    $script = Join-Path $ctx.Wt 'ops\release\wavee-store-submit.ps1'
    $r = Invoke-RunnerChild 'powershell' (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script) + $Arguments) -Show
    if ($r.ExitCode -ne 0) { Stop-RunnerStep ("wavee-store-submit.ps1 exited $($r.ExitCode): " + (@($r.Tail | Select-Object -Last 5) -join ' / ')) }
}

function Invoke-StepStoreSubmit {
    param($Extra)
    $f = Get-RunnerFacts
    $args2 = @('-Semver', $f.Version, '-PollMinutes', '0')
    if (Test-Path -LiteralPath $f.StoreLedger) { $args2 += '-Resume' }
    Invoke-StoreEngine $args2
    $l = Read-StoreLedger
    $id = ''
    if ($l) { $id = "$($l.submissionId)" }
    "Store submission $id for $($f.StoreQuad) (ledger $($f.StoreLedger))"
}

function Invoke-StepNotify {
    param($Extra)
    $cfg = $script:Cfg
    $f = Get-RunnerFacts
    $cmp = Get-ReleaseLinkedIssues
    if ($null -eq $cmp -or @($cmp.Linked).Count -eq 0) { return 'no issue to comment on' }
    $version = $f.Version; $codename = $f.Codename; $quad = $f.Quad; $tag = $f.Tag; $repo = $cfg.Repo
    $bodyFor = {
        param($l)
        New-ShippedIssueComment -Repo $repo -Tag $tag -Semver $version -Codename $codename -Quad $quad `
            -Commits $l.Commits -Prs @($l.Commits.Prs | Sort-Object -Unique)
    }.GetNewClosure()
    Write-RunnerLine "  > gh issue comment <n> --repo $repo (for #$(@($cmp.Linked | ForEach-Object { $_.Issue }) -join ', #'))" 'DarkGray'
    $r = Add-ShippedIssueComments -Repo $repo -Tag $tag -Linked @($cmp.Linked) -BodyFor $bodyFor
    $t = "commented on $(@($r.Posted).Count) issue(s)"
    if (@($r.Posted).Count) { $t += ': #' + ($r.Posted -join ', #') }
    if (@($r.Skipped).Count) { $t += " (already: #$($r.Skipped -join ', #'))" }
    $t
}

function Invoke-StepStorePoll {
    param($Extra)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    Invoke-StoreEngine @('-Semver', $f.Version, '-Resume', '-PollMinutes', "$($ctx.PollMinutes)")
    "polled the Store for $($ctx.PollMinutes) minute(s)"
}

$script:StepRuns = @{
    'changelog'    = { param($x) Invoke-StepChangelog $x }
    'bump'         = { param($x) Invoke-StepBump $x }
    'notes'        = { param($x) Invoke-StepNotes $x }
    'tag'          = { param($x) Invoke-StepTag $x }
    'pack-arm64'   = { param($x) Invoke-StepPack -Arch 'arm64' -Extra $x }
    'pack-x64'     = { param($x) Invoke-StepPack -Arch 'x64' -Extra $x }
    'symbols'      = { param($x) Invoke-StepSymbols $x }
    'sign'         = { param($x) Invoke-StepSign $x }
    'stage'        = { param($x) Invoke-StepStage $x }
    'push'         = { param($x) Invoke-StepPush $x }
    'release'      = { param($x) Invoke-StepRelease $x }
    'feed'         = { param($x) Invoke-StepFeed $x }
    'open-next'    = { param($x) Invoke-StepOpenNext $x }
    'merge-main'   = { param($x) Invoke-StepMergeMain $x }
    'forward-port' = { param($x) Invoke-StepForwardPort $x }
    'store-submit' = { param($x) Invoke-StepStoreSubmit $x }
    'notify'       = { param($x) Invoke-StepNotify $x }
    'store-poll'   = { param($x) Invoke-StepStorePoll $x }
}

#endregion

#region The run

function New-StepOutcome {
    param([string]$Status, [string]$Result = '', [int]$ExitCode = 0)
    @{ Status = $Status; Result = $Result; ExitCode = $ExitCode }
}

function Split-StepList {
    param([string[]]$Names)
    if (-not $Names) { return }
    @($Names | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# The steps of one invocation (preflight excluded: every -Go runs it), in order, with Pause set on the -PauseBefore
# steps except the one the last run paused at (a -Go with the same -PauseBefore continues there).
function Get-RunnerPlan {
    param([string]$From, [string]$To, [string[]]$Only, [string[]]$PauseBefore, [string]$ResumePaused)
    $order = @($script:StepNames | Where-Object { $_ -ne 'preflight' })
    if ($Only.Count -gt 0) { $selected = @($order | Where-Object { $Only -contains $_ }) }
    else {
        $start = 0
        $end = $order.Count - 1
        if ($From) { $start = [array]::IndexOf($order, $From) }
        if ($To) { $end = [array]::IndexOf($order, $To) }
        $selected = @($order[$start..$end])
    }
    foreach ($name in $selected) {
        [pscustomobject]@{ Name = $name; Pause = [bool](($PauseBefore -contains $name) -and $name -ne $ResumePaused) }
    }
}

function Write-RunnerHeader {
    param([string]$Mode)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    Write-RunnerLine "release $($f.Version) ($Mode)" 'Cyan'
    $where = "worktree $($ctx.Wt)"
    if ($f.WtExists) { $where += " at $(Format-Sha $f.HeadSha)" } else { $where += " (not made yet; this run reads origin/$($f.Branch))" }
    Write-RunnerLine "  $where"
    $prev = '(none)'
    if ($f.PrevTag) { $prev = $f.PrevTag }
    $hot = 'no'
    if ($f.Hotfix) { $hot = 'yes' }
    if (-not $f.Ref) {
        Write-RunnerLine "  origin has no $($f.Branch): nothing to read the version, the build or the engine pin from" 'Yellow'
        return
    }
    $quad = '-'
    if ($f.Quad) { $quad = $f.Quad }
    Write-RunnerLine "  Wavee $($f.Version) $($f.Codename), build $($f.Build): quad $quad, Store $($f.StoreQuad); previous $prev; engine $(Format-Sha $f.EngineSha); hotfix: $hot"
    if ($f.ReleaseCommit) { Write-RunnerLine "  release commit $(Format-Sha $f.ReleaseCommit) ($($f.ReleaseCommitSource))" }
    foreach ($n in $f.Notes) { Write-RunnerLine "  note: $n" 'DarkYellow' }
}

# One step of a -Go: skipped when done, paused when listed, refused when its ground is missing, else run, recorded
# and judged again.
function Invoke-RunnerStep {
    param([Parameter(Mandatory = $true)][string]$Name)
    $ctx = $script:Ctx
    Set-RunnerStepLog $Name
    $started = Get-Date
    Write-RunnerLine ''
    Write-RunnerLine "[$Name]" 'Cyan'
    $f = Get-RunnerFacts
    $commit = $f.ReleaseCommit
    if (-not $commit) { $commit = $f.HeadSha }
    $steps = Get-StateSteps
    $previous = $steps[$Name]
    $steps[$Name] = @{ status = 'RUNNING'; startedUtc = $started.ToUniversalTime().ToString('o'); commit = $commit }
    Save-RunnerState
    $extra = @{}
    try {
        $result = & $script:StepRuns[$Name] $extra
        $result = "$(@($result) | Select-Object -Last 1)"
        Reset-RunnerFacts
        $f2 = Get-RunnerFacts
        $commit2 = $f2.ReleaseCommit
        if (-not $commit2) { $commit2 = $f2.HeadSha }
        $rec = @{ status = 'DONE'; utc = (Get-UtcStamp); startedUtc = $started.ToUniversalTime().ToString('o'); commit = $commit2; result = $result }
        foreach ($k in $extra.Keys) { $rec[$k] = $extra[$k] }
        $steps[$Name] = $rec
        Save-RunnerState
        $after = Get-StepJudge $Name
        if ($after.Status -eq 'WAITING') {
            $rec.status = 'WAITING'
            $rec.result = $after.Text
            Save-RunnerState
            Write-StepResult -Step $Name -Status 'WAITING' -Started $started -Finished (Get-Date) -Result $after.Text
            return New-StepOutcome 'WAITING' $after.Text
        }
        if (-not $after.Done) { throw "the step ran, but reality does not report it done: $($after.Text)" }
        Write-StepResult -Step $Name -Status 'DONE' -Started $started -Finished (Get-Date) -Result $result
        New-StepOutcome 'DONE' $result
    }
    catch {
        $msg = Protect-RunnerText ($_.Exception.Message -replace "`r?`n", ' / ')
        $steps[$Name] = @{ status = 'STOPPED'; utc = (Get-UtcStamp); startedUtc = $started.ToUniversalTime().ToString('o'); commit = $commit; result = $msg }
        if ($previous -is [hashtable] -and $previous.status -eq 'DONE') { $steps[$Name].previous = $previous }
        Save-RunnerState
        Write-StepResult -Step $Name -Status 'STOPPED' -Started $started -Finished (Get-Date) -Result $msg
        New-StepOutcome 'STOPPED' $msg 3
    }
    finally { Reset-RunnerFacts }
}

function Invoke-RunnerSteps {
    param([string]$From, [string]$To, [string[]]$Only, [string[]]$PauseBefore)
    $ctx = $script:Ctx
    $state = $ctx.State
    $plan = @(Get-RunnerPlan -From $From -To $To -Only $Only -PauseBefore $PauseBefore -ResumePaused "$($state.pausedBefore)")
    $state.run = @{ id = [guid]::NewGuid().ToString(); pid = $PID; startedUtc = (Get-UtcStamp); mode = 'go' }
    Save-RunnerState
    Write-RunnerHeader -Mode "go, in $($ctx.Wt)"

    Set-RunnerStepLog 'preflight'
    $ctx.RunStep = 'preflight'
    $items = @(Get-PreflightItems)
    Show-PreflightTable $items
    if (Test-PreflightRefused $items) {
        $ctx.Outcomes['preflight'] = New-StepOutcome 'REFUSED' 'see the action items' 2
        (Get-StateSteps)['preflight'] = @{ status = 'REFUSED'; utc = (Get-UtcStamp); result = (@($items | Where-Object { $_.status -eq 'action' } | ForEach-Object { $_.name }) -join ', ') }
        Save-RunnerState
        Write-RunnerLine ''
        Write-RunnerLine 'REFUSED: the preflight has action items; nothing changed.' 'Red'
        Write-RunTable $ctx.Outcomes 2
        return 2
    }
    $ctx.Outcomes['preflight'] = New-StepOutcome 'DONE' 'every item ok'
    (Get-StateSteps)['preflight'] = @{ status = 'DONE'; utc = (Get-UtcStamp); result = 'every item ok' }
    if ($state.ContainsKey('pausedBefore')) { $state.Remove('pausedBefore') }
    Save-RunnerState

    $code = 0
    foreach ($step in $plan) {
        $name = $step.Name
        $ctx.RunStep = $name
        $j = Get-StepJudge $name
        if ($j.Done) {
            $ctx.Outcomes[$name] = New-StepOutcome $(if ($j.Status -eq 'N/A') { 'N/A' } else { 'SKIPPED' }) $j.Text
            Write-RunnerLine "[$name] $($ctx.Outcomes[$name].Status): $($j.Text)" 'DarkGray'
            continue
        }
        if ($step.Pause) {
            $state.pausedBefore = $name
            Save-RunnerState
            $ctx.Outcomes[$name] = New-StepOutcome 'PAUSED' 'waiting for the next -Go' 4
            Write-RunnerLine ''
            Write-RunnerLine "[$name] PAUSED before this step. Run release.ps1 -Version $($ctx.Version) -Go (with the same -PauseBefore) to continue." 'Yellow'
            $code = 4
            break
        }
        $missing = @()
        foreach ($need in $script:Needs[$name]) {
            $nj = Get-StepJudge $need
            if (-not $nj.Done) { $missing += "$need ($($nj.Text))" }
        }
        if ($missing.Count -gt 0) {
            $msg = "needs $($missing -join '; ') first"
            $ctx.Outcomes[$name] = New-StepOutcome 'STOPPED' $msg 3
            Write-RunnerLine "[$name] STOPPED: $msg" 'Red'
            $code = 3
            break
        }
        $o = Invoke-RunnerStep $name
        $ctx.Outcomes[$name] = $o
        if ($o.Status -eq 'STOPPED') { $code = 3; break }
    }
    if ($code -eq 0) { $ctx.RunStep = '' }
    Write-RunTable $ctx.Outcomes $code
    $code
}

function Invoke-RunnerDryRun {
    param([string]$From, [string]$To, [string[]]$Only, [string[]]$PauseBefore)
    $ctx = $script:Ctx
    Write-RunnerHeader -Mode 'dry run: nothing changes'
    $items = @(Get-PreflightItems)
    Show-PreflightTable $items
    $refused = Test-PreflightRefused $items
    $ctx.Outcomes['preflight'] = New-StepOutcome $(if ($refused) { 'REFUSED' } else { 'OK' }) $(if ($refused) { 'a -Go would refuse: see the action items' } else { 'every item ok' })
    $plan = @(Get-RunnerPlan -From $From -To $To -Only $Only -PauseBefore $PauseBefore -ResumePaused "$($ctx.State.pausedBefore)")
    foreach ($name in @($script:StepNames | Where-Object { $_ -ne 'preflight' })) {
        $j = Get-StepJudge $name
        $p = @($plan | Where-Object { $_.Name -eq $name }) | Select-Object -First 1
        Write-RunnerLine ''
        if ($j.Done) {
            Write-RunnerLine "[$name] $($j.Status): $($j.Text)" 'DarkGray'
            $ctx.Outcomes[$name] = New-StepOutcome $j.Status $j.Text
            continue
        }
        if (-not $p) {
            Write-RunnerLine "[$name] $($j.Status), not selected: $($j.Text)" 'DarkGray'
            $ctx.Outcomes[$name] = New-StepOutcome 'NOT RUN' $j.Text
            continue
        }
        $pause = ''
        if ($p.Pause) { $pause = ' (the run pauses before this step)' }
        Write-RunnerLine "[$name] PLANNED$($pause): $($j.Text)" 'Cyan'
        foreach ($l in (Get-StepPlan $name)) { Write-RunnerLine "  > $l" }
        $ctx.Outcomes[$name] = New-StepOutcome 'PLANNED' $j.Text
    }
    $code = 0
    if ($refused) { $code = 2 }
    Write-RunTable $ctx.Outcomes $code
    $code
}

function Get-StatusRows {
    $rows = @()
    foreach ($name in @($script:StepNames | Where-Object { $_ -ne 'preflight' })) {
        $j = Get-StepJudge $name
        $rec = Get-StepRecord $name
        $last = $null
        if ($rec) { $last = [ordered]@{ status = "$($rec.status)"; utc = "$($rec.utc)"; commit = "$($rec.commit)"; result = "$($rec.result)" } }
        $rows += [ordered]@{ name = $name; status = $j.Status; done = [bool]$j.Done; text = $j.Text; last = $last }
    }
    $rows
}

function Show-RunnerStatus {
    param([switch]$Json)
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $holder = $null
    if (Test-Path -LiteralPath $ctx.StateDir) { $holder = Get-RunnerLockHolder -Dir $ctx.StateDir }
    $rows = @(Get-StatusRows)
    $next = $null
    foreach ($r in $rows) { if (-not $r.done) { $next = $r.name; break } }
    $pre = Get-StepRecord 'preflight'
    if ($Json) {
        $o = [ordered]@{
            product      = 'wavee'
            version      = $ctx.Version
            kind         = 'status'
            exitCode     = 0
            release      = [ordered]@{
                codename = $f.Codename; build = $f.Build; quad = $f.Quad; storeQuad = $f.StoreQuad; previousTag = $f.PrevTag
                engine = $f.EngineSha; hotfix = [bool]$f.Hotfix; releaseCommit = $f.ReleaseCommit; branch = $f.Branch
                worktree = $ctx.Wt; worktreeExists = [bool]$f.WtExists; stage = $f.Stage
            }
            lock         = [ordered]@{ holder = $holder; path = (Join-Path $ctx.StateDir 'runner.lock') }
            pausedBefore = $(if ($ctx.State.pausedBefore) { "$($ctx.State.pausedBefore)" } else { $null })
            run          = $ctx.State.run
            preflight    = $pre
            next         = $next
            steps        = $rows
            stateDir     = $ctx.StateDir
        }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $o -Depth 8))
        return 0
    }
    Write-RunnerHeader -Mode 'status'
    if ($null -ne $holder) { Write-RunnerLine "  runner $holder is running (it holds $($ctx.StateDir)\runner.lock)" 'Yellow' } else { Write-RunnerLine "  lock: free; state $($ctx.StateDir)" }
    Write-RunnerLine ''
    Write-RunnerLine ('{0,-13} {1,-9} {2,-22} {3}' -f 'step', 'status', 'last run', 'detail')
    Write-RunnerLine ('{0,-13} {1,-9} {2,-22} {3}' -f 'preflight', '-', $(if ($pre) { "$($pre.status)" } else { '-' }), 'runs with every -Go and dry run')
    foreach ($r in $rows) {
        $l = '-'
        if ($r.last) { $l = "$($r.last.status) $(if ($r.last.utc.Length -ge 16) { $r.last.utc.Substring(5, 11) } else { $r.last.utc })" }
        Write-RunnerLine ('{0,-13} {1,-9} {2,-22} {3}' -f $r.name, $r.status, $l, $r.text)
    }
    if ($ctx.State.pausedBefore) { Write-RunnerLine "paused before $($ctx.State.pausedBefore): the next -Go with the same -PauseBefore continues there" 'Yellow' }
    if ($next) { Write-RunnerLine "next: $next" } else { Write-RunnerLine 'next: nothing; every step is done' 'Green' }
    0
}

#endregion

#region The other modes

function Get-OriginReleaseLines {
    $ctx = $script:Ctx
    $map = Get-RemoteRefMap -Dir $ctx.Root -Patterns @('refs/heads/release/*', "refs/tags/$($script:Cfg.TagPrefix)*")
    $branches = @{}
    $tags = @{}
    foreach ($k in $map.Keys) {
        $m = [regex]::Match($k, '^refs/heads/release/(\d+\.\d+\.\d+)$')
        if ($m.Success) { $branches[$m.Groups[1].Value] = $map[$k] }
        $m = [regex]::Match($k, '^refs/tags/' + [regex]::Escape($script:Cfg.TagPrefix) + '(\d+\.\d+\.\d+)$')
        if ($m.Success) { $tags[$m.Groups[1].Value] = $map[$k] }
    }
    @{ Branches = $branches; Tags = $tags }
}

function Invoke-RunnerHotfix {
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $v = $ctx.Version
    $sv = Test-WaveeSemver $v
    $next = "$($sv.Major).$($sv.Minor).$($sv.Patch + 1)"
    $mode = 'dry run: nothing changes'
    if ($ctx.Go) { $mode = 'go' }
    Write-RunnerLine "hotfix $v ($mode)" 'Cyan'
    Set-RunnerStepLog 'hotfix'
    Invoke-RepoGit -Dir $ctx.Root -Arguments @('fetch', 'origin', '--tags', '--quiet') -AllowFailure | Out-Null
    $o = Get-OriginReleaseLines
    if ($o.Tags.ContainsKey($v)) { Write-RunnerLine "REFUSED: $($cfg.TagPrefix)$v already shipped" 'Red'; return 2 }
    $prev = $null
    foreach ($t in $o.Tags.Keys) {
        $tv = [version]$t
        if ($tv -lt [version]$v -and ($null -eq $prev -or $tv -gt [version]$prev)) { $prev = $t }
    }
    if (-not $prev) { Write-RunnerLine "REFUSED: no shipped $($cfg.TagPrefix)* below $v to cut the hotfix from" 'Red'; return 2 }
    $pv = [version]$prev
    if ($pv.Major -ne $sv.Major -or $pv.Minor -ne $sv.Minor -or $pv.Build + 1 -ne $sv.Patch) {
        Write-RunnerLine "REFUSED: -Hotfix takes the patch after the last shipped release ($prev): $($pv.Major).$($pv.Minor).$($pv.Build + 1)" 'Red'
        return 1
    }
    $prevCommit = $o.Tags[$prev]
    $hasV = $o.Branches.ContainsKey($v)
    $hasNext = $o.Branches.ContainsKey($next)
    if (Test-Path -LiteralPath (Join-Path $ctx.Wt '.git')) {
        Write-RunnerLine "REFUSED: the release worktree $($ctx.Wt) exists from the work that becomes release/$next; remove it first (git worktree remove $($ctx.Wt))" 'Red'
        return 2
    }
    Write-RunnerLine "  shipped: $($cfg.TagPrefix)$prev at $(Format-Sha $prevCommit); release/$v on origin: $(if ($hasV) { Format-Sha $o.Branches[$v] } else { 'no' }); release/${next}: $(if ($hasNext) { Format-Sha $o.Branches[$next] } else { 'no' })"
    $actions = @()
    if ($hasV -and -not $hasNext) { $actions += 'rename' ; $actions += 'create' }
    elseif (-not $hasV) { $actions += 'create' }
    else {
        if (-not (Test-GitCommit $ctx.Root $o.Branches[$v])) { Invoke-RepoGit -Dir $ctx.Root -Arguments @('fetch', 'origin', "release/$v", "release/$next", '--quiet') -AllowFailure | Out-Null }
        $mb = (Invoke-RepoGit -Dir $ctx.Root -Arguments @('merge-base', $o.Branches[$v], $o.Branches[$next]) -AllowFailure).Text
        if ($mb -ne $prevCommit -and $o.Branches[$v] -ne $prevCommit) {
            Write-RunnerLine "REFUSED: release/$v and release/$next both exist and release/$v is not a branch from $($cfg.TagPrefix)$prev" 'Red'
            return 2
        }
        Write-RunnerLine "  done: release/$v is the hotfix branch from $($cfg.TagPrefix)$prev; the work waits on release/$next" 'Green'
    }
    $prs = @()
    if ($actions -contains 'rename') {
        try { $prs = @(@(Get-RunnerOpenPrsAll) | Where-Object { $_.baseRefName -eq "release/$v" }) } catch { }
    }
    foreach ($a in $actions) {
        if ($a -eq 'rename') {
            Write-RunnerLine "  > gh api -X POST repos/$($cfg.Repo)/branches/release%2F$v/rename -f new_name=release/$next   (GitHub retargets its $($prs.Count) open PR(s) to release/$next)"
        }
        else {
            Write-RunnerLine "  > gh api -X POST repos/$($cfg.Repo)/git/refs -f ref=refs/heads/release/$v -f sha=$prevCommit   (release/$v from $($cfg.TagPrefix)$prev)"
        }
    }
    if ($ctx.Go -and $actions.Count -gt 0) {
        foreach ($a in $actions) {
            if ($a -eq 'rename') {
                $r = Invoke-RunnerNative 'gh' @('api', '-X', 'POST', "repos/$($cfg.Repo)/branches/release%2F$v/rename", '-f', "new_name=release/$next") -AllowFailure
                if ($r.ExitCode -ne 0) { Write-RunnerLine ("STOPPED: the rename was refused (the release/** ruleset may forbid it; run it with the owner's bypass): " + (@($r.Output | Select-Object -Last 3) -join ' / ')) 'Red'; return 3 }
                Write-RunnerLine "  release/$v -> release/$next" 'Green'
            }
            else {
                Invoke-RunnerNative 'gh' @('api', '-X', 'POST', "repos/$($cfg.Repo)/git/refs", '-f', "ref=refs/heads/release/$v", '-f', "sha=$prevCommit") | Out-Null
                Write-RunnerLine "  release/$v created at $(Format-Sha $prevCommit)" 'Green'
            }
        }
    }
    if ($ctx.Go) {
        $statePath = Join-Path $ctx.StateDir 'state.json'
        if ((Test-Path -LiteralPath $statePath) -and -not $ctx.State.hotfix) {
            Copy-Item -LiteralPath $statePath (Join-Path $ctx.StateDir ('state.before-hotfix-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.json')) -Force
            $ctx.State = @{}
        }
        $ctx.State.hotfix = $true
        $ctx.State.hotfixFrom = "$($cfg.TagPrefix)$prev"
        $ctx.State.hotfixUtc = Get-UtcStamp
        Save-RunnerState
    }
    Write-RunnerLine ''
    Write-RunnerLine "  next: fix/<name> from release/$v, a PR into release/$v (a change file, Fixes #n), then release.ps1 -Version $v -Go"
    Write-RunnerLine "  an engine fix: a wavee-hotfix/$v branch in fluent-gpu from its $($cfg.TagPrefix)$prev tag, and engine.lock on release/$v points at it"
    0
}

function Get-RunnerOpenPrsAll {
    $t = Invoke-Gh @('pr', 'list', '--repo', $script:Cfg.Repo, '--state', 'open', '--limit', '300', '--json',
        'number,title,baseRefName,headRefName,isCrossRepository,author,url')
    $j = ConvertFrom-GhJson $t
    $j
}

function Invoke-RunnerRetargetPrs {
    $ctx = $script:Ctx
    $mode = 'dry run: nothing changes'
    if ($ctx.Go) { $mode = 'go' }
    Write-RunnerLine "retarget PRs ($mode)" 'Cyan'
    Set-RunnerStepLog 'retarget-prs'
    $o = Get-OriginReleaseLines
    $open = $null
    $shipped = @()
    foreach ($b in $o.Branches.Keys) {
        if ($o.Tags.ContainsKey($b)) { $shipped += "release/$b"; continue }
        if ($null -eq $open -or [version]$b -gt [version]$open) { $open = $b }
    }
    if (-not $open) { Write-RunnerLine 'STOPPED: no open release branch (every release/* on origin has shipped)' 'Red'; return 3 }
    $target = "release/$open"
    Write-RunnerLine "  the open release branch: $target; shipped: $(if ($shipped.Count) { ($shipped | Sort-Object) -join ', ' } else { 'none' })"
    $bases = @('main') + $shipped
    $prs = @(@(Get-RunnerOpenPrsAll) | Where-Object { $bases -contains $_.baseRefName -and -not (Test-RunnerPr $_) })
    if ($prs.Count -eq 0) { Write-RunnerLine '  nothing to move: no open PR targets main or a shipped release branch' 'Green'; return 0 }
    foreach ($pr in $prs) {
        Write-RunnerLine "  #$($pr.number) $($pr.title) ($($pr.author.login)): $($pr.baseRefName) -> $target"
        if (-not $ctx.Go) {
            Write-RunnerLine "    > gh api -X PATCH repos/$($script:Cfg.Repo)/pulls/$($pr.number) -f base=$target" 'DarkGray'
            Write-RunnerLine "    > gh pr comment $($pr.number) --repo $($script:Cfg.Repo) --body-file <one comment>" 'DarkGray'
            continue
        }
        $text = "Wavee's pull requests target the open release branch, so this PR now targets ``$target`` instead of ``$($pr.baseRefName)``. Nothing else changes."
        Move-RunnerPr -Pr $pr -Base $target -Comment $text
    }
    if ($ctx.Go) { Write-RunnerLine "  moved $($prs.Count) PR(s) to $target" 'Green' }
    0
}

# -RepointFeed, ported from the old script: point wavee-stable back (or forward) at an existing release.
function Invoke-RunnerRepointFeed {
    param([switch]$AllowDowngrade)
    $ctx = $script:Ctx
    $cfg = $script:Cfg
    $v = $ctx.Version
    $targetTag = "$($cfg.TagPrefix)$v"
    $mode = 'dry run: nothing changes'
    if ($ctx.Go) { $mode = 'go' }
    Write-RunnerLine "repoint $($cfg.FeedRelease) at $targetTag ($mode)" 'Cyan'
    Set-RunnerStepLog 'repoint-feed'
    $rel = Get-GhRelease $cfg.Repo $targetTag
    if (-not $rel) { Write-RunnerLine "STOPPED: release $targetTag not found in $($cfg.Repo)" 'Red'; return 3 }
    if ($rel.isDraft) { Write-RunnerLine "STOPPED: release $targetTag is still a draft; its assets are not public, so the feed cannot point at it" 'Red'; return 3 }
    $quad = ''
    $found = @()
    foreach ($a in $rel.assets) {
        $m = [regex]::Match("$($a.name)", '^Wavee_(?<q>\d+\.\d+\.\d+\.\d+)_(?<a>arm64|x64)\.msix$')
        if ($m.Success) {
            if ($quad -and $quad -ne $m.Groups['q'].Value) { Write-RunnerLine "STOPPED: release $targetTag carries more than one quad ($quad and $($m.Groups['q'].Value))" 'Red'; return 3 }
            $quad = $m.Groups['q'].Value
            $found += $m.Groups['a'].Value
        }
    }
    if (-not $quad) { Write-RunnerLine "STOPPED: release $targetTag has no Wavee_<quad>_<arch>.msix asset to point at" 'Red'; return 3 }
    $found = @($found | Sort-Object -Unique)
    Write-RunnerLine "  quad $quad, architectures $($found -join ', ')"
    if ($AllowDowngrade) {
        Write-RunnerLine "  warn: the monotonic gate is inverted by -AllowDowngrade: clients on a newer build WILL be moved back to $quad" 'Yellow'
    }
    else {
        try {
            $rows = Test-FeedMonotonic $cfg.Repo @($cfg.FeedRelease) $quad $v $found $cfg.AssetPrefix
            foreach ($r in $rows) { Write-RunnerLine "  $($r.Feed)/$($r.Arch): $($r.Current) -> $($r.New)" }
        }
        catch {
            Write-RunnerLine ("REFUSED: " + ($_.Exception.Message -replace "`r?`n", ' / ') + ' (-AllowDowngrade to move installs back)') 'Red'
            return 2
        }
    }
    $work = Join-Path $ctx.Root "artifacts\release\repoint-$v"
    foreach ($a in $found) {
        Write-RunnerLine "  > write $work\$(Get-AppInstallerName $a) (Version $quad, MainPackage $(Get-MsixUri $targetTag $quad $a))"
    }
    Write-RunnerLine "  > gh release upload $($cfg.FeedRelease) --repo $($cfg.Repo) --clobber $(($found | ForEach-Object { Get-AppInstallerName $_ }) -join ' ')"
    Write-RunnerLine "  > poll the feed until it says $quad and points at $targetTag"
    Write-RunnerLine '  whatsnew-index.json is deliberately left alone: the index is cumulative history, not a pointer.'
    if (-not $ctx.Go) { return 0 }

    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $assets = @()
    foreach ($a in $found) {
        $out = Join-Path $work (Get-AppInstallerName $a)
        New-WaveeAppInstaller -Template (Join-Path $ctx.Root 'ops\build\Wavee.AppInstaller.template.xml') -OutFile $out -Arch $a -Quad $quad `
            -Publisher $cfg.Publisher -IdentityName $cfg.IdentityName -FeedUri (Get-FeedUri $a) -MsixUri (Get-MsixUri $targetTag $quad $a) | Out-Null
        $assets += $out
    }
    Update-WaveeFeed -Repo $cfg.Repo -FeedRelease $cfg.FeedRelease -FeedBodyFile (Join-Path $ctx.Root 'ops\release\feed-release-body.md') -Assets $assets -Target 'main'
    foreach ($a in $found) {
        $want = Get-MsixUri $targetTag $quad $a
        $live = Test-WaveeFeedLive -Repo $cfg.Repo -FeedRelease $cfg.FeedRelease -Arch $a -AssetPrefix $cfg.AssetPrefix -ExpectedQuad $quad -ExpectedMsixUri $want
        if (-not $live) { Write-RunnerLine "STOPPED: feed $($cfg.FeedRelease)/$a did not come up at $quad pointing at $want" 'Red'; return 3 }
        Write-RunnerLine "  $($cfg.FeedRelease)/$a is live at $quad" 'Green'
    }
    0
}

# -Abort: before the push step only, delete the local tags and the runner's local commits in the release worktree.
function Invoke-RunnerAbort {
    $ctx = $script:Ctx
    $f = Get-RunnerFacts
    $mode = 'dry run: nothing changes'
    if ($ctx.Go) { $mode = 'go' }
    Write-RunnerLine "abort $($f.Version) ($mode)" 'Cyan'
    Set-RunnerStepLog 'abort'
    if (-not $f.WtExists) { Write-RunnerLine "  no release worktree at $($ctx.Wt): nothing to abort" 'Green'; return 0 }
    if ($f.OriginTagCommit) { Write-RunnerLine "REFUSED: $($f.Tag) is on origin; a pushed release is fixed forward, never aborted" 'Red'; return 3 }
    if ($f.EngineOriginTagCommit) { Write-RunnerLine "REFUSED: fluent-gpu has $($f.Tag) on origin; a pushed tag is never deleted" 'Red'; return 3 }
    $wt = $ctx.Wt
    $base = $f.OriginBranchSha
    $pushed = @()
    if ($base) {
        $range = $base
        if ($f.PrevTag) { $range = "$($f.PrevTag)..$base" }
        $pushed = @((Invoke-RepoGit -Dir $wt -Arguments @('log', '--format=%s', $range) -AllowFailure).Lines | Where-Object { $_ -eq "release: CHANGELOG for $($f.Version)" -or $_ -like "release: Wavee $($f.Version) *" })
    }
    if ($pushed.Count -gt 0) { Write-RunnerLine "REFUSED: origin/$($f.Branch) already carries the runner's commits ($($pushed -join '; ')); fix forward" 'Red'; return 3 }
    $dirty = @((Invoke-RepoGit -Dir $wt -Arguments @('status', '--porcelain') -DropAdvice).Lines)
    if ($dirty.Count -gt 0) { Write-RunnerLine "REFUSED: the release worktree has uncommitted changes (first: $($dirty[0])); commit or discard them first" 'Red'; return 3 }
    $local = @()
    if ($base) { $local = @((Invoke-RepoGit -Dir $wt -Arguments @('log', '--first-parent', '--format=%H%x1f%s', "$base..HEAD")).Lines) }
    $runner = @()
    $others = @()
    foreach ($l in $local) {
        $p = $l -split [char]0x1f, 2
        if ($p[1] -eq "release: CHANGELOG for $($f.Version)" -or $p[1] -like "release: Wavee $($f.Version) *") { $runner += $l } else { $others += "$(Format-Sha $p[0]) $($p[1])" }
    }
    if ($others.Count -gt 0) {
        $mb = (Invoke-RepoGit -Dir $wt -Arguments @('merge-base', 'HEAD', $base)).Text
        Write-RunnerLine "REFUSED: the worktree branch also holds commits that are not the runner's: $($others -join '; '). Move them first, or reset by hand: git -C $wt reset --hard $(Format-Sha $mb)" 'Red'
        return 3
    }
    $plan = @()
    if ($f.LocalTagCommit) { $plan += ,@($wt, @('tag', '-d', $f.Tag)) }
    if ($f.EngineLocalTagCommit -and $f.EngineDir) { $plan += ,@($f.EngineDir, @('tag', '-d', $f.Tag)) }
    if ($runner.Count -gt 0) {
        $mb = (Invoke-RepoGit -Dir $wt -Arguments @('merge-base', 'HEAD', $base)).Text
        $plan += ,@($wt, @('reset', '--hard', $mb))
    }
    if ($plan.Count -eq 0) { Write-RunnerLine '  nothing to abort: no local tag and no runner commit' 'Green'; return 0 }
    foreach ($p in $plan) { Write-RunnerLine "  > git -C $($p[0]) $($p[1] -join ' ')" }
    Write-RunnerLine "  the step records from changelog to push are cleared; the staging folder stays (a new commit rebuilds it)"
    if (-not $ctx.Go) { return 0 }
    foreach ($p in $plan) { Invoke-RepoGit -Dir $p[0] -Arguments $p[1] -Echo | Out-Null }
    $steps = Get-StateSteps
    foreach ($n in @('changelog', 'bump', 'notes', 'tag', 'pack-arm64', 'pack-x64', 'symbols', 'sign', 'stage', 'push')) { if ($steps.ContainsKey($n)) { $steps.Remove($n) } }
    $ctx.State.abortedUtc = Get-UtcStamp
    Save-RunnerState
    Write-RunnerLine '  aborted: the release worktree is back at origin' 'Green'
    0
}

#endregion

#region Entry point

function Send-RunnerToast {
    param([string]$Title, [string]$Text)
    try {
        $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $xml = '<toast><visual><binding template="ToastGeneric"><text>' + [Security.SecurityElement]::Escape($Title) +
            '</text><text>' + [Security.SecurityElement]::Escape($Text) + '</text></binding></visual></toast>'
        $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
        $doc.LoadXml($xml)
        $toast = New-Object Windows.UI.Notifications.ToastNotification $doc
        $aumid = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($aumid).Show($toast)
        $true
    }
    catch { $false }
}

function Test-StepName {
    param([string]$Name)
    ($script:StepNames | Where-Object { $_ -ne 'preflight' }) -contains $Name
}

function Write-Refusal {
    param([string]$Text)
    if ($script:Ctx -and $script:Ctx.Quiet) { [Console]::Error.WriteLine("REFUSED: $Text"); return }
    Write-Host "REFUSED: $Text" -ForegroundColor Red
}

# The entry point of release.ps1. Returns the exit code: 0 done, 1 usage or config, 2 refused by the preflight,
# 3 stopped, 4 paused.
function Invoke-WaveeRelease {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptRoot,
        [string]$Version,
        [switch]$Go,
        [string]$From,
        [string]$To,
        [string[]]$Only,
        [string[]]$PauseBefore,
        [switch]$Status,
        [switch]$Json,
        [switch]$Preflight,
        [string]$Codename,
        [string]$Hotfix,
        [switch]$RetargetPrs,
        [string]$RepointFeed,
        [switch]$AllowDowngrade,
        [switch]$Abort,
        [switch]$PublicOnly,
        [string]$SkipArch,
        [string]$X64Msix,
        [int]$PollMinutes = 15,
        [switch]$InWorktree)

    $bound = @{}
    foreach ($k in $PSBoundParameters.Keys) { if ($k -ne 'ScriptRoot') { $bound[$k] = $PSBoundParameters[$k] } }
    $Only = @(Split-StepList $Only)
    $PauseBefore = @(Split-StepList $PauseBefore)
    $script:Ctx = @{ Quiet = [bool]$Json; Secrets = @(); Outcomes = [ordered]@{}; Go = $false; Completed = $false }

    # Usage (exit 1), before anything is read.
    $modes = @()
    if ($Version) { $modes += '-Version' }
    if ($Hotfix) { $modes += '-Hotfix' }
    if ($RetargetPrs) { $modes += '-RetargetPrs' }
    if ($RepointFeed) { $modes += '-RepointFeed' }
    if ($modes.Count -ne 1) { Write-Refusal 'give exactly one of -Version, -Hotfix, -RetargetPrs, -RepointFeed (see release.ps1 -?)'; return 1 }
    $versionMode = [bool]$Version
    $actionFlags = @(@([bool]$Status, [bool]$Preflight, [bool]$Abort) | Where-Object { $_ }).Count
    if ($actionFlags -gt 1) { Write-Refusal '-Status, -Preflight and -Abort exclude each other'; return 1 }
    if (($Status -or $Preflight) -and $Go) { Write-Refusal '-Status and -Preflight are read-only; they take no -Go'; return 1 }
    if ($Json -and -not ($Status -or $Preflight)) { Write-Refusal '-Json goes with -Status or -Preflight'; return 1 }
    if (-not $versionMode -and ($Status -or $Preflight -or $Abort -or $From -or $To -or $Only.Count -or $PauseBefore.Count -or $Codename -or $SkipArch -or $X64Msix -or $PublicOnly -or $InWorktree)) {
        Write-Refusal "$($modes[0]) takes only -Go (and -AllowDowngrade with -RepointFeed)"; return 1
    }
    if ($AllowDowngrade -and -not $RepointFeed) { Write-Refusal '-AllowDowngrade goes with -RepointFeed'; return 1 }
    if (($Status -or $Preflight -or $Abort) -and ($From -or $To -or $Only.Count -or $PauseBefore.Count)) { Write-Refusal '-From, -To, -Only and -PauseBefore go with a run (the dry run or -Go)'; return 1 }
    if ($Only.Count -and ($From -or $To)) { Write-Refusal '-Only excludes -From and -To'; return 1 }
    foreach ($n in @($From, $To) + $Only + $PauseBefore) {
        if ($n -and -not (Test-StepName $n)) { Write-Refusal "unknown step '$n'; the steps are $(($script:StepNames | Where-Object { $_ -ne 'preflight' }) -join ', ')"; return 1 }
    }
    $order = @($script:StepNames | Where-Object { $_ -ne 'preflight' })
    if ($From -and $To -and [array]::IndexOf($order, $From) -gt [array]::IndexOf($order, $To)) { Write-Refusal "-From $From comes after -To $To"; return 1 }
    $v = $Version
    if ($Hotfix) { $v = $Hotfix }
    if ($RepointFeed) { $v = $RepointFeed }
    if ($v -and $v -notmatch '^\d+\.\d+\.\d+$') { Write-Refusal "'$v' is not a stable M.m.p version"; return 1 }
    if ($Codename -and $Codename -notmatch '^[A-Z][a-z]+$') { Write-Refusal "-Codename '$Codename' is one capitalised word (Drift, Ebb)"; return 1 }
    if ($SkipArch -and @('arm64', 'x64') -notcontains $SkipArch) { Write-Refusal '-SkipArch is arm64 or x64'; return 1 }
    if ($X64Msix -and $SkipArch -eq 'x64') { Write-Refusal '-X64Msix excludes -SkipArch x64'; return 1 }
    if ($X64Msix) {
        if (-not (Test-Path -LiteralPath $X64Msix)) { Write-Refusal "-X64Msix not found: $X64Msix"; return 1 }
        $X64Msix = (Resolve-Path -LiteralPath $X64Msix).Path
        $bound['X64Msix'] = $X64Msix
    }
    if ($InWorktree -and -not ($versionMode -and $Go)) { Write-Refusal '-InWorktree is internal: the outer -Go passes it'; return 1 }

    $root = Get-FullPath (Join-Path $ScriptRoot '..\..')
    $cfg = $script:Cfg
    $arches = @(@('arm64', 'x64') | Where-Object { $_ -ne $SkipArch })
    $stateRoot = Join-Path $env:LOCALAPPDATA 'WaveeRelease'
    $stateKey = $v
    if ($RetargetPrs) { $stateKey = '_retarget-prs' }
    if ($RepointFeed) { $stateKey = '_repoint-feed' }
    $wt = Join-Path $cfg.ReleaseRoot "$v\WaveeMusic"
    $ewt = Join-Path $cfg.ReleaseRoot "$v\fluent-gpu"
    $inWt = [bool]$InWorktree -or ($v -and (Test-SamePath $root $wt))
    $ctx = $script:Ctx
    $ctx.Version = $v
    $ctx.Go = [bool]$Go
    $ctx.Root = $root
    $ctx.Wt = $wt
    $ctx.Ewt = $ewt
    $ctx.EngineRepo = Get-FullPath (Join-Path (Split-Path -Parent $root) 'fluent-gpu')
    $ctx.InWorktree = $inWt
    $ctx.IsChild = [bool]$InWorktree
    $ctx.TopLevel = [bool]$Go -and -not $InWorktree
    $ctx.StateDir = Join-Path $stateRoot $stateKey
    $ctx.Arches = $arches
    $ctx.PublicOnly = [bool]$PublicOnly
    $ctx.X64Msix = $X64Msix
    $ctx.Codename = $Codename
    $ctx.PollMinutes = $PollMinutes
    $ctx.Facts = $null
    $ctx.FeedList = $null
    $ctx.RunStep = ''
    $ctx.Label = "Wavee $v release"
    if ($Hotfix) { $ctx.Label = "Wavee hotfix $v" }
    if ($RetargetPrs) { $ctx.Label = 'Wavee PR retarget' }
    if ($RepointFeed) { $ctx.Label = "Wavee feed repoint to $v" }
    if ($Abort) { $ctx.Label = "Wavee $v abort" }
    try { $ctx.State = Read-RunnerState -Dir $ctx.StateDir }
    catch { Write-Refusal $_.Exception.Message; return 1 }

    # The lock: every -Go takes it (a relaunched child works under its parent's).
    if ($Go) {
        $lock = Enter-RunnerLock -Dir $ctx.StateDir -AdoptParent:$InWorktree
        if (-not $lock) {
            Write-Refusal "another runner ($(Get-RunnerLockHolder -Dir $ctx.StateDir)) holds $($ctx.StateDir)\runner.lock"
            $ctx.RunStep = 'lock'
            return 2
        }
        $ctx.HoldsLock = ($lock -eq 'taken')
    }

    if ($RetargetPrs) { return Invoke-RunnerRetargetPrs }
    if ($RepointFeed) { return Invoke-RunnerRepointFeed -AllowDowngrade:$AllowDowngrade }
    if ($Hotfix) { return Invoke-RunnerHotfix }

    if (-not $Go) {
        Invoke-RepoGit -Dir $root -Arguments @('fetch', 'origin', '--tags', '--quiet') -AllowFailure | Out-Null
    }
    if ($Status) { return Show-RunnerStatus -Json:$Json }
    if ($Preflight) {
        $items = @(Get-PreflightItems)
        $refused = Test-PreflightRefused $items
        $code = 0
        if ($refused) { $code = 2 }
        if ($Json) {
            $o = [ordered]@{
                product  = 'wavee'
                version  = $v
                kind     = 'preflight'
                ok       = -not $refused
                exitCode = $code
                items    = @($items)
                refusals = @($items | Where-Object { $_.status -eq 'action' } | ForEach-Object { "$($_.name): $($_.text)" })
            }
            [Console]::Out.WriteLine((ConvertTo-Json -InputObject $o -Depth 6))
            return $code
        }
        Write-RunnerHeader -Mode 'preflight only'
        Show-PreflightTable $items
        if ($refused) { Write-RunnerLine ''; Write-RunnerLine 'a -Go would refuse: see the action items' 'Yellow' }
        return $code
    }
    if ($Abort) { return Invoke-RunnerAbort }
    if (-not $Go) { return Invoke-RunnerDryRun -From $From -To $To -Only $Only -PauseBefore $PauseBefore }

    # -Go from another checkout: check what the worktree needs, make it, and run the worktree's own runner.
    if (-not $inWt) {
        Write-RunnerLine "release $v (go: from $root; the steps run in $wt)" 'Cyan'
        Set-RunnerStepLog 'setup'
        $ctx.RunStep = 'preflight'
        $items = @(Get-PreflightItems -Setup)
        Show-PreflightTable $items
        if (Test-PreflightRefused $items) {
            Write-RunnerLine ''
            Write-RunnerLine 'REFUSED: the preflight has action items; nothing changed.' 'Red'
            return 2
        }
        $ctx.RunStep = 'setup'
        try { Initialize-ReleaseWorktree }
        catch {
            Write-RunnerLine "STOPPED: $($_.Exception.Message)" 'Red'
            return 3
        }
        $childScript = Join-Path $wt 'ops\release\release.ps1'
        if (-not (Test-Path -LiteralPath $childScript)) { Write-Refusal "$wt has no ops\release\release.ps1: merge the runner into release/$v first"; return 1 }
        $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $childScript)
        foreach ($k in $bound.Keys) {
            if ($k -eq 'InWorktree') { continue }
            $val = $bound[$k]
            if ($val -is [System.Management.Automation.SwitchParameter]) { if ($val.IsPresent) { $childArgs += "-$k" } }
            elseif ($val -is [array]) { if ($val.Count) { $childArgs += "-$k"; $childArgs += ((@($val) | ForEach-Object { "$_" }) -join ',') } }
            else { $childArgs += "-$k"; $childArgs += "$val" }
        }
        $childArgs += '-InWorktree'
        $argLine = (@($childArgs) | ForEach-Object { ConvertTo-Win32QuotedArgument "$_" }) -join ' '
        Write-RunnerLine ''
        Write-RunnerLine "  > powershell $argLine" 'DarkGray'
        # Start-Process with the console inherited: the child's output goes to the window (colours kept), never into
        # this function's return value. WaitForExit, not -Wait: -Wait also waits for build servers the child leaves.
        $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -NoNewWindow -PassThru
        $null = $p.Handle
        $p.WaitForExit()
        $code = $p.ExitCode
        try {
            $st = Read-RunnerState -Dir $ctx.StateDir
            if ($st.run -is [hashtable]) { $ctx.RunStep = "$($st.run.step)"; $ctx.RunWaiting = [bool]$st.run.storeWaiting }
        }
        catch { }
        return $code
    }

    if (-not $InWorktree) {
        try { Initialize-ReleaseWorktree }
        catch { Write-RunnerLine "STOPPED: $($_.Exception.Message)" 'Red'; $ctx.RunStep = 'setup'; return 3 }
    }
    $ctx.WritesRun = $true
    Invoke-RunnerSteps -From $From -To $To -Only $Only -PauseBefore $PauseBefore
}

# Ends a run: records its end in the state, frees runner.lock and, for a top-level -Go, shows the toast. Safe to call
# twice; a toast that fails changes nothing.
function Complete-WaveeRelease {
    param([int]$ExitCode)
    $ctx = $script:Ctx
    if ($null -eq $ctx -or $ctx.Completed) { return $ExitCode }
    $ctx.Completed = $true
    $waiting = [bool]$ctx.RunWaiting
    if ($ctx.Go -and $ctx.WritesRun -and $ctx.State -is [hashtable]) {
        try {
            if (-not ($ctx.State.run -is [hashtable])) { $ctx.State.run = @{} }
            $ctx.State.run.finishedUtc = Get-UtcStamp
            $ctx.State.run.exitCode = $ExitCode
            $ctx.State.run.step = "$($ctx.RunStep)"
            $poll = $ctx.Outcomes['store-poll']
            if ($poll -and $poll.Status -eq 'WAITING') { $waiting = $true }
            $ctx.State.run.storeWaiting = $waiting
            Save-RunnerState
        }
        catch { }
    }
    if ($ctx.HoldsLock) {
        try { Exit-RunnerLock -Dir $ctx.StateDir } catch { }
        $ctx.HoldsLock = $false
    }
    if ($ctx.TopLevel) {
        $step = "$($ctx.RunStep)"
        switch ($ExitCode) {
            0 { $kind = 'DONE'; if ($waiting) { $kind = 'DONE (the Store has not published yet)' } }
            1 { $kind = 'REFUSED (usage)' }
            2 { $kind = 'REFUSED by the preflight' }
            3 { $kind = 'STOPPED' ; if ($step) { $kind = "STOPPED at $step" } }
            4 { $kind = 'PAUSED'; if ($step) { $kind = "PAUSED before $step" } }
            default { $kind = "ended with $ExitCode" }
        }
        $text = "exit $ExitCode"
        if ($step) { $text += "; step $step" }
        [void](Send-RunnerToast -Title "$($ctx.Label): $kind" -Text $text)
    }
    $ExitCode
}

#endregion

Export-ModuleMember -Function Invoke-WaveeRelease, Complete-WaveeRelease
