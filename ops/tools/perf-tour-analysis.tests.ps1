$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'perf-tour-analysis.ps1')
function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) { throw "$Because -- expected $Expected, observed $Actual" }
}
$route = ' t=10 nav.route route=album:spotify:album:one arg=- navId=1'
$reveal = ' t=90 page.reveal navId=1 route=album:spotify:album:one arg=- revealMs=80'
$memory = ' t=100 mem.sample reason=session-end ws=100 wsPeak=110 processPeak=120 wsBytes=104857600 processPeakBytes=125829120'
$final = ' t=200 session.frames final=1 frames=10 over83=0 invalid=0 worstMs=8.3'
$good = @($route, $reveal, $memory, $final)
$result = Get-PerfSession $good $true $false
Assert-Equal 'PASS' $result.FramesVerdict 'Complete raw 8.3 boundary passes'
Assert-Equal 'PASS' $result.MemoryVerdict 'Complete memory passes'
Assert-Equal 'PASS' $result.RevealVerdict 'Complete matching reveal passes'
$result = Get-PerfSession @($route, $reveal, ($memory -replace 'reason=session-end', 'reason=periodic'), $final) $true $false
Assert-Equal 'INCOMPLETE' $result.MemoryVerdict 'Periodic memory cannot substitute for final lifetime peak sample'
$result = Get-PerfSession @() $true $false
Assert-Equal 'INCOMPLETE' $result.FramesVerdict 'No session observations cannot pass'
Assert-Equal 'INCOMPLETE' $result.MemoryVerdict 'No memory cannot pass'
Assert-Equal 'INCOMPLETE' $result.RevealVerdict 'No routes cannot pass'
$result = Get-PerfSession @($route, $memory, $final) $true $false
Assert-Equal 'INCOMPLETE' $result.RevealVerdict 'Missing reveal cannot pass'
$result = Get-PerfSession @($route, $reveal, $reveal, $memory, $final) $true $false
Assert-Equal 'INCOMPLETE' $result.RevealVerdict 'Duplicate reveal cannot pass'
$result = Get-PerfSession @($route, $route, $reveal, $memory, $final) $true $false
Assert-Equal 'INCOMPLETE' $result.RevealVerdict 'Duplicate route ID cannot pass'
$foreign = ' t=90 page.reveal navId=1 route=album:spotify:album:other arg=- revealMs=10'
$result = Get-PerfSession @($route, $foreign, $memory, $final) $true $false
Assert-Equal 'INCOMPLETE' $result.RevealVerdict 'Late foreign reveal cannot satisfy expected route'
$slow = ' t=90 page.reveal navId=1 route=album:spotify:album:one arg=- revealMs=100.0001'
$result = Get-PerfSession @($route, $slow, $memory) $false $false
Assert-Equal 'FAIL' $result.RevealVerdict 'Known reveal breach wins over incomplete coverage without rounding'
$result = Get-PerfSession @($route, $reveal, ' t=100 mem.sample ws=100 wsPeak=150 processPeak=200.0001', $final) $true $false
Assert-Equal 'FAIL' $result.MemoryVerdict 'OS lifetime peak breach counts even if samples were low'
$result = Get-PerfSession @($route, $reveal, ' t=100 mem.sample ws=100 wsPeak=150 processPeak=200.0 wsBytes=104857600 processPeakBytes=209715201', $final) $true $false
Assert-Equal 'FAIL' $result.MemoryVerdict 'One byte above memory threshold cannot round into passing'
$result = Get-PerfSession @($route, $reveal, ' t=100 mem.sample ws=201', $final) $false $false
Assert-Equal 'FAIL' $result.MemoryVerdict 'Known memory breach wins over missing peak fields'
$result = Get-PerfSession @($route, $reveal, $memory, ' t=200 session.frames final=0 frames=10 over83=1 invalid=0 worstMs=8.30001') $true $false
Assert-Equal 'FAIL' $result.FramesVerdict 'Periodic known breach fails without final snapshot'
$result = Get-PerfSession @($route, $reveal, $memory, ($final -replace 'final=1', 'final=0')) $true $false
Assert-Equal 'INCOMPLETE' $result.FramesVerdict 'Periodic clean snapshot cannot substitute for final'
$result = Get-PerfSession $good $true $true
Assert-Equal 'INCOMPLETE' $result.FramesVerdict 'Heap capture cannot establish unpolluted session'
$result = Get-PerfSession @($route, $reveal, $memory, ($final -replace 'invalid=0', 'invalid=1')) $true $false
Assert-Equal 'INCOMPLETE' $result.FramesVerdict 'Invalid frame duration cannot pass'
$result = Get-PerfSession @($route, $reveal, $memory, ($final -replace 'frames=10', 'frames=NaN')) $true $false
Assert-Equal 'INCOMPLETE' $result.FramesVerdict 'Non-numeric frames cannot pass'
$result = Get-PerfSession @($route, $reveal, $memory, ($final -replace 'final=1', 'final=0'), ($final -replace 'frames=10', 'frames=9')) $true $false
Assert-Equal 'INCOMPLETE' $result.FramesVerdict 'Counter regression cannot pass'
$rollup = Get-PerfRollup @('frames=10 overBudget=1 slow33=0 stall100=0 missedVblanks=2 frameMs=9', 'frames=20 overBudget=2 slow33=1 stall100=0 missedVblanks=3 frameMs=40')
Assert-Equal 30 $rollup.frames 'All rollup frames are reported'
Assert-Equal 3 $rollup.overBudget 'Later burst breaches are reported'
Assert-Equal 40 $rollup.Worst 'Later worst frame is retained'
$rollup = Get-PerfRollup @(' t=5 nav.frames frames=480 overBudget=2 slow33=0 stall100=0 missedVblanks=1 censusFrames=3 worst=frameMs=7.8 flush=6.6 reactive=6.6')
Assert-Equal $true $rollup.Complete 'The nested worst=frameMs census counts as complete frame coverage'
Assert-Equal 7.8 $rollup.Worst 'The nested worst frame is read'
$policy = Get-PerfStartupPolicy $false $true $true
Assert-Equal $false $policy.RunTour 'Failed startup readiness skips every route and input step'
Assert-Equal $false $policy.CollectHeap 'Failed startup readiness never collects a heap'
Assert-Equal $true $policy.Close 'Failed startup readiness still shuts the process down even with KeepOpen'
$policy = Get-PerfStartupPolicy $true $true $true
Assert-Equal $true $policy.RunTour 'Ready startup runs the tour'
Assert-Equal $true $policy.CollectHeap 'Ready startup honours an opted-in heap collection'
Assert-Equal $false $policy.Close 'KeepOpen leaves a ready process running'
$policy = Get-PerfStartupPolicy $true $false $false
Assert-Equal $true $policy.Close 'A ready process without KeepOpen is shut down for final coverage'
Assert-Equal $false $policy.CollectHeap 'Heap collection stays opt-in'

$idleLines = @(' t=10 nav.route route=home arg=- navId=1', ' t=20 put-state BecameActive active=True track=- pos=0 playing=False paused=True ctx=- msgId=1 commandId=- cluster=0B')
$playback = Get-PerfPlaybackState $idleLines
Assert-Equal $false $playback.Observed 'No playing=True put-state line is idle'
Assert-Equal 0 $playback.Count 'Idle slice counts zero playback events'
Assert-Equal 'idle: no playback events in the measured slice' $playback.Text 'Idle text matches the report line'

$playingLines = @(' t=10 nav.route route=home arg=- navId=1', ' t=30 put-state PlayerCommand active=True track=spotify:track:one pos=0 playing=True paused=False ctx=- msgId=2 commandId=- cluster=10B', ' t=45 put-state PositionTick active=True track=spotify:track:one pos=5000 playing=True paused=False ctx=- msgId=3 commandId=- cluster=0B')
$playback = Get-PerfPlaybackState $playingLines
Assert-Equal $true $playback.Observed 'A playing=True put-state line is playback observed'
Assert-Equal 2 $playback.Count 'Both playing=True lines are counted'
Assert-Equal 30 $playback.FirstT 'The earliest playing=True line is reported'
Assert-Equal 'playback events observed: 2 (first at t=30)' $playback.Text 'Observed text matches the report line'

Write-Host 'Performance tour analysis fixture checks passed.'
