// ── Screens/Diagnostics.Host.cs ────────────────────────────────────────────────────────────────────────────────────
// WaveeLogSessions' disk walk + export, NavigationFrameWatch + MemorySampler (the always-on frame and memory lines,
// the Shell.RouteNoted consumer), the lyrics evidence bundle. The old crash-report writer + GUI-run crash-prompt
// latch (Diagnostics.CrashReport, Diagnostics.BeginGuiRun, NewCrashDump) retired here — see
// docs/plans/wavee/crash-diagnostics-implementation.md §E "Deletions"; the crash & diagnostics pipeline now lives in
// Platform/Crash*.cs (WP-A/B/C) and Crash.Host.BeginGuiRun (called from Diagnostics.UI.cs's Install).
//
// Role: SHELL
// Owner: S
// Wave: 6
// Budget: 500 lines
// Spec: ch 27 §9.3, §7 G11/G12; gaps G-153, G-183, G-197
//
// Every decision is `Diagnostics.cs` / `Platform.Settings.cs` (CORE, tested); this file reads the disk, the process and
// the engine's frame relay. The frame watch runs on EVERY rendered frame, so its non-logging path allocates nothing: the
// counters are structs, and a string is built only for a line that is actually written. The line SHAPES
// (`nav.frames`, `scroll.frames`, `frame.slow`, `frame.churn`, `session.frames`, `mem.sample`) are 0.2.9's verbatim (`frame.slack` is new) —
// `ops/tools/perf-tour*.ps1` and `scroll-capture.ps1` parse them. `scroll.burst` is the engine's own scroll-probe
// verdict for the same burst (`ScrollProbe.EndBurst` → `BurstSummary.FormatLine`), written only while the probe level
// (Diagnostics ▸ Scroll, persisted) is Summary or Trace.

using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Hosting;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;

namespace Wavee;

// ── 1. past log sessions: the disk half ──────────────────────────────────────────────────────────────────────────────

public static partial class WaveeLogSessions
{
    /// <summary>Every completed session on disk, newest first (this run excluded). <paramref name="basePath"/> is the
    /// CONFIGURED path (<c>Log.BasePath</c>, wavee.log): the dated live file would narrow the glob to one day.</summary>
    public static List<Info> ListPastSessions(string? basePath, int currentPid)
    {
        try
        {
            string? dir = basePath is null ? null : Path.GetDirectoryName(basePath);
            if (basePath is null || dir is null || !Directory.Exists(dir)) return [];
            string root = Path.GetFileNameWithoutExtension(basePath), ext = Path.GetExtension(basePath);
            string[] files = Chronological(Directory.GetFiles(dir, root + "-*" + ext), File.Exists(basePath) ? basePath : null);
            return Split(files, ReadSharedLines, Log.SessionId, currentPid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    public static WaveeLogEntry[] LoadSession(Info info, int maxEntries = 4096) => Load(info, ReadSharedLines, maxEntries);

    /// <summary>A past session's raw lines to a file (the logs panel's Export on a past session).</summary>
    public static void ExportSessionToFile(Info info, string path) => File.WriteAllLines(path, RawLines(info, ReadSharedLines));

    /// <summary>Line by line with the share mode the log's persistent append sink needs (ReadWrite | Delete) — the default
    /// FileShare.Read throws a sharing violation against the LIVE file. Lazy: the open happens on the first MoveNext,
    /// inside the caller's try.</summary>
    public static IEnumerable<string> ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line) yield return line;
    }
}

public static partial class Diagnostics
{
    // ══ 4. THE FRAME WATCH (G-197: the Shell.RouteNoted consumer) ═══════════════════════════════════════════════════

    /// <summary>The always-on frame cost log: <c>frame.slow</c> per over-budget frame (≤ 6 a second, the rest counted),
    /// <c>nav.frames</c> for the four seconds after a route change, <c>scroll.frames</c> (+ <c>scroll.burst</c> while the
    /// scroll probe records) per wheel/drag burst, the
    /// once-a-second <c>frame.churn</c> census, and <c>session.frames</c> every 5 s. UI thread only (the engine raises the
    /// frame event inside the frame).</summary>
    public static class NavigationFrameWatch
    {
        const double WindowMs = 4000, SlowMs = 33.4, StallMs = 100, ScrollQuietMs = 500;
        const int SlowLinesPerSecond = 6, ScrollMinFrames = 5;
        /// <summary>A scrolling frame whose engine-reported slack exceeds this is a slack frame (and gets its own line).</summary>
        const float SlackMs = 12f;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly Action<FrameStats> s_onFrame = OnFrame;
        static string s_route = "", s_routeKey = "", s_scrollRoute = "";
        static string? s_arg;
        static bool s_attached;
        static long s_navigationId, s_scrollNavigationId;
        static double s_slowBucketAt = double.NegativeInfinity, s_navAt = double.NaN, s_firstFrameMs = double.NaN;
        static double s_scrollLastAt = double.NaN, s_scrollStartAt = double.NaN, s_lastSessionLogAt;
        static double s_refreshIntervalMs;
        static int s_slowTokens, s_slowSuppressed;
        static Window s_nav, s_scroll;
        static FrameSessionTotals s_session;
        static SidebarPaneInvariantFault s_paneFault;
        static bool s_imagesAttached;
        static long s_lastImageWarnMs = long.MinValue;

        /// <summary>Bumps on every navigation; 0 until the content host activates its first route (the startup bench's
        /// "session restored" mark).</summary>
        public static long NavigationId => Interlocked.Read(ref s_navigationId);
        public static string Route => s_route;
        public static string? Arg => s_arg;
        /// <summary>The most recent frame's refresh interval (ms; 0 before the first frame) — the scroll CSV export's
        /// <c>display_refresh_hz</c>.</summary>
        public static double RefreshIntervalMs => s_refreshIntervalMs;
        /// <summary>Milliseconds since the last route change; NaN before any.</summary>
        public static double SinceNavMs => double.IsNaN(s_navAt) ? double.NaN : Clock.Elapsed.TotalMilliseconds - s_navAt;

        internal static void Attach()
        {
            if (s_attached) return;
            s_attached = true;
            FluentApp.FrameCompleted += s_onFrame;
            AttachImages();
        }

        /// <summary>Subscribes to the engine's image-decode failures, once. <see cref="FluentApp.EngineImages"/> is null
        /// until the host exists — <see cref="Attach"/> runs before the window, so this also retries from <see
        /// cref="OnFrame"/> (cheap: a bool check) until the host is up, then never again.</summary>
        static void AttachImages()
        {
            if (s_imagesAttached) return;
            if (FluentApp.EngineImages is not { } images) return;
            images.ImageStatusChanged += OnImageStatusChanged;
            s_imagesAttached = true;
        }

        /// <summary>Always-on: an image that lands <see cref="ImageState.Failed"/> for any reason but a deliberate cancel
        /// (a recycled/unmounted row) is worth a line — nothing logs a decode failure today. Rate-limited to one line a
        /// second so a bad network burst cannot flood the log; no allocation on the suppressed path.</summary>
        static void OnImageStatusChanged(int id, ImageState state, ImageFailureKind failure, int attempts)
        {
            if (state != ImageState.Failed || failure == ImageFailureKind.Canceled) return;
            long now = Clock.ElapsedMilliseconds;
            if (now - s_lastImageWarnMs < 1000) return;
            s_lastImageWarnMs = now;
            // The source makes a failure actionable (which image, which host/format) — an id alone can't be traced back.
            string src = FluentApp.EngineImages?.SourceOf(new ImageHandle(id)) ?? "-";
            Log.Warn("image", "image decode failed id=" + id.ToString(CultureInfo.InvariantCulture)
                + " failure=" + failure + " attempts=" + attempts.ToString(CultureInfo.InvariantCulture) + " src=" + src);
        }

        /// <summary>`Shell.RouteNoted`: a repeat of the current name AND argument is ignored; album A → album B restarts the
        /// clock. One <c>nav.route</c> line anchors "time since this navigation".</summary>
        public static void NoteRoute(Shell.Route route)
        {
            string name = Shell.NameOf(route);
            string? arg = Shell.ArgOf(route);
            string key = RouteWatchKey(name, arg);
            if (key == s_routeKey) return;
            Flush();
            FlushScroll();
            s_route = name; s_arg = arg; s_routeKey = key;
            s_navAt = Clock.Elapsed.TotalMilliseconds;
            s_firstFrameMs = double.NaN;
            s_nav = default;
            long id = Interlocked.Increment(ref s_navigationId);
            Log.Event(WaveeLogLevel.Info, "ui", "nav.route", "route shown route=" + name + " arg="
                + (string.IsNullOrEmpty(arg) ? "-" : Uri.EscapeDataString(arg)) + " navId=" + id.ToString(CultureInfo.InvariantCulture));
        }

        internal static void EndSession()
        {
            if (!s_attached) return;
            Flush(sampleMemory: false);
            FlushScroll(sampleMemory: false);
            if (s_slowSuppressed > 0)
                Log.Event(WaveeLogLevel.Info, "ui", "frame.slow.suppressed", "frame.slow lines not written this second route=" + s_route + " count=" + s_slowSuppressed);
            s_slowSuppressed = 0;
            LogSession(true);
            FluentApp.FrameCompleted -= s_onFrame;
            if (s_imagesAttached && FluentApp.EngineImages is { } images) images.ImageStatusChanged -= OnImageStatusChanged;
            s_imagesAttached = false;
            s_attached = false;
        }

        static void OnFrame(FrameStats stats)
        {
            if (!s_imagesAttached) AttachImages();
            double now = Clock.Elapsed.TotalMilliseconds;
            s_refreshIntervalMs = stats.RefreshIntervalMs;
            s_session.Add(stats.FrameMs, stats.RefreshIntervalMs);
            if (now - s_lastSessionLogAt >= 5000) LogSession(false);
            if (stats.FrameMs > FrameBudgetMs(stats.RefreshIntervalMs)) NoteSlowFrame(in stats, now);
            else if (HasSlack(in stats)) NoteSlackFrame(in stats, now);
            else if (stats.Census is { Length: > 0 } census)
                Log.Event(WaveeLogLevel.Info, "ui", "frame.churn", "steady churn route=" + s_route + (stats.ScrollActive ? " scroll=1 " : " scroll=0 ")
                    + "frameMs=" + F(stats.FrameMs) + " census=" + census);
            NoteScrollFrame(in stats, now);
            EvidenceAuto.OnFrame(in stats);   // item G: one bundle on the first coverage clamp of a burst (rate-limited)
            MemorySampler.OnFrame();
            if (double.IsNaN(s_navAt)) return;
            double since = now - s_navAt;
            if (since > WindowMs) { Flush(); return; }
            if (double.IsNaN(s_firstFrameMs)) { s_firstFrameMs = since; s_nav.Reset(in stats); }
            s_nav.Add(in stats);
        }

        static void NoteSlowFrame(in FrameStats stats, double now)
        {
            if (now - s_slowBucketAt >= 1000)
            {
                if (s_slowSuppressed > 0)
                    Log.Event(WaveeLogLevel.Info, "ui", "frame.slow.suppressed", "frame.slow lines not written this second route=" + s_route + " count=" + s_slowSuppressed);
                s_slowBucketAt = now; s_slowTokens = SlowLinesPerSecond; s_slowSuppressed = 0;
            }
            if (s_slowTokens == 0) { s_slowSuppressed++; return; }
            s_slowTokens--;
            Log.Event(stats.FrameMs >= StallMs ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "frame.slow",
                "slow frame route=" + s_route + " budgetMs=" + F(FrameBudgetMs(stats.RefreshIntervalMs)) + " sinceNavMs=" + F(SinceNavMs)
                + (stats.ScrollActive ? " scroll=1 " : " scroll=0 ") + Describe(in stats) + Slack(in stats) + (stats.Census is { } c ? " census=" + c : ""));
        }

        /// <summary>A scrolling frame the engine reports as mostly not its own (a GC pause, a wake that slept, pre-emption)
        /// while still inside the budget. Shares the frame.slow token bucket.</summary>
        static void NoteSlackFrame(in FrameStats stats, double now)
        {
            if (now - s_slowBucketAt >= 1000)
            {
                if (s_slowSuppressed > 0)
                    Log.Event(WaveeLogLevel.Info, "ui", "frame.slow.suppressed", "frame.slow lines not written this second route=" + s_route + " count=" + s_slowSuppressed);
                s_slowBucketAt = now; s_slowTokens = SlowLinesPerSecond; s_slowSuppressed = 0;
            }
            if (s_slowTokens == 0) { s_slowSuppressed++; return; }
            s_slowTokens--;
            Log.Event(WaveeLogLevel.Info, "ui", "frame.slack",
                "slack frame route=" + s_route + " budgetMs=" + F(FrameBudgetMs(stats.RefreshIntervalMs)) + " sinceNavMs=" + F(SinceNavMs)
                + " scroll=1 frameMs=" + F(stats.FrameMs) + Slack(in stats));
        }

        static bool HasSlack(in FrameStats s) => s.ScrollActive && s.SlackMs > SlackMs;

        static string Slack(in FrameStats s) => HasSlack(in s) ? " slack=" + F(s.SlackMs) + " cause=" + SlackCauseName(in s) + Gap(in s) : "";

        /// <summary>The engine's decomposition of the UI-thread gap in front of this frame (<see cref="FrameStats.UiGap"/>,
        /// 2026-09-25, owner-approved RCA: "Preempted" was only a residual). <c>gap=</c> the whole hole (previous Paint's
        /// end → this Paint's start), <c>waitReq=</c>/<c>waitBlocked=</c> what the loop asked WaitForWork for (negative =
        /// infinite) vs how long it actually sat in it (<c>waits=</c> how many), <c>msgs=</c> Win32 message dispatch,
        /// <c>input=</c> input dispatch, <c>posts=</c> cross-thread UI posts, <c>cold=</c> cold maintenance,
        /// <c>other=</c> the unmeasured rest, <c>run=</c> the thread's own CPU time across the gap (cycle counter; NaN =
        /// unavailable; a lower bound on ARM64), then the pure classifier's split <c>blocked= notSched= overrun=</c> and
        /// <c>verdict=</c> (NotScheduled / Busy / Blocked), <c>atMs=</c> the gap's start on the QPC clock (joins
        /// <c>[render.pace]</c>'s <c>worst(… atMs=)</c> — a freeze on BOTH threads at one instant is the process or the
        /// machine, not our code).</summary>
        static string Gap(in FrameStats s)
        {
            UiGapReport g = s.UiGap;
            return " gap=" + F(g.GapMs) + " waitReq=" + F(g.GapWaitRequestedMs) + " waitBlocked=" + F(g.GapWaitBlockedMs)
                 + " waits=" + g.GapWaits.ToString(CultureInfo.InvariantCulture)
                 + " msgs=" + F(g.GapMessagesMs) + " input=" + F(g.GapInputMs) + " posts=" + F(g.GapPostsMs)
                 + " cold=" + F(g.GapColdMs) + " other=" + F(g.GapOtherMs)
                 + " run=" + (float.IsNaN(g.GapRunMs) ? "NaN" : F(g.GapRunMs))
                 + " blocked=" + F(g.GapBlockedMs) + " notSched=" + F(g.GapNotScheduledMs) + " overrun=" + F(g.GapOverrunMs)
                 + " verdict=" + g.GapVerdict.ToString()
                 + " atMs=" + (g.GapStartQpc * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // Const names by declaration order (None/Gc/WakeSlept/Preempted): Enum.ToString allocates.
        static string SlackCauseName(in FrameStats s) => (int)s.SlackCause switch
        {
            0 => "None",
            1 => "Gc",
            2 => "WakeSlept",
            3 => "Preempted",
            _ => "Other",
        };

        /// <summary>Close the navigation window: the rollup, a memory sample, and the sidebar pane's settled-state check
        /// (G-183 — logged on the fault EDGE only).</summary>
        static void Flush(bool sampleMemory = true)
        {
            if (double.IsNaN(s_navAt)) return;
            double wall = Math.Min(WindowMs, Clock.Elapsed.TotalMilliseconds - s_navAt);
            if (s_nav.Frames > 0)
                Log.Event(s_nav.Stalls > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "nav.frames",
                    "navigation frames route=" + s_route + " navId=" + s_navigationId + " firstFrameMs=" + F(s_firstFrameMs) + " " + s_nav.Rollup(wall, presentCadence: false));
            LogImageLatency("nav", s_route);
            if (sampleMemory) MemorySampler.Sample("nav-end route=" + s_route);
            s_navAt = double.NaN;
            if (SidebarPaneFrame is { } pane)
            {
                SidebarPaneFrameSnapshot snap = pane();
                var fault = SidebarPaneInvariant.Inspect(in snap);
                if (PaneFaultEdge(s_paneFault, fault))
                    Log.Event(WaveeLogLevel.Error, "sidebar", "sidebar.pane.invariant_failed", "Sidebar pane did not settle in a valid terminal state", null, -1, null,
                        WaveeLogField.Of("route", s_route), WaveeLogField.Of("design", snap.Design.ToString()),
                        WaveeLogField.Of("presentedCompact", snap.PresentedCompact), WaveeLogField.Of("preferredWidth", snap.PreferredExpandedWidth),
                        WaveeLogField.Of("renderedWidth", snap.RenderedPaneWidth), WaveeLogField.Of("fault", SidebarPaneInvariant.FaultName(fault)));
                s_paneFault = fault;
            }
        }

        static void NoteScrollFrame(in FrameStats stats, double now)
        {
            if (!double.IsNaN(s_scrollLastAt) && now - s_scrollLastAt > ScrollQuietMs) FlushScroll();
            if (!stats.ScrollActive) return;
            if (double.IsNaN(s_scrollLastAt))
            {
                s_scrollStartAt = now; s_scrollNavigationId = s_navigationId; s_scrollRoute = s_route;
                s_scroll.Reset(in stats);
                Shell.SidebarReplanCensus.Reset();   // the burst's own re-plan count (Sidebar.Census.cs)
            }
            s_scrollLastAt = now;
            s_scroll.Add(in stats);
        }

        /// <summary>A page open moves its viewport for a frame or two (a restore); only a burst of ≥ 5 frames is a scroll.
        /// The probe's burst is closed on EVERY flush (a short nudge included) so the next burst's summary never inherits
        /// its records; only a real burst writes the line.</summary>
        static void FlushScroll(bool sampleMemory = true)
        {
            if (double.IsNaN(s_scrollLastAt)) return;
            if (s_scroll.Frames >= ScrollMinFrames)
            {
                double wall = Math.Max(1, s_scrollLastAt - s_scrollStartAt);
                Log.Event(s_scroll.Stalls > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "scroll.frames",
                    "scroll frames route=" + s_scrollRoute + " navId=" + s_scrollNavigationId + " " + s_scroll.Rollup(wall, presentCadence: true)
                    + Shell.SidebarReplanCensus.Drain());
                if (ScrollProbe.Level >= ProbeLevel.Summary)
                {
                    // The engine's own verdict for this burst: notches, presents, coverage clamps, late extent jumps,
                    // pose jitter, per-phase cost. A Warning when the verdict is anything but Smooth.
                    var burst = ScrollProbe.EndBurst(System.Diagnostics.Stopwatch.GetTimestamp());
                    Log.Event(burst.Verdict == ScrollVerdict.Smooth ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "ui", "scroll.burst",
                        "scroll burst route=" + s_scrollRoute + " navId=" + s_scrollNavigationId + " " + burst.FormatLine());
                }
                LogImageLatency("scroll", s_scrollRoute);
                if (sampleMemory) MemorySampler.Sample("scroll-end route=" + s_route);
            }
            else if (ScrollProbe.Level >= ProbeLevel.Summary) ScrollProbe.EndBurst(System.Diagnostics.Stopwatch.GetTimestamp());
            s_scrollLastAt = s_scrollStartAt = double.NaN;
        }

        /// <summary>Always-on: where the pictures' time went since the last window closed (the engine's
        /// <see cref="FluentGpu.Scene.ImageLatencyCensus"/>). <c>srcWait</c> is a bound image sitting on an EMPTY source
        /// (its row's cover url had not landed), <c>fetch</c> a cache miss's request-to-texture time, <c>reveal</c> which
        /// fade each landing got, <c>canceled</c> the pending decodes a de-realized row gave up. One line per nav/scroll
        /// window, only when something happened.</summary>
        static void LogImageLatency(string window, string route)
        {
            if (FluentApp.EngineImages is not { } images) return;
            var census = images.Latency;
            if (census.IsEmpty) return;
            Log.Event(WaveeLogLevel.Info, "image", "image.latency", "image latency window=" + window + " route=" + route + " " + census.FormatLine());
            census.Reset();
        }

        static void LogSession(bool final)
        {
            s_lastSessionLogAt = Clock.Elapsed.TotalMilliseconds;
            Log.Event(WaveeLogLevel.Info, "ui", "session.frames", "completed UI frames final=" + (final ? 1 : 0) + " frames=" + s_session.Frames
                + " over83=" + s_session.Over83 + " overRefresh=" + s_session.OverRefresh + " invalid=" + s_session.Invalid
                + " worstMs=" + s_session.WorstMs.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>One measured window's accumulators; a struct so both windows share the code without an allocation.</summary>
        struct Window
        {
            public int Frames, OverBudget, Slow, Stalls, Comps, WithCensus, Gc0, Gc1, Gc2;
            public double SumMs, WorstMs, SumFlush, SumLayout, SumRecord, SumSubmit;
            // Itch plan item H: the closest RENDER-SIDE proxy for present-latency wait FrameStats currently exposes.
            // `FenceWaitMs`'s own doc (AppHost.cs ~183-184) is explicit that it is "submitting-thread wall-time
            // BLOCKED on back-buffer retirement + present-latency waitable INSIDE SubmitDrawList" — i.e. it already
            // conflates GPU fence wait with the swapchain's frame-latency-waitable wait, which is exactly the
            // "latW"/LastLatencyWaitMs figure the plan asked for. It is NOT the same thing as
            // `FluentGpu.Rhi.PresentStats.LatencyWaitMs` (Seams/Rhi/PresentStats.cs), which the engine samples
            // separately from DXGI and does NOT surface on `FrameStats` at all — see `Rollup`'s doc below for what
            // is genuinely missing and would need an engine change.
            public double SumFenceWaitMs, MaxFenceWaitMs, SumLatencyWaitMs, MaxLatencyWaitMs;
            public long DwmDropped, DwmMissed, DwmLate;
            // Why spans were re-recorded instead of reused, summed over the burst (FrameStats.SpanMisses).
            public long MissGlobal, MissScoped, MissDirty, MissKey, MissClip, MissCapacity;
            // Frames that recorded nothing (every retained slice kept; only composite poses moved — FrameStats.CompositeOnlyTurn).
            public long CompositeOnly;
            public long HotAlloc;
            public FrameStats Worst, First, Last;

            public void Reset(in FrameStats first) { this = default; First = first; }

            public void Add(in FrameStats s)
            {
                Last = s; Frames++; SumMs += s.FrameMs;
                SumFlush += s.FlushMs; SumLayout += s.LayoutMs; SumRecord += s.RecordMs; SumSubmit += s.SubmitMs;
                SumFenceWaitMs += s.FenceWaitMs; if (s.FenceWaitMs > MaxFenceWaitMs) MaxFenceWaitMs = s.FenceWaitMs;
                SumLatencyWaitMs += s.LatencyWaitMs; if (s.LatencyWaitMs > MaxLatencyWaitMs) MaxLatencyWaitMs = s.LatencyWaitMs;
                DwmDropped += s.DwmDropped; DwmMissed += s.DwmMissed; DwmLate += s.DwmLate;
                var m = s.SpanMisses;
                MissGlobal += m.GlobalDisabled; MissScoped += m.ScopedBlocked; MissDirty += m.ExactDirty; MissKey += m.ExactKey;
                MissClip += m.ExactClip; MissCapacity += m.ExactCapacity;
                if (s.CompositeOnlyTurn) CompositeOnly++;
                HotAlloc += s.HotPhaseAllocBytes; Comps += s.ComponentsRendered;
                Gc0 += s.Gc0Delta; Gc1 += s.Gc1Delta; Gc2 += s.Gc2Delta;
                if (s.Census is not null) WithCensus++;
                if (s.FrameMs > FrameBudgetMs(s.RefreshIntervalMs)) OverBudget++;
                if (s.FrameMs >= SlowMs) Slow++;
                if (s.FrameMs >= StallMs) Stalls++;
                if (s.FrameMs > WorstMs) { WorstMs = s.FrameMs; Worst = s; }
            }

            /// <summary><paramref name="presentCadence"/> adds <c>presented=</c>/<c>missedVblanks=</c>/<c>fenceWaitAvg=</c>/
            /// <c>fenceWaitMax=</c> — the scroll-burst-only figures (a navigation window is mostly idle vblanks, where
            /// present cadence reads as noise, not a hitch). The engine counts a missed vblank for every refresh interval
            /// BETWEEN two consecutive presents (AppHost.NotePresented), so the figure is only meaningful while the loop
            /// is continuously live — a scroll burst. A navigation window is four seconds of mostly idle vblanks (nothing
            /// to present is not a miss), where it read as "missed=301 of 162 presented" on an album page the user was
            /// simply reading (2026-09-16); the nav rollup keeps overBudget/slow33/stall100, which count the frames that
            /// actually ran long.
            /// <para>Itch plan item H ("make the scroll instruments truthful"): <c>fenceWaitAvg</c>/<c>fenceWaitMax</c> are
            /// the render-side wait figures this burst — see <see cref="SumFenceWaitMs"/>'s doc for why `FenceWaitMs` is
            /// the honest stand-in for a per-burst LastLatencyWaitMs. What is genuinely NOT available and would need an
            /// engine change (no engine file was touched to add this — reported here instead, per the itch plan):
            /// <list type="bullet">
            /// <item>A true present-latency-wait figure distinct from GPU fence wait: `FrameStats` would need a new field
            /// (e.g. <c>LastLatencyWaitMs</c>) mirroring <c>ISwapchain.LastPresentStats.LatencyWaitMs</c>
            /// (FluentGpu.Engine/Seams/Rhi/PresentStats.cs) — today that value is read internally by AppHost
            /// (`_swapchain.LastPresentStats`, AppHost.cs ~5423/~5456) but never copied onto the per-frame stats record
            /// the host's `FrameCompleted` event hands to this file.</item>
            /// <item>DXGI/DWM presented-vs-displayed-vs-repeated deltas: `PresentStats` already carries
            /// <c>DwmFramesDroppedDelta</c> ("we were late"), <c>DwmFramesMissedDelta</c> ("we starved the compositor")
            /// and <c>DwmFramesLateDelta</c> (the compositor's own lateness), plus the raw DXGI <c>PresentCount</c> /
            /// <c>PresentRefreshCount</c> pair a burst could difference for a vblank-attested repeat count — none of
            /// these are on `FrameStats` either, for the same reason: nothing copies `LastPresentStats` onto it per
            /// frame. `MissedVsyncs` (already logged as `missedVblanks=`) is the one present-cadence figure that DOES
            /// reach here today, and it is engine-derived from the present thread's own bookkeeping, not from DXGI's
            /// PresentStats sample.</item>
            /// </list></para></summary>
            public readonly string Rollup(double wall, bool presentCadence)
            {
                double fps = wall > 0 ? Frames * 1000.0 / wall : 0, n = Math.Max(1, Frames);
                string cadence = presentCadence
                    ? " presented=" + (Last.PresentedFrames - First.PresentedFrames) + " missedVblanks=" + (Last.MissedVsyncs - First.MissedVsyncs)
                        + " fenceWaitAvg=" + F(SumFenceWaitMs / n) + " fenceWaitMax=" + F(MaxFenceWaitMs)
                        + " latWaitAvg=" + F(SumLatencyWaitMs / n) + " latWaitMax=" + F(MaxLatencyWaitMs)
                        + " dwmDropped=" + DwmDropped + " dwmMissed=" + DwmMissed + " dwmLate=" + DwmLate
                        + " renderFresh=" + (Last.RenderFreshPresents - First.RenderFreshPresents)
                        + " renderMotion=" + (Last.RenderMotionPresents - First.RenderMotionPresents)
                        + " raceHits=" + (Last.RenderRaceHits - First.RenderRaceHits)
                        + " freshLongWaits=" + (Last.RenderFreshLongWaits - First.RenderFreshLongWaits)
                        + " motionLongWaits=" + (Last.RenderMotionLongWaits - First.RenderMotionLongWaits)
                        + " skippedTicks=" + (Last.RenderSkippedTicks - First.RenderSkippedTicks)
                        // Paced turns that presented nothing because the previous present missed its vblank and still
                        // owned this one (the engine's SlotCatchUp) — each is one vblank, never a run of late presents.
                        + " catchUps=" + (Last.RenderCatchUpSkips - First.RenderCatchUpSkips)
                        // OS-attested (DXGI frame statistics via the engine's PresentStatisticsLedger): what reached the
                        // glass, what DWM dropped, and vblanks that repeated the previous image.
                        + " displayed=" + (Last.PresentsDisplayed - First.PresentsDisplayed)
                        + " dropped=" + (Last.PresentsDropped - First.PresentsDropped)
                        + " repeated=" + (Last.VblanksRepeated - First.VblanksRepeated)
                        + " spanMiss=[global=" + MissGlobal + " scoped=" + MissScoped + " dirty=" + MissDirty + " key=" + MissKey
                        + " clip=" + MissClip + " cap=" + MissCapacity + "] compositeOnly=" + CompositeOnly
                    : "";
                return "frames=" + Frames + " wallMs=" + F(wall) + " fps=" + F(fps) + " budgetMs=" + F(FrameBudgetMs(Last.RefreshIntervalMs))
                    + " overBudget=" + OverBudget + " slow33=" + Slow + " stall100=" + Stalls + " avgFrameMs=" + F(SumMs / n)
                    + " avgFlush=" + F(SumFlush / n) + " avgLayout=" + F(SumLayout / n) + " avgRecord=" + F(SumRecord / n)
                    + " avgSubmit=" + F(SumSubmit / n) + " comps=" + Comps + " hotAllocKB=" + (HotAlloc / 1024) + " gc=" + Gc0 + "/" + Gc1 + "/" + Gc2
                    + cadence
                    + " censusFrames=" + WithCensus + " worst=" + Describe(in Worst) + (Worst.Census is { } c ? " worstCensus=" + c : "");
            }
        }

        // Fence wait and present are inside SubmitMs; "unaccounted" subtracts the disjoint phases once.
        static string Describe(in FrameStats s) =>
            "frameMs=" + F(s.FrameMs) + " flush=" + F(s.FlushMs) + " reactive=" + F(s.ReactiveFlushMs) + " realize=" + F(s.VirtualRealizeMs)
            + " layout=" + F(s.LayoutMs) + " layoutSolve=" + F(s.LayoutSolveMs) + " layoutEffects=" + F(s.LayoutEffectsMs)
            + " anim=" + F(s.AnimMs) + " record=" + F(s.RecordMs) + " imagePump=" + F(s.ImagePumpMs) + " realizeCatchup=" + F(s.RealizeCatchupMs)
            + " submit=" + F(s.SubmitMs) + " (hash=" + F(s.SubmitHashMs) + " capture=" + F(s.SubmitCaptureMs)
            + (s.CaptureIncremental ? "/inc" : "/full") + ":" + s.CapturedNodes + "[scene=" + F(s.CaptureSceneMs)
            + " cfg=" + F(s.CaptureConfigMs) + " img=" + F(s.CaptureImagesMs) + " anim=" + F(s.CaptureAnimMs) + "]"
            + " tail=" + F(s.SubmitTailMs) + ")"
            + " fenceWait=" + F(s.FenceWaitMs) + " present=" + F(s.PresentMs) + " gpu=" + F(s.GpuRenderMs)
            + " unaccounted=" + F(s.FrameMs - s.FlushMs - s.LayoutMs - s.AnimMs - s.RecordMs - s.SubmitMs)
            + " comps=" + s.ComponentsRendered + " nodes=" + s.NodesVisited + " draw=" + s.DrawNodeCount + " cmds=" + s.DrawCommandCount
            + " hotAlloc=" + s.HotPhaseAllocBytes + " measures=" + s.MeasureCount + " shapes=" + s.TextShapes + " textMiss=" + s.TextShapeMisses
            + " bindFires=" + s.BindingFires + " bindWrites=" + s.BindingWrites + " gc=" + s.Gc0Delta + "/" + s.Gc1Delta + "/" + s.Gc2Delta
            + " spansReused=" + s.SpansReused + " spansReRecorded=" + s.SpansReRecorded + " blurGroups=" + s.BlurGroupCount
            + " repaintPct=" + (double.IsNaN(s.RepaintCoverage) ? "-" : (s.RepaintCoverage * 100.0).ToString("0.00", CultureInfo.InvariantCulture))
            + " gaps=" + s.PublicationGaps;

        static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>Always-on memory attribution: one <c>mem.sample</c> every 5 s while frames render, plus one at the end of
    /// every navigation window and scroll burst — the process, the managed heap, the engine census and GPU residency. UI
    /// thread (the census is a UI-thread read).</summary>
    public static class MemorySampler
    {
        const double IntervalMs = 5000;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        static double s_lastAt = double.NegativeInfinity;
        static long s_lastAllocBytes, s_lastAllocTicks, s_peakWorkingSet;

        internal static void OnFrame()
        {
            if (Clock.Elapsed.TotalMilliseconds - s_lastAt >= IntervalMs) Sample("periodic");
        }

        public static void Sample(string reason)
        {
            s_lastAt = Clock.Elapsed.TotalMilliseconds;
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp(), allocNow = GC.GetTotalAllocatedBytes(precise: false);
            double rate = s_lastAllocTicks != 0 && nowTicks > s_lastAllocTicks
                ? (allocNow - s_lastAllocBytes) / ((nowTicks - s_lastAllocTicks) / (double)System.Diagnostics.Stopwatch.Frequency) / 1048576.0 : 0;
            s_lastAllocBytes = allocNow; s_lastAllocTicks = nowTicks;
            long ws = Environment.WorkingSet, privateBytes = 0, processPeak = 0;
            if (ws > s_peakWorkingSet) s_peakWorkingSet = ws;
            try { using var p = System.Diagnostics.Process.GetCurrentProcess(); privateBytes = p.PrivateMemorySize64; processPeak = p.PeakWorkingSet64; } catch { }
            var gc = GC.GetGCMemoryInfo();
            var gens = gc.GenerationInfo;   // a span: read into plain locals (a ref local cannot be captured)
            long gen0 = gens.Length > 0 ? gens[0].SizeAfterBytes : 0, gen1 = gens.Length > 1 ? gens[1].SizeAfterBytes : 0,
                 gen2 = gens.Length > 2 ? gens[2].SizeAfterBytes : 0, loh = gens.Length > 3 ? gens[3].SizeAfterBytes : 0,
                 poh = gens.Length > 4 ? gens[4].SizeAfterBytes : 0;
            var sb = new StringBuilder(512).Append("memory ").Append(reason)
                .Append(" ws=").Append(Mb(ws)).Append(" wsPeak=").Append(Mb(s_peakWorkingSet)).Append(" private=").Append(Mb(privateBytes))
                .Append(" processPeak=").Append(Mb(processPeak)).Append(" wsBytes=").Append(ws).Append(" processPeakBytes=").Append(processPeak)
                .Append(" heap=").Append(Mb(gc.HeapSizeBytes)).Append(" committed=").Append(Mb(gc.TotalCommittedBytes))
                .Append(" fragmented=").Append(Mb(gc.FragmentedBytes)).Append(" gen0=").Append(Mb(gen0)).Append(" gen1=").Append(Mb(gen1))
                .Append(" gen2=").Append(Mb(gen2)).Append(" loh=").Append(Mb(loh)).Append(" poh=").Append(Mb(poh))
                .Append(" gcs=").Append(GC.CollectionCount(0)).Append('/').Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2))
                .Append(" allocMBs=").Append(rate.ToString("0.0", CultureInfo.InvariantCulture)).Append(" totalAlloc=").Append(Mb(allocNow));
            if (FluentApp.EngineCensus() is { } e)
                sb.Append(" | engine scene=").Append(e.SceneLive).Append('/').Append(e.SceneCapacity).Append(" orphans=").Append(e.SceneOrphans)
                  .Append(" strings=").Append(e.StringMap).Append(" images=").Append(e.ImageCount).Append(" imagesReady=").Append(e.ImageReady)
                  .Append(" imageBytes=").Append(Mb(e.ImageUsedBytes)).Append(" decodeInflight=").Append(e.DecodeInflight)
                  .Append(" imagesPending=").Append(e.ImagePending).Append(" decodeCanceled=").Append(e.DecodeCanceledPending)
                  .Append(" components=").Append(e.Components).Append(" bindings=").Append(e.NodeBindings).Append(" virtuals=").Append(e.VirtualBoundaries)
                  .Append(" animTracks=").Append(e.AnimTracks).Append(" pixelPool=").Append(Mb(e.PixelPoolRetainedBytes)).Append('/').Append(Mb(e.PixelPoolPeakBytes))
                  .Append(" | snapshots slots=").Append(e.SnapshotSlots).Append(" indexedBytes=").Append(e.SnapshotIndexedBytes)
                  .Append(" textStyleBytes=").Append(e.SnapshotTextStyleBytes).Append(" capacity=").Append(e.SnapshotCapacity)
                  .Append(" required=").Append(e.SnapshotRequired).Append(" reclaims=").Append(e.SnapshotReclaims)
                  .Append(" reclaimedIndexedBytes=").Append(e.SnapshotReclaimedIndexedBytes);
            if (FluentApp.GpuResidency() is { } g)
            {
                sb.Append(" | gpu bytes=").Append(Mb(g.Bytes)).Append(" resources=").Append(g.Count);
                if (FluentApp.GpuCensusLine() is { Length: > 0 } detail) sb.Append(detail);
            }
            Log.Event(ws > 400L * 1024 * 1024 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "mem", "mem.sample", sb.ToString());
        }

        /// <summary>The process-lifetime peak, after the UI loop (no engine read after host disposal).</summary>
        internal static void SampleProcessEnd()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                long ws = p.WorkingSet64;
                s_peakWorkingSet = Math.Max(s_peakWorkingSet, ws);
                Log.Event(WaveeLogLevel.Info, "mem", "mem.sample", "memory session-end reason=session-end ws=" + Mb(ws) + " wsPeak=" + Mb(s_peakWorkingSet)
                    + " private=" + Mb(p.PrivateMemorySize64) + " processPeak=" + Mb(p.PeakWorkingSet64) + " wsBytes=" + ws + " processPeakBytes=" + p.PeakWorkingSet64);
            }
            catch (InvalidOperationException) { }
        }

        static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
    }

    // ══ 5. SMALL SHELL HELPERS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Open a folder in Explorer (the logs panel, the runtime page). Best-effort; a failure is logged.</summary>
    public static void OpenFolder(string? path)
    {
        string dir = string.IsNullOrEmpty(path) ? Platform.LogFolder : path;
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("app", "could not open folder " + dir, ex);
        }
    }

    // ══ 6. THE LYRICS EVIDENCE BUNDLE (the inspector's "Save bundle…", A14; ch 22 W19b) ═════════════════════════════

    /// <summary>UTF-8 with NO byte-order mark: three BOM bytes break a byte-for-byte diff, a checksum and a strict reader.</summary>
    static readonly UTF8Encoding s_lyricsBundleUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Write one track's lyrics evidence — <c>report.txt</c>, every captured payload byte-for-byte, every parse and
    /// the final document as TSVs — into <c>logs\lyrics-evidence\&lt;utc&gt;-&lt;track&gt;</c> and return the folder. Null
    /// with an empty <paramref name="error"/> when there is nothing to write; null with the reason when the disk refused.
    /// Never throws. BLOCKING I/O: the inspector calls it through Task.Run and posts the answer back.</summary>
    public static string? SaveLyricsBundle(string trackId, Lyrics.SearchReport? report, Lyrics.Inspection? insp, out string error)
    {
        error = "";
        if (report is null && insp is null) return null;
        try
        {
            string folder = Path.Combine(Platform.LogFolder, "lyrics-evidence", LyricsReport.BundleFolderName(trackId, DateTime.UtcNow));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.txt"), LyricsReport.BuildReport(trackId, report, insp), s_lyricsBundleUtf8);
            if (insp is not null)
            {
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in insp.Raw)
                {
                    seen.TryGetValue(p.SourceId, out int n);
                    seen[p.SourceId] = ++n;
                    File.WriteAllText(Path.Combine(folder, LyricsReport.RawFileName(p.SourceId, n, p.Format)), p.Text, s_lyricsBundleUtf8);
                }
                foreach (var c in insp.Candidates)
                    File.WriteAllText(Path.Combine(folder, LyricsReport.ParsedFileName(c.SourceId)), LyricsReport.BuildParsed(c.SourceId, c.Document), s_lyricsBundleUtf8);
                if (insp.Final is { } final)
                    File.WriteAllText(Path.Combine(folder, LyricsReport.ParsedFileName("final")), LyricsReport.BuildParsed("final", final), s_lyricsBundleUtf8);
            }
            Log.Info(Lyrics.Diag.Category, "lyrics evidence bundle written payloads=" + (insp?.Raw.Count ?? 0) + " folder=" + folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            Log.Warn(Lyrics.Diag.Category, "lyrics evidence bundle could not be written", ex);
            return null;
        }
    }
}
