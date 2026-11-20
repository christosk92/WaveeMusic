// ── Screens/Diagnostics.Probe.TestWindow.cs — a real-window test run's place: a screen beside the person's work, never focused ──
//
// Owner rule (2026-11-17, "stop rendering the app on the foreground, it's interrupting my work"): a real-window test run (a verify
// Wavee filmed by Record-Feature.ps1, a `--fake --profile <scratch>` rehearsal, a Store capture) goes on the SECONDARY monitor, never
// takes the foreground or the keyboard, and is never topmost.
//
//   --window-monitor secondary|primary|<n>   the monitor (secondary = the first that is not the primary; n = EnumDisplayMonitors order)
//   --window-rect x,y,w,h                    the CLIENT rect in DIP at that monitor's scale, from its work area's top-left (default
//                                            0,0 and the launch's own size)
//   --no-activate                            never take the foreground (implied by --window-monitor)
//
// The window is placed by the factory, before the first frame and while it is still hidden (the reveal gate shows it after its first
// present), with SWP_NOACTIVATE|SWP_NOZORDER: it is not pushed under the person's windows either (a covered window parks its frames).
// The no-activate half is the engine's (AppOptions.NoActivate: WS_EX_NOACTIVATE, SW_SHOWNOACTIVATE shows, no SetForegroundWindow on an
// activation redirect) and the tray's (Tray.Host.ShowWindowCore asks FluentApp.NeverActivates).

using System.Globalization;
using FluentGpu;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        /// <summary>The launch asks for a window that never takes the foreground (`--no-activate`, or a `--window-monitor` placement).</summary>
        public static bool NoActivateArg(string[] argv) => Array.IndexOf(argv, "--no-activate") >= 0 || Array.IndexOf(argv, "--window-monitor") >= 0;

        /// <summary>`--window-monitor` / `--window-rect`: move and size the (still hidden) main window onto that monitor. A no-op without them.</summary>
        public static void PlaceTestWindow()
        {
            string[] argv = Platform.Args.Argv;
            int at = Array.IndexOf(argv, "--window-monitor");
            nint hwnd = FluentApp.WindowHandle;
            if (at < 0 || at + 1 >= argv.Length || hwnd == 0) return;
            string which = argv[at + 1];
            var monitors = WorkAreas();
            int index = which switch
            {
                "primary" => monitors.FindIndex(static m => m.Primary),
                "secondary" => monitors.FindIndex(static m => !m.Primary),
                _ => int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 && n < monitors.Count ? n : -1,
            };
            if (index < 0) { Say("[test-window] no monitor '" + which + "' (" + monitors.Count.ToString(CultureInfo.InvariantCulture) + " monitors): left where it is"); return; }
            var work = monitors[index];
            const uint NoZOrder = 0x0004, NoActivate = 0x0010, NoSize = 0x0001;
            // First onto the monitor, so the window takes its DPI; then the rect at that scale.
            SetWindowPos(hwnd, 0, work.Left, work.Top, 0, 0, NoZOrder | NoActivate | NoSize);
            float scale = GetDpiForWindow(hwnd) is > 0 and uint dpi ? dpi / 96f : 1f;
            float x = 0f, y = 0f, w = 0f, h = 0f;
            int rectAt = Array.IndexOf(argv, "--window-rect");
            if (rectAt >= 0 && rectAt + 1 < argv.Length && argv[rectAt + 1].Split(',') is [var sx, var sy, var sw, var sh])
            {
                _ = float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                _ = float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                _ = float.TryParse(sw, NumberStyles.Float, CultureInfo.InvariantCulture, out w);
                _ = float.TryParse(sh, NumberStyles.Float, CultureInfo.InvariantCulture, out h);
            }
            int px = work.Left + (int)MathF.Round(x * scale), py = work.Top + (int)MathF.Round(y * scale);
            if (w > 0f && h > 0f)
            {
                int cx = (int)MathF.Round(w * scale), cy = (int)MathF.Round(h * scale);
                SetWindowPos(hwnd, 0, px, py, cx, cy, NoZOrder | NoActivate);
                // The size is the OUTER rect: grow it by what the frame takes, so the CLIENT is the size asked.
                if (GetClientRect(hwnd, out ClientRect client) && (client.Right != cx || client.Bottom != cy))
                    SetWindowPos(hwnd, 0, px, py, 2 * cx - client.Right, 2 * cy - client.Bottom, NoZOrder | NoActivate);
            }
            else SetWindowPos(hwnd, 0, px, py, 0, 0, NoZOrder | NoActivate | NoSize);
            Say("[test-window] on monitor " + index.ToString(CultureInfo.InvariantCulture) + " (" + which + ") at " + px.ToString(CultureInfo.InvariantCulture)
                + "," + py.ToString(CultureInfo.InvariantCulture) + " x" + scale.ToString("0.##", CultureInfo.InvariantCulture) + (FluentApp.NeverActivates ? ", never activated" : ""));
        }

        readonly record struct WorkArea(int Left, int Top, int Right, int Bottom, bool Primary);

        /// <summary>Every monitor's work area (virtual-screen px) and whether it is the primary, in EnumDisplayMonitors order.</summary>
        static unsafe List<WorkArea> WorkAreas()
        {
            var list = new List<WorkArea>();
            EnumDisplayMonitors(0, 0, (nint m, nint dc, nint r, nint data) =>
            {
                TwMonitorInfo info = default;
                info.Size = sizeof(TwMonitorInfo);
                if (TwGetMonitorInfo(m, ref info)) list.Add(new WorkArea(info.WorkLeft, info.WorkTop, info.WorkRight, info.WorkBottom, (info.Flags & 1) != 0));
                return true;
            }, 0);
            return list;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct TwMonitorInfo
        {
            public int Size;
            public int Left, Top, Right, Bottom;
            public int WorkLeft, WorkTop, WorkRight, WorkBottom;
            public uint Flags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool TwGetMonitorInfo(nint monitor, ref TwMonitorInfo info);
    }
}
