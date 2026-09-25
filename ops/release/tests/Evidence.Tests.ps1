#requires -Version 5.1
<#
    Pester 3.4 (Windows PowerShell 5.1's bundled version - Describe / It / Should Be).
    Run with: Invoke-Pester ops\release\tests

    The evidence harness's PURE functions (ops/tools/evidence/Evidence.psm1; docs/plans/evidence-diagnostics-
    implementation.md §C in ..\fluent-gpu) over a fixture bundle (fixtures/evidence-bundle): the bundle TSV parse (comment
    lines skipped, header-named columns), the replies.tsv parse the harness polls, and the artist band's geometry and sweep
    offsets derived from keyed.tsv. No app launch, no window, no input.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path
Import-Module (Join-Path $repoRoot 'ops\tools\evidence\Evidence.psm1') -Force -DisableNameChecking
$fixture = Join-Path $here 'fixtures\evidence-bundle'

Describe 'Evidence bundle TSV' {

    It 'skips the # comment and names the columns from the header' {
        $rows = @(Read-EvidenceTsv (Join-Path $fixture 'pixel-612-71.tsv'))
        $rows.Count | Should Be 2
        $rows[1].kind | Should Be 'Region'
        $rows[1].nodeKey | Should Be 'artist-band/1/0'
        $rows[1].stale | Should Be '1'
        $rows[0].feather1 | Should Be '1.000'
    }

    It 'returns nothing for a missing file' {
        @(Read-EvidenceTsv (Join-Path $fixture 'nope.tsv')).Count | Should Be 0
    }
}

Describe 'Evidence replies' {

    It 'parses seq, pid, cmd, result and path' {
        $r = @(ConvertFrom-EvidenceReplies -Lines (Get-Content (Join-Path $fixture 'replies.tsv')))
        $r.Count | Should Be 3
        $r[1].Cmd | Should Be 'bundle'
        $r[1].Result | Should Be 'ok'
        $r[1].Path | Should Be 'C:\verify\logs\evidence\20260924-210509-band-rest'
        $r[2].Result | Should Be 'not-found'
        $r[0].Pid | Should Be 4242
    }

    It 'ignores blank and short lines' {
        @(ConvertFrom-EvidenceReplies -Lines @('', "1`t2`tx")).Count | Should Be 0
    }

    It 'reads replies.tsv from an evidence root' {
        @(Get-EvidenceReplies -Root $fixture).Count | Should Be 3
    }
}

Describe 'Artist band geometry' {

    It 'derives the collapse distance from the magazine''s content y minus the band' {
        $g = Get-BandGeometry -Keyed (Read-EvidenceTsv (Join-Path $fixture 'keyed.tsv'))
        $g.ViewportY | Should Be 88
        $g.MagazineContentY | Should Be 464
        $g.CollapseDistance | Should Be 408
        $g.RampStart | Should Be 364
    }

    It 'picks the named artist and the magazine in its column when another artist page is kept alive' {
        $keyed = @(
            [pscustomobject]@{ path = 'artist-scroll:artist:spotify:artist:OTHER'; x = '-8'; y = '0'; w = '812' }
            [pscustomobject]@{ path = 'artist-under-band'; x = '-8'; y = '384'; w = '812' }
            [pscustomobject]@{ path = 'artist-under-band'; x = '240'; y = '-340'; w = '812' }
            [pscustomobject]@{ path = 'artist-scroll:artist:spotify:artist:LAUV'; x = '240'; y = '48'; w = '812' }
        )
        $g = Get-BandGeometry -Keyed $keyed -ArtistUri 'spotify:artist:LAUV'
        $g.ViewportX | Should Be 240
        $g.ViewportY | Should Be 48
        $g.MagazineContentY | Should Be -388
        (Get-BandGeometry -Keyed $keyed -ArtistUri 'spotify:artist:NOPE') | Should Be $null
    }

    It 'is null without the keyed parts' {
        (Get-BandGeometry -Keyed @()) | Should Be $null
    }

    It 'sweeps rest, the 44-DIP ramp in 4-DIP steps through cd + 4, and pinned' {
        $o = @(Get-BandSweepOffsets -CollapseDistance 408)
        $o[0] | Should Be 0
        $o[1] | Should Be 364
        $o[$o.Count - 2] | Should Be 412
        $o[$o.Count - 1] | Should Be 608
        $o.Count | Should Be 15
    }
}

Describe 'Thumb geometry (the engine''s expanded overlay-bar metrics)' {

    It 'floors a long list''s thumb at 8 % of the track between the two 12-DIP arrow buttons' {
        $g = Get-ThumbGeometry -X 100 -Y 50 -W 800 -H 900 -Offset 0 -Extent 84000
        $g.X | Should Be 894
        $g.TrackTop | Should Be 62
        [math]::Round($g.Length, 2) | Should Be 70.08
        [math]::Round($g.Travel, 2) | Should Be 805.92
        [math]::Round($g.Y, 2) | Should Be 97.04
    }

    It 'puts the thumb at the end of its travel at the last offset, and maps a fraction onto the travel' {
        $g = Get-ThumbGeometry -X 0 -Y 0 -W 800 -H 900 -Offset 83100 -Extent 84000
        [math]::Round($g.Y, 2) | Should Be ([math]::Round(12 + 805.92 + 35.04, 2))
        [math]::Round($g.TrackTop + $g.Length / 2 + 0.5 * $g.Travel, 2) | Should Be ([math]::Round(12 + 35.04 + 402.96, 2))
    }

    It 'sizes a short list''s thumb by viewport / extent of the track' {
        $g = Get-ThumbGeometry -X 0 -Y 0 -W 400 -H 500 -Offset 0 -Extent 600
        [math]::Round($g.Length, 2) | Should Be 396.67
        [math]::Round($g.Travel, 2) | Should Be 79.33
    }

    It 'is null with nothing to scroll' {
        (Get-ThumbGeometry -X 0 -Y 0 -W 400 -H 500 -Offset 0 -Extent 500) | Should Be $null
    }
}
