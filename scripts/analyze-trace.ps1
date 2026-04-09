# analyze-trace.ps1
#
# Exports and summarizes WinUI navigation/frame metrics from an ETL trace.
# Works with wpaexporter in headless mode and produces a normalized
# Navigation_Metrics.csv artifact for baseline-vs-change comparisons.

param(
    [string]$Trace = "",
    [string]$OutDir = "",
    [string]$ProfilePath = ""
)

$ErrorActionPreference = "Continue"

function Parse-Number {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $clean = $Text -replace ',', ''
    $value = 0.0
    if ([double]::TryParse($clean, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        return $value
    }
    return $null
}

function Get-RowCount {
    param([string]$CsvPath)
    if (-not (Test-Path $CsvPath)) { return 0 }
    try {
        $lines = Get-Content $CsvPath
        if ($lines.Count -le 1) { return 0 }
        return ($lines.Count - 1)
    }
    catch {
        return 0
    }
}

function Import-CsvSafe {
    param([string]$CsvPath)
    if (-not (Test-Path $CsvPath)) { return @() }

    $lines = Get-Content $CsvPath
    if ($lines.Count -le 1) { return @() }

    $rawHeaders = $lines[0] -split ','
    $counts = @{}
    $headers = New-Object System.Collections.Generic.List[string]

    foreach ($h in $rawHeaders) {
        $name = ($h -replace '^"|"$', '').Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { $name = "Column" }

        if ($counts.ContainsKey($name)) {
            $counts[$name] = [int]$counts[$name] + 1
            $name = "$name`_$($counts[$name])"
        }
        else {
            $counts[$name] = 1
        }

        $headers.Add($name) | Out-Null
    }

    return @($lines | Select-Object -Skip 1 | ConvertFrom-Csv -Header $headers)
}

$wpaexporter = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpaexporter.exe"
if (-not (Test-Path $wpaexporter)) {
    Write-Error "wpaexporter.exe not found at $wpaexporter"
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogProfile = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\Catalog\XamlAppResponsivenessAnalysis.wpaprofile"
$catalogRegions = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\Catalog\AppAnalysis.regions.xml"
$repoCustomProfile = Join-Path $repoRoot "scripts\wpa-profiles\Wavee.NavMetrics.wpaprofile"

if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    if (Test-Path $repoCustomProfile) {
        $ProfilePath = $repoCustomProfile
    }
    else {
        $ProfilePath = $catalogProfile
    }
}
elseif (-not (Test-Path $ProfilePath)) {
    $candidateProfile = Join-Path $repoRoot $ProfilePath
    if (Test-Path $candidateProfile) {
        $ProfilePath = $candidateProfile
    }
}

if (-not (Test-Path $ProfilePath)) {
    Write-Error "WPA profile not found: $ProfilePath"
    exit 1
}

# Repo-owned profiles may rely on a sibling AppAnalysis.regions.xml file.
$profileDir = Split-Path -Parent $ProfilePath
$profileRegions = Join-Path $profileDir "AppAnalysis.regions.xml"
if (-not (Test-Path $profileRegions) -and (Test-Path $catalogRegions)) {
    try {
        Copy-Item $catalogRegions $profileRegions -Force
        Write-Host "Provisioned profile dependency: $profileRegions" -ForegroundColor Gray
    }
    catch {
        Write-Host "Warning: failed to provision ${profileRegions}: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Resolve trace path (default: newest ETL in perf-traces/)
if ([string]::IsNullOrWhiteSpace($Trace)) {
    $perfDir = Join-Path $repoRoot "perf-traces"
    if (-not (Test-Path $perfDir)) {
        Write-Error "No -Trace specified and $perfDir does not exist. Run measure-page-nav.ps1 first."
        exit 1
    }
    $latest = Get-ChildItem $perfDir -Filter "*.etl" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        Write-Error "No .etl files found in $perfDir. Run measure-page-nav.ps1 first."
        exit 1
    }
    $Trace = $latest.FullName
}
elseif (-not (Test-Path $Trace)) {
    $candidate = Join-Path $repoRoot $Trace
    if (Test-Path $candidate) { $Trace = $candidate }
}

if (-not (Test-Path $Trace)) {
    Write-Error "Trace file not found: $Trace"
    exit 1
}

$Trace = (Get-Item $Trace).FullName
$traceName = [IO.Path]::GetFileNameWithoutExtension($Trace)
$scenario = ($traceName -replace '-\d{8}-\d{6}$', '')

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path $Trace) "analysis-$traceName"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host ""
Write-Host "===== Analyzing trace =====" -ForegroundColor Cyan
Write-Host "Input:    $Trace" -ForegroundColor Gray
Write-Host "Profile:  $ProfilePath" -ForegroundColor Gray
Write-Host "Output:   $OutDir" -ForegroundColor Gray
Write-Host "Scenario: $scenario" -ForegroundColor Gray
Write-Host ""

# Run exporter. New WPA builds may exit non-zero while still producing CSVs.
& $wpaexporter -i $Trace -profile $ProfilePath -outputfolder $OutDir -outputformat CSV
$exportExit = $LASTEXITCODE

$csvs = Get-ChildItem $OutDir -Filter "*.csv" -ErrorAction SilentlyContinue
if ($null -eq $csvs -or $csvs.Count -eq 0) {
    Write-Error "wpaexporter produced no CSV files in $OutDir"
    exit 1
}

if ($exportExit -ne 0) {
    Write-Host "wpaexporter exited with code $exportExit (continuing because CSV output exists)." -ForegroundColor Yellow
}

Write-Host "Produced CSVs:" -ForegroundColor Cyan
foreach ($csv in $csvs) {
    $sizeKb = [math]::Round($csv.Length / 1024, 1)
    Write-Host ("  {0,-60} {1,10} KB" -f $csv.Name, $sizeKb) -ForegroundColor Gray
}

# Quality gates: table presence + row thresholds (not exit code only)
$roiPath = Join-Path $OutDir "Regions_of_Interest_Details.csv"
$framePath = Join-Path $OutDir "Xaml_Frame_Details_Table_Xaml_UI_Frame_E2E.csv"
$sampledPath = Join-Path $OutDir "CPU_Usage_(Sampled)_Breakdown_by_Process,_Thread,_Activity,_Stack.csv"
$uiThreadPath = Join-Path $OutDir "CPU_Usage_(Attributed)_XAML_UI_Thread_CPU_Breakdown.csv"
$renderThreadPath = Join-Path $OutDir "CPU_Usage_(Attributed)_XAML_Render_Thread_CPU_Breakdown.csv"
$lifetimePath = Join-Path $OutDir "Processes_Lifetime_By_Process.csv"

$genericFiles = Get-ChildItem $OutDir -Filter "Generic_Events*.csv" -ErrorAction SilentlyContinue

$roiRows = Get-RowCount $roiPath
$frameRows = Get-RowCount $framePath
$sampledRows = Get-RowCount $sampledPath
$genericRows = 0
foreach ($f in $genericFiles) { $genericRows += Get-RowCount $f.FullName }

$issues = New-Object System.Collections.Generic.List[string]
if ($roiRows -lt 10) { $issues.Add("ROI table has only $roiRows rows (expected >= 10)") }
if ($frameRows -lt 5) { $issues.Add("XAML frame E2E table has only $frameRows rows (expected >= 5)") }
if ($sampledRows -lt 50) { $issues.Add("Sampled CPU table has only $sampledRows rows (expected >= 50)") }

Write-Host ""
Write-Host "Quality gate summary:" -ForegroundColor Cyan
Write-Host "  ROI rows:          $roiRows" -ForegroundColor Gray
Write-Host "  XAML frame rows:   $frameRows" -ForegroundColor Gray
Write-Host "  Sampled CPU rows:  $sampledRows" -ForegroundColor Gray
Write-Host "  Generic rows:      $genericRows" -ForegroundColor Gray

if ($issues.Count -gt 0) {
    Write-Host "  Status: WARN (sparse tables detected)" -ForegroundColor Yellow
    foreach ($i in $issues) { Write-Host "    - $i" -ForegroundColor Yellow }
}
else {
    Write-Host "  Status: PASS" -ForegroundColor Green
}

$metrics = New-Object System.Collections.Generic.List[object]
function Add-Metric {
    param(
        [string]$MetricType,
        [string]$Process = "",
        [string]$ThreadId = "",
        [double]$StartS = [double]::NaN,
        [double]$StopS = [double]::NaN,
        [double]$DurationMs = [double]::NaN,
        [string]$Details = "",
        [string]$SourceTable = ""
    )
    $metrics.Add([pscustomobject]@{
        Trace = $Trace
        Scenario = $scenario
        MetricType = $MetricType
        Process = $Process
        ThreadId = $ThreadId
        StartS = $StartS
        StopS = $StopS
        DurationMs = $DurationMs
        Details = $Details
        SourceTable = $SourceTable
    }) | Out-Null
}

# ROI metrics
if (Test-Path $roiPath) {
    try {
        $roi = Import-CsvSafe $roiPath
        $byRegion = $roi |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_.'Region Friendly Name') -and
                -not [string]::IsNullOrWhiteSpace($_.'Duration (s)')
            } |
            Group-Object 'Region Friendly Name' |
            ForEach-Object {
                $row = $_.Group | Select-Object -First 1
                $durMs = Parse-Number $row.'Duration (s)'
                $startS = Parse-Number $row.'Start Time (s)'
                $stopS = Parse-Number $row.'Stop Time (s)'
                [pscustomobject]@{
                    Region = $_.Name
                    DurationMs = if ($durMs -ne $null) { [math]::Round($durMs * 1000, 3) } else { [double]::NaN }
                    StartS = $startS
                    StopS = $stopS
                }
            } |
            Sort-Object DurationMs -Descending

        Write-Host ""
        Write-Host "Top ROI durations (ms):" -ForegroundColor Cyan
        $byRegion | Select-Object -First 12 | Format-Table -AutoSize

        foreach ($r in $byRegion) {
            if (-not [double]::IsNaN($r.DurationMs)) {
                Add-Metric -MetricType "RoiDuration" -StartS $r.StartS -StopS $r.StopS -DurationMs $r.DurationMs -Details $r.Region -SourceTable (Split-Path $roiPath -Leaf)
            }
        }
    }
    catch {
        Write-Host "Failed to parse ROI table: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# XAML frame E2E metrics (manual headers due duplicate StartTime column names)
if (Test-Path $framePath) {
    try {
        $headers = @(
            'Process','StartTimeMs1','ThreadId','FrameType','PTFrameId','CpuEndDeltaMs','GpuDurationMs','GpuEndDeltaMs',
            'DwmPickDeltaMs','DwmCpuEndDeltaMs','DwmGpuDurationMs','DwmFlipReqDeltaMs','DwmFlipDeltaMs','SinceLastDwmFlipMs',
            'WorkStack','Task','TaskInclusiveMs','TaskExclusiveMs','ElementId','ElementName','ElementTemplate','TaskInfo','StartTimeMs2','E2EDeltaMs'
        )
        $raw = Get-Content $framePath
        if ($raw.Count -gt 1) {
            $frameRowsData = $raw | Select-Object -Skip 1 | ConvertFrom-Csv -Header $headers
            $waveeRows = $frameRowsData | Where-Object { $_.Process -like 'Wavee.UI.WinUI*' }
            foreach ($fr in $waveeRows) {
                $startMs = Parse-Number $fr.StartTimeMs1
                $startS = [double]::NaN
                if ($startMs -ne $null) { $startS = $startMs / 1000.0 }
                $taskExclusiveMs = Parse-Number $fr.TaskExclusiveMs
                if ($taskExclusiveMs -ne $null) {
                    Add-Metric -MetricType "WaveeFrameTaskExclusive" -Process $fr.Process -ThreadId $fr.ThreadId -StartS $startS -DurationMs $taskExclusiveMs -Details ("FrameType=" + $fr.FrameType + ";PTFrameId=" + $fr.PTFrameId) -SourceTable (Split-Path $framePath -Leaf)
                }

                $cpuEndDelta = Parse-Number $fr.CpuEndDeltaMs
                if ($cpuEndDelta -ne $null) {
                    Add-Metric -MetricType "WaveeFrameCpuEndDelta" -Process $fr.Process -ThreadId $fr.ThreadId -StartS $startS -DurationMs $cpuEndDelta -Details ("FrameType=" + $fr.FrameType + ";PTFrameId=" + $fr.PTFrameId) -SourceTable (Split-Path $framePath -Leaf)
                }
            }
        }
    }
    catch {
        Write-Host "Failed to parse XAML frame E2E table: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Navigation pairing from Generic Events.
#
# Source of truth: the Wavee-UI-Navigation custom EventSource
# (Wavee.UI.WinUI/Diagnostics/WaveeNavigationEventSource.cs) emits Navigating/Navigated
# pairs around every Frame.Navigate call. These are captured when traces are taken via
# measure-page-nav.ps1 (which layers scripts/wpa-profiles/WaveeCustomProviders.wprp
# onto the built-in XAMLAppResponsiveness profile).
#
# This section also acts as a fallback for system XAML provider events
# (Microsoft-Windows-XAML) on older captures that predate the custom provider - the
# regex is deliberately broad to catch either source. Pair matching is O(1) per
# (Process, ThreadId) and produces one NavigationE2E row per completed hop.
$fallbackNavigationCount = 0
if ($genericFiles.Count -gt 0) {
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($gf in $genericFiles) {
        try {
            $rows = Import-CsvSafe $gf.FullName
            foreach ($row in $rows) {
                $provider = ""
                foreach ($name in @('Provider Name','Provider')) {
                    if ($row.PSObject.Properties.Name -contains $name -and -not [string]::IsNullOrWhiteSpace($row.$name)) {
                        $provider = [string]$row.$name
                        break
                    }
                }

                $eventName = ""
                foreach ($name in @('Event Name','Task Name','Opcode Name')) {
                    if ($row.PSObject.Properties.Name -contains $name -and -not [string]::IsNullOrWhiteSpace($row.$name)) {
                        $eventName = [string]$row.$name
                        break
                    }
                }

                $proc = if ($row.PSObject.Properties.Name -contains 'Process') { [string]$row.Process } elseif ($row.PSObject.Properties.Name -contains 'Process Name') { [string]$row.'Process Name' } else { "" }
                $thread = if ($row.PSObject.Properties.Name -contains 'ThreadId') { [string]$row.ThreadId } else { "" }

                $timeS = $null
                foreach ($p in $row.PSObject.Properties) {
                    if ($p.Name -like 'Time*') {
                        $parsed = Parse-Number ([string]$p.Value)
                        if ($parsed -ne $null) { $timeS = $parsed; break }
                    }
                }

                if ($timeS -eq $null) { continue }

                $isXamlSignal = ($provider -match 'XAML') -or ($eventName -match 'Frame|Layout|Navigat')
                if (-not $isXamlSignal) { continue }

                $events.Add([pscustomobject]@{
                    Process = $proc
                    ThreadId = $thread
                    Provider = $provider
                    Event = $eventName
                    TimeS = [double]$timeS
                    Source = $gf.Name
                }) | Out-Null
            }
        }
        catch {
            Write-Host "Skipping unreadable generic events file $($gf.Name): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    if ($events.Count -gt 0) {
        $ordered = $events | Sort-Object TimeS
        $pendingNav = @{}
        foreach ($e in $ordered) {
            $key = $e.Process + '|' + $e.ThreadId
            if ($e.Event -match '(?i)Navigating') {
                $pendingNav[$key] = $e
                continue
            }
            if ($e.Event -match '(?i)Navigated' -and $pendingNav.ContainsKey($key)) {
                $start = $pendingNav[$key]
                $durMs = [math]::Round(($e.TimeS - $start.TimeS) * 1000.0, 3)
                if ($durMs -ge 0) {
                    Add-Metric -MetricType "NavigationE2E" -Process $e.Process -ThreadId $e.ThreadId -StartS $start.TimeS -StopS $e.TimeS -DurationMs $durMs -Details ("Start=" + $start.Event + ';Stop=' + $e.Event) -SourceTable $e.Source
                    $fallbackNavigationCount++
                }
                $pendingNav.Remove($key)
            }
        }
    }
}

if ($fallbackNavigationCount -gt 0) {
    Write-Host ""
    Write-Host "Fallback pairing produced $fallbackNavigationCount navigation rows from Generic Events." -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "Fallback pairing found no navigable Generic Events rows." -ForegroundColor Yellow
}

# Process lifetime context for Wavee processes
if (Test-Path $lifetimePath) {
    try {
        $lifetimes = Import-CsvSafe $lifetimePath
        $waveeProc = $lifetimes | Where-Object { $_.Process -like 'Wavee.UI.WinUI*' -or $_.Process -like 'Wavee.AudioHost*' }
        foreach ($p in $waveeProc) {
            $durS = Parse-Number $p.'Duration (s)'
            $startS = Parse-Number $p.'Start Time (s)'
            $stopS = Parse-Number $p.'End Time (s)'
            if ($durS -ne $null) {
                Add-Metric -MetricType "ProcessLifetime" -Process $p.Process -StartS $startS -StopS $stopS -DurationMs ([math]::Round($durS * 1000.0, 3)) -Details "Process lifetime in trace window" -SourceTable (Split-Path $lifetimePath -Leaf)
            }
        }
    }
    catch {
        Write-Host "Failed to parse process lifetime table: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Existing summaries for convenience
if (Test-Path $sampledPath) {
    try {
        $proc = Import-CsvSafe $sampledPath
        Write-Host ""
        Write-Host "Top sampled CPU processes (% Weight):" -ForegroundColor Cyan
        $proc | Select-Object Process, '% Weight', 'Weight (in view) (ms)' | Select-Object -First 12 | Format-Table -AutoSize
    }
    catch {
        Write-Host "Failed to parse sampled CPU table: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if (Test-Path $uiThreadPath) {
    try {
        $ui = Import-CsvSafe $uiThreadPath
        Write-Host ""
        Write-Host "XAML UI thread CPU summary:" -ForegroundColor Cyan
        $ui | Select-Object 'Thread Activity Tag', 'CPU Usage (in view) (ms)', '% CPU Usage' | Format-Table -AutoSize
    }
    catch {
        Write-Host "Failed to parse XAML UI thread table: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if (Test-Path $renderThreadPath) {
    try {
        $rt = Import-CsvSafe $renderThreadPath
        Write-Host ""
        Write-Host "XAML render thread CPU summary:" -ForegroundColor Cyan
        $rt | Select-Object 'Thread Activity Tag', 'CPU Usage (in view) (ms)', '% CPU Usage' | Format-Table -AutoSize
    }
    catch {
        Write-Host "Failed to parse XAML render thread table: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Emit normalized artifact
$metricsPath = Join-Path $OutDir "Navigation_Metrics.csv"
if ($metrics.Count -gt 0) {
    $metrics | Sort-Object MetricType, StartS | Export-Csv -Path $metricsPath -NoTypeInformation -Encoding UTF8
    Write-Host ""
    Write-Host "Normalized metrics written:" -ForegroundColor Cyan
    Write-Host "  $metricsPath" -ForegroundColor Green

    $navRows = @($metrics | Where-Object { $_.MetricType -eq 'NavigationE2E' }).Count
    $frameRowsMetric = @($metrics | Where-Object { $_.MetricType -like 'WaveeFrame*' }).Count
    $roiRowsMetric = @($metrics | Where-Object { $_.MetricType -eq 'RoiDuration' }).Count

    Write-Host ""
    Write-Host "Metric counts:" -ForegroundColor Cyan
    Write-Host "  NavigationE2E:   $navRows" -ForegroundColor Gray
    Write-Host "  WaveeFrame*:     $frameRowsMetric" -ForegroundColor Gray
    Write-Host "  RoiDuration:     $roiRowsMetric" -ForegroundColor Gray
}
else {
    Write-Host ""
    Write-Host "No metrics were extracted. Export profile is likely too aggregated." -ForegroundColor Red
    Write-Host "Recommendation: use a repo-owned WPA profile with ungrouped XAML frame + generic event views." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Analysis complete." -ForegroundColor Cyan
