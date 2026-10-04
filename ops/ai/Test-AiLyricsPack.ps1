#Requires -Version 5.1
<#
    Smoke check of the hosted AI lyrics pack against its manifest (ops/ai/lyrics-pack.v1.json). Read-only:
    it only sends HEAD and GET requests, it never uploads or changes anything.

    Default (seconds, a few KB of traffic):
      every model part   HEAD -> 200, Content-Length == bytes, Cache-Control contains "immutable";
                         GET Range: bytes=0-1023 -> 206 with a Content-Range for the right total
      every wheel        HEAD -> 200, Content-Length == bytes
    -Full (about 1.4 GB): additionally streams every object and checks the SHA-256 of each part, of each
    reassembled model file and of each wheel.

    Prints a table and exits 1 when anything does not match, 0 when everything does.
    Docs: docs/guide/ai-lyrics-pack.md

    Usage:
        powershell -File ops/ai/Test-AiLyricsPack.ps1
        powershell -File ops/ai/Test-AiLyricsPack.ps1 -Full
        powershell -File ops/ai/Test-AiLyricsPack.ps1 -Manifest ops/ai/lyrics-pack.v2.json
#>

param(
    [string]$Manifest = (Join-Path $PSScriptRoot 'lyrics-pack.v1.json'),
    [switch]$Full
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$pack = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
$base = [string]$pack.base

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::None
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromMinutes(30)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('Wavee-ops/1.0 (Test-AiLyricsPack)')

function Send-Request([string]$Method, [string]$Url, [long]$RangeTo = -1) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method)), $Url)
    if ($RangeTo -ge 0) { $req.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue([long]0, [long]$RangeTo) }
    $http.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
}

function ConvertTo-Hex([byte[]]$Bytes) { ([BitConverter]::ToString($Bytes) -replace '-', '').ToLowerInvariant() }

# HEAD: status, size and (for model parts) the immutable cache header.
function Test-Head($Row, [string]$Url, [bool]$CheckCache) {
    try {
        $resp = Send-Request 'HEAD' $Url
        try {
            $status = [int]$resp.StatusCode
            $length = $resp.Content.Headers.ContentLength
            $Row.Head = $status
            $Row.Length = if ($null -eq $length) { 'missing' } elseif ($length -eq $Row.Bytes) { 'ok' } else { "$length" }
            if ($status -ne 200) { $Row.Problems += "HEAD $status" }
            if ($Row.Length -ne 'ok') { $Row.Problems += "Content-Length $($Row.Length), expected $($Row.Bytes)" }
            if ($CheckCache) {
                $cc = if ($resp.Headers.CacheControl) { $resp.Headers.CacheControl.ToString() } else { '' }
                $Row.Cache = if ($cc -match 'immutable') { 'ok' } elseif ($cc) { $cc } else { 'missing' }
                if ($Row.Cache -ne 'ok') { $Row.Problems += "Cache-Control '$cc' lacks immutable" }
            }
        } finally { $resp.Dispose() }
    } catch {
        $Row.Head = 'error'
        $Row.Problems += "HEAD failed: $($_.Exception.Message)"
    }
}

# GET bytes=0-1023: the downloader resumes with Range, so the host must answer 206 with the right Content-Range.
function Test-Range($Row, [string]$Url) {
    $to = [Math]::Min([long]1023, [long]$Row.Bytes - 1)
    try {
        $resp = Send-Request 'GET' $Url 1023
        try {
            $status = [int]$resp.StatusCode
            $cr = $resp.Content.Headers.ContentRange
            $body = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $good = $status -eq 206 -and $null -ne $cr -and $cr.From -eq 0 -and $cr.To -eq $to -and
                $cr.Length -eq $Row.Bytes -and $body.Length -eq ($to + 1)
            $Row.Range = if ($good) { 'ok' } else { "$status $cr" }
            if (-not $good) {
                $Row.Problems += "Range: status $status, Content-Range '$cr', $($body.Length) bytes; expected 206 'bytes 0-$to/$($Row.Bytes)'"
            }
        } finally { $resp.Dispose() }
    } catch {
        $Row.Range = 'error'
        $Row.Problems += "Range GET failed: $($_.Exception.Message)"
    }
}

# Streams one object into its own hasher and (for model parts) the reassembled file's hasher.
function Get-Streamed([string]$Url, $FileHasher) {
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    $buffer = New-Object byte[] (1MB)
    [long]$count = 0
    $resp = Send-Request 'GET' $Url
    try {
        if ([int]$resp.StatusCode -ne 200) { throw "GET returned $([int]$resp.StatusCode)" }
        $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                [void]$hasher.TransformBlock($buffer, 0, $n, $null, 0)
                if ($FileHasher) { [void]$FileHasher.TransformBlock($buffer, 0, $n, $null, 0) }
                $count += $n
            }
        } finally { $stream.Dispose() }
        [void]$hasher.TransformFinalBlock($buffer, 0, 0)
        [pscustomobject]@{ Bytes = $count; Sha256 = ConvertTo-Hex $hasher.Hash }
    } finally {
        $resp.Dispose()
        $hasher.Dispose()
    }
}

function Test-Content($Row, [string]$Url, [string]$Sha256, $FileHasher) {
    $size = if ($Row.Bytes -ge 1MB) { '{0:0.0} MiB' -f ($Row.Bytes / 1MB) } else { '{0:0.0} KiB' -f ($Row.Bytes / 1KB) }
    Write-Host "  streaming $($Row.Object) ($size)"
    try {
        $got = Get-Streamed $Url $FileHasher
        $good = $got.Bytes -eq $Row.Bytes -and $got.Sha256 -eq $Sha256.ToLowerInvariant()
        $Row.Sha256 = if ($good) { 'ok' } else { 'MISMATCH' }
        if (-not $good) { $Row.Problems += "streamed $($got.Bytes) bytes with SHA-256 $($got.Sha256)" }
    } catch {
        $Row.Sha256 = 'error'
        $Row.Problems += "stream failed: $($_.Exception.Message)"
    }
}

function New-Row([string]$Object, [long]$Bytes) {
    [pscustomobject]@{
        Object = $Object; Bytes = $Bytes; Head = '-'; Length = '-'; Cache = '-'; Range = '-'; Sha256 = '-'
        Result = ''; Problems = @()
    }
}

# Model files in manifest order: common first, then each language.
$modelFiles = @($pack.common)
foreach ($lang in ($pack.languages.PSObject.Properties | Sort-Object Name)) { $modelFiles += @($lang.Value.files) }

$rows = New-Object System.Collections.Generic.List[object]
Write-Host "Checking $base$(if ($Full) { ' (full: streaming every object)' })"

foreach ($file in $modelFiles) {
    $fileHasher = if ($Full) { [System.Security.Cryptography.SHA256]::Create() } else { $null }
    try {
        foreach ($part in $file.parts) {
            $row = New-Row $part.path $part.bytes
            $url = $base + $part.path
            Test-Head $row $url $true
            Test-Range $row $url
            if ($Full) { Test-Content $row $url $part.sha256 $fileHasher }
            $rows.Add($row)
        }
        if ($Full) {
            $whole = New-Row "$($file.name) (reassembled)" $file.bytes
            [void]$fileHasher.TransformFinalBlock((New-Object byte[] 0), 0, 0)
            $partSum = [long]0
            foreach ($part in $file.parts) { $partSum += [long]$part.bytes }
            $partsOk = -not ($rows | Where-Object { $_.Object -in @($file.parts | ForEach-Object { $_.path }) -and $_.Sha256 -ne 'ok' })
            $good = $partsOk -and $partSum -eq $file.bytes -and (ConvertTo-Hex $fileHasher.Hash) -eq $file.sha256.ToLowerInvariant()
            $whole.Sha256 = if ($good) { 'ok' } else { 'MISMATCH' }
            if (-not $good) { $whole.Problems += "reassembled SHA-256 $(ConvertTo-Hex $fileHasher.Hash), parts sum $partSum" }
            $rows.Add($whole)
        }
    } finally {
        if ($fileHasher) { $fileHasher.Dispose() }
    }
}

foreach ($wheel in $pack.runtime) {
    $row = New-Row "wheel: $($wheel.id)" $wheel.bytes
    Test-Head $row $wheel.url $false
    if ($Full) { Test-Content $row $wheel.url $wheel.sha256 $null }
    $rows.Add($row)
}

foreach ($row in $rows) { $row.Result = if ($row.Problems.Count -eq 0) { 'PASS' } else { 'FAIL' } }
$http.Dispose()

$columns = @('Object', 'Bytes', 'Head', 'Length', 'Cache', 'Range')
if ($Full) { $columns += 'Sha256' }
$columns += 'Result'
Write-Host ($rows | Format-Table -Property $columns -AutoSize | Out-String -Width 300)

$failed = @($rows | Where-Object { $_.Result -ne 'PASS' })
foreach ($row in $failed) {
    foreach ($p in $row.Problems) { Write-Host "FAIL $($row.Object): $p" -ForegroundColor Red }
}
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) of $($rows.Count) checks failed." -ForegroundColor Red
    exit 1
}
Write-Host "All $($rows.Count) objects match the manifest." -ForegroundColor Green
exit 0
