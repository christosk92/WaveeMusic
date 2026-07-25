[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Exe,

    [Parameter(Mandatory = $true)]
    [string] $OutputDir,

    [ValidateSet("Startup", "Idle")]
    [string] $Mode,

    [int] $StartupRuns = 20,
    [int] $WarmupSeconds = 30,
    [int] $IdleSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Exe = (Resolve-Path -LiteralPath $Exe).Path
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]] $Values,
        [Parameter(Mandatory = $true)][double] $Percentile
    )

    if ($Values.Count -eq 0) {
        return 0.0
    }

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Round(($Percentile / 100.0) * ($sorted.Count - 1))
    $bounded = [int]$index
    if ($bounded -lt 0) { $bounded = 0 }
    if ($bounded -ge $sorted.Count) { $bounded = $sorted.Count - 1 }
    return [double]$sorted[$bounded]
}

function Stop-OwnedProcess {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process] $Process)

    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }

    $null = $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(5000)) {
        Stop-Process -Id $Process.Id -Force
        $Process.WaitForExit(5000)
    }
}

function Start-MeasuredProcess {
    param(
        [Parameter(Mandatory = $true)][string] $Label
    )

    $stdout = Join-Path $OutputDir "$Label.stdout.txt"
    $stderr = Join-Path $OutputDir "$Label.stderr.txt"
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process `
        -FilePath $Exe `
        -ArgumentList "--fake" `
        -PassThru `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr

    return [pscustomobject]@{
        Process = $process
        Timer = $timer
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Wait-ForWindow {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)][System.Diagnostics.Stopwatch] $Timer,
        [int] $TimeoutMs = 15000
    )

    while ($Timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $Process.Refresh()
        if ($Process.HasExited) {
            return $null
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [pscustomobject]@{
                ElapsedMs = $Timer.Elapsed.TotalMilliseconds
                Handle = $Process.MainWindowHandle.ToInt64()
                Title = $Process.MainWindowTitle
                Responding = $Process.Responding
            }
        }
        [System.Threading.Thread]::Sleep(1)
    }

    return $null
}

function Get-BinaryMetadata {
    $item = Get-Item -LiteralPath $Exe
    $hash = Get-FileHash -LiteralPath $Exe -Algorithm SHA256
    return [ordered]@{
        path = $item.FullName
        bytes = $item.Length
        mebibytes = [Math]::Round($item.Length / 1MB, 3)
        fileVersion = $item.VersionInfo.FileVersion
        productVersion = $item.VersionInfo.ProductVersion
        sha256 = $hash.Hash
        lastWriteUtc = $item.LastWriteTimeUtc.ToString("O")
    }
}

function Measure-Startup {
    $rows = [System.Collections.Generic.List[object]]::new()

    for ($run = 1; $run -le $StartupRuns; $run++) {
        $owned = Start-MeasuredProcess -Label ("startup-{0:D2}" -f $run)
        $window = Wait-ForWindow -Process $owned.Process -Timer $owned.Timer
        $owned.Timer.Stop()

        $valid = $null -ne $window
        $rows.Add([pscustomobject]@{
            run = $run
            cacheClass = if ($run -eq 1) { "first-run-observation" } else { "warm-filesystem-cache" }
            processId = $owned.Process.Id
            windowReadyMs = if ($valid) { [Math]::Round($window.ElapsedMs, 3) } else { $null }
            windowTitle = if ($valid) { $window.Title } else { "" }
            responding = if ($valid) { $window.Responding } else { $false }
            valid = $valid
            exitCode = if ($owned.Process.HasExited) { $owned.Process.ExitCode } else { $null }
        })

        if ($valid) {
            [System.Threading.Thread]::Sleep(500)
        }
        Stop-OwnedProcess -Process $owned.Process
        [System.Threading.Thread]::Sleep(500)
    }

    $csvPath = Join-Path $OutputDir "startup.csv"
    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csvPath

    $warm = @($rows | Where-Object { $_.valid -and $_.cacheClass -eq "warm-filesystem-cache" } | ForEach-Object { [double]$_.windowReadyMs })
    $summary = [ordered]@{
        schemaVersion = 1
        measuredAtUtc = [DateTime]::UtcNow.ToString("O")
        metric = "process-start-to-main-window-handle"
        binary = Get-BinaryMetadata
        requestedRuns = $StartupRuns
        validRuns = @($rows | Where-Object valid).Count
        firstRunMs = if ($rows[0].valid) { [double]$rows[0].windowReadyMs } else { $null }
        warmRuns = $warm.Count
        warmP50Ms = [Math]::Round((Get-Percentile -Values $warm -Percentile 50), 3)
        warmP95Ms = [Math]::Round((Get-Percentile -Values $warm -Percentile 95), 3)
        warmMinMs = if ($warm.Count) { [Math]::Round(($warm | Measure-Object -Minimum).Minimum, 3) } else { 0 }
        warmMaxMs = if ($warm.Count) { [Math]::Round(($warm | Measure-Object -Maximum).Maximum, 3) } else { 0 }
        limitations = @(
            "External process-start to first non-zero MainWindowHandle; not first present or interactive content.",
            "The first run is a first-run observation, not a statistically controlled cold-cache result.",
            "All later runs retain the operating-system filesystem cache."
        )
    }
    $summaryPath = Join-Path $OutputDir "startup-summary.json"
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $summaryPath
    $summary | ConvertTo-Json -Depth 8
}

function Measure-Idle {
    $owned = Start-MeasuredProcess -Label "idle"
    $window = Wait-ForWindow -Process $owned.Process -Timer $owned.Timer
    if ($null -eq $window) {
        Stop-OwnedProcess -Process $owned.Process
        throw "Wavee did not create a main window within the timeout."
    }

    Start-Sleep -Seconds $WarmupSeconds
    $process = $owned.Process
    $process.Refresh()
    $previousCpu = $process.TotalProcessorTime
    $previousTick = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $logicalProcessors = [Math]::Max(1, [Environment]::ProcessorCount)
    $rows = [System.Collections.Generic.List[object]]::new()

    for ($second = 1; $second -le $IdleSeconds; $second++) {
        Start-Sleep -Seconds 1
        $process.Refresh()
        if ($process.HasExited) {
            break
        }

        $nowTick = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $nowCpu = $process.TotalProcessorTime
        $wallMs = ($nowTick - $previousTick) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency
        $cpuMs = ($nowCpu - $previousCpu).TotalMilliseconds
        $cpuPct = if ($wallMs -gt 0) { $cpuMs / ($wallMs * $logicalProcessors) * 100.0 } else { 0.0 }
        $previousTick = $nowTick
        $previousCpu = $nowCpu

        $rows.Add([pscustomobject]@{
            elapsedSec = $second
            cpuPct = [Math]::Round($cpuPct, 4)
            workingSetMB = [Math]::Round($process.WorkingSet64 / 1MB, 3)
            privateMB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 3)
            handles = $process.HandleCount
            threads = $process.Threads.Count
            responding = $process.Responding
        })
    }

    Stop-OwnedProcess -Process $process
    $csvPath = Join-Path $OutputDir "idle.csv"
    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csvPath

    $cpu = @($rows | ForEach-Object { [double]$_.cpuPct })
    $ws = @($rows | ForEach-Object { [double]$_.workingSetMB })
    $private = @($rows | ForEach-Object { [double]$_.privateMB })
    $summary = [ordered]@{
        schemaVersion = 1
        measuredAtUtc = [DateTime]::UtcNow.ToString("O")
        metric = "loaded-fake-home-idle"
        binary = Get-BinaryMetadata
        warmupSeconds = $WarmupSeconds
        requestedSampleSeconds = $IdleSeconds
        capturedSamples = $rows.Count
        logicalProcessors = $logicalProcessors
        cpuNormalization = "100 percent equals all logical processors busy"
        cpuAvgPct = [Math]::Round(($cpu | Measure-Object -Average).Average, 4)
        cpuP95Pct = [Math]::Round((Get-Percentile -Values $cpu -Percentile 95), 4)
        cpuMaxPct = [Math]::Round(($cpu | Measure-Object -Maximum).Maximum, 4)
        workingSetAvgMB = [Math]::Round(($ws | Measure-Object -Average).Average, 3)
        workingSetP95MB = [Math]::Round((Get-Percentile -Values $ws -Percentile 95), 3)
        workingSetMinMB = [Math]::Round(($ws | Measure-Object -Minimum).Minimum, 3)
        workingSetMaxMB = [Math]::Round(($ws | Measure-Object -Maximum).Maximum, 3)
        privateAvgMB = [Math]::Round(($private | Measure-Object -Average).Average, 3)
        privateP95MB = [Math]::Round((Get-Percentile -Values $private -Percentile 95), 3)
        allSamplesResponding = @($rows | Where-Object { -not $_.responding }).Count -eq 0
        limitations = @(
            "Current FluentGPU NativeAOT binary only; not a WinUI comparison.",
            "Fake-data Home surface with no playback and no user input.",
            "Per-process CPU and memory; other system activity can still affect scheduling."
        )
    }
    $summaryPath = Join-Path $OutputDir "idle-summary.json"
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $summaryPath
    $summary | ConvertTo-Json -Depth 8
}

switch ($Mode) {
    "Startup" { Measure-Startup }
    "Idle" { Measure-Idle }
}
