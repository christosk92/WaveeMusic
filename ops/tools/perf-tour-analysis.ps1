# Pure log analysis shared by the live tour and fixture tests. No app launch or source inspection.
function Get-PerfStartupPolicy([bool]$Ready, [bool]$KeepOpen, [bool]$CollectHeap) {
    # Failed startup aborts route/input/capture work, and still requires cleanup.
    return [pscustomobject]@{ RunTour = $Ready; CollectHeap = $Ready -and $CollectHeap; Close = -not $Ready -or -not $KeepOpen }
}
function Get-Field([string]$Line, [string]$Key, [string]$Default = '-') {
    if ($Line -match "(?:^|\s)$([regex]::Escape($Key))=([^\s|]+)") { return $Matches[1] }
    return $Default
}
function Get-Num([string]$Line, [string]$Key) {
    $value = Get-Field $Line $Key ''
    $number = 0.0
    if ([double]::TryParse($value, [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref]$number) -and
        -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)) { return $number }
    return [double]::NaN
}
function Get-T([string]$Line) { return Get-Num $Line 't' }
function Fmt([double]$Value, [string]$Format = '0.0') {
    if ([double]::IsNaN($Value)) { return '-' }
    return $Value.ToString($Format, [cultureinfo]::InvariantCulture)
}
function Get-PerfVerdict([bool]$Complete, [bool]$Violation) {
    if ($Violation) { return 'FAIL' }
    if (-not $Complete) { return 'INCOMPLETE' }
    return 'PASS'
}
function Get-PerfRollup([string[]]$Lines) {
    $result = [ordered]@{ Count = $Lines.Count; frames = 0; overBudget = 0; slow33 = 0; stall100 = 0; missedVblanks = 0; Worst = [double]::NaN; WorstLine = ''; Complete = $Lines.Count -gt 0 }
    foreach ($line in $Lines) {
        foreach ($key in @('frames', 'overBudget', 'slow33', 'stall100', 'missedVblanks')) {
            $value = Get-Num $line $key
            if ([double]::IsNaN($value) -or $value -lt 0) { $result.Complete = $false }
            else { $result[$key] += $value }
        }
        # nav.frames/scroll.frames carry the worst frame as the nested `worst=frameMs=N ...` census, which the
        # whitespace-anchored Get-Field cannot see; a bare `frameMs=` (frame.slow lines) still wins when present.
        $worst = Get-Num $line 'frameMs'
        if ([double]::IsNaN($worst) -and $line -match 'worst=frameMs=([^\s|]+)') {
            $nested = 0.0
            if ([double]::TryParse($Matches[1], [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref]$nested)) { $worst = $nested }
        }
        if ([double]::IsNaN($worst)) { $result.Complete = $false }
        elseif ([double]::IsNaN($result.Worst) -or $worst -gt $result.Worst) { $result.Worst = $worst; $result.WorstLine = $line }
    }
    return [pscustomobject]$result
}
function Get-PerfReveal([string]$RouteLine, [string[]]$Lines) {
    $id = Get-Field $RouteLine 'navId' ''
    $route = Get-Field $RouteLine 'route' ''
    $arg = Get-Field $RouteLine 'arg' ''
    $matchesForId = @($Lines | Where-Object { $_ -match '\bpage\.reveal\b' -and (Get-Field $_ 'navId' '') -eq $id })
    $valid = @($matchesForId | Where-Object {
        (Get-Field $_ 'route' '') -eq $route -and (Get-Field $_ 'arg' '') -eq $arg -and
        (Get-T $_) -ge (Get-T $RouteLine) -and (Get-Num $_ 'revealMs') -ge 0
    })
    $complete = $id -match '^\d+$' -and $route -ne '' -and $arg -ne '' -and $matchesForId.Count -eq 1 -and $valid.Count -eq 1
    $ms = [double]::NaN
    if ($valid.Count -gt 0) { $ms = ($valid | ForEach-Object { Get-Num $_ 'revealMs' } | Measure-Object -Maximum).Maximum }
    return [pscustomobject]@{ Complete = $complete; Ms = $ms; Line = $(if ($valid.Count -gt 0) { $valid[0] } else { '' }); NavId = $id }
}
function Get-PerfPlaybackState([string[]]$Lines) {
    # DeviceStatePublisher (category "playback") logs one `put-state ... playing=True|False ...` line per Connect-state
    # PUT — the only always-on record of whether the app was actually rendering audio during the measured slice.
    $events = @($Lines | Where-Object { $_ -match '\bput-state\b' -and (Get-Field $_ 'playing' '') -eq 'True' })
    if ($events.Count -eq 0) {
        return [pscustomobject]@{ Observed = $false; Count = 0; FirstT = [double]::NaN; Text = 'idle: no playback events in the measured slice' }
    }
    $firstT = ($events | ForEach-Object { Get-T $_ } | Measure-Object -Minimum).Minimum
    return [pscustomobject]@{ Observed = $true; Count = $events.Count; FirstT = $firstT; Text = "playback events observed: $($events.Count) (first at t=$firstT)" }
}
function Get-PerfSession([string[]]$Lines, [bool]$CoverageComplete, [bool]$HeapCollected, [double]$WorkingSetGateMB = 200, [double]$RevealGateMs = 100) {
    $snapshots = @($Lines | Where-Object { $_ -match '\bsession\.frames\b' })
    $finals = @($snapshots | Where-Object { (Get-Field $_ 'final') -eq '1' })
    $frameComplete = $CoverageComplete -and -not $HeapCollected -and $finals.Count -eq 1
    $over = 0.0; $worst = [double]::NaN; $frames = [double]::NaN
    $previousFrames = 0.0; $previousOver = 0.0; $previousWorst = 0.0
    foreach ($line in $snapshots) {
        $count = Get-Num $line 'frames'; $bad = Get-Num $line 'over83'; $ms = Get-Num $line 'worstMs'
        $invalid = Get-Num $line 'invalid'
        if ([double]::IsNaN($invalid) -or $invalid -ne 0 -or $count -lt $previousFrames -or $bad -lt $previousOver -or $ms -lt $previousWorst -or
            $count -ne [math]::Floor($count) -or $bad -ne [math]::Floor($bad)) { $frameComplete = $false }
        $previousFrames = $count; $previousOver = $bad; $previousWorst = $ms
        if ($bad -gt $over) { $over = $bad }
        if ($ms -gt $worst -or [double]::IsNaN($worst)) { $worst = $ms }
        if ($count -gt $frames -or [double]::IsNaN($frames)) { $frames = $count }
        if ([double]::IsNaN($bad) -or $bad -lt 0 -or [double]::IsNaN($ms) -or $ms -lt 0 -or
            [double]::IsNaN($count) -or $count -le 0 -or $bad -gt $count) { $frameComplete = $false }
    }
    if ($finals.Count -eq 1 -and $snapshots[-1] -ne $finals[0]) { $frameComplete = $false }
    # Logged known breaches still fail even when an old binary lacks the authoritative session counter.
    $knownFrameBreach = @($Lines | Where-Object { $_ -match '\b(frame\.slow|nav\.frames|scroll\.frames)\b' -and (Get-Num $_ 'frameMs') -gt 8.3 }).Count -gt 0
    $memory = @($Lines | Where-Object { $_ -match '\bmem\.sample\b' })
    $memoryFinals = @($memory | Where-Object { (Get-Field $_ 'reason' '') -eq 'session-end' })
    $memoryComplete = $CoverageComplete -and $memory.Count -gt 0 -and $memoryFinals.Count -eq 1 -and -not $HeapCollected
    $peak = [double]::NaN; $sampledPeak = [double]::NaN; $processPeak = [double]::NaN
    $rawMemoryBreach = $false
    foreach ($line in $memory) {
        foreach ($key in @('wsBytes', 'processPeakBytes')) {
            $bytes = Get-Num $line $key
            if ([double]::IsNaN($bytes) -or $bytes -lt 0 -or $bytes -ne [math]::Floor($bytes)) { $memoryComplete = $false }
            elseif ($bytes -gt $WorkingSetGateMB * 1MB) { $rawMemoryBreach = $true }
        }
        foreach ($key in @('ws', 'wsPeak', 'processPeak')) {
            $value = Get-Num $line $key
            if ([double]::IsNaN($value) -or $value -lt 0) { $memoryComplete = $false; continue }
            if ([double]::IsNaN($peak) -or $value -gt $peak) { $peak = $value }
            if ($key -eq 'processPeak' -and ([double]::IsNaN($processPeak) -or $value -gt $processPeak)) { $processPeak = $value }
            if ($key -ne 'processPeak' -and ([double]::IsNaN($sampledPeak) -or $value -gt $sampledPeak)) { $sampledPeak = $value }
        }
    }
    $routes = @($Lines | Where-Object { $_ -match '\bnav\.route\b' })
    $revealComplete = $CoverageComplete -and $routes.Count -gt 0
    $ids = @{}; $reveals = @(); $revealBreaches = @()
    foreach ($route in $routes) {
        $reveal = Get-PerfReveal $route $Lines
        if (-not $reveal.Complete -or $ids.ContainsKey($reveal.NavId)) { $revealComplete = $false }
        $ids[$reveal.NavId] = $true
        $reveals += $reveal
        if ($reveal.Ms -gt $RevealGateMs) { $revealBreaches += $reveal }
    }
    $anyRevealBreach = $revealBreaches.Count -gt 0
    foreach ($line in @($Lines | Where-Object { $_ -match '\bpage\.reveal\b' })) {
        if (-not $ids.ContainsKey((Get-Field $line 'navId' ''))) { $revealComplete = $false }
        if ((Get-Num $line 'revealMs') -gt $RevealGateMs) { $anyRevealBreach = $true }
    }
    return [pscustomobject]@{
        FramesVerdict = Get-PerfVerdict $frameComplete ($over -gt 0 -or $worst -gt 8.3 -or $knownFrameBreach)
        MemoryVerdict = Get-PerfVerdict $memoryComplete ($rawMemoryBreach -or $peak -gt $WorkingSetGateMB)
        RevealVerdict = Get-PerfVerdict $revealComplete $anyRevealBreach
        Frames = $frames; Over83 = $over; WorstMs = $worst; PeakWs = $peak; SampledPeak = $sampledPeak; ProcessPeak = $processPeak
        FinalCount = $finals.Count; Reveals = $reveals; RevealBreaches = $revealBreaches; Routes = $routes.Count
    }
}
