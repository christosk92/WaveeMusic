// ── Platform/ZoomService.Monitor.cs — the monitor the first window opens on, read before the window exists ──────────
//
// The engine sets the process DPI awareness when it creates its platform (`Win32App`), which is AFTER the app has
// decided the first frame's zoom, so the awareness this thread has HERE is not the one the window will have. The work
// area therefore comes back in whatever units that awareness gives: an unaware thread reads it already virtualized to
// 96 DPI (= raw DIP); an aware thread reads physical px, which divide by the monitor's own DPI scale into raw DIP. Only
// READS happen here (the thread's awareness is never changed: the window is created on this thread, and a restored
// "previous" awareness would stay behind as a thread-level override of the process default the engine sets later).
// Primary because the window is created at CW_USEDEFAULT. Every failure answers "unknown" (`ZoomMonitor.IsKnown` false),
// never throws: a missing bound only means the requested client size is used as it is.

using System.Runtime.InteropServices;

namespace Wavee;

public static partial class ZoomService
{
    const uint MonitorDefaultToPrimary = 1;
    const int MdtEffectiveDpi = 0;
    const int DpiAwarenessUnaware = 0;   // DPI_AWARENESS_UNAWARE

    /// <summary>The primary monitor's work area in raw DIP, or <c>default</c> when it cannot be read.</summary>
    public static unsafe ZoomMonitor PrimaryMonitor()
    {
        try
        {
            nint monitor = MonitorFromWindow(0, MonitorDefaultToPrimary);
            if (monitor == 0) return default;
            var info = new MonitorInfoNative { Size = (uint)sizeof(MonitorInfoNative) };
            if (!GetMonitorInfoW(monitor, &info)) return default;

            float scale = 1f;   // an unaware thread already reads 96-DPI units
            if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != DpiAwarenessUnaware)
            {
                uint dpiX = 96, dpiY = 96;
                if (GetDpiForMonitor(monitor, MdtEffectiveDpi, &dpiX, &dpiY) != 0 || dpiX == 0) dpiX = 96;
                scale = dpiX / 96f;
            }
            return new ZoomMonitor((info.WorkRight - info.WorkLeft) / scale, (info.WorkBottom - info.WorkTop) / scale);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return default;
        }
    }

#pragma warning disable CS0649   // fields written by the OS through the pointer
    /// <summary>MONITORINFO: cbSize, rcMonitor, rcWork, dwFlags. The OS fills it; the fields the code never assigns are
    /// the OS's.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfoNative
    {
        public uint Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
    }
#pragma warning restore CS0649

    [LibraryImport("user32.dll")]
    private static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    private static partial int GetAwarenessFromDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetMonitorInfoW(nint monitor, MonitorInfoNative* info);

    [LibraryImport("shcore.dll")]
    private static unsafe partial int GetDpiForMonitor(nint monitor, int dpiType, uint* dpiX, uint* dpiY);
}
