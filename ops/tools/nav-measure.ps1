param(
    [string]$Repo = "C:\wavee\waveemusic",
    [string]$Exe = "C:\wavee\waveemusic\src\apps\Wavee\bin\Release\net10.0\Wavee.exe",
    [int]$SettleSeconds = 7
)
$ErrorActionPreference = "Stop"
$log = Join-Path $env:LOCALAPPDATA ("Wavee\logs\wavee-" + (Get-Date -Format "yyyyMMdd") + ".log")
$startLines = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }

Write-Host "launching $Exe"
$app = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$ready = $false
while ($sw.Elapsed.TotalSeconds -lt 60) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $log) {
        $tail = Get-Content $log | Select-Object -Skip $startLines
        if ($tail | Where-Object { $_ -match "pid=$($app.Id) .*protocol-installed" }) { $ready = $true; break }
        if ($tail | Where-Object { $_ -match "pid=$($app.Id) .*Fatal error" }) { Write-Host "FATAL during launch"; break }
    }
}
Write-Host ("ready=" + $ready + " after " + [int]$sw.Elapsed.TotalSeconds + "s")
Start-Sleep -Seconds 8   # let the sidebar's initial hydrate settle before timing navigations

# A second Wavee.exe with a wavee:// argument hands the link to the running instance and exits.
$routes = @(
    "wavee://open?route=home",
    "wavee://open?route=album&arg=spotify%3Aalbum%3A5Qg2iNuV6zTCGlIYunvvTd",
    "wavee://open?route=artist&arg=spotify%3Aartist%3A3eVa5w3URK5duf6eyVDbu9",
    "wavee://open?route=pl&arg=spotify%3Aplaylist%3A37i9dQZF1EP6YuccBxUcC1",
    "wavee://open?route=album&arg=spotify%3Aalbum%3A7kFyd5oyJdVX2pIi6P4iHE",
    "wavee://open?route=artist&arg=spotify%3Aartist%3A4lxfqrEsLX6N1N4OCSkILp",
    "wavee://open?route=home",
    "wavee://open?route=album&arg=spotify%3Aalbum%3A5Qg2iNuV6zTCGlIYunvvTd",   # warm re-open
    "wavee://open?route=artist&arg=spotify%3Aartist%3A3eVa5w3URK5duf6eyVDbu9",  # warm re-open
    "wavee://open?route=pl&arg=spotify%3Aplaylist%3A37i9dQZF1EP6YuccBxUcC1"     # warm re-open
)
foreach ($r in $routes) {
    Write-Host ("nav " + $r)
    $p = Start-Process -FilePath $Exe -ArgumentList ('"' + $r + '"') -PassThru -WorkingDirectory (Split-Path $Exe)
    $p.WaitForExit(15000) | Out-Null
    Start-Sleep -Seconds $SettleSeconds
}
Start-Sleep -Seconds 4
Write-Host "closing"
try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 2

$lines = Get-Content $log | Select-Object -Skip $startLines | Where-Object { $_ -match "pid=$($app.Id) " }
$out = Join-Path $env:TEMP "wavee-nav-measure-result.txt"
$lines | Where-Object { $_ -match "nav\.frames|frame\.stall|hydration\.|catalog\.demand\.wave|catalog\.fetch\.batch|Surface error|Fatal error|catalog\.scope\.published|play intent|play resolved|outbox\.replay" } | Set-Content $out -Encoding UTF8
Write-Host ("wrote " + $out + " (" + (Get-Content $out).Count + " lines)")
