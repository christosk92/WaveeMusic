<#
.SYNOPSIS
  Diff two `--frame-bench` summaries (frame-bench-summary.json) and flag regressions.

.DESCRIPTION
  Reads two summaries written by `Wavee.exe ... --frame-bench --probe-out <dir>` (docs/guide/frame-bench.md), matches
  scenarios by name and metrics by key, and prints one row per metric: before, after, delta, % change, and a flag:
    REGRESSED  worse by more than -Threshold percent AND by more than the metric's absolute floor
    improved   better by the same margins
  Direction per metric mirrors Diagnostics.FrameBenchMath.Better (Screens/Diagnostics.FrameBench.cs): presents per second and
  the audio padding minimum are higher-is-better; run-describing figures are neutral (wallSec, frames, paintedFrames, presents,
  gpuFrames, framesPerSec, paintedFramesPerSec, cyclesPerMs.window, settleSec, scrollViewports, scrollSteps, the census
  exitPerSec.* / wakePerSec.* / turnPerSec.*, and the arms *.off / *.on of an A/B); everything else (CPU, GPU, allocations, GC,
  memory, missed vsyncs, underruns) is lower-is-better. Every total in a summary is already per second.
  CPU HEADLINE = raw cycles (processGcyclesPerSec, uiMcyclesPerSec, renderMcyclesPerSec, otherMcyclesPerSec, uiKcyclesPerPaintedFrame,
  renderKcyclesPerTurn): rate-free, lower is better, flagged at -CycleThreshold. processCpuPct, uiCoresTimes, renderCoresTimes and
  paintedCpuMs.* are INFO only (never flagged): GetProcessTimes/GetThreadTimes charge whole ~15.6 ms scheduler ticks, so at low
  load a run lands in one of two modes (docs/guide/frame-bench.md, "Measuring CPU: cycles, not time").
  Absolute floors keep timer noise out of the flags: 0.05 for a metric in ms, us or cores, 1 MB for MB, 1 % for a percentage,
  64 bytes for an allocation rate, 0.1 for a per-second rate, 1 for a count.

  Before comparing it WARNS when the two runs are not alike: fake vs real data, window/panel, processors, measure or warm-up
  window, GPU pass timing on one side only, or a cycles-per-ms rate more than 1 % apart (CPU figures in cycles are then on
  different scales; processCpuPct, uiCoresTimes and renderCoresTimes come from GetProcessTimes/GetThreadTimes and need no rate).

  A scenario skipped on either side, or a metric null (NaN) on either side, is reported, never compared.
  Tests: ops/tools/frame-bench-compare.tests.ps1.

.PARAMETER Before
  The baseline summary (a file, or a directory holding frame-bench-summary.json).

.PARAMETER After
  The candidate summary (same forms).

.PARAMETER Threshold
  Percent change that counts (default 10).

.PARAMETER CycleThreshold
  Percent change that counts for the raw-cycle CPU metrics (default 5; they are stable to about 3 %).

.PARAMETER All
  Print every metric. Default: the headline set.

.PARAMETER Scenario
  Only these scenarios (names, comma-separated or an array).

.PARAMETER FailOnRegression
  Exit 1 when anything is flagged REGRESSED.

.EXAMPLE
  powershell -File ops\tools\frame-bench-compare.ps1 -Before bench\main -After bench\lane-a
.EXAMPLE
  powershell -File ops\tools\frame-bench-compare.ps1 bench\cold\frame-bench-summary.json bench\warm -Threshold 5 -All
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Before,
    [Parameter(Mandatory = $true, Position = 1)][string]$After,
    [double]$Threshold = 10,
    [double]$CycleThreshold = 5,
    [switch]$All,
    [string[]]$Scenario,
    [switch]$FailOnRegression
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Read-Summary([string]$path) {
    if (Test-Path -LiteralPath $path -PathType Container) { $path = Join-Path $path 'frame-bench-summary.json' }
    if (-not (Test-Path -LiteralPath $path)) { throw "no summary at $path" }
    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($json.schema -notin @('wavee-frame-bench/1', 'wavee-frame-bench/2', 'wavee-frame-bench/2.1')) { throw "$path is not a wavee-frame-bench summary (schema '$($json.schema)')" }
    return $json
}

function Get-Prop($obj, [string]$name) {
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# Mirrors FrameBenchMath.Better: +1 higher is better, 0 neutral, -1 lower is better.
function Test-InfoCpu([string]$key) {
    return ($key -in @('processCpuPct', 'uiCoresTimes', 'renderCoresTimes', 'overheadProcessCpuPct') -or $key -like 'processCpuPct.*' -or $key -like 'paintedCpuMs*')
}

function Get-Direction([string]$key) {
    if (Test-InfoCpu $key) { return 0 }
    if ($key -in @('presentsPerSec', 'audioPaddingMinMs')) { return 1 }
    if ($key -in @('wallSec', 'frames', 'paintedFrames', 'presents', 'gpuFrames', 'framesPerSec', 'paintedFramesPerSec',
                   'cyclesPerMs.window', 'settleSec', 'scrollViewports', 'scrollSteps')) { return 0 }
    if ($key -like 'exitPerSec.*' -or $key -like 'wakePerSec.*' -or $key -like 'turnPerSec.*' -or $key -like '*.off' -or $key -like '*.on') { return 0 }
    return -1
}

function Get-Floor([string]$key) {
    if ($key -match 'Gcycles') { return 0.01 }
    if ($key -match 'KcyclesPer') { return 10.0 }
    if ($key -match 'Mcycles') { return 2.0 }
    if ($key -match 'MB(\.|$)') { return 1.0 }
    if ($key -match 'Pct') { return 1.0 }
    if ($key -match 'Bytes') { return 64.0 }
    if ($key -match '(Ms|Us)(\.|$)|Cores') { return 0.05 }
    if ($key -match 'PerSec') { return 0.1 }
    return 1.0
}

$headline = @(
    'presentsPerSec', 'presentIntervalMs.p99',
    'paintedCpuMs.p50', 'paintedCpuMs.p99', 'renderCpuMs.p50', 'renderCpuMs.p99', 'otherCpuMs.avg',
    'processGcyclesPerSec', 'uiMcyclesPerSec', 'renderMcyclesPerSec', 'otherMcyclesPerSec', 'uiKcyclesPerPaintedFrame', 'renderKcyclesPerTurn',
    'uiCores', 'renderCores', 'otherCores', 'uiCoresTimes', 'renderCoresTimes', 'processCpuPct',
    'gpuMs.p50', 'gpuMs.p99', 'gpuBusyPct',
    'uiAllocBytesPerFrame', 'paintedAllocBytesPerFrame', 'processAllocBytesPerSec', 'gc0PerSec', 'gc2PerSec', 'gcPauseMsPerSec',
    'workingSetMB.avg', 'privateMB.avg', 'vramLocalMB.avg', 'missedVsyncsPerSec', 'audioDeviceUnderrunsPerSec', 'audioXrunsPerSec',
    'scrollViewports', 'settleSec',
    'overheadUiCpuUsPerFrame', 'overheadRunFrameWallUs', 'overheadProcessCpuPct', 'overheadGpuMs'
)

$b = Read-Summary $Before
$a = Read-Summary $After
Write-Output ("before: {0} {1} {2} {3}" -f $b.version, $b.data, $b.label, $b.utc)
Write-Output ("after:  {0} {1} {2} {3}" -f $a.version, $a.data, $a.label, $a.utc)
$script:warnings = 0
function Warn([string]$text) { Write-Output "WARNING: $text"; $script:warnings++ }
if ($b.data -ne $a.data) { Warn "comparing $($b.data) data against $($a.data) data" }
if ($b.windowPx -ne $a.windowPx -or $b.refreshHz -ne $a.refreshHz) { Warn "window/panel differ ($($b.windowPx)@$($b.refreshHz)Hz vs $($a.windowPx)@$($a.refreshHz)Hz)" }
foreach ($k in @('processors', 'measureSec', 'warmupSec', 'gpuPasses')) {
    $vb = Get-Prop $b $k; $va = Get-Prop $a $k
    if ("$vb" -ne "$va") { Warn "$k differs ($vb vs $va)" }
}
$rb = Get-Prop $b 'cyclesPerMs'; $ra = Get-Prop $a 'cyclesPerMs'
if ($null -ne $rb -and $null -ne $ra -and [double]$rb -gt 0 -and [math]::Abs([double]$ra - [double]$rb) / [double]$rb -gt 0.01) {
    Warn ("cycles-per-ms differs by {0:0.0}% ({1:0} vs {2:0}): cycle-based CPU figures are on different scales; trust processCpuPct / *CoresTimes" -f (([double]$ra - [double]$rb) / [double]$rb * 100), [double]$rb, [double]$ra)
}
Write-Output ''

$wanted = @()
if ($Scenario) { foreach ($s in $Scenario) { $wanted += ($s -split ',') | ForEach-Object { $_.Trim() } } }

$regressions = 0; $improvements = 0
$rows = New-Object System.Collections.Generic.List[object]
foreach ($sa in $a.scenarios) {
    if ($wanted.Count -gt 0 -and $sa.name -notin $wanted) { continue }
    $sb = $b.scenarios | Where-Object { $_.name -eq $sa.name } | Select-Object -First 1
    if ($null -eq $sb) { Write-Output "$($sa.name): only in after"; continue }
    $skipA = Get-Prop $sa 'skipped'; $skipB = Get-Prop $sb 'skipped'
    if ($skipA -or $skipB) { Write-Output ("{0}: skipped ({1})" -f $sa.name, $(if ($skipB) { "before: $skipB" } else { "after: $skipA" })); continue }
    foreach ($p in $sa.metrics.PSObject.Properties) {
        $key = $p.Name
        if (-not $All -and $key -notin $headline) { continue }
        $mb = $sb.metrics.PSObject.Properties[$key]
        if ($null -eq $mb) { continue }
        $va = $p.Value; $vb = $mb.Value
        if ($null -eq $va -or $null -eq $vb) {
            $rows.Add([pscustomobject]@{ scenario = $sa.name; metric = $key; before = $vb; after = $va; delta = $null; pct = $null; flag = 'n/a' })
            continue
        }
        $delta = [double]$va - [double]$vb
        $pct = if ([math]::Abs([double]$vb) -gt 1e-9) { $delta / [math]::Abs([double]$vb) * 100.0 } elseif ([math]::Abs($delta) -gt 1e-9) { [double]::PositiveInfinity } else { 0.0 }
        $dir = Get-Direction $key
        $flag = ''
        $thr = if ($key -match 'cycles') { $CycleThreshold } else { $Threshold }
        if ($dir -ne 0 -and [math]::Abs($pct) -gt $thr -and [math]::Abs($delta) -gt (Get-Floor $key)) {
            $worse = ($dir -lt 0 -and $delta -gt 0) -or ($dir -gt 0 -and $delta -lt 0)
            if ($worse) { $flag = 'REGRESSED'; $regressions++ } else { $flag = 'improved'; $improvements++ }
        }
        $rows.Add([pscustomobject]@{ scenario = $sa.name; metric = $key; before = [math]::Round([double]$vb, 3); after = [math]::Round([double]$va, 3)
                                    delta = [math]::Round($delta, 3); pct = $(if ([double]::IsInfinity($pct)) { 'new' } else { '{0:+0.0;-0.0;0.0}%' -f $pct }); flag = $flag })
    }
}
foreach ($sb in $b.scenarios) {
    if (-not ($a.scenarios | Where-Object { $_.name -eq $sb.name })) { Write-Output "$($sb.name): only in before" }
}

$rows | Format-Table scenario, metric, before, after, delta, pct, flag -AutoSize | Out-String -Width 200 | Write-Output
Write-Output "note: CPU headline = raw cycle counts (*cycles*); processCpuPct / *CoresTimes / paintedCpuMs are time-based (15.6 ms tick-charged, two-mode at low load) and shown as info only, never flagged."
Write-Output ("{0} regressed, {1} improved, {2} warning(s) (threshold {3}%, cycles {4}%)" -f $regressions, $improvements, $script:warnings, $Threshold, $CycleThreshold)
if ($FailOnRegression -and $regressions -gt 0) { exit 1 }
exit 0
