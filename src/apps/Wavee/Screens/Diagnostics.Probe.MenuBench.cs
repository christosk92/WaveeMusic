// ── Screens/Diagnostics.Probe.MenuBench.cs ─────────────────────────────────────────────────────────────────────────
// the `--menu-bench` GUI arm: what opening a track context menu costs the WHOLE window, measured in the real app.
//
// Role: SHELL
// Owner: S
//
// Run beside a live Wavee with a scratch profile (its own single instance, no real account):
//   Wavee.exe --fake --profile <scratch dir> --menu-bench [--bench-menu-rounds N] [--bench-menu-fullscreen N]
//
// It opens a `--fake` album page, finds a real track row and raises a REAL right-click on it through the input dispatcher
// (`InputDispatcher.RequestContextAt` — the same hit-test and context funnel a mouse uses), then closes the menu with a
// posted Escape. Each round visits both variants in a rotating order, so outside load (the owner's own Wavee on the same
// GPU) lands on both alike:
//   inwindow  OS popup windows refused (AppHost.PopupWindowsEnabled = false) — the reference
//   shipped   the engine decides: an OS popup window only when the menu must leave the window
// Per open: request → on screen, the UI frame that mounted the menu, the worst UI frame of the open, and whether a popup
// window was leased; the window's own cost is the `[overlay.popup]` line it leaves in the log. Plain text to stderr +
// `menu-bench-latest.txt` under the probe-out directory. `--fake` cannot play audio, so the background is idle: open
// costs compare, steady-state frame rates do not.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static bool TryMenuBench(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            if (!s_options.MenuBench) return false;
            if (window is not Win32Window w || device is not D3D12Device gpu) { Say("[menu-bench] unavailable: requires Win32Window + D3D12Device"); return true; }
            if (!WarmUntilShell(host, w, gpu, 600)) { Say("[menu-bench] the shell never activated a route"); return true; }

            // Topmost for the run: a covered window stands its presents down, and a bench launched from a terminal opens
            // behind it — every number would then measure the stand-down instead of the menu.
            SetWindowPos(w.Handle.Value, -1 /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            var album = EntityUri.Parse("spotify:album:al0");
            // A playable uri plays as a one-row context at once (no resolve — `--fake` has no network): the visualizer and
            // the now-playing chrome then tick at the panel rate, which is the state the owner opens menus in.
            Playback.PlayContext("spotify:track:tr0");
            Nav(Shell.For(album, "Bench album"));
            PumpPaced(host, w, 3.0);
            Say("[menu-bench] playback: current=" + Playback.Snap().HasCurrent + " playing=" + Playback.Snap().IsPlaying);
            if (FindTrackRow(host) is not { } row) { Say("[menu-bench] no track row with a context handler is on screen"); return true; }
            Say("[menu-bench] anchor row at " + row.ToString() + "; rounds=" + s_options.MenuRounds);

            bool shipped = host.PopupWindowsEnabled;
            if (!shipped) { Say("[menu-bench] this host refuses popup windows (no secondary swapchains): nothing to compare"); return true; }
            // The engine keeps one popup window warm, built at idle once the window has been up a second with no input (AppHost
            // popup pool). Whether the first measured open finds it decides what that open costs, so the bench waits for it
            // (bounded) instead of leaving it to the clock; the report says how long that took, or that the wait expired.
            var prewarm = Stopwatch.StartNew();
            const double PrewarmWaitSec = 10.0;
            while (!w.IsClosed && host.PrewarmedPopupWindows == 0 && prewarm.Elapsed.TotalSeconds < PrewarmWaitSec) PumpPaced(host, w, 0.1);
            string prewarmNote = host.PrewarmedPopupWindows > 0
                ? string.Format(CultureInfo.InvariantCulture, "prewarm parked after {0:0} ms of waiting", prewarm.Elapsed.TotalMilliseconds)
                : string.Format(CultureInfo.InvariantCulture, "prewarm wait EXPIRED after {0:0.0} s: the first open builds its popup", PrewarmWaitSec);
            Say("[menu-bench] " + prewarmNote);
            var point = new Point2(row.X + Math.Min(60f, row.W / 4f), row.Y + row.H / 2f);
            var opens = new Dictionary<string, List<MenuOpen>> { ["inwindow"] = new(), ["shipped"] = new() };
            string[] order = ["inwindow", "shipped"];
            for (int r = 0; r < s_options.MenuRounds && !w.IsClosed; r++)
            {
                for (int k = 0; k < order.Length; k++)
                {
                    string v = order[(k + r) % order.Length];
                    host.PopupWindowsEnabled = v == "shipped";
                    opens[v].Add(MeasureOpen(host, w, point));
                    PumpPaced(host, w, 2.0);
                    PostEscape(w);
                    PumpPaced(host, w, 1.0);
                }
            }
            host.PopupWindowsEnabled = shipped;

            // --bench-menu-fullscreen N: with the closed menus' popup pairs parked, toggle the window's full screen (F11, posted to
            // this window) N times, then open the menu once more: what a parked popup costs the owner's swap chain resizes, and
            // that the next menu still opens. The [render.pace] and popup lines of the log are the evidence.
            int fsCycles = ArgInt("--bench-menu-fullscreen");
            MenuOpen? afterFs = null;
            if (fsCycles > 0)
            {
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] fullscreen cycles={0} pooled={1}", fsCycles, host.PooledPopupWindows));
                for (int c = 0; c < fsCycles && !w.IsClosed; c++)
                {
                    PostKey(w, 0x7A /*VK_F11*/); PumpPaced(host, w, 1.5);
                    PostKey(w, 0x7A); PumpPaced(host, w, 1.5);
                }
                afterFs = MeasureOpen(host, w, point);
                PumpPaced(host, w, 1.0);
                PostEscape(w);
                PumpPaced(host, w, 1.0);
            }

            var sb = new StringBuilder(4096).AppendLine()
                .AppendLine("=== WAVEE MENU BENCH (track context menu, real right-click path) ===")
                .AppendLine("version=" + VersionLabel() + "  rounds=" + s_options.MenuRounds + "  " + prewarmNote)
                .AppendLine("inwindow = OS popup windows refused (the reference); shipped = the engine decides (a window only when the menu")
                .AppendLine("must leave the window). per open, ms: visible = request -> menu on screen (in-window: the main present of the")
                .AppendLine("mounting frame; leased: the popup window shown) | openFrame = the UI frame that mounted it | uiWorst = worst UI")
                .AppendLine("frame in the next 600 ms | mainGap = worst gap between main presents in it (idle gaps count: compare, don't read)")
                .AppendLine("popup = where a leased popup's window + swapchain came from: Built (made for it), Prewarmed (the idle prewarm's),")
                .AppendLine("Reused (a closed popup's)");
            foreach (var v in order)
            {
                var list = opens[v];
                sb.AppendLine(v + ":");
                foreach (var o in list)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   visible={0,6:0.0}  openFrame={1,5:0.0}  uiWorst={2,5:0.0}  mainGap={3,5:0.0}  {4}",
                        o.VisibleMs, o.OpenFrameMs, o.UiWorstMs, o.MainGapMs, o.Leased ? "LEASED a popup window  popup=" + o.Origin : "in-window"));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   median: visible={0:0.0} openFrame={1:0.0} uiWorst={2:0.0} mainGap={3:0.0}",
                    Median(list, o => o.VisibleMs), Median(list, o => o.OpenFrameMs), Median(list, o => o.UiWorstMs), Median(list, o => o.MainGapMs)));
            }
            // --bench-menu-restyle: with the menus' popup pairs parked, flip light/dark, open the menu; then switch the theme
            // (Classic <-> WinUI), open it again. A pair made for the old look must be released (the log's "pool drop reason=stale")
            // and the new menu built with the new material, never the parked one reused.
            var restyled = new List<(string What, MenuOpen Open)>();
            if (ArgFlag("--bench-menu-restyle"))
            {
                Say("[menu-bench] restyle: pooled=" + host.PooledPopupWindows);
                Wavee.Appearance.ToggleMode();
                PumpPaced(host, w, 2.0);
                restyled.Add(("mode flipped", RestyleOpen(host, w)));
                string other = Wavee.Theming.ThemeInspection.ActiveId == "winui" ? "classic" : "winui";
                _ = Wavee.Theming.ThemeHost.Switch(other);
                PumpPaced(host, w, 3.0);
                restyled.Add(("theme " + other, RestyleOpen(host, w)));
                Wavee.Appearance.ToggleMode();
                PumpPaced(host, w, 2.0);
                restyled.Add(("mode flipped back", RestyleOpen(host, w)));
            }
            // --bench-menu-devicelost: a controlled device removal (the live adapter switch's rendezvous, to the adapter already in
            // use: GpuAdapterInfo.RequestAdapterSwitch) once with the popup pairs parked and once with the menu open, each followed
            // by a fresh open. The parked pairs' swapchains are released with the device and rebuilt on their windows at reuse; the
            // popup windows this process holds are counted before and after, so a leak shows.
            if (ArgFlag("--bench-menu-devicelost"))
            {
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] devicelost: pooled={0} popupHwnds={1}", host.PooledPopupWindows, PopupHwndCount()));
                FluentGpu.Rhi.D3D12.GpuAdapterInfo.RequestAdapterSwitch(FluentGpu.Rhi.D3D12.GpuAdapterInfo.PreferredAdapterLuid);
                PumpPaced(host, w, 3.0);
                restyled.Add(("after loss with pairs parked", RestyleOpen(host, w)));
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] devicelost parked: pooled={0} popupHwnds={1}", host.PooledPopupWindows, PopupHwndCount()));
                host.Input.RequestContextAt(point);
                PumpPaced(host, w, 0.6);
                FluentGpu.Rhi.D3D12.GpuAdapterInfo.RequestAdapterSwitch(FluentGpu.Rhi.D3D12.GpuAdapterInfo.PreferredAdapterLuid);
                PumpPaced(host, w, 3.0);
                PostEscape(w);
                PumpPaced(host, w, 1.5);
                restyled.Add(("after loss with the menu open", RestyleOpen(host, w)));
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] devicelost open: pooled={0} popupHwnds={1}", host.PooledPopupWindows, PopupHwndCount()));
            }
            // --bench-menu-alternate N: N times, the track menu in this theme, then in Zune (a wider menu: 320 against 278 DIP), each
            // from the one pooled window when the pool key holds across the switch: the screen grabs of each popup's first frames show
            // whether a reused window ever shows the new menu in the previous one's geometry.
            int alternate = ArgInt("--bench-menu-alternate");
            if (alternate > 0)
            {
                string home = Wavee.Theming.ThemeInspection.ActiveId, other = home == "zune" ? "classic" : "zune";
                for (int k = 0; k < alternate && !w.IsClosed; k++)
                {
                    foreach (var theme in new[] { other, home })
                    {
                        _ = Wavee.Theming.ThemeHost.Switch(theme);
                        PumpPaced(host, w, 2.5);
                        restyled.Add(("alternate " + theme + " " + k, RestyleOpen(host, w)));
                    }
                }
            }
            // --bench-menu-minimize: minimise and restore the window with the pairs parked, then with a menu open; the popup windows
            // shown afterwards are counted (a parked one must stay hidden; an open menu's comes back with the window).
            if (ArgFlag("--bench-menu-minimize"))
            {
                nint hwnd = w.Handle.Value;
                ShowWindow(hwnd, 6 /*SW_MINIMIZE*/); PumpPaced(host, w, 1.5);
                ShowWindow(hwnd, 9 /*SW_RESTORE*/); PumpPaced(host, w, 1.5);
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] minimise+restore with pairs parked: pooled={0} visiblePopupHwnds={1} hostPopups={2}",
                    host.PooledPopupWindows, PopupHwndCount(visibleOnly: true), host.PopupWindows.Count));
                host.Input.RequestContextAt(point);
                PumpPaced(host, w, 1.0);
                int openVisible = PopupHwndCount(visibleOnly: true);
                ShowWindow(hwnd, 6); PumpPaced(host, w, 1.5);
                int minimisedVisible = PopupHwndCount(visibleOnly: true);
                ShowWindow(hwnd, 9); PumpPaced(host, w, 1.5);
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] minimise+restore with a menu open: visible before={0} minimised={1} restored={2} hostPopups={3}",
                    openVisible, minimisedVisible, PopupHwndCount(visibleOnly: true), host.PopupWindows.Count));
                PostEscape(w);
                PumpPaced(host, w, 1.5);
                Say(string.Format(CultureInfo.InvariantCulture, "[menu-bench] after escape: visiblePopupHwnds={0} pooled={1}", PopupHwndCount(visibleOnly: true), host.PooledPopupWindows));
            }
            foreach (var (what, o) in restyled)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "restyle {0}: visible={1:0.0} openFrame={2:0.0} {3}",
                    what, o.VisibleMs, o.OpenFrameMs, o.Leased ? "LEASED popup=" + o.Origin : "in-window"));
            if (afterFs is { } af)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "after {0} full-screen cycles: visible={1:0.0} openFrame={2:0.0} {3}",
                    fsCycles, af.VisibleMs, af.OpenFrameMs, af.Leased ? "LEASED popup=" + af.Origin : "in-window"));
            string report = sb.ToString();
            WriteArtifact(OutDir(), "menu-bench-latest.txt", report);
            Say(report);
            return true;
        }

        readonly record struct MenuOpen(double VisibleMs, double OpenFrameMs, double UiWorstMs, double MainGapMs, bool Leased, PopupOrigin Origin);

        static double Median(List<MenuOpen> list, Func<MenuOpen, double> f)
        {
            if (list.Count == 0) return double.NaN;
            var xs = new List<double>(list.Count);
            foreach (var o in list) xs.Add(f(o));
            xs.Sort();
            return xs[xs.Count / 2];
        }

        /// <summary>Raise one real right-click and pace frames for 600 ms, timing the menu onto the screen.</summary>
        static MenuOpen MeasureOpen(AppHost host, Win32Window w, Point2 point)
        {
            long req = Stopwatch.GetTimestamp();
            host.Input.RequestContextAt(point);
            host.RunFrame();
            double openFrame = host.LastStats.FrameMs;
            ulong openSeq = host.PublishSequence;
            double visible = double.NaN, uiWorst = openFrame, mainGap = 0;
            bool leased = false;
            var origin = PopupOrigin.Built;
            ulong lastPresent = host.PresentedSequence;
            long lastPresentQ = 0;
            static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
            var sw = Stopwatch.StartNew();
            while (!w.IsClosed && sw.Elapsed.TotalSeconds < 0.6)
            {
                long now = Stopwatch.GetTimestamp();
                if (!leased && host.PopupWindows.Count > 0) origin = host.PopupWindows[0].Origin;
                leased |= host.PopupWindows.Count > 0;
                if (double.IsNaN(visible))
                {
                    bool shown = false;
                    if (leased) { foreach (var p in host.PopupWindows) shown |= p.Window.IsShown; }
                    else shown = host.LastPresentPublishSeq >= openSeq;
                    if (shown) visible = Ms(req, now);
                }
                if (host.PresentedSequence != lastPresent)
                {
                    if (lastPresentQ != 0) mainGap = Math.Max(mainGap, Ms(lastPresentQ, now));
                    lastPresent = host.PresentedSequence;
                    lastPresentQ = now;
                }
                var wait = host.WaitRequestWithDetached();
                int remaining = Math.Max(0, (int)Math.Ceiling((0.6 - sw.Elapsed.TotalSeconds) * 1000));
                wait = wait with { TimeoutMs = wait.TimeoutMs < 0 ? Math.Min(remaining, 4) : Math.Min(Math.Min(remaining, 4), wait.TimeoutMs) };
                w.WaitForWork(in wait);
                host.RunFrame();
                uiWorst = Math.Max(uiWorst, host.LastStats.FrameMs);
            }
            return new MenuOpen(visible, openFrame, uiWorst, mainGap, leased, origin);
        }

        /// <summary>The first row-shaped, on-screen node carrying a context handler (a track row), in window DIP.</summary>
        static RectF? FindTrackRow(AppHost host)
        {
            var scene = host.Scene;
            if (scene.Root.IsNull) return null;
            var vp = scene.Bounds(scene.Root);
            var stack = new Stack<NodeHandle>();
            stack.Push(scene.Root);
            RectF? best = null;
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!scene.IsLive(n) || (scene.Flags(n) & NodeFlags.Parked) != 0) continue;
                if ((scene.Interaction(n).HandlerMask & InteractionInfo.ContextBit) != 0)
                {
                    var r = scene.AbsoluteRect(n);
                    bool rowShaped = r.H is >= 32f and <= 96f && r.W >= 400f;
                    bool visible = r.Y >= 120f && r.Y + r.H <= vp.H - 160f && r.X >= 0f && r.X + r.W <= vp.W;
                    if (rowShaped && visible && (best is null || r.Y < best.Value.Y)) best = r;
                }
                for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
            }
            return best;
        }

        /// <summary>The production loop shape (the host's own wait request), for <paramref name="seconds"/> of wall time.</summary>
        static void PumpPaced(AppHost host, Win32Window w, double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (!w.IsClosed && sw.Elapsed.TotalSeconds < seconds)
            {
                host.RunFrame();
                var wait = host.WaitRequestWithDetached();
                int remaining = Math.Max(0, (int)Math.Ceiling((seconds - sw.Elapsed.TotalSeconds) * 1000));
                wait = wait with { TimeoutMs = wait.TimeoutMs < 0 ? remaining : Math.Min(remaining, wait.TimeoutMs) };
                w.WaitForWork(in wait);
            }
        }

        static MenuOpen RestyleOpen(AppHost host, Win32Window w)
        {
            var row = FindTrackRow(host) ?? default;
            var at = new Point2(row.X + Math.Min(60f, row.W / 4f), row.Y + row.H / 2f);
            var o = MeasureOpen(host, w, at);
            PumpPaced(host, w, 1.5);
            PostEscape(w);
            PumpPaced(host, w, 1.0);
            return o;
        }

        /// <summary>Top-level popup windows (FluentGpuPopupWindow) this process holds, shown or hidden.</summary>
        static int PopupHwndCount(bool visibleOnly = false)
        {
            int pid = Environment.ProcessId, n = 0;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out int owner);
                if (owner != pid) return true;
                var cls = new StringBuilder(64);
                GetClassNameW(h, cls, cls.Capacity);
                if (cls.ToString() == "FluentGpuPopupWindow" && (!visibleOnly || IsWindowVisible(h))) n++;
                return true;
            }, 0);
            return n;
        }

        delegate bool EnumWindowsProc(nint hwnd, nint lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(nint hwnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);

        [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);

        static bool ArgFlag(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

        static int ArgInt(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, 0, 20) : 0;
        }

        static void PostKey(Win32Window w, nint vk)
        {
            nint hwnd = w.Handle.Value;
            PostMessageW(hwnd, 0x0100 /*WM_KEYDOWN*/, vk, 0x00010001);
            PostMessageW(hwnd, 0x0101 /*WM_KEYUP*/, vk, unchecked((nint)0xC0010001));
        }

        static void PostEscape(Win32Window w)
        {
            nint hwnd = w.Handle.Value;
            PostMessageW(hwnd, 0x0100 /*WM_KEYDOWN*/, 0x1B, 0x00010001);
            PostMessageW(hwnd, 0x0101 /*WM_KEYUP*/, 0x1B, unchecked((nint)0xC0010001));
        }

        [DllImport("user32.dll")]
        static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    }
}
