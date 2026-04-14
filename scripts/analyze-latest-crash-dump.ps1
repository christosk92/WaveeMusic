# analyze-latest-crash-dump.ps1
#
# Headless triage for the newest Wavee WinUI crash dump using dotnet-dump.
# Produces raw artifacts plus a short summary that tells us whether the crash
# is attributable from managed frames or is native-only from dotnet-dump's view.

param(
    [string]$Dump = "",
    [string]$CrashDumpsDir = "$env:LOCALAPPDATA\CrashDumps",
    [string]$ProcessPattern = "Wavee.UI.WinUI.exe.*.dmp",
    [string]$OutDir = "",
    [string]$DotnetDumpPath = ""
)

$ErrorActionPreference = "Stop"

function Resolve-DotnetDumpPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (Test-Path $RequestedPath) {
            return (Get-Item $RequestedPath).FullName
        }

        throw "dotnet-dump not found: $RequestedPath"
    }

    $candidates = @(
        "$HOME\.dotnet\tools\dotnet-dump.exe",
        (Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-dump.exe")
    ) | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return (Get-Item $candidate).FullName
        }
    }

    $command = Get-Command dotnet-dump -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        return $command.Source
    }

    throw "dotnet-dump was not found. Install it with: dotnet tool install --global dotnet-dump"
}

function Resolve-DumpPath {
    param(
        [string]$RequestedDump,
        [string]$SearchDir,
        [string]$Pattern
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedDump)) {
        if (Test-Path $RequestedDump) {
            return (Get-Item $RequestedDump).FullName
        }

        throw "Dump file not found: $RequestedDump"
    }

    if (-not (Test-Path $SearchDir)) {
        throw "Crash dump directory not found: $SearchDir"
    }

    $latest = Get-ChildItem $SearchDir -Filter $Pattern -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        throw "No dumps matching '$Pattern' found in $SearchDir"
    }

    return $latest.FullName
}

function Invoke-DotnetDumpCommand {
    param(
        [string]$ToolPath,
        [string]$DumpPath,
        [string[]]$Commands,
        [string]$OutputPath
    )

    $invokeArgs = @("analyze", $DumpPath)
    foreach ($command in $Commands) {
        $invokeArgs += @("-c", $command)
    }
    $invokeArgs += @("-c", "exit")

    $output = & $ToolPath @invokeArgs 2>&1
    $output | Out-File -FilePath $OutputPath -Encoding utf8

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet-dump analyze failed for $DumpPath (exit code $LASTEXITCODE). See $OutputPath"
    }

    return $output
}

function Get-InterestingManagedFrames {
    param([string[]]$ClrStackLines)

    $patterns = @(
        'Wavee\.UI\.WinUI\.',
        'CommunityToolkit',
        'Microsoft\.UI\.Xaml',
        'ElementCompositionPreview',
        'CanvasComposition',
        'StartAnimation',
        'Storyboard'
    )

    $matches = $ClrStackLines | Select-String -Pattern $patterns -SimpleMatch:$false
    $results = New-Object System.Collections.Generic.List[string]

    foreach ($match in $matches) {
        $line = $match.Line.TrimEnd()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '^\s*Loading core dump:') {
            continue
        }

        if ($results.Contains($line)) {
            continue
        }

        $results.Add($line) | Out-Null
    }

    return $results
}

function Get-AppManagedFrames {
    param([string[]]$ClrStackLines)

    $matches = $ClrStackLines | Select-String -Pattern 'Wavee\.UI\.WinUI\.' -SimpleMatch:$false
    $results = New-Object System.Collections.Generic.List[string]

    foreach ($match in $matches) {
        $line = $match.Line.TrimEnd()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '^\s*Loading core dump:') {
            continue
        }

        if ($line -match 'Wavee\.UI\.WinUI\.Program\.Main') {
            continue
        }

        if ($results.Contains($line)) {
            continue
        }

        $results.Add($line) | Out-Null
    }

    return $results
}

function Get-ModuleHits {
    param([string[]]$ModuleLines)

    $patterns = @(
        'dcompi\.dll',
        'dwmcorei\.dll',
        'wuceffectsi\.dll',
        'Microsoft\.UI\.Xaml',
        'Microsoft\.WindowsAppRuntime',
        'CommunityToolkit',
        'ComputeSharp',
        'Vortice',
        'Spout'
    )

    $matches = $ModuleLines | Select-String -Pattern $patterns -SimpleMatch:$false
    $results = New-Object System.Collections.Generic.List[string]

    foreach ($match in $matches) {
        $line = $match.Line.TrimEnd()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($results.Contains($line)) {
            continue
        }

        $results.Add($line) | Out-Null
    }

    return $results
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedDump = Resolve-DumpPath -RequestedDump $Dump -SearchDir $CrashDumpsDir -Pattern $ProcessPattern
$resolvedDotnetDump = Resolve-DotnetDumpPath -RequestedPath $DotnetDumpPath
$dumpItem = Get-Item $resolvedDump

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $name = [IO.Path]::GetFileNameWithoutExtension($resolvedDump)
    $OutDir = Join-Path $repoRoot "perf-traces\crash-analysis-$name-$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$threadsPath = Join-Path $OutDir "01-clrthreads.txt"
$exceptionPath = Join-Path $OutDir "02-exception.txt"
$clrStackPath = Join-Path $OutDir "03-clrstack-all.txt"
$modulesPath = Join-Path $OutDir "04-modules.txt"
$summaryPath = Join-Path $OutDir "summary.txt"

Write-Host ""
Write-Host "===== Analyzing crash dump =====" -ForegroundColor Cyan
Write-Host "Dump:        $resolvedDump" -ForegroundColor Gray
Write-Host "Updated:     $($dumpItem.LastWriteTime)" -ForegroundColor Gray
Write-Host ("Size:        {0:N1} MB" -f ($dumpItem.Length / 1MB)) -ForegroundColor Gray
Write-Host "dotnet-dump: $resolvedDotnetDump" -ForegroundColor Gray
Write-Host "Output:      $OutDir" -ForegroundColor Gray
Write-Host ""

$threadsOutput = Invoke-DotnetDumpCommand -ToolPath $resolvedDotnetDump -DumpPath $resolvedDump -Commands @("clrthreads") -OutputPath $threadsPath
$exceptionOutput = Invoke-DotnetDumpCommand -ToolPath $resolvedDotnetDump -DumpPath $resolvedDump -Commands @("pe -nested") -OutputPath $exceptionPath
$clrStackOutput = Invoke-DotnetDumpCommand -ToolPath $resolvedDotnetDump -DumpPath $resolvedDump -Commands @("clrstack -all") -OutputPath $clrStackPath
$modulesOutput = Invoke-DotnetDumpCommand -ToolPath $resolvedDotnetDump -DumpPath $resolvedDump -Commands @("modules") -OutputPath $modulesPath

$interestingFrames = Get-InterestingManagedFrames -ClrStackLines $clrStackOutput
$appFrames = Get-AppManagedFrames -ClrStackLines $clrStackOutput
$moduleHits = Get-ModuleHits -ModuleLines $modulesOutput

$classification = if ($appFrames.Count -gt 0) {
    "Managed-attributable"
}
else {
    "Native-only from dotnet-dump's perspective"
}

$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("Crash dump analysis summary") | Out-Null
$summaryLines.Add("===========================") | Out-Null
$summaryLines.Add("") | Out-Null
$summaryLines.Add("Dump: $resolvedDump") | Out-Null
$summaryLines.Add("LastWriteTime: $($dumpItem.LastWriteTime)") | Out-Null
$summaryLines.Add(("SizeMB: {0:N1}" -f ($dumpItem.Length / 1MB))) | Out-Null
$summaryLines.Add("Classification: $classification") | Out-Null
$summaryLines.Add("") | Out-Null
$summaryLines.Add("App managed frames:") | Out-Null
if ($appFrames.Count -eq 0) {
    $summaryLines.Add("  (none beyond Program.Main)") | Out-Null
}
else {
    foreach ($frame in $appFrames) {
        $summaryLines.Add("  $frame") | Out-Null
    }
}

$summaryLines.Add("") | Out-Null
$summaryLines.Add("Interesting managed frames:") | Out-Null
if ($interestingFrames.Count -eq 0) {
    $summaryLines.Add("  (none)") | Out-Null
}
else {
    foreach ($frame in $interestingFrames | Select-Object -First 25) {
        $summaryLines.Add("  $frame") | Out-Null
    }
}

$summaryLines.Add("") | Out-Null
$summaryLines.Add("Relevant modules:") | Out-Null
if ($moduleHits.Count -eq 0) {
    $summaryLines.Add("  (none)") | Out-Null
}
else {
    foreach ($module in $moduleHits | Select-Object -First 40) {
        $summaryLines.Add("  $module") | Out-Null
    }
}

$summaryLines.Add("") | Out-Null
$summaryLines.Add("Artifacts:") | Out-Null
$summaryLines.Add("  $threadsPath") | Out-Null
$summaryLines.Add("  $exceptionPath") | Out-Null
$summaryLines.Add("  $clrStackPath") | Out-Null
$summaryLines.Add("  $modulesPath") | Out-Null

$summaryLines | Out-File -FilePath $summaryPath -Encoding utf8

Write-Host "Classification: $classification" -ForegroundColor Yellow
Write-Host ""
Write-Host "App managed frames:" -ForegroundColor Cyan
if ($appFrames.Count -eq 0) {
    Write-Host "  (none beyond Program.Main)" -ForegroundColor Gray
}
else {
    foreach ($frame in $appFrames | Select-Object -First 12) {
        Write-Host "  $frame" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "Relevant modules:" -ForegroundColor Cyan
foreach ($module in $moduleHits | Select-Object -First 12) {
    Write-Host "  $module" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Summary: $summaryPath" -ForegroundColor Green
