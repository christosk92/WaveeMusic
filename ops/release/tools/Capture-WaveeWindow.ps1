#requires -Version 5.1
<#
.SYNOPSIS
  Screenshot the running Wavee window at native DPI, without its drop shadow.

.DESCRIPTION
  Dev-box tool. It needs Wavee installed, running and signed in.

  Sizes the main window to -W x -H at (-X,-Y), optionally deep-links it to a route
  (wavee://open?route=<route>), waits -SettleMs for animations to land, then takes the window
  rectangle from DWM (DWMWA_EXTENDED_FRAME_BOUNDS - the shadow-free frame, unlike GetWindowRect)
  and copies those pixels off the screen into a PNG.

  The process is made per-monitor-DPI-aware v2 first, so the DWM rectangle is in PHYSICAL pixels
  and the capture is 1:1 with the screen on a scaled display.

  The PNG it writes is the SOURCE for ops\release\tools\New-ReleaseImage.ps1 - keep it in
  artifacts\media-src\ (gitignored); only the framed JPEG belongs in the release folder.

.EXAMPLE
  .\Capture-WaveeWindow.ps1 -Route home -Out artifacts\media-src\home.png

.EXAMPLE
  .\Capture-WaveeWindow.ps1 -Route settings -Out artifacts\media-src\settings.png -W 1600 -H 900
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Out,
  [string]$Route,
  [string]$Arg,
  [int]$W = 1600,
  [int]$H = 1000,
  [int]$SettleMs = 2500,
  [int]$X = 40,
  [int]$Y = 40
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not ([System.Management.Automation.PSTypeName]'WaveeCapture.Win32').Type) {
  Add-Type -Namespace WaveeCapture -Name Win32 -MemberDefinition @'
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
'@
}

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4. Must happen before any window geometry call, so
# MoveWindow and the DWM rectangle are in physical pixels on a scaled display.
$dpiOk = $false
try { $dpiOk = [WaveeCapture.Win32]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch { $dpiOk = $false }
if (-not $dpiOk) {
  try { $dpiOk = [WaveeCapture.Win32]::SetProcessDPIAware() } catch { $dpiOk = $false }
  if (-not $dpiOk) {
    Write-Host 'note: could not set DPI awareness (already set for this host) - check the reported size against the window.'
  }
}

# ---------------------------------------------------------------------------------------------------
# the window
# ---------------------------------------------------------------------------------------------------

$proc = Get-Process -Name Wavee -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
if (-not $proc) {
  throw 'No running Wavee window was found. Start Wavee (installed or dotnet run), sign in, and try again.'
}
$hwnd = $proc.MainWindowHandle

if ([WaveeCapture.Win32]::IsIconic($hwnd)) { $null = [WaveeCapture.Win32]::ShowWindow($hwnd, 9) }  # SW_RESTORE
if (-not [WaveeCapture.Win32]::MoveWindow($hwnd, $X, $Y, $W, $H, $true)) {
  throw "MoveWindow failed (Win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
}
$null = [WaveeCapture.Win32]::SetForegroundWindow($hwnd)

if ($Route) {
  $link = 'wavee://open?route=' + [uri]::EscapeDataString($Route)
  if ($Arg) { $link += '&arg=' + [uri]::EscapeDataString($Arg) }
  Write-Host "deep link: $link"
  Start-Process $link
}

Start-Sleep -Milliseconds $SettleMs

# Foreground again (the shell activation may have stolen it), then let the window settle once more.
$null = [WaveeCapture.Win32]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

# ---------------------------------------------------------------------------------------------------
# the rectangle: DWMWA_EXTENDED_FRAME_BOUNDS excludes the drop shadow GetWindowRect includes
# ---------------------------------------------------------------------------------------------------

$rect = New-Object WaveeCapture.Win32+RECT
$size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'WaveeCapture.Win32+RECT')
$hr = [WaveeCapture.Win32]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, $size)
if ($hr -ne 0) {
  Write-Host ("note: DwmGetWindowAttribute failed (0x{0:X8}) - falling back to GetWindowRect (includes the shadow)." -f $hr)
  if (-not [WaveeCapture.Win32]::GetWindowRect($hwnd, [ref]$rect)) { throw 'GetWindowRect failed.' }
}

$rw = $rect.Right - $rect.Left
$rh = $rect.Bottom - $rect.Top
if ($rw -le 0 -or $rh -le 0) { throw "Bad window rectangle: ${rw}x${rh}." }

# ---------------------------------------------------------------------------------------------------
# the pixels
# ---------------------------------------------------------------------------------------------------

$outDir = Split-Path -Parent $Out
if (-not $outDir) { $outDir = (Get-Location).ProviderPath }
if (-not (Test-Path -LiteralPath $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
$outPath = Join-Path (Resolve-Path -LiteralPath $outDir).ProviderPath (Split-Path -Leaf $Out)

$bmp = New-Object System.Drawing.Bitmap($rw, $rh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0,
                      (New-Object System.Drawing.Size($rw, $rh)),
                      [System.Drawing.CopyPixelOperation]::SourceCopy)
  }
  finally { $g.Dispose() }
  $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally { $bmp.Dispose() }

$bytes = (Get-Item -LiteralPath $outPath).Length
Write-Host ("rect: ({0},{1})-({2},{3})" -f $rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
Write-Host "$outPath  $bytes B  ${rw}x${rh}"
