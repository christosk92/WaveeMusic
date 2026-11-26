// ── Screens/Diagnostics.MixBench.Render.cs ────────────────────────────────────────────────────────────────────────────
// `--mix-bench --scenario render`: #48 M4's app half proved OFFLINE. Each plan (every preset 1-22 at the bar counts asked for, the
// noise, effect and looping overrides, beatmatched pairs) is armed on a headless PcmAudioSession exactly as the pump arms it
// (MixRender.Join + MixRender.Arm: B's envelope, automation and rate plan, A's envelope, automation and loop, one gate), pulled
// block by block through the engine's real mixer and voice chains, handed off when A is over (SetActiveVoice, the announce) and
// run on through B's glide back to 1, its rate read on a 200 ms tick of mixer time as the pump's TickMixSpeed reads it. Synthetic
// sources (a beat-locked kick, a bass, a mid and a high tone per deck), no device, no files read, no sign-in.
//
//   Wavee.exe --mix-bench --scenario render [--wav-out C:/WAVEE/scratch/mixm4] [--presets 1-22] [--bars 2,8]
//
// One line per plan: the lanes rendered and skipped, what each side's automation holds, when A left the mixer (an effect rings
// out past its window), the peak, NaN, the loop's boundaries, the managed bytes allocated by the render thread's blocks over the
// overlap; then the beatmatched pairs' published speed steps. WAVs (32-bit float, 48 kHz stereo) per plan. Exit 0 ok, 1 fault
// (a NaN, an allocation in a mixed block, a skipped lane), 64 usage. Nothing here runs in a normal launch.

using System.Globalization;
using FluentGpu.Media;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>One #48 join played offline through the engine, armed as the pump arms it (see the file header).</summary>
    public static class MixOffline
    {
        public const int Rate = 48_000, Ch = 2, Block = 480;
        /// <summary>The mixer frame the window starts at (A has played this long).</summary>
        public const long Start = Rate * 3L / 2;

        /// <summary>A step of B's own rate as the 200 ms tick saw it: the tick's mixer frame, the factor, the engine's change count, the
        /// mixer frame the step landed on and the plan's frame for it.</summary>
        public readonly record struct Step(long TickFrame, double Factor, long Changes, long LandedFrame, long PlanFrame);

        public sealed class Result
        {
            public required Playback.MixJoin Join;
            public required int OutFrames;
            public required float[] Pcm;
            public required List<Step> Steps;
            public long HandOffFrame, ARetiredFrame = -1, End;
            public long OverlapBlocks, OverlapAllocBytes;
            public double Peak;
            public bool NaN;
            public VoiceLoopReading? Loop;
        }

        /// <summary>Play <paramref name="t"/>'s join: A (tempo <see cref="MixTiming.BpmA"/>) for <see cref="Start"/> frames, the
        /// window (A's <see cref="MixTiming.OutDurationMs"/>, B's from its media window), the hand-off when A is over, then B on until its
        /// glide has ended (or <paramref name="tailSeconds"/> after the windows for a plan at speed 1: an effect's tail rings out).</summary>
        public static Result Run(in MixTiming t, double tailSeconds = 3)
        {
            int outFrames = (int)(t.OutDurationMs * (long)Rate / 1000);
            float bpmA = t.BpmA > 0 ? t.BpmA : MixCurves.FallbackBpm;
            var join = Playback.MixRender.Join(in t, Rate, Ch, Start, outFrames, Start, outFrames);
            long windowsEnd = Start + Math.Max(outFrames, join.InFrames);
            long end = join.InRate is { StepCount: > 0 } plan
                ? plan.StepFrame(plan.StepCount) + Rate
                : windowsEnd + (long)(tailSeconds * Rate);
            long aFrames = Start + outFrames + Rate;
            long bFrames = (long)(join.InRate?.SourceFramesBetween(Start, end) ?? end - Start) + Rate;
            // B's own tempo is A's / speedB: at speedB it beats with A through the overlap.
            var aPcm = Deck(aFrames, bpmA, 55, 440, 5_000, 0.3f, 0);
            var bPcm = Deck(bFrames, bpmA / Math.Max(0.5f, t.SpeedB), 82, 660, 7_000, 0.3f, 0.37);
            var endpoint = new HeadlessAudioEndpoint(new MixFormat(Rate, Ch), (int)(end + Rate));
            var s = new PcmAudioSession(new MixFormat(Rate, Ch), endpoint.Sink, endpoint.Clock, 1024, driveWithOwnThread: false, endpoint);
            try
            {
                s.SetVoice(new MemoryAudioSource(aPcm, Ch), TimeSpan.FromSeconds((double)aFrames / Rate), aFrames, NormMode.Off, -18f, 1f);
                s.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
                _ = s.PlayAsync();
                for (int i = 0; i < 16 && s.SampleClock == 0; i++) s.PumpAudio(Block);
                if (s.SampleClock == 0) throw new InvalidOperationException("the session never rendered");
                PumpTo(s, Start - Rate / 2);                       // the pump commits within 2 s, at least 300 ms ahead: 500 ms here
                long aId = s.PrimaryVoiceIdValue;
                const long bId = 2;
                var b = new MemoryAudioSource(bPcm, Ch);
                if (!Playback.MixRender.Arm(s, join, b, bId, aId, Start, outFrames, 1f, out _))
                    throw new InvalidOperationException("B was not admitted");
                var result = new Result { Join = join, OutFrames = outFrames, Pcm = [], Steps = [], End = end };
                long seen = 0, nextTick = 0;
                bool handed = false;
                while (s.SampleClock < end)
                {
                    long clock = s.SampleClock;
                    bool mixed = clock >= Start && clock + Block <= windowsEnd;
                    long before = mixed ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    s.PumpAudio(Block);
                    if (mixed)
                    {
                        result.OverlapAllocBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                        result.OverlapBlocks++;
                    }
                    if (s.SampleClock == clock) throw new InvalidOperationException($"the session stalled at {clock} ({s.CurrentState})");
                    if (!handed && s.SampleClock >= Start + outFrames)
                    {
                        // A's loop as it stands at the end of A's window (the hand-off retires A's source from the session's map).
                        if (s.TryReadVoiceLoop(aId, out var loop)) result.Loop = loop;
                        // The announce: B is the deck's row from here (AnnounceGaplessJoin's SetActiveVoice).
                        s.SetActiveVoice(bId, b, TimeSpan.FromSeconds((double)bFrames / Rate), bFrames);
                        result.HandOffFrame = s.SampleClock;
                        handed = true;
                    }
                    if (result.ARetiredFrame < 0 && handed && s.MixerRef.VoiceCount <= 1) result.ARetiredFrame = s.SampleClock;
                    if (s.SampleClock < nextTick) continue;
                    nextTick = s.SampleClock + Rate / 5;           // the pump's 200 ms tick
                    // TickMixSpeed: B's speed goes out once B is the deck's row, then on every change the engine applied.
                    if (handed && join.InRate is { } rp && s.TryReadVoiceRate(bId, out var r) && r.Changes != 0 && r.Changes != seen)
                    {
                        seen = r.Changes;
                        result.Steps.Add(new Step(s.SampleClock, r.Factor, r.Changes, r.LastChangeMixerFrame,
                            r.Changes <= 1 ? Start : rp.StepFrame((int)r.Changes - 1)));
                    }
                }
                var y = ((NullAudioSink)endpoint.Sink).Captured.ToArray();
                result.Pcm = y;
                foreach (float v in y)
                {
                    if (float.IsNaN(v) || float.IsInfinity(v)) { result.NaN = true; continue; }
                    result.Peak = Math.Max(result.Peak, Math.Abs(v));
                }
                return result;
            }
            finally { s.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        static void PumpTo(PcmAudioSession s, long mixerFrame)
        {
            while (s.SampleClock < mixerFrame)
            {
                long before = s.SampleClock;
                s.PumpAudio(Block);
                if (s.SampleClock == before) throw new InvalidOperationException($"the session stalled at {before} ({s.CurrentState})");
            }
        }

        /// <summary>A deck: a decaying kick on every beat at <paramref name="bpm"/> plus a bass, a mid and a high tone (one per EQ band),
        /// so every lane has something to move.</summary>
        static float[] Deck(long frames, double bpm, double bassHz, double midHz, double highHz, float amp, double phase)
        {
            var y = new float[frames * Ch];
            double beat = 60.0 * Rate / bpm;
            for (long f = 0; f < frames; f++)
            {
                double tb = (f + phase * beat) % beat / Rate;
                double kick = Math.Exp(-tb * 30) * Math.Sin(2 * Math.PI * 60 * tb);
                double t = (double)f / Rate;
                double v = amp * (0.6 * kick + 0.25 * Math.Sin(2 * Math.PI * bassHz * t) + 0.2 * Math.Sin(2 * Math.PI * midHz * t)
                    + 0.1 * Math.Sin(2 * Math.PI * highHz * t));
                y[f * Ch] = y[f * Ch + 1] = (float)v;
            }
            return y;
        }

        /// <summary>32-bit float WAV, 48 kHz stereo.</summary>
        public static void Wav(string path, float[] y)
        {
            int frames = y.Length / Ch;
            using var file = File.Create(path);
            using var w = new BinaryWriter(file);
            int bytes = frames * Ch * 4;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)Ch); w.Write(Rate); w.Write(Rate * Ch * 4); w.Write((short)(Ch * 4)); w.Write((short)32);
            w.Write("data"u8); w.Write(bytes);
            foreach (float v in y.AsSpan(0, frames * Ch)) w.Write(v);
        }

        /// <summary>What one side's automation holds, as <c>eq sweep=12 noise=40 echo=12000f reverb</c> ("-" when none).</summary>
        public static string Describe(VoiceAutomation? a)
        {
            if (a is null) return "-";
            var parts = new List<string>();
            if (a.HasEq) parts.Add("eq");
            if (a.SweepSteps > 0) parts.Add("sweep=" + a.SweepSteps);
            if (a.ShelfSteps > 0) parts.Add("shelf=" + a.ShelfSteps);
            if (a.HasNoise) parts.Add("noise=" + a.NoiseColourSteps);
            if (a.EchoDelayFrames > 0) parts.Add("delayLine=" + a.EchoDelayFrames + "f");
            if (a.HasEffects) parts.Add("fx=" + a.EffectBytes / 1024 + "KB");
            return parts.Count == 0 ? "(neutral)" : string.Join(' ', parts);
        }
    }

    /// <summary>`--mix-bench --scenario render` (see the file header).</summary>
    public static class MixRenderBench
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static int Run(string[] args)
        {
            string dir = "C:/WAVEE/scratch/mixm4";
            int[] presets = Enumerable.Range(1, 22).ToArray();
            int[] barsList = [2, 8];
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--wav-out") dir = args[i + 1];
                else if (args[i] == "--presets") presets = Range(args[i + 1]);
                else if (args[i] == "--bars") barsList = args[i + 1].Split(',').Select(v => int.Parse(v, Inv)).ToArray();
            }
            Directory.CreateDirectory(dir);
            int faults = 0;
            Console.Out.WriteLine($"mix-bench render: rate={MixOffline.Rate} block={MixOffline.Block} out={dir}");
            Console.Out.WriteLine("plan                                  rendered | skipped | A auto | B auto | B rate | A loop | A left (ms past window) | peak | alloc/blocks");
            foreach (int preset in presets)
                foreach (int bars in barsList)
                    faults += One($"preset{preset}-bars{bars}", Plan(preset, default, bars), dir);
            // The ingredients no preset uses: the noise sweep, every effect style, every looping style (overrides, on Fade at 4 bars).
            faults += One("noise-bars4", Plan(1, new MixStyleOverrides(Filter: MixFilterStyle.NoiseOutEnd), 4), dir);
            foreach (MixFxStyle fx in Enum.GetValues<MixFxStyle>())
                if (fx > MixFxStyle.None) faults += One($"fx{(int)fx}-{fx}-bars4", Plan(1, new MixStyleOverrides(Fx: fx), 4), dir);
            foreach (MixLoopingStyle loop in Enum.GetValues<MixLoopingStyle>())
                if (loop > MixLoopingStyle.None) faults += One($"loop{(int)loop}-{loop}-bars4", Plan(1, new MixStyleOverrides(Looping: loop), 4), dir);
            // Beatmatched pairs (the official player's speeds, protocol §6.2), and a plan unmatched at 0 bars (the Auto shape).
            faults += One("unmatched-fade-2890", MixTiming.Of(MixSource.Auto, 1, default, 0, 0, 2_890, 0, 2_510, 1f, 120f), dir);
            foreach (float speed in new[] { 0.901696f, 1.09994f, 1.00008f })
            {
                var t = MixTiming.Of(MixSource.Saved, (int)MixPresetId.Wave, default, 4, 0, 8_000, 0, (uint)Math.Round(8_000 * (double)speed), speed, 120f);
                faults += One("beatmatched-wave-" + speed.ToString("0.######", Inv), t, dir, steps: true);
            }
            Console.Out.WriteLine(faults == 0 ? "done: every lane rendered, no NaN, no allocation in a mixed block" : "done: " + faults + " fault(s)");
            return faults == 0 ? 0 : 1;
        }

        static MixTiming Plan(int preset, in MixStyleOverrides o, int bars)
        {
            // A window of `bars` bars at 120 bpm (2 s a bar); 0 bars: the 2-bar layout over the rule's 2.89 s.
            uint window = bars > 0 ? (uint)(bars * 2_000) : 2_890u;
            return MixTiming.Of(MixSource.Saved, preset, in o, bars, 0, window, 0, window, 1f, 120f);
        }

        static int One(string label, in MixTiming t, string dir, bool steps = false)
        {
            var r = MixOffline.Run(in t);
            string skipped = Playback.MixRender.Lanes(in t, rendered: false);
            string rate = r.Join.InRate is { } p ? $"{p.Initial:0.######}->1 x{p.StepCount}" : "1";
            string loop = r.Join.OutLoop is { } l ? $"{l.Blocks.Length}blk/{l.End} {(r.Loop is { } lr ? lr.Phase + " b=" + lr.Boundaries : "-")}" : "-";
            long windowEnd = MixOffline.Start + r.OutFrames;
            string left = r.ARetiredFrame < 0 ? "never" : ((r.ARetiredFrame - windowEnd) * 1000 / MixOffline.Rate).ToString(Inv);
            bool fault = r.NaN || skipped.Length > 0 || r.OverlapAllocBytes != 0;
            Console.Out.WriteLine($"{label,-36}  [{r.Join.Lanes}] | [{skipped}] | {MixOffline.Describe(r.Join.OutAuto)} | {MixOffline.Describe(r.Join.InAuto)} | {rate} | {loop} | {left} | "
                + $"{r.Peak:0.000}{(r.NaN ? " NaN!" : "")} | {r.OverlapAllocBytes}B/{r.OverlapBlocks}{(fault ? "  FAULT" : "")}");
            MixOffline.Wav(Path.Combine(dir, label + ".wav"), r.Pcm);
            if (steps)
            {
                Console.Out.WriteLine("    published (TickMixSpeed on a 200 ms tick of mixer time; hand-off at " + r.HandOffFrame + "):");
                Console.Out.WriteLine("      #  tick-frame  landed-frame  plan-frame  playback_speed");
                for (int i = 0; i < r.Steps.Count; i++)
                {
                    var s = r.Steps[i];
                    Console.Out.WriteLine($"     {i,2}  {s.TickFrame,10}  {s.LandedFrame,12}  {s.PlanFrame,10}  {s.Factor.ToString("0.000000", Inv)}{(i == 0 ? "  (B's speedB, published at the hand-off)" : "")}");
                }
            }
            return fault ? 1 : 0;
        }

        static int[] Range(string v)
        {
            var dash = v.Split('-');
            if (dash.Length == 2) { int a = int.Parse(dash[0], Inv), b = int.Parse(dash[1], Inv); return Enumerable.Range(a, b - a + 1).ToArray(); }
            return v.Split(',').Select(x => int.Parse(x, Inv)).ToArray();
        }
    }
}
