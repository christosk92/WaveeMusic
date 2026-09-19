#requires -Version 5.1
<#
    Pester 3.4 (Windows PowerShell 5.1's bundled version - Describe / Context / It / Should Be / Should Throw).
    Run with: Invoke-Pester ops\release\tests

    Covers the pure parsing/analysis functions behind ops/tools/scroll-latency-compare.ps1:
      - both PresentMon CSV column layouts this machine has produced (plain and -qpc_time_ms)
      - the input -> present join, including an input with no later present
      - percentile math on a small fixture
      - the ScrollTrace RawWheel -> OffsetWrite -> Frame breakdown join on a 6-row fixture

    No app launch, no PresentMon invocation, no window/input APIs, no reading of production source - every fixture
    below is data, built the same way Wavee.Store.Bundle.Tests.ps1 builds synthetic msix payloads.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path
Import-Module (Join-Path $repoRoot 'ops\tools\ScrollLatency.psm1') -Force -DisableNameChecking

$script:TmpRoot = Join-Path $env:TEMP ('wavee-scrolllatency-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:TmpRoot | Out-Null

function New-TmpCsv {
    param([string]$Name, [string[]]$Lines)
    $p = Join-Path $script:TmpRoot $Name
    Set-Content -Path $p -Value $Lines -Encoding UTF8
    $p
}

# ===================================================================================================================

Describe 'PresentMon column resolution' {

    It 'resolves the -qpc_time_ms layout and flags it absolute' {
        $rows = @(
            [pscustomobject]@{ Application = 'dwm.exe'; ProcessID = '2548'; TimeInSeconds = '23.8430'; MsBetweenPresents = '9.0071'; MsUntilDisplayed = '3.3842'; CPUStartQPCTimeInMs = '279150449.6520' }
            [pscustomobject]@{ Application = 'dwm.exe'; ProcessID = '2548'; TimeInSeconds = '31.6910'; MsBetweenPresents = '7.8480'; MsUntilDisplayed = '3.8676'; CPUStartQPCTimeInMs = '279150458.6591' }
        )
        $norm = ConvertTo-NormalizedPresentMonRows -Rows $rows
        $norm.IsAbsoluteQpc | Should Be $true
        $norm.Rows.Count | Should Be 2
        $norm.Rows[0].StartMs | Should Be 279150449.652
        $norm.Rows[0].DisplayedMs | Should Be ([math]::Round(279150449.652 + 3.3842, 10))
        $norm.Rows[0].Dropped | Should Be $false
    }

    It 'resolves the plain (no -qpc_time_ms) v2 layout as relative, not absolute' {
        $rows = @(
            [pscustomobject]@{ Application = 'dwm.exe'; ProcessID = '2548'; TimeInMs = '19.8924'; MsBetweenPresents = '9.0240'; MsUntilDisplayed = '10.8684'; CPUStartTimeInMs = '9.0240' }
        )
        $norm = ConvertTo-NormalizedPresentMonRows -Rows $rows
        $norm.IsAbsoluteQpc | Should Be $false
        $norm.Rows[0].StartMs | Should Be 9.024
    }

    It 'resolves a PresentMon 1.x-shaped layout: TimeInSeconds + lowercase msUntilDisplayed, no CPUStart column' {
        $rows = @(
            [pscustomobject]@{ Application = 'app.exe'; ProcessID = '111'; TimeInSeconds = '1.250000'; MsBetweenPresents = '16.667'; msUntilDisplayed = '5.000'; Dropped = '0' }
            [pscustomobject]@{ Application = 'app.exe'; ProcessID = '111'; TimeInSeconds = '1.266667'; MsBetweenPresents = '16.667'; msUntilDisplayed = 'NA'; Dropped = '1' }
        )
        $norm = ConvertTo-NormalizedPresentMonRows -Rows $rows
        $norm.IsAbsoluteQpc | Should Be $false
        $norm.Rows[0].StartMs | Should Be 1250
        $norm.Rows[0].MsUntilDisplayed | Should Be 5
        $norm.Rows[0].Dropped | Should Be $false
        $norm.Rows[1].MsUntilDisplayed | Should Be $null
        $norm.Rows[1].Dropped | Should Be $true
    }

    It 'throws on a CSV that is missing the columns this module needs' {
        $rows = @([pscustomobject]@{ Foo = '1'; Bar = '2' })
        { ConvertTo-NormalizedPresentMonRows -Rows $rows } | Should Throw
    }

    It 'round-trips through Import-PresentMonCsv from a real file' {
        $csv = New-TmpCsv 'pm.csv' @(
            'Application,ProcessID,TimeInSeconds,MsBetweenPresents,MsUntilDisplayed,CPUStartQPCTimeInMs'
            'SystemSettings.exe,44744,10.0,8.333,4.0,1000000.000'
            'SystemSettings.exe,44744,18.3,8.300,4.2,1000008.333'
        )
        $norm = Import-PresentMonCsv -Path $csv
        $norm.IsAbsoluteQpc | Should Be $true
        $norm.Rows.Count | Should Be 2
        $norm.Rows[1].StartMs | Should Be 1000008.333
    }
}

Describe 'Get-Percentile / Get-LatencyPercentiles' {

    It 'computes p50/p90/p99 by nearest-rank over 1..100' {
        $values = [double[]](1..100)
        (Get-Percentile -Values $values -Percentile 50) | Should Be 50
        (Get-Percentile -Values $values -Percentile 90) | Should Be 90
        (Get-Percentile -Values $values -Percentile 99) | Should Be 99
    }

    It 'returns $null for an empty set rather than a fabricated zero' {
        (Get-Percentile -Values @() -Percentile 50) | Should Be $null
        $lp = Get-LatencyPercentiles -Values @()
        $lp.Count | Should Be 0
        $lp.Mean | Should Be $null
        $lp.P99 | Should Be $null
    }

    It 'reports mean alongside percentiles on a small known fixture' {
        $lp = Get-LatencyPercentiles -Values @(10.0, 20.0, 30.0)
        $lp.Count | Should Be 3
        $lp.Mean | Should Be 20
        $lp.P50 | Should Be 20
    }
}

Describe 'Get-PresentCadence' {

    It 'computes mean/p99 interval and counts dropped frames' {
        $rows = @(
            [pscustomobject]@{ MsBetweenPresents = 8.0; Dropped = $false }
            [pscustomobject]@{ MsBetweenPresents = 9.0; Dropped = $false }
            [pscustomobject]@{ MsBetweenPresents = 40.0; Dropped = $true }
        )
        $c = Get-PresentCadence -NormalizedRows $rows
        $c.Frames | Should Be 3
        $c.DroppedFrames | Should Be 1
        $c.DroppedPct | Should Be ([math]::Round(100.0 / 3.0, 2))
        $c.MeanIntervalMs | Should Be ([math]::Round((8.0 + 9.0 + 40.0) / 3.0, 3))
    }

    It 'handles an empty capture without throwing' {
        $c = Get-PresentCadence -NormalizedRows @()
        $c.Frames | Should Be 0
        $c.DroppedFrames | Should Be 0
        $c.DroppedPct | Should Be $null
    }
}

Describe 'Join-InputToPresent' {

    # Same clock domain as -qpc_time_ms: absolute QPC-ms. Two presents, one at 1000 one at 1020 (DisplayedMs).
    $rows = @(
        [pscustomobject]@{ StartMs = 995.0; DisplayedMs = 1000.0 }
        [pscustomobject]@{ StartMs = 1012.0; DisplayedMs = 1020.0 }
    )

    It 'joins an input to the first present at/after it, preferring DisplayedMs' {
        $result = Join-InputToPresent -InputTimestampsMs @(990.0) -NormalizedRows $rows
        $result[0].Matched | Should Be $true
        $result[0].PresentMs | Should Be 1000.0
        $result[0].LatencyMs | Should Be 10.0
        $result[0].ByDisplayedTime | Should Be $true
    }

    It 'joins an input that lands between two presents to the later one' {
        $result = Join-InputToPresent -InputTimestampsMs @(1005.0) -NormalizedRows $rows
        $result[0].PresentMs | Should Be 1020.0
        $result[0].LatencyMs | Should Be 15.0
    }

    It 'reports Matched=$false and LatencyMs=$null for an input with no later present' {
        $result = Join-InputToPresent -InputTimestampsMs @(1025.0) -NormalizedRows $rows
        $result[0].Matched | Should Be $false
        $result[0].LatencyMs | Should Be $null
        $result[0].PresentMs | Should Be $null
    }

    It 'falls back to StartMs (and reports ByDisplayedTime=$false) when a row has no DisplayedMs' {
        $noDisplay = @([pscustomobject]@{ StartMs = 2000.0; DisplayedMs = $null })
        $result = Join-InputToPresent -InputTimestampsMs @(1990.0) -NormalizedRows $noDisplay
        $result[0].Matched | Should Be $true
        $result[0].PresentMs | Should Be 2000.0
        $result[0].ByDisplayedTime | Should Be $false
    }

    It 'joins a whole batch of inputs, one row per input, in order' {
        $result = Join-InputToPresent -InputTimestampsMs @(990.0, 1005.0, 1025.0) -NormalizedRows $rows
        $result.Count | Should Be 3
        $result[0].Matched | Should Be $true
        $result[1].Matched | Should Be $true
        $result[2].Matched | Should Be $false
    }
}

Describe 'Get-ScrollTraceBreakdown' {

    # 6-row fixture: two full gestures (RawWheel -> OffsetWrite -> Frame each), tMs increasing.
    function New-STRow([double]$TMs, [string]$Kind) {
        [pscustomobject]@{ TMs = $TMs; Kind = $Kind; I0 = 0.0; I1 = 0.0; I2 = 0.0; F0 = 0.0; F1 = 0.0 }
    }
    $rows = @(
        (New-STRow 100.0 'RawWheel')
        (New-STRow 102.5 'OffsetWrite')
        (New-STRow 108.3 'Frame')
        (New-STRow 250.0 'RawWheel')
        (New-STRow 253.1 'OffsetWrite')
        (New-STRow 259.9 'Frame')
    )

    It 'walks RawWheel -> OffsetWrite -> Frame for every gesture in the fixture' {
        $b = Get-ScrollTraceBreakdown -NormalizedRows $rows
        $b.Count | Should Be 2
        $b[0].Matched | Should Be $true
        $b[0].ReceiveToWriteMs | Should Be 2.5
        $b[0].WriteToFrameMs | Should Be ([math]::Round(108.3 - 102.5, 3))
        $b[0].TotalMs | Should Be ([math]::Round(108.3 - 100.0, 3))
        $b[1].ReceiveToWriteMs | Should Be ([math]::Round(253.1 - 250.0, 3))
        $b[1].TotalMs | Should Be ([math]::Round(259.9 - 250.0, 3))
    }

    It 'reports Matched=$false for a RawWheel with no OffsetWrite inside the window' {
        $stranded = @(
            (New-STRow 100.0 'RawWheel')
            (New-STRow 900.0 'OffsetWrite')   # far outside the default 500ms window
            (New-STRow 905.0 'Frame')
        )
        $b = Get-ScrollTraceBreakdown -NormalizedRows $stranded -MaxWindowMs 500.0
        $b.Count | Should Be 1
        $b[0].Matched | Should Be $false
        $b[0].OffsetWriteTMs | Should Be $null
    }

    It 'returns a true one-element array (not an unwrapped scalar) for a single-gesture join' {
        $one = @( (New-STRow 100.0 'RawWheel'), (New-STRow 101.0 'OffsetWrite'), (New-STRow 102.0 'Frame') )
        $result = @(Join-InputToPresent -InputTimestampsMs @(50.0) -NormalizedRows @([pscustomobject]@{ StartMs = 60.0; DisplayedMs = $null }))
        $result.Count | Should Be 1
        $b = @(Get-ScrollTraceBreakdown -NormalizedRows $one)
        $b.Count | Should Be 1
    }

    It 'reports a matched write but an unmatched frame when the write never reaches a Frame boundary' {
        $noFrame = @(
            (New-STRow 100.0 'RawWheel')
            (New-STRow 101.0 'OffsetWrite')
        )
        $b = Get-ScrollTraceBreakdown -NormalizedRows $noFrame
        $b[0].OffsetWriteTMs | Should Be 101.0
        $b[0].FrameTMs | Should Be $null
        $b[0].Matched | Should Be $false
    }

    It 'round-trips through Import-ScrollTraceRows from a real 6-row CSV fixture' {
        $csv = New-TmpCsv 'scrolltrace.csv' @(
            'tMs,frame,kind,i0,i1,i2,f0,f1,f2,f3,f4,f5,auxMs,state,ack'
            '100.0,1,RawWheel,-1,64,0,-0.5,12.0,,,,,99.5,,'
            '102.5,1,OffsetWrite,7,1,1,120.5,,,,,,,,'
            '108.3,2,Frame,1,1,0,2.1,4,,,,,,,'
            '250.0,10,RawWheel,-1,64,0,-0.5,140.0,,,,,249.4,,'
            '253.1,10,OffsetWrite,7,1,1,125.6,,,,,,,,'
            '259.9,11,Frame,1,1,0,2.3,4,,,,,,,'
        )
        $norm = Import-ScrollTraceRows -Path $csv
        $norm.Count | Should Be 6
        $b = Get-ScrollTraceBreakdown -NormalizedRows $norm
        $b.Count | Should Be 2
        $b[0].Matched | Should Be $true
        $b[0].TotalMs | Should Be ([math]::Round(108.3 - 100.0, 3))
    }
}
