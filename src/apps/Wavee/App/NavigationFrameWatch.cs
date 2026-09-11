using System;
using System.Diagnostics;
using System.Globalization;
using FluentGpu;
using FluentGpu.Hosting;

namespace Wavee;

/// <summary>Always-on frame cost log. Every rendered frame arrives from the engine with its phase times and the
/// panel's refresh interval (the frame budget: 8.3 ms at 120 Hz, 16.7 at 60); the watch writes:
/// <list type="bullet">
/// <item><c>frame.slow</c> — one line per frame over budget (at most <see cref="SlowLinesPerSecond"/> a second; the
/// rest are counted, never lost): the phase split, GC deltas and, when the engine's render census fired for that
/// frame, WHICH components rendered and who allocated.</item>
/// <item><c>nav.frames</c> — one line per route change covering the four seconds after it: frames, fps, the count
/// over budget / over 33 ms / over 100 ms, missed vblanks, time to first frame, the worst frame with its census.</item>
/// <item><c>scroll.frames</c> — the same rollup for a wheel/drag burst (frames the engine stamps <c>ScrollActive</c>;
/// closes 500 ms after the last one). This is the number that answers "is scrolling smooth".</item>
/// </list>
/// The nav and scroll windows also ask <see cref="MemorySampler"/> for a sample when they close, so every route has a
/// memory reading next to its frame reading. UI thread only: the engine raises the frame event inside the frame.</summary>
public static class NavigationFrameWatch
{
    const double WindowMs = 4000, SlowMs = 33.4, StallMs = 100, ScrollQuietMs = 500;
    const int SlowLinesPerSecond = 6;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static string _route = "";
    static bool _attached;
    static long _navigationId;

    // frame.slow rate limit: a token bucket refilled once a second.
    static double _slowBucketAt = double.NegativeInfinity;
    static int _slowTokens, _slowSuppressed;

    // Navigation window.
    static double _navAt = double.NaN, _firstFrameMs = double.NaN;
    static Window _nav;
    // Scroll burst.
    static double _scrollLastAt = double.NaN, _scrollStartAt = double.NaN;
    static Window _scroll;
    static long _scrollNavigationId;
    static string _scrollRoute = "";
    static Wavee.Core.FrameSessionTotals _session;
    static double _lastSessionLogAt;
    static string? _arg;

    /// <summary>One measured window's accumulators (nav or scroll). A struct so the two windows share the code
    /// without a per-frame allocation.</summary>
    struct Window
    {
        public int Frames, OverBudget, Slow, Stalls, Comps, WithCensus;
        public double SumMs, WorstMs, SumFlush, SumLayout, SumRecord, SumSubmit;
        public long HotAlloc;
        public int Gc0, Gc1, Gc2;
        public FrameStats Worst, First, Last;

        public void Reset(in FrameStats first)
        {
            this = default;
            First = first;
        }

        public void Add(in FrameStats s)
        {
            Last = s;
            Frames++;
            SumMs += s.FrameMs;
            SumFlush += s.FlushMs; SumLayout += s.LayoutMs; SumRecord += s.RecordMs; SumSubmit += s.SubmitMs;
            HotAlloc += s.HotPhaseAllocBytes;
            Comps += s.ComponentsRendered;
            Gc0 += s.Gc0Delta; Gc1 += s.Gc1Delta; Gc2 += s.Gc2Delta;
            if (s.Census is not null) WithCensus++;
            if (s.FrameMs > Budget(s)) OverBudget++;
            if (s.FrameMs >= SlowMs) Slow++;
            if (s.FrameMs >= StallMs) Stalls++;
            if (s.FrameMs > WorstMs) { WorstMs = s.FrameMs; Worst = s; }
        }

        public string Rollup(double wall)
        {
            double fps = wall > 0 ? Frames * 1000.0 / wall : 0;
            return "frames=" + Frames + " wallMs=" + F(wall) + " fps=" + F(fps)
                + " budgetMs=" + F(Budget(Last)) + " overBudget=" + OverBudget + " slow33=" + Slow + " stall100=" + Stalls
                + " avgFrameMs=" + F(Frames > 0 ? SumMs / Frames : 0)
                + " avgFlush=" + F(Frames > 0 ? SumFlush / Frames : 0) + " avgLayout=" + F(Frames > 0 ? SumLayout / Frames : 0)
                + " avgRecord=" + F(Frames > 0 ? SumRecord / Frames : 0) + " avgSubmit=" + F(Frames > 0 ? SumSubmit / Frames : 0)
                + " comps=" + Comps + " hotAllocKB=" + (HotAlloc / 1024) + " gc=" + Gc0 + "/" + Gc1 + "/" + Gc2
                // The displayed cadence over the window: presents and the refresh slots they missed (the smoothness number).
                + " presented=" + (Last.PresentedFrames - First.PresentedFrames)
                + " missedVblanks=" + (Last.MissedVsyncs - First.MissedVsyncs)
                + " censusFrames=" + WithCensus
                + " worst=" + Describe(Worst) + (Worst.Census is { } c ? " worstCensus=" + c : "");
        }
    }

    /// <summary>Milliseconds since the last route change, for the hydration-gap lines; NaN before any navigation.</summary>
    public static double SinceNavMs => Wavee.Core.Diagnostics.PerformanceDiagnostics.Navigation.SinceNavigationMs;
    public static long NavigationId => System.Threading.Interlocked.Read(ref _navigationId);
    public static string Route => _route;
    public static string? Arg => _arg;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        FluentApp.FrameCompleted += OnFrame;
    }

    /// <summary>The content host reports each route it starts showing. A repeat of the current route AND argument is
    /// ignored; album A → album B is a new navigation (same name, different arg) and restarts the clock. Writes one
    /// <c>nav.route</c> line so the log has an exact anchor for "time since this navigation".</summary>
    public static void NoteRoute(string route, string? arg)
    {
        string key = arg is null ? route : route + ":" + arg;
        if (key == _routeKey) return;
        Flush();
        FlushScroll();
        _route = route;
        _arg = arg;
        _routeKey = key;
        _navAt = Clock.Elapsed.TotalMilliseconds;
        Wavee.Core.Diagnostics.PerformanceDiagnostics.Navigation.MarkNavigation();
        System.Threading.Interlocked.Increment(ref _navigationId);
        _firstFrameMs = double.NaN;
        _nav = default;
        WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "nav.route", "route shown route=" + route + " arg=" + (string.IsNullOrEmpty(arg) ? "-" : Uri.EscapeDataString(arg)) + " navId=" + _navigationId);
    }
    static string _routeKey = "";

    /// <summary>The frame budget the engine paced this frame against; falls back to 60 Hz when the panel rate is unknown.</summary>
    static double Budget(in FrameStats s) => s.RefreshIntervalMs > 0 ? s.RefreshIntervalMs : 16.67;

    static void OnFrame(FrameStats stats)
    {
        double now = Clock.Elapsed.TotalMilliseconds;
        _session.Add(stats.FrameMs, stats.RefreshIntervalMs);
        if (now - _lastSessionLogAt >= 5000) LogSession(false);
        if (stats.FrameMs > Budget(stats)) NoteSlowFrame(in stats, now);
        else NoteChurn(in stats);
        NoteScrollFrame(in stats, now);
        MemorySampler.OnFrame();
        if (double.IsNaN(_navAt)) return;
        double since = now - _navAt;
        if (since > WindowMs) { Flush(); return; }
        if (double.IsNaN(_firstFrameMs)) { _firstFrameMs = since; _nav.Reset(in stats); }
        _nav.Add(in stats);
    }

    // A frame that stayed INSIDE budget but still re-rendered components. On a settled page that number should be zero:
    // anything else is work the app repeats every frame for no visible change, and at 120 Hz it is where the session's
    // managed allocation goes — which is what a gen1 pause twice a second is made of. The engine samples at most one of
    // these per second, so this is a handful of lines a minute, not a stream.
    static void NoteChurn(in FrameStats stats)
    {
        if (stats.Census is not { } census || census.Length == 0) return;
        WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "frame.churn",
            "steady churn route=" + _route + (stats.ScrollActive ? " scroll=1 " : " scroll=0 ")
            + "frameMs=" + F(stats.FrameMs) + " census=" + census);
    }

    static void NoteSlowFrame(in FrameStats stats, double now)
    {
        if (now - _slowBucketAt >= 1000)
        {
            if (_slowSuppressed > 0)
                WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "frame.slow.suppressed",
                    "frame.slow lines not written this second route=" + _route + " count=" + _slowSuppressed);
            _slowBucketAt = now; _slowTokens = SlowLinesPerSecond; _slowSuppressed = 0;
        }
        if (_slowTokens == 0) { _slowSuppressed++; return; }
        _slowTokens--;
        WaveeLog.Instance.Event(stats.FrameMs >= StallMs ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "frame.slow",
            "slow frame route=" + _route + " budgetMs=" + F(Budget(stats)) + " sinceNavMs=" + F(SinceNavMs)
            + (stats.ScrollActive ? " scroll=1 " : " scroll=0 ") + Describe(stats)
            + (stats.Census is { } c ? " census=" + c : ""));
    }

    static void Flush(bool sampleMemory = true)
    {
        if (double.IsNaN(_navAt)) return;
        double wall = Math.Min(WindowMs, Clock.Elapsed.TotalMilliseconds - _navAt);
        if (_nav.Frames > 0)
            WaveeLog.Instance.Event(_nav.Stalls > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "nav.frames",
                "navigation frames route=" + _route + " navId=" + _navigationId + " firstFrameMs=" + F(_firstFrameMs) + " " + _nav.Rollup(wall));
        if (sampleMemory) MemorySampler.Sample("nav-end route=" + _route);
        _navAt = double.NaN;
    }

    static void NoteScrollFrame(in FrameStats stats, double now)
    {
        if (!double.IsNaN(_scrollLastAt) && now - _scrollLastAt > ScrollQuietMs) FlushScroll();
        if (!stats.ScrollActive) return;
        if (double.IsNaN(_scrollLastAt))
        {
            _scrollStartAt = now;
            _scrollNavigationId = _navigationId;
            _scrollRoute = _route;
            _scroll.Reset(in stats);
        }
        _scrollLastAt = now;
        _scroll.Add(in stats);
    }

    // A page open moves its viewport programmatically for a frame or two (scroll restore, a scroll-to); a wheel or
    // drag burst spans many frames. Only the latter is a scroll measurement.
    const int ScrollMinFrames = 5;

    static void FlushScroll(bool sampleMemory = true)
    {
        if (double.IsNaN(_scrollLastAt)) return;
        if (_scroll.Frames < ScrollMinFrames) { _scrollLastAt = _scrollStartAt = double.NaN; return; }
        double wall = Math.Max(1, _scrollLastAt - _scrollStartAt);
        WaveeLog.Instance.Event(_scroll.Stalls > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "scroll.frames",
            "scroll frames route=" + _scrollRoute + " navId=" + _scrollNavigationId + " " + _scroll.Rollup(wall));
        if (sampleMemory) MemorySampler.Sample("scroll-end route=" + _route);
        _scrollLastAt = _scrollStartAt = double.NaN;
    }

    /// <summary>Called after the UI loop exits cleanly; no engine reads after host disposal.</summary>
    public static void EndSession()
    {
        if (!_attached) return;
        Flush(sampleMemory: false);
        FlushScroll(sampleMemory: false);
        if (_slowSuppressed > 0)
            WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "frame.slow.suppressed",
                "frame.slow lines not written this second route=" + _route + " count=" + _slowSuppressed);
        _slowSuppressed = 0;
        LogSession(true);
        FluentApp.FrameCompleted -= OnFrame;
        _attached = false;
    }

    static void LogSession(bool final)
    {
        _lastSessionLogAt = Clock.Elapsed.TotalMilliseconds;
        WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "session.frames",
            "completed UI frames final=" + (final ? 1 : 0) + " frames=" + _session.Frames
            + " over83=" + _session.Over83 + " overRefresh=" + _session.OverRefresh
            + " invalid=" + _session.Invalid + " worstMs=" + _session.WorstMs.ToString("R", CultureInfo.InvariantCulture));
    }

    // Fence wait and present are included in SubmitMs. Subtract the disjoint phases once.
    static string Describe(in FrameStats s) =>
        "frameMs=" + F(s.FrameMs) + " flush=" + F(s.FlushMs) + " reactive=" + F(s.ReactiveFlushMs) + " realize=" + F(s.VirtualRealizeMs)
        + " layout=" + F(s.LayoutMs) + " layoutSolve=" + F(s.LayoutSolveMs) + " layoutEffects=" + F(s.LayoutEffectsMs)
        + " anim=" + F(s.AnimMs) + " record=" + F(s.RecordMs) + " imagePump=" + F(s.ImagePumpMs) + " realizeCatchup=" + F(s.RealizeCatchupMs)
        + " submit=" + F(s.SubmitMs) + " fenceWait=" + F(s.FenceWaitMs) + " present=" + F(s.PresentMs) + " gpu=" + F(s.GpuRenderMs)
        + " unaccounted=" + F(s.FrameMs - s.FlushMs - s.LayoutMs - s.AnimMs - s.RecordMs - s.SubmitMs)
        + " comps=" + s.ComponentsRendered + " nodes=" + s.NodesVisited + " draw=" + s.DrawNodeCount
        + " cmds=" + s.DrawCommandCount + " hotAlloc=" + s.HotPhaseAllocBytes
        // textMiss is the layout measure-cache MISS count (each one is an auto-fit search or a fresh shape) — the honest
        // counter next to the raw shape count, so a run measured at two widths shows up as two misses (#92).
        + " measures=" + s.MeasureCount + " shapes=" + s.TextShapes + " textMiss=" + s.TextShapeMisses
        + " bindFires=" + s.BindingFires + " bindWrites=" + s.BindingWrites
        + " gc=" + s.Gc0Delta + "/" + s.Gc1Delta + "/" + s.Gc2Delta
        // Render-side causes: how much of the frame's recording was reused, how many blur groups the GPU paid for,
        // and how much of the target was repainted (repaintPct=100.0 is full).
        + " spansReused=" + s.SpansReused + " spansReRecorded=" + s.SpansReRecorded + " blurGroups=" + s.BlurGroupCount
        + " blurHeld=" + s.BlurSuppressedByScrollCount + " repaintPct=" + Pct(s.RepaintCoverage) + " gaps=" + s.PublicationGaps;

    static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Repaint coverage as a PERCENTAGE with two decimals, not the raw 0..1 fraction through <see cref="F"/>.
    /// The whole point of damage-scoped repaint is coverage in the single-digit-percent range, and "0.0" — which is
    /// what the one-decimal fraction printed for every successful partial frame — cannot tell a 3 % frame from a
    /// 0.3 % one or from a frame that damaged nothing at all. Renamed to `repaintPct=` with the unit change so a log
    /// from either side of this commit can never be misread as the other.</summary>
    static string Pct(double v) => double.IsNaN(v) ? "-" : (v * 100.0).ToString("0.00", CultureInfo.InvariantCulture);
}
