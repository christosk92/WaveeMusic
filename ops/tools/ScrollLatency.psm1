#requires -Version 5.1
<#
.SYNOPSIS
  Pure parsing/analysis for the scroll-latency comparison tool (ops/tools/scroll-latency-compare.ps1).

.DESCRIPTION
  Everything here is engine-free and app-free: it turns PresentMon CSV rows and the engine's ScrollTrace CSV rows
  (both already parsed to PSCustomObjects, e.g. via Import-Csv) into latency/cadence numbers. No app launch, no
  window handles, no SendInput, no reading of Wavee/engine source - CLAUDE.md's "no source-text tests" rule reads
  the SAME way for this tool: the decision (how to join an input timestamp to a present, how to compute a
  percentile, how to walk RawWheel->OffsetWrite->Frame) lives in ordinary functions over data, testable with small
  fixtures, never by grepping production source.

  PresentMon has shipped two CSV column layouts (both seen on this machine, PresentMon 2.5.1):
    - without -qpc_time_ms:  ...,TimeInMs,...,MsUntilDisplayed,CPUStartTimeInMs,...        (relative-to-capture ms)
    - with    -qpc_time_ms:  ...,TimeInSeconds,...,MsUntilDisplayed,CPUStartQPCTimeInMs,... (CPUStart in ABSOLUTE
      QueryPerformanceCounter ms - the SAME clock domain as [System.Diagnostics.Stopwatch]::GetTimestamp(), which
      is what makes an input timestamp recorded by this machine's own SendInput driver joinable to a present
      recorded by a separate elevated PresentMon process).
    PresentMon 1.x (older, referenced defensively): TimeInSeconds, msUntilDisplayed (lowercase m), no CPUStart*
      column at all - TimeInSeconds doubles as the present-call reference time.
  Resolve-PresentMonColumns below is the single place that maps aliases to a canonical shape; every other function
  in this module operates on the NORMALIZED row shape only.
#>

Set-StrictMode -Version Latest

# -- column resolution --------------------------------------------------------------------------------------------

# Given the property names on one parsed CSV row (Import-Csv leaves every cell a string; PSObject.Properties.Name
# is what actually varies release to release), resolve the columns this module needs and say whether the start-time
# column is an ABSOLUTE QueryPerformanceCounter timestamp (only true for *QPCTime* columns, which -qpc_time_ms
# produces) - callers must refuse a cross-process join otherwise, because a capture-relative "ms since this
# PresentMon session started" number from one process is not comparable to one from another process or to our own
# Stopwatch-based input timestamps.
function Resolve-PresentMonColumns {
    param([Parameter(Mandatory = $true)][string[]]$PropertyNames)

    function FindOne([string[]]$Names, [string]$Pattern) {
        foreach ($n in $Names) { if ($n -match $Pattern) { return $n } }
        return $null
    }

    $displayedCol = FindOne $PropertyNames '^[Mm]s[Uu]ntil[Dd]isplayed$'
    $cadenceCol = FindOne $PropertyNames '^MsBetweenPresents$'
    $droppedCol = FindOne $PropertyNames '^Dropped$'
    $processIdCol = FindOne $PropertyNames '^ProcessID$'
    $applicationCol = FindOne $PropertyNames '^Application$'

    # Prefer an absolute-QPC start-time column, then a relative CPUStart* column, then TimeInMs, then TimeInSeconds
    # (which needs x1000). Order matters: CPUStartQPCTimeInMs must be found before the broader CPUStartTime pattern.
    $qpcStartCol = FindOne $PropertyNames '^CPUStartQPCTime'
    $isAbsoluteQpc = $false
    $startCol = $qpcStartCol
    $startIsSeconds = $false
    if ($startCol) { $isAbsoluteQpc = $true }
    else {
        $startCol = FindOne $PropertyNames '^CPUStartTime'
        if (-not $startCol) { $startCol = FindOne $PropertyNames '^TimeInMs$' }
        if (-not $startCol) { $startCol = FindOne $PropertyNames '^TimeInSeconds$'; if ($startCol) { $startIsSeconds = $true } }
    }

    if (-not $displayedCol) { throw 'Resolve-PresentMonColumns: no MsUntilDisplayed/msUntilDisplayed column found - is this a PresentMon CSV?' }
    if (-not $cadenceCol) { throw 'Resolve-PresentMonColumns: no MsBetweenPresents column found - is this a PresentMon CSV?' }
    if (-not $startCol) { throw 'Resolve-PresentMonColumns: no CPUStartTime*/TimeInMs/TimeInSeconds column found - is this a PresentMon CSV?' }

    return [pscustomobject]@{
        DisplayedCol    = $displayedCol
        CadenceCol      = $cadenceCol
        DroppedCol      = $droppedCol
        ProcessIdCol    = $processIdCol
        ApplicationCol  = $applicationCol
        StartCol        = $startCol
        StartIsSeconds  = $startIsSeconds
        IsAbsoluteQpc   = $isAbsoluteQpc
    }
}

function ConvertTo-NullableDouble([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -eq 'NA') { return $null }
    [double]$parsed = 0.0
    if ([double]::TryParse($Text, [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref]$parsed)) { return $parsed }
    return $null
}

# -- PresentMon row normalization ---------------------------------------------------------------------------------

<#
.SYNOPSIS
  Turn raw PresentMon CSV rows (as returned by Import-Csv - or any array of PSCustomObjects with the same string
  properties) into the normalized shape every other function in this module consumes.

.OUTPUTS
  [pscustomobject]@{
    IsAbsoluteQpc = <bool>          # true only when the CPUStart column came from -qpc_time_ms
    Rows = @(
      [pscustomobject]@{
        Application; ProcessId
        StartMs               # present-call reference time, in ms; ABSOLUTE QPC-ms iff IsAbsoluteQpc
        MsBetweenPresents      # double, may be $null for the first present of the app
        MsUntilDisplayed        # nullable double
        DisplayedMs             # StartMs + MsUntilDisplayed when both known, else $null
        Dropped                # bool - inferred from a Dropped column if present, else MsUntilDisplayed being NA/blank
      }
    )
  }
#>
function ConvertTo-NormalizedPresentMonRows {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Rows)

    if ($Rows.Count -eq 0) { return [pscustomobject]@{ IsAbsoluteQpc = $false; Rows = @() } }

    $propNames = @($Rows[0].PSObject.Properties.Name)
    $cols = Resolve-PresentMonColumns -PropertyNames $propNames

    $out = New-Object System.Collections.Generic.List[object]
    foreach ($r in $Rows) {
        $startRaw = ConvertTo-NullableDouble ([string]$r.($cols.StartCol))
        $startMs = $null
        if ($null -ne $startRaw) { $startMs = $(if ($cols.StartIsSeconds) { $startRaw * 1000.0 } else { $startRaw }) }
        $between = ConvertTo-NullableDouble ([string]$r.($cols.CadenceCol))
        $displayed = ConvertTo-NullableDouble ([string]$r.($cols.DisplayedCol))
        $displayedMs = $null
        if ($null -ne $startMs -and $null -ne $displayed) { $displayedMs = $startMs + $displayed }
        $dropped = $false
        if ($cols.DroppedCol) { $dropped = ([string]$r.($cols.DroppedCol)) -in @('1', 'true', 'True') }
        else { $dropped = ($null -eq $displayed) }

        $out.Add([pscustomobject]@{
            Application       = $(if ($cols.ApplicationCol) { [string]$r.($cols.ApplicationCol) } else { '' })
            ProcessId         = $(if ($cols.ProcessIdCol) { [string]$r.($cols.ProcessIdCol) } else { '' })
            StartMs           = $startMs
            MsBetweenPresents = $between
            MsUntilDisplayed  = $displayed
            DisplayedMs       = $displayedMs
            Dropped           = $dropped
        })
    }
    return [pscustomobject]@{ IsAbsoluteQpc = [bool]$cols.IsAbsoluteQpc; Rows = $out.ToArray() }
}

function Import-PresentMonCsv {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "No such PresentMon CSV: $Path" }
    $raw = @(Import-Csv -LiteralPath $Path)
    return ConvertTo-NormalizedPresentMonRows -Rows $raw
}

# -- percentiles --------------------------------------------------------------------------------------------------

# Nearest-rank percentile over a plain double[]. $null/empty input returns $null (never a fabricated 0), matching
# this repo's "a missing signal is not a zero" discipline (see parse-scroll-csv.ps1 / the bug-handoff doc section 2.5).
function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][double[]]$Values,
        [Parameter(Mandatory = $true)][ValidateRange(0, 100)][double]$Percentile
    )
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $idx = [int][math]::Floor(($Percentile / 100.0) * ($sorted.Count - 1))
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -gt $sorted.Count - 1) { $idx = $sorted.Count - 1 }
    return $sorted[$idx]
}

# p50/p90/p99 + mean + count in one call, the shape the compare script's report table wants.
function Get-LatencyPercentiles {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][double[]]$Values)
    if ($Values.Count -eq 0) { return [pscustomobject]@{ Count = 0; Mean = $null; P50 = $null; P90 = $null; P99 = $null } }
    $mean = ($Values | Measure-Object -Average).Average
    return [pscustomobject]@{
        Count = $Values.Count
        Mean  = [math]::Round($mean, 3)
        P50   = [math]::Round((Get-Percentile -Values $Values -Percentile 50), 3)
        P90   = [math]::Round((Get-Percentile -Values $Values -Percentile 90), 3)
        P99   = [math]::Round((Get-Percentile -Values $Values -Percentile 99), 3)
    }
}

# -- present cadence ----------------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Present cadence over one app's normalized PresentMon rows: mean/p99 MsBetweenPresents and a dropped-frame count.
#>
function Get-PresentCadence {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$NormalizedRows)

    $betweens = @($NormalizedRows | Where-Object { $null -ne $_.MsBetweenPresents } | ForEach-Object { [double]$_.MsBetweenPresents })
    $dropped = @($NormalizedRows | Where-Object { $_.Dropped }).Count
    $percentiles = Get-LatencyPercentiles -Values $betweens
    return [pscustomobject]@{
        Frames         = $NormalizedRows.Count
        MeanIntervalMs = $percentiles.Mean
        P99IntervalMs  = $percentiles.P99
        DroppedFrames  = $dropped
        DroppedPct     = $(if ($NormalizedRows.Count -gt 0) { [math]::Round(100.0 * $dropped / $NormalizedRows.Count, 2) } else { $null })
    }
}

# -- input -> present join ----------------------------------------------------------------------------------------

<#
.SYNOPSIS
  Join each injected-input QPC-ms timestamp to the first present whose reference time is at/after it.

.DESCRIPTION
  Both InputTimestampsMs and the rows' StartMs must be in the SAME clock domain (absolute QueryPerformanceCounter,
  ms) - i.e. NormalizedPresentMon.IsAbsoluteQpc must be true (PresentMon run with -qpc_time_ms) and the input
  timestamps recorded via [System.Diagnostics.Stopwatch]::GetTimestamp()/Frequency*1000. Presents are matched on
  DisplayedMs when known (StartMs + MsUntilDisplayed - the honest "input to something reaching the screen" number),
  falling back to StartMs (present-call time) when MsUntilDisplayed is unavailable/NA for that row - Matched is
  still true in the fallback case, but ByDisplayedTime is false, so a caller can separate the two.

  An input with no later present (the gesture outlived the capture window, or nothing repainted) comes back with
  LatencyMs = $null and Matched = $false - never a fabricated number.
#>
function Join-InputToPresent {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][double[]]$InputTimestampsMs,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$NormalizedRows
    )

    # Candidate present time per row: DisplayedMs when known, else StartMs. Pre-sort ascending once.
    $candidates = @(
        $NormalizedRows | ForEach-Object {
            $t = $(if ($null -ne $_.DisplayedMs) { $_.DisplayedMs } else { $_.StartMs })
            if ($null -ne $t) {
                [pscustomobject]@{ T = [double]$t; ByDisplayedTime = ($null -ne $_.DisplayedMs) }
            }
        } | Sort-Object T
    )

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($inputMs in $InputTimestampsMs) {
        $match = $null
        foreach ($c in $candidates) {
            if ($c.T -ge $inputMs) { $match = $c; break }
        }
        if ($null -eq $match) {
            $results.Add([pscustomobject]@{ InputMs = $inputMs; PresentMs = $null; LatencyMs = $null; Matched = $false; ByDisplayedTime = $false })
        }
        else {
            $results.Add([pscustomobject]@{
                InputMs = $inputMs; PresentMs = $match.T; LatencyMs = [math]::Round($match.T - $inputMs, 3)
                Matched = $true; ByDisplayedTime = $match.ByDisplayedTime
            })
        }
    }
    # Force-array: a bare `return` of a one-element collection unwraps to a scalar on the pipeline, which would
    # make a single-input caller's `$result.Count` silently $null instead of 1.
    return ,$results.ToArray()
}

# -- ScrollTrace (engine CSV) -------------------------------------------------------------------------------------

# Columns per the engine's own contract (fluent-gpu-pin/src/FluentGpu.Engine/Foundation/ScrollTrace.cs):
#   tMs,frame,kind,i0,i1,i2,f0,f1,f2,f3,f4,f5,auxMs,state,ack
# RawWheel:    i0=notch i1=flags i2=phaseSeq f0=dip f1=gapMs        aux=packet QPC mapped to the tMs axis
# OffsetWrite: i0=nodeIdx i1=activity i2=writer f0=offset            (no aux - written synchronously in-frame)
# Frame:       f0=dtMs f1=inputKindMask i0=pumped i1=scrollActive i2=spinSuppressed
# Join on tMs (the class doc: "frame [the counter column] is NOT a join key... join on tMs").
function ConvertTo-NullableDoubleOrZero([string]$Text) {
    $v = ConvertTo-NullableDouble $Text
    if ($null -eq $v) { return 0.0 }
    return $v
}

function Import-ScrollTraceRows {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "No such ScrollTrace CSV: $Path" }
    $raw = @(Import-Csv -LiteralPath $Path)
    return ConvertTo-NormalizedScrollTraceRows -Rows $raw
}

function ConvertTo-NormalizedScrollTraceRows {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Rows)
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($r in $Rows) {
        $out.Add([pscustomobject]@{
            TMs   = ConvertTo-NullableDoubleOrZero ([string]$r.tMs)
            Kind  = [string]$r.kind
            I0    = ConvertTo-NullableDoubleOrZero ([string]$r.i0)
            I1    = ConvertTo-NullableDoubleOrZero ([string]$r.i1)
            I2    = ConvertTo-NullableDoubleOrZero ([string]$r.i2)
            F0    = ConvertTo-NullableDoubleOrZero ([string]$r.f0)
            F1    = ConvertTo-NullableDoubleOrZero ([string]$r.f1)
        })
    }
    return ,$out.ToArray()
}

<#
.SYNOPSIS
  The RawWheel -> OffsetWrite -> Frame breakdown for a ScrollTrace capture: how long from the packet reaching the
  Win32 producer to the offset write that applied it, and from that write to the next Frame boundary (the
  engine-side proxy for "the present of that frame" - ScrollTrace's own doc: join on tMs; there is no per-row
  present timestamp on OffsetWrite itself, so the enclosing Frame row's tMs is the nearest honest proxy).

.PARAMETER MaxWindowMs
  A RawWheel with no OffsetWrite (or an OffsetWrite with no Frame) within this many ms is reported unmatched
  rather than joined to some unrelated later event from a different gesture.
#>
function Get-ScrollTraceBreakdown {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$NormalizedRows,
        [double]$MaxWindowMs = 500.0
    )
    $rawWheelRows = @($NormalizedRows | Where-Object { $_.Kind -eq 'RawWheel' } | Sort-Object TMs)
    $offsetWriteRows = @($NormalizedRows | Where-Object { $_.Kind -eq 'OffsetWrite' } | Sort-Object TMs)
    $frameRows = @($NormalizedRows | Where-Object { $_.Kind -eq 'Frame' } | Sort-Object TMs)

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($rw in $rawWheelRows) {
        $ow = $offsetWriteRows | Where-Object { $_.TMs -ge $rw.TMs -and ($_.TMs - $rw.TMs) -le $MaxWindowMs } | Select-Object -First 1
        $receiveToWriteMs = $null
        $writeToFrameMs = $null
        $totalMs = $null
        $frameTMs = $null
        $owTMs = $null
        if ($ow) {
            $owTMs = $ow.TMs
            $receiveToWriteMs = [math]::Round($ow.TMs - $rw.TMs, 3)
            $fr = $frameRows | Where-Object { $_.TMs -ge $ow.TMs -and ($_.TMs - $ow.TMs) -le $MaxWindowMs } | Select-Object -First 1
            if ($fr) {
                $frameTMs = $fr.TMs
                $writeToFrameMs = [math]::Round($fr.TMs - $ow.TMs, 3)
                $totalMs = [math]::Round($fr.TMs - $rw.TMs, 3)
            }
        }
        $results.Add([pscustomobject]@{
            RawWheelTMs      = $rw.TMs
            OffsetWriteTMs   = $owTMs
            FrameTMs         = $frameTMs
            ReceiveToWriteMs = $receiveToWriteMs
            WriteToFrameMs   = $writeToFrameMs
            TotalMs          = $totalMs
            Matched          = ($null -ne $totalMs)
        })
    }
    return ,$results.ToArray()
}

Export-ModuleMember -Function `
    Resolve-PresentMonColumns, ConvertTo-NormalizedPresentMonRows, Import-PresentMonCsv, `
    Get-Percentile, Get-LatencyPercentiles, Get-PresentCadence, Join-InputToPresent, `
    Import-ScrollTraceRows, ConvertTo-NormalizedScrollTraceRows, Get-ScrollTraceBreakdown
