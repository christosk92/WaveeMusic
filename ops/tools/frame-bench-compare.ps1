<#
.SYNOPSIS
  Diff two `--frame-bench` summaries (frame-bench-summary.json) and flag regressions.

.DESCRIPTION
  Reads two summaries written by `Wavee.exe ... --frame-bench --probe-out <dir>` (docs/guide/frame-bench.md), matches
  scenarios by name and metrics by key, and prints one row per metric: before, after, delta, % change, and a flag:
    REGRESSED  worse by more than -Threshold percent AND by more than the metric's absolute floor
    improved   better by the same margins
  Direction per metric mirrors Diagnostics.FrameBenchMath.Better (Screens/Diagnostics.FrameBench.cs): presents/frames
  per second and the audio padding minimum are higher-is-better; wallSec/frames/paintedFrames/presents/gpuFrames are
  neutral (they describe the run); everything else (CPU, GPU, allocations, GC, memory, missed vsyncs, xruns) is
  lower-is-better.
  Absolute floors keep timer noise out of the flags: 0.05 for a metric in ms or cores, 1 MB for MB, 1 % for a
  percentage, 64 bytes for an allocation rate, 1 for a count.

  A scenario skipped on either side, or a metric null (NaN) on either side, is reported, never compared.

.PARAMETER Before
  The baseline summary (a file, or a directory holding frame-bench-summary.json).

.PARAMETER After
  The candidate summary (same forms).

.PARAMETER Threshold
  Percent change that counts (default 10).

.PARAMETER All
  Print every metric. Default: the headline set (presents/s, UI/render/other CPU, process CPU, GPU, allocations, GC,
  working set, VRAM, missed vsyncs, audio).

.PARAMETER Scenario
  Only these scenarios (names, comma-separated or an array).

.PARAMETER FailOnRegression
  Exit 1 when anything is flagged REGRESSED.

.EXAMPLE
  pwsh ops/tools/frame-bench-compare.ps1 -Before bench\main -After bench\lane-a
.EXAMPLE
  powershell -File ops\tools\frame-bench-compare.ps1 bench\cold\frame-bench-summary.json bench\warm -Threshold 5 -All
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Before,
    [Parameter(Mandatory = $true, Position = 1)][string]$After,
    [double]$Threshold = 10,
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
    if ($json.schema -ne 'wavee-frame-bench/1') { throw "$path is not a wavee-frame-bench/1 summary (schema '$($json.schema)')" }
    return $json
}

# Mirrors FrameBenchMath.Better: +1 higher is better, 0 neutral, -1 lower is better.
function Get-Direction([string]$key) {
    if ($key -in @('presentsPerSec', 'framesPerSec', 'audioPaddingMinMs')) { return 1 }
    if ($key -in @('wallSec', 'frames', 'paintedFrames', 'presents', 'gpuFrames') -or $key -like '*.off' -or $key -like '*.on') { return 0 }
    return -1
}

function Get-Floor([string]$key) {
    if ($key -match 'MB(\.|$)') { return 1.0 }
    if ($key -match 'Pct') { return 1.0 }
    if ($key -match 'Bytes') { return 64.0 }
    if ($key -match '(Ms|Cores|Us)(\.|$)' -or $key -match 'Cores$') { return 0.05 }
    return 1.0
}

$headline = @(
    'presentsPerSec', 'presentIntervalMs.p99',
    'uiCpuMs.p50', 'uiCpuMs.p99', 'renderCpuMs.p50', 'renderCpuMs.p99', 'otherCpuMs.p50',
    'uiCores', 'renderCores', 'otherCores', 'processCpuPct',
    'gpuMs.p50', 'gpuMs.p99', 'gpuBusyPct',
    'uiAllocBytesPerFrame', 'processAllocBytesPerSec', 'gc0', 'gc2', 'gcPauseMs',
    'workingSetMB.avg', 'privateMB.avg', 'vramLocalMB.avg', 'missedVsyncs', 'audioDeviceDryEdges', 'audioXruns',
    'overheadUiCpuUsPerFrame', 'overheadRunFrameWallUs', 'overheadProcessCpuPct'
)

$b = Read-Summary $Before
$a = Read-Summary $After
Write-Output ("before: {0} {1} {2} {3}" -f $b.version, $b.data, $b.label, $b.utc)
Write-Output ("after:  {0} {1} {2} {3}" -f $a.version, $a.data, $a.label, $a.utc)
if ($b.data -ne $a.data) { Write-Output "WARNING: comparing $($b.data) data against $($a.data) data" }
if ($b.windowPx -ne $a.windowPx -or $b.refreshHz -ne $a.refreshHz) { Write-Output "WARNING: window/panel differ ($($b.windowPx)@$($b.refreshHz)Hz vs $($a.windowPx)@$($a.refreshHz)Hz)" }
Write-Output ''

$wanted = @()
if ($Scenario) { foreach ($s in $Scenario) { $wanted += ($s -split ',') | ForEach-Object { $_.Trim() } } }

$regressions = 0; $improvements = 0
$rows = New-Object System.Collections.Generic.List[object]
foreach ($sa in $a.scenarios) {
    if ($wanted.Count -gt 0 -and $sa.name -notin $wanted) { continue }
    $sb = $b.scenarios | Where-Object { $_.name -eq $sa.name } | Select-Object -First 1
    if ($null -eq $sb) { Write-Output "$($sa.name): only in after"; continue }
    $skipA = $sa.PSObject.Properties['skipped']; $skipB = $sb.PSObject.Properties['skipped']
    if ($skipA -or $skipB) { Write-Output ("{0}: skipped ({1})" -f $sa.name, $(if ($skipB) { "before: $($skipB.Value)" } else { "after: $($skipA.Value)" })); continue }
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
        if ($dir -ne 0 -and [math]::Abs($pct) -gt $Threshold -and [math]::Abs($delta) -gt (Get-Floor $key)) {
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
Write-Output ("{0} regressed, {1} improved (threshold {2}%)" -f $regressions, $improvements, $Threshold)
if ($FailOnRegression -and $regressions -gt 0) { exit 1 }
