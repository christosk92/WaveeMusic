# analyze-native-crash-dump.ps1
#
# Headless native crash-dump triage for Wavee using cdb.exe.
# Resolves the newest dump by default, runs !analyze -v with public symbols,
# extracts a compact summary, and can compare the latest N dumps.

param(
    [string]$Dump = "",
    [string]$CrashDumpsDir = "$env:LOCALAPPDATA\CrashDumps",
    [string]$ProcessPattern = "Wavee.UI.WinUI.exe.*.dmp",
    [int]$LatestCount = 1,
    [string]$OutDir = "",
    [string]$CdbPath = "",
    [string]$SymbolCacheDir = "C:\symbols"
)

$ErrorActionPreference = "Stop"

function Resolve-CdbPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (Test-Path $RequestedPath) {
            return (Get-Item $RequestedPath).FullName
        }

        throw "cdb.exe not found: $RequestedPath"
    }

    $candidates = @(
        "C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe",
        "C:\Program Files (x86)\Windows Kits\10\Debuggers\arm64\cdb.exe",
        "C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe",
        "C:\Program Files\Windows Kits\10\Debuggers\arm64\cdb.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Get-Item $candidate).FullName
        }
    }

    $command = Get-Command cdb.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        return $command.Source
    }

    $message = @"
cdb.exe was not found.

Install one of these, then rerun this script:
  1. Windows SDK -> Debugging Tools for Windows
     Expected paths include:
       C:\Program Files (x86)\Windows Kits\10\Debuggers\arm64\cdb.exe
       C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe
  2. Or pass -CdbPath <full path to cdb.exe>

After cdb is installed, this script will run native stack analysis automatically.
"@
    throw $message
}

function Resolve-Dumps {
    param(
        [string]$RequestedDump,
        [string]$SearchDir,
        [string]$Pattern,
        [int]$Count
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedDump)) {
        if (-not (Test-Path $RequestedDump)) {
            throw "Dump file not found: $RequestedDump"
        }

        return @((Get-Item $RequestedDump).FullName)
    }

    if (-not (Test-Path $SearchDir)) {
        throw "Crash dump directory not found: $SearchDir"
    }

    if ($Count -lt 1) {
        throw "-LatestCount must be at least 1"
    }

    $dumps = Get-ChildItem $SearchDir -Filter $Pattern -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $Count

    if ($null -eq $dumps -or $dumps.Count -eq 0) {
        throw "No dumps matching '$Pattern' found in $SearchDir"
    }

    return @($dumps.FullName)
}

function Invoke-CdbAnalysis {
    param(
        [string]$DebuggerPath,
        [string]$DumpPath,
        [string]$OutputPath,
        [string]$SymbolCache
    )

    New-Item -ItemType Directory -Force -Path $SymbolCache | Out-Null

    $outputDir = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    $stdoutPath = Join-Path $outputDir "cdb-stdout.txt"
    $stderrPath = Join-Path $outputDir "cdb-stderr.txt"

    $commands = @(
        ".reload /f",
        "!analyze -v",
        ".ecxr",
        "kb",
        "kv",
        "lmvm dcompi",
        "lmvm dwmcorei",
        "q"
    ) -join "; "

    $symbolPath = "srv*$SymbolCache*https://msdl.microsoft.com/download/symbols"
    $quotedDumpPath = '"' + $DumpPath + '"'
    $quotedSymbolPath = '"' + $symbolPath + '"'
    $quotedCommands = '"' + $commands.Replace('"', '\"') + '"'
    $arguments = "-z $quotedDumpPath -lines -y $quotedSymbolPath -c $quotedCommands"

    if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }
    if (Test-Path $stderrPath) { Remove-Item $stderrPath -Force }
    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }

    $process = Start-Process -FilePath $DebuggerPath `
        -ArgumentList $arguments `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -NoNewWindow `
        -PassThru `
        -Wait

    $stdoutLines = if (Test-Path $stdoutPath) { Get-Content $stdoutPath } else { @() }
    $stderrLines = if (Test-Path $stderrPath) { Get-Content $stderrPath } else { @() }

    $combined = New-Object System.Collections.Generic.List[string]
    if ($stdoutLines.Count -gt 0) {
        foreach ($line in $stdoutLines) { $combined.Add($line) | Out-Null }
    }
    if ($stderrLines.Count -gt 0) {
        if ($combined.Count -gt 0) {
            $combined.Add("") | Out-Null
            $combined.Add("==== STDERR ====") | Out-Null
        }
        foreach ($line in $stderrLines) { $combined.Add($line) | Out-Null }
    }

    $combined | Out-File -FilePath $OutputPath -Encoding utf8

    if ($process.ExitCode -ne 0) {
        throw "cdb.exe failed for $DumpPath (exit code $($process.ExitCode)). See $OutputPath"
    }

    return $combined
}

function Get-ValueAfterLabel {
    param(
        [string[]]$Lines,
        [string]$Label
    )

    $line = $Lines | Select-String -Pattern ("^\s*" + [regex]::Escape($Label) + "\s*:\s*(.+)$") | Select-Object -First 1
    if ($null -eq $line) {
        return $null
    }

    return $line.Matches[0].Groups[1].Value.Trim()
}

function Get-SectionLines {
    param(
        [string[]]$Lines,
        [string]$Header
    )

    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match ("^\s*" + [regex]::Escape($Header) + "\s*:")) {
            $start = $i + 1
            break
        }
    }

    if ($start -lt 0) {
        return @()
    }

    $results = New-Object System.Collections.Generic.List[string]
    for ($i = $start; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($results.Count -gt 0) {
                break
            }
            continue
        }

        if ($line -match '^[A-Z0-9_ ]+\s*:') {
            break
        }

        $results.Add($line.TrimEnd()) | Out-Null
    }

    return $results
}

function Write-DumpSummary {
    param(
        [string]$DumpPath,
        [string]$RawOutputPath,
        [string]$SummaryPath
    )

    $lines = Get-Content $RawOutputPath
    $exceptionCode = Get-ValueAfterLabel -Lines $lines -Label "EXCEPTION_CODE"
    $faultingModule = Get-ValueAfterLabel -Lines $lines -Label "MODULE_NAME"
    $symbolName = Get-ValueAfterLabel -Lines $lines -Label "SYMBOL_NAME"
    $failureBucket = Get-ValueAfterLabel -Lines $lines -Label "FAILURE_BUCKET_ID"
    $stackText = Get-SectionLines -Lines $lines -Header "STACK_TEXT"

    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("Native crash dump analysis summary") | Out-Null
    $summary.Add("=================================") | Out-Null
    $summary.Add("") | Out-Null
    $summary.Add("Dump: $DumpPath") | Out-Null
    $summary.Add("RawAnalysis: $RawOutputPath") | Out-Null
    $summary.Add("EXCEPTION_CODE: $exceptionCode") | Out-Null
    $summary.Add("MODULE_NAME: $faultingModule") | Out-Null
    $summary.Add("SYMBOL_NAME: $symbolName") | Out-Null
    $summary.Add("FAILURE_BUCKET_ID: $failureBucket") | Out-Null
    $summary.Add("") | Out-Null
    $summary.Add("STACK_TEXT:") | Out-Null

    if ($stackText.Count -eq 0) {
        $summary.Add("  (not found)") | Out-Null
    }
    else {
        foreach ($line in $stackText | Select-Object -First 30) {
            $summary.Add("  $line") | Out-Null
        }
    }

    $summary | Out-File -FilePath $SummaryPath -Encoding utf8

    return [pscustomobject]@{
        DumpPath = $DumpPath
        ExceptionCode = $exceptionCode
        ModuleName = $faultingModule
        SymbolName = $symbolName
        FailureBucket = $failureBucket
        SummaryPath = $SummaryPath
    }
}

$resolvedCdb = Resolve-CdbPath -RequestedPath $CdbPath
$resolvedDumps = Resolve-Dumps -RequestedDump $Dump -SearchDir $CrashDumpsDir -Pattern $ProcessPattern -Count $LatestCount

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) "perf-traces\native-crash-analysis-$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host ""
Write-Host "===== Native crash analysis =====" -ForegroundColor Cyan
Write-Host "cdb.exe: $resolvedCdb" -ForegroundColor Gray
Write-Host "Output:  $OutDir" -ForegroundColor Gray
Write-Host "Dumps:   $($resolvedDumps.Count)" -ForegroundColor Gray
Write-Host ""

$results = New-Object System.Collections.Generic.List[object]

foreach ($dumpPath in $resolvedDumps) {
    $dumpName = [IO.Path]::GetFileNameWithoutExtension($dumpPath)
    $dumpOutDir = Join-Path $OutDir $dumpName
    New-Item -ItemType Directory -Force -Path $dumpOutDir | Out-Null

    $rawOutputPath = Join-Path $dumpOutDir "cdb-output.txt"
    $summaryPath = Join-Path $dumpOutDir "summary.txt"

    Write-Host "Analyzing $dumpName ..." -ForegroundColor Yellow
    Invoke-CdbAnalysis -DebuggerPath $resolvedCdb -DumpPath $dumpPath -OutputPath $rawOutputPath -SymbolCache $SymbolCacheDir | Out-Null
    $result = Write-DumpSummary -DumpPath $dumpPath -RawOutputPath $rawOutputPath -SummaryPath $summaryPath
    $results.Add($result) | Out-Null
}

$indexSummaryPath = Join-Path $OutDir "index-summary.txt"
$indexSummary = New-Object System.Collections.Generic.List[string]
$indexSummary.Add("Native crash dump comparison") | Out-Null
$indexSummary.Add("============================") | Out-Null
$indexSummary.Add("") | Out-Null

foreach ($result in $results) {
    $indexSummary.Add("Dump: $($result.DumpPath)") | Out-Null
    $indexSummary.Add("  EXCEPTION_CODE: $($result.ExceptionCode)") | Out-Null
    $indexSummary.Add("  MODULE_NAME: $($result.ModuleName)") | Out-Null
    $indexSummary.Add("  SYMBOL_NAME: $($result.SymbolName)") | Out-Null
    $indexSummary.Add("  FAILURE_BUCKET_ID: $($result.FailureBucket)") | Out-Null
    $indexSummary.Add("  Summary: $($result.SummaryPath)") | Out-Null
    $indexSummary.Add("") | Out-Null
}

$indexSummary | Out-File -FilePath $indexSummaryPath -Encoding utf8

Write-Host "Index summary: $indexSummaryPath" -ForegroundColor Green
