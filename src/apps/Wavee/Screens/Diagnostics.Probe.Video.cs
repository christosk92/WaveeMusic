// ── Screens/Diagnostics.Probe.Video.cs ─────────────────────────────────────────────────────────────────────────────
// `--video-probe <script>`: drive the REAL, signed-in GUI through the video paths a user takes (play, the player bar's video
// button, the placement menu, next track) with window input (pointer move/down/up queued on the window input queue the
// WM_POINTER handler feeds, at a keyed node's presented centre), and measure where the time goes.
//
// It never takes the frame loop over: the app runs exactly as the user's does (the normal message loop, vsync, the render
// thread). A background thread reads the script and posts each step to the UI thread; three meters run beside it:
//   - the UI stall watchdog: a 10 ms ping posted to the UI thread; a dispatch later than 50 ms is a stall (logged with its length),
//   - the frame meter: the gap between consecutive FrameCompleted on the UI thread while frames are produced,
//   - the video timeline: every `[vtime]` event the video stack writes (Playback.Video.Timeline, Playback.Video.cs) lands in the same log.
// Each step writes a `[vprobe]` event, so tools/video-probe-report.py can line steps, stalls, frames and stages up on one clock
// (Log.SinceStartMs, the process's own start).
//
// Script (one verb per line, `#` comments; args key=value):
//   online [timeout=ms]                 wait for the Spotify session to be online
//   allow device=ID                     a second scratch instance's device id: a foreign owner that is it counts as safe
//   requireowner kind=Safe|Us|Nobody|Foreign [timeout=ms]   wait for that owner; ABORT the run if it never comes. Safe = Us,
//                                       Nobody or the allowed device: never drive a run while someone else's device plays
//   owner                               log the owner, the active device and the deck's video facts
//   queue uri=spotify:track:X           add one track to the queue (after the deck)
//   signal to=video|audio [raw=1]       a controller's video switch for the owner (raw: the `signal` POST straight to it)
//   takeover                            "Play here": pull playback to this device
//   licdelay ms=N                       DEBUG BUILDS ONLY: hold every PlayReady licence answer N ms after its POST returns (0 = off),
//                                       so the source's first Start beats the key deterministically (the cold-licence race)
//   waitkind kind=Video|Audio [timeout=ms]   wait for the deck's mode (local host, or the owner's track_player)
//   remoteplay uri=X [video=1]          a single-track play straight to the foreign owner (video=1: the song-videos play)
//   waitowner kind=Us|Nobody|Foreign [timeout=ms]   wait for that owner (no abort)
//   play uri=spotify:track:X [video=1] [fromMs=N]   play the track as its own context (what a row click does); video=1 plays it as a video
//   click key=K [nth=N] [timeout=ms]    a click on the Nth presented node keyed K (waits until it is shown)
//   clickprefix prefix=P [nth=N]        same, first node whose key starts with P
//   keypress vk=27                      a key down/up pair on the window input queue (27 = Escape, 70 = F)
//   wait ms=N                           sleep
//   waitframe [timeout=ms]              wait until the video timeline reports a first presented frame since the last mark
//   keys [contains=S]                   log every live key containing S (to find click targets in a theme)
//   shot path=C:/x.png                  composited back buffer to a PNG
//   monitorof                           log each of this process's visible top-level windows (main, the pop-out) with its outer
//                                       rect, the monitor MonitorFromWindow picks, that monitor's rect, the window DPI, and fs=1
//                                       when the window covers that whole monitor (physical virtual-screen px)
//   movewin monitor=N [which=main|detached] [max=1]   restore the window and move it to the centre of monitor N's work area
//                                       (N counts in EnumDisplayMonitors order, from 1), through the engine's SetBoundsPx for
//                                       the main window; max=1 maximizes it there
//   screen monitor=N path=C:/x.png      the desktop as the user sees it on monitor N (GDI, physical px; every window on it)
//   resolve uri=X [rows=N]              context-resolve X and log its metadata (is_video_first, format_list_type) and first N rows
//                                       (uri, uid); reads only, never touches playback
//   ctxsettings uris=A,B                the account's stored per-context playback settings (PlaybackSettingsService
//                                       GetAllStoredContentValues): the entry count and each named uri's raw value, hex
//   plread id=X [rows=N]                GET /playlist/v2/playlist/X and log its attributes' format keys and the first N items raw (hex)
//   mark name=S                         a named mark (the report splits rounds on these)
//   opts                                the video ... menu's REAL state: deck kind, episode speed, the rate the deck applies, the
//                                       player's rate, the live quality pin and the playing rung, the aspect and on-top prefs
//   speed value=X                       the ... > Playback speed row's own action (Playback.SetEpisodeSpeed)
//   vrate value=X                       DIAGNOSTIC: the video host's rate straight (Playback.Video.SetRate), past the deck's
//                                       episode-only rule: does the pipeline honour a rate at all (see possample's vslope)
//   aspect mode=fit|crop|stretch|native|custom [ratio=R]   the ... > Aspect ratio row's own action
//   quality h=N                         the ... > Quality row's own action (0 = Auto)
//   ontop value=0|1                     the ... > Always on top toggle's own action
//   turnoff                             the ... > Turn off video row's own action
//   quit                                close the window after the script

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static string s_videoScript = "";
        static ulong s_allowedDevice;
        /// <summary>The verbs that only read or wait: they never reach another device, so they run whoever owns playback.</summary>
        static readonly HashSet<string> PassiveVerbs = new(StringComparer.Ordinal)
        {
            "online", "allow", "requireowner", "waitowner", "owner", "wait", "waitframe", "waitvideo", "keys", "pipkeys",
            "shot", "mark", "quit", "vcheck", "monitorof", "movewin", "screen", "possample", "resolve", "ctxsettings", "plread",
        };

        /// <summary>`possample [ms=16000] [every=250]`: sample the deck's playhead as the bar paints it (<c>State.Position(now)</c>),
        /// the published <c>Playback.PositionMs</c>, the duration and the live video clock every <paramref name="everyMs"/> for
        /// <paramref name="ms"/>, one `[vprobe] pos` event each, and judge the run: the largest and smallest step between samples
        /// and the slope against wall time.</summary>
        static string SamplePosition(int ms, int everyMs)
        {
            long t0 = Stopwatch.GetTimestamp();
            long firstPos = -1, lastPos = -1, prev = -1, maxStep = long.MinValue, minStep = long.MaxValue;
            int durFirst = -1, durMin = int.MaxValue, durMax = int.MinValue, n = 0;
            long firstV = -1, lastV = -1;
            double lastT = 0;
            while (true)
            {
                double t = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (t > ms) break;
                (int painted, int published, int dur, long vclock, string kind) = OnUi(static () =>
                {
                    Playback.State st = Playback.Snap();
                    return (st.Position(Playback.FrameNowMs()), Playback.PositionMs.Peek(), st.DurationMs, Playback.Video.ClockPositionMs(), st.Kind.ToString());
                });
                VEvent("pos", ("t", F(t)), ("pos", painted.ToString(CultureInfo.InvariantCulture)),
                    ("sig", published.ToString(CultureInfo.InvariantCulture)), ("dur", dur.ToString(CultureInfo.InvariantCulture)),
                    ("vclock", vclock.ToString(CultureInfo.InvariantCulture)), ("kind", kind));
                if (firstPos < 0) { firstPos = painted; durFirst = dur; firstV = vclock; }
                lastV = vclock;
                if (prev >= 0) { long step = painted - prev; maxStep = Math.Max(maxStep, step); minStep = Math.Min(minStep, step); }
                prev = lastPos = painted;
                lastT = t;
                durMin = Math.Min(durMin, dur);
                durMax = Math.Max(durMax, dur);
                n++;
                Thread.Sleep(everyMs);
            }
            double slope = lastT > 0 ? (lastPos - firstPos) / lastT : 0;
            double vslope = lastT > 0 && firstV >= 0 ? (lastV - firstV) / lastT : 0;
            return "samples=" + n + " maxStep=" + maxStep + " minStep=" + minStep + " slope=" + slope.ToString("0.000", CultureInfo.InvariantCulture)
                + " vslope=" + vslope.ToString("0.000", CultureInfo.InvariantCulture)
                + " dur=" + durFirst + (durMin != durMax ? " durRange=" + durMin + ".." + durMax : "");
        }
        static volatile bool s_vpAbort;
        static Win32Window? s_vpWindow;
        static AppHost? s_vpHost;
        static volatile bool s_vpQuit;

        /// <summary>`--video-probe &lt;script&gt;` from argv; "" without it. Parsed in <see cref="InstallGuiArms"/>.</summary>
        internal static void ParseVideoProbe(string[] args)
        {
            int at = Array.IndexOf(args, "--video-probe");
            s_videoScript = at >= 0 && at + 1 < args.Length && File.Exists(args[at + 1]) ? Path.GetFullPath(args[at + 1]) : "";
        }

        /// <summary>The pre-loop hook's door: starts the driver thread and returns false, so the normal loop runs.</summary>
        static bool TryVideoProbe(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            if (s_videoScript.Length == 0) return false;
            if (window is not Win32Window w) { Say("[video-probe] unavailable: requires Win32Window"); return false; }
            s_vpWindow = w;
            s_vpHost = host;
            string[] lines = File.ReadAllLines(s_videoScript);
            FluentApp.FrameCompleted += OnProbeFrame;
            new Thread(() => StallWatch()) { IsBackground = true, Name = "video-probe-watch", Priority = ThreadPriority.AboveNormal }.Start();
            new Thread(() => RunVideoScript(lines)) { IsBackground = true, Name = "video-probe" }.Start();
            Probe.VEvent("start", ("script", s_videoScript));
            return false;
        }

        // ── the meters ────────────────────────────────────────────────────────────────────────────────────────────

        static long s_lastFrameTs;
        static long s_frames;
        static bool s_prevRendered;

        /// <summary>UI thread, every completed frame: a gap over 50 ms between two frames is logged (the frame meter).</summary>
        static void OnProbeFrame(FrameStats stats)
        {
            long now = Stopwatch.GetTimestamp();
            long last = s_lastFrameTs;
            s_lastFrameTs = now;
            s_frames++;
            if (last == 0) return;
            double gap = (now - last) * 1000.0 / Stopwatch.Frequency;
            bool rendered = stats.Rendered;
            bool prevRendered = s_prevRendered;
            s_prevRendered = rendered;
            if (rendered && prevRendered && gap >= 50.0) VEvent("frame.gap", ("ms", F(gap)));
        }

        /// <summary>The UI stall watchdog: posts a ping every 10 ms and measures when the UI thread ran it. A dispatch later than
        /// 50 ms is a stall; the event carries its length and when it began (both on the log's clock).</summary>
        static void StallWatch()
        {
            var done = new ManualResetEventSlim(false);
            while (!s_vpQuit)
            {
                done.Reset();
                long posted = Stopwatch.GetTimestamp();
                long startMs = Log.SinceStartMs;
                long ran = 0;
                Shell.PostUiForProbe(() => { ran = Stopwatch.GetTimestamp(); done.Set(); });
                // Wait for the ping however long the UI is stuck; report the stall once it ends.
                done.Wait();
                double lat = (Volatile.Read(ref ran) - posted) * 1000.0 / Stopwatch.Frequency;
                if (lat >= 50.0) VEvent("ui.stall", ("ms", F(lat)), ("from", startMs.ToString(CultureInfo.InvariantCulture)));
                Thread.Sleep(10);
            }
        }

        // ── the driver ────────────────────────────────────────────────────────────────────────────────────────────

        static void RunVideoScript(string[] lines)
        {
            try
            {
                for (int i = 0; i < lines.Length && !s_vpQuit; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var (verb, args) = ParseLine(line);
                    long t0 = Log.SinceStartMs;
                    VEvent("step", ("n", (i + 1).ToString(CultureInfo.InvariantCulture)), ("verb", verb), ("line", line));
                    // Every verb that can reach the owner (a play, a click, a key, a seek, a signal) runs only while the owner is
                    // safe: us, nobody, or our own second scratch instance. Anything else aborts the run before it acts.
                    string result = PassiveVerbs.Contains(verb) || SafeToDrive(out string unsafeWhy) ? RunStep(verb, args) : unsafeWhy;
                    VEvent("step.done", ("verb", verb), ("ms", (Log.SinceStartMs - t0).ToString(CultureInfo.InvariantCulture)), ("result", result));
                    // A run that never signed in measures nothing real: stop instead of driving a signed-out app.
                    if (verb == "online" && result != "online") { VEvent("abort", ("why", "the session never came online")); break; }
                    // Someone else's device owns playback: a play from here would be FORWARDED to it. Never drive it; close.
                    if (s_vpAbort) { RunStep("quit", new Dictionary<string, string>()); break; }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("vprobe", "the video probe script failed", ex);
            }
            VEvent("end");
        }

        static (string Verb, Dictionary<string, string> Args) ParseLine(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int k = 1; k < parts.Length; k++)
            {
                int eq = parts[k].IndexOf('=');
                if (eq > 0) args[parts[k][..eq]] = parts[k][(eq + 1)..];
            }
            return (parts[0].ToLowerInvariant(), args);
        }

        /// <summary>`resolve`: one context-resolve answer, summarised — the metadata that says how the context plays
        /// (<c>is_video_first</c>, <c>format_list_type</c>) and its first <paramref name="rows"/> rows.</summary>
        static string ResolveRows(string uri, int rows)
        {
            Spotify.Api.Result result = Spotify.Api.ContextResolve(uri, CancellationToken.None);
            if (!result.Ok || result.Length == 0) return "refused status=" + result.Status;
            var buffer = Spotify.Decode.ClusterBuffer.Rent();
            try
            {
                Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
                var sb = new System.Text.StringBuilder("rows=" + page.TrackCount + " meta=");
                sb.Append(page.Metadata.IsEmpty ? "{}" : System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.Metadata)));
                ReadOnlySpan<Spotify.Decode.ClusterTrack> tracks = buffer.Tracks(page.TrackStart, page.TrackCount);
                for (int k = 0; k < tracks.Length && k < rows; k++)
                    sb.Append(" | ").Append(System.Text.Encoding.UTF8.GetString(buffer.Utf8(tracks[k].Uri)))
                      .Append(" uid=").Append(System.Text.Encoding.UTF8.GetString(buffer.Utf8(tracks[k].Uid)))
                      .Append(" dur=").Append(tracks[k].DurationMs);
                return sb.ToString();
            }
            finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
        }

        /// <summary>`plread`: one playlist4 read, summarised — the length, the format type and the first rows' raw item messages.</summary>
        static string PlaylistItems(string id, int rows)
        {
            Spotify.Api.Result result = Spotify.Api.Playlist(id, CancellationToken.None);
            if (!result.Ok) return "refused status=" + result.Status;
            var sb = new System.Text.StringBuilder("bytes=" + result.Length);
            for (var r = new Spotify.Decode.ProtoReader(result.Bytes); r.Next();)
            {
                if (r.Field == 3 && r.Wire == 2)
                    for (var at = new Spotify.Decode.ProtoReader(r.Bytes()); at.Next();)
                    {
                        if (at.Field == 11 && at.Wire == 2) sb.Append(" format=").Append(System.Text.Encoding.UTF8.GetString(at.Bytes()));
                        else at.Skip();
                    }
                else if (r.Field == 5 && r.Wire == 2)
                {
                    int n = 0;
                    for (var c = new Spotify.Decode.ProtoReader(r.Bytes()); c.Next();)
                    {
                        if (c.Field == 3 && c.Wire == 2 && n++ < rows) sb.Append(" | item=").Append(Convert.ToHexString(c.Bytes()));
                        else if (c.Field != 3 && c.Wire == 2) sb.Append(" | f").Append(c.Field).Append('=').Append(Convert.ToHexString(c.Bytes()));
                        else c.Skip();
                    }
                    sb.Append(" items=").Append(n);
                }
                else r.Skip();
            }
            return sb.ToString();
        }

        /// <summary>`ctxsettings`: the stored per-context settings (uri ↔ value, parallel fields 1 and 2), summarised.</summary>
        static string ContextSettings(string[] uris)
        {
            Spotify.Api.Result result = Spotify.Api.PostEncoded(
                "/playback-settings/spotify.playbacksettings.PlaybackSettingsService/GetAllStoredContentValues",
                Spotify.ApiHost.Spclient, Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity,
                [0x08, 0xE8, 0x07], "application/x-protobuf", null, CancellationToken.None);
            if (!result.Ok) return "refused status=" + result.Status;
            var keys = new List<string>();
            var values = new List<string>();
            for (var r = new Spotify.Decode.ProtoReader(result.Bytes); r.Next();)
            {
                if (r.Field == 1 && r.Wire == 2) keys.Add(System.Text.Encoding.UTF8.GetString(r.Bytes()));
                else if (r.Field == 2 && r.Wire == 2) values.Add(Convert.ToHexString(r.Bytes()));
                else r.Skip();
            }
            var sb = new System.Text.StringBuilder("entries=" + keys.Count);
            var counts = new Dictionary<string, int>();
            for (int k = 0; k < values.Count; k++) counts[values[k]] = counts.GetValueOrDefault(values[k]) + 1;
            foreach (var kv in counts) sb.Append(" value[").Append(kv.Key).Append("]x").Append(kv.Value);
            foreach (string uri in uris)
            {
                int at = keys.IndexOf(uri);
                sb.Append(" | ").Append(uri).Append('=').Append(at >= 0 && at < values.Count ? values[at] : "(none)");
            }
            for (int k = 0; k < keys.Count && k < values.Count; k++)
                if (!values[k].StartsWith("0A0204051202100012", StringComparison.Ordinal) && !values[k].StartsWith("0A0105", StringComparison.Ordinal))
                    sb.Append(" || ").Append(keys[k]).Append('=').Append(values[k]);
            return sb.ToString();
        }

        static string Arg(Dictionary<string, string> a, string k, string fallback = "") => a.TryGetValue(k, out var v) ? v : fallback;
        static int ArgInt(Dictionary<string, string> a, string k, int fallback)
            => a.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

        static string RunStep(string verb, Dictionary<string, string> a)
        {
            switch (verb)
            {
                case "online":
                    {
                        long until = Environment.TickCount64 + ArgInt(a, "timeout", 90_000);
                        while (Environment.TickCount64 < until)
                        {
                            if (OnUi(static () => Spotify.Current.IsOnline)) return "online";
                            Thread.Sleep(100);
                        }
                        return "timeout";
                    }
                case "allow":
                    s_allowedDevice = Arg(a, "device").Length > 0 ? Playback.DeviceHash(Arg(a, "device")) : 0;
                    return "allowed " + (s_allowedDevice != 0 ? "device" : "none");
                case "requireowner":
                case "waitowner":
                    {
                        string want = Arg(a, "kind", "Safe");
                        long until = Environment.TickCount64 + ArgInt(a, "timeout", 5_000);
                        while (true)
                        {
                            string now = OnUi(OwnerLine);
                            if (OwnerMatches(want)) return "owner " + want + ": " + now;
                            if (Environment.TickCount64 >= until)
                            {
                                if (verb == "waitowner") return "timeout: " + now;
                                VEvent("abort", ("why", "owner is not " + want), ("owner", now));
                                s_vpAbort = true;
                                return "ABORT owner is not " + want + ": " + now;
                            }
                            Thread.Sleep(100);
                        }
                    }
                case "owner":
                    return OnUi(OwnerLine);
                case "signal":
                    {
                        // A controller's video switch to the device that owns playback. raw=1 posts the `signal` command straight to
                        // the owner (the wire alone); otherwise it is the player bar's own verb (the placement post the reducer forwards).
                        bool on = Arg(a, "to", "video") != "audio";
                        if (Arg(a, "raw") is "1")
                        {
                            string target = OnUi(static () => Playback.Snap() is { Own.Kind: Playback.Owner.Foreign } st ? Playback.Devices.IdOf(st.Own.Device) : "");
                            if (target.Length == 0) return "no foreign owner";
                            bool sent = Spotify.Connect.Signal(target, on ? Spotify.Decode.VideoSignal.ToVideo : Spotify.Decode.VideoSignal.ToAudio, CancellationToken.None);
                            return "raw signal " + (on ? "switch-to-video" : "switch-to-audio") + " sent=" + sent;
                        }
                        OnUi(() => { Playback.SwitchVideo(on); return true; });
                        return "signal " + (on ? "video" : "audio") + " posted";
                    }
                case "waitkind":
                    {
                        // Wait for the deck's mode (State.Kind: the local host, or the foreign owner's track_player).
                        string want = Arg(a, "kind", "Video");
                        long t0 = Environment.TickCount64, until = t0 + ArgInt(a, "timeout", 20_000);
                        while (Environment.TickCount64 < until)
                        {
                            if (OnUi(() => Playback.Snap().Kind.ToString()) == want) return want + " after " + (Environment.TickCount64 - t0) + " ms";
                            Thread.Sleep(50);
                        }
                        return "timeout: " + OnUi(OwnerLine);
                    }
                case "remoteplay":
                    {
                        // The desktop's "song videos" play straight to the foreign owner: a single-track context whose metadata says
                        // is_single_track_video_content (all.saz 1831).
                        string uri = Arg(a, "uri");
                        string target = OnUi(static () => Playback.Snap() is { Own.Kind: Playback.Owner.Foreign } st ? Playback.Devices.IdOf(st.Own.Device) : "");
                        if (target.Length == 0) return "no foreign owner";
                        bool sent = Spotify.Connect.PlayContext(target, uri, uri, false, CancellationToken.None, video: Arg(a, "video") is "1");
                        return "remote play " + uri + " sent=" + sent;
                    }
                case "plread":
                    return PlaylistItems(Arg(a, "id"), ArgInt(a, "rows", 3));
                case "ctxsettings":
                    return ContextSettings(Arg(a, "uris").Split(',', StringSplitOptions.RemoveEmptyEntries));
                case "resolve":
                    return ResolveRows(Arg(a, "uri"), ArgInt(a, "rows", 4));
                case "takeover":
                    OnUi(static () => { Playback.TakeOver(); return true; });
                    return "takeover posted";
                case "pause":
                case "resume":
                    {
                        // The transport's pause / resume (what the play button does), on THIS device only.
                        if (OnUi(static () => Playback.Snap().Own.Kind == Playback.Owner.Foreign)) return "refused: another device owns playback";
                        bool pause = verb == "pause";
                        OnUi(() => { if (pause) Playback.Pause("probe"); else Playback.Resume("probe"); return true; });
                        return verb + " posted";
                    }
                case "volume":
                    {
                        // `volume level=0`: this device's own volume (and mute at 0), for a silent run. Never while another
                        // device owns playback: there the volume input is forwarded to that device.
                        float level = Math.Clamp(float.Parse(Arg(a, "level", "0"), CultureInfo.InvariantCulture), 0f, 1f);
                        if (OnUi(static () => Playback.Snap().Own.Kind == Playback.Owner.Foreign)) return "refused: another device owns playback";
                        OnUi(() => { Playback.SetVolume(level); Playback.SetMuted(level <= 0f); return true; });
                        long until = Environment.TickCount64 + 3_000;
                        (float vol, bool muted) = (1f, false);
                        while (Environment.TickCount64 < until)
                        {
                            (vol, muted) = OnUi(static () => (Playback.Snap().Volume, Playback.Muted.Peek()));
                            if (Math.Abs(vol - level) < 0.01f && muted == (level <= 0f)) break;
                            Thread.Sleep(50);
                        }
                        (double videoVol, bool videoMuted) = OnUi(static () => Playback.Video.Level());
                        string got = "volume=" + vol.ToString("0.000", CultureInfo.InvariantCulture) + " muted=" + muted
                            + " videoVolume=" + videoVol.ToString("0.000", CultureInfo.InvariantCulture) + " videoMuted=" + videoMuted;
                        if (level <= 0f && (vol > Playback.MuteFloor || !muted || (videoVol > Playback.MuteFloor && !videoMuted)))
                        {
                            // A silent run that is not silent ends here, before anything plays.
                            VEvent("abort", ("why", "not silent"), ("state", got));
                            s_vpAbort = true;
                            return "ABORT not silent: " + got;
                        }
                        return got;
                    }
                case "licdelay":
#if DEBUG
                    Playback.Video.License.ProbeDelayMs = Math.Clamp(ArgInt(a, "ms", 0), 0, 8_000);
                    return "licence answers held " + Playback.Video.License.ProbeDelayMs + " ms";
#else
                    return "licdelay: Debug builds only";
#endif
                case "queue":
                    {
                        // "Add to queue" for one track (what a row's menu does): the row after the deck, in the user queue.
                        string uri = Arg(a, "uri");
                        return OnUi(() =>
                        {
                            if (!EntityId.TryParse(uri.AsSpan(), out EntityId id)) return false;
                            Queue.Enqueue(Entities.Ref(id));
                            return true;
                        }) ? "queued " + uri : "bad uri";
                    }
                case "play":
                    {
                        string uri = Arg(a, "uri");
                        bool video = Arg(a, "video") is "1" or "true";
                        int fromMs = ArgInt(a, "fromMs", 0);
                        Playback.Video.Timeline.Reset("play " + uri);
                        // The real UI's Play verb (a row click, a deeplink): the context path, metadata fetch and all. InspectKit.Play is
                        // the headless recipe (Queue.Replace on a bare row) and does not take under a live session. The deck's own
                        // ensure asks the row's Video group with its identity, so the bar's toggle lights with no help from here.
                        if (!video) return OnUi(() =>
                        {
                            if (!EntityId.TryParse(uri.AsSpan(), out EntityId id)) return false;
                            Playback.PlayContext(id, default, fromMs > 0 ? fromMs : -1);
                            return true;
                        }) ? "ok" : "bad uri";
                        return OnUi(() => InspectKit.Play(uri, fromMs, video: video)) ? "ok" : "bad uri";
                    }
                case "click":
                case "clickprefix":
                    {
                        string key = verb == "click" ? Arg(a, "key") : Arg(a, "prefix");
                        int nth = ArgInt(a, "nth", 0);
                        // fx: where across the node to press, as a fraction of its width (a split button's halves are one key).
                        float fx = Arg(a, "fx") is { Length: > 0 } fxs ? float.Parse(fxs, CultureInfo.InvariantCulture) : 0.5f;
                        long until = Environment.TickCount64 + ArgInt(a, "timeout", 15_000);
                        while (Environment.TickCount64 < until)
                        {
                            (bool ok, Point2 p, string why) = OnUi(() => Aim(key, nth, fx, prefix: verb == "clickprefix"));
                            if (ok)
                            {
                                if (Arg(a, "frame") is "1") Playback.Video.Timeline.Reset("click " + key);
                                PostClick(p);
                                return "clicked " + key + " at " + F(p.X) + "," + F(p.Y);
                            }
                            if (Environment.TickCount64 + 100 >= until) return "not found: " + why;
                            Thread.Sleep(100);
                        }
                        return "timeout";
                    }
                case "keypress":
                    {
                        int vk = ArgInt(a, "vk", 27);
                        PostKey(vk);
                        return "vk " + vk;
                    }
                case "wait":
                    Thread.Sleep(Math.Clamp(ArgInt(a, "ms", 1000), 0, 600_000));
                    return "ok";
                case "waitframe":
                    {
                        long until = Environment.TickCount64 + ArgInt(a, "timeout", 30_000);
                        while (Environment.TickCount64 < until)
                        {
                            if (Playback.Video.Timeline.FirstFrameSeen) return "frame after " + Playback.Video.Timeline.SinceResetMs + " ms";
                            Thread.Sleep(20);
                        }
                        return "timeout after " + Playback.Video.Timeline.SinceResetMs + " ms";
                    }
                case "waitvideo":
                    {
                        // The bar's video toggle is pressable only once the now-playing track's metadata says it has a
                        // video (kind 99/182). How long that takes after a play is part of what the user feels.
                        long t0 = Environment.TickCount64, until = t0 + ArgInt(a, "timeout", 20_000);
                        while (Environment.TickCount64 < until)
                        {
                            if (OnUi(() => Shell.NowPlayingFace.HasVideoPeek())) return "available after " + (Environment.TickCount64 - t0) + " ms";
                            Thread.Sleep(50);
                        }
                        string track = OnUi(() => Shell.NowPlayingFace.TrackPeek() is { IsValid: true } t ? t.Title : "(none)");
                        return "never available in " + (Environment.TickCount64 - t0) + " ms track=" + track;
                    }
                case "place":
                    {
                        // The placement menu row's own action (Video.PlacementMenu → PlayerVerbs.SetVideoPlacement).
                        string to = Arg(a, "to");
                        Wavee.Video.SurfacePlacement target = to switch
                        {
                            "docked" => Wavee.Video.SurfacePlacement.Docked, "floating" => Wavee.Video.SurfacePlacement.Floating,
                            "detached" => Wavee.Video.SurfacePlacement.Detached, "fullscreen" => Wavee.Video.SurfacePlacement.Fullscreen,
                            _ => Wavee.Video.SurfacePlacement.None,
                        };
                        Playback.Video.Timeline.Reset("place " + to);
                        long t0 = Environment.TickCount64;
                        OnUi(() => { PlayerVerbs.Default.SetVideoPlacement(target, true); return true; });
                        return "placed " + to + " uiMs=" + (Environment.TickCount64 - t0);
                    }
                case "vcheck":
                    return Health(ArgInt(a, "ms", 3000), "vcheck", out _);
                case "possample":
                    return SamplePosition(ArgInt(a, "ms", 16_000), Math.Max(20, ArgInt(a, "every", 250)));
                case "dragat":
                    {
                        // A press-move-release at window DIP x,y (an unkeyed surface: the mini player's drag chrome).
                        float x = ArgInt(a, "x", 0), y = ArgInt(a, "y", 0);
                        return DragFrom(new Point2(x, y), ArgInt(a, "dx", -300), ArgInt(a, "dy", -200),
                            Math.Max(1, ArgInt(a, "steps", 30)), ArgInt(a, "stepMs", 16), "at " + F(x) + "," + F(y), ArgInt(a, "holdMs", 0));
                    }
                case "seekdrag":
                    {
                        // Grab the seek bar at fraction `from` and drag it to `to` (a scrub), optionally holding before release.
                        float from = float.Parse(Arg(a, "from", "0.2"), CultureInfo.InvariantCulture);
                        float to = float.Parse(Arg(a, "to", "0.7"), CultureInfo.InvariantCulture);
                        RectF r = OnUi(() => AimRect("seek", false));
                        if (r.W < 1f) return "not found: seek";
                        Playback.Video.Timeline.Reset("seekdrag " + Arg(a, "from") + "->" + Arg(a, "to"));
                        return DragFrom(new Point2(r.X + r.W * from, r.Y + r.H * 0.5f), r.W * (to - from), 0,
                            Math.Max(1, ArgInt(a, "steps", 20)), ArgInt(a, "stepMs", 16), "seek", ArgInt(a, "holdMs", 0));
                    }
                case "pipkeys":
                    {
                        // Every shown key whose rect sits inside the mini player's corner (the PiP draws unkeyed chrome over keyed content).
                        string text = OnUi(() =>
                        {
                            var all = new List<(NodeHandle Node, string Key)>();
                            SceneStore scene = s_vpHost!.Scene;
                            scene.CopyDebugKeys(all);
                            var sb = new System.Text.StringBuilder();
                            foreach (var (node, k) in all)
                            {
                                if (!scene.IsShown(node)) continue;
                                RectF r = scene.PresentedAbsoluteRect(node);
                                if (r.X < ArgInt(a, "minX", 700) || r.Y < ArgInt(a, "minY", 450) || r.W < 1f) continue;
                                sb.Append(k).Append('@').Append(F(r.X)).Append(',').Append(F(r.Y)).Append(' ').Append(F(r.W)).Append('x').Append(F(r.H)).Append("; ");
                            }
                            return sb.ToString();
                        });
                        VEvent("keys", ("contains", "pip-corner"), ("list", text));
                        return "listed";
                    }
                case "soak":
                    return Soak(ArgInt(a, "minutes", 15), ArgInt(a, "seed", 1), Arg(a, "uris").Split(',', StringSplitOptions.RemoveEmptyEntries));
                case "drag":
                    {
                        // A real press-move-release on the Nth shown node keyed `key` (fx/fy: where on it), dx/dy DIP over `steps` moves.
                        string key = Arg(a, "key");
                        float fx = Arg(a, "fx") is { Length: > 0 } fxs ? float.Parse(fxs, CultureInfo.InvariantCulture) : 0.5f;
                        float fy = Arg(a, "fy") is { Length: > 0 } fys ? float.Parse(fys, CultureInfo.InvariantCulture) : 0.5f;
                        float dx = ArgInt(a, "dx", -300), dy = ArgInt(a, "dy", -200);
                        int steps = Math.Max(1, ArgInt(a, "steps", 30)), stepMs = ArgInt(a, "stepMs", 16);
                        (bool ok, Point2 c, string why) = OnUi(() => Aim(key, ArgInt(a, "nth", 0), 0.5f, prefix: Arg(a, "prefix") is "1"));
                        if (!ok) return "not found: " + why;
                        RectF r = OnUi(() => AimRect(key, Arg(a, "prefix") is "1"));
                        Playback.Video.Timeline.Reset("drag " + key);
                        return DragFrom(new Point2(r.X + r.W * fx, r.Y + r.H * fy), dx, dy, steps, stepMs, key, ArgInt(a, "holdMs", 0));
                    }
                case "keys":
                    {
                        string contains = Arg(a, "contains");
                        string text = OnUi(() =>
                        {
                            var all = new List<(NodeHandle Node, string Key)>();
                            SceneStore scene = s_vpHost!.Scene;
                            scene.CopyDebugKeys(all);
                            var sb = new System.Text.StringBuilder();
                            foreach (var (node, k) in all)
                            {
                                if (contains.Length > 0 && k.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                RectF r = scene.PresentedAbsoluteRect(node);
                                sb.Append(k).Append('@').Append(F(r.X)).Append(',').Append(F(r.Y)).Append(' ').Append(F(r.W)).Append('x').Append(F(r.H))
                                  .Append(scene.IsShown(node) ? "" : "(hidden)").Append("; ");
                            }
                            return sb.ToString();
                        });
                        VEvent("keys", ("contains", contains), ("list", text));
                        return "listed";
                    }
                case "shot":
                    {
                        string path = Arg(a, "path");
                        OnUi(() => { s_vpHost!.RequestFrameCapture(); return true; });
                        long until = Environment.TickCount64 + 5_000;
                        while (Environment.TickCount64 < until)
                        {
                            FrameCaptureResult? cap = OnUi(() => s_vpHost!.TryTakeFrameCapture(out FrameCaptureResult? c) ? c : null);
                            if (cap?.Bgra is { } bgra)
                            {
                                PngWriter.WriteBgra(path, bgra, cap.WidthPx, cap.HeightPx, keepAlpha: false);
                                return "shot " + path;
                            }
                            Thread.Sleep(30);
                        }
                        return "no frame";
                    }
                case "monitorof":
                    {
                        string text = OnUi(MonitorReport);
                        VEvent("monitorof", ("windows", text));
                        return text;
                    }
                case "movewin":
                    return OnUi(() => MoveWindowTo(Arg(a, "which", "main"), ArgInt(a, "monitor", 1), Arg(a, "max") is "1"));
                case "screen":
                    return OnUi(() => ScreenShot(ArgInt(a, "monitor", 1), Arg(a, "path")));
                case "opts":
                    {
                        string text = OnUi(OptionsReport);
                        VEvent("opts", ("state", text));
                        return text;
                    }
                case "speed":
                    {
                        float rate = float.Parse(Arg(a, "value", "1"), CultureInfo.InvariantCulture);
                        OnUi(() => { Playback.SetEpisodeSpeed(rate); return true; });
                        return "speed " + rate.ToString("0.##", CultureInfo.InvariantCulture) + " -> " + OnUi(OptionsReport);
                    }
                case "vrate":
                    {
                        float rate = float.Parse(Arg(a, "value", "1"), CultureInfo.InvariantCulture);
                        OnUi(() => { Playback.Video.SetRate(rate); return true; });
                        return "video rate " + rate.ToString("0.##", CultureInfo.InvariantCulture);
                    }
                case "aspect":
                    {
                        Wavee.Video.AspectPreference mode = Arg(a, "mode", "fit") switch
                        {
                            "crop" => Wavee.Video.AspectPreference.Crop, "stretch" => Wavee.Video.AspectPreference.Stretch,
                            "native" => Wavee.Video.AspectPreference.Native, "custom" => Wavee.Video.AspectPreference.Custom,
                            _ => Wavee.Video.AspectPreference.Fit,
                        };
                        double ratio = double.Parse(Arg(a, "ratio", "0"), CultureInfo.InvariantCulture);
                        OnUi(() => { Wavee.Video.Prefs.SetAspect(Platform.Settings, mode, ratio); return true; });
                        return "aspect " + mode + " -> " + OnUi(OptionsReport);
                    }
                case "quality":
                    {
                        int h = ArgInt(a, "h", 0);
                        OnUi(() => { Playback.Video.PinQuality(h); return true; });
                        return "quality " + h;
                    }
                case "ontop":
                    {
                        bool on = Arg(a, "value", "1") is "1" or "true";
                        OnUi(() => { Wavee.Video.Prefs.SetAlwaysOnTop(Platform.Settings, on); return true; });
                        return "ontop " + on;
                    }
                case "turnoff":
                    OnUi(() => { Wavee.Video.State.TurnOff(); return true; });
                    return "turned off -> placement=" + OnUi(static () => Wavee.Video.State.Resolved.ToString());
                case "mark":
                    Playback.Video.Timeline.Reset(Arg(a, "name"));
                    return Arg(a, "name");
                case "quit":
                    s_vpQuit = true;
                    OnUi(() => { VpPost(s_vpWindow!.Handle.Value, WM_CLOSE, 0, 0); return true; });
                    return "bye";
                default:
                    return "unknown verb";
            }
        }

        /// <summary>Does the owner match <paramref name="want"/>? Safe = us, nobody, or the allowed scratch device.</summary>
        static bool OwnerMatches(string want) => OnUi(() =>
        {
            Playback.State st = Playback.Snap();
            bool allowedForeign = st.Own.Kind == Playback.Owner.Foreign && s_allowedDevice != 0 && st.Own.Device == s_allowedDevice;
            return want switch
            {
                "Us" => st.Own.Kind == Playback.Owner.Us,
                "Nobody" => st.Own.Kind == Playback.Owner.Nobody,
                "Foreign" => allowedForeign,
                _ => st.Own.Kind != Playback.Owner.Foreign || allowedForeign,
            };
        });

        /// <summary>The guard every verb that could reach another device runs first: abort the run when a device that is not one
        /// of ours owns playback (a play would be forwarded to the owner's phone).</summary>
        static bool SafeToDrive(out string why)
        {
            if (OwnerMatches("Safe")) { why = ""; return true; }
            string line = OnUi(OwnerLine);
            VEvent("abort", ("why", "a foreign device owns playback"), ("owner", line));
            s_vpAbort = true;
            why = "ABORT foreign owner: " + line;
            return false;
        }

        /// <summary>UI thread: the owner, the active device and the deck's video facts, one line.</summary>
        static string OwnerLine()
        {
            Playback.State st = Playback.Snap();
            var f = Playback.DeckVideo.Peek();
            ulong device = st.Own.Kind == Playback.Owner.Us ? st.Us : st.Own.Device;
            string name = Playback.Devices.NameOf(device);
            string mine = st.Own.Kind == Playback.Owner.Foreign && device == s_allowedDevice && device != 0 ? " (allowed)" : "";
            return "owner=" + st.Own.Kind + " device=" + (name.Length > 0 ? name : "-") + mine
                + " id=" + Platform.Redact(Playback.Devices.IdOf(device)) + " track=" + (st.CurrentId.IsEmpty ? "-" : st.CurrentId.Text)
                + " kind=" + st.Kind + " phase=" + st.Phase + " wanted=" + st.VideoWanted
                + " facts{has=" + f.HasVideo + " known=" + f.Known + " on=" + f.OnVideo + " toVideo=" + f.CanSwitchToVideo
                + " toAudio=" + f.CanSwitchToAudio + " remote=" + f.Remote
                + " manifest=" + (f.ManifestId.IsEmpty ? "-" : Entities.Strings.Resolve(f.ManifestId)) + "}"
                + " placement=" + Wavee.Video.PlacementCore.Resolve(Wavee.Video.State.Surface.Peek())
                + " join=" + Wavee.Video.JoinNow.Peek();
        }

        /// <summary>UI thread: the presented centre of the Nth shown node keyed <paramref name="key"/> (or whose key starts with it).</summary>
        static (bool, Point2, string) Aim(string key, int nth, float fx, bool prefix)
        {
            SceneStore scene = s_vpHost!.Scene;
            var all = new List<(NodeHandle Node, string Key)>();
            scene.CopyDebugKeys(all);
            var hits = new List<(NodeHandle Node, RectF R)>();
            foreach (var (node, k) in all)
            {
                bool match = prefix ? k.StartsWith(key, StringComparison.Ordinal) : string.Equals(k, key, StringComparison.Ordinal);
                if (!match || !scene.IsShown(node)) continue;
                RectF r = scene.PresentedAbsoluteRect(node);
                if (r.W < 1f || r.H < 1f) continue;
                hits.Add((node, r));
            }
            hits.Sort(static (x, y) => Actionability.ReadingOrder(x.R, y.R));
            if (nth >= hits.Count) return (false, default, hits.Count + " shown nodes keyed " + key);
            RectF hit = hits[nth].R;
            return (true, new Point2(hit.X + hit.W * fx, hit.Y + hit.H * 0.5f), "");
        }

        /// <summary>UI thread: the presented rect of the first shown node keyed <paramref name="key"/> in reading order.</summary>
        static RectF AimRect(string key, bool prefix)
        {
            SceneStore scene = s_vpHost!.Scene;
            var all = new List<(NodeHandle Node, string Key)>();
            scene.CopyDebugKeys(all);
            var hits = new List<RectF>();
            foreach (var (node, k) in all)
            {
                bool match = prefix ? k.StartsWith(key, StringComparison.Ordinal) : string.Equals(k, key, StringComparison.Ordinal);
                if (!match || !scene.IsShown(node)) continue;
                RectF r = scene.PresentedAbsoluteRect(node);
                if (r.W >= 1f && r.H >= 1f) hits.Add(r);
            }
            hits.Sort(static (x, y) => Actionability.ReadingOrder(x, y));
            return hits.Count > 0 ? hits[0] : default;
        }

        // ── health, drag, soak ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Is playback alive? Video: the session's rendered-frame counter (published a couple of times a second) must move
        /// while the video is on; its position is the fallback (it updates in bursts). Audio: Playback.PositionMs must move while
        /// IsPlaying. A suspect sample is re-checked once more (a burst gap is not a hang) before it is called STUCK.</summary>
        static string Health(int ms, string tag, out bool bad)
        {
            string line = HealthOnce(ms, out bad, out string detail);
            if (bad)
            {
                string again = HealthOnce(ms, out bad, out string detail2);
                line = bad ? "STUCK " + again : "recovered " + again;
                detail += " | " + detail2;
            }
            VEvent(tag, ("verdict", line), ("detail", detail));
            return line;
        }

        static string HealthOnce(int ms, out bool bad, out string detail)
        {
            var v0 = Playback.Video.ProbeSnapshot();
            var h0 = OnUi(static () => (Playback.Video.Phase.Peek(), Playback.PositionMs.Peek(), Playback.IsPlaying.Peek(), Playback.Buffering.Peek(), Playback.OwnerSignal.Peek()));
            Thread.Sleep(ms);
            var v1 = Playback.Video.ProbeSnapshot();
            var h1 = OnUi(static () => (Playback.Video.Phase.Peek(), Playback.PositionMs.Peek(), Playback.IsPlaying.Peek(), Playback.Buffering.Peek(), Playback.OwnerSignal.Peek()));
            var phase = h1.Item1;
            bool videoOn = phase != Playback.Video.SwitchPhase.Idle;
            long dr = v1.Rendered - v0.Rendered;
            double dp = v1.PositionS - v0.PositionS;
            int da = h1.Item2 - h0.Item2;
            string video = !videoOn ? "off"
                : v1.State == "(no player)" ? "NOPLAYER"
                : phase == Playback.Video.SwitchPhase.Failed ? "FAILED"
                : !h1.Item3 ? "paused"
                : dr > 0 ? "ok"
                : dp > ms / 2000.0 ? "ok(pos)"
                : "NOFRAMES";
            string audio = !h1.Item3 ? "paused" : h1.Item4 ? "buffering" : da > ms / 4 ? "ok" : "NOADVANCE";
            string owner = h1.Item5.ToString();
            bool foreign = h1.Item5 == Playback.Owner.Foreign;
            bad = !foreign && (video is "NOPLAYER" or "FAILED" or "NOFRAMES" || audio == "NOADVANCE" || (h0.Item4 && h1.Item4));
            detail = "phase=" + phase + " vstate=" + v1.State + " dRendered=" + dr + " dDropped=" + (v1.Dropped - v0.Dropped)
                + " dVideoPos=" + dp.ToString("0.00", CultureInfo.InvariantCulture) + " dAudioMs=" + da + " playing=" + h1.Item3
                + " buffering=" + h1.Item4 + " owner=" + owner;
            return "video=" + video + " audio=" + audio + " owner=" + owner + " (" + detail + ")";
        }

        /// <summary>Press at <paramref name="start"/>, move by (dx, dy) over <paramref name="steps"/> moves, hold, release. The frame
        /// and stall meters run throughout, so the drag's own cost shows as frame.gap / ui.stall events between input.drag and drag.end.</summary>
        static string DragFrom(Point2 start, float dx, float dy, int steps, int stepMs, string what, int holdMs)
        {
            Win32Window w = s_vpWindow!;
            void Q(InputKind k, Point2 pt) => OnUi(() => { w.QueueInput(new InputEvent(k, pt, 0, 0, TimestampMs: (uint)Environment.TickCount64)); return true; });
            VEvent("input.drag", ("what", what), ("x", F(start.X)), ("y", F(start.Y)), ("dx", F(dx)), ("dy", F(dy)));
            Q(InputKind.PointerMove, start); Thread.Sleep(300);   // hover reveals a strip / thumb
            Q(InputKind.PointerDown, start); Thread.Sleep(30);
            long t0 = Environment.TickCount64;
            for (int i = 1; i <= steps; i++) { Q(InputKind.PointerMove, new Point2(start.X + dx * i / steps, start.Y + dy * i / steps)); Thread.Sleep(stepMs); }
            long moveMs = Environment.TickCount64 - t0;
            if (holdMs > 0) Thread.Sleep(holdMs);
            Q(InputKind.PointerUp, new Point2(start.X + dx, start.Y + dy));
            VEvent("drag.end", ("what", what), ("moveMs", moveMs.ToString(CultureInfo.InvariantCulture)));
            return "dragged " + what + " from " + F(start.X) + "," + F(start.Y) + " moveMs=" + moveMs + " (planned " + steps * stepMs + ")";
        }

        /// <summary>Run one script line as a nested step (logged like a top-level step).</summary>
        static string Do(string line)
        {
            var (verb, args) = ParseLine(line);
            long t0 = Log.SinceStartMs;
            VEvent("step", ("n", "soak"), ("verb", verb), ("line", line));
            string result;
            try { result = RunStep(verb, args); }
            catch (Exception ex) { result = "threw " + ex.GetType().Name + ": " + ex.Message; }
            VEvent("step.done", ("verb", verb), ("ms", (Log.SinceStartMs - t0).ToString(CultureInfo.InvariantCulture)), ("result", result));
            return result;
        }

        /// <summary>Random user-shaped actions for <paramref name="minutes"/>, biased to keep the video on, with a health check after
        /// each action; a STUCK verdict is logged with a screenshot, then watched for 10 s to see whether it heals on its own.</summary>
        static string Soak(int minutes, int seed, string[] uris)
        {
            var rng = new Random(seed);
            long until = Environment.TickCount64 + minutes * 60_000L;
            int actions = 0, stuck = 0, takeovers = 0;
            string[] places = ["docked", "floating", "detached", "fullscreen", "docked"];
            string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            while (Environment.TickCount64 < until && !s_vpQuit)
            {
                var (phase, owner, hasVideo) = OnUi(static () => (Playback.Video.Phase.Peek(), Playback.OwnerSignal.Peek(), Shell.NowPlayingFace.HasVideoPeek()));
                bool videoOn = phase != Playback.Video.SwitchPhase.Idle;
                if (owner == Playback.Owner.Foreign)
                {
                    // Another device (the owner's own Wavee or phone) took playback: stop rather than fight a person for it.
                    takeovers++;
                    VEvent("soak.takeover", ("n", takeovers.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                int roll = rng.Next(100);
                string label;
                if (!videoOn && hasVideo && roll < 45) { label = "video-on"; Do("click key=video fx=0.3 frame=1"); }
                else if (roll < 8 && uris.Length > 0) { label = "play"; Do("play uri=" + uris[rng.Next(uris.Length)]); }
                else if (roll < 16) { label = "next"; Do("click key=next"); }
                else if (roll < 20) { label = "prev"; Do("click key=prev"); }
                else if (roll < 34) { label = "seek-click"; Do("click key=seek fx=" + F2(0.05 + rng.NextDouble() * 0.9)); }
                else if (roll < 44) { label = "seek-drag"; Do("seekdrag from=" + F2(0.1 + rng.NextDouble() * 0.8) + " to=" + F2(0.1 + rng.NextDouble() * 0.8) + " holdMs=" + rng.Next(0, 800)); }
                else if (roll < 50) { label = "seek-burst"; for (int k = 0; k < 4; k++) { Do("click key=seek fx=" + F2(0.1 + rng.NextDouble() * 0.8)); Thread.Sleep(rng.Next(60, 250)); } }
                else if (roll < 58) { label = "pause-resume"; Do("click key=primary"); Thread.Sleep(rng.Next(300, 2500)); Do("click key=primary"); }
                else if (roll < 66 && videoOn) { label = "video-off"; Do("click key=video fx=0.3"); }
                else if (roll < 72 && videoOn) { label = "flap"; Do("click key=video fx=0.3"); Thread.Sleep(rng.Next(100, 900)); Do("click key=video fx=0.3 frame=1"); }
                else if (roll < 84 && videoOn) { label = "place"; Do("place to=" + places[rng.Next(places.Length)]); }
                else if (roll < 90 && hasVideo) { label = "on-then-seek"; if (!videoOn) Do("click key=video fx=0.3 frame=1"); Thread.Sleep(rng.Next(100, 700)); Do("click key=seek fx=" + F2(0.1 + rng.NextDouble() * 0.8)); }
                else if (roll < 95 && hasVideo) { label = "on-then-next"; if (!videoOn) Do("click key=video fx=0.3 frame=1"); Thread.Sleep(rng.Next(100, 700)); Do("click key=next"); }
                else { label = "idle"; }
                actions++;
                VEvent("soak.action", ("n", actions.ToString(CultureInfo.InvariantCulture)), ("what", label), ("videoWasOn", videoOn ? "1" : "0"));
                Thread.Sleep(rng.Next(200, 2500));
                Health(3000, "soak.health", out bool bad);
                if (bad)
                {
                    stuck++;
                    VEvent("soak.STUCK", ("n", stuck.ToString(CultureInfo.InvariantCulture)), ("after", label));
                    Do("shot path=C:/WAVEE/scratch/vdiag/stuck-" + stuck.ToString(CultureInfo.InvariantCulture) + ".png");
                    Thread.Sleep(10_000);
                    Health(3000, "soak.after10s", out bool still);
                    if (still) { VEvent("soak.STUCK.persistent", ("n", stuck.ToString(CultureInfo.InvariantCulture))); Do("place to=off"); Thread.Sleep(2000); }
                }
            }
            return "soak actions=" + actions + " stuck=" + stuck + " takeovers=" + takeovers;
        }

        // ── real input ────────────────────────────────────────────────────────────────────────────────────────────

        const uint WM_CLOSE = 0x0010;

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool VpPost(nint hwnd, uint msg, nint wParam, nint lParam);



        /// <summary>A click at window DIP <paramref name="p"/> through the window's own input queue: the queue the WM_POINTER handler
        /// feeds (the engine runs EnableMouseInPointer, so posted WM_LBUTTON* messages are never read and a posted WM_POINTER*
        /// has no pointer info behind it). Hit-testing, capture, press and click dispatch run exactly as for the mouse.</summary>
        static void PostClick(Point2 p)
        {
            Win32Window w = s_vpWindow!;
            VEvent("input.click", ("x", F(p.X)), ("y", F(p.Y)));
            OnUi(() => { w.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: (uint)Environment.TickCount64)); return true; });
            Thread.Sleep(40);
            OnUi(() => { w.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: (uint)Environment.TickCount64)); return true; });
            Thread.Sleep(60);
            OnUi(() => { w.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: (uint)Environment.TickCount64)); return true; });
        }

        static void PostKey(int vk)
        {
            Win32Window w = s_vpWindow!;
            VEvent("input.key", ("vk", vk.ToString(CultureInfo.InvariantCulture)));
            OnUi(() => { w.QueueInput(new InputEvent(InputKind.Key, default, 0, vk, TimestampMs: (uint)Environment.TickCount64)); return true; });
            Thread.Sleep(40);
            OnUi(() => { w.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, vk, TimestampMs: (uint)Environment.TickCount64)); return true; });
        }

        // ── monitors (monitorof, movewin) ─────────────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        struct VpRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        unsafe struct VpMonitorInfoEx
        {
            public uint Size;
            public VpRect Monitor, Work;
            public uint Flags;
            public fixed char Device[32];
        }

        [LibraryImport("user32.dll")]
        private static unsafe partial int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, VpRect*, nint, int> proc, nint data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        private static unsafe partial int VpGetMonitorInfo(nint monitor, VpMonitorInfoEx* info);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromWindow(nint hwnd, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static unsafe partial int VpGetWindowRect(nint hwnd, VpRect* rect);

        [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static partial uint VpGetDpiForWindow(nint hwnd);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW")]
        private static partial nint VpFindWindowEx(nint parent, nint after, nint className, nint windowName);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static unsafe partial uint VpGetWindowThreadProcessId(nint hwnd, uint* pid);

        [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
        private static partial int VpIsWindowVisible(nint hwnd);

        [LibraryImport("user32.dll", EntryPoint = "IsZoomed")]
        private static partial int VpIsZoomed(nint hwnd);

        [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
        private static partial int VpShowWindow(nint hwnd, int cmd);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
        private static partial int VpSetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

        static List<nint>? s_monitorScratch;

        [UnmanagedCallersOnly]
        static unsafe int CollectMonitor(nint monitor, nint hdc, VpRect* rect, nint data)
        {
            s_monitorScratch?.Add(monitor);
            return 1;
        }

        /// <summary>UI thread: every monitor in EnumDisplayMonitors order.</summary>
        static unsafe List<nint> Monitors()
        {
            var list = new List<nint>();
            s_monitorScratch = list;
            EnumDisplayMonitors(0, 0, &CollectMonitor, 0);
            s_monitorScratch = null;
            return list;
        }

        static unsafe (VpRect Monitor, VpRect Work, string Device) MonitorInfo(nint monitor)
        {
            VpMonitorInfoEx mi = default;
            mi.Size = (uint)sizeof(VpMonitorInfoEx);
            if (VpGetMonitorInfo(monitor, &mi) == 0) return default;
            return (mi.Monitor, mi.Work, new string(mi.Device));
        }

        static string R(VpRect r) => r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);

        /// <summary>UI thread: this process's visible top-level windows, the main window first, the rest largest first.</summary>
        static unsafe List<nint> OurWindows()
        {
            nint main = s_vpWindow!.Handle.Value;
            uint me = (uint)Environment.ProcessId;
            var others = new List<(nint H, long Area)>();
            for (nint h = VpFindWindowEx(0, 0, 0, 0); h != 0; h = VpFindWindowEx(0, h, 0, 0))
            {
                uint pid = 0;
                VpGetWindowThreadProcessId(h, &pid);
                if (pid != me || h == main || VpIsWindowVisible(h) == 0) continue;
                VpRect r;
                if (VpGetWindowRect(h, &r) == 0) continue;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area < 100 * 100) continue;   // tooltips, IME and shell helper windows
                others.Add((h, area));
            }
            others.Sort(static (x, y) => y.Area.CompareTo(x.Area));
            var list = new List<nint>(others.Count + 1) { main };
            foreach (var (h, _) in others) list.Add(h);
            return list;
        }

        /// <summary>UI thread: the `opts` line: what each row of the video menu would show, read from the live objects.</summary>
        static string OptionsReport()
        {
            Playback.State st = Playback.Snap();
            var player = Playback.Video.Player.Peek().Player;
            var active = player?.Qualities.Active.Peek();
            var heights = new System.Text.StringBuilder();
            if (player is not null) foreach (var v in player.Qualities.Variants) heights.Append(v.Resolution.Height).Append(',');
            return "id=" + st.CurrentId.Text + " kind=" + st.Kind + " episodeSpeed=" + Playback.EpisodeSpeed.Peek().ToString("0.##", CultureInfo.InvariantCulture)
                + " contentRate=" + st.ContentRate.ToString("0.##", CultureInfo.InvariantCulture)
                + " playerRate=" + (player is null ? "-" : player.Rate.Peek().ToString("0.##", CultureInfo.InvariantCulture))
                + " vclock=" + Playback.Video.ClockPositionMs()
                + " qualityPin=" + Playback.Video.LiveQualityPin() + " storedPin=" + Platform.Settings.Get(Platform.Keys.VideoQuality)
                + " active=" + (active is null ? "-" : active.Resolution.Height + "p@" + active.Bitrate) + " rungs=" + heights
                + " aspect=" + Wavee.Video.Prefs.Aspect(Platform.Settings) + "/" + Wavee.Video.Prefs.CustomRatio(Platform.Settings).ToString("0.###", CultureInfo.InvariantCulture)
                + " onTop=" + Wavee.Video.Prefs.AlwaysOnTop(Platform.Settings) + " placement=" + Wavee.Video.State.Resolved;
        }

        /// <summary>UI thread: the `monitorof` line: the monitors, then each of our windows and the monitor it is on.</summary>
        static unsafe string MonitorReport()
        {
            var sb = new System.Text.StringBuilder();
            var mons = Monitors();
            for (int i = 0; i < mons.Count; i++)
            {
                var (m, w, dev) = MonitorInfo(mons[i]);
                sb.Append("monitor").Append(i + 1).Append('=').Append(dev).Append(" rect=").Append(R(m)).Append(" work=").Append(R(w)).Append("; ");
            }
            var wins = OurWindows();
            for (int i = 0; i < wins.Count; i++)
            {
                nint h = wins[i];
                VpRect r;
                VpGetWindowRect(h, &r);
                nint mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
                var (m, _, dev) = MonitorInfo(mon);
                bool fs = r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom;
                sb.Append(i == 0 ? "main" : "window" + i).Append(" hwnd=").Append(h.ToString("X", CultureInfo.InvariantCulture))
                  .Append(" rect=").Append(R(r)).Append(" on=monitor").Append(mons.IndexOf(mon) + 1).Append('(').Append(dev).Append(')')
                  .Append(" monRect=").Append(R(m)).Append(" dpi=").Append(VpGetDpiForWindow(h)).Append(" zoomed=").Append(VpIsZoomed(h))
                  .Append(" fs=").Append(fs ? 1 : 0)
                  .Append(" topmost=").Append(((long)VpGetWindowLongPtr(h, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0 ? 1 : 0).Append("; ");
            }
            sb.Append("popOutFs=").Append(Wavee.Video.State.DetachedFullscreen.Peek() ? 1 : 0)
              .Append(" placement=").Append(Wavee.Video.State.Resolved);
            return sb.ToString();
        }

        /// <summary>UI thread: restore <paramref name="which"/> (the main window, or the largest other window: the pop-out) and
        /// centre it in monitor <paramref name="monitor"/>'s work area at 60 % of its size; the main window moves through the
        /// engine's own <see cref="Win32Window.SetBoundsPx"/>.</summary>
        static string MoveWindowTo(string which, int monitor, bool maximize)
        {
            var mons = Monitors();
            if (monitor < 1 || monitor > mons.Count) return "no monitor " + monitor + " (have " + mons.Count + ")";
            var wins = OurWindows();
            nint h = which == "main" ? wins[0] : wins.Count > 1 ? wins[1] : 0;
            if (h == 0) return "no " + which + " window";
            if (VpIsZoomed(h) != 0) VpShowWindow(h, 9 /* SW_RESTORE */);
            var (_, work, dev) = MonitorInfo(mons[monitor - 1]);
            int ww = work.Right - work.Left, wh = work.Bottom - work.Top;
            int w = ww * 6 / 10, ht = wh * 6 / 10;
            int x = work.Left + (ww - w) / 2, y = work.Top + (wh - ht) / 2;
            if (which == "main")
            {
                s_vpWindow!.SetBoundsPx(new RectF(x, y, w, ht));
                // Raise it above the other apps' windows without activating it (a probe process is not the foreground one, so
                // SetForegroundWindow is refused): a window left fully occluded parks its frame loop, and a fullscreen asked
                // for then is applied only once it presents again. The pop-out is topmost by preference already.
                const uint keep = 0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x0010 /* SWP_NOACTIVATE */;
                VpSetWindowPos(h, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, keep);
                VpSetWindowPos(h, -2 /* HWND_NOTOPMOST */, 0, 0, 0, 0, keep);
            }
            else VpSetWindowPos(h, 0, x, y, w, ht, 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */);
            if (maximize) VpShowWindow(h, 3 /* SW_MAXIMIZE */);
            return "moved " + which + " to monitor" + monitor + "(" + dev + ") at " + x + "," + y + " " + w + "x" + ht + (maximize ? " maximized" : "");
        }

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint VpGetWindowLongPtr(nint hwnd, int index);
        [LibraryImport("user32.dll", EntryPoint = "GetDC")] private static partial nint VpGetDC(nint hwnd);
        [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")] private static partial int VpReleaseDC(nint hwnd, nint dc);
        [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static partial nint VpCreateCompatibleDC(nint dc);
        [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")] private static partial nint VpCreateCompatibleBitmap(nint dc, int w, int h);
        [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")] private static partial nint VpSelectObject(nint dc, nint obj);
        [LibraryImport("gdi32.dll", EntryPoint = "BitBlt")] private static partial int VpBitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
        [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")] private static unsafe partial int VpGetDIBits(nint dc, nint bmp, uint start, uint lines, void* bits, void* info, uint usage);
        [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")] private static partial int VpDeleteObject(nint obj);
        [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")] private static partial int VpDeleteDC(nint dc);

        /// <summary>UI thread: monitor <paramref name="monitor"/>'s pixels from the screen DC (the process is per-monitor DPI aware,
        /// so the DC is in physical px) to a PNG — the composited desktop, both of this app's windows included.</summary>
        static unsafe string ScreenShot(int monitor, string path)
        {
            var mons = Monitors();
            if (monitor < 1 || monitor > mons.Count) return "no monitor " + monitor;
            var (m, _, dev) = MonitorInfo(mons[monitor - 1]);
            int w = m.Right - m.Left, h = m.Bottom - m.Top;
            nint screen = VpGetDC(0), mem = VpCreateCompatibleDC(screen), bmp = VpCreateCompatibleBitmap(screen, w, h);
            nint old = VpSelectObject(mem, bmp);
            VpBitBlt(mem, 0, 0, w, h, screen, m.Left, m.Top, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
            VpSelectObject(mem, old);
            byte[] bgra = new byte[w * h * 4];
            int* bih = stackalloc int[10];   // BITMAPINFOHEADER (40 bytes)
            new Span<int>(bih, 10).Clear();
            bih[0] = 40; bih[1] = w; bih[2] = -h;   // top-down
            ((short*)bih)[6] = 1; ((short*)bih)[7] = 32;   // planes, bit count (BI_RGB)
            int lines;
            fixed (byte* p = bgra) lines = VpGetDIBits(mem, bmp, 0, (uint)h, p, bih, 0);
            VpDeleteObject(bmp); VpDeleteDC(mem); VpReleaseDC(0, screen);
            if (lines != h) return "capture failed lines=" + lines;
            PngWriter.WriteBgra(path, bgra, w, h, keepAlpha: false);
            return "screen monitor" + monitor + "(" + dev + ") " + w + "x" + h + " -> " + path;
        }

        // ── plumbing ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Run <paramref name="f"/> on the UI thread and wait for its answer (the driver thread only).</summary>
        static T OnUi<T>(Func<T> f)
        {
            T result = default!;
            Exception? error = null;
            using var done = new ManualResetEventSlim(false);
            Shell.PostUiForProbe(() =>
            {
                try { result = f(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error is not null) throw new InvalidOperationException("probe step failed on the UI thread", error);
            return result;
        }

        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>One `[vprobe]` event on the log's clock.</summary>
        internal static void VEvent(string name, params (string Name, string Value)[] fields)
        {
            var f = new WaveeLogField[fields.Length + 1];
            f[0] = WaveeLogField.Of("t", Log.SinceStartMs);
            for (int i = 0; i < fields.Length; i++) f[i + 1] = WaveeLogField.Of(fields[i].Name, fields[i].Value);
            Log.Event(WaveeLogLevel.Info, "vprobe", name, "", null, Log.SinceStartMs, null, f);
            if (s_echo) { try { Console.Error.WriteLine("[vprobe] " + name + " " + string.Join(' ', fields.Select(x => x.Name + "=" + x.Value))); } catch (IOException) { } }
        }
    }
}
