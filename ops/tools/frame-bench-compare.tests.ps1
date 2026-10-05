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
                'exitPerSec.Idle' = $presents; 'processCpuPct' = $null } },
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

    # Painted CPU +50 % (lower is better) and presents/s -20 % (higher is better) regress; gc0/s 0.5 -> 0.55 is under its floor.
    $worse = Write-Fixture 'worse' 3000000 1.50 94 0.55
    $r = Invoke-Compare @($base, $worse, '-FailOnRegression')
    Assert-True ($r.Code -eq 1) "a regression exits 1 with -FailOnRegression (got $($r.Code))"
    Assert-True ($r.Text -match 'paintedCpuMs\.p50.*REGRESSED') 'higher painted CPU regresses'
    Assert-True ($r.Text -match 'presentsPerSec.*REGRESSED') 'fewer presents per second regresses'
    Assert-True ($r.Text -notmatch 'gc0PerSec.*REGRESSED') 'a change under the absolute floor is not flagged'
    Assert-True ($r.Text -match '2 regressed') 'exactly two regressions'

    # The reverse direction improves; neutral metrics (framesPerSec, the exit census) never flag.
    $r = Invoke-Compare @($worse, $base, '-All')
    Assert-True ($r.Code -eq 0) 'no -FailOnRegression: exit 0'
    Assert-True ($r.Text -match 'paintedCpuMs\.p50.*improved') 'lower painted CPU improves'
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

    Write-Output 'frame-bench-compare: all tests passed'
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
