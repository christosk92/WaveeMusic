// ── Screens/Diagnostics.MixBench.cs ───────────────────────────────────────────────────────────────────────────────────
// `--mix-bench`: two local files through the REAL pump and the REAL default output device, windowless, no sign-in: the
// on-box way to hear, and record with a loopback capture, one boundary: a #48 Mix join, the user's Crossfade, a gapless
// join, or a seek. Built on the --stress-audio host's composition (one writer thread, the marshallers, overlay settings).
//
//   Wavee.exe --mix-bench --file A.flac --file2 B.flac --scenario mix --from 150000 --seconds 30 --preset 3
//             [--out <ms> --overlap <ms> --in <ms> --in-dur <ms>] [--crossfade-ms 12000] [--seek-at <ms> --seek-to <ms>]
//             [--profile <dir>]
//
// The two files are imported as local tracks and queued [A, B]; A plays from `--from`. Both are handed to the pump as AUDIO
// (the reducer's local-file hard cut is #240's to lift, and the planner never mixes a local file, which is Spotify's own
// rule, so `mix` hands the timing to the planner's bench seam: what is measured is the pump and the engine). Every second
// prints a JSON line (position, playing row, hand-off counters); the run ends after `--seconds`. Exit 0 ok, 1 fault,
// 64 usage. Nothing here runs in a normal launch.

using System.Globalization;

namespace Wavee;

public static partial class Diagnostics
{
    public static class MixBenchHost
    {
        const int TickMs = 100;
        static HeadlessLoop s_loop = null!;
        static Timer? s_tick;
        static int s_exit, s_tickQueued;
        static bool s_started, s_exiting, s_seeked;
        static long s_t0 = -1, s_lastPrint;
        static readonly Action s_tickAction = Tick;
        static Options s_o;
        static Track s_a, s_b;

        public readonly record struct Options(string A, string B, string Scenario, int FromMs, int Seconds, int CrossfadeMs,
            byte Preset, int OutMs, int OverlapMs, int InMs, int InDurMs, int SeekAtMs, int SeekToMs);

        public static bool TryRun(string[] args, out int code)
        {
            code = 0;
            if (Array.IndexOf(args, "--mix-bench") < 0) return false;
            var utf8 = new System.Text.UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            if (!TryParse(args, out s_o, out string why)) { Console.Error.WriteLine("mix-bench: " + why); code = 64; return true; }
            code = Run();
            return true;
        }

        static bool TryParse(string[] args, out Options o, out string why)
        {
            string a = "", b = "", scenario = "mix";
            int from = 0, seconds = 30, cf = 12_000, outMs = -1, overlap = 4_000, inMs = 0, inDur = -1, seekAt = -1, seekTo = -1;
            byte preset = 1;
            why = "";
            for (int i = 0; i < args.Length; i++)
            {
                string k = args[i];
                string v = i + 1 < args.Length ? args[i + 1] : "";
                bool n = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x);
                switch (k)
                {
                    case "--file": a = v; i++; break;
                    case "--file2": b = v; i++; break;
                    case "--scenario": scenario = v; i++; break;
                    case "--from" when n: from = x; i++; break;
                    case "--seconds" when n: seconds = x; i++; break;
                    case "--crossfade-ms" when n: cf = x; i++; break;
                    case "--preset" when n: preset = (byte)x; i++; break;
                    case "--out" when n: outMs = x; i++; break;
                    case "--overlap" when n: overlap = x; i++; break;
                    case "--in" when n: inMs = x; i++; break;
                    case "--in-dur" when n: inDur = x; i++; break;
                    case "--seek-at" when n: seekAt = x; i++; break;
                    case "--seek-to" when n: seekTo = x; i++; break;
                }
            }
            o = new Options(a, b, scenario, from, seconds, cf, preset, outMs, overlap, inMs, inDur < 0 ? overlap : inDur, seekAt, seekTo);
            if (!File.Exists(a) || !File.Exists(b)) { why = "--file and --file2 must name existing files"; return false; }
            if (scenario is not ("mix" or "crossfade" or "gapless" or "seek")) { why = "--scenario mix|crossfade|gapless|seek"; return false; }
            return true;
        }

        static int Run()
        {
            s_loop = new HeadlessLoop();
            Platform.UseSettings(new Headless.OverlaySettings(Platform.BackingSettings));
            Playback.ToUi = s_loop.Post;
            Spotify.Post = s_loop.Post;
            Store.Post = s_loop.Post;
            Palette.Post = s_loop.Post;
            Playback.FrameNowMs = static () => s_loop.NowMs;
            App.RegisterShapes();
            Store.Use(null);
            Entities.Boot(Platform.Scope);
            Spotify.Boot();
            Playback.Boot();
            Playback.Audio.LocalPath = Playlist.LocalPathOf;
            s_tick = new Timer(static _ => { if (Interlocked.CompareExchange(ref s_tickQueued, 1, 0) == 0) s_loop.Post(s_tickAction); }, null, TickMs, TickMs);
            try { s_loop.Run(); }
            catch (Exception ex) { Console.Error.WriteLine("mix-bench: host faulted: " + ex.Message); s_exit = 1; }
            finally { s_tick?.Dispose(); }
            return s_exit;
        }

        static void Tick()
        {
            Volatile.Write(ref s_tickQueued, 0);
            if (s_exiting) return;
            long now = s_loop.NowMs;
            if (s_t0 < 0) s_t0 = now;
            if (!s_started)
            {
                if (now - s_t0 < 1000) return;                  // the pump builds its device on the first load, as --stress-audio does
                s_started = true;
                Start();
                return;
            }
            if (s_o.SeekAtMs >= 0 && !s_seeked && now - s_t0 >= s_o.SeekAtMs) { s_seeked = true; Playback.SeekTo(s_o.SeekToMs); Print(now, "seek"); }
            if (now - s_lastPrint >= 1000) { s_lastPrint = now; Print(now, "tick"); }
            if (now - s_t0 >= s_o.Seconds * 1000L + 500) Exit(0, "done");
        }

        static void Start()
        {
            s_a = Playlist.ImportLocalFile(Path.GetFullPath(s_o.A));
            s_b = Playlist.ImportLocalFile(Path.GetFullPath(s_o.B));
            if (!s_a.IsValid || !s_b.IsValid) { Exit(1, "import failed"); return; }
            bool fade = s_o.Scenario == "crossfade";
            Playback.Audio.SetCrossfade(fade, fade ? s_o.CrossfadeMs : 0);
            if (s_o.Scenario == "mix") ArmMix();
            Span<EntityRef> refs = [new EntityRef(EntityKind.Track, s_a.Slot), new EntityRef(EntityKind.Track, s_b.Slot)];
            Span<QueueEdge> rows = stackalloc QueueEdge[2];
            int n = Headless.BuildContextQueue(refs, 0, refs, rows);
            Queue.Replace(refs[..n], rows[..n]);
            Playback.PlayNow(refs[0], refs[0].Id, Queue.CursorOf(0), Playback.PlayableKind.Audio, s_o.FromMs);
            Console.Out.WriteLine("{\"kind\":\"start\",\"utc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"scenario\":\"" + s_o.Scenario
                + "\",\"aMs\":" + s_a.DurationMs + ",\"bMs\":" + s_b.DurationMs + ",\"from\":" + s_o.FromMs + "}");
        }

        /// <summary>The mix scenario's timing, handed to the planner's bench seam (Playback.Mix.Bench).</summary>
        static void ArmMix()
        {
            long durA = s_a.DurationMs;
            uint outMs = (uint)(s_o.OutMs >= 0 ? s_o.OutMs : Math.Max(0, durA - s_o.OverlapMs - 1));
            var t = new MixTiming(MixSource.Saved, MixWhyNot.None, s_o.Preset, outMs, (uint)s_o.OverlapMs, (uint)s_o.InMs, (uint)s_o.InDurMs);
            Playback.Mix.Bench(t);
            Console.Out.WriteLine("{\"kind\":\"mix\",\"preset\":" + t.Preset + ",\"out\":" + t.OutStartMs + ",\"outDur\":" + t.OutDurationMs
                + ",\"in\":" + t.InStartMs + ",\"inDur\":" + t.InDurationMs + "}");
        }

        static void Print(long now, string what)
        {
            var s = Playback.Snap();
            var m = Playback.Audio.Metrics.Read();
            string playing = s.CurrentId == s_a.Id ? "A" : s.CurrentId == s_b.Id ? "B" : "-";
            Console.Out.WriteLine("{\"kind\":\"" + what + "\",\"t\":" + (now - s_t0) + ",\"utc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"playing\":\"" + playing
                + "\",\"pos\":" + s.PosMs + ",\"dur\":" + s.DurationMs + ",\"crossfades\":" + m.Crossfades + ",\"gapless\":" + (m.GaplessExact + m.GaplessDegraded)
                + ",\"abandoned\":" + m.GaplessAbandoned + ",\"mixSaved\":" + Playback.Audio.MixSavedCount + ",\"mixRefused\":" + Playback.Audio.MixRefusedCount
                + ",\"mixCancelled\":" + Playback.Audio.MixCancelledCount + ",\"sounding\":" + (Playback.Audio.MixSounding ? 1 : 0) + "}");
        }

        static void Exit(int code, string why)
        {
            if (s_exiting) return;
            s_exiting = true;
            s_exit = code;
            Console.Out.WriteLine("{\"kind\":\"end\",\"code\":" + code + ",\"why\":\"" + why + "\",\"utc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
            s_tick?.Change(Timeout.Infinite, Timeout.Infinite);
            try { Playback.Stop(); } catch { }
            s_loop.Stop();
        }
    }
}
