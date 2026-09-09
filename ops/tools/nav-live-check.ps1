param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string[]]$Routes,
    [int]$DwellMilliseconds = 5000
)
$ErrorActionPreference = 'Stop'
# Diagnostic only: forward navigation to this exact running process. No launches, playback, cache reset or shutdown.
if (-not ('WaveeNavigationCheck' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WaveeNavigationCheck {
    [StructLayout(LayoutKind.Sequential)] struct CopyData { public UIntPtr Tag; public int Size; public IntPtr Data; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr sender,
        ref CopyData data, uint flags, uint timeout, out UIntPtr result);
    public static void Send(IntPtr window, string uri) {
        IntPtr text = Marshal.StringToHGlobalUni(uri);
        try {
            var data = new CopyData { Tag = (UIntPtr)0x46474143, Size = uri.Length * 2, Data = text };
            UIntPtr result;
            if (SendMessageTimeoutW(window, 0x004A, IntPtr.Zero, ref data, 2, 5000, out result) == IntPtr.Zero)
                throw new InvalidOperationException("Navigation delivery timed out or failed: " + Marshal.GetLastWin32Error());
        } finally { Marshal.FreeHGlobal(text); }
    }
}
'@
}
$app = Get-Process -Id $AppProcessId
if ($app.ProcessName -ne 'Wavee' -or $app.MainWindowHandle -eq 0) { throw 'Target is not a running Wavee window.' }
foreach ($route in $Routes) {
    if (-not $route.StartsWith('wavee://open?route=')) { throw 'Only navigation deep links are accepted.' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    [WaveeNavigationCheck]::Send($app.MainWindowHandle, $route)
    Write-Output ("{0:o} pid={1} deliveredMs={2:0.0} route={3}" -f (Get-Date), $AppProcessId, $clock.Elapsed.TotalMilliseconds, $route)
    Start-Sleep -Milliseconds $DwellMilliseconds
}
