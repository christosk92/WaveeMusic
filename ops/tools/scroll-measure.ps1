param(
    [string]$Repo = "C:\wavee\waveemusic",
    [string]$Exe = "C:\wavee\waveemusic\src\apps\Wavee\bin\Release\net10.0\Wavee.exe",
    [string]$Route = "wavee://open?route=pl&arg=spotify%3Aplaylist%3A2pnt79m93NytfAj2lByLlQ",
    [int]$SettleSeconds = 6,
    [int]$ScrollSeconds = 8,
    [int]$Passes = 2,
    [int]$WheelPerTick = 120,
    [int]$TickIntervalMs = 16
)
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class ScrollMeasureNative
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_HOME = 0x24;
    public const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    public static bool FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        ShowWindow(hwnd, SW_RESTORE);
        BringWindowToTop(hwnd);
        return SetForegroundWindow(hwnd);
    }

    public static bool CursorOverList(IntPtr hwnd)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;
        int x = r.Left + (int)(w * 0.65);
        int y = r.Top + (int)(h * 0.60);
        return SetCursorPos(x, y);
    }

    public static void Wheel(int delta)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].u.mi.mouseData = unchecked((uint)delta);
        input[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
        SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Home()
    {
        INPUT[] input = new INPUT[2];
        input[0].type = INPUT_KEYBOARD;
        input[0].u.ki.wVk = VK_HOME;
        input[1].type = INPUT_KEYBOARD;
        input[1].u.ki.wVk = VK_HOME;
        input[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, input, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

function Wait-MainWindow([int]$ProcessId, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $proc) { return [IntPtr]::Zero }
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }
        Start-Sleep -Milliseconds 200
    }
    return [IntPtr]::Zero
}

function Invoke-ScrollPass([IntPtr]$Hwnd, [int]$Seconds, [int]$Delta, [int]$IntervalMs) {
    if (-not [ScrollMeasureNative]::FocusWindow($Hwnd)) {
        Write-Host "warn: SetForegroundWindow failed"
    }
    Start-Sleep -Milliseconds 150
    if (-not [ScrollMeasureNative]::CursorOverList($Hwnd)) {
        Write-Host "warn: SetCursorPos failed"
    }
    Start-Sleep -Milliseconds 80
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ticks = 0
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        [ScrollMeasureNative]::Wheel(-[Math]::Abs($Delta))
        $ticks++
        Start-Sleep -Milliseconds $IntervalMs
    }
    Write-Host ("scrolled ticks=" + $ticks + " seconds=" + [Math]::Round($sw.Elapsed.TotalSeconds, 1))
}

function Reset-ScrollTop([IntPtr]$Hwnd) {
    [ScrollMeasureNative]::FocusWindow($Hwnd) | Out-Null
    Start-Sleep -Milliseconds 80
    [ScrollMeasureNative]::Home()
    Start-Sleep -Milliseconds 200
}

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
Start-Sleep -Seconds 8

Write-Host ("open " + $Route)
$handoff = Start-Process -FilePath $Exe -ArgumentList ('"' + $Route + '"') -PassThru -WorkingDirectory (Split-Path $Exe)
$handoff.WaitForExit(15000) | Out-Null
Write-Host ("settle " + $SettleSeconds + "s")
Start-Sleep -Seconds $SettleSeconds

$hwnd = Wait-MainWindow -ProcessId $app.Id -TimeoutSeconds 15
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "warn: main window handle is zero; wheel input may miss the list"
} else {
    Write-Host ("hwnd=" + $hwnd)
}

for ($pass = 1; $pass -le $Passes; $pass++) {
    $label = if ($pass -eq 1) { "cold" } else { "warm" }
    Write-Host ("pass " + $pass + " (" + $label + ")")
    if ($pass -gt 1) { Reset-ScrollTop $hwnd }
    Invoke-ScrollPass -Hwnd $hwnd -Seconds $ScrollSeconds -Delta $WheelPerTick -IntervalMs $TickIntervalMs
}

Start-Sleep -Seconds 2
Write-Host "closing"
try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 2

$lines = Get-Content $log | Select-Object -Skip $startLines | Where-Object { $_ -match "pid=$($app.Id) " }
$out = Join-Path $env:TEMP "wavee-scroll-measure-result.txt"
$lines | Where-Object { $_ -match "scroll\.frames|nav\.frames|frame\.stall|page\.reveal|catalog\.demand\.wave|catalog\.fetch\.batch" } |
    Set-Content $out -Encoding UTF8
Write-Host ("wrote " + $out + " (" + (Get-Content $out).Count + " lines)")

Write-Host ""
Write-Host ("{0,-8} {1,6} {2,7} {3,10} {4,7} {5,8} {6,8} {7,-44} {8,10}" -f `
    "route", "frames", "fps", "avgMs", "slow33", "missed", "worst", "phases", "hotAlloc")
Get-Content $out | Where-Object { $_ -match "scroll\.frames" } | ForEach-Object {
    $route = if ($_ -match "route=(\S+)") { $Matches[1] } else { "?" }
    if ($route.Length -gt 8) { $route = $route.Substring(0, 8) }
    $frames = if ($_ -match "frames=(\d+)") { $Matches[1] } else { "?" }
    $fps = if ($_ -match "fps=([\d.]+)") { $Matches[1] } else { "?" }
    $avg = if ($_ -match "avgFrameMs=([\d.]+)") { $Matches[1] } else { "?" }
    $slow = if ($_ -match "slow33=(\d+)") { $Matches[1] } else { "?" }
    $missed = if ($_ -match "missedVblanks=(\d+)") { $Matches[1] } else { "?" }
    $worst = if ($_ -match "worst=frameMs=([\d.]+)") { $Matches[1] } else { "?" }
    $flush = if ($_ -match "flush=([\d.]+)") { $Matches[1] } else { "-" }
    $reactive = if ($_ -match "reactive=([\d.]+)") { $Matches[1] } else { "-" }
    $realize = if ($_ -match "realize=([\d.]+)") { $Matches[1] } else { "-" }
    $layout = if ($_ -match "layout=([\d.]+)") { $Matches[1] } else { "-" }
    $record = if ($_ -match "(?<![a-z])record=([\d.]+)") { $Matches[1] } else { "-" }
    $submit = if ($_ -match "submit=([\d.]+)") { $Matches[1] } else { "-" }
    $hot = if ($_ -match "hotAlloc=(\d+)") { $Matches[1] } else { "?" }
    $phases = "f=$flush r=$reactive z=$realize l=$layout c=$record s=$submit"
    Write-Host ("{0,-8} {1,6} {2,7} {3,10} {4,7} {5,8} {6,8} {7,-44} {8,10}" -f `
        $route, $frames, $fps, $avg, $slow, $missed, $worst, $phases, $hot)
}
