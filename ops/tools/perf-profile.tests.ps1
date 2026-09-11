# Run: powershell -NoProfile -File ops/tools/perf-profile.tests.ps1
# Fixture-driven executable checks, independent of Pester version and live processes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'perf-profile.helpers.ps1')
function Assert-Profile($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$fixture = @'
Loading core dump: fixture.dmp ...
         Address               MT           Size
    01d538882bb8     7ffd693150a8     16,777,240
    01d53c240240     7ffd693150a8      2,097,176
    01d53c440240     7ffd693150a8      2,097,176
    01d53c640240     7ffd693150a8      2,097,176
    01d53c840240     7ffd693150a8      2,097,200
Statistics:
          MT Count  TotalSize Class Name
7ffd693150a8    5 25,165,968 System.Byte[]
0000000000000000 2 1,048,576 Free
Total 7 objects, 26,214,544 bytes
garbage 1234 18
'@
$rows = @(ConvertFrom-DumpHeapRows ($fixture -split '\r?\n'))
Assert-Profile ($rows.Count -eq 5) 'Object row parser accepted a header/summary or lost rows.'
Assert-Profile ($rows[0].Address -eq '01d538882bb8' -and $rows[0].MethodTable -eq '7ffd693150a8' -and $rows[0].Bytes -eq 16777240) 'Address/MT/size columns are incorrect.'
Assert-Profile (@(ConvertFrom-DumpHeapRows @('', 'Total 0 objects')).Count -eq 0) 'Empty listing is not empty.'
$stats = @(ConvertFrom-DumpHeapStatistics ($fixture -split '\r?\n'))
Assert-Profile ($stats.Count -eq 2 -and @($stats | Where-Object IsFree).Count -eq 1) 'Statistics/free parsing failed.'
Assert-Profile (($stats | Where-Object { -not $_.IsFree } | Measure-Object Bytes -Sum).Sum -eq 25165968) 'Free bytes entered object total.'
$samples = @(Select-HeapRootSamples $rows 2 2)
Assert-Profile ($samples.Count -eq 4) 'Exact distinct sizes plus common group sampling failed.'
Assert-Profile (@($samples | Where-Object Bytes -eq 2097176).Count -eq 2) 'Repeated size must sample distinct addresses.'
Assert-Profile (@($samples | Select-Object -ExpandProperty Address -Unique).Count -eq $samples.Count) 'Sample addresses duplicated.'
$obj = "Name:        System.Byte[]`nMethodTable: 00007ffd693150a8`nSize:        16,777,240(0x1000018) bytes"
Assert-Profile (Test-DumpObject $obj $rows[0] 'System.Byte[]') 'Valid dumpobj rejected.'
Assert-Profile (-not (Test-DumpObject $obj $rows[1] 'System.Byte[]')) 'Wrong object size accepted.'
Assert-Profile (-not (Test-DumpObject $obj $rows[0] 'FluentGpu.Dsl.BoxEl')) 'Wrong type accepted.'
$root = "gcroot 01d538882bb8`n -> 01d537cbedd8 Wavee.LiveDealerTransport`n -> 01d538882bb8 System.Byte[]`nFound 2 unique roots."
Assert-Profile (Test-GcRootTarget $root '01d538882bb8') 'Verified terminal target rejected.'
Assert-Profile (-not (Test-GcRootTarget $root '01d53c240240')) 'Shared prefix/wrong target accepted.'
Assert-Profile (-not (Test-GcRootTarget "gcroot 01d538882bb8`nFound 0 unique roots." '01d538882bb8')) 'Unrooted command echo accepted.'

$cpu = @'
{"shared":{"frames":[{"name":"FluentApp.RunCoreOnUiThread()"},{"name":"AppHost.RunFrame()"},{"name":"FlexLayout.Measure()"},{"name":"Win32Window.WaitForPacedWork(int32,bool)"},{"name":"RenderThread.Loop()"},{"name":"D3D12Device.SubmitDrawList()"}]},"profiles":[{"name":"Thread (101)","type":"sampled","unit":"milliseconds","startValue":0,"endValue":10,"samples":[[0,1,2],[0,3],[0,1,2]],"weights":[2,6,2]},{"name":"Thread (202)","type":"evented","unit":"seconds","startValue":0,"endValue":0.01,"events":[{"type":"O","at":0,"frame":4},{"type":"O","at":0,"frame":5},{"type":"C","at":0.01,"frame":5},{"type":"C","at":0.01,"frame":4}]}]}
'@ | ConvertFrom-Json
$reports = @(Get-SpeedscopeThreadReport $cpu @([pscustomobject]@{StartMs=1;EndMs=9}))
Assert-Profile ($reports.Count -eq 2 -and $reports[0].IsUi -and $reports[1].IsRender) 'Thread stack identity failed.'
Assert-Profile ($reports[0].ObservedMs -eq 8 -and $reports[0].WorkMs -eq 2 -and $reports[0].WaitMs -eq 6) 'Window clipping or wait separation failed.'
Assert-Profile ($reports[0].Inclusive['AppHost.RunFrame()'] -eq 2 -and $reports[0].Exclusive['FlexLayout.Measure()'] -eq 2) 'Inclusive/exclusive aggregation failed.'
Assert-Profile ($reports[1].ObservedMs -eq 8 -and $reports[1].WorkMs -eq 8) 'Evented stack durations or unit conversion failed.'
$threw = $false
try { Get-SpeedscopeThreadReport $cpu @([pscustomobject]@{StartMs=1;EndMs=5}, [pscustomobject]@{StartMs=4;EndMs=8}) | Out-Null } catch { $threw = $true }
Assert-Profile $threw 'Overlapping windows silently double counted.'

# Repeated names at different frame IDs and recursive frames count once inclusively;
# wait classification follows any ancestor, while attribution still uses the leaf.
$recursive = @'
{"shared":{"frames":[{"name":"AppHost.RunFrame()"},{"name":"Work()"},{"name":"WaitHandle.WaitOne()"},{"name":"Work()"}]},"profiles":[{"name":"recursive","type":"evented","unit":"milliseconds","startValue":0,"endValue":4,"events":[{"type":"O","at":0,"frame":0},{"type":"O","at":0,"frame":1},{"type":"O","at":0,"frame":3},{"type":"C","at":1,"frame":3},{"type":"O","at":1,"frame":2},{"type":"O","at":1,"frame":3},{"type":"C","at":3,"frame":3},{"type":"C","at":3,"frame":2},{"type":"C","at":4,"frame":1},{"type":"C","at":4,"frame":0}]}]}
'@ | ConvertFrom-Json
$recursiveReport = @(Get-SpeedscopeThreadReport $recursive)[0]
Assert-Profile ($recursiveReport.WorkMs -eq 2 -and $recursiveReport.WaitMs -eq 2 -and $recursiveReport.Intervals -eq 3) 'Zero-time events added intervals or wait depth was lost.'
Assert-Profile ($recursiveReport.Inclusive['Work()'] -eq 2 -and $recursiveReport.Exclusive['Work()'] -eq 2 -and $recursiveReport.Waits['Work()'] -eq 2) 'Recursive/duplicate names or wait leaf attribution changed.'
$clipped = @(Get-SpeedscopeThreadReport $recursive @([pscustomobject]@{StartMs=2.5;EndMs=3.5}, [pscustomobject]@{StartMs=0.5;EndMs=1.5}))[0]
Assert-Profile ($clipped.WorkMs -eq 1 -and $clipped.WaitMs -eq 1 -and $clipped.ObservedMs -eq 2) 'Sorted disjoint window cursor clipped or double counted intervals.'

$identity = @'
{"shared":{"frames":[{"name":"AppHost.RunFrame()"},{"name":"Work()"}]},"profiles":[{"name":"zero identity","type":"evented","unit":"milliseconds","startValue":0,"endValue":4,"events":[{"type":"O","at":0,"frame":0},{"type":"C","at":0,"frame":0},{"type":"O","at":0,"frame":1},{"type":"C","at":4,"frame":1}]},{"name":"sample identity","type":"sampled","unit":"microseconds","startValue":0,"endValue":4000,"samples":[[0],[1]],"weights":[0,4000]}]}
'@ | ConvertFrom-Json
$identityReports = @(Get-SpeedscopeThreadReport $identity @([pscustomobject]@{StartMs=1;EndMs=3}))
foreach ($report in $identityReports) {
    Assert-Profile ($report.IsUi -and $report.Intervals -eq 1 -and $report.WorkMs -eq 2) 'Zero-duration thread identity outside windows was dropped or counted.'
    Assert-Profile (-not $report.Inclusive.ContainsKey('AppHost.RunFrame()')) 'Zero-duration identity accrued work.'
}

$negative = @'
{"shared":{"frames":[{"name":"Work()"}]},"profiles":[{"name":"negative","type":"sampled","unit":"milliseconds","startValue":0,"endValue":0,"samples":[[]],"weights":[-1]}]}
'@ | ConvertFrom-Json
$threw = $false
try { Get-SpeedscopeThreadReport $negative | Out-Null } catch { $threw = $true }
Assert-Profile $threw 'Negative sample duration accepted for an empty stack.'
$broken = @'
{"shared":{"frames":[{"name":"Work()"},{"name":"Other()"}]},"profiles":[{"name":"broken","type":"evented","unit":"milliseconds","startValue":0,"endValue":0,"events":[{"type":"O","at":0,"frame":0},{"type":"C","at":0,"frame":1}]}]}
'@ | ConvertFrom-Json
$threw = $false
try { Get-SpeedscopeThreadReport $broken | Out-Null } catch { $threw = $true }
Assert-Profile $threw 'Invalid zero-duration close escaped validation.'

# Real exporter shape: many zero-duration stack changes, one measured interval.
# This exercises the fast path without a machine-dependent timing assertion.
$events = New-Object 'System.Collections.Generic.List[object]'
for ($i = 0; $i -lt 5000; $i++) {
    $events.Add([pscustomobject]@{type='O'; at=0; frame=0})
    $events.Add([pscustomobject]@{type='C'; at=0; frame=0})
}
$events.Add([pscustomobject]@{type='O'; at=0; frame=0})
$events.Add([pscustomobject]@{type='C'; at=1; frame=0})
$dense = [pscustomobject]@{
    shared=[pscustomobject]@{frames=@([pscustomobject]@{name='AppHost.RunFrame()'})}
    profiles=@([pscustomobject]@{name='dense'; type='evented'; unit='milliseconds'; startValue=0; endValue=1; events=$events.ToArray()})
}
$denseReport = @(Get-SpeedscopeThreadReport $dense)[0]
Assert-Profile ($denseReport.IsUi -and $denseReport.Intervals -eq 1 -and $denseReport.WorkMs -eq 1 -and $denseReport.Inclusive['AppHost.RunFrame()'] -eq 1) 'Dense zero-duration groups changed accounting.'

# Real dotnet-trace exports decorate resolved methods with exporter pseudo frames.
# Categories must preserve the denominator while rankings contain actual methods.
$markers = @'
{"shared":{"frames":[{"name":"Process64 Wavee (123) Args:  "},{"name":"(Non-Activities)"},{"name":"Threads"},{"name":"Thread (42)"},{"name":"AppHost.RunFrame()"},{"name":"Measured.Work()"},{"name":"CPU_TIME"},{"name":"UNMANAGED_CODE_TIME"},{"name":"WaitHandle.WaitOne()"},{"name":"?!?"}]},"profiles":[{"name":"markers","type":"sampled","unit":"milliseconds","startValue":0,"endValue":11,"samples":[[0,1,2,3,4,5,6],[0,1,2,3,4,5,9,7],[0,1,2,3,4,8,7],[0,1,2,3,6],[0,1,2,3,4,5]],"weights":[2,3,4,1,1]},{"name":"event markers","type":"evented","unit":"milliseconds","startValue":0,"endValue":10,"events":[{"type":"O","at":0,"frame":0},{"type":"O","at":0,"frame":4},{"type":"O","at":0,"frame":5},{"type":"O","at":0,"frame":6},{"type":"C","at":2,"frame":6},{"type":"O","at":2,"frame":7},{"type":"C","at":5,"frame":7},{"type":"C","at":5,"frame":5},{"type":"O","at":5,"frame":8},{"type":"O","at":5,"frame":7},{"type":"C","at":9,"frame":7},{"type":"C","at":9,"frame":8},{"type":"C","at":9,"frame":4},{"type":"O","at":9,"frame":6},{"type":"C","at":10,"frame":6},{"type":"C","at":10,"frame":0}]}]}
'@ | ConvertFrom-Json
$markerReports = @(Get-SpeedscopeThreadReport $markers)
Assert-Profile ($markerReports[0].ObservedMs -eq 11 -and $markerReports[0].WorkMs -eq 7 -and $markerReports[0].WaitMs -eq 4) 'Sampled marker stripping changed the observed denominator.'
Assert-Profile ($markerReports[0].Exclusive['Measured.Work()'] -eq 6 -and $markerReports[0].Inclusive['AppHost.RunFrame()'] -eq 6) 'Sampled synthetic leaf stole resolved-method attribution.'
Assert-Profile ($markerReports[0].Categories['UNMARKED'] -eq 1) 'Unmarked category duration disappeared.'
Assert-Profile ($markerReports[1].ObservedMs -eq 10 -and $markerReports[1].Exclusive['Measured.Work()'] -eq 5) 'Evented marker stripping changed duration or leaf attribution.'
foreach ($report in $markerReports) {
    Assert-Profile ($report.Categories['CPU_TIME'] -eq 3 -and $report.Categories['UNMANAGED_CODE_TIME'] -eq 7) 'Exporter categories did not preserve durations including named waits.'
    Assert-Profile ($report.Waits['WaitHandle.WaitOne()'] -eq 4) 'Wait method attribution was replaced by a synthetic marker.'
    Assert-Profile ($report.UnattributedWorkMs -eq 1) 'Synthetic-only work disappeared or became a fake method.'
    Assert-Profile (($report.Categories.Values | Measure-Object -Sum).Sum -eq $report.ObservedMs) 'Category totals no longer partition the observed denominator.'
    Assert-Profile ((($report.Exclusive.Values | Measure-Object -Sum).Sum + $report.UnattributedWorkMs) -eq $report.WorkMs) 'Resolved and unattributed exclusive work does not reconcile.'
    foreach ($pseudo in @('CPU_TIME', 'UNMANAGED_CODE_TIME', '(Non-Activities)', 'Threads', 'Thread (42)', 'Process64 Wavee (123) Args:  ', '?!?')) {
        Assert-Profile (-not $report.Inclusive.ContainsKey($pseudo) -and -not $report.Exclusive.ContainsKey($pseudo) -and -not $report.Waits.ContainsKey($pseudo)) "Pseudo frame entered a method ranking: $pseudo"
    }
}
Write-Host 'perf-profile fixture checks passed.'
