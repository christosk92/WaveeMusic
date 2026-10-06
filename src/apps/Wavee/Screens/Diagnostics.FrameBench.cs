// ── Screens/Diagnostics.FrameBench.cs ──────────────────────────────────────────────────────────────────────────────
// the `--frame-bench` arm's PURE half: its argv, the real-data target pick, and the per-scenario summary of a frame-ledger
// window (FluentGpu's FrameLedger) with its JSON and its stdout table
//
// Role: SHELL
// Owner: S
//
// The arm itself (Diagnostics.Probe.FrameBench.cs) drives the process, the frame loop and the scenarios; every DECISION it
// makes lives here and is pinned by FrameBenchTests: which scenarios run for how long, which real URIs a profile yields,
// and every number in frame-bench-summary.json — the file ops/tools/frame-bench-compare.ps1 diffs before/after.
// Nothing here starts the engine or touches a file except the profile reads in `Targets.FromProfile` (READ-ONLY).
//
// THE SUMMARY'S CPU (2.1): the HEADLINE is now the RAW-CYCLE metrics (processGcyclesPerSec, uiMcyclesPerSec, renderMcyclesPerSec,
// otherMcyclesPerSec, uiKcyclesPerPaintedFrame, renderKcyclesPerTurn), computed from the ledger's raw cycle counters with no rate:
// GetProcessTimes/GetThreadTimes are charged in whole ~15.6 ms scheduler ticks and read two modes at low load. The time-based
// figures below remain in the JSON as info only. Original description (pre-2.1):
// THE SUMMARY'S CPU. The HEADLINE process figure is `processCpuPct`, from GetProcessTimes (scheduler-tick accounted, exact over
// a window of seconds, no rate). The split comes from the ledger's cycle counts (QueryThreadCycleTime per thread,
// QueryProcessCycleTime for the process), every window of a run converted at ONE rate: the run's effective cycles per CPU-ms
// (Δ process cycles / Δ GetProcessTimes over the whole run). Over a window:
//   ui     = the UI thread's cumulative cycles across the window (frames AND everything between them),
//   render = the render thread's turn cycles, each turn attributed to the window (and to a frame interval) by its overlap,
//   other  = process - ui - render (decode, audio, network, the GC, the ledger's own sampler),
// each as average cores. `uiCoresTimes` / `renderCoresTimes` are the same two threads from GetThreadTimes (no rate) — the
// cross-check. Per-frame distributions are per RunFrame (ui), per painted RunFrame (painted), per presented render turn
// (render) and per UI frame interval (other: process − UI − the turns' overlapping share, UNCLAMPED, so its mean stays unbiased;
// a single interval can read slightly negative where the two counters' read instants skew).
// Totals (GC, missed vsyncs, underruns) are normalised per second so windows of different lengths compare.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentGpu.Hosting;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The `--frame-bench` scenarios, in the order they run.</summary>
    public static class FrameBenchScenarios
    {
        public const string Idle = "idle", IdlePlaying = "idle-playing", HomeScroll = "home-scroll", NavBurst = "nav-burst",
            PlaylistOpen = "playlist-open", PlaylistScroll = "playlist-scroll", Lyrics = "lyrics", LyricsLine = "lyrics-line",
            StageLyrics = "stage-lyrics", StageVisualizer = "stage-visualizer", TrackChange = "track-change", Video = "video",
            LedgerOverhead = "ledger-overhead", GpuPassOverhead = "gpu-pass-overhead", HideRestore = "hide-restore";

        /// <summary>Every scenario, in run order. <see cref="Video"/> runs only with `--fake-video`, <see cref="GpuPassOverhead"/> only
        /// with `--bench-gpu-passes`.</summary>
        public static readonly string[] All =
        [
            Idle, IdlePlaying, HomeScroll, NavBurst, PlaylistOpen, PlaylistScroll, Lyrics, LyricsLine, StageLyrics,
            StageVisualizer, TrackChange, Video, LedgerOverhead, GpuPassOverhead,
        ];

        /// <summary>Scenarios that run ONLY when named (`--frame-bench=hide-restore`): <see cref="HideRestore"/> minimizes and hides the
        /// window for tens of seconds per cycle, so a default run never does it.</summary>
        public static readonly string[] OptIn = [HideRestore];
    }

    /// <summary>`--frame-bench[=a,b]` and its knobs. Out-of-range or garbage values fall back to the default (the
    /// `ProbeOptions` rule). <see cref="Real"/> is EXPLICIT: real data (the profile's account, real playback) is never implied.
    /// <see cref="GpuPasses"/> (`--bench-gpu-passes`) turns the pass-granular GPU timeline on for the run (it adds timestamp queries
    /// at every pass boundary, so it is opt-in, and the run then measures its cost).</summary>
    public readonly record struct FrameBenchOptions(bool Enabled, string[] Scenarios, int MeasureSec, int WarmupSec, bool Real,
        string Label, IReadOnlyDictionary<string, string> Uris, bool GpuPasses = false, int HideCycles = 3, int HiddenSec = 30)
    {
        public static FrameBenchOptions Parse(string[] args)
        {
            bool on = false;
            string[] filter = [];
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--frame-bench")
                {
                    on = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) filter = Split(args[i + 1]);
                }
                else if (a.StartsWith("--frame-bench=", StringComparison.Ordinal)) { on = true; filter = Split(a["--frame-bench=".Length..]); }
            }
            var scenarios = new List<string>();
            foreach (string s in FrameBenchScenarios.All)
                if (filter.Length == 0 || Array.IndexOf(filter, s) >= 0) scenarios.Add(s);
            foreach (string s in FrameBenchScenarios.OptIn)
                if (Array.IndexOf(filter, s) >= 0) scenarios.Add(s);
            // A filter that named nothing known runs nothing, and says so (the arm reports the unknown names).
            return new FrameBenchOptions(on, scenarios.ToArray(), Int(args, "--bench-sec", 10, 3, 120), Int(args, "--bench-warmup-sec", 2, 0, 30),
                Array.IndexOf(args, "--bench-real") >= 0, Value(args, "--bench-label") ?? "", ParseUris(Value(args, "--bench-uris")),
                Array.IndexOf(args, "--bench-gpu-passes") >= 0, Int(args, "--bench-hide-cycles", 3, 1, 100), Int(args, "--bench-hidden-sec", 30, 3, 900));
        }

        /// <summary>The filter's names that are not scenarios (reported, never silently dropped).</summary>
        public static string[] UnknownNames(string[] args)
        {
            var bad = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string[] names = args[i] == "--frame-bench" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? Split(args[i + 1])
                    : args[i].StartsWith("--frame-bench=", StringComparison.Ordinal) ? Split(args[i]["--frame-bench=".Length..]) : [];
                foreach (string n in names) if (Array.IndexOf(FrameBenchScenarios.All, n) < 0 && Array.IndexOf(FrameBenchScenarios.OptIn, n) < 0) bad.Add(n);
            }
            return bad.ToArray();
        }

        /// <summary>Why a real-data run must not start, or null to go: `--bench-real` drives the account of the profile it runs in and
        /// writes its settings, so it needs `--profile` (an explicit profile root) — never the owner's default profile and registry.</summary>
        public static string? RealRefusal(bool fake, bool real, string profileRoot)
            => fake ? null
             : !real ? "without --fake this would drive the profile's real account. Add --bench-real to mean it, or run with --fake."
             : profileRoot.Length == 0 ? "--bench-real needs --profile <bench profile>: it plays on that profile's account and sets its settings, never the default profile's."
             : null;

        /// <summary>`--bench-uris key=uri,key=uri`: <c>track</c>, <c>lyrics</c> (word-synced), <c>lyrics-line</c>, <c>album</c>,
        /// <c>playlist</c>, <c>big</c> (the long list to scroll; <c>liked</c> = Liked Songs), <c>artist</c>.</summary>
        public static IReadOnlyDictionary<string, string> ParseUris(string? spec)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(spec)) return d;
            foreach (string part in Split(spec))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && eq < part.Length - 1) d[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
            return d;
        }

        static string[] Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static string? Value(string[] args, string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }

        static int Int(string[] args, string flag, int fallback, int lo, int hi)
            => int.TryParse(Value(args, flag), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) && v >= lo && v <= hi ? v : fallback;
    }

    /// <summary>What a run plays and opens: the fake catalogue's ids under `--fake`, the profile's own recent entities under
    /// `--bench-real` (or explicit `--bench-uris`). Lists are most-recent first.</summary>
    public sealed record FrameBenchTargets(string[] Tracks, string[] Albums, string[] Playlists, string[] Artists, string? Big)
    {
        /// <summary>The `--fake` catalogue (Entities.Fake.cs): tr*, al0-12, pl0-6, ar0-11; the big list is its 166 liked tracks.</summary>
        public static FrameBenchTargets Fake { get; } = new(
            ["spotify:track:tr0", "spotify:track:tr1", "spotify:track:tr2", "spotify:track:tr3"],
            Ids("spotify:album:al", 13), Ids("spotify:playlist:pl", 7), Ids("spotify:artist:ar", 12), "liked");

        static string[] Ids(string prefix, int n)
        {
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = prefix + i.ToString(CultureInfo.InvariantCulture);
            return a;
        }

        /// <summary>From `play-recency.json` ({uri: unix ms}) — the entities the user actually played, newest first — and
        /// `history.json`'s navigation names (<c>album:uri</c>, <c>pl:uri</c>, <c>artist:uri</c>) for anything recency lacks.
        /// <paramref name="explicitUris"/> (`--bench-uris`) go first in their lists. Pure over the two documents' text.</summary>
        public static FrameBenchTargets FromProfileText(string? recencyJson, string? historyJson, IReadOnlyDictionary<string, string> explicitUris, int perKind = 12)
        {
            var tracks = new List<string>(); var albums = new List<string>(); var playlists = new List<string>(); var artists = new List<string>();
            void Add(string uri)
            {
                var list = uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? tracks
                    : uri.StartsWith("spotify:album:", StringComparison.Ordinal) ? albums
                    : uri.StartsWith("spotify:playlist:", StringComparison.Ordinal) ? playlists
                    : uri.StartsWith("spotify:artist:", StringComparison.Ordinal) ? artists : null;
                if (list is not null && list.Count < perKind && !list.Contains(uri)) list.Add(uri);
            }
            foreach (var kv in explicitUris)
                if (kv.Key is "track" or "lyrics" or "lyrics-line" or "album" or "playlist" or "artist") Add(kv.Value);
            if (recencyJson is { Length: > 0 })
            {
                try
                {
                    using var doc = JsonDocument.Parse(recencyJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var rows = new List<(string Uri, long Ms)>();
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out long ms)) rows.Add((p.Name, ms));
                        rows.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));
                        foreach (var r in rows) Add(r.Uri);
                    }
                }
                catch (JsonException) { }
            }
            if (historyJson is { Length: > 0 })
            {
                try
                {
                    using var doc = JsonDocument.Parse(historyJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        // Newest last in the log: walk it backwards.
                        var items = doc.RootElement.EnumerateArray().ToArray();
                        for (int i = items.Length - 1; i >= 0; i--)
                            if (items[i].ValueKind == JsonValueKind.Object && items[i].TryGetProperty("name", out var n) && n.GetString() is { } name)
                            {
                                int colon = name.IndexOf(":spotify:", StringComparison.Ordinal);
                                if (colon > 0) Add(name[(colon + 1)..]);
                            }
                    }
                }
                catch (JsonException) { }
            }
            string? big = explicitUris.TryGetValue("big", out var b) ? b : "liked";
            return new FrameBenchTargets(tracks.ToArray(), albums.ToArray(), playlists.ToArray(), artists.ToArray(), big);
        }
    }

    /// <summary>One scenario's numbers. <see cref="Metrics"/> is flat (<c>uiCpuMs.p95</c>, <c>gpuBusyPct</c>, ...) and ordered, so
    /// the JSON and the compare tool walk the same keys.</summary>
    public sealed record FrameBenchScenarioSummary(string Name, string? Skipped, IReadOnlyList<KeyValuePair<string, double>> Metrics, string? Note = null)
    {
        public double this[string key]
        {
            get { foreach (var kv in Metrics) if (kv.Key == key) return kv.Value; return double.NaN; }
        }
    }

    /// <summary>One arm of an off/on overhead A/B (ledger-overhead, gpu-pass-overhead), RAW: the arm's frames, the UI thread's cycles
    /// and its RunFrame wall time across them, the window's wall time and the process's CPU time (GetProcessTimes) in it, and the
    /// GPU milliseconds the ledger saw (NaN when the ledger was off).</summary>
    public readonly record struct OverheadArm(int Frames, ulong UiCycles, double RunFrameWallMs, double WallMs, double ProcessCpuMs, double GpuMsAvg = double.NaN)
    {
        public OverheadArm Add(in OverheadArm o) => new(Frames + o.Frames, UiCycles + o.UiCycles, RunFrameWallMs + o.RunFrameWallMs,
            WallMs + o.WallMs, ProcessCpuMs + o.ProcessCpuMs,
            double.IsNaN(GpuMsAvg) ? o.GpuMsAvg : double.IsNaN(o.GpuMsAvg) ? GpuMsAvg : (GpuMsAvg + o.GpuMsAvg) / 2);
    }

    /// <summary>The summary maths over one ledger window (pure; FrameBenchTests).</summary>
    public static class FrameBenchMath
    {
        /// <summary>Nearest-rank percentile over a sorted COPY; NaN for no values.</summary>
        public static double Percentile(IReadOnlyList<double> values, double p)
        {
            if (values.Count == 0) return double.NaN;
            var a = new double[values.Count];
            for (int i = 0; i < a.Length; i++) a[i] = values[i];
            Array.Sort(a);
            int rank = (int)Math.Ceiling(p * a.Length) - 1;
            return a[Math.Clamp(rank, 0, a.Length - 1)];
        }

        static void Dist(List<KeyValuePair<string, double>> m, string name, List<double> v)
        {
            double sum = 0, max = v.Count > 0 ? double.MinValue : double.NaN;
            foreach (double x in v) { sum += x; if (x > max) max = x; }
            m.Add(new(name + ".avg", v.Count > 0 ? sum / v.Count : double.NaN));
            m.Add(new(name + ".p50", Percentile(v, 0.50)));
            m.Add(new(name + ".p95", Percentile(v, 0.95)));
            m.Add(new(name + ".p99", Percentile(v, 0.99)));
            m.Add(new(name + ".max", max));
        }

        /// <summary>The share of <paramref name="t"/>'s cycles that falls inside (<paramref name="from"/>, <paramref name="to"/>], by
        /// time overlap (a zero-length turn counts whole where its end lands).</summary>
        static double Overlap(in LedgerRenderTurn t, long from, long to)
        {
            long s = t.StartQpc, e = t.EndQpc;
            if (e <= s) return e > from && e <= to ? t.Cycles : 0;
            long lo = Math.Max(s, from), hi = Math.Min(e, to);
            return hi > lo ? t.Cycles * (double)(hi - lo) / (e - s) : 0;
        }

        static readonly string[] s_wakeNames = BuildWakeNames();
        static string[] BuildWakeNames()
        {
            var n = new string[32];
            foreach (WakeReasons r in Enum.GetValues<WakeReasons>())
            {
                uint v = (uint)r;
                if (v != 0 && (v & (v - 1)) == 0) n[System.Numerics.BitOperations.TrailingZeroCount(v)] = r.ToString();
            }
            return n;
        }

        /// <summary>Summarize one window. <paramref name="snap"/> is <c>FrameLedger.Snapshot(mark)</c> taken at the window's end, read
        /// at the run's one rate (<c>LedgerSnapshot.WithRate</c>); <paramref name="extra"/> metrics (a scroll distance, a settle
        /// time) are appended as given.</summary>
        public static FrameBenchScenarioSummary Summarize(string name, LedgerSnapshot snap, string? note = null,
            IReadOnlyList<KeyValuePair<string, double>>? extra = null)
        {
            var m = new List<KeyValuePair<string, double>>(160);
            double wallMs = snap.WallMs, wallSec = wallMs / 1000.0;
            double PerSec(double v) => wallSec > 0 ? v / wallSec : double.NaN;
            var ui = snap.Ui;
            var rt = snap.Render;
            m.Add(new("wallSec", wallSec));
            m.Add(new("frames", ui.Length));
            int painted = 0;
            foreach (var f in ui) if ((f.Flags & (ushort)LedgerUiFlags.Painted) != 0) painted++;
            m.Add(new("paintedFrames", painted));

            // Presents: render turns that called Present on the primary; an inline (no render thread) host counts its own.
            int presents = 0;
            var presentGaps = new List<double>();
            long prevDone = 0;
            foreach (var t in rt)
            {
                if ((t.Flags & (byte)LedgerTurnFlags.Presented) == 0) continue;
                presents++;
                if (prevDone != 0 && t.DoneQpc > prevDone) presentGaps.Add(snap.SpanMs(prevDone, t.DoneQpc));
                prevDone = t.DoneQpc;
            }
            if (rt.Length == 0 && ui.Length > 1) presents = (int)Math.Max(0, ui[^1].PresentedTotal - ui[0].PresentedTotal);
            m.Add(new("presents", presents));
            m.Add(new("presentsPerSec", PerSec(presents)));
            m.Add(new("framesPerSec", PerSec(ui.Length)));
            m.Add(new("paintedFramesPerSec", PerSec(painted)));
            Dist(m, "presentIntervalMs", presentGaps);

            // The census: which gate each RunFrame left by, what woke the frames, what the render turns did.
            var exits = new int[8];
            var wakes = new int[32];
            foreach (var f in ui)
            {
                if (f.Exit < exits.Length) exits[f.Exit]++;
                for (uint w = f.WakeMask; w != 0; w &= w - 1) wakes[System.Numerics.BitOperations.TrailingZeroCount(w)]++;
            }
            foreach (LedgerFrameExit e in Enum.GetValues<LedgerFrameExit>()) m.Add(new("exitPerSec." + e, PerSec(exits[(int)e])));
            for (int b = 0; b < 32; b++) if (wakes[b] > 0 && s_wakeNames[b] is { } wn) m.Add(new("wakePerSec." + wn, PerSec(wakes[b])));
            var kinds = new int[8];
            foreach (var t in rt) if (t.Kind < kinds.Length) kinds[t.Kind]++;
            foreach (LedgerTurnKind k in Enum.GetValues<LedgerTurnKind>()) m.Add(new("turnPerSec." + k, PerSec(kinds[(int)k])));

            // Per-frame distributions.
            var uiCpu = new List<double>(ui.Length);
            var uiWall = new List<double>(ui.Length);
            var paintedCpu = new List<double>(painted);
            var paintedWall = new List<double>(painted);
            double uiAllocSum = 0, paintedAllocSum = 0;
            foreach (var f in ui)
            {
                double cpu = snap.CyclesMs(f.UiCycles), wall = snap.SpanMs(f.StartQpc, f.EndQpc);
                uiCpu.Add(cpu);
                uiWall.Add(wall);
                uiAllocSum += f.AllocBytes;
                if ((f.Flags & (ushort)LedgerUiFlags.Painted) == 0) continue;
                paintedCpu.Add(cpu);
                paintedWall.Add(wall);
                paintedAllocSum += f.AllocBytes;
            }
            Dist(m, "uiCpuMs", uiCpu);
            Dist(m, "paintedCpuMs", paintedCpu);
            Dist(m, "uiFrameMs", uiWall);
            Dist(m, "paintedFrameMs", paintedWall);
            var rCpu = new List<double>();
            var rWork = new List<double>();
            double rAlloc = 0;
            foreach (var t in rt)
            {
                rAlloc += t.AllocBytes;
                if ((t.Flags & (byte)LedgerTurnFlags.Presented) == 0) continue;
                rCpu.Add(snap.CyclesMs(t.Cycles));
                rWork.Add(snap.SpanMs(t.StartQpc, t.EndQpc));
            }
            Dist(m, "renderCpuMs", rCpu);
            Dist(m, "renderTurnMs", rWork);

            // Window CPU split (cumulative counters; see the file header). Render turns are attributed by overlap.
            double uiMs = double.NaN, procMs = double.NaN, renderMs = double.NaN, spanMs = double.NaN, uiTimesMs = double.NaN;
            var otherPerFrame = new List<double>();
            double uiCycRaw = double.NaN, procCycRaw = double.NaN, renderCycRaw = double.NaN;   // RAW cycles (no rate): the rate-free CPU headline
            if (ui.Length >= 2)
            {
                long s0 = ui[0].EndQpc, s1 = ui[^1].EndQpc;
                spanMs = snap.SpanMs(s0, s1);
                if (ui[^1].UiCyclesTotal >= ui[0].UiCyclesTotal && ui[0].UiCyclesTotal != 0) uiMs = snap.CyclesMs(ui[^1].UiCyclesTotal - ui[0].UiCyclesTotal);
                if (ui[^1].ProcessCyclesTotal >= ui[0].ProcessCyclesTotal && ui[0].ProcessCyclesTotal != 0) procMs = snap.CyclesMs(ui[^1].ProcessCyclesTotal - ui[0].ProcessCyclesTotal);
                if (ui[^1].UiCyclesTotal >= ui[0].UiCyclesTotal && ui[0].UiCyclesTotal != 0) uiCycRaw = ui[^1].UiCyclesTotal - ui[0].UiCyclesTotal;
                if (ui[^1].ProcessCyclesTotal >= ui[0].ProcessCyclesTotal && ui[0].ProcessCyclesTotal != 0) procCycRaw = ui[^1].ProcessCyclesTotal - ui[0].ProcessCyclesTotal;
                if (ui[0].UiCpuTimeTotal != 0 && ui[^1].UiCpuTimeTotal >= ui[0].UiCpuTimeTotal) uiTimesMs = (ui[^1].UiCpuTimeTotal - ui[0].UiCpuTimeTotal) / 10_000.0;
                double rc = 0;
                int j = 0;
                for (int i = 1; i < ui.Length; i++)
                {
                    long a0 = ui[i - 1].EndQpc, a1 = ui[i].EndQpc;
                    while (j < rt.Length && rt[j].EndQpc <= a0) j++;
                    double inInterval = 0;
                    for (int k = j; k < rt.Length && rt[k].StartQpc < a1; k++) inInterval += Overlap(in rt[k], a0, a1);
                    rc += inInterval;
                    ref readonly var a = ref ui[i - 1];
                    ref readonly var b = ref ui[i];
                    if (a.ProcessCyclesTotal != 0 && b.ProcessCyclesTotal >= a.ProcessCyclesTotal && b.UiCyclesTotal >= a.UiCyclesTotal && snap.CyclesPerMs > 0)
                        otherPerFrame.Add((b.ProcessCyclesTotal - a.ProcessCyclesTotal - (double)(b.UiCyclesTotal - a.UiCyclesTotal) - inInterval) / snap.CyclesPerMs);
                }
                renderMs = snap.CyclesPerMs > 0 ? rc / snap.CyclesPerMs : double.NaN;
                renderCycRaw = rc;
            }
            Dist(m, "otherCpuMs", otherPerFrame);
            double Cores(double cpuMs) => spanMs > 0 ? cpuMs / spanMs : double.NaN;
            m.Add(new("uiCores", Cores(uiMs)));
            m.Add(new("renderCores", Cores(renderMs)));
            m.Add(new("otherCores", Cores(procMs - (double.IsNaN(uiMs) ? 0 : uiMs) - (double.IsNaN(renderMs) ? 0 : renderMs))));
            m.Add(new("processCores", Cores(procMs)));
            m.Add(new("uiCoresTimes", Cores(uiTimesMs)));
            double renderTimesCores = double.NaN;
            if (rt.Length >= 2 && rt[0].CpuTimeTotal != 0 && rt[^1].CpuTimeTotal >= rt[0].CpuTimeTotal)
            {
                double ms = snap.SpanMs(rt[0].EndQpc, rt[^1].EndQpc);
                if (ms > 0) renderTimesCores = (rt[^1].CpuTimeTotal - rt[0].CpuTimeTotal) / 10_000.0 / ms;
            }
            m.Add(new("renderCoresTimes", renderTimesCores));
            // The headline process figure, from GetProcessTimes through the memory stream; the cycle figure beside it.
            var mem = snap.Memory;
            double timesPct = double.NaN;
            if (mem.Length >= 2 && mem[^1].ProcessCpuTicksTotal > mem[0].ProcessCpuTicksTotal)
            {
                double ms = snap.SpanMs(mem[0].Qpc, mem[^1].Qpc);
                if (ms > 0) timesPct = (mem[^1].ProcessCpuTicksTotal - mem[0].ProcessCpuTicksTotal) / 10_000.0 / ms / Math.Max(1, snap.Processors) * 100.0;
            }
            m.Add(new("processCpuPct", timesPct));
            m.Add(new("processCpuPctCycles", spanMs > 0 && snap.Processors > 0 ? procMs / spanMs / snap.Processors * 100.0 : double.NaN));
            m.Add(new("cyclesPerMs.window", snap.EffectiveCyclesPerMs(250)));

            // Rate-free CPU (raw QueryProcessCycleTime / QueryThreadCycleTime deltas; stable to a few %, unlike the tick-charged times).
            double PerSecSpan(double cyc, double scale) => spanMs > 0 && double.IsFinite(cyc) ? cyc / (spanMs / 1000.0) / scale : double.NaN;
            double otherCycRaw = double.IsFinite(procCycRaw) ? procCycRaw - (double.IsFinite(uiCycRaw) ? uiCycRaw : 0) - (double.IsFinite(renderCycRaw) ? renderCycRaw : 0) : double.NaN;
            double uiWindowCyc = 0;
            foreach (var f in ui) uiWindowCyc += f.UiCycles;
            double renderTurnCyc = 0;
            foreach (var t in rt) renderTurnCyc += t.Cycles;
            m.Add(new("processGcyclesPerSec", PerSecSpan(procCycRaw, 1e9)));
            m.Add(new("uiMcyclesPerSec", PerSecSpan(uiCycRaw, 1e6)));
            m.Add(new("renderMcyclesPerSec", PerSecSpan(renderCycRaw, 1e6)));
            m.Add(new("otherMcyclesPerSec", PerSecSpan(otherCycRaw, 1e6)));
            m.Add(new("uiKcyclesPerPaintedFrame", painted > 0 ? uiWindowCyc / painted / 1e3 : double.NaN));
            m.Add(new("renderKcyclesPerTurn", rt.Length > 0 ? renderTurnCyc / rt.Length / 1e3 : double.NaN));

            // GPU.
            var gpuMs = new List<double>(snap.Gpu.Length);
            double gpuSum = 0;
            int passFrames = 0, missedSamples = 0;
            double up = 0, blur = 0, clear = 0, scene = 0, glyph = 0, tile = 0, off = 0, comp = 0;
            foreach (var g in snap.Gpu)
            {
                gpuMs.Add(g.GpuMs);
                gpuSum += g.GpuMs;
                missedSamples += g.MissedSamples;
                if (g.PassCount <= 0) continue;
                passFrames++;
                up += g.UploadsMs; blur += g.BakedBlurMs; clear += g.ClearMs; scene += g.SceneMs; glyph += g.GlyphBandMs;
                tile += g.TileRasterMs; off += g.OffscreenMs; comp += g.CompositeMs;
            }
            Dist(m, "gpuMs", gpuMs);
            m.Add(new("gpuFrames", snap.Gpu.Length));
            m.Add(new("gpuMissedSamplesPerSec", PerSec(missedSamples)));
            m.Add(new("gpuBusyPct", wallMs > 0 ? gpuSum / wallMs * 100.0 : double.NaN));
            double P(double v) => passFrames > 0 ? v / passFrames : double.NaN;
            m.Add(new("gpuPass.uploadsMs", P(up)));
            m.Add(new("gpuPass.bakedBlurMs", P(blur)));
            m.Add(new("gpuPass.clearMs", P(clear)));
            m.Add(new("gpuPass.sceneMs", P(scene)));
            m.Add(new("gpuPass.glyphBandMs", P(glyph)));
            m.Add(new("gpuPass.tileRasterMs", P(tile)));
            m.Add(new("gpuPass.offscreenMs", P(off)));
            m.Add(new("gpuPass.compositeMs", P(comp)));

            // Allocations and the GC (per second).
            m.Add(new("uiAllocBytesPerFrame", ui.Length > 0 ? uiAllocSum / ui.Length : double.NaN));
            m.Add(new("paintedAllocBytesPerFrame", painted > 0 ? paintedAllocSum / painted : double.NaN));
            m.Add(new("renderAllocBytesPerTurn", rt.Length > 0 ? rAlloc / rt.Length : double.NaN));
            m.Add(new("processAllocBytesPerSec", mem.Length >= 2 && snap.SpanMs(mem[0].Qpc, mem[^1].Qpc) > 0
                ? (mem[^1].TotalAllocatedBytes - mem[0].TotalAllocatedBytes) / (snap.SpanMs(mem[0].Qpc, mem[^1].Qpc) / 1000.0) : double.NaN));
            bool two = ui.Length >= 2;
            m.Add(new("gc0PerSec", two ? PerSec(ui[^1].Gc0Total - ui[0].Gc0Total) : double.NaN));
            m.Add(new("gc1PerSec", two ? PerSec(ui[^1].Gc1Total - ui[0].Gc1Total) : double.NaN));
            m.Add(new("gc2PerSec", two ? PerSec(ui[^1].Gc2Total - ui[0].Gc2Total) : double.NaN));
            m.Add(new("gcPauseMsPerSec", two ? PerSec((ui[^1].GcPauseTicksTotal - ui[0].GcPauseTicksTotal) / 10_000.0) : double.NaN));
            m.Add(new("missedVsyncsPerSec", two ? PerSec(ui[^1].MissedVsyncsTotal - ui[0].MissedVsyncsTotal) : double.NaN));
            long missedTicks = 0;
            foreach (var t in rt) missedTicks += t.MissedTicks;
            m.Add(new("missedTicksPerSec", PerSec(missedTicks)));

            // Memory (avg + peak, MB).
            void Mem(string key, Func<LedgerMemorySample, long> f)
            {
                double sum = 0, peak = double.NaN;
                foreach (var s in mem) { double v = f(s) / 1048576.0; sum += v; peak = double.IsNaN(peak) ? v : Math.Max(peak, v); }
                m.Add(new(key + "MB.avg", mem.Length > 0 ? sum / mem.Length : double.NaN));
                m.Add(new(key + "MB.peak", peak));
            }
            Mem("workingSet", static s => s.WorkingSetBytes);
            Mem("private", static s => s.PrivateBytes);
            Mem("managed", static s => s.ManagedBytes);
            Mem("gcHeap", static s => s.GcHeapBytes);
            Mem("vramLocal", static s => s.VramLocalBytes);
            Mem("vramNonLocal", static s => s.VramNonLocalBytes);
            Mem("imageCache", static s => s.ImageCacheBytes);
            Mem("glyphAtlas", static s => s.GlyphAtlasBytes);

            // Audio health (device underruns vs app-side xruns), per second.
            var au = snap.Audio;
            double padMin = double.NaN;
            foreach (var a in au)
                if (a.PaddingMinFrames >= 0 && a.Rate > 0)
                {
                    double ms = a.PaddingMinFrames * 1000.0 / a.Rate;
                    padMin = double.IsNaN(padMin) ? ms : Math.Min(padMin, ms);
                }
            double auSec = au.Length >= 2 ? snap.SpanMs(au[0].Qpc, au[^1].Qpc) / 1000.0 : 0;
            m.Add(new("audioDeviceUnderrunsPerSec", auSec > 0 ? (au[^1].DeviceUnderrunsTotal - au[0].DeviceUnderrunsTotal) / auSec : double.NaN));
            m.Add(new("audioXrunsPerSec", auSec > 0 ? (au[^1].XrunsTotal - au[0].XrunsTotal) / auSec : double.NaN));
            m.Add(new("audioPaddingMinMs", padMin));
            if (extra is not null) m.AddRange(extra);
            return new FrameBenchScenarioSummary(name, null, m, note);
        }

        /// <summary>An off/on overhead A/B (ledger-overhead, gpu-pass-overhead) from its raw arms, at the run's rate.</summary>
        public static FrameBenchScenarioSummary Overhead(string name, in OverheadArm off, in OverheadArm on, double cyclesPerMs, int processors, string? note)
        {
            double Fps(in OverheadArm a) => a.WallMs > 0 ? a.Frames * 1000.0 / a.WallMs : double.NaN;
            double UiUs(in OverheadArm a) => a.Frames > 0 && cyclesPerMs > 0 ? a.UiCycles / cyclesPerMs * 1000.0 / a.Frames : double.NaN;
            double WallUs(in OverheadArm a) => a.Frames > 0 ? a.RunFrameWallMs * 1000.0 / a.Frames : double.NaN;
            double Pct(in OverheadArm a) => a.WallMs > 0 ? a.ProcessCpuMs / a.WallMs / Math.Max(1, processors) * 100.0 : double.NaN;
            var m = new List<KeyValuePair<string, double>>
            {
                new("framesPerSec.off", Fps(off)), new("framesPerSec.on", Fps(on)),
                new("uiCpuUsPerFrame.off", UiUs(off)), new("uiCpuUsPerFrame.on", UiUs(on)),
                new("runFrameWallUs.off", WallUs(off)), new("runFrameWallUs.on", WallUs(on)),
                new("processCpuPct.off", Pct(off)), new("processCpuPct.on", Pct(on)),
                new("gpuMs.off", off.GpuMsAvg), new("gpuMs.on", on.GpuMsAvg),
                new("overheadUiCpuUsPerFrame", UiUs(on) - UiUs(off)),
                new("overheadRunFrameWallUs", WallUs(on) - WallUs(off)),
                new("overheadProcessCpuPct", Pct(on) - Pct(off)),
                new("overheadGpuMs", on.GpuMsAvg - off.GpuMsAvg),
            };
            return new FrameBenchScenarioSummary(name, null, m, note);
        }

        /// <summary>Metric direction for the compare tool: +1 higher is better, 0 neutral (it describes the run — counts, rates of
        /// frames, the census, the rate itself, an A/B arm, and the tick-charged TIME-based CPU figures: processCpuPct / *CoresTimes are
        /// info only, see <see cref="IsInfoCpu"/>), -1 (the default) lower is better.</summary>
        public static bool IsInfoCpu(string key) => key is "processCpuPct" or "uiCoresTimes" or "renderCoresTimes" or "overheadProcessCpuPct"
            || key.StartsWith("processCpuPct.", StringComparison.Ordinal) || key.StartsWith("paintedCpuMs", StringComparison.Ordinal)
            // the per-frame ms figures are cycles converted at the run's GetProcessTimes-derived rate, so they inherit its noise
            || key.StartsWith("uiCpuMs", StringComparison.Ordinal) || key.StartsWith("renderCpuMs", StringComparison.Ordinal)
            || key.StartsWith("otherCpuMs", StringComparison.Ordinal);

        public static int Better(string key)
            => key is "presentsPerSec" or "audioPaddingMinMs" ? +1
             : IsInfoCpu(key) ? 0
             : key is "wallSec" or "frames" or "paintedFrames" or "presents" or "gpuFrames" or "framesPerSec" or "paintedFramesPerSec"
                 or "cyclesPerMs.window" or "settleSec" or "scrollViewports" or "scrollSteps" or "cycles" or "presentChecked" or "damageValidated"
               || key.EndsWith(".noPresent", StringComparison.Ordinal)
               || key.StartsWith("exitPerSec.", StringComparison.Ordinal) || key.StartsWith("wakePerSec.", StringComparison.Ordinal)
               || key.StartsWith("turnPerSec.", StringComparison.Ordinal)
               || key.EndsWith(".off", StringComparison.Ordinal) || key.EndsWith(".on", StringComparison.Ordinal) ? 0
             : -1;

        /// <summary>frame-bench-summary.json: schema, run identity (incl. the ONE cycles-per-ms every window was read at), then one
        /// object per scenario with its flat metrics (NaN → null).</summary>
        public static string Json(string version, string label, bool real, int processors, int refreshHz, string windowPx, int measureSec,
            int warmupSec, double cyclesPerMs, DateTime utc, IReadOnlyList<FrameBenchScenarioSummary> scenarios, bool gpuPasses = false)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("schema", "wavee-frame-bench/2.1");
                w.WriteString("version", version);
                w.WriteString("label", label);
                w.WriteString("data", real ? "real" : "fake");
                w.WriteString("utc", utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                w.WriteNumber("processors", processors);
                w.WriteNumber("refreshHz", refreshHz);
                w.WriteString("windowPx", windowPx);
                w.WriteNumber("measureSec", measureSec);
                w.WriteNumber("warmupSec", warmupSec);
                w.WriteBoolean("gpuPasses", gpuPasses);
                Num(w, "cyclesPerMs", cyclesPerMs);
                w.WriteStartArray("scenarios");
                foreach (var s in scenarios)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    if (s.Skipped is { } why) w.WriteString("skipped", why);
                    if (s.Note is { } note) w.WriteString("note", note);
                    w.WriteStartObject("metrics");
                    foreach (var kv in s.Metrics) Num(w, kv.Key, kv.Value);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static void Num(Utf8JsonWriter w, string key, double v)
        {
            if (double.IsFinite(v)) w.WriteNumber(key, Math.Round(v, 4));
            else w.WriteNull(key);
        }

        /// <summary>The stdout table: one row per scenario, the numbers a reader scans first.</summary>
        public static string Table(IReadOnlyList<FrameBenchScenarioSummary> scenarios)
        {
            var sb = new StringBuilder(256 + scenarios.Count * 200);
            const string Fmt = "{0,-17} {1,6} {2,6} {3,6} {4,6} {5,6} {6,6} {7,6} {8,6} {9,6} {10,6} {11,6} {12,6} {13,6} {14,7} {15,6} {16,6} {17,6}";
            string H = string.Format(CultureInfo.InvariantCulture, Fmt,
                "scenario", "pres/s", "pdP50", "pdP99", "rdP50", "rdP99", "uiMc/s", "rdMc/s", "othMc/s", "procGc/s", "gpP50", "gpP99", "gpBsy%", "miss/s", "alloc/f", "wsMB", "vramMB", "gc0/s");
            sb.AppendLine(H);
            sb.AppendLine(new string('-', H.Length));
            foreach (var s in scenarios)
            {
                if (s.Skipped is { } why) { sb.Append(s.Name.PadRight(17)).Append(" skipped: ").AppendLine(why); continue; }
                if (!double.IsNaN(s["overheadUiCpuUsPerFrame"]))
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-17} off/on: frames/s {1}/{2}  UI CPU per frame {3}/{4} us (+{5})  RunFrame wall {6}/{7} us  process CPU {8}/{9} %  GPU {10}/{11} ms",
                        s.Name, F(s["framesPerSec.off"], "0.0"), F(s["framesPerSec.on"], "0.0"), F(s["uiCpuUsPerFrame.off"], "0"), F(s["uiCpuUsPerFrame.on"], "0"),
                        F(s["overheadUiCpuUsPerFrame"], "0.0"), F(s["runFrameWallUs.off"], "0"), F(s["runFrameWallUs.on"], "0"),
                        F(s["processCpuPct.off"], "0.00"), F(s["processCpuPct.on"], "0.00"), F(s["gpuMs.off"]), F(s["gpuMs.on"])));
                    if (s.Note is { } on) sb.Append("  ").AppendLine(on);
                    continue;
                }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, Fmt,
                    s.Name, F(s["presentsPerSec"], "0.0"), F(s["paintedCpuMs.p50"]), F(s["paintedCpuMs.p99"]), F(s["renderCpuMs.p50"]), F(s["renderCpuMs.p99"]),
                    F(s["uiMcyclesPerSec"], "0"), F(s["renderMcyclesPerSec"], "0"), F(s["otherMcyclesPerSec"], "0"), F(s["processGcyclesPerSec"], "0.00"), F(s["gpuMs.p50"]), F(s["gpuMs.p99"]),
                    F(s["gpuBusyPct"], "0.0"), F(s["missedVsyncsPerSec"], "0.0"), F(s["uiAllocBytesPerFrame"], "0"), F(s["workingSetMB.avg"], "0"),
                    F(s["vramLocalMB.avg"], "0"), F(s["gc0PerSec"], "0.0")));
                if (s.Note is { } note) sb.Append("  ").AppendLine(note);
            }
            sb.AppendLine("(pd/rd = UI CPU ms per painted frame / render CPU ms per presented turn; Mc/s = mega-cycles per second (UI thread / render thread / the rest);");
            sb.AppendLine(" procGc/s = process giga-cycles per second (raw cycle counters; GetProcessTimes-based processCpuPct / *CoresTimes are info only, in the JSON);");
            sb.AppendLine(" gp = GPU ms per frame; miss/s = missed vsyncs per second)");
            return sb.ToString();
        }

        static string F(double v, string fmt = "0.00") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";
    }
}
