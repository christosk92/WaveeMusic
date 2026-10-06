# hidden-mem.ps1 -Summarize against a synthetic artifacts folder, run the way a user runs it (powershell -File, a fresh process):
#   powershell -NoProfile -File ops\tools\hidden-mem.tests.ps1
# Throws on the first failed assertion; prints "hidden-mem: all tests passed" otherwise.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'hidden-mem.ps1'
$dir = Join-Path ([IO.Path]::GetTempPath()) ('hidden-mem-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null

function Assert-True([bool]$Condition, [string]$Because) { if (-not $Condition) { throw "FAILED: $Because" } }

function Write-Run([string]$name, [double]$hidPriv, [double]$restoreMs, [int]$presentBad = 0) {
    $d = Join-Path $dir $name
    New-Item -ItemType Directory -Path $d | Out-Null
    $lines = @('mode,cycle,phase,privateMB,workingSetMB,vramLocalMB,trackedGpuMB,imageCacheMB,glyphAtlasMB,restoreMs')
    foreach ($c in 1..3) {
        $lines += "minimize,$c,vis,400.0,450.0,300.0,200.0,60.0,16.0,"
        $lines += "minimize,$c,hid5,$($hidPriv + 20),430.0,120.0,60.0,20.0,16.0,"
        $lines += "minimize,$c,hidEnd,$hidPriv,420.0,100.0,40.0,20.0,16.0,"
        $lines += "minimize,$c,res,400.0,450.0,300.0,200.0,60.0,16.0,$($restoreMs + $c)"
        $lines += "cover,$c,vis,400.0,450.0,300.0,200.0,60.0,16.0,"
        $lines += "cover,$c,res,400.0,450.0,300.0,200.0,60.0,16.0,no-present"
    }
    Set-Content -LiteralPath (Join-Path $d 'hide-restore-cycles.csv') -Value $lines -Encoding ascii
    $doc = @{ schema = 'wavee-frame-bench/2'; scenarios = @(@{ name = 'hide-restore'; metrics = @{
        presentChecked = 120; presentBad = $presentBad; damageValidated = 90; damageBad = 0; tileBad = 0; cycles = 6 } }) }
    $doc | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $d 'frame-bench-summary.json') -Encoding utf8
    return $d
}

function Invoke-Hm([string[]]$arguments) {
    $out = & powershell -NoProfile -File $script @arguments 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $out }
}

try {
    $after = Write-Run 'after' 150.0 40.0
    $before = Write-Run 'before' 380.0 35.0
    $r = Invoke-Hm @('-Summarize', $after)
    Assert-True ($r.Code -eq 0) "a clean run exits 0 (got $($r.Code)): $($r.Text)"
    Assert-True ($r.Text -match 'minimize\s+hidEnd\s+150\.0') 'the hidden-end private MB median is printed'
    Assert-True ($r.Text -match 'minimize\s+restore ms: p50 42\.0') 'the restore p50 is the median of 41, 42, 43'
    Assert-True ($r.Text -match 'cover\s+restore ms: p50 -\s+p95 -\s+max -\s+noPresent 3/3') 'a mode whose restores presented nothing reports noPresent, not a latency'
    Assert-True ($r.Text -match 'presentBad=0') 'the validation counters are printed'

    $r = Invoke-Hm @('-Summarize', $after, '-Baseline', $before)
    Assert-True ($r.Code -eq 0) 'a baseline compare exits 0'
    Assert-True ($r.Text -match 'delta priv -230\.0') 'the private MB delta against the baseline is hidEnd 150 vs 380'

    $bad = Write-Run 'bad' 150.0 40.0 3
    $r = Invoke-Hm @('-Summarize', $bad)
    Assert-True ($r.Code -eq 2) "a non-zero validation counter exits 2 (got $($r.Code))"
    Write-Output 'hidden-mem: all tests passed'
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
