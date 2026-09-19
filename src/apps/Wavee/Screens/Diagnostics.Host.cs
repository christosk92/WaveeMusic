// ── Screens/Diagnostics.Host.cs ────────────────────────────────────────────────────────────────────────────────────
// WaveeLogSessions' disk walk + export, the crash report writer and CrashReport.List, the GUI run's crash-prompt latch
// (the run marker + the WER dump probe), NavigationFrameWatch + MemorySampler (the always-on frame and memory lines,
// the Shell.RouteNoted consumer), the lyrics evidence bundle
//
// Role: SHELL
// Owner: S
// Wave: 6
// Budget: 500 lines
// Spec: ch 27 §9.3, §7 G11/G12; gaps G-094 (S half), G-153, G-183, G-197
//
// Every decision is `Diagnostics.cs` / `Platform.Settings.cs` (CORE, tested); this file reads the disk, the process and
// the engine's frame relay. The frame watch runs on EVERY rendered frame, so its non-logging path allocates nothing: the
// counters are structs, and a string is built only for a line that is actually written. The line SHAPES
// (`nav.frames`, `scroll.frames`, `frame.slow`, `frame.churn`, `session.frames`, `mem.sample`) are 0.2.9's verbatim (`frame.slack` is new) —
// `ops/tools/perf-tour*.ps1` and `scroll-capture.ps1` parse them.

using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Hosting;
using FluentGpu.Scene;

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
    // ══ 2. THE CRASH REPORT (G-094, S half) ═════════════════════════════════════════════════════════════════════════

    /// <summary>The one-file crash artifact: the build stamp, arch, pid, framework, OS and module base, the exception, its
    /// frame RVAs, and the tail of the app log. A shipped build is NativeAOT with no stack-trace metadata, so its frames are
    /// module offsets only — the header carries what a later symbolication needs (the commit + quad pick the symbols zip,
    /// the module base turns offsets into addresses). Written by `Platform.Host.cs`'s unhandled-exception handler; the path
    /// is stashed in <c>crash.pendingReport</c> so the next launch can offer it.</summary>
    public static class CrashReport
    {
        /// <summary>Enough to see a pattern across a few days, small enough that a crash loop cannot fill the folder.</summary>
        public const int DefaultKeep = 10;

        /// <summary>The SAME folder the app log lives in, so "open the report folder" lands beside the logs it quotes.</summary>
        public static string DefaultDirectory => Platform.LogFolder;

        static string? s_written;

        /// <summary>Write (once per process — both handlers fire for one crash and the second gets the first's path), then
        /// prune. Throws on a disk failure; the caller logs it.</summary>
        public static string Write(Exception ex, string? logPath)
        {
            if (s_written is { } already) return already;
            string dir = DefaultDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, CrashFiles.NameFor(DateTimeOffset.Now));
            var sb = new StringBuilder(32 * 1024).Append("Wavee crash report\n=================\n");
            Describe(ex, sb);
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                sb.Append("\nwavee.log tail\n--------------\n");
                foreach (string line in TailLines(logPath, 600)) sb.Append(line).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            s_written = path;
            Prune(dir);
            return path;
        }

        /// <summary>The report minus the banner and the tail, for a handler that logs rather than writes a file.</summary>
        public static string Describe(Exception ex)
        {
            var sb = new StringBuilder(4 * 1024);
            Describe(ex, sb);
            return sb.ToString();
        }

        static void Describe(Exception ex, StringBuilder sb)
        {
            // version FIRST after the banner: a report pasted into an issue is usually truncated after a few lines.
            WaveeVersionInfo? v = null;
            try { v = Platform.Version; } catch { }
            string Or(string? s) => string.IsNullOrEmpty(s) ? "unknown" : s;
            sb.Append("version=").Append(Or(v?.SemVer)).Append('\n')
              .Append("commit=").Append(Or(v?.Commit)).Append('\n')
              .Append("buildDate=").Append(Or(v?.BuildDate)).Append('\n')
              .Append("channel=").Append(Or(v?.Channel)).Append('\n')
              .Append("quad=").Append(Or(v?.Quad)).Append('\n')
              .Append("arch=").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()).Append('\n')
              .Append("timeLocal=").Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
              .Append("pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("framework=").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('\n')
              .Append("os=").Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription).Append('\n')
              .Append(MainModuleLine()).Append("\n\nException\n---------\n");
            // The whole ToString(), not StackTrace: an inner exception's frames (an async continuation's real fault) are
            // only in the former, and the section is pasted into a symbol lookup as-is.
            string text = ex.ToString();
            sb.Append(text).Append('\n');
            var rvas = CrashFiles.ParseRvas(text);
            if (rvas.Count == 0) return;
            sb.Append("\nFrames (RVA)\n------------\n")
              .Append("# offsets from the module base above, in stack-trace order (innermost first);\n")
              .Append("# resolve each with `ln Wavee+0x<rva>` against the release's Wavee-<quad>-<rid>-symbols.zip\n");
            foreach (long rva in rvas) sb.Append("0x").Append(rva.ToString("x", CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>Delete all but the newest <paramref name="keep"/> reports, ordered by NAME. Best-effort.</summary>
        public static void Prune(string dir, int keep = DefaultKeep)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string[] files = Directory.GetFiles(dir, CrashFiles.Prefix + "*" + CrashFiles.Suffix, SearchOption.TopDirectoryOnly);
                if (files.Length <= Math.Max(0, keep)) return;
                Array.Sort(files, CrashFiles.NewestFirst);
                for (int i = Math.Max(0, keep); i < files.Length; i++) try { File.Delete(files[i]); } catch { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>The newest <paramref name="max"/> reports, newest name first (the Crash reports card, owner R). A missing
        /// folder or an I/O failure is an empty list — this backs a settings card, not a gate.</summary>
        public static (string Path, DateTime Stamp)[] List(int max = DefaultKeep)
        {
            if (max <= 0) return [];
            string[] files;
            try
            {
                string dir = DefaultDirectory;
                if (!Directory.Exists(dir)) return [];
                files = Directory.GetFiles(dir, CrashFiles.Prefix + "*" + CrashFiles.Suffix, SearchOption.TopDirectoryOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
            Array.Sort(files, CrashFiles.NewestFirst);
            var result = new List<(string, DateTime)>(Math.Min(max, files.Length));
            foreach (string file in files)
            {
                if (result.Count == max) break;
                if (CrashFiles.TryStamp(Path.GetFileName(file), out var stamp)) result.Add((file, stamp));
            }
            return result.ToArray();
        }

        static string MainModuleLine()
        {
            string path = Environment.ProcessPath ?? "unknown";
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                if (proc.MainModule is { } main)
                    return "module=" + (string.IsNullOrEmpty(main.FileName) ? path : main.FileName)
                         + " base=0x" + main.BaseAddress.ToInt64().ToString("x", CultureInfo.InvariantCulture)
                         + " size=0x" + main.ModuleMemorySize.ToString("x", CultureInfo.InvariantCulture);
            }
            catch { }
            return "module=" + path + " base=unknown size=unknown";
        }

        static Queue<string> TailLines(string path, int maxLines)
        {
            var q = new Queue<string>(maxLines);
            foreach (string line in WaveeLogSessions.ReadSharedLines(path))
            {
                if (q.Count == maxLines) q.Dequeue();
                q.Enqueue(line);
            }
            return q;
        }
    }

    // ══ 3. THE GUI RUN: the run marker, the WER dump probe, the crash-prompt latch (G-094, S half) ═══════════════════

    /// <summary>Once per GUI launch, before the window (from <see cref="Install"/>; never the headless arm, whose settings
    /// writes are an overlay). MUST run before `Update.Host.Start` rewrites <c>app.lastRunVersion</c>: the version-changed
    /// input is read here. The report chrome (owner R) consumes <see cref="CrashPromptPolicy.ThisLaunch"/> once.</summary>
    internal static void BeginGuiRun()
    {
        var settings = Platform.Settings;
        string? dump = NewCrashDump(settings);
        RunOutcome previous = RunMarker.Begin(settings);
        bool versionChanged = Array.IndexOf(Environment.GetCommandLineArgs(), Platform.RelaunchedAfterUpdateFlag) >= 0
            || settings.Get(Platform.Keys.LastRunVersion) != Platform.Version.LastRunKey;
        var decision = CrashPromptPolicy.Decide(settings.Get(Platform.Keys.PendingCrashReport), dump, previous,
            settings.Get(Platform.Keys.CrashPromptOptOut), versionChanged, settings.Get(Platform.Keys.UncleanExitOffered));
        CrashPromptPolicy.ThisLaunch = decision;
        if (decision.Source == CrashSource.UncleanExit) settings.Set(Platform.Keys.UncleanExitOffered, true);
        settings.Set(Platform.Keys.PendingCrashReport, "");
        Log.Event(WaveeLogLevel.Info, "crash", "run.begin", "previous run " + previous, null, -1, null,
            WaveeLogField.Of("prompt", decision.Mode.ToString()), WaveeLogField.Of("source", decision.Source.ToString()),
            WaveeLogField.Of("versionChanged", versionChanged));
        // ProcessExit is the one hook that ALSO fires when Windows force-closes an otherwise-orderly shutdown.
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => { try { RunMarker.End(Platform.Settings); } catch { } };
    }

    /// <summary>A NEW Windows Error Reporting dump since the last launch looked, logged as a breadcrumb (a hard crash that
    /// ran no managed handler still leaves one). Null when there is none, it was already seen, or the probe failed.</summary>
    static string? NewCrashDump(IAppSettings settings)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            if (!Directory.Exists(dir)) return null;
            string? newest = null;
            DateTime newestWrite = default;
            foreach (string d in Directory.GetFiles(dir, "Wavee.exe*.dmp"))
            {
                DateTime w = File.GetLastWriteTimeUtc(d);
                if (newest is null || w > newestWrite) { newest = d; newestWrite = w; }
            }
            if (newest is null) return null;
            if (string.Equals(settings.Get(Platform.Keys.LastSeenCrashDumpPath), newest, StringComparison.OrdinalIgnoreCase)
                && settings.Get(Platform.Keys.LastSeenCrashDumpTicksUtc) == newestWrite.Ticks) return null;
            long size = 0;
            try { size = new FileInfo(newest).Length; } catch { }
            Log.Event(WaveeLogLevel.Critical, "crash", "wer.dump.detected", "Previous run left a Windows crash dump", null, -1, null,
                WaveeLogField.Of("path", newest), WaveeLogField.Of("writtenUtc", newestWrite.ToString("O", CultureInfo.InvariantCulture)),
                WaveeLogField.Of("sizeBytes", size));
            settings.Set(Platform.Keys.LastSeenCrashDumpPath, newest);
            settings.Set(Platform.Keys.LastSeenCrashDumpTicksUtc, newestWrite.Ticks);
            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("crash", "wer.dump.detect.failed", ex);
            return null;
        }
    }

    // ══ 4. THE FRAME WATCH (G-197: the Shell.RouteNoted consumer) ═══════════════════════════════════════════════════

    /// <summary>The always-on frame cost log: <c>frame.slow</c> per over-budget frame (≤ 6 a second, the rest counted),
    /// <c>nav.frames</c> for the four seconds after a route change, <c>scroll.frames</c> per wheel/drag burst, the
    /// once-a-second <c>frame.churn</c> census, and <c>session.frames</c> every 5 s. UI thread only (the engine raises the
    /// frame event inside the frame).</summary>
    public static class NavigationFrameWatch
    {
        const double WindowMs = 4000, SlowMs = 33.4, StallMs = 100, ScrollQuietMs = 500;
        const int SlowLinesPerSecond = 6, ScrollMinFrames = 5;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly Action<FrameStats> s_onFrame = OnFrame;
        static string s_route = "", s_routeKey = "", s_scrollRoute = "";
        static string? s_arg;
        static bool s_attached;
        static long s_navigationId, s_scrollNavigationId;
        static double s_slowBucketAt = double.NegativeInfinity, s_navAt = double.NaN, s_firstFrameMs = double.NaN;
        static double s_scrollLastAt = double.NaN, s_scrollStartAt = double.NaN, s_lastSessionLogAt;
        // The per-frame motion trace of the current burst (Diagnostics.ScrollTrace.cs): dt from the previous COMPLETED
        // frame (live or not), the kernel's displacement, the present counter's delta as read this frame (the ring moves
        // it to the frame it belongs to), the notches applied, the engine's slack.
        static readonly ScrollTraceRing s_trace = new();
        static double s_lastFrameAt = double.NaN;
        static long s_lastMissed = long.MinValue;
        static float s_traceRefreshMs;
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
            Log.Warn("image", "image decode failed id=" + id.ToString(CultureInfo.InvariantCulture)
                + " failure=" + failure + " attempts=" + attempts.ToString(CultureInfo.InvariantCulture));
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
            float dtMs = double.IsNaN(s_lastFrameAt) ? 0f : (float)(now - s_lastFrameAt);
            int missed = s_lastMissed == long.MinValue ? 0 : (int)Math.Clamp(stats.MissedVsyncs - s_lastMissed, 0, int.MaxValue);
            s_lastFrameAt = now; s_lastMissed = stats.MissedVsyncs;
            s_session.Add(stats.FrameMs, stats.RefreshIntervalMs);
            if (now - s_lastSessionLogAt >= 5000) LogSession(false);
            if (stats.FrameMs > FrameBudgetMs(stats.RefreshIntervalMs)) NoteSlowFrame(in stats, now);
            else if (HasSlack(in stats)) NoteSlackFrame(in stats, now);
            else if (stats.Census is { Length: > 0 } census)
                Log.Event(WaveeLogLevel.Info, "ui", "frame.churn", "steady churn route=" + s_route + (stats.ScrollActive ? " scroll=1 " : " scroll=0 ")
                    + "frameMs=" + F(stats.FrameMs) + " census=" + census);
            NoteScrollFrame(in stats, now, dtMs, missed);
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

        static bool HasSlack(in FrameStats s) => s.ScrollActive && s.SlackMs > ScrollTraceRules.SlackMs;

        static string Slack(in FrameStats s) => HasSlack(in s) ? " slack=" + F(s.SlackMs) + " cause=" + SlackCauseName(in s) : "";

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

        static void NoteScrollFrame(in FrameStats stats, double now, float dtMs, int missed)
        {
            if (!double.IsNaN(s_scrollLastAt) && now - s_scrollLastAt > ScrollQuietMs) FlushScroll();
            if (!stats.ScrollActive) return;
            if (double.IsNaN(s_scrollLastAt))
            {
                s_scrollStartAt = now; s_scrollNavigationId = s_navigationId; s_scrollRoute = s_route;
                s_scroll.Reset(in stats);
                s_trace.Clear();
                Shell.SidebarReplanCensus.Reset();   // the burst's own re-plan count (Sidebar.Census.cs)
            }
            s_scrollLastAt = now;
            s_scroll.Add(in stats);
            s_traceRefreshMs = (float)stats.RefreshIntervalMs;
            s_trace.Add(new ScrollTraceSample(dtMs, stats.ScrollDeltaDip, missed, stats.WheelNotches, stats.ScrollLiveMotion, stats.SlackMs,
                stats.ScrollContactHeld, stats.ScrollEdgePins, stats.ScrollStructuralDip, (byte)stats.ScrollZeroReason, stats.ScrollDtRepaired));
        }

        /// <summary>A page open moves its viewport for a frame or two (a restore); only a burst of ≥ 5 frames is a scroll.</summary>
        static void FlushScroll(bool sampleMemory = true)
        {
            if (double.IsNaN(s_scrollLastAt)) return;
            if (s_scroll.Frames >= ScrollMinFrames)
            {
                double wall = Math.Max(1, s_scrollLastAt - s_scrollStartAt);
                Log.Event(s_scroll.Stalls > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "scroll.frames",
                    "scroll frames route=" + s_scrollRoute + " navId=" + s_scrollNavigationId + " " + s_scroll.Rollup(wall, presentCadence: true)
                    + Shell.SidebarReplanCensus.Drain());
                // The verdict line: the burst's per-frame shifts and what they say (Diagnostics.ScrollTrace.cs). A
                // Warning when the user saw a hitch (a vblank without a present while the kernel was moving something).
                var samples = s_trace.Samples();
                var verdict = ScrollTraceRules.Analyse(samples, s_traceRefreshMs);
                Log.Event(verdict.LiveMissed > 0 || verdict.LiveZero > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "ui", "scroll.trace",
                    "scroll trace route=" + s_scrollRoute + " navId=" + s_scrollNavigationId + " refreshMs=" + F(s_traceRefreshMs) + " "
                    + ScrollTraceRules.Describe(in verdict) + " shifts=" + ScrollTraceRules.Format(samples, s_traceRefreshMs));
                if (sampleMemory) MemorySampler.Sample("scroll-end route=" + s_route);
            }
            s_scrollLastAt = s_scrollStartAt = double.NaN;
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
            public long HotAlloc;
            public FrameStats Worst, First, Last;

            public void Reset(in FrameStats first) { this = default; First = first; }

            public void Add(in FrameStats s)
            {
                Last = s; Frames++; SumMs += s.FrameMs;
                SumFlush += s.FlushMs; SumLayout += s.LayoutMs; SumRecord += s.RecordMs; SumSubmit += s.SubmitMs;
                HotAlloc += s.HotPhaseAllocBytes; Comps += s.ComponentsRendered;
                Gc0 += s.Gc0Delta; Gc1 += s.Gc1Delta; Gc2 += s.Gc2Delta;
                if (s.Census is not null) WithCensus++;
                if (s.FrameMs > FrameBudgetMs(s.RefreshIntervalMs)) OverBudget++;
                if (s.FrameMs >= SlowMs) Slow++;
                if (s.FrameMs >= StallMs) Stalls++;
                if (s.FrameMs > WorstMs) { WorstMs = s.FrameMs; Worst = s; }
            }

            /// <summary><paramref name="presentCadence"/> adds <c>presented=</c>/<c>missedVblanks=</c>. The engine counts a
            /// missed vblank for every refresh interval BETWEEN two consecutive presents (AppHost.NotePresented), so the
            /// figure is only meaningful while the loop is continuously live — a scroll burst. A navigation window is
            /// four seconds of mostly idle vblanks (nothing to present is not a miss), where it read as "missed=301 of
            /// 162 presented" on an album page the user was simply reading (2026-09-16); the nav rollup keeps
            /// overBudget/slow33/stall100, which count the frames that actually ran long.</summary>
            public readonly string Rollup(double wall, bool presentCadence)
            {
                double fps = wall > 0 ? Frames * 1000.0 / wall : 0, n = Math.Max(1, Frames);
                string cadence = presentCadence
                    ? " presented=" + (Last.PresentedFrames - First.PresentedFrames) + " missedVblanks=" + (Last.MissedVsyncs - First.MissedVsyncs)
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
            + " submit=" + F(s.SubmitMs) + " fenceWait=" + F(s.FenceWaitMs) + " present=" + F(s.PresentMs) + " gpu=" + F(s.GpuRenderMs)
            + " unaccounted=" + F(s.FrameMs - s.FlushMs - s.LayoutMs - s.AnimMs - s.RecordMs - s.SubmitMs)
            + " comps=" + s.ComponentsRendered + " nodes=" + s.NodesVisited + " draw=" + s.DrawNodeCount + " cmds=" + s.DrawCommandCount
            + " hotAlloc=" + s.HotPhaseAllocBytes + " measures=" + s.MeasureCount + " shapes=" + s.TextShapes + " textMiss=" + s.TextShapeMisses
            + " bindFires=" + s.BindingFires + " bindWrites=" + s.BindingWrites + " gc=" + s.Gc0Delta + "/" + s.Gc1Delta + "/" + s.Gc2Delta
            + " spansReused=" + s.SpansReused + " spansReRecorded=" + s.SpansReRecorded + " blurGroups=" + s.BlurGroupCount
            + " blurHeld=" + s.BlurSuppressedByScrollCount + " repaintPct=" + (double.IsNaN(s.RepaintCoverage) ? "-" : (s.RepaintCoverage * 100.0).ToString("0.00", CultureInfo.InvariantCulture))
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
