// ── Shell/Shell.Host.Placement.cs ─────────────────────────────────────────────────────────────────────────────────
// the main window reopens at the size, position and maximized state the user left it in
//
// Role: SHELL
// Named partial of `Shell.Host.cs`: the launch reads the setting before the window exists, the engine creates the window
// directly at it (`AppOptions.InitialBoundsPx` / `InitialMaximized`), and the placement edges write it back.
//
// THE SETTING. `shell.window.placement` = "x,y,w,h,max": the NORMAL (restore) outer rect in physical virtual-screen px and
// 1 when maximized (`FluentApp.MainWindowPlacement`, GetWindowPlacement semantics: a maximized window keeps its restore size).
//
// WHAT IS SAVED. Only a settled placement: shown, not minimized, not borderless full screen. So full screen (the video
// stage), the mini player and the tray (both hide the main window) and a minimize never overwrite where the user left it.
// One write per gesture: the engine raises `MainWindowPlacementChanged` once at the end of a drag (never per drag step) and
// at a snap / maximize / restore, and this file writes DebounceMs after the last of them; the exit flushes a pending one.
//
// WHAT IS NOT. The harness and capture launches (`--frames`, `--screenshot`, `--offscreen`) and an explicit
// `--width`/`--height` keep their own size and neither read nor write it; `--inspect` never reaches `Shell.Run`.
//
// NO STARTUP JUMP. The engine resolves the saved rect against today's monitors (clamped into the nearest work area when
// its monitor is gone or the resolution / DPI changed) the same way `FluentApp.ResolveMainWindowRestore` predicts here, so
// the zoom service seeds the first frame from the restored client size at the target monitor's DPI. A clamp is never
// written back on its own: plug the monitor in again and the saved rect is whole again, until the user moves the window.

using System.Globalization;
using System.Threading;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;

namespace Wavee;

public static partial class Shell
{
    static class WindowPlacementStore
    {
        /// <summary>Quiet time after the last placement edge before the write (a maximize or a snap can raise several).</summary>
        const int DebounceMs = 600;
        /// <summary>The save line's floor: at most one `[window.placement] save` line per this many ms (the rest are counted).</summary>
        const long SaveLineMinGapMs = 5000;
        /// <summary>A saved client smaller than this (DIP, either axis) is a corrupt value, not a placement: the default size wins.</summary>
        const float MinSaneDip = 200f;

        static bool s_armed;
        static string s_lastKnown = "";          // the placement the window has (launch) or last wrote: an equal edge writes nothing
        static string? s_pending;                // UI thread writes, the timer thread takes (Interlocked)
        static Timer? s_timer;
        static long s_lastSaveLineMs = -SaveLineMinGapMs;
        static int s_quietSaves;

        public static string Format(RectF r, bool maximized)
        {
            var ci = CultureInfo.InvariantCulture;
            return ((int)MathF.Round(r.X)).ToString(ci) + "," + ((int)MathF.Round(r.Y)).ToString(ci) + "," +
                   ((int)MathF.Round(r.W)).ToString(ci) + "," + ((int)MathF.Round(r.H)).ToString(ci) + "," + (maximized ? "1" : "0");
        }

        public static bool TryParse(string? raw, out RectF r, out bool maximized)
        {
            r = default;
            maximized = false;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parts = raw.Split(',');
            if (parts.Length != 5) return false;
            var ci = CultureInfo.InvariantCulture;
            if (!int.TryParse(parts[0], NumberStyles.Integer, ci, out int x) || !int.TryParse(parts[1], NumberStyles.Integer, ci, out int y)
                || !int.TryParse(parts[2], NumberStyles.Integer, ci, out int w) || !int.TryParse(parts[3], NumberStyles.Integer, ci, out int h)
                || parts[4] is not ("0" or "1"))
                return false;
            if (w <= 0 || h <= 0) return false;
            r = new RectF(x, y, w, h);
            maximized = parts[4] == "1";
            return true;
        }

        /// <summary>The saved placement resolved against the monitors as they are now, or null (nothing saved, or a corrupt
        /// value): the window then opens at the default size. Logs the one `[window.placement] restore` line.</summary>
        public static (RectF Saved, WindowRestorePlan Plan)? ResolveForLaunch()
        {
            string raw = Platform.Settings.Get(Platform.Keys.ShellWindowPlacement);
            if (!TryParse(raw, out RectF saved, out bool maximized))
            {
                Log.Info("window", raw.Length == 0
                    ? "[window.placement] restore none (default size)"
                    : "[window.placement] restore ignored: unreadable value '" + raw + "'");
                return null;
            }
            var plan = FluentApp.ResolveMainWindowRestore(saved, maximized, customFrame: true);
            if (plan.ClientSizeDip.Width < MinSaneDip || plan.ClientSizeDip.Height < MinSaneDip)
            {
                Log.Info("window", "[window.placement] restore ignored: client " + Dip(plan.ClientSizeDip) + " DIP is too small");
                return null;
            }
            // The baseline an edge must differ from to be written: what the window is created at (the clamped rect), so the reveal's
            // own show edge writes nothing and a clamp is not persisted until the user moves the window.
            s_lastKnown = Format(plan.BoundsPx, plan.Maximized);
            Log.Info("window", string.Create(CultureInfo.InvariantCulture,
                $"[window.placement] restore saved={raw} {(plan.Clamped ? "clamped" : "as-saved")} rect={Format(plan.BoundsPx, plan.Maximized)} dpi={plan.DpiScale * 96f:0} client={Dip(plan.ClientSizeDip)} DIP"));
            return (saved, plan);
        }

        /// <summary>Start following the main window's placement (before the loop starts; the engine raises on the UI thread).</summary>
        public static void Arm()
        {
            if (s_armed) return;
            s_armed = true;
            // The timer fires on a pool thread; the write is the UI thread's (Settings.Set raises SettingsChanged, a UI signal).
            s_timer = new Timer(static _ => s_marshal?.Invoke(s_writeDebounced), null, Timeout.Infinite, Timeout.Infinite);
            FluentApp.MainWindowPlacementChanged += OnPlacementChanged;
        }

        static void OnPlacementChanged()
        {
            // Hidden (the tray, the mini player), minimized or full screen: not the user's placement. The last settled one stands.
            if (FluentApp.MainWindowPlacement is not { IsSettledPlacement: true } p) return;
            string now = Format(p.NormalBoundsPx, p.Maximized);
            if (now == s_lastKnown) return;   // unchanged (a second edge of one maximize, the reveal's show): a pending write stays
            s_lastKnown = now;
            Interlocked.Exchange(ref s_pending, now);
            s_timer?.Change(DebounceMs, Timeout.Infinite);
        }

        static readonly Action s_writeDebounced = static () => WritePending("debounce", Platform.Settings);

        /// <summary>The exit's write: a placement still inside its debounce is saved now (the window is gone by then, so the
        /// last settled edge it raised is what it was left at). The UI loop has ended and this runs on the main thread, so it
        /// writes the backing store directly: no UI subscriber of SettingsChanged is left to tell.</summary>
        public static void Flush()
        {
            if (!s_armed) return;
            FluentApp.MainWindowPlacementChanged -= OnPlacementChanged;
            s_timer?.Dispose();
            s_timer = null;
            WritePending("exit", Platform.BackingSettings);
        }

        static void WritePending(string why, IAppSettings store)
        {
            string? value = Interlocked.Exchange(ref s_pending, null);
            if (value is null) return;
            store.Set(Platform.Keys.ShellWindowPlacement, value);
            long now = Environment.TickCount64;
            // The exit's line always lands (one per run): it is the last word on where the window was left.
            if (why != "exit" && now - Interlocked.Read(ref s_lastSaveLineMs) < SaveLineMinGapMs) { Interlocked.Increment(ref s_quietSaves); return; }
            Interlocked.Exchange(ref s_lastSaveLineMs, now);
            int quiet = Interlocked.Exchange(ref s_quietSaves, 0);
            Log.Info("window", "[window.placement] save " + value + " why=" + why + (quiet > 0 ? " (+" + quiet.ToString(CultureInfo.InvariantCulture) + " unlogged)" : ""));
        }

        static string Dip(Size2 s) => string.Create(CultureInfo.InvariantCulture, $"{s.Width:0}x{s.Height:0}");
    }

    /// <summary>`--width` / `--height` on the command line: an explicit size that the saved placement never overrides.</summary>
    static bool HasExplicitSize(string[] args) => Array.IndexOf(args, "--width") >= 0 || Array.IndexOf(args, "--height") >= 0;
}
