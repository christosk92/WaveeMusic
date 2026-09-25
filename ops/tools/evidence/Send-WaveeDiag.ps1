#requires -Version 5.1
<#
.SYNOPSIS
  Hand a wavee:// verb to ONE Wavee window by WM_COPYDATA and (for wavee://diag) wait for its reply file.

.DESCRIPTION
  docs/plans/evidence-diagnostics-implementation.md §B/§C (in ..\fluent-gpu). The window is named by handle or by process
  id — never looked up by class (the owner's Wavee has the same window class). wavee://diag verbs are developer-only and
  answer with files: one line per command in <profile>\logs\evidence\replies.tsv (seq, pid, cmd, result, path); a bundle
  is also appended to index.txt. Navigation verbs (wavee://open?route=…) have no reply.

.EXAMPLE
  .\Send-WaveeDiag.ps1 -ProcessId 1234 -EvidenceRoot C:\wavee\verify-profile\logs\evidence -Uri 'wavee://diag?cmd=bundle&tag=rest'
#>
[CmdletBinding()]
param(
    [IntPtr]$Hwnd = [IntPtr]::Zero,
    [int]$ProcessId = 0,
    [Parameter(Mandatory)][string]$Uri,
    [string]$EvidenceRoot,
    [int]$TimeoutMs = 8000
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Evidence.psm1') -Force -DisableNameChecking
if ($Hwnd -eq [IntPtr]::Zero) {
    if ($ProcessId -le 0) { throw 'pass -Hwnd or -ProcessId' }
    $Hwnd = Wait-WindowByPid -ProcessId $ProcessId -TimeoutSec 10
}
if ($Uri -like 'wavee://diag*') {
    if (-not $EvidenceRoot) { throw 'a wavee://diag verb needs -EvidenceRoot (its reply is a file)' }
    Invoke-WaveeDiag -Hwnd $Hwnd -Uri $Uri -EvidenceRoot $EvidenceRoot -TimeoutMs $TimeoutMs
}
else {
    Send-WaveeUri -Hwnd $Hwnd -Uri $Uri
    [pscustomobject]@{ Sent = $Uri }
}
