# frame-bench-compare.ps1 against fixture summaries, run the way a user runs it (powershell -File, a fresh process):
#   powershell -NoProfile -File ops\tools\frame-bench-compare.tests.ps1
# Throws on the first failed assertion; prints "frame-bench-compare: all tests passed" otherwise.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'frame-bench-compare.ps1'
$dir = Join-Path ([IO.Path]::GetTempPath()) ('frame-bench-compare-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null

function Assert-True([bool]$Condition, [string]$Because) { if (-not $Condition) { throw "FAILED: $Because" } }

function Write-Fixture([string]$name, [double]$rate, [double]$uiP50, [double]$presents, [double]$gc0, [string]$measure = '10', [string]$skipped = '') {
    $scen = if ($skipped) { @{ name = 'lyrics-line'; skipped = $skipped; metrics = @{} } } else { @{ name = 'lyrics-line'; metrics = @{ 'paintedCpuMs.p50' = 1.0 } } }
    $doc = [ordered]@{
        schema = 'wavee-frame-bench/2'; version = 't'; label = $name; data = 'fake'; utc = '2026-10-06T00:00:00Z'
        processors = 12; refreshHz = 120; windowPx = '1770x1140'; measureSec = [int]$measure; warmupSec = 2; gpuPasses = $false; cyclesPerMs = $rate
        scenarios = @(
            @{ name = 'home-scroll'; metrics = [ordered]@{
                'presentsPerSec' = $presents; 'framesPerSec' = 50.0 + $presents; 'paintedCpuMs.p50' = $uiP50; 'gc0PerSec' = $gc0
                'exitPerSec.Idle' = $presents; 'processCpuPct' = $null; 'uiCoresTimes' = $uiP50
                'processGcyclesPerSec' = 0.4 * $uiP50; 'uiMcyclesPerSec' = 100.0 * $uiP50 } },
            $scen
        )
    }
    $path = Join-Path $dir "$name.json"
    $doc | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Invoke-Compare([string[]]$arguments) {
    $out = & powershell -NoProfile -File $script @arguments 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $out }
}

try {
    $base = Write-Fixture 'base' 3000000 1.00 118 0.5
    $same = Write-Fixture 'same' 3000000 1.00 118 0.5
    $r = Invoke-Compare @($base, $same, '-FailOnRegression')
    Assert-True ($r.Code -eq 0) "identical summaries exit 0 (got $($r.Code)): $($r.Text)"
    Assert-True ($r.Text -match '0 regressed, 0 improved, 0 warning') 'identical summaries flag nothing'

    # Cycle CPU +50 % (lower is better; the time-based paintedCpuMs / uiCoresTimes move too but are info only) and presents/s -20 % (higher is better) regress; gc0/s 0.5 -> 0.55 is under its floor.
    $worse = Write-Fixture 'worse' 3000000 1.50 94 0.55
    $r = Invoke-Compare @($base, $worse, '-FailOnRegression')
    Assert-True ($r.Code -eq 1) "a regression exits 1 with -FailOnRegression (got $($r.Code))"
    Assert-True ($r.Text -match 'processGcyclesPerSec.*REGRESSED') 'more process cycles regress'
    Assert-True ($r.Text -match 'uiMcyclesPerSec.*REGRESSED') 'more UI cycles regress'
    Assert-True ($r.Text -notmatch 'paintedCpuMs\.p50.*REGRESSED') 'time-based painted CPU is info, never flagged'
    Assert-True ($r.Text -notmatch 'uiCoresTimes.*REGRESSED') 'uiCoresTimes is info, never flagged'
    Assert-True ($r.Text -match 'note: CPU headline = raw cycle') 'the cycles-not-time note is printed'
    Assert-True ($r.Text -match 'presentsPerSec.*REGRESSED') 'fewer presents per second regresses'
    Assert-True ($r.Text -notmatch 'gc0PerSec.*REGRESSED') 'a change under the absolute floor is not flagged'
    Assert-True ($r.Text -match '3 regressed') 'exactly three regressions'

    # The reverse direction improves; neutral metrics (framesPerSec, the exit census) never flag.
    $r = Invoke-Compare @($worse, $base, '-All')
    Assert-True ($r.Code -eq 0) 'no -FailOnRegression: exit 0'
    Assert-True ($r.Text -match 'processGcyclesPerSec.*improved') 'fewer process cycles improve'
    Assert-True ($r.Text -notmatch 'framesPerSec.*(REGRESSED|improved)') 'framesPerSec is neutral'
    Assert-True ($r.Text -notmatch 'exitPerSec\.Idle.*(REGRESSED|improved)') 'the exit census is neutral'
    Assert-True ($r.Text -match 'processCpuPct.*n/a') 'a null metric is reported, not compared'

    # Unlike runs warn: a cycle rate more than 1 % apart, a different measure window, a skipped scenario on one side.
    $other = Write-Fixture 'other' 3100000 1.00 118 0.5 '15' 'no line-synced lyrics'
    $r = Invoke-Compare @($base, $other)
    Assert-True ($r.Text -match 'WARNING: cycles-per-ms differs') 'a rate more than 1 % apart warns'
    Assert-True ($r.Text -match 'WARNING: measureSec differs') 'a different measure window warns'
    Assert-True ($r.Text -match 'lyrics-line: skipped') 'a skipped scenario is reported, not compared'
    Assert-True ($r.Text -match '2 warning') 'two warnings'

    # Cycle threshold: +3 % is noise, +7 % flags (default 5 %).
    $n3 = Write-Fixture 'n3' 3000000 1.03 118 0.5
    $n7 = Write-Fixture 'n7' 3000000 1.07 118 0.5
    $big = Write-Fixture 'big' 3000000 100.0 118 0.5
    $big7 = Write-Fixture 'big7' 3000000 107.0 118 0.5
    Assert-True ((Invoke-Compare @($big, (Write-Fixture 'big3' 3000000 103.0 118 0.5))).Text -match 'processGcyclesPerSec.*REGRESSED' -eq $false) '+3 % cycles is under the 5 % threshold'
    Assert-True ((Invoke-Compare @($big, $big7)).Text -match 'processGcyclesPerSec.*REGRESSED') '+7 % cycles flags'

    Write-Output 'frame-bench-compare: all tests passed'
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
