#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1 - Describe / Context / It / Should Be /
    Should BeExactly / Should Throw / Should Match), matching Wavee.Release.Tests.ps1 in this folder. Run with:

        Invoke-Pester -Path ops/release/tests/Wavee.ReleaseRefs.Tests.ps1

    Covers the release <-> issue linkage helpers added to Wavee.Release.psm1 (docs/plans/wavee/*-implementation.md
    Part A): parsing `git log --format=%H%x1f%h%x1f%s%x1f%b%x1e` text, extracting the trailing-group refs from one
    CHANGELOG entry, cross-checking the two, formatting the mismatch lines, the commits.json shape and the
    shipped-issue-comment helpers. Pure: no git, no gh, no network - Add-ShippedIssueComments (the one gh-facing
    function) is intentionally NOT tested here.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path

Import-Module (Join-Path $repoRoot 'ops\release\Wavee.Release.psm1') -Force -DisableNameChecking
# Build module LAST: the release module's nested -Force import would otherwise unload these exports from the test scope.
Import-Module (Join-Path $repoRoot 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking

$script:RS = [char]0x1e
$script:US = [char]0x1f

function New-GitLogText {
    <#  Builds the exact text `git log --format=%H%x1f%h%x1f%s%x1f%b%x1e` emits for a list of
        @{ Sha; Short; Subject; Body } records - each record's formatted output is followed by a newline before
        the next record's RS-separated fields begin (git's default per-commit trailing newline). #>
    param([object[]]$Records)

    $sb = New-Object System.Text.StringBuilder
    foreach ($r in $Records) {
        [void]$sb.Append($r.Sha).Append($script:US).Append($r.Short).Append($script:US).Append($r.Subject).Append($script:US).Append($r.Body).Append($script:RS).Append("`n")
    }
    $sb.ToString()
}

# ===================================================================================================================

Describe 'ConvertFrom-GitLogRecords' {

    It 'parses a closing keyword in the body as an Issue' {
        $text = New-GitLogText @(@{ Sha = 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2'; Short = 'a1b2c3d'; Subject = 'audio: fix xrun logging'; Body = "Fixes #48`n" })
        $r = ConvertFrom-GitLogRecords $text
        $r.Count | Should Be 1
        $r[0].Sha | Should BeExactly 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2'
        $r[0].Short | Should BeExactly 'a1b2c3d'
        $r[0].Issues.Count | Should Be 1
        $r[0].Issues[0] | Should Be 48
        $r[0].Prs.Count | Should Be 0
    }

    It 'parses "closes #n" (case-insensitive) in the body as an Issue' {
        $text = New-GitLogText @(@{ Sha = 'b'*40; Short = 'bbbbbbb'; Subject = 'store: fix submission bug'; Body = "CLOSES #41`n" })
        $r = ConvertFrom-GitLogRecords $text
        $r[0].Issues.Count | Should Be 1
        $r[0].Issues[0] | Should Be 41
    }

    It 'a commit with no closing keyword has an empty Issues array' {
        $text = New-GitLogText @(@{ Sha = 'c'*40; Short = 'ccccccc'; Subject = 'chore: tidy comments'; Body = '' })
        $r = ConvertFrom-GitLogRecords $text
        $r[0].Issues.Count | Should Be 0
        , $r[0].Issues | Should BeOfType ([array])
    }

    It 'three commits in one log - Fixes / closes / none - parse independently' {
        $text = New-GitLogText @(
            @{ Sha = 'a'*40; Short = 'aaaaaaa'; Subject = 'fix a'; Body = "Fixes #48`n" },
            @{ Sha = 'b'*40; Short = 'bbbbbbb'; Subject = 'fix b'; Body = "closes #41`n" },
            @{ Sha = 'c'*40; Short = 'ccccccc'; Subject = 'chore: c'; Body = '' }
        )
        $r = ConvertFrom-GitLogRecords $text
        $r.Count | Should Be 3
        $r[0].Issues[0] | Should Be 48
        $r[1].Issues[0] | Should Be 41
        $r[2].Issues.Count | Should Be 0
    }

    It 'a squash-merge subject suffix (#49) is a Pr, not an Issue' {
        $text = New-GitLogText @(@{ Sha = 'd'*40; Short = 'ddddddd'; Subject = 'audio: adaptive read-ahead (#49)'; Body = '' })
        $r = ConvertFrom-GitLogRecords $text
        $r[0].Issues.Count | Should Be 0
        $r[0].Prs.Count | Should Be 1
        $r[0].Prs[0] | Should Be 49
    }

    It 'a bare !52 anywhere is a Pr' {
        $text = New-GitLogText @(@{ Sha = 'e'*40; Short = 'eeeeeee'; Subject = 'merge branch'; Body = "see !52 for context`n" })
        $r = ConvertFrom-GitLogRecords $text
        $r[0].Prs.Count | Should Be 1
        $r[0].Prs[0] | Should Be 52
    }

    It 'prose mentioning "PR #32 added..." is not read as a closing keyword' {
        $text = New-GitLogText @(@{ Sha = 'f'*40; Short = 'fffffff'; Subject = 'docs: mention PR #32 added a knob'; Body = '' })
        $r = ConvertFrom-GitLogRecords $text
        $r[0].Issues.Count | Should Be 0
    }

    It 'keyword matching is case-insensitive (Fix/FIX/fix)' {
        $text = New-GitLogText @(
            @{ Sha = '1'*40; Short = '1111111'; Subject = 'a'; Body = "Fix #10`n" },
            @{ Sha = '2'*40; Short = '2222222'; Subject = 'b'; Body = "FIX #11`n" },
            @{ Sha = '3'*40; Short = '3333333'; Subject = 'c'; Body = "fix #12`n" }
        )
        $r = ConvertFrom-GitLogRecords $text
        $r[0].Issues[0] | Should Be 10
        $r[1].Issues[0] | Should Be 11
        $r[2].Issues[0] | Should Be 12
    }

    It 'empty text yields an empty array' {
        $r = ConvertFrom-GitLogRecords ''
        $r.Count | Should Be 0
    }
}

# ===================================================================================================================

Describe 'Get-ChangelogEntryRefs' {

    $script:Changelog = @'
# Changelog

## [0.2.6] - unreleased
- audio: fix xrun logging (#48)
- store: fix submission bug (#48, !52)
- chore: tidy comments
- docs: mention issue (#412) mid-sentence, not a ref
- **A wrapped bullet.** The CHANGELOG wraps at ~118 columns, so the trailing group lands on the
  continuation line; a mid-sentence (#413) on the way is still not a ref. (#77)

## [0.2.5] - 2026-08-30
- audio: adaptive read-ahead (#99)
'@

    It 'picks only the [0.2.6] entry, ignoring refs in [0.2.5] below it' {
        $r = Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '0.2.6'
        $r.Issues -contains 99 | Should Be $false
    }

    It 'reads the trailing group (#48) as an Issue' {
        $r = Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '0.2.6'
        $r.Issues -contains 48 | Should Be $true
    }

    It 'ignores a mid-sentence (#412) - parity with ChangelogParserTests.cs:186' {
        $r = Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '0.2.6'
        $r.Issues -contains 412 | Should Be $false
    }

    It 'splits a trailing group "(#48, !52)" into an Issue and a Pr' {
        $r = Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '0.2.6'
        $r.Issues -contains 48 | Should Be $true
        $r.Prs -contains 52 | Should Be $true
    }

    It 'reads the trailing group of a WRAPPED bullet from its continuation line - parity with ChangelogParser.cs FlushBullet' {
        $r = Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '0.2.6'
        $r.Issues -contains 77 | Should Be $true
        $r.Issues -contains 413 | Should Be $false
    }

    It 'counts Bullets and Unreferenced (a wrapped bullet is ONE bullet)' {
        $r = Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '0.2.6'
        $r.Bullets | Should Be 5
        $r.Unreferenced | Should Be 2
    }

    It 'throws when the semver has no matching entry' {
        { Get-ChangelogEntryRefs -Changelog $script:Changelog -Semver '9.9.9' } | Should Throw
    }
}

# ===================================================================================================================

Describe 'Compare-ReleaseIssueRefs' {

    function New-Commit {
        param([string]$Short, [int[]]$Issues = @(), [int[]]$Prs = @())
        [pscustomobject]@{ Sha = $Short + ('0' * 33); Short = $Short; Subject = "commit $Short"; Issues = @($Issues); Prs = @($Prs) }
    }

    It 'a commit fixing an issue the CHANGELOG cites is Linked' {
        $commits = @(New-Commit -Short 'a1b2c3d' -Issues @(48))
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @(48) -Commits $commits
        $r.Linked.Count | Should Be 1
        $r.Linked[0].Issue | Should Be 48
        $r.MissingInChangelog.Count | Should Be 0
        $r.MissingInGit.Count | Should Be 0
    }

    It 'a commit fixing an issue the CHANGELOG does not cite is MissingInChangelog' {
        $commits = @(New-Commit -Short 'a1b2c3d' -Issues @(48))
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @() -Commits $commits
        $r.MissingInChangelog.Count | Should Be 1
        $r.MissingInChangelog[0].Issue | Should Be 48
        $r.Linked.Count | Should Be 0
    }

    It 'a CHANGELOG-cited issue no commit fixes is MissingInGit' {
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @(48) -Commits @()
        $r.MissingInGit.Count | Should Be 1
        $r.MissingInGit[0] | Should Be 48
        $r.Linked.Count | Should Be 0
    }

    It 'both a missing-in-changelog and a missing-in-git can occur in the same comparison' {
        $commits = @(New-Commit -Short 'a1b2c3d' -Issues @(48))
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @(41) -Commits $commits
        $r.MissingInChangelog.Count | Should Be 1
        $r.MissingInChangelog[0].Issue | Should Be 48
        $r.MissingInGit.Count | Should Be 1
        $r.MissingInGit[0] | Should Be 41
    }

    It 'a CHANGELOG issue satisfied only by a commit''s Pr ref (squash suffix) is Linked, not MissingInGit' {
        $commits = @(New-Commit -Short 'a1b2c3d' -Prs @(48))
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @(48) -Commits $commits
        $r.Linked.Count | Should Be 1
        $r.MissingInGit.Count | Should Be 0
        # A pr-only ref must not surface as MissingInChangelog (only closing-keyword Issues can).
        $r.MissingInChangelog.Count | Should Be 0
    }

    It 'a commit citing nothing is Unlinked' {
        $commits = @(New-Commit -Short 'a1b2c3d')
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @() -Commits $commits
        $r.Unlinked.Count | Should Be 1
        $r.Unlinked[0].Short | Should BeExactly 'a1b2c3d'
    }

    It 'handles an empty commit list and an empty CHANGELOG issue list without throwing' {
        { Compare-ReleaseIssueRefs -ChangelogIssues @() -Commits @() } | Should Not Throw
        $r = Compare-ReleaseIssueRefs -ChangelogIssues @() -Commits @()
        $r.Linked.Count | Should Be 0
        $r.MissingInChangelog.Count | Should Be 0
        $r.MissingInGit.Count | Should Be 0
        $r.Unlinked.Count | Should Be 0
    }
}

# ===================================================================================================================

Describe 'Format-IssueRefMismatch' {

    function New-Commit {
        param([string]$Short, [string]$Subject, [int[]]$Issues = @())
        [pscustomobject]@{ Sha = $Short + ('0' * 33); Short = $Short; Subject = $Subject; Issues = @($Issues); Prs = @() }
    }

    It 'formats a MissingInChangelog entry naming the commit short SHA and subject' {
        $commits = @(New-Commit -Short 'a1b2c3d' -Subject 'audio: fix xrun logging' -Issues @(48))
        $cmp = Compare-ReleaseIssueRefs -ChangelogIssues @() -Commits $commits
        $lines = Format-IssueRefMismatch -Comparison $cmp -Semver '0.2.6' -Range 'wavee-v0.2.5..HEAD'
        $lines.Count | Should Be 1
        $lines[0] | Should Match 'issue #48 is fixed by a1b2c3d "audio: fix xrun logging" but the CHANGELOG \[0\.2\.6\] entry does not cite it'
    }

    It 'formats a MissingInGit entry naming the semver and the range' {
        $cmp = Compare-ReleaseIssueRefs -ChangelogIssues @(48) -Commits @()
        $lines = Format-IssueRefMismatch -Comparison $cmp -Semver '0.2.6' -Range 'wavee-v0.2.5..HEAD'
        $lines.Count | Should Be 1
        $lines[0] | Should BeExactly "CHANGELOG [0.2.6] cites #48 but no commit in wavee-v0.2.5..HEAD carries 'Fixes #48'"
    }

    It 'returns an empty array when the comparison has nothing to report' {
        $cmp = Compare-ReleaseIssueRefs -ChangelogIssues @() -Commits @()
        $lines = Format-IssueRefMismatch -Comparison $cmp -Semver '0.2.6' -Range 'wavee-v0.2.5..HEAD'
        $lines.Count | Should Be 0
    }
}

# ===================================================================================================================

Describe 'Write-ReleaseCommitsJson' {

    $script:TmpRoot = Join-Path $env:TEMP ('wavee-releaserefs-tests-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $script:TmpRoot | Out-Null

    It 'writes camelCase keys and arrays even when Issues/Prs are empty' {
        $out = Join-Path $script:TmpRoot 'commits.json'
        $commits = @(
            [pscustomobject]@{ Sha = 'a'*40; Short = 'aaaaaaa'; Subject = 'fix a'; Issues = @(48); Prs = @() },
            [pscustomobject]@{ Sha = 'b'*40; Short = 'bbbbbbb'; Subject = 'chore b'; Issues = @(); Prs = @() }
        )
        Write-ReleaseCommitsJson -Commits $commits -Path $out
        Test-Path $out | Should Be $true
        $json = Get-Content -Raw -Path $out | ConvertFrom-Json
        $json.Count | Should Be 2
        $json[0].sha | Should BeExactly ('a'*40)
        $json[0].short | Should BeExactly 'aaaaaaa'
        $json[0].subject | Should BeExactly 'fix a'
        , $json[0].issues | Should BeOfType ([array])
        $json[0].issues[0] | Should Be 48
        , $json[1].issues | Should Not Be $null
        $json[1].issues.Count | Should Be 0
        $json[1].prs.Count | Should Be 0
    }

    It 'writes an empty JSON array (not null) for an empty commit list' {
        $out = Join-Path $script:TmpRoot 'commits-empty.json'
        Write-ReleaseCommitsJson -Commits @() -Path $out
        $text = Get-Content -Raw -Path $out
        # Windows PowerShell 5.1's ConvertTo-Json renders an empty array as "[\r\n\r\n]" rather than "[]" - assert
        # on the brackets (not-null, not the bare literal "null") rather than the exact whitespace between them.
        $text.TrimStart().StartsWith('[') | Should Be $true
        $text.TrimEnd().EndsWith(']') | Should Be $true
        $text.Trim() | Should Not BeExactly 'null'
    }
}

# ===================================================================================================================

Describe 'New-ShippedIssueComment' {

    It 'contains the release tag URL, every short SHA and the PR reference' {
        $commits = @(
            [pscustomobject]@{ Sha = 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2'; Short = 'a1b2c3d'; Subject = 'fix a'; Issues = @(48); Prs = @(52) },
            [pscustomobject]@{ Sha = 'b1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2'; Short = 'b1b2c3d'; Subject = 'fix b'; Issues = @(48); Prs = @() }
        )
        $body = New-ShippedIssueComment -Repo 'christosk92/WaveeMusic' -Tag 'wavee-v0.2.6' -Semver '0.2.6' -Codename 'Breaker' -Quad '0.2.6.1' -Commits $commits -Prs @(52)

        $body | Should Match ([regex]::Escape('https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.6'))
        $body | Should Match ([regex]::Escape('a1b2c3d'))
        $body | Should Match ([regex]::Escape('b1b2c3d'))
        $body | Should Match 'PR #52'
    }

    It 'omits the PR clause entirely when there are no PRs' {
        $commits = @([pscustomobject]@{ Sha = 'a'*40; Short = 'aaaaaaa'; Subject = 'fix a'; Issues = @(48); Prs = @() })
        $body = New-ShippedIssueComment -Repo 'christosk92/WaveeMusic' -Tag 'wavee-v0.2.6' -Semver '0.2.6' -Codename 'Breaker' -Quad '0.2.6.1' -Commits $commits -Prs @()
        $body | Should Not Match 'PR #'
    }
}

# ===================================================================================================================

Describe 'Test-IssueAlreadyNotified' {

    It 'is true when a comment body already contains this tag''s release URL' {
        $comments = @([pscustomobject]@{ body = 'Shipped in Wavee 0.2.5: https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.5 - enjoy!' })
        (Test-IssueAlreadyNotified -Comments $comments -Repo 'christosk92/WaveeMusic' -Tag 'wavee-v0.2.5') | Should Be $true
    }

    It 'is false when no comment mentions this tag' {
        $comments = @([pscustomobject]@{ body = 'thanks for the report' })
        (Test-IssueAlreadyNotified -Comments $comments -Repo 'christosk92/WaveeMusic' -Tag 'wavee-v0.2.5') | Should Be $false
    }

    It 'is false for an empty comment list' {
        (Test-IssueAlreadyNotified -Comments @() -Repo 'christosk92/WaveeMusic' -Tag 'wavee-v0.2.5') | Should Be $false
    }

    It 'does not false-positive on a different tag''s release URL' {
        $comments = @([pscustomobject]@{ body = 'Shipped in Wavee 0.2.4: https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.4' })
        (Test-IssueAlreadyNotified -Comments $comments -Repo 'christosk92/WaveeMusic' -Tag 'wavee-v0.2.5') | Should Be $false
    }
}
