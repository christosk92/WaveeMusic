// ── Screens/Diagnostics.MixBench.Fx.cs ────────────────────────────────────────────────────────────────────────────────
// `--mix-bench-fx`: the #48 M4 engine lanes (E5 3-band EQ, E6 noise, E7 echo / delay / reverb) measured OFFLINE: synthetic
// sources (tones, sine sweeps, pink noise, a click train) through the engine's real CrossfadeMixer and per-voice chain
// (AudioGraphHost.BuildVoiceChain: gain, EQ, VoiceAutomationStage), rendered block by block as the render thread does, with
// each lane armed. No device, no window, no sign-in. Prints one measurement line per check and writes the renders as 32-bit
// float WAVs.
//
//   Wavee.exe --mix-bench-fx [--out C:/WAVEE/scratch/mixbench-fx] [--only eq|noise|echo|reverb|cpu|bypass]
//
// `--only bypass` touches only the engine API that predates E5 (no VoiceAutomationSpec), so the same Wavee.dll can time an
// unarmed voice against an older FluentGpu.Engine.dll dropped into a copy of the run folder.
//
// Exit 0 ok, 1 fault, 64 usage. Nothing here runs in a normal launch.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using FluentGpu.Media;

namespace Wavee;

public static partial class Diagnostics
{
    public static class MixFxBench
    {
        const int Rate = 48_000, Ch = 2, Block = 480;
        static string s_out = "C:/WAVEE/scratch/mixbench-fx";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool TryRun(string[] args, out int code)
        {
            code = 0;
            if (Array.IndexOf(args, "--mix-bench-fx") < 0) return false;
            var utf8 = new System.Text.UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            string only = "";
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--out") s_out = args[i + 1];
                else if (args[i] == "--only") only = args[i + 1];
            }
            if (only is not ("" or "eq" or "noise" or "echo" or "reverb" or "cpu" or "bypass"))
            {
                Console.Error.WriteLine("mix-bench-fx: --only eq|noise|echo|reverb|cpu|bypass");
                code = 64;
                return true;
            }
            try
            {
                Directory.CreateDirectory(s_out);
                bool jitOff = typeof(VoiceAutomation).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is [DebuggableAttribute d, ..] && d.IsJITOptimizerDisabled;
                Line($"mix-bench-fx rate={Rate} ch={Ch} block={Block} out={s_out} engine JIT optimizer={(jitOff ? "off (Debug engine)" : "on")}");
                if (only is "" or "eq") Eq();
                if (only is "" or "noise") Noise();
                if (only is "" or "echo") Echo();
                if (only is "" or "reverb") Reverb();
                if (only is "" or "cpu") Cpu();
                if (only is "bypass") Bypass();
                Line("done");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("mix-bench-fx: fault: " + ex);
                code = 1;
            }
            return true;
        }

        // ── E5: the 3-band EQ ─────────────────────────────────────────────────────────────────────────────────────────

        static void Eq()
        {
            Line("== E5 3-band EQ (Linkwitz-Riley crossovers " + F0(EqLaneSpec.DefaultLowMidHz) + " Hz / " + F0(EqLaneSpec.DefaultMidHighHz) + " Hz)");
            int frames = 3 * Rate;
            // Tones in the middle of each band: the kill depth and the gain accuracy, read by Goertzel over the last second.
            var tones = Mono(frames, i => 0.1f * (Sin(60, i) + Sin(800, i) + Sin(8000, i)));
            // Pink noise: band RMS over 20-100 Hz, 500-1200 Hz, 6-16 kHz (clear of the crossovers), by Welch FFT.
            var pink = Pink(frames, 0.1f, seed: 7);
            float[] toneHz = [60f, 800f, 8000f];
            double[] dryTone = new double[3], dryBand = new double[3];
            for (int k = 0; k < 3; k++) dryTone[k] = Tone(tones, toneHz[k], Rate, 2 * Rate, Rate);
            BandRms(pink, 2 * Rate, Rate, dryBand);
            string[] bands = ["low", "mid", "high"];
            float[] gains = [float.NegativeInfinity, -12f, 0f, 6f];
            Line("slope band  set      | tones dB (60 Hz / 800 Hz / 8 kHz) | pink band RMS dB (20-100 / 500-1200 / 6k-16k)");
            foreach (int slope in (int[])[48, 24])
            for (int b = 0; b < 3; b++)
                foreach (float g in gains)
                {
                    if (slope == 24 && !float.IsNegativeInfinity(g)) continue;   // 24 dB/oct: the kill rows only
                    float[] low = [b == 0 ? g : 0f], mid = [b == 1 ? g : 0f], high = [b == 2 ? g : 0f];
                    var spec = new VoiceAutomationSpec(Rate, 0, frames) { Eq = new EqLaneSpec(low, mid, high) { Slope = slope } };
                    var outTones = RenderOne(tones, spec, frames);
                    var outPink = RenderOne(pink, spec, frames);
                    double[] band = new double[3];
                    BandRms(outPink, 2 * Rate, Rate, band);
                    string set = float.IsNegativeInfinity(g) ? "kill" : F1(g) + " dB";
                    Line($"{slope,-5} {bands[b],-5} {set,-8} | "
                        + $"{Db(Tone(outTones, 60, Rate, 2 * Rate, Rate) / dryTone[0]),7} {Db(Tone(outTones, 800, Rate, 2 * Rate, Rate) / dryTone[1]),7} {Db(Tone(outTones, 8000, Rate, 2 * Rate, Rate) / dryTone[2]),7}       | "
                        + $"{Db(band[0] / dryBand[0]),7} {Db(band[1] / dryBand[1]),7} {Db(band[2] / dryBand[2]),7}");
                    if (slope == 48 && float.IsNegativeInfinity(g)) Wav($"eq-{bands[b]}-kill-pink.wav", outPink);
                }
            var flat = RenderOne(pink, new VoiceAutomationSpec(Rate, 0, frames) { Eq = new EqLaneSpec(new float[] { 0f }, new float[] { 0f }, new float[] { 0f }) }, frames);
            Line($"all 0 dB: Build drops the lane; max |out - in| = {MaxDiff(flat, pink):E2}");

            // Zipper: a 60 Hz sine whose low band moves 0 dB -> -48 -> kill -> -48 -> 0 -> +6 -> 0 over 2 s; the max sample step and
            // second difference, against the static runs at the extremes. The same move on E2's stepped low shelf for reference.
            int mf = 2 * Rate, mlen = mf + Rate / 2;
            var sine = Mono(mlen, i => 0.5f * Sin(60, i));
            var moveDb = Curve(GainEnvelope.TablePoints, x => (float)(
                x < 0.45 ? -48 * (x / 0.45) : x < 0.5 ? (x > 0.495 ? double.NegativeInfinity : -48) : x < 0.55 ? -48
                : x < 0.75 ? -48 + 48 * ((x - 0.55) / 0.2) : x < 0.9 ? 6 * ((x - 0.75) / 0.15) : 6 - 6 * ((x - 0.9) / 0.1)));
            var moving = RenderOne(sine, new VoiceAutomationSpec(Rate, Rate / 4, mf) { Eq = new EqLaneSpec(moveDb, new float[] { 0f }, new float[] { 0f }) }, mlen);
            var at0 = RenderOne(sine, new VoiceAutomationSpec(Rate, 0, mf) { Eq = new EqLaneSpec(new float[] { 0.001f }, new float[] { 0f }, new float[] { 0f }) }, mlen);
            var at6 = RenderOne(sine, new VoiceAutomationSpec(Rate, 0, mf) { Eq = new EqLaneSpec(new float[] { 6f }, new float[] { 0f }, new float[] { 0f }) }, mlen);
            var shelfDb = (float[])moveDb.Clone();
            for (int i = 0; i < shelfDb.Length; i++) shelfDb[i] = Math.Max(-48f, shelfDb[i]);
            var shelf = RenderOne(sine, new VoiceAutomationSpec(Rate, Rate / 4, mf) { LowShelf = new ShelfLaneSpec(200f, shelfDb) }, mlen);
            Wav("eq-low-moving-60hz.wav", moving);
            Wav("eq-low-moving-60hz-e2-shelf.wav", shelf);
            // Measured from 0.1 s on: the sine's own onset (a step in its slope) is not the EQ's. The residual is the moving run
            // against the static 0 dB run times the table's gain read per sample (linear interpolation in linear gain): what a
            // perfectly smooth gain on the low band gives. A stepped gain would leave its steps in it.
            int skip = Rate / 10;
            double resid = 0;
            for (int i = skip; i < mlen; i++)
            {
                double pos = Math.Clamp((i - Rate / 4) / (double)mf, 0, 1) * (moveDb.Length - 1);
                int k = Math.Min((int)pos, moveDb.Length - 2);
                double g0 = DbGain(moveDb[k]), g1 = DbGain(moveDb[k + 1]), g = g0 + (g1 - g0) * (pos - k);
                resid = Math.Max(resid, Math.Abs(moving[i * Ch] - g * at0[i * Ch]));
            }
            Line($"zipper (60 Hz sine 0.5 FS, low band 0 -> -48 -> kill -> -48 -> 0 -> +6 -> 0 dB over 2 s): max|d1| moving={MaxD1(moving, skip):F5} "
                + $"(static 0 dB {MaxD1(at0, skip):F5}, +6 dB {MaxD1(at6, skip):F5}) | max|d2| moving={MaxD2(moving, skip):E2} (static 0 dB {MaxD2(at0, skip):E2}, "
                + $"+6 dB {MaxD2(at6, skip):E2}) | E2 stepped shelf, same move: max|d2|={MaxD2(shelf, skip):E2}");
            Line($"zipper residual: max |moving - gain(t) x static| = {resid:E2} ({Db(resid / 0.5)} dB re the sine)");
        }

        // ── E6: noise ──────────────────────────────────────────────────────────────────────────────────────────────────

        static void Noise()
        {
            Line("== E6 noise (white xorshift per channel, level = dBFS RMS of the white noise, colour = a 12 dB/oct filter over it)");
            int frames = 2 * Rate;
            var silence = new float[frames * Ch];
            Line("level table dB | measured RMS dBFS L / R | L-R correlation");
            foreach (float db in (float[])[-60f, -40f, -20f, -6f, 0f])
            {
                var o = RenderOne(silence, new VoiceAutomationSpec(Rate, 0, frames) { Noise = new NoiseLaneSpec(new[] { db }) }, frames);
                Line($"{F1(db),14} | {Db(Rms(o, 0, 0, frames)),8} / {Db(Rms(o, 1, 0, frames)),-8} | {Corr(o, 0, frames):F4}");
                if (db == -20f) Wav("noise-white-20db.wav", o);
            }
            var a = RenderOne(silence, new VoiceAutomationSpec(Rate, 0, frames) { Noise = new NoiseLaneSpec(new[] { -20f }) { Seed = 42 } }, frames);
            var b = RenderOne(silence, new VoiceAutomationSpec(Rate, 0, frames) { Noise = new NoiseLaneSpec(new[] { -20f }) { Seed = 42 } }, frames);
            var c = RenderOne(silence, new VoiceAutomationSpec(Rate, 0, frames) { Noise = new NoiseLaneSpec(new[] { -20f }) { Seed = 43 } }, frames);
            Line($"determinism: same seed max|diff| = {MaxDiff(a, b):E2}; another seed max|diff| = {MaxDiff(a, c):F3}");

            // A level ramp -60 -> 0 dB over 2 s: the RMS per 0.25 s window against the table.
            var ramp = Curve(GainEnvelope.TablePoints, x => (float)(-60 + 60 * x));
            var r = RenderOne(silence, new VoiceAutomationSpec(Rate, 0, frames) { Noise = new NoiseLaneSpec(ramp) }, frames);
            var sb = new System.Text.StringBuilder("level ramp -60 -> 0 dB over 2 s, window table / measured dB:");
            for (int w = 0; w < 8; w++)
            {
                int from = w * Rate / 4, len = Rate / 4;
                double tableDb = 20 * Math.Log10(MeanSquareOfDbRamp(-60 + 60.0 * w / 8, -60 + 60.0 * (w + 1) / 8)) / 2;
                sb.Append($" {F1(tableDb)}/{Db(Rms(r, 0, from, len))}");
            }
            Line(sb.ToString());

            // Colour: fixed filters, then the reference's noise colour curve (kind 5: low-pass 100 Hz -> 29.4 kHz below 0.5, off
            // around 0.5, high-pass 37 Hz -> 15.2 kHz above) swept 0 -> 1 over 8 s at -20 dB. The spectral centroid per window.
            Line("colour (fixed)        | centroid Hz | RMS dBFS");
            (string Name, SweepPoint P)[] fixedColours =
            [
                ("white", new SweepPoint(SweepPoint.Bypass, 0f, 0.707f)),
                ("low-pass 100 Hz", new SweepPoint(SweepPoint.LowPass, 100f, 0.707f)),
                ("low-pass 1 kHz", new SweepPoint(SweepPoint.LowPass, 1000f, 0.707f)),
                ("low-pass 5 kHz", new SweepPoint(SweepPoint.LowPass, 5000f, 0.707f)),
                ("high-pass 1 kHz", new SweepPoint(SweepPoint.HighPass, 1000f, 0.707f)),
                ("high-pass 5 kHz", new SweepPoint(SweepPoint.HighPass, 5000f, 0.707f)),
                ("high-pass 15 kHz", new SweepPoint(SweepPoint.HighPass, 15000f, 0.707f)),
            ];
            foreach (var (name, pt) in fixedColours)
            {
                var o = RenderOne(silence, new VoiceAutomationSpec(Rate, 0, frames) { Noise = new NoiseLaneSpec(new[] { -20f }) { Colour = new[] { pt } } }, frames);
                Line($"{name,-21} | {Centroid(o, 0, frames),11:F0} | {Db(Rms(o, 0, 0, frames))}");
            }
            int sf = 8 * Rate;
            var colour = new SweepPoint[GainEnvelope.TablePoints];
            for (int i = 0; i < colour.Length; i++) colour[i] = NoiseColour(i / (double)(colour.Length - 1));
            var swept = RenderOne(new float[sf * Ch], new VoiceAutomationSpec(Rate, 0, sf) { Noise = new NoiseLaneSpec(new[] { -20f }) { Colour = colour } }, sf);
            Wav("noise-colour-sweep.wav", swept);
            Line("colour sweep 0 -> 1 over 8 s | window raw | filter at the window centre | centroid Hz");
            for (int w = 0; w < 16; w++)
            {
                int from = w * Rate / 2, len = Rate / 2;
                double raw = (w + 0.5) / 16;
                var pt = NoiseColour(raw);
                string what = pt.Mode == SweepPoint.LowPass ? $"low-pass {F0(pt.FreqHz)} Hz" : pt.Mode == SweepPoint.HighPass ? $"high-pass {F0(pt.FreqHz)} Hz" : "off (white)";
                Line($"  {raw,5:F3} | {what,-20} | {Centroid(swept, from, len),8:F0}");
            }
        }

        /// <summary>The reference's noise colour semantic (48-mix-reference.md §4.1, kind 5), in the bench only: the engine takes
        /// the filter, the app owns this mapping.</summary>
        static SweepPoint NoiseColour(double raw)
        {
            if (raw > 0.495 && raw < 0.505) return new SweepPoint(SweepPoint.Bypass, 0f, 0.707f);
            if (raw < 0.5)
            {
                double t = Math.Pow(raw / 0.5, 1.78);
                return new SweepPoint(SweepPoint.LowPass, (float)(100.48416305247163 * Math.Pow(29436.773229308717 / 100.48416305247163, t)), 0.707f);
            }
            double u = 1 - Math.Pow(1 - (raw - 0.5) / 0.5, 1.78);
            return new SweepPoint(SweepPoint.HighPass, (float)(37.33756354910836 * Math.Pow(15214.43784823654 / 37.33756354910836, u)), 0.707f);
        }

        /// <summary>The mean square of a level ramping linearly in dB from <paramref name="a"/> to <paramref name="b"/> (the
        /// engine interpolates in linear amplitude between table points 2 ms apart, the same to a few hundredths of a dB).</summary>
        static double MeanSquareOfDbRamp(double a, double b)
        {
            double s = 0;
            for (int i = 0; i < 1000; i++) s += Math.Pow(10, (a + (b - a) * (i + 0.5) / 1000) / 10);
            return s / 1000;
        }

        // ── E7: echo and delay ─────────────────────────────────────────────────────────────────────────────────────

        static void Echo()
        {
            const int bpm = 120;
            int half = Rate * 60 / bpm / 2, threeQ = Rate * 60 * 3 / bpm / 4, beat = Rate * 60 / bpm;
            Line($"== E7 echo / delay (a click at 0.1 s, 0.5 FS; wet 0.5 equal-power; delays from {bpm} bpm: 1/2 beat {half}, 3/4 beat {threeQ}, 1 beat {beat} frames)");
            Line("unit   delay req | spacing measured (frames)          | per repeat dB (feedback: expected) | repeats > -80 dB | tail after the dry end | voice retired");
            foreach (var (name, delay, fb) in ((string, int, float)[])[("echo", half, DelayLaneSpec.DefaultEchoFeedback), ("echo", threeQ, 0.25f),
                         ("echo", beat, 0.75f), ("delay", half, DelayLaneSpec.DefaultDelayFeedback), ("delay", threeQ, DelayLaneSpec.DefaultDelayFeedback)])
            {
                int len = 40 * Rate, click = Rate / 10, srcLen = Rate / 5;
                var src = new float[srcLen * Ch];
                src[click * Ch] = src[click * Ch + 1] = 0.5f;
                var lane = new DelayLaneSpec(delay, new[] { 0.5f }) { Feedback = fb };
                var spec = name == "echo" ? new VoiceAutomationSpec(Rate, 0, len) { Echo = lane } : new VoiceAutomationSpec(Rate, 0, len) { Delay = lane };
                var rig = new Rig();
                rig.Add(src, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(spec));
                var o = rig.RenderAll(len, out long retired);
                if (name == "echo" && delay == half) Wav("echo-click-halfbeat.wav", o);
                // Repeats: the peak of |out| in a window around each expected repeat; spacing from the first two found.
                var peaks = new List<(int At, double Level)>();
                for (int k = 1; k < 200; k++)
                {
                    int at = click + k * delay;
                    if (at + 64 >= len) break;
                    int best = -1; double lv = 0;
                    for (int i = Math.Max(click + 1, at - 64); i < at + 64; i++)
                        if (Math.Abs(o[i * Ch]) > lv) { lv = Math.Abs(o[i * Ch]); best = i; }
                    if (lv <= 1e-4) break;
                    peaks.Add((best, lv));
                }
                string spacing = peaks.Count >= 2
                    ? string.Join(",", Enumerable.Range(1, Math.Min(4, peaks.Count - 1)).Select(i => peaks[i].At - peaks[i - 1].At)) + $" (first at +{peaks[0].At - click})"
                    : peaks.Count == 1 ? $"one repeat at +{peaks[0].At - click}" : "none";
                string decay = peaks.Count >= 2 ? (20 * Math.Log10(peaks[1].Level / peaks[0].Level)).ToString("F2", Inv) : "-";
                string expect = fb > 0 ? (20 * Math.Log10(fb)).ToString("F2", Inv) : "single";
                int lastLoud = LastAbove(o, 1e-4);
                Line($"{name,-6} {delay,9} | {spacing,-34} | {decay,8} ({expect,6})               | {peaks.Count,16} | "
                    + $"{(lastLoud - click) / (double)Rate,6:F2} s{"",15} | {(retired < 0 ? "no" : $"at {retired / (double)Rate:F2} s")}");
            }

            // Echo out: a 120 bpm click train (a kick-like burst every beat) on an Out envelope 1 -> 0 over 4 s, the echo's wet
            // rising 0 -> 0.6 across the window (an "EchoHalfOutEnd"-like shape): the tail after the envelope reaches 0.
            {
                int win = 4 * Rate, len = 12 * Rate;
                var src = new float[(win + Rate) * Ch];
                for (int i = 0; i < win + Rate; i++)
                {
                    int ph = i % beat;
                    float v = ph < 2400 ? (float)(0.6 * Math.Exp(-ph / 600.0) * Math.Sin(2 * Math.PI * 60 * ph / Rate)) : 0f;
                    src[i * Ch] = src[i * Ch + 1] = v;
                }
                var outTable = Curve(GainEnvelope.TablePoints, x => (float)Math.Cos(x * Math.PI / 2));
                var wet = Curve(GainEnvelope.TablePoints, x => (float)(0.6 * x));
                var rig = new Rig();
                rig.Add(src, GainEnvelope.FromCurve(FadeKind.Out, 0, win, outTable), 0)!
                    .Arm(VoiceAutomation.Build(new VoiceAutomationSpec(Rate, 0, win) { Echo = new DelayLaneSpec(half, wet) }));
                var o = rig.RenderAll(len, out long retired);
                Wav("echo-out-120bpm.wav", o);
                int lastLoud = LastAbove(o, 1e-4);
                Line($"echo out (Out envelope 4 s, wet 0 -> 0.6, 1/2 beat, feedback 0.5): envelope at 0 at {win / (double)Rate:F2} s; "
                    + $"tail above -80 dBFS until {lastLoud / (double)Rate:F2} s (+{(lastLoud - win) / (double)Rate:F2} s); voice retired at "
                    + $"{(retired < 0 ? "never" : (retired / (double)Rate).ToString("F2", Inv) + " s")}; peak of the tail after the window {Db(MaxAbs(o, win, len - win))} dBFS");
                var plain = new Rig();
                plain.Add(src, GainEnvelope.FromCurve(FadeKind.Out, 0, win, outTable), 0);
                plain.RenderAll(len, out long plainRetired);
                Line($"the same voice without the echo retires at {plainRetired / (double)Rate:F2} s (the window's end)");
            }

            // Damping: a click through an echo with a 3 kHz high-cut in the feedback: the centroid of each repeat.
            {
                int len = 4 * Rate, click = Rate / 10;
                var src = new float[(Rate / 5) * Ch];
                src[click * Ch] = src[click * Ch + 1] = 0.5f;
                var rig = new Rig();
                rig.Add(src, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(new VoiceAutomationSpec(Rate, 0, len)
                { Echo = new DelayLaneSpec(half, new[] { 0.5f }) { HighCutHz = 3000f } }));
                var o = rig.RenderAll(len, out _);
                var sb = new System.Text.StringBuilder("echo with HighCutHz 3000: centroid of repeats 1..5 (Hz):");
                for (int k = 1; k <= 5; k++) sb.Append(' ').Append(F0(Centroid(o, click + k * half - 512, 8192)));
                Line(sb.ToString());
            }
        }

        // ── E7: reverb ────────────────────────────────────────────────────────────────────────────────────────────────

        static void Reverb()
        {
            Line("== E7 reverb (8-line FDN, Householder, 2 all-pass diffusers per channel)");
            Line("decay param | damping | RT60 measured (T30 x2, both channels) | EDT (s) | L-R wet correlation");
            int burst = Rate / 2;
            var noise = Pink(burst, 0.2f, seed: 11);
            foreach (float rt in (float[])[0.5f, 1f, 2f, 4f])
                foreach (float damp in (float[])[0f, ReverbLaneSpec.DefaultHighCutHz])
                {
                    int len = burst + (int)(rt * 2.2f * Rate) + Rate;
                    var rig = new Rig();
                    rig.Add(noise, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(new VoiceAutomationSpec(Rate, 0, len)
                    { Reverb = new ReverbLaneSpec(rt, new[] { 1f }) { HighCutHz = damp } }));
                    var o = rig.RenderAll(len, out _);
                    var (t30, edt) = Rt60(o, burst, len - burst);
                    Line($"{F1(rt),9} s | {(damp == 0 ? "none" : F0(damp) + " Hz"),7} | {t30,10:F2} s{"",27} | {edt,7:F2} | {Corr(o, burst, len - burst):F3}");
                    if (rt == 2f && damp > 0) Wav("reverb-2s-burst.wav", o);
                }

            // Wet/dry against the table: steady white noise (no correlation past one sample, so the projection onto the input is the
            // dry gain), wet fixed per run; the wet part is what remains. Both relative to the input RMS.
            Line("wet table | dry gain measured (equal-power expected) | wet re its level at wet 1, dB (expected) | total re input dB");
            int sl = 6 * Rate;
            var steady = White(sl, 0.1f, seed: 5);
            double inRms = Rms(steady, 0, Rate, sl - Rate);
            double wet1 = 0;
            foreach (float w in (float[])[1f, 0.75f, 0.5f, 0.25f, 0f])
            {
                var o = RenderOne(steady, new VoiceAutomationSpec(Rate, 0, sl) { Reverb = new ReverbLaneSpec(2f, new[] { w }) }, sl);
                double xy = 0, xx = 0;
                for (int i = 2 * Rate; i < sl; i++) { xy += o[i * Ch] * (double)steady[i * Ch]; xx += steady[i * Ch] * (double)steady[i * Ch]; }
                double dryG = xy / xx, rest = 0;
                for (int i = 2 * Rate; i < sl; i++) { double r = o[i * Ch] - dryG * steady[i * Ch]; rest += r * r; }
                double wetRms = Math.Sqrt(rest / (sl - 2 * Rate));
                if (w == 1f) wet1 = wetRms;
                Line($"{F1(w),9} | {dryG,8:F3} ({Math.Cos(w * Math.PI / 2),5:F3}){"",22} | {Db(wetRms / wet1),6} ({Db(Math.Sin(w * Math.PI / 2))}){"",21} | {Db(Rms(o, 0, 2 * Rate, sl - 2 * Rate) / inRms)}");
            }
            // The wet's absolute level at wet 1 (dry 0), against the input: the output scale is computed for a pink spectrum.
            var sb = new System.Text.StringBuilder("wet level at wet 1 re the input (dB), white / pink noise:");
            var pinkIn = Pink(sl, 0.1f, seed: 9);
            foreach (var (rt, hc) in ((float, float)[])[(1f, ReverbLaneSpec.DefaultHighCutHz), (2f, ReverbLaneSpec.DefaultHighCutHz), (4f, ReverbLaneSpec.DefaultHighCutHz), (2f, 0f)])
            {
                var spec = new VoiceAutomationSpec(Rate, 0, sl) { Reverb = new ReverbLaneSpec(rt, new[] { 1f }) { HighCutHz = hc } };
                var ow = RenderOne(steady, spec, sl);
                var op = RenderOne(pinkIn, spec, sl);
                sb.Append($" | {F1(rt)} s {(hc == 0 ? "undamped" : "damped")}: {Db(Rms(ow, 0, 2 * Rate, sl - 2 * Rate) / inRms)} / {Db(Rms(op, 0, 2 * Rate, sl - 2 * Rate) / Rms(pinkIn, 0, 2 * Rate, sl - 2 * Rate))}");
            }
            Line(sb.ToString());

            // DC: the wet's mean over a 6 s pink noise run (pink: strong sub-bass), and a DC step (0.1 for 1 s) whose tail must
            // return to exactly 0.
            {
                var pinkRun = Pink(sl, 0.1f, seed: 5);
                var o = RenderOne(pinkRun, new VoiceAutomationSpec(Rate, 0, sl) { Reverb = new ReverbLaneSpec(4f, new[] { 1f }) }, sl);
                double mean = 0;
                for (int i = Rate; i < sl; i++) mean += o[i * Ch];
                mean /= sl - Rate;
                int dl = 12 * Rate;
                var dc = Mono(Rate, _ => 0.1f);
                var rig = new Rig();
                rig.Add(dc, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(new VoiceAutomationSpec(Rate, 0, dl) { Reverb = new ReverbLaneSpec(2f, new[] { 1f }) }));
                var d = rig.RenderAll(dl, out long retired);
                double dcDuring = 0;
                for (int i = Rate / 2; i < Rate; i++) dcDuring += d[i * Ch];
                dcDuring /= Rate / 2;
                Line($"DC: wet mean over a 6 s pink noise run {mean:E2} (input RMS 0.100); a 0.1 DC step: wet mean while it lasts {dcDuring:F4}, "
                    + $"tail above -80 dBFS until {LastAbove(d, 1e-4) / (double)Rate:F2} s, voice retired at {(retired < 0 ? "never" : (retired / (double)Rate).ToString("F2", Inv) + " s")}, "
                    + $"last sample {d[^2]:E1}");
            }

            // Denormals: echo + delay + reverb armed (wet 0.5) on one voice: 1 s of noise, then 30 s of digital silence from the source
            // (the voice ends, its tails ring out and stop). ns per block while loud, while the tails decay, and once they are silent.
            {
                int loud = Rate, total = 31 * Rate;
                var src = Pink(loud, 0.2f, seed: 13);
                var rig = new Rig();
                rig.Add(src, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(new VoiceAutomationSpec(Rate, 0, total)
                {
                    Echo = new DelayLaneSpec(Rate / 4, new[] { 0.5f }) { Feedback = 0.9f },
                    Delay = new DelayLaneSpec(Rate / 3, new[] { 0.5f }) { Feedback = DelayLaneSpec.DefaultDelayFeedback },
                    Reverb = new ReverbLaneSpec(8f, new[] { 0.5f }),
                }));
                // A long silent source keeps the voice alive past the tails, so the post-tail blocks are measured too.
                var silentTail = new float[total * Ch];
                Array.Copy(src, silentTail, src.Length);
                var rig2 = new Rig();
                rig2.Add(silentTail, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(new VoiceAutomationSpec(Rate, 0, total)
                {
                    Echo = new DelayLaneSpec(Rate / 4, new[] { 0.5f }) { Feedback = 0.9f },
                    Delay = new DelayLaneSpec(Rate / 3, new[] { 0.5f }) { Feedback = DelayLaneSpec.DefaultDelayFeedback },
                    Reverb = new ReverbLaneSpec(8f, new[] { 0.5f }),
                }));
                var buf = new float[Block * Ch];
                var ns = new double[total / Block];
                for (int b = 0; b < ns.Length; b++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    rig2.Render(buf, Block);
                    ns[b] = Stopwatch.GetElapsedTime(t0).TotalNanoseconds;
                }
                var o = rig.RenderAll(total, out long retired);
                int lastLoud = LastAbove(o, 1e-4);
                double Avg(int from, int to)   // the median block of the phase (other lanes build on this machine: means are noisy)
                {
                    if (to <= from) return double.NaN;
                    var part = ns[from..to];
                    Array.Sort(part);
                    return part[part.Length / 2];
                }
                int loudBlocks = loud / Block, quietFrom = lastLoud / Block + Rate / Block;
                Line($"denormals: tails (echo fb 0.9, delay, reverb 8 s) above -80 dBFS until {lastLoud / (double)Rate:F2} s, voice retired at "
                    + $"{(retired < 0 ? "never" : (retired / (double)Rate).ToString("F2", Inv) + " s")}; median ns/block loud {Avg(10, loudBlocks):F0}, "
                    + $"decaying {Avg(loudBlocks + 10, Math.Min(quietFrom, ns.Length)):F0}, silent after the tails {Avg(Math.Min(quietFrom, ns.Length - 1), ns.Length):F0}");
            }
        }

        static (double T30, double Edt) Rt60(float[] o, int from, int len)
        {
            // Schroeder backward integration of the summed energy of both channels.
            var edc = new double[len];
            double acc = 0;
            for (int i = len - 1; i >= 0; i--)
            {
                double l = o[(from + i) * Ch], r = o[(from + i) * Ch + 1];
                acc += l * l + r * r;
                edc[i] = acc;
            }
            double e0 = edc[0];
            double At(double db) { for (int i = 0; i < len; i++) if (10 * Math.Log10(edc[i] / e0) <= db) return i / (double)Rate; return double.NaN; }
            double t5 = At(-5), t35 = At(-35), t10 = At(-10);
            return ((t35 - t5) * 2, t10 * 6);
        }

        static int LastAbove(float[] x, double floor)
        {
            for (int i = x.Length - 1; i >= 0; i--) if (Math.Abs(x[i]) > floor) return i / Ch;
            return 0;
        }

        static double MaxAbs(float[] x, int from, int len)
        {
            double m = 0;
            for (int i = from * Ch; i < (from + len) * Ch && i < x.Length; i++) m = Math.Max(m, Math.Abs(x[i]));
            return m;
        }

        // ── CPU ─────────────────────────────────────────────────────────────────────────────────────────────────────

        static void Cpu()
        {
            Line("== CPU per " + Block + "-frame block (" + F1(Block * 1000.0 / Rate) + " ms deadline), two voices, median of 5 runs x 2000 blocks");
            int frames = 2000 * Block;
            var src = Pink(frames + Block, 0.1f, seed: 3);
            double none = Median(5, () => TimeBlocks(src, frames, arm: null, stageless: false, out _));
            double bare = Median(5, () => TimeBlocks(src, frames, arm: null, stageless: true, out _));
            Line($"none armed (today's chain, stage bypassed): {none:F0} ns/block | chain without the stage: {bare:F0} ns/block | bypass cost {none - bare:F0} ns/block");
            var down = Curve(1025, x => (float)(-24 * x));
            var up = Curve(1025, x => (float)(3 * x));
            var wet = Curve(1025, x => (float)(0.2 + 0.6 * x));
            var sweep = new SweepPoint[1025];
            for (int i = 0; i < sweep.Length; i++) sweep[i] = new SweepPoint(SweepPoint.LowPass, (float)(16000 * Math.Pow(120 / 16000.0, i / 1024.0)), 0.9f);
            var colour = new SweepPoint[1025];
            for (int i = 0; i < colour.Length; i++) colour[i] = new SweepPoint(SweepPoint.HighPass, (float)(40 * Math.Pow(15000 / 40.0, i / 1024.0)), 0.707f);
            VoiceAutomationSpec ArmEq(long start) => new(Rate, start, frames) { Eq = new EqLaneSpec(down, up, down) };
            VoiceAutomationSpec ArmNoise(long start) => new(Rate, start, frames) { Noise = new NoiseLaneSpec(Curve(1025, x => (float)(-40 + 20 * x))) { Colour = colour } };
            VoiceAutomationSpec ArmFx(long start) => new(Rate, start, frames)
            {
                Echo = new DelayLaneSpec(Rate / 4, wet) { HighCutHz = 4000 },
                Delay = new DelayLaneSpec(3 * Rate / 8, wet) { Feedback = DelayLaneSpec.DefaultDelayFeedback },
                Reverb = new ReverbLaneSpec(2.5f, wet),
            };
            VoiceAutomationSpec ArmAll(long start) => new(Rate, start, frames)
            {
                LowShelf = new ShelfLaneSpec(200f, down),
                Sweep = sweep,
                Eq = new EqLaneSpec(down, up, down),
                Noise = new NoiseLaneSpec(Curve(1025, x => (float)(-40 + 20 * x))) { Colour = colour },
                Echo = new DelayLaneSpec(Rate / 4, wet) { HighCutHz = 4000 },
                Delay = new DelayLaneSpec(3 * Rate / 8, wet) { Feedback = DelayLaneSpec.DefaultDelayFeedback },
                Reverb = new ReverbLaneSpec(2.5f, wet),
            };
            double deadline = Block * 1e9 / Rate;
            foreach (var (name, arm) in ((string, Func<long, VoiceAutomationSpec>)[])[("E5 EQ (48 dB/oct)", ArmEq), ("E6 noise + colour", ArmNoise),
                         ("E7 echo + delay + reverb", ArmFx), ("all lanes (shelf, sweep, EQ, noise, echo, delay, reverb)", ArmAll)])
            {
                long alloc = 0;
                double t = Median(5, () => TimeBlocks(src, frames, arm, stageless: false, out alloc));
                Line($"{name} armed on both voices: {t:F0} ns/block = {t * 100.0 / deadline:F2} % of the deadline (+{t - none:F0} over none armed); "
                    + $"allocated while rendering: {alloc} bytes");
            }
        }

        /// <summary>Two unarmed voices, the chain as built today (gain, EQ, the automation stage bypassed) and without the stage;
        /// 15 interleaved runs of 2000 blocks, the median of each. Pre-E5 API only.</summary>
        static void Bypass()
        {
            int frames = 2000 * Block;
            var src = Pink(frames + Block, 0.1f, seed: 3);
            var with = new double[15];
            var without = new double[15];
            for (int r = 0; r < with.Length; r++)
            {
                with[r] = TimeUnarmed(src, frames, stageless: false);
                without[r] = TimeUnarmed(src, frames, stageless: true);
            }
            Array.Sort(with);
            Array.Sort(without);
            Line($"bypass: two unarmed voices, median of 15 x 2000 blocks: {with[7]:F0} ns/block (chain with the bypassed stage), "
                + $"{without[7]:F0} ns/block (chain without it); engine {typeof(CrossfadeMixer).Assembly.GetName().Version}");
        }

        static double TimeUnarmed(float[] src, int frames, bool stageless)
        {
            var rig = new Rig();
            for (int v = 0; v < 2; v++) rig.Add(src, GainEnvelope.Constant, 0, stageless);
            var buf = new float[Block * Ch];
            rig.Render(buf, Block);   // warm
            int blocks = frames / Block - 1;
            long t0 = Stopwatch.GetTimestamp();
            for (int b = 0; b < blocks; b++) rig.Render(buf, Block);
            return Stopwatch.GetElapsedTime(t0).TotalNanoseconds / blocks;
        }

        static double TimeBlocks(float[] src, int frames, Func<long, VoiceAutomationSpec>? arm, bool stageless, out long allocated)
        {
            var rig = new Rig();
            for (int v = 0; v < 2; v++)
            {
                var stage = rig.Add(src, GainEnvelope.Constant, 0, stageless);
                if (arm is not null && stage is not null) stage.Arm(VoiceAutomation.Build(arm(0)));
            }
            var buf = new float[Block * Ch];
            rig.Render(buf, Block);   // warm
            int blocks = frames / Block - 1;
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            for (int b = 0; b < blocks; b++) rig.Render(buf, Block);
            double ns = Stopwatch.GetElapsedTime(t0).TotalNanoseconds / blocks;
            allocated = GC.GetAllocatedBytesForCurrentThread() - a0;
            return ns;
        }

        // ── The rig: the engine's mixer and voice chain, rendered block by block ─────────────────────────────────────

        sealed class Rig
        {
            readonly CrossfadeMixer _mixer = new(Ch, Block);
            readonly AudioGraphHost _host = new(Ch, Rate);
            readonly ParamPlane _plane = new();
            long _nextId = 1;

            public VoiceAutomationStage? Add(float[] src, GainEnvelope env, long start, bool stageless = false)
            {
                var chain = _host.Live.BuildVoiceChain();
                if (stageless) chain = chain[..^1];
                _mixer.AddVoice(new MixVoice
                {
                    Id = _nextId++, Src = new MemoryAudioSource(src, Ch), Env = env, StartFrame = start, ReplayGainScalar = 1f, Chain = chain,
                });
                return stageless ? null : (VoiceAutomationStage)chain[^1];
            }

            public int Voices => _mixer.VoiceCount;

            public void Render(float[] buf, int frames)
                => _mixer.Render(buf, frames, new BlockCtx(_mixer.ConsumeSeq, Rate, Ch, _plane));

            /// <summary>Render <paramref name="frames"/> frames (the tail may run past the sources).</summary>
            public float[] RenderAll(int frames) => RenderAll(frames, out _);

            /// <summary>Render <paramref name="frames"/> frames; <paramref name="retired"/> = the block end at which the mixer was
            /// left without voices (-1 = never).</summary>
            public float[] RenderAll(int frames, out long retired)
            {
                retired = -1;
                var all = new float[frames * Ch];
                var buf = new float[Block * Ch];
                for (int done = 0; done < frames;)
                {
                    int n = Math.Min(Block, frames - done);
                    Render(buf, n);
                    Array.Copy(buf, 0, all, done * Ch, n * Ch);
                    done += n;
                    if (retired < 0 && Voices == 0) retired = done;
                }
                return all;
            }
        }

        /// <summary>One voice (constant envelope) through a chain armed with <paramref name="spec"/> before it sounds.</summary>
        static float[] RenderOne(float[] src, VoiceAutomationSpec spec, int frames)
        {
            var rig = new Rig();
            rig.Add(src, GainEnvelope.Constant, 0)!.Arm(VoiceAutomation.Build(spec));
            return rig.RenderAll(frames);
        }

        // ── Sources ─────────────────────────────────────────────────────────────────────────────────────────────────

        static float Sin(double hz, int i) => (float)Math.Sin(2 * Math.PI * hz * i / Rate);

        static float[] Mono(int frames, Func<int, float> f)
        {
            var a = new float[frames * Ch];
            for (int i = 0; i < frames; i++) { float v = f(i); for (int c = 0; c < Ch; c++) a[i * Ch + c] = v; }
            return a;
        }

        /// <summary>Pink noise (Paul Kellet's refined filter over white), RMS ≈ <paramref name="rms"/>, decorrelated channels.</summary>
        static float[] Pink(int frames, float rms, int seed)
        {
            var a = new float[frames * Ch];
            var rnd = new Random(seed);
            for (int c = 0; c < Ch; c++)
            {
                double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
                for (int i = 0; i < frames; i++)
                {
                    double w = rnd.NextDouble() * 2 - 1;
                    b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
                    b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
                    a[i * Ch + c] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362);
                    b6 = w * 0.115926;
                }
            }
            double s = 0;
            foreach (float v in a) s += v * v;
            float k = rms / (float)Math.Sqrt(s / a.Length);
            for (int i = 0; i < a.Length; i++) a[i] *= k;
            return a;
        }

        /// <summary>White noise (uniform), RMS <paramref name="rms"/>, decorrelated channels.</summary>
        static float[] White(int frames, float rms, int seed)
        {
            var a = new float[frames * Ch];
            var rnd = new Random(seed);
            float k = rms * 1.7320508f;
            for (int i = 0; i < a.Length; i++) a[i] = k * (float)(rnd.NextDouble() * 2 - 1);
            return a;
        }

        static float[] Curve(int n, Func<double, float> f)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = f(i / (double)(n - 1));
            return a;
        }

        // ── Analysis ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A tone's amplitude in channel 0 over [from, from+len) (Goertzel, Hann-windowed).</summary>
        static double Tone(float[] x, double hz, int rate, int from, int len)
        {
            double w = 2 * Math.PI * hz / rate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0, wsum = 0;
            for (int i = 0; i < len; i++)
            {
                double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (len - 1));
                wsum += win;
                double s = x[(from + i) * Ch] * win + coeff * s1 - s2;
                s2 = s1; s1 = s;
            }
            double re = s1 - s2 * Math.Cos(w), im = s2 * Math.Sin(w);
            return 2 * Math.Sqrt(re * re + im * im) / wsum;
        }

        /// <summary>Band RMS of channel 0 over [from, from+len) by Welch (8192-point Hann segments, half overlap): 20-100 Hz,
        /// 500-1200 Hz, 6-16 kHz.</summary>
        static void BandRms(float[] x, int from, int len, double[] bands)
        {
            double[][] edges = [[20, 100], [500, 1200], [6000, 16000]];
            var spec = Psd(x, from, len, 0);
            for (int b = 0; b < 3; b++)
            {
                double s = 0;
                for (int k = 1; k < spec.Length; k++)
                {
                    double hz = k * (double)Rate / Fft;
                    if (hz >= edges[b][0] && hz < edges[b][1]) s += spec[k];
                }
                bands[b] = Math.Sqrt(s);
            }
        }

        const int Fft = 8192;

        /// <summary>The averaged power spectrum (Fft/2 bins) of channel <paramref name="c"/> over [from, from+len).</summary>
        static double[] Psd(float[] x, int from, int len, int c)
        {
            var acc = new double[Fft / 2];
            var buf = new Complex[Fft];
            int segs = 0;
            for (int s = from; s + Fft <= from + len; s += Fft / 2, segs++)
            {
                for (int i = 0; i < Fft; i++)
                    buf[i] = x[(s + i) * Ch + c] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1)));
                FftInPlace(buf);
                for (int k = 0; k < Fft / 2; k++) acc[k] += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            }
            for (int k = 0; k < acc.Length; k++) acc[k] /= Math.Max(1, segs);
            return acc;
        }

        static void FftInPlace(Complex[] a)
        {
            int n = a.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (a[i], a[j]) = (a[j], a[i]);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (int k = 0; k < len / 2; k++)
                    {
                        var u = a[i + k];
                        var v = a[i + k + len / 2] * w;
                        a[i + k] = u + v;
                        a[i + k + len / 2] = u - v;
                        w *= wl;
                    }
                }
            }
        }

        static double Rms(float[] x, int c, int from, int len)
        {
            double s = 0;
            for (int i = from; i < from + len; i++) s += x[i * Ch + c] * (double)x[i * Ch + c];
            return Math.Sqrt(s / len);
        }

        static double Corr(float[] x, int from, int len)
        {
            double sl = 0, sr = 0, slr = 0;
            for (int i = from; i < from + len; i++) { double l = x[i * Ch], r = x[i * Ch + 1]; sl += l * l; sr += r * r; slr += l * r; }
            return slr / Math.Sqrt(sl * sr + 1e-30);
        }

        /// <summary>The spectral centroid (Hz) of channel 0 over [from, from+len).</summary>
        static double Centroid(float[] x, int from, int len)
        {
            var p = Psd(x, from, len, 0);
            double num = 0, den = 0;
            for (int k = 1; k < p.Length; k++) { double hz = k * (double)Rate / Fft; num += hz * p[k]; den += p[k]; }
            return num / Math.Max(den, 1e-30);
        }

        static double MaxD1(float[] x, int from = 1)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < x.Length / Ch; i++) m = Math.Max(m, Math.Abs(x[i * Ch] - x[(i - 1) * Ch]));
            return m;
        }

        static double MaxD2(float[] x, int from = 2)
        {
            double m = 0;
            for (int i = Math.Max(2, from); i < x.Length / Ch; i++) m = Math.Max(m, Math.Abs(x[i * Ch] - 2 * x[(i - 1) * Ch] + x[(i - 2) * Ch]));
            return m;
        }

        static double DbGain(float db) => float.IsNegativeInfinity(db) || db <= EqLaneSpec.KillDb ? 0 : Math.Pow(10, db / 20.0);

        static double MaxDiff(float[] a, float[] b)
        {
            double m = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        static double Median(int runs, Func<double> f)
        {
            var r = new double[runs];
            for (int i = 0; i < runs; i++) r[i] = f();
            Array.Sort(r);
            return r[runs / 2];
        }

        // ── Output ──────────────────────────────────────────────────────────────────────────────────────────────────

        static string Db(double ratio) => ratio <= 1e-12 ? "-inf" : (20 * Math.Log10(ratio)).ToString("F1", Inv);
        static string F0(double v) => v.ToString("F0", Inv);
        static string F1(double v) => v.ToString("F1", Inv);
        static void Line(string s) => Console.Out.WriteLine(s);

        /// <summary>Write interleaved stereo float samples as a 32-bit float WAV under the output folder.</summary>
        static void Wav(string name, float[] x)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(s_out, name)));
            int bytes = x.Length * 4;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)Ch); w.Write(Rate); w.Write(Rate * Ch * 4); w.Write((short)(Ch * 4)); w.Write((short)32);
            w.Write("data"u8); w.Write(bytes);
            foreach (float v in x) w.Write(v);
        }
    }
}
