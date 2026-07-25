[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Exe,

    [Parameter(Mandatory = $true)]
    [string] $RawDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$RawDir = (Resolve-Path -LiteralPath $RawDir).Path
$dataDir = Join-Path $root "benchmark-data"
$publicRawDir = Join-Path $dataDir "raw\2026-07-26-nativeaot"
New-Item -ItemType Directory -Force -Path $publicRawDir | Out-Null

$startupSummary = Get-Content -Raw -Encoding UTF8 (Join-Path $RawDir "startup-summary.json") | ConvertFrom-Json
$idleSummary = Get-Content -Raw -Encoding UTF8 (Join-Path $RawDir "idle-summary.json") | ConvertFrom-Json
$idleRows = @(Import-Csv (Join-Path $RawDir "idle.csv"))
$censusText = Get-Content -Raw -Encoding UTF8 (Join-Path $RawDir "gpu-mem-idle.stderr.txt")

function Match-Last {
    param(
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    $matches = [regex]::Matches($censusText, $Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -eq 0) {
        throw "Could not match census pattern: $Pattern"
    }
    return $matches[$matches.Count - 1]
}

function Average-Property {
    param(
        [Parameter(Mandatory = $true)][object[]] $Rows,
        [Parameter(Mandatory = $true)][string] $Property
    )
    return [double](($Rows | Measure-Object -Property $Property -Average).Average)
}

$gc = Match-Last "^\s*gc\s+heap=(?<heap>[0-9.]+)MB committed=(?<committed>[0-9.]+)MB"
$proc = Match-Last "^\s*proc\s+workingSet=(?<working>[0-9.]+)MB"
$images = Match-Last "^\s*images\s+count=(?<count>\d+) ready=(?<ready>\d+) pending=(?<pending>\d+) used=(?<used>[0-9.]+)MB"
$pixelPool = Match-Last "^\s*pixpool\s+retained=(?<retained>[0-9.]+)MB peak=(?<peak>[0-9.]+)MB cap=(?<cap>[0-9.]+)MB"
$gpu = Match-Last "^\s*gpu\s+bytes=(?<bytes>[0-9.]+)MB count=(?<count>\d+)"
$allocationMatches = [regex]::Matches(
    $censusText,
    "^\s*gc\s+heap=[0-9.]+MB committed=[0-9.]+MB.* alloc=(?<rate>[0-9.]+)KB/s",
    [Text.RegularExpressions.RegexOptions]::Multiline)
$steadyAllocationRates = @(
    $allocationMatches |
        Select-Object -Skip 1 |
        ForEach-Object { [double]$_.Groups["rate"].Value }
)
$sortedAllocationRates = @($steadyAllocationRates | Sort-Object)
$allocationMedian = if ($sortedAllocationRates.Count -eq 0) {
    0.0
} elseif (($sortedAllocationRates.Count % 2) -eq 1) {
    $sortedAllocationRates[[int]($sortedAllocationRates.Count / 2)]
} else {
    ($sortedAllocationRates[$sortedAllocationRates.Count / 2 - 1] + $sortedAllocationRates[$sortedAllocationRates.Count / 2]) / 2.0
}

$first30 = @($idleRows | Select-Object -First 30)
$last30 = @($idleRows | Select-Object -Last 30)
$first30Ws = Average-Property -Rows $first30 -Property "workingSetMB"
$last30Ws = Average-Property -Rows $last30 -Property "workingSetMB"
$cpuOneCorePct = [double]$idleSummary.cpuAvgPct * [double]$idleSummary.logicalProcessors
$totalCpuMs = [double]$idleSummary.cpuAvgPct / 100.0 * [double]$idleSummary.logicalProcessors * [double]$idleSummary.capturedSamples * 1000.0

$binary = Get-Item -LiteralPath $Exe
$hash = Get-FileHash -LiteralPath $Exe -Algorithm SHA256
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$gpuAdapter = Get-CimInstance Win32_VideoController | Where-Object CurrentRefreshRate | Select-Object -First 1
$power = (powercfg /getactivescheme | Out-String).Trim()
$otherWavee = @(
    Get-Process Wavee -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Exe }
)

$result = [ordered]@{
    schemaVersion = 1
    measuredAtUtc = $idleSummary.measuredAtUtc
    status = "current-binary snapshot; not a WinUI comparison"
    binary = [ordered]@{
        productVersion = $binary.VersionInfo.ProductVersion
        fileVersion = $binary.VersionInfo.FileVersion
        architecture = "win-arm64"
        nativeAot = $true
        bytes = $binary.Length
        mebibytes = [Math]::Round($binary.Length / 1MB, 3)
        sha256 = $hash.Hash
        lastWriteUtc = $binary.LastWriteTimeUtc.ToString("O")
    }
    environment = [ordered]@{
        os = $os.Caption
        osBuild = $os.BuildNumber
        cpu = $cpu.Name
        physicalCores = $cpu.NumberOfCores
        logicalProcessors = $cpu.NumberOfLogicalProcessors
        totalMemoryMiB = [Math]::Round($os.TotalVisibleMemorySize / 1024.0, 0)
        gpu = $gpuAdapter.Name
        gpuDriver = $gpuAdapter.DriverVersion
        display = "$($gpuAdapter.CurrentHorizontalResolution)x$($gpuAdapter.CurrentVerticalResolution)"
        displayHz = $gpuAdapter.CurrentRefreshRate
        powerScheme = $power
        umaIntegratedGpu = $true
        anotherWaveeInstancePresentDuringCapture = $otherWavee.Count -gt 0
    }
    startupToWindow = [ordered]@{
        requestedRuns = $startupSummary.requestedRuns
        validRuns = $startupSummary.validRuns
        firstRunObservationMs = $startupSummary.firstRunMs
        warmRuns = $startupSummary.warmRuns
        warmP50Ms = $startupSummary.warmP50Ms
        warmP95Ms = $startupSummary.warmP95Ms
        warmMinMs = $startupSummary.warmMinMs
        warmMaxMs = $startupSummary.warmMaxMs
        metric = "external process start to first non-zero main-window handle"
        scope = "Window readiness only; not first present or interactive content."
    }
    fiveMinuteIdle = [ordered]@{
        surface = "fake-data Home; no playback or user input"
        warmupSeconds = $idleSummary.warmupSeconds
        samples = $idleSummary.capturedSamples
        sampleIntervalSeconds = 1
        allSamplesResponding = $idleSummary.allSamplesResponding
        cpuTotalCapacityAveragePct = $idleSummary.cpuAvgPct
        cpuTotalCapacityP95Pct = $idleSummary.cpuP95Pct
        cpuTotalCapacityMaxPct = $idleSummary.cpuMaxPct
        cpuOneCoreEquivalentAveragePct = [Math]::Round($cpuOneCorePct, 4)
        totalCpuTimeMs = [Math]::Round($totalCpuMs, 1)
        workingSetAverageMiB = $idleSummary.workingSetAvgMB
        workingSetP95MiB = $idleSummary.workingSetP95MB
        workingSetMinMiB = $idleSummary.workingSetMinMB
        workingSetMaxMiB = $idleSummary.workingSetMaxMB
        workingSetFirst30AverageMiB = [Math]::Round($first30Ws, 3)
        workingSetLast30AverageMiB = [Math]::Round($last30Ws, 3)
        workingSetFirstToLast30DeltaMiB = [Math]::Round($last30Ws - $first30Ws, 3)
        privateAverageMiB = $idleSummary.privateAvgMB
        privateP95MiB = $idleSummary.privateP95MB
    }
    gpuAwareIdleCensus = [ordered]@{
        separateInstrumentedRun = $true
        managedHeapMiB = [double]$gc.Groups["heap"].Value
        managedCommittedMiB = [double]$gc.Groups["committed"].Value
        processWorkingSetMiB = [double]$proc.Groups["working"].Value
        trackedD3d12ResourcesMiB = [double]$gpu.Groups["bytes"].Value
        trackedD3d12ResourceCount = [int]$gpu.Groups["count"].Value
        imageCacheCount = [int]$images.Groups["count"].Value
        imageCacheReady = [int]$images.Groups["ready"].Value
        imageCacheUsedMiB = [double]$images.Groups["used"].Value
        pixelPoolRetainedMiB = [double]$pixelPool.Groups["retained"].Value
        pixelPoolPeakMiB = [double]$pixelPool.Groups["peak"].Value
        note = "On a UMA/iGPU system, working set and tracked graphics resources are overlapping residency views, not additive buckets."
    }
    instrumentedIdleAllocation = [ordered]@{
        samplesAfterStartup = $steadyAllocationRates.Count
        medianKiBPerSec = [Math]::Round($allocationMedian, 3)
        minKiBPerSec = if ($steadyAllocationRates.Count) { [Math]::Round(($steadyAllocationRates | Measure-Object -Minimum).Minimum, 3) } else { 0 }
        maxKiBPerSec = if ($steadyAllocationRates.Count) { [Math]::Round(($steadyAllocationRates | Measure-Object -Maximum).Maximum, 3) } else { 0 }
        scope = "Whole-process allocation rate reported by FG_MEM_DIAG after startup."
        limitation = "The memory census prints a report every five seconds and therefore contributes to the measured allocation rate; treat this as an instrumented upper bound."
    }
    reliability = [ordered]@{
        successfulWindowLaunches = $startupSummary.validRuns
        attemptedWindowLaunches = $startupSummary.requestedRuns
        idleRespondingSamples = $idleSummary.capturedSamples
        idleRequestedSamples = $idleSummary.requestedSampleSeconds
        startupErrorLogMatches = 0
        idleErrorLogMatches = 0
    }
    rejectedCaptures = @(
        [ordered]@{
            name = "1,000-navigation memory soak"
            reason = "Completed 1,000 synthetic navigations in 1.1 seconds without a render-thread quiescence barrier; working set is a stress high-water mark, not settled retention."
        },
        [ordered]@{
            name = "Home scroll specialist probe"
            reason = "Captured only three painted frames; Home reported no net movement and Liked Songs exposed no viewport."
        },
        [ordered]@{
            name = "dotnet-counters idle allocation"
            reason = "The external counter process did not complete or emit a CSV against this NativeAOT process."
        }
    )
    publicationNotes = @(
        "Use warm startup p50/p95 as process-to-window readiness, never as first content or first present.",
        "State CPU both as total 12-core capacity and one-core equivalent.",
        "Working set includes CPU, native, shared, mapped, driver, and graphics residency on UMA.",
        "The executable size is not a signed MSIX download or installed-footprint measurement.",
        "No WinUI-versus-FluentGPU improvement ratio is supported by this current-only snapshot."
    )
}

$jsonPath = Join-Path $dataDir "fluentgpu-binary-2026-07-26.json"
$result | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -Path $jsonPath

Copy-Item -LiteralPath (Join-Path $RawDir "startup.csv") -Destination (Join-Path $publicRawDir "startup.csv") -Force
Copy-Item -LiteralPath (Join-Path $RawDir "idle.csv") -Destination (Join-Path $publicRawDir "idle.csv") -Force
Copy-Item -LiteralPath (Join-Path $RawDir "gpu-mem-idle.stderr.txt") -Destination (Join-Path $publicRawDir "gpu-memory-census.txt") -Force

Write-Host $jsonPath
Write-Host $publicRawDir
