<#
.SYNOPSIS
  CPU + heap attribution for one Wavee session (JIT Release build only: the profiler and the dump analyzer need the
  runtime's diagnostic port, which a NativeAOT publish does not have). Complements perf-tour.ps1, which says WHICH
  frames are slow and HOW MUCH memory is held; this says WHICH FUNCTIONS burn the frame and WHO holds the bytes.

  1. launches Wavee, waits for login, then deep-links a short route list while `dotnet-trace collect --profile dotnet-sampled-thread-time` samples every thread at ~100 Hz
  2. `dotnet-trace convert --format Speedscope` -> thread-scoped inclusive/exclusive time and named waits
  3. `dotnet-dump collect` at the end -> `dumpheap -stat`, the largest byte[] / Element owners via `gcroot`
  4. one markdown report next to the tour's

.EXAMPLE
  powershell -File ops/tools/perf-profile.ps1
  powershell -File ops/tools/perf-profile.ps1 -Routes @("wavee://open?route=pl&arg=spotify%3Aplaylist%3A2pnt79m93NytfAj2lByLlQ")
  powershell -File ops/tools/perf-profile.ps1 -AnalyzeOnly -Dump C:\captures\wavee.dmp -Trace C:\captures\wavee.nettrace

  -AnalyzeOnly never starts or closes Wavee. -Dump, -Trace and -Speedscope may be supplied independently.
  -Windows accepts JSON [{"StartMs":1000,"EndMs":1200}] using trace-relative milliseconds, with no overlaps.
  Without verified navigation windows CPU attribution is explicitly whole-capture, not navigation-scoped.
  Evented exports retain stack durations but do not retain the original sample count.
#>
param(
    [string]$Exe = "C:\wavee\WaveeMusic\src\apps\Wavee\bin\Release\net10.0\Wavee.exe",
    [string]$Out = "",
    [string[]]$Routes = @(
        "wavee://open?route=album&arg=spotify%3Aalbum%3A5Qg2iNuV6zTCGlIYunvvTd",
        "wavee://open?route=pl&arg=spotify%3Aplaylist%3A2pnt79m93NytfAj2lByLlQ",
        "wavee://open?route=artist&arg=spotify%3Aartist%3A4lxfqrEsLX6N1N4OCSkILp",
        "wavee://open?route=liked",
        "wavee://open?route=album&arg=spotify%3Aalbum%3A5Qg2iNuV6zTCGlIYunvvTd",
        "wavee://open?route=pl&arg=spotify%3Aplaylist%3A2pnt79m93NytfAj2lByLlQ",
        "wavee://open?route=artist&arg=spotify%3Aartist%3A4lxfqrEsLX6N1N4OCSkILp"
    ),
    [int]$SettleSeconds = 5,
    [int]$TopN = 40,
    [ValidateRange(1, 100)][int]$GcRootSamples = 3,
    [switch]$AnalyzeOnly,
    [string]$Dump = '',
    [string]$Trace = '',
    [string]$Speedscope = '',
    [string]$Windows = ''
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'perf-profile.helpers.ps1')

$stamp = Get-Date -Format "yyyyMMdd-HHmm"
if ($Out -eq "") { $Out = Join-Path $env:TEMP "wavee-perf-profile-$stamp.md" }
$Out = [IO.Path]::GetFullPath($Out)
$rawDir = $Out + '.raw'
[IO.Directory]::CreateDirectory($rawDir) | Out-Null
if ($AnalyzeOnly) {
    if (-not $Dump -and -not $Trace -and -not $Speedscope) { throw '-AnalyzeOnly requires -Dump, -Trace or -Speedscope.' }
    foreach ($inputPath in @($Dump, $Trace, $Speedscope, $Windows) | Where-Object { $_ }) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Artifact not found: $inputPath" }
    }
} else {
    if ($Dump -or $Trace -or $Speedscope) { throw 'Existing artifacts require -AnalyzeOnly.' }
    if ($Exe -match '\\(native|publish)\\') { throw 'NativeAOT has no managed diagnostic port; use a JIT Release executable.' }
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Wavee.exe not found: $Exe" }
    $Trace = Join-Path $env:TEMP "wavee-profile-$stamp.nettrace"
    $Dump = Join-Path $env:TEMP "wavee-profile-$stamp.dmp"
}
foreach ($tool in @('dotnet-dump', 'dotnet-trace')) {
    if (($tool -eq 'dotnet-dump' -and $Dump) -or ($tool -eq 'dotnet-trace' -and $Trace)) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required tool missing: $tool" }
    }
}

function Invoke-ProfileCommand([string]$Tool, [string[]]$Arguments, [string]$Name) {
    $path = Join-Path $rawDir ($Name + '.txt')
    # Retain complete output, command and exit status. Native stderr is captured by
    # redirection rather than converted into terminating NativeCommandError records.
    $stderr = Join-Path $rawDir ($Name + '.stderr.txt')
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $Tool @Arguments 2> $stderr | Out-String
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    [IO.File]::WriteAllText($path, $output, (New-Object Text.UTF8Encoding($true)))
    @{ Tool = $Tool; Arguments = $Arguments; ExitCode = $code } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $rawDir ($Name + '.command.json')) -Encoding UTF8
    if ($code -ne 0) { throw "$Tool exited $code; raw output: $path; stderr: $stderr" }
    return $output
}

function Invoke-DumpCommand([string]$Command, [string]$Name) {
    Invoke-ProfileCommand 'dotnet-dump' @('analyze', $Dump, '-c', $Command, '-c', 'exit') $Name
}

# Close the app the way a user does (WM_CLOSE), never kill it: a killed Wavee leaves its "running" marker behind and
# the NEXT launch offers an unclean-exit crash report for a session that did not crash.
function Stop-WaveeGracefully([int]$ProcessId = 0) {
    $procs = if ($ProcessId -gt 0) { @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) } else { @(Get-Process Wavee -ErrorAction SilentlyContinue) }
    foreach ($p in $procs) {
        if ($null -eq $p) { continue }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $p.CloseMainWindow() | Out-Null }
        if (-not $p.WaitForExit(15000)) { Write-Host "warn: Wavee pid $($p.Id) did not exit on close; killing it"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

if (-not $AnalyzeOnly) {
Stop-WaveeGracefully
Start-Sleep -Milliseconds 800
$log = Join-Path $env:LOCALAPPDATA ("Wavee\logs\wavee-" + (Get-Date -Format "yyyyMMdd") + ".log")
$startLines = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
Write-Host "launching $Exe"
$app = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 60) {
    Start-Sleep -Milliseconds 500
    if ((Test-Path $log) -and ((Get-Content $log | Select-Object -Skip $startLines) | Where-Object { $_ -match "pid=$($app.Id) .*protocol-installed" })) { break }
}
Start-Sleep -Seconds 8

# ── CPU sampling across the route list ───────────────────────────────────────────────────────────────────────────────
$duration = [TimeSpan]::FromSeconds($Routes.Count * $SettleSeconds + 6)
Write-Host ("sampling CPU for " + $duration.TotalSeconds + "s")
$tracer = Start-Process -FilePath "dotnet-trace" -ArgumentList @("collect", "-p", $app.Id, "--profile", "dotnet-sampled-thread-time", "--duration", $duration.ToString("hh\:mm\:ss"), "-o", "`"$trace`"") -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $rawDir 'trace-collect.txt') -RedirectStandardError (Join-Path $rawDir 'trace-collect.stderr.txt')
Start-Sleep -Seconds 2
$profiled = @()
foreach ($r in $Routes) {
    Write-Host "nav $r"
    $t = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $p = Start-Process -FilePath $Exe -ArgumentList ('"' + $r + '"') -PassThru -WorkingDirectory (Split-Path $Exe)
    $p.WaitForExit(15000) | Out-Null
    Start-Sleep -Seconds $SettleSeconds
    $profiled += [pscustomobject]@{ Route = $r; StartedAt = $t }
}
$tracer.WaitForExit()
if ($tracer.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Trace)) { throw 'Trace collection failed; inspect raw collection output.' }
Write-Host "trace: $trace"

# ── Heap dump ────────────────────────────────────────────────────────────────────────────────────────────────────────
Write-Host "collecting heap dump"
Invoke-ProfileCommand 'dotnet-dump' @('collect', '-p', "$($app.Id)", '-o', $Dump) 'dump-collect' | Out-Null
Stop-WaveeGracefully -ProcessId $app.Id
$profiled | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $rawDir 'routes-wallclock.json') -Encoding UTF8
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# Wavee managed attribution - ' + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
[void]$sb.AppendLine()
[void]$sb.AppendLine('- Analysis scope: managed JIT artifacts; these are not native ARM64 AOT timing or memory measurements.')
[void]$sb.AppendLine("- Trace: $Trace; dump: $Dump; raw commands/output: $rawDir")
[void]$sb.AppendLine('- Root samples establish example ownership paths, not a complete retained-size accounting.')
[void]$sb.AppendLine()

if ($Trace -and -not $Speedscope) {
    $exportBase = Join-Path $rawDir 'cpu'
    Invoke-ProfileCommand 'dotnet-trace' @('convert', $Trace, '--format', 'Speedscope', '-o', $exportBase) 'trace-convert' | Out-Null
    $Speedscope = $exportBase + '.speedscope.json'
    if (-not (Test-Path -LiteralPath $Speedscope)) { throw "Trace converter did not produce $Speedscope; inspect trace-convert.txt." }
}
if ($Speedscope) {
    $windowsData = if ($Windows) { @(Get-Content -LiteralPath $Windows -Raw -Encoding UTF8 | ConvertFrom-Json) } else { @() }
    $threads = @(Get-SpeedscopeThreadReport (Get-Content -LiteralPath $Speedscope -Raw -Encoding UTF8 | ConvertFrom-Json) $windowsData)
    [void]$sb.AppendLine('## Thread-scoped sampled thread time')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- UI identity requires an AppHost.RunFrame stack; render identity requires RenderThread.Loop. Names below preserve exporter thread IDs.')
    [void]$sb.AppendLine('- Wait classification uses named wait stacks; remaining time is not proof of on-CPU execution. Inclusive times overlap and must not be summed.')
    [void]$sb.AppendLine('- Exporter categories CPU_TIME and UNMANAGED_CODE_TIME are retained separately, including intervals classified as named waits. Category totals partition observed thread time; they do not add another denominator.')
    [void]$sb.AppendLine('- Function rankings omit exporter scaffolding and unresolved ?!? markers. Exclusive attribution uses the deepest resolved method beneath those markers. Under UNMANAGED_CODE_TIME this may be a managed ancestor; it does not resolve which native function performed the work.')
    if ($Windows) { [void]$sb.AppendLine("- Scope: explicit trace-relative navigation windows in $Windows.") }
    else { [void]$sb.AppendLine('- Scope: whole capture. Navigation-window attribution INCOMPLETE: the earlier capture did not retain trace-relative navigation anchors.') }
    [void]$sb.AppendLine('| Thread profile | role | observed ms | non-wait ms | named wait ms | count | count meaning |')
    [void]$sb.AppendLine('|---|---|---:|---:|---:|---:|---|')
    foreach ($thread in $threads) {
        $role = if ($thread.IsUi) { 'UI' } elseif ($thread.IsRender) { 'render' } else { 'other' }
        [void]$sb.AppendLine(('| {0} | {1} | {2:F2} | {3:F2} | {4:F2} | {5} | {6} |' -f $thread.Name, $role, $thread.ObservedMs, $thread.WorkMs, $thread.WaitMs, $thread.Intervals, $thread.CountKind))
    }
    if (@($threads | Where-Object IsUi).Count -ne 1) { [void]$sb.AppendLine('UI attribution INCOMPLETE: expected one uniquely identified UI thread; inspect trace/profile identities.') }
    foreach ($thread in ($threads | Where-Object { $_.IsUi -or $_.IsRender })) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("### $($thread.Name): Exporter categories")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| observed ms (including waits) | % of observed thread time | category |')
        [void]$sb.AppendLine('|---:|---:|---|')
        foreach ($entry in ($thread.Categories.GetEnumerator() | Sort-Object Value -Descending)) {
            $percent = if ($thread.ObservedMs -gt 0) { 100 * $entry.Value / $thread.ObservedMs } else { 0 }
            [void]$sb.AppendLine(('| {0:F3} | {1:F2}% | {2} |' -f $entry.Value, $percent, $entry.Key))
        }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine(('- No resolved method: non-wait {0:F3} ms; named wait {1:F3} ms. These durations remain in observed/category totals and are excluded from function rankings.' -f $thread.UnattributedWorkMs, $thread.UnattributedWaitMs))
        foreach ($mode in @('Inclusive', 'Exclusive', 'Waits')) {
            [void]$sb.AppendLine()
            [void]$sb.AppendLine("### $($thread.Name): $mode")
            [void]$sb.AppendLine()
            [void]$sb.AppendLine('| observed ms | % of observed thread time (including waits) | function |')
            [void]$sb.AppendLine('|---:|---:|---|')
            foreach ($entry in ($thread.$mode.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $TopN)) {
                $percent = if ($thread.ObservedMs -gt 0) { 100 * $entry.Value / $thread.ObservedMs } else { 0 }
                [void]$sb.AppendLine(('| {0:F3} | {1:F2}% | {2} |' -f $entry.Value, $percent, ($entry.Key -replace '\|', '\|')))
            }
        }
    }
} else { [void]$sb.AppendLine('CPU attribution INCOMPLETE: no trace or Speedscope artifact supplied.') }

function Add-RootReport([object]$Object, [string]$Type, [string]$Label) {
    $address = $Object.Address
    $details = Invoke-DumpCommand "dumpobj $address" "$Label-$address-object"
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("### $Type $address ($($Object.Bytes) bytes)")
    [void]$sb.AppendLine()
    if (-not (Test-DumpObject $details $Object $Type)) {
        [void]$sb.AppendLine("Ownership UNRESOLVED: dumpobj did not verify expected type, method table and size. Inspect $Label-$address-object.txt.")
        return
    }
    $roots = Invoke-DumpCommand "gcroot $address" "$Label-$address-root"
    if (-not (Test-GcRootTarget $roots $address)) {
        [void]$sb.AppendLine("Ownership UNRESOLVED: no completed positive root chain reaches the requested address. Full output: $Label-$address-root.txt.")
        return
    }
    [void]$sb.AppendLine("Verified dumpobj type/MT/size and root chain target. Full output: $Label-$address-root.txt.")
    [void]$sb.AppendLine('Root-chain tail (a shared root prefix alone cannot identify the buffer owner):')
    [void]$sb.AppendLine('```')
    # Keep the target and its nearest owners, rather than discarding the end of a long chain.
    $rootLines = @($roots -split '\r?\n')
    $targetPattern = '^\s*->\s*0*' + [regex]::Escape($address.TrimStart('0')) + '\s+'
    for ($i = 0; $i -lt $rootLines.Count; $i++) {
        if ($rootLines[$i] -match $targetPattern) {
            $first = [Math]::Max(0, $i - 12)
            foreach ($line in $rootLines[$first..$i]) { [void]$sb.AppendLine($line) }
            break
        }
    }
    [void]$sb.AppendLine('```')
}

if ($Dump) {
    if (-not (Test-Path -LiteralPath $Dump -PathType Leaf)) { throw "Dump missing: $Dump" }
    $stat = Invoke-DumpCommand 'dumpheap -stat' 'heap-stat'
    $rows = @(ConvertFrom-DumpHeapStatistics ($stat -split '\r?\n'))
    if ($rows.Count -eq 0) { throw 'No heap statistics parsed; inspect heap-stat.txt.' }
    $objects = @($rows | Where-Object { -not $_.IsFree })
    $objectBytes = ($objects | Measure-Object Bytes -Sum).Sum
    $freeBytes = ($rows | Where-Object IsFree | Measure-Object Bytes -Sum).Sum
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Managed heap objects by type')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine(('Non-Free objects: {0:F2} MiB; Free fragmentation: {1:F2} MiB (excluded). Reachability of every non-Free object is not established by dumpheap.' -f ($objectBytes / 1MB), ($freeBytes / 1MB)))
    [void]$sb.AppendLine('| MiB | objects | type |')
    [void]$sb.AppendLine('|---:|---:|---|')
    foreach ($row in ($objects | Sort-Object Bytes -Descending | Select-Object -First $TopN)) {
        [void]$sb.AppendLine(('| {0:F2} | {1} | {2} |' -f ($row.Bytes / 1MB), $row.Count, $row.Type))
    }
    foreach ($spec in @(@{ Type = 'System.Byte[]'; Label = 'bytes'; Min = ' -min 1000000' }, @{ Type = 'FluentGpu.Dsl.BoxEl'; Label = 'boxes'; Min = '' })) {
        # Exact MT lookup prevents -type substring matching from selecting other types.
        $typeRows = @($objects | Where-Object Type -eq $spec.Type)
        if ($typeRows.Count -eq 0) { [void]$sb.AppendLine("No $($spec.Type) objects in the dump histogram."); continue }
        $instances = @()
        foreach ($typeRow in $typeRows) {
            $listing = Invoke-DumpCommand ("dumpheap -mt " + $typeRow.MethodTable + $spec.Min) ($spec.Label + '-' + $typeRow.MethodTable + '-heap')
            $instances += @(ConvertFrom-DumpHeapRows ($listing -split '\r?\n'))
        }
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("## $($spec.Type): $($instances.Count) candidate objects$($spec.Min)")
        if ($instances.Count -eq 0) { [void]$sb.AppendLine('Ownership INCOMPLETE: no eligible object rows parsed. Inspect raw heap listing.'); continue }
        $samples = @(Select-HeapRootSamples $instances $GcRootSamples 2)
        foreach ($sample in $samples) { Add-RootReport $sample $spec.Type $spec.Label }
    }
} else { [void]$sb.AppendLine('Heap attribution INCOMPLETE: no dump supplied.') }

[IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object Text.UTF8Encoding($true)))
Write-Host "wrote $Out"
