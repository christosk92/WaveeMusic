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
// Real data is never implied: without `--fake` the arm refuses to run unless `--bench-real` is on the line.
//
// The loop is FluentApp's own (RunFrame, TickDetachedHosts, WaitRequestWithDetached → WaitForWork → NoteLoopWait), with the
// wait's timeout capped only so a scenario's next action and its window end land on time — vsync, the latency waitable and
// the compositor tick pace every frame exactly as in a normal run. Each scenario: set up, a warm-up window, then a measured
// window (FrameLedger.Mark → Snapshot) whose four streams land as `<scenario>-{ui,render,gpu,memory,audio}.csv` + a binary
// `<scenario>.fgl`, and whose summary (Diagnostics.FrameBench.cs, pure) joins `frame-bench-summary.json` and the stdout
// table. Pass-granular GPU timing is on for the run (it adds a few timestamp queries per frame). The window is made topmost:
// a covered window stands its presents down, and a bench launched from a terminal opens behind it.

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

        /// <summary>A scenario's action clock: <see cref="Step"/> runs when the loop reaches <see cref="Next"/> (seconds since the
        /// scenario began, warm-up included) and returns when it wants to run again.</summary>
        sealed class Driver(Func<double, double> step)
        {
            public double Next;
            public readonly long T0 = Stopwatch.GetTimestamp();
            public double Now => (Stopwatch.GetTimestamp() - T0) / (double)Stopwatch.Frequency;
            public void Run() { if (Now >= Next) Next = step(Now); }
        }

        static bool TryFrameBench(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            var o = s_frameBench;
            if (!o.Enabled) return false;
            if (window is not Win32Window w || device is not D3D12Device) { Say("[frame-bench] unavailable: requires Win32Window + D3D12Device"); return true; }
            bool fake = Platform.Args.Fake;
            if (!fake && !o.Real)
            {
                Say("[frame-bench] refusing: without --fake this would drive the profile's real account. Add --bench-real to mean it, or run with --fake.");
                return true;
            }
            foreach (string bad in FrameBenchOptions.UnknownNames(Environment.GetCommandLineArgs()))
                Say("[frame-bench] unknown scenario '" + bad + "' (known: " + string.Join(",", FrameBenchScenarios.All) + ")");
            if (o.Scenarios.Length == 0) { Say("[frame-bench] no scenario selected"); return true; }

            SetWindowPos(w.Handle.Value, -1 /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            if (!PumpUntil(host, w, static () => NavigationFrameWatch.NavigationId > 0, 20)) { Say("[frame-bench] the shell never activated a route"); return true; }
            bool real = !fake;
            if (real)
            {
                Say("[frame-bench] REAL data: waiting for the profile's session to come online");
                if (!PumpUntil(host, w, static () => Spotify.Current.IsOnline, 60)) { Say("[frame-bench] the session never came online (is the profile signed in?)"); return true; }
                // A --profile run's settings live in the profile, not the owner's: set what the owner actually runs with.
                Platform.Settings.Set(Platform.Keys.PlaybackQuality, Platform.Network.QualityMax);   // lossless (FLAC)
                Prefs.AiLyrics.Set(Platform.Keys.AiLyricsEnabled, true);
            }
            var targets = real ? RealTargets(o) : FrameBenchTargets.Fake;
            Say("[frame-bench] " + (real ? "real" : "fake") + " targets: tracks=" + targets.Tracks.Length + " albums=" + targets.Albums.Length
                + " playlists=" + targets.Playlists.Length + " artists=" + targets.Artists.Length + " big=" + (targets.Big ?? "-"));

            bool passTiming = host.GpuPassTimingEnabled;
            host.GpuPassTimingEnabled = true;
            FrameLedger.Enable();
            FrameLedger.Attach(host);
            int stageMode = Prefs.Stage.Mode();
            var results = new List<FrameBenchScenarioSummary>(o.Scenarios.Length);
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
                    var r = RunScenario(name, host, w, o, targets, play, word, line, dir);
                    results.Add(r);
                    Say("[frame-bench] " + name + (r.Skipped is { } why ? " skipped: " + why
                        : string.Format(CultureInfo.InvariantCulture, " presents/s={0:0.0} uiP50={1:0.00}ms gpuP50={2:0.00}ms", r["presentsPerSec"], r["uiCpuMs.p50"], r["gpuMs.p50"])));
                }
            }
            finally
            {
                // Leave the account and the app as the run found them: nothing playing, no stage, no rail.
                Playback.Stop();
                if (Shell.Ui.ImmersiveLyrics.Peek()) Stage.Close(null, "bench");
                Shell.Ui.RailOpen.Value = false;
                Prefs.Stage.SetMode(stageMode);
                Pump(host, w, 1.0, null);
                host.GpuPassTimingEnabled = passTiming;
                FrameLedger.Disable();
            }

            var size = w.ClientSizePx;
            string json = FrameBenchMath.Json(VersionLabel(), o.Label, real, Environment.ProcessorCount, w.CurrentRefreshHz(),
                ((int)size.Width).ToString(CultureInfo.InvariantCulture) + "x" + ((int)size.Height).ToString(CultureInfo.InvariantCulture),
                o.MeasureSec, o.WarmupSec, FrameLedger.CyclesPerMs, DateTime.UtcNow, results);
            WriteArtifact(dir, "frame-bench-summary.json", json);
            var report = new StringBuilder(4096).AppendLine()
                .AppendLine("=== WAVEE FRAME BENCH (" + (real ? "real data" : "--fake") + (o.Label.Length > 0 ? ", " + o.Label : "") + ") ===")
                .AppendLine("version=" + VersionLabel() + "  processors=" + Environment.ProcessorCount + "  panel=" + w.CurrentRefreshHz() + "Hz  window="
                    + (int)size.Width + "x" + (int)size.Height + "  measure=" + o.MeasureSec + "s warmup=" + o.WarmupSec + "s")
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

        static FrameBenchScenarioSummary RunScenario(string name, AppHost host, Win32Window w, FrameBenchOptions o, FrameBenchTargets t,
            string? play, string? word, string? line, string dir)
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
            FrameBenchScenarioSummary Skip(string why) => new(name, why, []);
            FrameBenchScenarioSummary Measure(Driver? d, string? note = null) => MeasureWindow(name, host, w, o, d, dir, note);

            switch (name)
            {
                case FrameBenchScenarios.Idle:
                    Base(playing: false);
                    return Measure(null);
                case FrameBenchScenarios.IdlePlaying:
                    if (play is null) return Skip("no track to play");
                    Base(playing: true);
                    return Measure(null, "track=" + play);
                case FrameBenchScenarios.HomeScroll:
                    Base(playing: play is not null);
                    return Measure(ScrollSweeps(host, depthViewports: 8));
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
                    Nav(big == "liked" ? new Shell.Route(Shell.RouteKind.Liked) : Shell.For(EntityUri.Parse(big), "Bench list"));
                    Settle(2.0);
                    return Measure(ScrollSweeps(host, depthViewports: 30), "list=" + big);
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
                    return LedgerOverhead(host, w, o, play);
                default:
                    return Skip("unknown scenario");
            }
        }

        /// <summary>Warm-up, then the measured window: its ledger snapshot becomes the scenario's CSVs, binary dump and summary.</summary>
        static FrameBenchScenarioSummary MeasureWindow(string name, AppHost host, Win32Window w, FrameBenchOptions o, Driver? d, string dir, string? note)
        {
            Pump(host, w, o.WarmupSec, d);
            var mark = FrameLedger.Mark();
            Pump(host, w, o.MeasureSec, d);
            var snap = FrameLedger.Snapshot(mark);
            try
            {
                snap.WriteCsv(dir, name);
                snap.WriteFile(Path.Combine(dir, name + ".fgl"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("probe", "[frame-bench] could not write " + name + "'s ledger", ex);
            }
            return FrameBenchMath.Summarize(name, snap, note);
        }

        /// <summary>Programmatic glide sweeps over the largest vertical viewport on screen (the page): down in steps of 85 % of a
        /// viewport to <paramref name="depthViewports"/> viewports deep (or the end), then back up, a step every 0.55 s.</summary>
        static Driver ScrollSweeps(AppHost host, int depthViewports)
        {
            int dir = 1;
            var vps = new List<ViewportInfo>();
            return new Driver(now =>
            {
                vps.Clear();
                host.CopyViewports(vps);
                ViewportInfo best = default;
                double area = 0;
                foreach (var v in vps)
                    if (!v.Horizontal && v.Extent > v.Viewport + 1 && v.W * v.H > area) { area = v.W * v.H; best = v; }
                if (area <= 0 || host.TryGetScrollHandle(host.Scene.HandleAt(best.NodeIndex)) is not { } handle) return now + 0.5;
                double max = Math.Min(best.Extent - best.Viewport, best.Viewport * depthViewports);
                double to = best.Offset + dir * best.Viewport * 0.85;
                if (to >= max) { to = max; dir = -1; }
                else if (to <= 0) { to = 0; dir = 1; }
                handle.ScrollTo(to, ScrollMove.Glide);
                return now + 0.55;
            });
        }

        /// <summary>The ledger's own cost: the steady fullscreen visualizer (a frame every vblank, the same work every frame) in
        /// alternating windows with the ledger OFF and ON, measured around the loop itself — the UI thread's cycles per frame
        /// and the process's CPU (GetProcessTimes) — so the ON figure includes everything the ledger does.</summary>
        static FrameBenchScenarioSummary LedgerOverhead(AppHost host, Win32Window w, FrameBenchOptions o, string play)
        {
            var home = new Shell.Route(Shell.RouteKind.Home);
            Shell.Ui.RailOpen.Value = false;
            Nav(home);
            var snap0 = Playback.Snap();
            if (!snap0.IsPlaying) { Playback.PlayContext(play); PumpUntil(host, w, static () => Playback.Snap().IsPlaying, 20); }
            Prefs.Stage.SetMode((int)Stage.Mode.Visualizer);
            Stage.Open(null, "bench");
            Pump(host, w, 2.0, null);
            double sec = Math.Max(3, o.MeasureSec / 2.0);
            double[] uiUs = new double[2], frames = new double[2], procPct = new double[2], uiFrameUs = new double[2];
            using var proc = Process.GetCurrentProcess();
            for (int round = 0; round < 4; round++)
            {
                int on = round & 1;
                if (on == 1) { FrameLedger.Enable(); FrameLedger.Attach(host); } else FrameLedger.Disable();
                Pump(host, w, 0.5, null);
                proc.Refresh();
                TimeSpan cpu0 = proc.TotalProcessorTime;
                ulong c0 = ThreadCycles.Read();
                long t0 = Stopwatch.GetTimestamp();
                int n = Pump(host, w, sec, null, out double frameWallUs);
                ulong c1 = ThreadCycles.Read();
                double wallMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                proc.Refresh();
                double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
                double rate = FrameLedger.CyclesPerMs;
                uiUs[on] += rate > 0 && c1 > c0 ? (c1 - c0) / rate * 1000.0 : double.NaN;
                frames[on] += n;
                uiFrameUs[on] += frameWallUs;
                procPct[on] += cpuMs / wallMs / Environment.ProcessorCount * 100.0 / 2.0;   // two windows per arm: the mean
            }
            FrameLedger.Enable();
            FrameLedger.Attach(host);
            Stage.Close(null, "bench");
            var m = new List<KeyValuePair<string, double>>
            {
                new("framesPerSec.off", frames[0] / (2 * sec)), new("framesPerSec.on", frames[1] / (2 * sec)),
                new("uiCpuUsPerFrame.off", uiUs[0] / Math.Max(1, frames[0])), new("uiCpuUsPerFrame.on", uiUs[1] / Math.Max(1, frames[1])),
                new("runFrameWallUs.off", uiFrameUs[0] / Math.Max(1, frames[0])), new("runFrameWallUs.on", uiFrameUs[1] / Math.Max(1, frames[1])),
                new("processCpuPct.off", procPct[0]), new("processCpuPct.on", procPct[1]),
            };
            m.Add(new("overheadUiCpuUsPerFrame", m[3].Value - m[2].Value));
            m.Add(new("overheadRunFrameWallUs", m[5].Value - m[4].Value));
            m.Add(new("overheadProcessCpuPct", procPct[1] - procPct[0]));
            return new FrameBenchScenarioSummary(FrameBenchScenarios.LedgerOverhead, null, m, "stage-visualizer, " + 2 * sec + " s off vs on");
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
                double untilSec = seconds - Elapsed();
                if (d is not null) untilSec = Math.Min(untilSec, d.Next - d.Now);
                int cap = Math.Max(0, (int)Math.Ceiling(untilSec * 1000.0));
                wait = wait with { TimeoutMs = wait.TimeoutMs < 0 ? cap : Math.Min(cap, wait.TimeoutMs) };
                long ws = Stopwatch.GetTimestamp();
                w.WaitForWork(in wait);
                host.NoteLoopWait(ws, Stopwatch.GetTimestamp(), wait.TimeoutMs);
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
