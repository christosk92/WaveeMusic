# Drive-WaveeWindow.ps1 — occlusion-proof capture + minimal input for the running Wavee window (dev-box tool; the
#   sibling of Capture-WaveeWindow.ps1 for when the desktop is busy: PrintWindow reads the window even while it is
#   covered, clicks wait for the user to be idle and hand the foreground straight back).
#   -Out <png>      PrintWindow(PW_RENDERFULLCONTENT) → cropped to the DWM frame bounds (works while covered)
#   -Key <vk>       PostMessage WM_KEYDOWN/WM_KEYUP (13 = Enter). Reaches the window proc, but the engine ignores keys
#                   while the window is inactive (verified 2026-09-02) — prefer -Click for anything that must land
#   -Click x,y      SendInput click at CLIENT-relative DIP coords; waits until the user has been idle ≥ -IdleMs,
#                   then restores the previous foreground window and cursor position
#   -Move x,y,w,h   MoveWindow (physical px)   -Link <wavee://…>  hand a deep link to the running instance
param([string]$Out, [int]$Key = 0, [string]$Click, [string]$Move, [string]$Link, [int]$Wait = 0, [int]$IdleMs = 4000, [switch]$Info)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class D {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
}
'@
[void][D]::SetProcessDpiAwarenessContext([IntPtr](-4))
if ($Wait -gt 0) { Start-Sleep -Milliseconds $Wait }
$exe = 'C:\wavee\WaveeMusic\src\apps\Wavee\bin\Debug\net10.0\Wavee.exe'
if ($Link) { Start-Process -FilePath $exe -ArgumentList $Link -WorkingDirectory (Split-Path $exe) | Out-Null; "linked $Link"; Start-Sleep -Milliseconds 1500 }
$p = Get-Process Wavee -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw 'no Wavee window' }
$h = $p.MainWindowHandle
if ($Move) { $m = $Move.Split(','); [void][D]::MoveWindow($h, [int]$m[0], [int]$m[1], [int]$m[2], [int]$m[3], $true); Start-Sleep -Milliseconds 700 }
function Frame { $r = New-Object D+RECT; [void][D]::DwmGetWindowAttribute($h, 9, [ref]$r, 16); $r }
$fr = Frame; $scale = [D]::GetDpiForWindow($h) / 96.0
if ($Info) { "hwnd=$h frame=$($fr.L),$($fr.T) $($fr.R-$fr.L)x$($fr.B-$fr.T) scale=$scale" }
if ($Key -ne 0) { [void][D]::PostMessage($h, 0x0100, [IntPtr]$Key, [IntPtr]0); Start-Sleep -Milliseconds 60; [void][D]::PostMessage($h, 0x0101, [IntPtr]$Key, [IntPtr]([long]1 -shl 30 -bor [long]1 -shl 31)); "key $Key"; Start-Sleep -Milliseconds 400 }
if ($Click) {
  $xy = $Click.Split(','); $x = [int]($fr.L + [double]$xy[0] * $scale); $y = [int]($fr.T + [double]$xy[1] * $scale)
  $li = New-Object D+LASTINPUTINFO; $li.cbSize = 8
  for ($i = 0; $i -lt 120; $i++) { [void][D]::GetLastInputInfo([ref]$li); if (([Environment]::TickCount - $li.dwTime) -ge $IdleMs) { break }; Start-Sleep -Milliseconds 500 }
  $fr = Frame; $x = [int]($fr.L + [double]$xy[0] * $scale); $y = [int]($fr.T + [double]$xy[1] * $scale)   # re-read: the window may have moved during the idle wait
  $prevFg = [D]::GetForegroundWindow(); $cur = New-Object D+POINT; [void][D]::GetCursorPos([ref]$cur)
  $fgTid = [D]::GetWindowThreadProcessId($prevFg, [ref]([uint32]0)); $me = [D]::GetCurrentThreadId()
  [void][D]::AttachThreadInput($me, $fgTid, $true); [void][D]::BringWindowToTop($h); [void][D]::SetForegroundWindow($h); [void][D]::AttachThreadInput($me, $fgTid, $false)
  Start-Sleep -Milliseconds 250
  [void][D]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 80
  [D]::mouse_event(1, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 40   # MOUSEEVENTF_MOVE — a pointer update lands before the press
  [D]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60; [D]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 350
  [void][D]::SetCursorPos($cur.X, $cur.Y)
  $tid2 = [D]::GetWindowThreadProcessId($h, [ref]([uint32]0)); [void][D]::AttachThreadInput($me, $tid2, $true); [void][D]::SetForegroundWindow($prevFg); [void][D]::AttachThreadInput($me, $tid2, $false)
  "clicked $x,$y (dip $Click), foreground restored"
}
if ($Out) {
  Start-Sleep -Milliseconds 500
  $fr = Frame; $wr = New-Object D+RECT; [void][D]::GetWindowRect($h, [ref]$wr)
  $w = $wr.R - $wr.L; $hh = $wr.B - $wr.T
  $bmp = New-Object System.Drawing.Bitmap $w, $hh, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); $ok = [D]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
  $crop = New-Object System.Drawing.Rectangle ($fr.L - $wr.L), ($fr.T - $wr.T), ($fr.R - $fr.L), ($fr.B - $fr.T)
  $c = $bmp.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb); $c.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $c.Dispose(); $bmp.Dispose()
  "saved $Out ($($crop.Width)x$($crop.Height)) ok=$ok"
}
