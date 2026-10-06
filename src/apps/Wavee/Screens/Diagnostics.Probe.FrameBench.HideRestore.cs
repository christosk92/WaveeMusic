// ── Screens/Diagnostics.Probe.FrameBench.HideRestore.cs ────────────────────────────────────────────────────────────
// the `hide-restore` frame-bench scenario: how much memory a window that is minimized, hidden to the tray or covered by another
// window holds, and what the way back costs
//
// Role: SHELL
// Owner: S
//
// Run it by name (it is opt-in: a default `--frame-bench` never minimizes the window):
//   Wavee.exe --fake --profile <scratch dir> --frame-bench=hide-restore [--bench-hide-cycles 3] [--bench-hidden-sec 30]
//       [--fg present-validate,damage-validate] --probe-out <dir>
// Three modes run in turn, each `--bench-hide-cycles` times: MINIMIZE (SW_MINIMIZE ... SW_RESTORE), HIDE (the tray: SW_HIDE ... SW_SHOW)
// and COVER (a topmost full-screen window over the app for 3 s, the alt-tab-away case, which must NOT release anything). Per cycle:
//   vis    a visible, settled window                                    (3 s)
//   hid5   hidden for 5 s: the Shallow release (2 s) has run and drained
//   hidEnd hidden for `--bench-hidden-sec` seconds
//   res    restored and settled again                                   (3 s)
// each phase sampled on the frame ledger's own memory sampler (process private bytes, working set, DXGI LOCAL usage, the engine's
// tracked GPU bytes, the image cache), plus `restoreMs` = the time from the restore call to the first present that followed (a restore
// whose frame is legitimately elided as unchanged presents nothing: it is counted in `noPresent`, not folded into the latency).
// Every cycle is one row of hide-restore-cycles.csv; the summary carries the per-mode medians and the validation counters
// (`--fg present-validate,damage-validate` make them mean something): presentBad, damageBad, exposedTileMissing, lostPlacements.
// The script ops/tools/hidden-mem.ps1 runs this and prints the table; docs/guide/hidden-memory.md says how to read it.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Hosting;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi.D3D12;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static D3D12Device? s_benchDevice;

        /// <summary>One cycle's four phase samples, the restore latency and whether a present followed the restore.</summary>
        readonly record struct HideCycle(string Mode, int Index, LedgerMemorySample Vis, LedgerMemorySample Hid5, LedgerMemorySample HidEnd,
            LedgerMemorySample Res, double RestoreMs, bool Presented, int TileBad, bool Parked, double HiddenMcPerSec);

        static Pending HideRestore(string name, AppHost host, Win32Window w, FrameBenchOptions o)
        {
            var mark = FrameLedger.Mark();
            Pump(host, w, o.WarmupSec, null);
            var cycles = new List<HideCycle>();
            foreach (string mode in new[] { "minimize", "hide", "cover" })
                for (int i = 1; i <= o.HideCycles && !w.IsClosed; i++)
                {
                    var c = OneHideCycle(mode, i, host, w, o, mark);
                    cycles.Add(c);
                    Say($"[hide-restore] {mode} #{i}: vis={Mb(c.Vis.PrivateBytes)}MB hid5={Mb(c.Hid5.PrivateBytes)}MB hidEnd={Mb(c.HidEnd.PrivateBytes)}MB res={Mb(c.Res.PrivateBytes)}MB "
                        + $"vramVis={Mb(c.Vis.VramLocalBytes)} vramHid={Mb(c.HidEnd.VramLocalBytes)} trackedVis={Mb(c.Vis.TrackedGpuBytes)} trackedHid={Mb(c.HidEnd.TrackedGpuBytes)} "
                        + $"restoreMs={(c.Presented ? c.RestoreMs.ToString("0.0", CultureInfo.InvariantCulture) : "no-present")}");
                }
            var snap = FrameLedger.Snapshot(mark);
            WriteArtifact(OutDir(), "hide-restore-cycles.csv", CyclesCsv(cycles));

            var extra = new List<KeyValuePair<string, double>>();
            foreach (string mode in new[] { "minimize", "hide", "cover" })
            {
                var rows = cycles.FindAll(c => c.Mode == mode && c.Parked);   // a cycle whose window never parked measured nothing hidden
                extra.Add(new(mode + ".notParked", cycles.Count(c => c.Mode == mode && !c.Parked)));
                if (rows.Count == 0) continue;
                void Add(string key, Func<HideCycle, double> f) => extra.Add(new(mode + "." + key, Median(rows, f)));
                Add("visPrivMB", c => c.Vis.PrivateBytes / 1048576.0);
                Add("hid5PrivMB", c => c.Hid5.PrivateBytes / 1048576.0);
                Add("hidEndPrivMB", c => c.HidEnd.PrivateBytes / 1048576.0);
                Add("resPrivMB", c => c.Res.PrivateBytes / 1048576.0);
                Add("visWsMB", c => c.Vis.WorkingSetBytes / 1048576.0);
                Add("hidEndWsMB", c => c.HidEnd.WorkingSetBytes / 1048576.0);
                Add("visVramMB", c => c.Vis.VramLocalBytes / 1048576.0);
                Add("hidEndVramMB", c => c.HidEnd.VramLocalBytes / 1048576.0);
                Add("visTrackedMB", c => c.Vis.TrackedGpuBytes / 1048576.0);
                Add("hidEndTrackedMB", c => c.HidEnd.TrackedGpuBytes / 1048576.0);
                Add("hiddenMcPerSec", c => c.HiddenMcPerSec);
                var lat = rows.FindAll(c => c.Presented).ConvertAll(c => c.RestoreMs);
                lat.Sort();
                extra.Add(new(mode + ".restoreMsP50", lat.Count > 0 ? lat[lat.Count / 2] : double.NaN));
                extra.Add(new(mode + ".restoreMsMax", lat.Count > 0 ? lat[^1] : double.NaN));
                extra.Add(new(mode + ".noPresent", rows.Count - lat.Count));
            }
            extra.Add(new("cycles", cycles.Count));
            if (s_benchDevice is { } d)
            {
                var pc = d.LastPresentCensus;
                var dc = d.LastDamageCensus;
                extra.Add(new("presentChecked", pc.Checked));
                extra.Add(new("presentBad", pc.CompositeMismatches + pc.UnderReports + pc.StaleScreens));
                extra.Add(new("damageValidated", dc.Validated));
                extra.Add(new("damageBad", dc.Mismatches));
            }
            extra.Add(new("tileBad", cycles.Sum(c => c.TileBad)));   // exposed-missing + lost placements read off the turn that followed each restore
            int notParked = cycles.Count(c => !c.Parked);
            if (notParked > 0) Say($"[hide-restore] FAILED: {notParked} cycle(s) never parked and were left out of the medians");
            return new Pending(name, Snap: snap, Note: $"{o.HideCycles} cycles x minimize/hide/cover, hidden {o.HiddenSec} s, cover {o.CoverSec} s" + (notParked > 0 ? $", {notParked} NOT PARKED" : ""), Extra: extra);
        }

        static HideCycle OneHideCycle(string mode, int index, AppHost host, Win32Window w, FrameBenchOptions o, LedgerMark mark)
        {
            Pump(host, w, 3.0, null);
            var vis = MemoryNow(mark);
            nint cover = 0;
            double hiddenSec = mode == "cover" ? o.CoverSec : o.HiddenSec;
            switch (mode)
            {
                case "minimize": ShowWindow(w.Handle.Value, 6 /*SW_MINIMIZE*/); break;
                case "hide": w.Hide(); break;
                default:
                    // Another top-level window completely over the app: the cover-park the host decides from the window-event hook.
                    cover = StressNative.CreateWindowExW(0x08000000 | 0x00000008 | 0x00000080 /*NOACTIVATE|TOPMOST|TOOLWINDOW*/, "STATIC", "Wavee hide-restore cover",
                        0x90000000 /*WS_POPUP|WS_VISIBLE*/, -32000, -32000, 100000, 100000, 0, 0, 0, 0);
                    ShowWindow(cover, 4 /*SW_SHOWNOACTIVATE*/);
                    break;
            }
            double first = Math.Min(5.0, hiddenSec);
            Pump(host, w, first, null);
            var hid5 = MemoryNow(mark);
            // The park must have happened (and, for a window that left the screen, the release with it) or the cycle measured nothing hidden.
            bool parked = host.IsParked && (mode != "minimize" || StressNative.IsIconic(w.Handle.Value) != 0) && (mode != "hide" || !w.IsVisible);
            if (parked && mode != "cover" && hiddenSec >= 5.0) parked = host.HiddenStageCensus != 0;
            if (hiddenSec > first) Pump(host, w, hiddenSec - first, null);
            var hidEnd = MemoryNow(mark);
            double hidSec = (hidEnd.Qpc - hid5.Qpc) / (double)Stopwatch.Frequency;
            double hiddenMc = hidSec > 1.0 && hidEnd.ProcessCyclesTotal >= hid5.ProcessCyclesTotal
                ? (hidEnd.ProcessCyclesTotal - hid5.ProcessCyclesTotal) / hidSec / 1e6 : double.NaN;   // QueryProcessCycleTime, not CPU time

            ulong seq0 = host.PresentedSequence;
            long t0 = Stopwatch.GetTimestamp();
            switch (mode)
            {
                case "minimize": ShowWindow(w.Handle.Value, 9 /*SW_RESTORE*/); break;
                case "hide": w.Show(); break;
                default: if (cover != 0) StressNative.DestroyWindow(cover); break;
            }
            bool presented = false;
            double restoreMs = double.NaN;
            // The restore frame is the first present AFTER the host un-parked: a present before that is not it, and one that arrives long
            // after (300 ms) is unrelated - a restore whose frame was legitimately elided counts as no-present, never as a latency.
            long unparkedAt = 0;
            while (!w.IsClosed)
            {
                double sinceMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (sinceMs > 2000 || (unparkedAt != 0 && (Stopwatch.GetTimestamp() - unparkedAt) * 1000.0 / Stopwatch.Frequency > 300)) break;
                Pump(host, w, 0.004, null);
                if (unparkedAt == 0 && !host.IsParked) { unparkedAt = Stopwatch.GetTimestamp(); seq0 = host.PresentedSequence; }
                if (unparkedAt != 0 && host.PresentedSequence > seq0)
                {
                    restoreMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    presented = true;
                    break;
                }
            }
            var tc = host.LastTileCensus;
            int tileBad = tc.ExposedMissing + tc.LostPlacements;
            Pump(host, w, 3.0, null);
            tc = host.LastTileCensus;
            tileBad += tc.ExposedMissing + tc.LostPlacements;
            var res = MemoryNow(mark);
            return new HideCycle(mode, index, vis, hid5, hidEnd, res, restoreMs, presented, tileBad, parked, hiddenMc);
        }

        /// <summary>One fresh memory sample from the ledger's sampler, read back.</summary>
        static LedgerMemorySample MemoryNow(LedgerMark mark)
        {
            s_benchDevice?.RefreshVideoMemorySnapshot();   // a hidden window presents nothing, so the cadence-driven snapshot would be the release-time one
            FrameLedger.SampleMemory();
            var m = FrameLedger.Snapshot(mark).Memory;
            return m.Length > 0 ? m[^1] : default;
        }

        static double Median(List<HideCycle> rows, Func<HideCycle, double> f)
        {
            var v = rows.ConvertAll(r => f(r));
            v.Sort();
            return v[v.Count / 2];
        }

        static string CyclesCsv(List<HideCycle> cycles)
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("mode,cycle,phase,privateMB,workingSetMB,vramLocalMB,trackedGpuMB,imageCacheMB,glyphAtlasMB,restoreMs,parked,hiddenMcPerSec");
            foreach (var c in cycles)
            {
                void Row(string phase, in LedgerMemorySample s, string restore) => sb.Append(c.Mode).Append(',').Append(c.Index).Append(',').Append(phase).Append(',')
                    .Append(Mb(s.PrivateBytes)).Append(',').Append(Mb(s.WorkingSetBytes)).Append(',').Append(Mb(s.VramLocalBytes)).Append(',').Append(Mb(s.TrackedGpuBytes)).Append(',')
                    .Append(Mb(s.ImageCacheBytes)).Append(',').Append(Mb(s.GlyphAtlasBytes)).Append(',').Append(restore).Append(',').Append(c.Parked ? 1 : 0).Append(',')
                    .AppendLine(double.IsNaN(c.HiddenMcPerSec) ? "" : c.HiddenMcPerSec.ToString("0.00", CultureInfo.InvariantCulture));
                Row("vis", c.Vis, "");
                Row("hid5", c.Hid5, "");
                Row("hidEnd", c.HidEnd, "");
                Row("res", c.Res, c.Presented ? c.RestoreMs.ToString("0.0", CultureInfo.InvariantCulture) : "no-present");
            }
            return sb.ToString();
        }
    }
}
