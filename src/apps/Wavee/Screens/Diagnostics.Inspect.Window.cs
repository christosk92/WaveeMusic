// ── Screens/Diagnostics.Inspect.Window.cs ──────────────────────────────────────────────────────────────────────────────
// the `window` verb: park the mounted window (minimized or hidden) and restore it, and move the engine's cold-maintenance
// clock ahead so its 30 s services (the scene slab trim above all) run on the next frame
//
// Role: SHELL (UI thread)
// Why: Wavee 0.3.7 crashed on the first painted frame after a long minimize (IndexOutOfRangeException in
// SceneStore.ClearSubtreeLayoutDirtyBits). Nothing in the inspector could park a window or wait out the trim, so the crash
// could not be reproduced headless.
//
// THE PATH IS PRODUCTION'S. `state=minimized` is the caption button's command (HeadlessWindow.Minimize: the placement reads
// Minimized), `state=hidden` a close-to-tray hide (the window reads invisible), and the host parks on its own next frame exactly
// as over a real window: AppHost.RunFrame skips Paint (no layout, record or present) but still pumps, drains the UI posts,
// flushes the reactive work they schedule and runs the cold maintenance. Every verb that steps frames (`play`, `goto`, `frames`,
// `settle`) then steps PARKED frames until `state=restored` puts the window back; the restore edge forces the next frame to
// paint, as WM_SIZE does.
//
// THE CLOCK: the cold services (ColdMaintenanceDeadline: 30 s after the slab last grew or shrank, once the host has been free of
// input for 2 s) read the OS uptime clock, not the 16 ms fake frame clock. `advance=31000` moves that clock 31 s ahead
// (AppHost.AdvanceColdMaintenanceClock) and the next frame runs whatever fell due. The reply's `scene` says whether the slab
// trim ran (`trimmed`) and what the next layout's subtree-dirty clear still holds (`dirty`: `pastCapacity` above 0 is the
// crash's precondition: a recorded slot a trim cut off).
//
// ACTIVATION: `active=false` blurs the window (HeadlessWindow.IsActive, which queues the WindowBlur a Win32 WM_ACTIVATE sends: the
// dispatcher drops the pointer, clears hover, and announces a pointer that was in as leaving the window before the blur itself),
// `active=true` re-activates it (WindowFocus; the pointer is back only with the next real move, a `hover`). The fullscreen stage's
// chrome hears the blur as a leave (`[stage.chrome] exit as=left src=window` for a pointer that was in, then `src=blur`).

using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`window {state?, active?, advance?, floor?, frames?, + the settle and trace args (a restore)}`.</summary>
        string Window(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            string state = a.Str("state");
            long advance = (long)a.Float("advance", 0f);
            var window = shell.Window;
            var host = shell.Host;
            SceneStore scene = host.Scene;
            if (a.Has("floor")) scene.CapacityFloor = a.Int("floor", 0);
            int capBefore = scene.Capacity, highBefore = scene.HighWater, runsBefore = host.ColdMaintenanceRunCount;
            switch (state)
            {
                case "minimized": window.Minimize(); break;
                case "hidden": window.Hide(); break;
                case "restored":
                    if (window.State == WindowState.Minimized) window.State = WindowState.Normal;
                    if (!window.IsVisible) window.Show();
                    break;
            }
            if (a.Has("active")) window.IsActive = a.Bool("active", true);
            if (advance > 0) host.AdvanceColdMaintenanceClock(advance);

            bool restored = state == "restored";
            int frames = restored ? 1 : Math.Clamp(a.Int("frames", 2), 0, 100_000);
            // The first frame after the change runs here, not inside a settle: a frame that throws (the restore crash) replies with
            // its stack, so the reply names the engine frame that failed.
            try { RunFrames(frames); }
            catch (Exception ex)
            {
                Log.Warn("inspect", "window: a frame threw", ex);
                return Inspect.Reply.Fail(id, name, ex.GetType().Name + ": " + ex.Message, w =>
                {
                    w.WriteStartArray("stack");
                    foreach (string line in (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        w.WriteStringValue(line);
                    w.WriteEndArray();
                    WriteWindowFacts(w, shell, capBefore, highBefore, runsBefore);
                });
            }
            void Body(Utf8JsonWriter w)
            {
                w.WriteNumber("framesRun", frames);
                WriteWindowFacts(w, shell, capBefore, highBefore, runsBefore);
            }
            if (restored) return Settled(id, name, a, Body);
            return Inspect.Reply.Ok(id, name, Body);
        }

        static void WriteWindowFacts(Utf8JsonWriter w, Shell.HeadlessShell shell, int capBefore, int highBefore, int runsBefore)
        {
            var window = shell.Window;
            var host = shell.Host;
            SceneStore scene = host.Scene;
            w.WriteString("state", !window.IsVisible ? "hidden" : window.State == WindowState.Minimized ? "minimized" : "normal");
            w.WriteBoolean("active", window.IsActive);
            w.WriteBoolean("pointerInWindow", host.Input.PointerInWindow);
            w.WriteBoolean("parked", host.IsParked);
            w.WriteNumber("coldRuns", host.ColdMaintenanceRunCount - runsBefore);
            w.WriteStartObject("scene");
            w.WriteNumber("capacity", scene.Capacity);
            w.WriteNumber("capacityBefore", capBefore);
            w.WriteBoolean("trimmed", scene.Capacity < capBefore);
            w.WriteNumber("highWater", scene.HighWater);
            w.WriteNumber("highWaterBefore", highBefore);
            w.WriteNumber("live", scene.LiveCount);
            NodeHandle top = scene.HighestLiveNode();   // what holds the trim up: its slot and its nearest key
            w.WriteNumber("highestLive", top.IsNull ? 0 : (int)top.Raw.Index);
            if (!top.IsNull) w.WriteString("highestLiveKey", InspectKit.KeyOf(scene, top));
            w.WriteNumber("floor", scene.CapacityFloor);
            var (count, maxSlot, past) = scene.SubtreeLayoutDirtyCensus();
            w.WriteStartObject("dirty");
            w.WriteNumber("pending", count);
            w.WriteNumber("maxSlot", maxSlot);
            w.WriteNumber("pastCapacity", past);
            w.WriteEndObject();
            w.WriteEndObject();
        }
    }
}
