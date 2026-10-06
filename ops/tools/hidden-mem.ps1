<#
.SYNOPSIS
  What a minimized / tray-hidden / covered Wavee holds in memory, and what the way back costs: runs the `hide-restore`
  frame-bench scenario and prints one table.

.DESCRIPTION
  Runs `Wavee.exe --fake --profile <scratch> --frame-bench=hide-restore ...` (docs/guide/hidden-memory.md), then reads the
  `hide-restore-cycles.csv` and `frame-bench-summary.json` it wrote. The scenario cycles three modes - MINIMIZE, HIDE (the tray)
  and COVER (a topmost window over the app for 3 s: the alt-tab-away case, which must release nothing) - and samples each cycle
  in four phases: vis (settled and visible), hid5 (hidden 5 s: the release has run), hidEnd (hidden -HiddenSec) and res (restored
  and settled). The table has one row per mode and phase with the MEDIAN over cycles of process private MB, working set MB, DXGI
  LOCAL usage MB, the engine's tracked GPU MB and the image cache MB, then the restore latency (restore call to the first present
  after it; `noPresent` counts restores whose frame was legitimately elided as unchanged and presented nothing) and the validation
  counters (`-Validate` adds `--fg present-validate,damage-validate`; presentBad, damageBad and tileBad must be 0).

  With -Baseline (a directory holding the same two files from another run, e.g. the same build with `--fg hidden=max:max`, which
  disables the release) it prints the delta of every row. Never touches a running Wavee: the run uses its own scratch profile
  and a window it starts itself, and `-Summarize <dir>` only reads files.

.PARAMETER Exe
  Wavee.exe to run (Release or a NativeAOT publish).

.PARAMETER Profile
  Scratch profile directory (created if missing). Never the owner's profile.

.PARAMETER OutDir
  Where the run writes its artifacts (default: a fresh folder under the temp directory).

.PARAMETER Cycles
  Cycles per mode (default 3; 10 or more for a p95).

.PARAMETER HiddenSec
  Seconds hidden per minimize / hide cycle (default 30).

.PARAMETER Validate
  Add `--fg present-validate,damage-validate` (a debug-grade arm: it slows frames, so do not read latency from that run).

.PARAMETER Fg
  Extra `--fg` switches, comma-separated (e.g. `hidden=max:max`).

.PARAMETER Baseline
  A directory with a previous run's artifacts: print deltas against it.

.PARAMETER Summarize
  Do not run anything: read this artifacts directory.

.EXAMPLE
  powershell -File ops\tools\hidden-mem.ps1 -Exe C:\build\Wavee.exe -Profile C:\scratch\hm -OutDir C:\scratch\hm-out -Cycles 10
.EXAMPLE
  powershell -File ops\tools\hidden-mem.ps1 -Summarize C:\scratch\hm-out -Baseline C:\scratch\hm-off
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Profile,
    [string]$OutDir,
    [int]$Cycles = 3,
    [int]$HiddenSec = 30,
    [switch]$Validate,
    [string]$Fg = '',
    [string]$Baseline,
    [string]$Summarize
)
$ErrorActionPreference = 'Stop'

function Get-Median([double[]]$values) {
    $v = @($values | Where-Object { -not [double]::IsNaN($_) } | Sort-Object)
    if ($v.Count -eq 0) { return [double]::NaN }
    return $v[[int][Math]::Floor($v.Count / 2)]
}

function ConvertTo-Number([string]$s) {
    $d = 0.0
    if ([double]::TryParse($s, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return [double]::NaN
}

# rows: mode, phase -> median of each memory column over the cycles
function Read-Cycles([string]$dir) {
    $csv = Join-Path $dir 'hide-restore-cycles.csv'
    if (-not (Test-Path -LiteralPath $csv)) { throw "no hide-restore-cycles.csv in $dir (did the run reach the hide-restore scenario?)" }
    $rows = Import-Csv -LiteralPath $csv
    $out = New-Object System.Collections.Generic.List[object]
    $latency = @{}
    foreach ($mode in @($rows | ForEach-Object { $_.mode } | Select-Object -Unique)) {
        foreach ($phase in @('vis', 'hid5', 'hidEnd', 'res')) {
            $sel = @($rows | Where-Object { $_.mode -eq $mode -and $_.phase -eq $phase })
            if ($sel.Count -eq 0) { continue }
            $out.Add([pscustomobject][ordered]@{
                Mode = $mode; Phase = $phase; Cycles = $sel.Count
                PrivateMB = Get-Median ($sel | ForEach-Object { ConvertTo-Number $_.privateMB })
                WorkingSetMB = Get-Median ($sel | ForEach-Object { ConvertTo-Number $_.workingSetMB })
                VramMB = Get-Median ($sel | ForEach-Object { ConvertTo-Number $_.vramLocalMB })
                TrackedMB = Get-Median ($sel | ForEach-Object { ConvertTo-Number $_.trackedGpuMB })
                ImageCacheMB = Get-Median ($sel | ForEach-Object { ConvertTo-Number $_.imageCacheMB })
            })
        }
        $res = @($rows | Where-Object { $_.mode -eq $mode -and $_.phase -eq 'res' })
        $ms = @($res | Where-Object { $_.restoreMs -ne 'no-present' -and $_.restoreMs -ne '' } | ForEach-Object { ConvertTo-Number $_.restoreMs } | Sort-Object)
        $latency[$mode] = [pscustomobject]@{
            P50 = if ($ms.Count) { $ms[[int][Math]::Floor($ms.Count / 2)] } else { [double]::NaN }
            P95 = if ($ms.Count) { $ms[[int][Math]::Min($ms.Count - 1, [Math]::Ceiling($ms.Count * 0.95) - 1)] } else { [double]::NaN }
            Max = if ($ms.Count) { $ms[$ms.Count - 1] } else { [double]::NaN }
            NoPresent = $res.Count - $ms.Count
            Cycles = $res.Count
        }
    }
    return [pscustomobject]@{ Rows = $out; Latency = $latency }
}

function Read-Validation([string]$dir) {
    $json = Join-Path $dir 'frame-bench-summary.json'
    if (-not (Test-Path -LiteralPath $json)) { return @{} }
    $doc = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    $scen = @($doc.scenarios | Where-Object { $_.name -eq 'hide-restore' })
    $m = @{}
    if ($scen.Count -and $scen[0].metrics) {
        foreach ($k in @('presentChecked', 'presentBad', 'damageValidated', 'damageBad', 'tileBad', 'cycles')) {
            $p = $scen[0].metrics.PSObject.Properties[$k]
            if ($p) { $m[$k] = $p.Value }
        }
    }
    return $m
}

function Format-Num($v) { if ($null -eq $v -or ([double]::IsNaN([double]$v))) { return '-' } return ('{0:0.0}' -f [double]$v) }

if (-not $Summarize) {
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw '-Exe must name an existing Wavee.exe' }
    if (-not $Profile) { throw '-Profile must name a scratch profile directory (never the default profile)' }
    if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) ('hidden-mem-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    New-Item -ItemType Directory -Force -Path $Profile, $OutDir | Out-Null
    $fg = @()
    if ($Validate) { $fg += 'present-validate'; $fg += 'damage-validate' }
    if ($Fg) { $fg += ($Fg -split ',' | Where-Object { $_ }) }
    $argv = @('--fake', '--profile', $Profile, '--frame-bench=hide-restore', '--bench-hide-cycles', $Cycles, '--bench-hidden-sec', $HiddenSec,
              '--bench-warmup-sec', 2, '--probe-out', $OutDir)
    if ($fg.Count) { $argv += @('--fg', ($fg -join ',')) }
    Write-Output ("running: {0} {1}" -f $Exe, ($argv -join ' '))
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -PassThru -Wait -NoNewWindow
    if ($p.ExitCode -ne 0) { Write-Warning "Wavee exited with code $($p.ExitCode)" }
    $Summarize = $OutDir
}

$cur = Read-Cycles $Summarize
$base = if ($Baseline) { Read-Cycles $Baseline } else { $null }
$val = Read-Validation $Summarize

Write-Output ''
Write-Output ('=== hidden memory: {0} ===' -f $Summarize)
$fmt = '{0,-9} {1,-7} {2,7} {3,9} {4,9} {5,9} {6,9}'
Write-Output ($fmt -f 'mode', 'phase', 'privMB', 'wsMB', 'vramMB', 'trackMB', 'imgMB')
foreach ($r in $cur.Rows) {
    $line = $fmt -f $r.Mode, $r.Phase, (Format-Num $r.PrivateMB), (Format-Num $r.WorkingSetMB), (Format-Num $r.VramMB), (Format-Num $r.TrackedMB), (Format-Num $r.ImageCacheMB)
    if ($base) {
        $b = $base.Rows | Where-Object { $_.Mode -eq $r.Mode -and $_.Phase -eq $r.Phase } | Select-Object -First 1
        if ($b) { $line += ('   delta priv {0:+0.0;-0.0;0.0} ws {1:+0.0;-0.0;0.0} vram {2:+0.0;-0.0;0.0} track {3:+0.0;-0.0;0.0}' -f
            ($r.PrivateMB - $b.PrivateMB), ($r.WorkingSetMB - $b.WorkingSetMB), ($r.VramMB - $b.VramMB), ($r.TrackedMB - $b.TrackedMB)) }
    }
    Write-Output $line
}
Write-Output ''
foreach ($mode in $cur.Latency.Keys | Sort-Object) {
    $l = $cur.Latency[$mode]
    $line = '{0,-9} restore ms: p50 {1}  p95 {2}  max {3}   noPresent {4}/{5}' -f $mode, (Format-Num $l.P50), (Format-Num $l.P95), (Format-Num $l.Max), $l.NoPresent, $l.Cycles
    if ($base -and $base.Latency.ContainsKey($mode)) { $line += ('   (baseline p50 {0} max {1})' -f (Format-Num $base.Latency[$mode].P50), (Format-Num $base.Latency[$mode].Max)) }
    Write-Output $line
}
if ($val.Count) {
    Write-Output ''
    Write-Output ('validation: ' + (($val.Keys | Sort-Object | ForEach-Object { '{0}={1}' -f $_, $val[$_] }) -join ' '))
    $bad = 0
    foreach ($k in @('presentBad', 'damageBad', 'tileBad')) { if ($val.ContainsKey($k) -and [double]$val[$k] -ne 0) { $bad++ } }
    if ($bad) { Write-Warning 'a validation counter is not 0'; exit 2 }
}
