// ── Screens/Diagnostics.Probe.FrameBench.cs ────────────────────────────────────────────────────────────────────────
// the `--frame-bench` GUI arm: real paced frames through wall-clock scenarios, every frame on the engine's FrameLedger
//
// Role: SHELL
// Owner: S
//
// Offline (no account, no network):
//   Wavee.exe --fake --profile <scratch dir> --frame-bench[=a,b] [--bench-sec 10] [--bench-warmup-sec 2] --probe-out <dir>
// Real data (the profile's own account, CDN images and audio, real lyrics; takes over that account's playback):
//   Wavee.exe --profile <bench profile> --frame-bench --bench-real [--bench-uris k=uri,...] [--bench-label cold|warm] --probe-out <dir>
// Real data is never implied: without `--fake` the arm refuses unless `--bench-real` is on the line, and `--bench-real` refuses
// unless `--profile` is too (it plays on that profile's account and sets that profile's settings, restored at the end).
//
// The loop is FluentApp's own (RunFrame, TickDetachedHosts, WaitRequestWithDetached → WaitForWork → NoteLoopWait), with the
// wait's timeout capped only so a scenario's next action and its window end land on time — vsync, the latency waitable and
// the compositor tick pace every frame exactly as in a normal run. Each scenario: set up, a warm-up window, then a measured
// window (FrameLedger.Mark → Snapshot). The snapshots are kept until the run ends, then every window is read at ONE cycle rate
// (the run's effective rate, Δ process cycles / Δ GetProcessTimes over the whole run) — so windows and runs compare on one
// scale — and written as `<scenario>-{ui,render,gpu,memory,audio}.csv` + a binary `<scenario>.fgl`, with the summary
// (Diagnostics.FrameBench.cs, pure) in `frame-bench-summary.json` and the stdout table. Pass-granular GPU timing is opt-in
// (`--bench-gpu-passes`: it adds timestamp queries at every pass boundary) and then measures its own cost (gpu-pass-overhead).
// The window is made topmost: a covered window stands its presents down, and a bench launched from a terminal opens behind it.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scroll.Runtime;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static FrameBenchOptions s_frameBench;

        /// <summary>A scenario's action clock: the step runs when the loop reaches <see cref="Next"/> (seconds since the scenario
        /// began, warm-up included) and returns when it wants to run again.</summary>
        sealed class Driver(Func<double, double> step)
        {
            public double Next;
            public readonly long T0 = Stopwatch.GetTimestamp();
            public double Now => (Stopwatch.GetTimestamp() - T0) / (double)Stopwatch.Frequency;
            public void Run() { if (Now >= Next) Next = step(Now); }
        }

        /// <summary>A scenario's outcome before the run's rate is known: a skip, a measured window (its snapshot), or an off/on A/B.</summary>
        sealed record Pending(string Name, string? Skipped = null, LedgerSnapshot? Snap = null, string? Note = null,
            List<KeyValuePair<string, double>>? Extra = null, OverheadArm Off = default, OverheadArm On = default, bool AB = false);

        static bool TryFrameBench(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            var o = s_frameBench;
            if (!o.Enabled) return false;
            if (window is not Win32Window w || device is not D3D12Device) { Say("[frame-bench] unavailable: requires Win32Window + D3D12Device"); return true; }
            bool fake = Platform.Args.Fake;
            if (FrameBenchOptions.RealRefusal(fake, o.Real, Platform.ProfileRoot) is { } refusal) { Say("[frame-bench] refusing: " + refusal); return true; }
            foreach (string bad in FrameBenchOptions.UnknownNames(Environment.GetCommandLineArgs()))
                Say("[frame-bench] unknown scenario '" + bad + "' (known: " + string.Join(",", FrameBenchScenarios.All) + ")");
            if (o.Scenarios.Length == 0) { Say("[frame-bench] no scenario selected"); return true; }

            SetWindowPos(w.Handle.Value, -1 /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            if (!PumpUntil(host, w, static () => NavigationFrameWatch.NavigationId > 0, 20)) { Say("[frame-bench] the shell never activated a route"); return true; }
            bool real = !fake;
            int quality = Platform.Settings.Get(Platform.Keys.PlaybackQuality);
            bool aiLyrics = Platform.Settings.Get(Platform.Keys.AiLyricsEnabled);
            if (real)
            {
                Say("[frame-bench] REAL data (profile " + Platform.ProfileRoot + "): waiting for its session to come online");
                if (!PumpUntil(host, w, static () => Spotify.Current.IsOnline, 60)) { Say("[frame-bench] the session never came online (is the profile signed in?)"); return true; }
                // A --profile run's settings live in the profile, not the owner's: set what the owner actually runs with (restored below).
                Platform.Settings.Set(Platform.Keys.PlaybackQuality, Platform.Network.QualityMax);   // lossless (FLAC)
                Prefs.AiLyrics.Set(Platform.Keys.AiLyricsEnabled, true);
            }
            var targets = real ? RealTargets(o) : FrameBenchTargets.Fake;
            Say("[frame-bench] " + (real ? "real" : "fake") + " targets: tracks=" + targets.Tracks.Length + " albums=" + targets.Albums.Length
                + " playlists=" + targets.Playlists.Length + " artists=" + targets.Artists.Length + " big=" + (targets.Big ?? "-"));

            bool passTiming = host.GpuPassTimingEnabled;
            host.GpuPassTimingEnabled = o.GpuPasses;
            FrameLedger.Enable();
            FrameLedger.Attach(host);
            var runMark = FrameLedger.Mark();
            int stageMode = Prefs.Stage.Mode();
            var pending = new List<Pending>(o.Scenarios.Length);
            string dir = OutDir();
            try
            {
                Directory.CreateDirectory(dir);
                (string? word, string? line) = NeedsLyrics(o) ? PickLyricsTracks(host, w, o, targets) : (null, null);
                string? play = word ?? (targets.Tracks.Length > 0 ? targets.Tracks[0] : null);
                Say("[frame-bench] playing track=" + (play ?? "-") + " word-synced=" + (word ?? "-") + " line-synced=" + (line ?? "-"));
                foreach (string name in o.Scenarios)
                {
                    if (w.IsClosed) break;
                    Say("[frame-bench] " + name + " ...");
                    var p = RunScenario(name, host, w, o, targets, play, word, line);
                    pending.Add(p);
                    Say("[frame-bench] " + name + (p.Skipped is { } why ? " skipped: " + why : " done") + (p.Note is { } n ? " (" + n + ")" : ""));
                }
            }
            finally
            {
                // Leave the account and the app as the run found them: nothing playing, no stage, no rail, the profile's settings back.
                Playback.Stop();
                if (Shell.Ui.ImmersiveLyrics.Peek()) Stage.Close(null, "bench");
                Shell.Ui.RailOpen.Value = false;
                Prefs.Stage.SetMode(stageMode);
                if (real)
                {
                    Platform.Settings.Set(Platform.Keys.PlaybackQuality, quality);
                    Prefs.AiLyrics.Set(Platform.Keys.AiLyricsEnabled, aiLyrics);
                }
                Pump(host, w, 1.0, null);
                host.GpuPassTimingEnabled = passTiming;
            }

            // ONE rate for the whole run (see the file header), then every window is read, written and summarized at it.
            var whole = FrameLedger.Snapshot(runMark);
            FrameLedger.Disable();
            double rate = FrameLedger.RateBetween(whole.Memory, 1000);
            if (!double.IsFinite(rate)) rate = whole.CyclesPerMs;
            var results = new List<FrameBenchScenarioSummary>(pending.Count);
            foreach (var p in pending)
            {
                if (p.Skipped is { } why) { results.Add(new FrameBenchScenarioSummary(p.Name, why, [])); continue; }
                if (p.AB) { results.Add(FrameBenchMath.Overhead(p.Name, p.Off, p.On, rate, Environment.ProcessorCount, p.Note)); continue; }
                var snap = p.Snap!.WithRate(rate);
                try
                {
                    snap.WriteCsv(dir, p.Name);
                    snap.WriteFile(Path.Combine(dir, p.Name + ".fgl"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("probe", "[frame-bench] could not write " + p.Name + "'s ledger", ex);
                }
                results.Add(FrameBenchMath.Summarize(p.Name, snap, p.Note, p.Extra));
            }

            var size = w.ClientSizePx;
            string json = FrameBenchMath.Json(VersionLabel(), o.Label, real, Environment.ProcessorCount, w.CurrentRefreshHz(),
                ((int)size.Width).ToString(CultureInfo.InvariantCulture) + "x" + ((int)size.Height).ToString(CultureInfo.InvariantCulture),
                o.MeasureSec, o.WarmupSec, rate, DateTime.UtcNow, results, o.GpuPasses);
            WriteArtifact(dir, "frame-bench-summary.json", json);
            var report = new StringBuilder(4096).AppendLine()
                .AppendLine("=== WAVEE FRAME BENCH (" + (real ? "real data" : "--fake") + (o.Label.Length > 0 ? ", " + o.Label : "") + ") ===")
                .AppendLine("version=" + VersionLabel() + "  processors=" + Environment.ProcessorCount + "  panel=" + w.CurrentRefreshHz() + "Hz  window="
                    + (int)size.Width + "x" + (int)size.Height + "  measure=" + o.MeasureSec + "s warmup=" + o.WarmupSec + "s  gpuPasses=" + (o.GpuPasses ? "on" : "off")
                    + "  cyclesPerMs=" + rate.ToString("0", CultureInfo.InvariantCulture))
                .AppendLine("output=" + Path.GetFullPath(dir)).AppendLine()
                .Append(FrameBenchMath.Table(results)).ToString();
            WriteArtifact(dir, "frame-bench-summary.txt", report);
            Say(report);
            try { Console.Out.WriteLine(report); } catch (IOException) { }
            return true;
        }

        static bool NeedsLyrics(FrameBenchOptions o)
            => Array.IndexOf(o.Scenarios, FrameBenchScenarios.Lyrics) >= 0 || Array.IndexOf(o.Scenarios, FrameBenchScenarios.LyricsLine) >= 0
            || Array.IndexOf(o.Scenarios, FrameBenchScenarios.StageLyrics) >= 0;

        static FrameBenchTargets RealTargets(FrameBenchOptions o)
        {
            string root = Path.Combine(Platform.LocalFolder, "WaveeMusic");
            static string? Read(string p) { try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }
            return FrameBenchTargets.FromProfileText(Read(Path.Combine(root, "play-recency.json")), Read(Path.Combine(root, "history.json")), o.Uris);
        }

        /// <summary>Ask the lyrics store for the candidates (the explicit `lyrics` / `lyrics-line` uris first, then recent tracks)
        /// WITHOUT playing them, and take the first word-synced and the first line-synced document.</summary>
        static (string? Word, string? Line) PickLyricsTracks(AppHost host, Win32Window w, FrameBenchOptions o, FrameBenchTargets t)
        {
            var cands = new List<string>();
            if (o.Uris.TryGetValue("lyrics", out var lw)) cands.Add(lw);
            if (o.Uris.TryGetValue("lyrics-line", out var ll)) cands.Add(ll);
            foreach (string u in t.Tracks) if (cands.Count < 10 && !cands.Contains(u)) cands.Add(u);
            static string Id(string uri) => uri[(uri.LastIndexOf(':') + 1)..];
            foreach (string u in cands) Lyrics.Store.Ensure(Id(u));
            PumpUntil(host, w, () => cands.TrueForAll(u => Lyrics.Store.Answered(Id(u))), 10);
            string? word = null, line = null;
            foreach (string u in cands)
            {
                var d = Lyrics.Store.Doc(Id(u));
                if (d is null || d.Lines.Count < 3) continue;
                if (word is null && d.Sync == Lyrics.SyncKind.Syllable) word = u;
                else if (line is null && d.Sync == Lyrics.SyncKind.Line) line = u;
            }
            return (word, line);
        }

        static Pending RunScenario(string name, AppHost host, Win32Window w, FrameBenchOptions o, FrameBenchTargets t,
            string? play, string? word, string? line)
        {
            var home = new Shell.Route(Shell.RouteKind.Home);
            void Settle(double s) => Pump(host, w, s, null);
            void Base(bool playing, string? track = null)
            {
                if (Shell.Ui.ImmersiveLyrics.Peek()) Stage.Close(null, "bench");
                Shell.Ui.RailOpen.Value = false;
                Nav(home);
                track ??= play;
                if (!playing) { Playback.Stop(); Settle(1.0); return; }
                if (track is null) return;
                var snap = Playback.Snap();
                if (!snap.HasCurrent || !snap.IsPlaying || snap.CurrentId.Text != track) Playback.PlayContext(track);
                PumpUntil(host, w, static () => Playback.Snap().IsPlaying, 20);
                Settle(0.5);
            }
            Pending Skip(string why) => new(name, why);
            Pending Measure(Driver? d, string? note = null, ScrollState? scroll = null, List<KeyValuePair<string, double>>? extra = null)
                => MeasureWindow(name, host, w, o, d, note, scroll, extra);

            switch (name)
            {
                case FrameBenchScenarios.Idle:
                {
                    Base(playing: false);
                    // Idle means quiet: let image loads, model loads and the post-navigation settle finish (≤ 20 s) before the window.
                    double settled = SettleQuiet(host, w, 20);
                    return Measure(null, "settled after " + settled.ToString("0.0", CultureInfo.InvariantCulture) + " s", extra: [new("settleSec", settled)]);
                }
                case FrameBenchScenarios.IdlePlaying:
                    if (play is null) return Skip("no track to play");
                    Base(playing: true);
                    return Measure(null, "track=" + play);
                case FrameBenchScenarios.HomeScroll:
                {
                    Base(playing: play is not null);
                    var s = new ScrollState(depthViewports: 8);
                    if (!s.WaitReady(host, w, 20)) return Skip("no scrollable page on Home after 20 s (" + s.Describe(host) + ")");
                    return Measure(s.Driver(host), "viewport=" + s.Key, s);
                }
                case FrameBenchScenarios.NavBurst:
                {
                    Base(playing: play is not null);
                    var hops = new List<Shell.Route>();
                    for (int i = 0; i < 6; i++)
                    {
                        if (i < t.Albums.Length) hops.Add(Shell.For(EntityUri.Parse(t.Albums[i]), "Bench album"));
                        if (i < t.Artists.Length) hops.Add(Shell.For(EntityUri.Parse(t.Artists[i]), "Bench artist"));
                        if (i < t.Playlists.Length) hops.Add(Shell.For(EntityUri.Parse(t.Playlists[i]), "Bench playlist"));
                    }
                    if (hops.Count == 0) return Skip("no entities to visit");
                    int k = 0;
                    return Measure(new Driver(now => { Nav(++k % 4 == 0 ? home : hops[k % hops.Count]); return now + 0.7; }), "hops over " + hops.Count + " entities");
                }
                case FrameBenchScenarios.PlaylistOpen:
                {
                    if (t.Playlists.Length == 0) return Skip("no playlist");
                    Base(playing: play is not null);
                    var pl = Shell.For(EntityUri.Parse(t.Playlists[0]), "Bench playlist");
                    bool open = false;
                    return Measure(new Driver(now => { open = !open; Nav(open ? pl : home); return now + (open ? 1.2 : 0.6); }), "playlist=" + t.Playlists[0]);
                }
                case FrameBenchScenarios.PlaylistScroll:
                {
                    Base(playing: play is not null);
                    string big = t.Big ?? (t.Playlists.Length > 0 ? t.Playlists[0] : "liked");
                    // Wait for the route to actually change before looking for its list: right after the call, the page just left is
                    // still the one on screen.
                    long nav0 = NavigationFrameWatch.NavigationId;
                    Nav(big == "liked" ? new Shell.Route(Shell.RouteKind.Liked) : Shell.For(EntityUri.Parse(big), "Bench list"));
                    PumpUntil(host, w, () => NavigationFrameWatch.NavigationId != nav0, 10);
                    Settle(1.0);
                    var s = new ScrollState(depthViewports: 30);
                    if (!s.WaitReady(host, w, 30)) return Skip("list " + big + " never became scrollable in 30 s (" + s.Describe(host) + ")");
                    return Measure(s.Driver(host), "list=" + big + " viewport=" + s.Key, s);
                }
                case FrameBenchScenarios.Lyrics:
                case FrameBenchScenarios.LyricsLine:
                {
                    string? track = name == FrameBenchScenarios.Lyrics ? word : line;
                    if (track is null) return Skip(name == FrameBenchScenarios.Lyrics ? "no word-synced lyrics among the candidates" : "no line-synced lyrics among the candidates");
                    Base(playing: true, track);
                    Shell.Ui.Mode.Value = Shell.RailMode.Lyrics;
                    Shell.Ui.RailOpen.Value = true;
                    Settle(1.0);
                    var r = Measure(null, "track=" + track);
                    Shell.Ui.RailOpen.Value = false;
                    return r;
                }
                case FrameBenchScenarios.StageLyrics:
                case FrameBenchScenarios.StageVisualizer:
                {
                    if (play is null) return Skip("no track to play");
                    Base(playing: true, name == FrameBenchScenarios.StageLyrics ? word ?? line ?? play : play);
                    Prefs.Stage.SetMode((int)(name == FrameBenchScenarios.StageLyrics ? Stage.Mode.Lyrics : Stage.Mode.Visualizer));
                    Stage.Open(null, "bench");
                    Settle(1.5);
                    var r = Measure(null);
                    Stage.Close(null, "bench");
                    return r;
                }
                case FrameBenchScenarios.TrackChange:
                {
                    string? ctx = t.Albums.Length > 0 ? t.Albums[0] : t.Playlists.Length > 0 ? t.Playlists[0] : null;
                    if (ctx is null) return Skip("no album or playlist to play through");
                    Base(playing: false);
                    Playback.PlayContext(ctx);
                    PumpUntil(host, w, static () => Playback.Snap().IsPlaying, 20);
                    int changes = 0, step = 0;
                    string last = Playback.Snap().CurrentId.Text;
                    // Alternate a skip (Next) with a natural roll into the next track (seek 3.5 s before the end: the gapless join).
                    var d = new Driver(now =>
                    {
                        var s = Playback.Snap();
                        if (s.CurrentId.Text != last) { changes++; last = s.CurrentId.Text; }
                        if (++step % 2 == 1) Playback.Next();
                        else if (s.DurationMs > 8000) Playback.SeekTo(s.DurationMs - 3500);
                        return now + 4.0;
                    });
                    var r = Measure(d, "context=" + ctx);
                    return r with { Note = r.Note + " trackChanges>=" + changes };
                }
                case FrameBenchScenarios.Video:
                    if (Platform.Args.FakeVideo is null) return Skip("needs --fake --fake-video <path>");
                    Base(playing: true, "spotify:track:tr0");
                    Shell.Ui.Mode.Value = Shell.RailMode.Video;
                    Shell.Ui.RailOpen.Value = true;
                    Settle(2.0);
                    {
                        var r = Measure(null, "clip=" + Path.GetFileName(Platform.Args.FakeVideo));
                        Shell.Ui.RailOpen.Value = false;
                        return r;
                    }
                case FrameBenchScenarios.LedgerOverhead:
                    if (play is null) return Skip("no track to play");
                    return OffOn(name, host, w, o, play, "ledger off vs on, stage-visualizer", restore: true,
                        set: on => { if (on) { FrameLedger.Enable(); FrameLedger.Attach(host); } else FrameLedger.Disable(); });
                case FrameBenchScenarios.GpuPassOverhead:
                    if (!o.GpuPasses) return Skip("needs --bench-gpu-passes");
                    if (play is null) return Skip("no track to play");
                    return OffOn(name, host, w, o, play, "GPU pass timing off vs on, stage-visualizer", restore: true,
                        set: on => host.GpuPassTimingEnabled = on);
                default:
                    return Skip("unknown scenario");
            }
        }

        /// <summary>Warm-up, then the measured window: its ledger snapshot (read at the run's rate when the run ends).</summary>
        static Pending MeasureWindow(string name, AppHost host, Win32Window w, FrameBenchOptions o, Driver? d, string? note, ScrollState? scroll,
            List<KeyValuePair<string, double>>? extra)
        {
            Pump(host, w, o.WarmupSec, d);
            scroll?.Restart(host);
            var mark = FrameLedger.Mark();
            Pump(host, w, o.MeasureSec, d);
            var snap = FrameLedger.Snapshot(mark);
            if (scroll is not null)
            {
                scroll.Observe(host);
                extra ??= [];
                extra.Add(new("scrollViewports", scroll.DistanceViewports));
                extra.Add(new("scrollSteps", scroll.Steps));
                note = (note is null ? "" : note + " ") + "scrolled " + scroll.DistanceViewports.ToString("0.0", CultureInfo.InvariantCulture) + " viewports in " + scroll.Steps + " steps";
            }
            return new Pending(name, Snap: snap, Note: note, Extra: extra);
        }

        /// <summary>Pump until the loop has gone quiet — two consecutive seconds with at most 2 presents each — or <paramref name="maxSec"/>.
        /// Returns the seconds it took.</summary>
        static double SettleQuiet(AppHost host, Win32Window w, double maxSec)
        {
            var sw = Stopwatch.StartNew();
            int quiet = 0;
            while (!w.IsClosed && sw.Elapsed.TotalSeconds < maxSec && quiet < 2)
            {
                ulong p0 = host.PresentedSequence;
                Pump(host, w, 1.0, null);
                quiet = host.PresentedSequence - p0 <= 2 ? quiet + 1 : 0;
            }
            return sw.Elapsed.TotalSeconds;
        }

        /// <summary>The page scroller a scroll scenario drives: among the vertical viewports at least 30 % the area of the largest, the one
        /// with the longest content that is at least three viewports long, is SHOWN (not a parked tab's page) and has a handle (on a list
        /// page that is the list, never the sidebar). Once found it is held by its <see cref="ScrollHandle"/> (a re-created viewport node does not lose it). Glide steps
        /// of 85 % of a viewport every 0.55 s, down to <c>depthViewports</c> viewports deep (or the end) and back; the distance it
        /// actually covered is read off the handle's own offset (the app-facing content offset).</summary>
        sealed class ScrollState(int depthViewports)
        {
            readonly List<ViewportInfo> _vps = new();
            ScrollHandle? _handle;
            double _lastOffset, _viewport = 1;
            int _dir = 1;
            public string Key = "";
            public double Distance;
            public int Steps;
            public double DistanceViewports => _viewport > 0 ? Distance / _viewport : 0;

            /// <summary>A page scroller's key starts with its tab scope (<c>Shell.ScrollScopeOf</c>: "tab" + n + "/").</summary>
            internal static bool IsPageScrollKey(string? key)
                => key is { Length: > 4 } && key.StartsWith("tab", StringComparison.Ordinal) && char.IsAsciiDigit(key[3]) && key.Contains('/');

            bool Adopt(AppHost host, double minRatio)
            {
                _vps.Clear();
                host.CopyViewports(_vps);
                // The window's client area (DIP): a viewport parked OFF it (the closed lyrics rail sits at x = window width and still
                // passes IsShown) is not a candidate — 2026-10-06, the real-data home-scroll scrolled that list, not Home.
                RectF client = host.Scene.AbsoluteRect(host.Scene.Root);
                bool OnScreen(in ViewportInfo v)
                {
                    float iw = Math.Min(v.X + v.W, client.X + client.W) - Math.Max(v.X, client.X);
                    float ih = Math.Min(v.Y + v.H, client.Y + client.H) - Math.Max(v.Y, client.Y);
                    return iw > 1f && ih > 1f && iw * ih >= 0.5f * v.W * v.H;
                }
                double maxArea = 0;
                foreach (var v in _vps) if (!v.Horizontal && OnScreen(v) && host.Scene.IsShown(host.Scene.HandleAt(v.NodeIndex))) maxArea = Math.Max(maxArea, v.W * v.H);
                ViewportInfo best = default;
                ScrollHandle? bestHandle = null;
                bool bestPage = false;
                foreach (var v in _vps)
                {
                    if (v.Horizontal || v.Viewport <= 0 || v.W * v.H < 0.3 * maxArea || v.Extent < v.Viewport * minRatio || !OnScreen(v)) continue;
                    var node = host.Scene.HandleAt(v.NodeIndex);
                    if (!host.Scene.IsShown(node)) continue;   // a parked tab's page (the one just left) is live but not on screen
                    if (host.TryGetScrollHandle(node) is not { } h) continue;
                    // The route's main page scroller (its key carries the tab scope, "tab2/home:…") outranks any other viewport.
                    bool page = IsPageScrollKey(v.ScrollKey);
                    if (bestHandle is null || (page && !bestPage) || (page == bestPage && v.Extent > best.Extent)) { best = v; bestHandle = h; bestPage = page; }
                }
                if (bestHandle is null) return false;
                if (!ReferenceEquals(bestHandle, _handle))
                {
                    _handle = bestHandle;
                    Key = (best.ScrollKey ?? "(no key)") + " rect=" + best.X.ToString("0", CultureInfo.InvariantCulture) + "," + best.Y.ToString("0", CultureInfo.InvariantCulture)
                        + " " + best.W.ToString("0", CultureInfo.InvariantCulture) + "x" + best.H.ToString("0", CultureInfo.InvariantCulture);
                    _lastOffset = bestHandle.Offset.Peek();
                }
                _viewport = best.Viewport;
                return true;
            }

            public bool WaitReady(AppHost host, Win32Window w, double maxSec)
                => PumpUntil(host, w, () => Adopt(host, 3.0), maxSec);

            public string Describe(AppHost host)
            {
                _vps.Clear();
                host.CopyViewports(_vps);
                var sb = new StringBuilder();
                foreach (var v in _vps)
                    if (!v.Horizontal) sb.Append(v.ScrollKey ?? "?").Append(' ').Append(v.Extent.ToString("0", CultureInfo.InvariantCulture)).Append('/')
                        .Append(v.Viewport.ToString("0", CultureInfo.InvariantCulture)).Append("; ");
                return sb.Length > 0 ? sb.ToString() : "no vertical viewport";
            }

            /// <summary>The measured window starts: distance from here.</summary>
            public void Restart(AppHost host)
            {
                Distance = 0;
                Steps = 0;
                if (_handle is { } h) _lastOffset = h.Offset.Peek();
            }

            /// <summary>Fold the handle's movement since the last look into the distance.</summary>
            public void Observe(AppHost host)
            {
                if (_handle is not { } h) return;
                double off = h.Offset.Peek();
                Distance += Math.Abs(off - _lastOffset);
                _lastOffset = off;
            }

            public Driver Driver(AppHost host) => new(now =>
            {
                // Re-adopt every step: a page that re-created its scroller, or a route change, hands over to what is on screen now.
                if (!Adopt(host, 1.5) && _handle is null) return now + 0.25;
                var h = _handle!;
                Observe(host);
                double vp = h.Viewport > 0 ? h.Viewport : _viewport;
                if (vp > 0) _viewport = vp;
                double max = Math.Min(h.MaxOffset, vp * depthViewports);
                double to = h.Offset.Peek() + _dir * vp * 0.85;
                if (to >= max) { to = max; _dir = -1; }
                else if (to <= 0) { to = 0; _dir = 1; }
                h.ScrollTo(to, ScrollMove.Glide);
                Steps++;
                return now + 0.55;
            });
        }

        /// <summary>An off/on A/B over the steady fullscreen visualizer (a frame every vblank, the same work every frame): four
        /// alternating windows, measured around the loop itself — the UI thread's cycles and RunFrame wall per frame, the process's
        /// CPU (GetProcessTimes) and, while the ledger records, the GPU ms per frame. The switch ends at <paramref name="restore"/>, the ledger on.</summary>
        static Pending OffOn(string name, AppHost host, Win32Window w, FrameBenchOptions o, string play, string note, bool restore, Action<bool> set)
        {
            Shell.Ui.RailOpen.Value = false;
            Nav(new Shell.Route(Shell.RouteKind.Home));
            // The steady state the A/B needs: the bench track itself playing (a previous scenario may have left an album mid-change).
            var now = Playback.Snap();
            if (!now.IsPlaying || now.CurrentId.Text != play) { Playback.PlayContext(play); PumpUntil(host, w, () => Playback.Snap().IsPlaying && Playback.Snap().CurrentId.Text == play, 20); }
            Prefs.Stage.SetMode((int)Stage.Mode.Visualizer);
            Stage.Open(null, "bench");
            Pump(host, w, 2.0, null);
            double sec = Math.Max(3, o.MeasureSec / 2.0);
            OverheadArm off = default, on = default;
            using var proc = Process.GetCurrentProcess();
            for (int round = 0; round < 4; round++)
            {
                bool arm = (round & 1) == 1;
                set(arm);
                Pump(host, w, 0.5, null);
                var mark = FrameLedger.Enabled ? FrameLedger.Mark() : default;
                proc.Refresh();
                TimeSpan cpu0 = proc.TotalProcessorTime;
                ulong c0 = ThreadCycles.Read();
                long t0 = Stopwatch.GetTimestamp();
                int n = Pump(host, w, sec, null, out double frameWallUs);
                ulong c1 = ThreadCycles.Read();
                double wallMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                proc.Refresh();
                double gpu = double.NaN;
                if (FrameLedger.Enabled)
                {
                    var g = FrameLedger.Snapshot(mark).Gpu;
                    double s = 0;
                    foreach (var x in g) s += x.GpuMs;
                    if (g.Length > 0) gpu = s / g.Length;
                }
                var a = new OverheadArm(n, c1 > c0 ? c1 - c0 : 0, frameWallUs / 1000.0, wallMs, (proc.TotalProcessorTime - cpu0).TotalMilliseconds, gpu);
                if (arm) on = on.Frames == 0 ? a : on.Add(a); else off = off.Frames == 0 ? a : off.Add(a);
            }
            set(restore);
            if (!FrameLedger.Enabled) { FrameLedger.Enable(); FrameLedger.Attach(host); }
            Stage.Close(null, "bench");
            return new Pending(name, Note: note + ", 2 x " + sec.ToString("0.#", CultureInfo.InvariantCulture) + " s per arm", Off: off, On: on, AB: true);
        }

        /// <summary>FluentApp's loop for <paramref name="seconds"/> of wall time (the wait capped to the window's end and the driver's
        /// next action). Returns the frames run.</summary>
        static int Pump(AppHost host, Win32Window w, double seconds, Driver? d) => Pump(host, w, seconds, d, out _);

        static int Pump(AppHost host, Win32Window w, double seconds, Driver? d, out double runFrameWallUs)
        {
            long t0 = Stopwatch.GetTimestamp();
            double Elapsed() => (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            int n = 0;
            long frameTicks = 0;
            while (!w.IsClosed && Elapsed() < seconds)
            {
                d?.Run();
                long f0 = Stopwatch.GetTimestamp();
                host.RunFrame();
                frameTicks += Stopwatch.GetTimestamp() - f0;
                host.TickDetachedHosts();
                n++;
                var wait = host.WaitRequestWithDetached();
                int asked = wait.TimeoutMs;
                double untilSec = seconds - Elapsed();
                if (d is not null) untilSec = Math.Min(untilSec, d.Next - d.Now);
                int cap = Math.Max(0, (int)Math.Ceiling(untilSec * 1000.0));
                wait = wait with { TimeoutMs = asked < 0 ? cap : Math.Min(cap, asked) };
                long ws = Stopwatch.GetTimestamp();
                w.WaitForWork(in wait);
                host.NoteLoopWait(ws, Stopwatch.GetTimestamp(), asked);   // the loop's own request, not the bench's cap
            }
            runFrameWallUs = frameTicks * 1_000_000.0 / Stopwatch.Frequency;
            return n;
        }

        /// <summary>Paced frames until <paramref name="ready"/> (checked each frame) or <paramref name="maxSec"/>.</summary>
        static bool PumpUntil(AppHost host, Win32Window w, Func<bool> ready, double maxSec)
        {
            var sw = Stopwatch.StartNew();
            while (!w.IsClosed && sw.Elapsed.TotalSeconds < maxSec)
            {
                if (ready()) return true;
                Pump(host, w, 0.05, null);
            }
            return ready();
        }
    }
}
