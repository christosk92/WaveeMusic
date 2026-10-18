// ── Screens/Diagnostics.Probe.FrameBench.NavGc.cs ──────────────────────────────────────────────────────────────────
// the `nav-gc` frame-bench scenario (opt-in): where a navigation's gen-0 collections land, measured on the real GUI loop
//
// Run it by name (it is opt-in):
//   Wavee.exe --fake --profile <scratch dir> --frame-bench=nav-gc [--theme zune] [--bench-nav-ms 700] [--bench-nav-count 40]
//             [--bench-nav-input] [--bench-nav-play] --probe-out <dir>
//
// It navigates through playlist / album / artist / home pages every `--bench-nav-ms` (programmatic navigation, like nav-burst)
// and samples EVERY frame around RunFrame: the UI thread's allocation, the process's (all threads, during the frame and in the
// gap before it), gen-0/1 collections, their pause and promoted bytes, and the host's quiet state (what holds
// AppHost.IsInteractionQuiet false). `--bench-nav-input` queues a pointer move on the window's own input queue every 40 ms (a
// hand on the mouse: the real input path from the pump on, no real cursor moved); `--bench-nav-play` plays the first playlist (its row's equalizer runs). Writes
// `nav-gc-frames.csv` (one row per frame) and `nav-gc-navs.csv` (one row per navigation); the note carries the summary:
// how many mount frames (the navigation's frame that allocated most on the UI thread) ran a gen-0 collection.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        readonly record struct NavGcFrame(double TMs, int Nav, int FrameInNav, double UiMs, long AllocUi, long AllocAll, long GapAll,
            int Gc0, int Gc1, double PauseMs, long PromotedBytes, bool NavGcInFrame, bool Quiet, WakeReasons Blockers, long HoldMs,
            long SinceGcBytes, bool Settled, long SinceInteractionMs, bool Foreground);

        static int ArgInt(string name, int fallback, int min, int max)
        {
            string[] a = Platform.Args.Argv;
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? Math.Clamp(v, min, max) : fallback;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern nint GetForegroundWindow();

        static Pending NavGc(string name, AppHost host, Win32Window w, FrameBenchTargets t)
        {
            int intervalMs = ArgInt("--bench-nav-ms", 700, 50, 10_000);
            int count = ArgInt("--bench-nav-count", 40, 4, 1000);
            bool input = Array.IndexOf(Platform.Args.Argv, "--bench-nav-input") >= 0;
            var home = new Shell.Route(Shell.RouteKind.Home);
            var hops = new List<(string Label, Shell.Route Route)>();
            int n = Math.Min(t.Playlists.Length, Math.Min(t.Albums.Length, t.Artists.Length));
            for (int i = 0; i < n; i++)
            {
                hops.Add((t.Playlists[i], Shell.For(EntityUri.Parse(t.Playlists[i]), "Bench playlist")));
                hops.Add((t.Albums[i], Shell.For(EntityUri.Parse(t.Albums[i]), "Bench album")));
                hops.Add((t.Artists[i], Shell.For(EntityUri.Parse(t.Artists[i]), "Bench artist")));
                hops.Add(("home", home));
            }
            if (hops.Count == 0) return new Pending(name, "no entities to visit");

            var frames = new List<NavGcFrame>(count * Math.Max(8, intervalMs / 8));
            var navs = new List<(string Label, double TMs, long SinceGcAtClick)>(count);
            var size = w.ClientSizePx;
            long t0 = Stopwatch.GetTimestamp();
            double Now() => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            int nav = -1, frameInNav = 0, navGcRuns = Residency.NavGcRuns, lastGc0 = GC.CollectionCount(0);
            long allocAtGc = GC.GetTotalAllocatedBytes(false), lastEndAll = allocAtGc;
            double nextNav = 0, nextMove = 0, end = (double)count * intervalMs;
            int move = 0;
            var mark = FrameLedger.Mark();
            while (!w.IsClosed)
            {
                double now = Now();
                if (now >= nextNav)
                {
                    if (nav + 1 >= count) break;
                    nav++;
                    frameInNav = 0;
                    var hop = hops[nav % hops.Count];
                    navs.Add((hop.Label, now, GC.GetTotalAllocatedBytes(false) - allocAtGc));
                    Nav(hop.Route);
                    nextNav = now + intervalMs;
                }
                if (input && now >= nextMove)
                {
                    // A hand resting on the mouse: small moves over the content area (no click, no real cursor).
                    int x = (int)(size.Width * 0.55) + (move % 7) * 9, y = (int)(size.Height * 0.5) + (move % 5) * 7;
                    w.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(x, y), 0, 0));
                    move++;
                    nextMove = now + 40;
                }
                long ui0 = GC.GetAllocatedBytesForCurrentThread(), all0 = GC.GetTotalAllocatedBytes(false);
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
                TimeSpan p0 = GC.GetTotalPauseDuration();
                long f0 = Stopwatch.GetTimestamp();
                host.RunFrame();
                double uiMs = (Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency;
                long all1 = GC.GetTotalAllocatedBytes(false);
                int gc0 = GC.CollectionCount(0) - g0, gc1 = GC.CollectionCount(1) - g1;
                long promoted = 0;
                if (gc0 > 0)
                {
                    GCMemoryInfo gi = GC.GetGCMemoryInfo(GCKind.Any);
                    promoted = gi.PromotedBytes;
                }
                if (GC.CollectionCount(0) != lastGc0) { lastGc0 = GC.CollectionCount(0); allocAtGc = all1; }
                bool navGc = Residency.NavGcRuns != navGcRuns;
                navGcRuns = Residency.NavGcRuns;
                frames.Add(new NavGcFrame(now, nav, frameInNav++, uiMs, GC.GetAllocatedBytesForCurrentThread() - ui0, all1 - all0, all0 - lastEndAll,
                    gc0, gc1, (GC.GetTotalPauseDuration() - p0).TotalMilliseconds, promoted, navGc, host.IsInteractionQuiet,
                    host.InteractionQuietBlockers, host.InteractionQuietInputHoldMs, all1 - allocAtGc,
                    host.IsSettledForPause(NavGcPolicy.InteractionQuietMs), host.MsSinceInteraction, GetForegroundWindow() == w.Handle.Value));
                lastEndAll = all1;
                host.TickDetachedHosts();
                var wait = host.WaitRequestWithDetached();
                int asked = wait.TimeoutMs;
                double until = Math.Min(nextNav, input ? nextMove : nextNav) - Now();
                int cap = Math.Max(0, (int)Math.Ceiling(until));
                wait = wait with { TimeoutMs = asked < 0 ? cap : Math.Min(cap, asked) };
                long ws = Stopwatch.GetTimestamp();
                w.WaitForWork(in wait);
                host.NoteLoopWait(ws, Stopwatch.GetTimestamp(), asked);
                if (now > end + 2 * intervalMs) break;
            }
            var snap = FrameLedger.Snapshot(mark);

            // Per navigation: the mount frame is the one that allocated most on the UI thread in the first 400 ms.
            var fcsv = new StringBuilder(frames.Count * 96).AppendLine("t_ms,nav,f,ui_ms,alloc_ui,alloc_all,gap_all,gc0,gc1,pause_ms,promoted,navgc,quiet,blockers,hold_ms,since_gc,settled,since_interaction,fg");
            foreach (var f in frames)
                fcsv.Append(CultureInfo.InvariantCulture, $"{f.TMs:0.0},{f.Nav},{f.FrameInNav},{f.UiMs:0.00},{f.AllocUi},{f.AllocAll},{f.GapAll},{f.Gc0},{f.Gc1},{f.PauseMs:0.00},{f.PromotedBytes},{(f.NavGcInFrame ? 1 : 0)},{(f.Quiet ? 1 : 0)},{(int)f.Blockers},{f.HoldMs},{f.SinceGcBytes},{(f.Settled ? 1 : 0)},{f.SinceInteractionMs},{(f.Foreground ? 1 : 0)}").AppendLine();
            var ncsv = new StringBuilder(navs.Count * 96).AppendLine("nav,route,since_gc_at_click,mount_f,mount_ui_ms,mount_alloc_ui,mount_gc0,mount_pause_ms,window_gc0,nav_alloc_ui,nav_alloc_all,navgc_at_ms,quiet_frames,frames");
            int mountGc = 0, windowGc = 0, navGcCount = 0;
            var mountMs = new List<double>(navs.Count);
            for (int k = 0; k < navs.Count; k++)
            {
                int best = -1;
                long allocUi = 0, allocAll = 0;
                int wgc = 0, quiet = 0, nf = 0;
                double navGcAt = double.NaN;
                for (int i = 0; i < frames.Count; i++)
                {
                    var f = frames[i];
                    if (f.Nav != k) continue;
                    nf++;
                    allocUi += f.AllocUi; allocAll += f.AllocAll + f.GapAll;
                    if (f.Quiet) quiet++;
                    if (f.NavGcInFrame && double.IsNaN(navGcAt)) navGcAt = f.TMs - navs[k].TMs;
                    if (f.TMs - navs[k].TMs > 400) continue;
                    wgc += f.Gc0;
                    if (best < 0 || f.AllocUi > frames[best].AllocUi) best = i;
                }
                if (best < 0) continue;
                var m = frames[best];
                if (m.Gc0 > 0) mountGc++;
                if (wgc > 0) windowGc++;
                if (!double.IsNaN(navGcAt)) navGcCount++;
                if (k > 0) mountMs.Add(m.UiMs);
                ncsv.Append(CultureInfo.InvariantCulture, $"{k},{navs[k].Label},{navs[k].SinceGcAtClick},{m.FrameInNav},{m.UiMs:0.00},{m.AllocUi},{m.Gc0},{m.PauseMs:0.00},{wgc},{allocUi},{allocAll},{navGcAt:0},{quiet},{nf}").AppendLine();
            }
            string dir = OutDir();
            WriteArtifact(dir, name + "-frames.csv", fcsv.ToString());
            WriteArtifact(dir, name + "-navs.csv", ncsv.ToString());
            mountMs.Sort();
            double P(double q) => mountMs.Count == 0 ? double.NaN : mountMs[Math.Min(mountMs.Count - 1, (int)(q * mountMs.Count))];
            string note = string.Create(CultureInfo.InvariantCulture,
                $"navs={navs.Count} every={intervalMs}ms input={input} mountGc0={mountGc} window400Gc0={windowGc} navGcRuns={navGcCount} mountUi p50={P(0.5):0.00} p95={P(0.95):0.00}");
            return new Pending(name, Snap: snap, Note: note);
        }
    }
}
