// ── Screens/Diagnostics.MixBench.Rate.cs ──────────────────────────────────────────────────────────────────────────────
// `--mix-bench-rate`: #48 M4's engine items E4 (a voice's own rate, the beatmatch glide) and E8 (the beat loop), measured
// OFFLINE on synthetic sources: no device, no files read, no sign-in, no window. Two drives:
//   the STAGE  a voice's WsolaAudioSource driven exactly as PcmAudioSession.RenderBlock drives it (BeginBlock(session rate,
//              mixer frame), then one 480-frame read), so the DSP is measured without the master chain;
//   the SESSION a headless PcmAudioSession (HeadlessAudioEndpoint: null sink with capture, synthetic clock) pumped block by
//              block, so the public API, the hand-off and the reported positions are the real ones.
//
//   Wavee.exe --mix-bench-rate [--out C:/WAVEE/scratch/mixbench-rate] [--only rate|glide|two|session|loop|cpu]
//
// Prints the measurements (one block per section) and writes WAVs (32-bit float, 48 kHz stereo) into the output folder. The
// CPU section compares against TodayWsola, the stage as it was before E4 (fluent-gpu e3330d9f), kept below verbatim for that
// comparison and for the bit-identity check of a voice without a plan. Exit 0 ok, 1 fault. Nothing here runs in a normal launch.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu.Media;

namespace Wavee;

public static partial class Diagnostics
{
    public static class MixRateBench
    {
        const int Sr = 48_000, Ch = 2, Block = 480;
        static string s_dir = "C:/WAVEE/scratch/mixbench-rate";
        static string s_only = "";

        public static bool TryRun(string[] args, out int code)
        {
            code = 0;
            if (Array.IndexOf(args, "--mix-bench-rate") < 0) return false;
            var utf8 = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--out") s_dir = args[i + 1];
                if (args[i] == "--only") s_only = args[i + 1];
            }
            try
            {
                Directory.CreateDirectory(s_dir);
                P($"mix-bench-rate: E4 per-voice rate + glide, E8 beat loop. {Sr} Hz x {Ch} ch, {Block}-frame blocks. WAVs -> {s_dir}");
                if (Want("rate")) Rates();
                if (Want("glide")) Glides();
                if (Want("two")) TwoVoices();
                if (Want("session")) SessionRate();
                if (Want("loop")) Loops();
                if (Want("cpu")) Cpu();
                P($"mix-bench-rate: done");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("mix-bench-rate: faulted: " + ex);
                code = 1;
            }
            return true;
        }

        static bool Want(string section) => s_only.Length == 0 || s_only == section;

        static void P(FormattableString line) => Console.Out.WriteLine(FormattableString.Invariant(line));

        // ── E4: one voice at its own factor ──────────────────────────────────────────────────────────────────────────

        static void Rates()
        {
            P($"");
            P($"== E4 rate: one voice at a constant factor (stage; 30 s sources: a 120 bpm click train, period 24000; a 1 kHz sine)");
            P($"   threshold: |factor - 1| < {WsolaAudioSource.ResampleBelow} is resampled (64-tap sinc, pitch x factor); otherwise WSOLA (pitch kept)");
            const int period = 24_000;
            foreach (double r in new[] { 1.00008, 0.901696, 1.09994 })
            {
                long srcFrames = 30L * Sr;
                long outFrames = (long)(srcFrames / r) - Sr;
                var plan = VoiceRatePlan.Constant(r);
                var stage = new WsolaAudioSource(new MemoryAudioSource(Clicks(srcFrames, period, 0.5f), Ch), Sr, Ch, plan);
                var y = DriveStage(stage, outFrames);
                var reading = stage.ReadRate();
                var peaks = Peaks(y, +1, 0.15f, period / 2, Sr / 4, outFrames);
                var gaps = Gaps(peaks);
                double expected = period / r;
                double mapIdeal = 0, mapAudio = 0;
                foreach (long m in new[] { Sr / 2L, 2L * Sr, 5L * Sr, 10L * Sr, 20L * Sr })
                    mapIdeal = Math.Max(mapIdeal, Math.Abs(stage.SourceFrameAt(m) - r * m));
                foreach (double o in peaks)
                {
                    double k = Math.Round(stage.SourceFrameAt(o) / period);
                    mapAudio = Math.Max(mapAudio, Math.Abs(stage.SourceFrameAt(o) - k * period));
                }
                Wav($"rate-click-{r.ToString(CultureInfo.InvariantCulture)}.wav", y);

                var sineStage = new WsolaAudioSource(new MemoryAudioSource(Sine(srcFrames, 1000, 0.5f), Ch), Sr, Ch, plan);
                var ys = DriveStage(sineStage, 10L * Sr);
                double hz = Pitch(ys, Sr, 9L * Sr);
                Wav($"rate-sine-{r.ToString(CultureInfo.InvariantCulture)}.wav", ys);

                string mode = reading.Resampled ? "resampler" : "WSOLA";
                P($"   factor {r,-9} mode={mode,-9} clicks={peaks.Count,3} spacing mean={Mean(gaps):F2} expected={expected:F2} (err {Mean(gaps) - expected:+0.00;-0.00}) min={Min(gaps):F1} max={Max(gaps):F1}");
                string pitchNote = reading.Resampled ? FormattableString.Invariant($"expected {1000 * r:F2}: pitch x factor") : "expected 1000.00: pitch kept";
                P($"                      sine 1000 Hz -> {hz:F2} Hz ({pitchNote})");
                P($"                      position map vs ideal clock (r x frame) at 0.5/2/5/10/20 s: max err {mapIdeal:F4} frames; vs the clicks heard: max err {mapAudio:F1} frames");
            }

            P($"   -- the same through a session (SetVoice + SetVoiceRate(primary, factor); ContentPositionFrames, timeline Rate, RemainingOutputFrames)");
            foreach (double r in new[] { 1.00008, 0.901696, 1.09994 })
            {
                long srcFrames = 30L * Sr;
                var s = NewSession(out _, 0);
                Start(s, Clicks(srcFrames, period, 0.25f), srcFrames);
                if (!s.SetVoiceRate(s.PrimaryVoiceIdValue, r)) throw new InvalidOperationException("SetVoiceRate refused");
                Play(s);
                int lat = s.Graph.Live.TotalLatencySamples;
                double posErr = 0, rateErr = 0, remErr = 0;
                foreach (long at in new[] { 2L * Sr, 5L * Sr, 10L * Sr, 20L * Sr })
                {
                    PumpTo(s, at);
                    double audible = s.SampleClock - lat;
                    posErr = Math.Max(posErr, Math.Abs(s.ContentPositionFrames - r * audible));
                    if (s.TryReadTimeline(out var anchor)) rateErr = Math.Max(rateErr, Math.Abs(anchor.Rate - r));
                    double remain = (srcFrames - s.ContentPositionFrames) / r;
                    remErr = Math.Max(remErr, Math.Abs(s.RemainingOutputFrames - remain));
                }
                P($"   factor {r,-9} session: ContentPositionFrames vs r x audible frame max err {posErr:F1} frames; timeline Rate err {rateErr:E1}; RemainingOutputFrames err {remErr:F1} frames");
                Close(s);
            }
        }

        // ── E4: the glide back to 1.0 ────────────────────────────────────────────────────────────────────────────────

        static void Glides()
        {
            P($"");
            P($"== E4 glide: hold the factor, then 0.005 per step, one step a second, to 1.0 (stage; first step at frame 96123, off every block/hop edge)");
            P($"   roughness = max |x[n]-x[n-1]| (d1) and |x[n]-2x[n-1]+x[n-2]| (d2) of a 1 kHz sine (amp 0.5) in [step-480, step+1440),");
            P($"   as a ratio of the same in a window 0.5 s later (1.00 = the step adds nothing to what the mode itself does; a click reads >> 1);");
            P($"   level = the lowest 2 ms RMS in that window / the sine's RMS (a crossfade between two out-of-phase copies dips here without clicking)");
            foreach (double from in new[] { 0.901696, 1.09994, 1.00008 })
            {
                long glideStart = 2L * Sr + 123;
                var plan = VoiceRatePlan.Glide(from, glideStart, 1.0, VoiceRatePlan.DefaultGlideStep, Sr);
                long outFrames = glideStart + (plan.StepCount + 2L) * Sr;
                long srcFrames = (long)plan.SourceFramesBetween(0, outFrames) + 2L * Sr;
                var stage = new WsolaAudioSource(new MemoryAudioSource(Sine(srcFrames, 1000, 0.5f), Ch), Sr, Ch, plan);
                var log = new List<VoiceRateReading>();
                long seen = 0;
                var y = DriveStage(stage, outFrames, afterBlock: _ =>
                {
                    var r = stage.ReadRate();
                    if (r.Changes != seen) { seen = r.Changes; log.Add(r); }
                });
                Wav($"glide-sine-{from.ToString(CultureInfo.InvariantCulture)}.wav", y);
                var sweepStage = new WsolaAudioSource(new MemoryAudioSource(Sweep(srcFrames, 60, 12_000, 0.4f), Ch), Sr, Ch, plan);
                Wav($"glide-sweep-{from.ToString(CultureInfo.InvariantCulture)}.wav", DriveStage(sweepStage, outFrames));

                P($"   glide {from} -> 1.0: {plan.StepCount} steps; renderer changes seen {log.Count} (the first = the initial factor at frame {(log.Count > 0 ? log[0].LastChangeMixerFrame : -1)})");
                P($"     step  plan-frame  landed(reading)  landed(map)  value      mode       d1-ratio  d2-ratio  level  ideal-clock err");
                double worstIdeal = 0;
                for (int k = 1; k <= plan.StepCount; k++)
                {
                    long pf = plan.StepFrame(k);
                    double v = plan.StepValue(k);
                    long landed = k < log.Count ? log[k].LastChangeMixerFrame : -1;
                    bool resampled = k < log.Count && log[k].Resampled;
                    // the output -> source map: where the recorded rate becomes this step's rate
                    long mapAt = -1;
                    double want = VoiceRatePlan.EffectiveRate(1, v);
                    for (long o = pf - 2 * Block; o <= pf + 2 * Block; o++)
                        if (Math.Abs(stage.RateAt(o) - want) < 1e-12 && Math.Abs(stage.RateAt(o - 1) - want) > 1e-12) { mapAt = o; break; }
                    var (s1, s2) = Rough(y, pf - Block, pf + 3 * Block);
                    var (r1, r2) = Rough(y, pf + Sr / 2 - Block, pf + Sr / 2 + 3 * Block);
                    double ideal = Math.Abs(stage.SourceFrameAt(pf) - plan.SourceFramesBetween(0, pf));
                    worstIdeal = Math.Max(worstIdeal, ideal);
                    double level = MinRms(y, pf - Block, pf + 3 * Block, 96) / (0.5 / Math.Sqrt(2));
                    string mode = v == 1.0 ? "unity" : resampled ? "resampler" : "WSOLA";
                    P($"     {k,4}  {pf,10}  {landed,15}  {mapAt,11}  {v,-9:F6}  {mode,-9}  {s1 / r1,8:F2}  {s2 / r2,8:F2}  {level,5:F2}  {ideal,8:F3}");
                }
                P($"     position map vs the plan's ideal clock at every step frame: max err {worstIdeal:F3} frames");
            }
        }

        // ── E4: two voices, the hand-off, the glide after it ─────────────────────────────────────────────────────────

        static void TwoVoices()
        {
            P($"");
            P($"== E4 two voices (session): A at 1.0 (+clicks, period 24000), B joins at 1.09994 (-clicks, period 26399) over a 4 s linear crossfade,");
            P($"   B is handed off (SetActiveVoice) when A's fade ends, then glides to 1.0 (first step 0.5 s after the fade, +37 frames)");
            const int pa = 24_000, pb = 26_399;
            long S = 5L * Sr, overlap = 4L * Sr, handoff = S + overlap, glideStart = handoff + Sr / 2 + 37;
            var plan = VoiceRatePlan.Glide(1.09994, glideStart, 1.0, VoiceRatePlan.DefaultGlideStep, Sr);
            long end = glideStart + (plan.StepCount + 2L) * Sr;
            long aFrames = end + Sr, bFrames = (long)plan.SourceFramesBetween(S, end) + 2L * Sr;
            var s = NewSession(out var ep, (int)(end + Sr));
            Start(s, Clicks(aFrames, pa, 0.4f), aFrames);
            Play(s);
            PumpTo(s, S - Sr);
            var bSource = new MemoryAudioSource(Clicks(bFrames, pb, -0.4f), Ch);
            const long bId = 2;
            if (!s.TryAddCrossfadeVoice(bSource, GainEnvelope.Fade(FadeKind.In, S, (int)overlap, CrossCurve.Linear), S, 1f, s.BuildVoiceChain(), bId, plan))
                throw new InvalidOperationException("B not admitted");
            if (!s.SetVoiceEnvelope(s.PrimaryVoiceIdValue, GainEnvelope.Fade(FadeKind.Out, S, (int)overlap, CrossCurve.Linear)))
                throw new InvalidOperationException("A's fade not admitted");
            int lat = s.Graph.Live.TotalLatencySamples;
            var steps = new List<(long Tick, VoiceRateReading R)>();
            long seen = 0, nextTick = 0, handedAt = -1;
            double posErr = 0, rateErr = 0;
            int posChecks = 0;
            while (s.SampleClock < end)
            {
                s.PumpAudio(Block);
                if (handedAt < 0 && s.SampleClock >= handoff)
                {
                    s.SetActiveVoice(bId, bSource, TimeSpan.FromSeconds((double)bFrames / Sr), bFrames);
                    handedAt = s.SampleClock;
                }
                if (s.SampleClock < nextTick) continue;
                nextTick = s.SampleClock + Sr / 5;   // the app's 200 ms tick
                if (s.TryReadVoiceRate(bId, out var r) && r.Changes != seen) { seen = r.Changes; steps.Add((s.SampleClock, r)); }
                if (handedAt >= 0 && s.SampleClock > handedAt + Sr / 10)
                {
                    long audible = s.SampleClock - lat;
                    posErr = Math.Max(posErr, Math.Abs(s.ContentPositionFrames - plan.SourceFramesBetween(S, audible)));
                    if (s.TryReadTimeline(out var anchor))
                        rateErr = Math.Max(rateErr, Math.Abs(anchor.Rate - VoiceRatePlan.EffectiveRate(1, plan.FactorAt(audible))));
                    posChecks++;
                }
            }
            var y = ((NullAudioSink)ep.Sink).Captured.ToArray();
            Wav("two-voices.wav", y);

            // A: its clicks must sit exactly on k x 24000 (+ the master chain's latency) wherever it is still audible.
            var aPeaks = Peaks(y, +1, 0.04f, pa / 2, Sr / 2, S + (long)(overlap * 0.85));
            double aErr = 0;
            foreach (double p in aPeaks) aErr = Math.Max(aErr, Math.Abs(p - lat - Math.Round((p - lat) / pa) * pa));
            // B: each click k (source frame k x 26399) against the plan's mixer frame for it, and against the session's own map.
            var bPeaks = Peaks(y, -1, 0.03f, pb / 2, S + overlap / 4, end);
            double planErr = 0, sessErr = 0, sessErrAfter = 0;
            long switchFrame = plan.StepFrame(plan.StepCount - 1);   // the stretch hands over to the resampler here (1.00494)
            var overlapGaps = new List<double>();
            var finalGaps = new List<double>();
            long finalFrom = plan.StepFrame(plan.StepCount);
            double prev = -1;
            foreach (double p in bPeaks)
            {
                double m = p - lat;
                double k = Math.Round(plan.SourceFramesBetween(S, (long)m) / pb);
                double predicted = S + plan.MixerFramesFor(k * pb, S);
                planErr = Math.Max(planErr, Math.Abs(m - predicted));
                if (s.TryMixerFrameAtSourceFrame(bId, k * pb, out long mapped))
                {
                    if (m < switchFrame) sessErr = Math.Max(sessErr, Math.Abs(m - mapped));
                    else sessErrAfter = Math.Max(sessErrAfter, Math.Abs(m - mapped));
                }
                if (prev >= 0 && m < handoff) overlapGaps.Add(m - prev);
                if (prev >= 0 && prev >= finalFrom) finalGaps.Add(m - prev);
                prev = m;
            }
            P($"   hand-off (SetActiveVoice B) at mixer frame {handedAt}; B's own plan keeps running in the same voice");
            P($"   A: {aPeaks.Count} clicks in [0, {S + (long)(overlap * 0.85)}) - max offset from k x 24000: {aErr:F3} frames (A untouched by B's rate)");
            P($"   B in the overlap (1.09994, WSOLA): click spacing mean {Mean(overlapGaps):F1} expected {pb / 1.09994:F1} ({overlapGaps.Count} gaps)");
            P($"   B after the glide (1.0, pass-through): click spacing mean {Mean(finalGaps):F2} expected {pb} ({finalGaps.Count} gaps)");
            s.TrySourceFrameAtMixerFrame(bId, end - Sr, out double mappedLate);
            double delta = mappedLate - plan.SourceFramesBetween(S, end - Sr);
            P($"   B clicks vs plan.MixerFramesFor (ideal clock): max err {planErr:F1} frames ({bPeaks.Count} clicks; WSOLA aligns a transient within its 480-frame search)");
            P($"   B clicks vs session.TryMixerFrameAtSourceFrame: while stretched max err {sessErr:F1} frames (the ideal clock, as above); from the switch to the resampler on: max err {sessErrAfter:F2} frames (exact)");
            P($"   the switch out of the stretch moved B's clock by {delta:F2} frames (it continues from the stretch window's own continuation; the map follows the audio)");
            P($"   after the hand-off: ContentPositionFrames (B) vs the plan's ideal source frame at the audible frame: max err {posErr:F2} frames (= that switch offset + rounding); timeline Rate vs plan max err {rateErr:E1} ({posChecks} ticks)");
            P($"   steps seen on the app's 200 ms tick (TryReadVoiceRate(B).Changes):");
            P($"     #  seen-at-tick  landed-frame  plan-frame   factor");
            for (int i = 0; i < steps.Count; i++)
            {
                var (tick, r) = steps[i];
                long planFrame = i == 0 ? S : plan.StepFrame(i);
                P($"    {i,2}  {tick,12}  {r.LastChangeMixerFrame,12}  {planFrame,10}   {r.Factor:F6}{(i == 0 ? "  (B's first frame)" : "")}");
            }
            Close(s);
        }

        // ── the session rate (podcast speed) is unchanged ─────────────────────────────────────────────────────────────

        static void SessionRate()
        {
            P($"");
            P($"== session rate (podcast speed) x voice factor 1.0: unchanged from before E4");
            long frames = 20L * Sr;
            var input = Mix(Clicks(frames, 24_000, 0.4f), Sine(frames, 1000, 0.2f), Noise(frames, 0.05f));
            foreach (double rate in new[] { 1.0, 1.5, 0.75 })
            {
                long outFrames = (long)(frames / rate) - Sr;
                var today = new TodayWsola(new MemoryAudioSource(input, Ch), Sr, Ch);
                var after = new WsolaAudioSource(new MemoryAudioSource(input, Ch), Sr, Ch);
                var yt = DriveToday(today, outFrames, rate);
                var ya = DriveStage(after, outFrames, rate);
                double maxDiff = 0;
                long differ = 0;
                for (int i = 0; i < yt.Length; i++)
                {
                    double d = Math.Abs(yt[i] - ya[i]);
                    if (d > 0) differ++;
                    maxDiff = Math.Max(maxDiff, d);
                }
                double mapDiff = 0;
                for (long o = 0; o < outFrames; o += 4_801) mapDiff = Math.Max(mapDiff, Math.Abs(today.SourceFrameAt(o) - after.SourceFrameAt(o)));
                P($"   session rate {rate,-4}: stage output vs the pre-E4 stage: {differ} of {yt.Length} samples differ, max |diff| {maxDiff:E1}; position map max diff {mapDiff:E1} frames");
            }
            // through a session at 1.5: spacing, pitch, timeline rate
            {
                long srcFrames = 30L * Sr;
                var s = NewSession(out var ep, (int)(16L * Sr));
                Start(s, Mix(Clicks(srcFrames, 24_000, 0.3f), Sine(srcFrames, 1000, 0.1f)), srcFrames);
                s.SetRate(1.5);
                Play(s);
                int lat = s.Graph.Live.TotalLatencySamples;
                double posErr = 0, rateErr = 0;
                foreach (long at in new[] { 2L * Sr, 6L * Sr, 12L * Sr, 15L * Sr })
                {
                    PumpTo(s, at);
                    posErr = Math.Max(posErr, Math.Abs(s.ContentPositionFrames - 1.5 * (s.SampleClock - lat)));
                    if (s.TryReadTimeline(out var anchor)) rateErr = Math.Max(rateErr, Math.Abs(anchor.Rate - 1.5));
                }
                var y = ((NullAudioSink)ep.Sink).Captured.ToArray();
                Wav("session-rate-1.5.wav", y);
                var gaps = Gaps(Peaks(y, +1, 0.2f, 8_000, Sr, 15L * Sr));
                P($"   session at 1.5 (SetRate, no voice plan): click spacing mean {Mean(gaps):F1} expected {24_000 / 1.5:F1}; ContentPositionFrames vs 1.5 x audible max err {posErr:F1}; timeline Rate err {rateErr:E1}");
                Close(s);
            }
        }

        // ── E8: the beat loop ────────────────────────────────────────────────────────────────────────────────────────

        static void Loops()
        {
            P($"");
            P($"== E8 beat loop (120 bpm: beat 24000 frames). Block 1: 2 beats from 96000, 3 repeats. Block 2: 4 beats from 336000, halving until 528000, floor 1/8 beat.");
            P($"   source = a position code (L = sin, R = cos of 2 pi p / 480000): the output decodes to the source frame it plays");
            const int beat = 24_000;
            LoopBlock[] blocks = [LoopBlock.Repeat(96_000, 2 * beat, 3), LoopBlock.HalvingUntil(336_000, 4 * beat, 528_000, beat / 8)];
            var schedule = new BeatLoopSchedule(blocks, LoopEnd.Continue, Sr);
            int seam = schedule.SeamFrames;
            long frames = 700_000;
            var code = PosCode(frames + Sr, 480_000, 0.25f);
            var loop = new BeatLoopSource(new MemoryAudioSource(code, Ch), Ch, schedule);
            var y = ReadAll(loop, frames);
            Wav("loop-poscode.wav", y);
            var bounds = ScheduledBoundaries(blocks, schedule);
            ReportLoop("stage (BeatLoopSource over the source)", y, 0, frames, blocks, schedule, bounds, 480_000);

            // the seam: a 997 Hz sine through the same schedule
            var sine = Sine(frames + Sr, 997, 0.25f);
            var ys = ReadAll(new BeatLoopSource(new MemoryAudioSource(sine, Ch), Ch, schedule), frames);
            Wav("loop-sine997.wav", ys);
            double bound1 = 0.25 * 2 * Math.PI * 997 / Sr, bound2 = bound1 * 2 * Math.PI * 997 / Sr;
            double worst1 = 0, worst2 = 0, worstCut = 0;
            foreach (var (frame, loopStart, cont) in bounds)
            {
                var (d1, d2) = Rough(ys, frame - 2, frame + seam + 2);
                worst1 = Math.Max(worst1, d1);
                worst2 = Math.Max(worst2, d2);
                if (loopStart >= 0) worstCut = Math.Max(worstCut, Math.Abs(sine[loopStart * Ch] - sine[cont * Ch]));
            }
            P($"   seams ({seam} frames, equal power) on a 997 Hz sine (amp 0.25): max d1 {worst1:F4} (the sine's own {bound1:F4}, ratio {worst1 / bound1:F2}); max d2 {worst2:F5} (own {bound2:F5}, ratio {worst2 / bound2:F2}); a hard cut would step up to {worstCut:F3}");

            // loop-out: Stop after the last block
            var stopSchedule = new BeatLoopSchedule([LoopBlock.Repeat(96_000, 2 * beat, 2)], LoopEnd.Stop, Sr);
            var stop = new BeatLoopSource(new MemoryAudioSource(PosCode(frames, 480_000, 0.25f), Ch), Ch, stopSchedule);
            var yStop = ReadAll(stop, frames);
            long emitted = yStop.Length / Ch;
            long expectedStop = stopSchedule.Blocks[0].EndFrame + stopSchedule.SeamFrames;
            float lastAbs = Math.Max(Math.Abs(yStop[(emitted - 1) * Ch]), Math.Abs(yStop[(emitted - 1) * Ch + 1]));
            P($"   Stop (loop-out, 2 beats x 2 repeats from 96000): emitted {emitted} frames (block end {stopSchedule.Blocks[0].EndFrame} + one seam = {expectedStop}); Exhausted={stop.Exhausted}; last frame |x| {lastAbs:E1}");

            // through a session: SetVoiceLoop on the playing primary voice, at 1.0 and at 1.09994
            foreach (double factor in new[] { 1.0, 1.09994 })
            {
                long srcFrames = frames + 2L * Sr;
                long run = (long)(frames / factor);
                var s = NewSession(out var ep, (int)(run + Sr));
                Start(s, PosCode(srcFrames, 480_000, 0.25f), srcFrames);
                if (factor != 1.0) s.SetVoiceRate(s.PrimaryVoiceIdValue, factor);
                if (!s.SetVoiceLoop(s.PrimaryVoiceIdValue, schedule)) throw new InvalidOperationException("SetVoiceLoop refused");
                Play(s);
                PumpTo(s, run);
                s.TryReadVoiceLoop(s.PrimaryVoiceIdValue, out var reading);
                var yc = ((NullAudioSink)ep.Sink).Captured.ToArray();
                int lat = s.Graph.Live.TotalLatencySamples;
                if (factor == 1.0)
                {
                    Wav("loop-session.wav", yc);
                    ReportLoop($"session at 1.0 (SetVoiceLoop; latency {lat})", yc, lat, run, blocks, schedule, bounds, 480_000);
                }
                else
                {
                    Wav("loop-session-1.09994.wav", yc);
                    // the loop sits before the rate stage: a boundary at source frame B is heard at output B / factor (WSOLA: within a hop)
                    var heard = DecodeAll(yc, lat, run, 480_000);
                    double worst = 0;
                    var found = new List<string>();
                    foreach (var (frame, _, _) in bounds)
                    {
                        long o = (long)Math.Round(frame / factor);
                        long jump = FindJump(heard, o - 2 * Block, o + 2 * Block);
                        found.Add(jump < 0 ? "-" : (jump - o).ToString(CultureInfo.InvariantCulture));
                        if (jump >= 0) worst = Math.Max(worst, Math.Abs(jump - o));
                    }
                    P($"   session at 1.09994 with the loop: boundaries heard at source/1.09994 + [{string.Join(", ", found)}] frames (max {worst}; WSOLA aligns within its 480-frame search)");
                }
                P($"     TryReadVoiceLoop: phase {reading.Phase}, block {reading.Block}, boundaries {reading.Boundaries} (scheduled {bounds.Count}), last boundary at source frame {reading.LastBoundaryFrame}");
                Close(s);
            }
        }

        /// <summary>(boundary source frame, loop start frame or -1 at a block end, the frame a hard cut would have continued from).</summary>
        static List<(long Frame, long LoopStart, long Continuation)> ScheduledBoundaries(LoopBlock[] blocks, BeatLoopSchedule schedule)
        {
            var list = new List<(long, long, long)>();
            foreach (var b in blocks)
            {
                long end = b.EndFrame, at = b.LoopPointFrame;
                int prevLen = b.LengthFrames;
                for (int k = 1; at < end; k++)
                {
                    list.Add((at, b.StartFrame, b.StartFrame + prevLen));
                    prevLen = b.RepeatLength(k);
                    at += prevLen;
                }
                list.Add((end, -1, -1));
            }
            return list;
        }

        /// <summary>The source frame the schedule plays at original position <paramref name="p"/> (slip: output frame = original frame).</summary>
        static long Expected(LoopBlock[] blocks, long p)
        {
            foreach (var b in blocks)
            {
                if (p < b.LoopPointFrame) return p;
                if (p >= b.EndFrame) continue;
                long rs = b.LoopPointFrame;
                for (int k = 1; ; k++)
                {
                    int len = b.RepeatLength(k);
                    if (p < rs + len) return b.StartFrame + (p - rs);
                    rs += len;
                }
            }
            return p;
        }

        static void ReportLoop(string what, float[] y, int lat, long frames, LoopBlock[] blocks, BeatLoopSchedule schedule,
            List<(long Frame, long LoopStart, long Continuation)> bounds, double period)
        {
            int seam = schedule.SeamFrames;
            var heard = DecodeAll(y, lat, frames, period);
            double worst = 0, worstAfter = 0;
            long checkedFrames = 0;
            for (long o = Sr / 10; o < frames - 1; o++)
            {
                bool inSeam = false;
                foreach (var b in bounds) if (o >= b.Frame && o < b.Frame + seam) { inSeam = true; break; }
                if (inSeam) continue;
                double err = Wrap(heard[o] - Expected(blocks, o), period);
                worst = Math.Max(worst, Math.Abs(err));
                if (o >= blocks[^1].EndFrame + seam) worstAfter = Math.Max(worstAfter, Math.Abs(err));
                checkedFrames++;
            }
            var detected = new List<long>();
            for (long o = Sr / 10; o < frames - 1; o++)
                if (Math.Abs(Wrap(heard[o] - heard[o - 1] - 1, period)) > 0.5 && (detected.Count == 0 || o - detected[^1] > seam)) detected.Add(o);
            var offsets = new List<string>();
            int matched = 0;
            foreach (var b in bounds)
            {
                long hit = detected.FirstOrDefault(d => Math.Abs(d - b.Frame) <= seam, -1);
                offsets.Add(hit < 0 ? "-" : (hit - b.Frame).ToString(CultureInfo.InvariantCulture));
                if (hit >= 0) matched++;
            }
            var halving = new List<long>();
            var b2 = bounds.Where(b => b.Frame >= blocks[^1].LoopPointFrame).Select(b => b.Frame).ToList();
            for (int i = 1; i < b2.Count; i++) halving.Add(b2[i] - b2[i - 1]);
            P($"   {what}:");
            P($"     heard vs schedule outside the seams: max err {worst:F3} frames over {checkedFrames} frames; after the last block (continue): max err {worstAfter:F3} (the original's own frame)");
            P($"     boundaries: scheduled [{string.Join(", ", bounds.Select(b => b.Frame))}]");
            P($"                 detected - scheduled = [{string.Join(", ", offsets)}] ({matched}/{bounds.Count} found, {detected.Count} jumps detected)");
            P($"     halving repeats (block 2): [{string.Join(", ", halving)}] frames (expected 48000, 24000, 12000, 6000, 3000, 3000)");
        }

        static double[] DecodeAll(float[] y, int lat, long frames, double period)
        {
            var heard = new double[frames];
            for (long o = 0; o < frames; o++)
            {
                long i = o + lat;
                if ((i + 1) * Ch > y.Length) break;
                double a = Math.Atan2(y[i * Ch], y[i * Ch + 1]);
                if (a < 0) a += 2 * Math.PI;
                heard[o] = a / (2 * Math.PI) * period;
            }
            return heard;
        }

        static long FindJump(double[] heard, long from, long to)
        {
            for (long o = Math.Max(1, from); o < Math.Min(heard.Length, to); o++)
                if (Math.Abs(Wrap(heard[o] - heard[o - 1], 480_000)) > 5) return o;
            return -1;
        }

        static double Wrap(double d, double period)
        {
            d %= period;
            if (d > period / 2) d -= period;
            if (d < -period / 2) d += period;
            return d;
        }

        // ── CPU and allocation ───────────────────────────────────────────────────────────────────────────────────────

        static void Cpu()
        {
            P($"");
            P($"== CPU per 480-frame block (Debug build, JIT; min of 5 trials x 6000 blocks = 60 s of audio) and managed bytes allocated while rendering");
            long frames = 62L * Sr;
            var input = Mix(Clicks(frames, 24_000, 0.4f), Sine(frames, 440, 0.2f), Noise(frames, 0.05f));
            var buffer = new float[Block * Ch];
            Time("rate 1.0, no plan     TODAY (pre-E4 stage)", () => new TodayWsola(new MemoryAudioSource(input, Ch), Sr, Ch), (st, m) => { var t = (TodayWsola)st; t.Rate = 1.0; return t.Read(buffer, Ch); });
            Time("rate 1.0, no plan     AFTER", () => new WsolaAudioSource(new MemoryAudioSource(input, Ch), Sr, Ch), (st, m) => { var w = (WsolaAudioSource)st; w.BeginBlock(1.0, m); return w.Read(buffer, Ch); });
            Time("session 1.5, no plan  TODAY (pre-E4 stage)", () => new TodayWsola(new MemoryAudioSource(input, Ch), Sr, Ch), (st, m) => { var t = (TodayWsola)st; t.Rate = 1.5; return t.Read(buffer, Ch); }, 1.5);
            Time("session 1.5, no plan  AFTER", () => new WsolaAudioSource(new MemoryAudioSource(input, Ch), Sr, Ch), (st, m) => { var w = (WsolaAudioSource)st; w.BeginBlock(1.5, m); return w.Read(buffer, Ch); }, 1.5);
            foreach (double f in new[] { 1.00008, 0.901696, 1.09994 })
                Time($"factor {f,-9}      AFTER ({(Math.Abs(f - 1) < WsolaAudioSource.ResampleBelow ? "resampler" : "WSOLA")})", () => new WsolaAudioSource(new MemoryAudioSource(input, Ch), Sr, Ch, VoiceRatePlan.Constant(f)),
                    (st, m) => { var w = (WsolaAudioSource)st; w.BeginBlock(1.0, m); return w.Read(buffer, Ch); }, f);

            // the whole session block (mixer + voice chain + master chain), RenderBlock as the render thread calls it
            foreach (double f in new[] { 1.0, 1.00008, 1.09994 })
            {
                var s = NewSession(out _, 0);
                Start(s, input, frames);
                if (f != 1.0) s.SetVoiceRate(s.PrimaryVoiceIdValue, f);
                Play(s);
                PumpTo(s, Sr);
                double best = double.MaxValue;
                long bytes = 0;
                for (int trial = 0; trial < 3; trial++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 1000; i++) s.RenderBlock(Block);
                    long t1 = Stopwatch.GetTimestamp();
                    bytes = Math.Max(bytes, GC.GetAllocatedBytesForCurrentThread() - a0);
                    best = Math.Min(best, (t1 - t0) * 1e9 / Stopwatch.Frequency / 1000);
                }
                P($"   session RenderBlock, factor {f,-9}: {best,9:F0} ns/block, {bytes} bytes allocated over 3000 blocks");
                Close(s);
            }
        }

        static void Time(string what, Func<IAudioSource> make, Func<IAudioSource, long, int> block, double rate = 1.0)
        {
            double best = double.MaxValue;
            long bytes = 0;
            for (int trial = 0; trial < 5; trial++)
            {
                var stage = make();
                for (int i = 0; i < 20; i++) block(stage, (long)i * Block);   // warm
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                long m = 20L * Block;
                for (int i = 0; i < 6000; i++, m += Block) block(stage, m);
                long t1 = Stopwatch.GetTimestamp();
                bytes = Math.Max(bytes, GC.GetAllocatedBytesForCurrentThread() - a0);
                best = Math.Min(best, (t1 - t0) * 1e9 / Stopwatch.Frequency / 6000);
            }
            P($"   {what,-44}: {best,9:F0} ns/block, {bytes} bytes allocated over 6000 blocks (rate {rate})");
        }

        // ── drives ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Drive a stage as PcmAudioSession.RenderBlock + MixVoice.MixInto do: BeginBlock, then reads until the block is full.</summary>
        static float[] DriveStage(WsolaAudioSource stage, long frames, double sessionRate = 1.0, Action<long>? afterBlock = null)
        {
            var y = new float[frames * Ch];
            long m = 0;
            while (m < frames)
            {
                int want = (int)Math.Min(Block, frames - m);
                stage.BeginBlock(sessionRate, m);
                var span = y.AsSpan((int)(m * Ch), want * Ch);
                int got = 0;
                while (got < want)
                {
                    int n = stage.Read(span[(got * Ch)..], Ch);
                    if (n <= 0) break;
                    got += n;
                    if (stage.Exhausted) break;
                }
                m += want;
                afterBlock?.Invoke(m);
                if (got < want) break;
            }
            return y;
        }

        static float[] DriveToday(TodayWsola stage, long frames, double rate)
        {
            var y = new float[frames * Ch];
            long m = 0;
            while (m < frames)
            {
                int want = (int)Math.Min(Block, frames - m);
                stage.Rate = rate;
                var span = y.AsSpan((int)(m * Ch), want * Ch);
                int got = 0;
                while (got < want)
                {
                    int n = stage.Read(span[(got * Ch)..], Ch);
                    if (n <= 0) break;
                    got += n;
                    if (stage.Exhausted) break;
                }
                m += want;
                if (got < want) break;
            }
            return y;
        }

        static float[] ReadAll(IAudioSource source, long frames)
        {
            var y = new float[frames * Ch];
            long o = 0;
            while (o < frames)
            {
                int want = (int)Math.Min(Block, frames - o);
                int n = source.Read(y.AsSpan((int)(o * Ch), want * Ch), Ch);
                if (n <= 0) break;
                o += n;
            }
            return o * Ch == y.Length ? y : y.AsSpan(0, (int)(o * Ch)).ToArray();
        }

        static PcmAudioSession NewSession(out HeadlessAudioEndpoint endpoint, int captureFrames)
        {
            var format = new MixFormat(Sr, Ch);
            endpoint = new HeadlessAudioEndpoint(format, captureFrames);
            return new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, 1024, driveWithOwnThread: false, endpoint);
        }

        static void Start(PcmAudioSession s, float[] voice, long frames)
            => s.SetVoice(new MemoryAudioSource(voice, Ch), TimeSpan.FromSeconds((double)frames / Sr), frames, NormMode.Off, -18f, 1f);

        static void Play(PcmAudioSession s)
        {
            s.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
            _ = s.PlayAsync();
            for (int i = 0; i < 16 && s.SampleClock == 0; i++) s.PumpAudio(Block);
            if (s.SampleClock == 0) throw new InvalidOperationException("the session never rendered");
        }

        static void Close(PcmAudioSession s) => s.DisposeAsync().AsTask().GetAwaiter().GetResult();

        static void PumpTo(PcmAudioSession s, long mixerFrame)
        {
            while (s.SampleClock < mixerFrame)
            {
                long before = s.SampleClock;
                s.PumpAudio(Block);
                if (s.SampleClock == before) throw new InvalidOperationException($"the session stalled at {before} ({s.CurrentState})");
            }
        }

        // ── signals ──────────────────────────────────────────────────────────────────────────────────────────────────

        static float[] Clicks(long frames, int period, float amp)
        {
            var x = new float[frames * Ch];
            for (long f = period; f < frames; f += period)
                for (int c = 0; c < Ch; c++) x[f * Ch + c] = amp;
            return x;
        }

        static float[] Sine(long frames, double hz, float amp)
        {
            var x = new float[frames * Ch];
            double w = 2 * Math.PI * hz / Sr;
            for (long f = 0; f < frames; f++)
            {
                float v = amp * (float)Math.Sin(w * f);
                x[f * Ch] = v;
                x[f * Ch + 1] = v;
            }
            return x;
        }

        static float[] Sweep(long frames, double f0, double f1, float amp)
        {
            var x = new float[frames * Ch];
            double k = Math.Log(f1 / f0), T = (double)frames / Sr;
            for (long f = 0; f < frames; f++)
            {
                double t = (double)f / Sr;
                float v = amp * (float)Math.Sin(2 * Math.PI * f0 * T / k * (Math.Exp(t / T * k) - 1));
                x[f * Ch] = v;
                x[f * Ch + 1] = v;
            }
            return x;
        }

        static float[] PosCode(long frames, double period, float amp)
        {
            var x = new float[frames * Ch];
            for (long f = 0; f < frames; f++)
            {
                double a = 2 * Math.PI * f / period;
                x[f * Ch] = amp * (float)Math.Sin(a);
                x[f * Ch + 1] = amp * (float)Math.Cos(a);
            }
            return x;
        }

        static float[] Noise(long frames, float amp)
        {
            var x = new float[frames * Ch];
            uint state = 0x2545F491;
            for (int i = 0; i < x.Length; i++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                x[i] = amp * ((state & 0xFFFF) / 32768f - 1f);
            }
            return x;
        }

        static float[] Mix(params float[][] parts)
        {
            var x = new float[parts[0].Length];
            foreach (var p in parts)
                for (int i = 0; i < x.Length; i++) x[i] += p[i];
            return x;
        }

        // ── measurements ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Peaks of sign·x (channel 0) above <paramref name="thr"/>: one per cluster of <paramref name="refractory"/> frames,
        /// refined to a sub-frame time by a parabola through the largest sample and its neighbours.</summary>
        static List<double> Peaks(float[] y, int sign, float thr, int refractory, long from, long to)
        {
            var list = new List<double>();
            long n = Math.Min(y.Length / Ch, to);
            long f = Math.Max(1, from);
            while (f < n - 1)
            {
                if (sign * y[f * Ch] < thr) { f++; continue; }
                long best = f;
                float bv = sign * y[f * Ch];
                long end = Math.Min(n - 1, f + refractory);
                for (long g = f; g < end; g++)
                {
                    float v = sign * y[g * Ch];
                    if (v > bv) { bv = v; best = g; }
                }
                double a = sign * y[(best - 1) * Ch], c = sign * y[(best + 1) * Ch], den = a - 2 * bv + c;
                list.Add(best + (den < 0 ? 0.5 * (a - c) / den : 0));
                f = end;
            }
            return list;
        }

        static List<double> Gaps(List<double> peaks)
        {
            var g = new List<double>();
            for (int i = 1; i < peaks.Count; i++) g.Add(peaks[i] - peaks[i - 1]);
            return g;
        }

        static double Mean(List<double> v) => v.Count == 0 ? double.NaN : v.Average();
        static double Min(List<double> v) => v.Count == 0 ? double.NaN : v.Min();
        static double Max(List<double> v) => v.Count == 0 ? double.NaN : v.Max();

        /// <summary>Frequency from positive-going zero crossings of channel 0 (linearly interpolated).</summary>
        static double Pitch(float[] y, long from, long to)
        {
            double first = -1, last = -1;
            int count = 0;
            for (long f = Math.Max(1, from); f < Math.Min(to, y.Length / Ch); f++)
            {
                float a = y[(f - 1) * Ch], b = y[f * Ch];
                if (a < 0 && b >= 0)
                {
                    double t = f - 1 + a / (a - b);
                    if (first < 0) first = t;
                    last = t;
                    count++;
                }
            }
            return count > 1 ? (count - 1) * (double)Sr / (last - first) : 0;
        }

        /// <summary>Max first and second difference of channel 0 over [from, to).</summary>
        static (double D1, double D2) Rough(float[] y, long from, long to)
        {
            double d1 = 0, d2 = 0;
            for (long f = Math.Max(2, from); f < Math.Min(to, y.Length / Ch); f++)
            {
                double a = y[f * Ch], b = y[(f - 1) * Ch], c = y[(f - 2) * Ch];
                d1 = Math.Max(d1, Math.Abs(a - b));
                d2 = Math.Max(d2, Math.Abs(a - 2 * b + c));
            }
            return (d1, d2);
        }

        /// <summary>The lowest RMS of channel 0 over <paramref name="window"/>-frame windows (stepped by a quarter window) in [from, to).</summary>
        static double MinRms(float[] y, long from, long to, int window)
        {
            double min = double.MaxValue;
            for (long w = Math.Max(0, from); w + window <= Math.Min(to, y.Length / Ch); w += window / 4)
            {
                double sum = 0;
                for (long f = w; f < w + window; f++) sum += (double)y[f * Ch] * y[f * Ch];
                min = Math.Min(min, Math.Sqrt(sum / window));
            }
            return min;
        }

        static void Wav(string name, float[] y)
        {
            int frames = y.Length / Ch;
            using var file = File.Create(Path.Combine(s_dir, name));
            using var w = new BinaryWriter(file);
            int bytes = frames * Ch * 4;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)Ch); w.Write(Sr); w.Write(Sr * Ch * 4); w.Write((short)(Ch * 4)); w.Write((short)32);
            w.Write("data"u8); w.Write(bytes);
            foreach (float v in y.AsSpan(0, frames * Ch)) w.Write(v);
        }

        /// <summary>The voice stage exactly as it was before #48 E4 (fluent-gpu e3330d9f, src/FluentGpu.Engine/Media/Playback/Audio/
        /// WsolaAudioSource.cs; comments and the members the bench does not call removed): the CPU baseline and the reference the
        /// stage without a plan must stay bit-identical to.</summary>
        sealed class TodayWsola : IAudioSource
        {
            private readonly IAudioSource _inner;
            private readonly int _channels, _hop, _search;
            private readonly float[] _input, _tail, _output;
            private long _inputStart, _outputFrames;
            private int _inputFrames, _ready, _offset;
            private double _contentFrame, _hopRate;
            private bool _hasTail;
            private double _rate = 1;
            private readonly Mapping[] _mapping = new Mapping[4096];
            private long _mappingCount, _lastMappingOutput;
            private double _lastMappingSource, _lastMappingRate;
            private struct Mapping { public long Version, Output; public double Source, Rate; }

            public TodayWsola(IAudioSource inner, int sampleRate, int channels)
            {
                _inner = inner;
                _channels = channels;
                _hop = Math.Max(1, sampleRate / 50);
                _search = Math.Max(1, sampleRate / 100);
                _input = new float[(_hop * 8 + _search * 2) * channels];
                _tail = new float[_hop * channels];
                _output = new float[_hop * channels];
                Reset(inner.PositionFrames);
            }

            public static double ClampRate(double rate) => double.IsFinite(rate) ? Math.Clamp(rate, 0.5, 3) : 1;
            public double Rate { get => _rate; set => _rate = ClampRate(value); }
            public long InitialSourceFrame { get; private set; }
            public long PositionFrames => (long)_contentFrame;
            public bool Exhausted => _ready == 0 && _inner.Exhausted && _contentFrame >= _inputStart + _inputFrames;
            public GaplessInfo Gapless => _inner.Gapless;
            public ReplayGainInfo Loudness => _inner.Loudness;

            public void Reset(long sourceFrame)
            {
                InitialSourceFrame = _inputStart = Math.Max(0, sourceFrame);
                _contentFrame = _inputStart;
                _inputFrames = _ready = _offset = 0;
                _outputFrames = 0;
                Volatile.Write(ref _mappingCount, 0);
                _hasTail = false;
            }

            public int Read(Span<float> dst, int channels)
            {
                if (channels != _channels) throw new ArgumentException("Channel count differs from the voice format.", nameof(channels));
                int wanted = dst.Length / channels, written = 0;
                if (TryEnterPassThrough())
                {
                    int got = _inner.Read(dst, channels);
                    if (got > 0)
                    {
                        RecordMapping(_outputFrames, _contentFrame, 1);
                        _outputFrames += got;
                        _contentFrame += got;
                        _inputStart += got;
                    }
                    return got;
                }
                while (written < wanted)
                {
                    if (_ready == 0 && !ProduceHop()) break;
                    int take = Math.Min(wanted - written, _ready);
                    _output.AsSpan(_offset * channels, take * channels).CopyTo(dst[(written * channels)..]);
                    RecordMapping(_outputFrames, _contentFrame, _hopRate);
                    _contentFrame += take * _hopRate;
                    if (_inner.Exhausted) _contentFrame = Math.Min(_contentFrame, _inputStart + _inputFrames);
                    _outputFrames += take;
                    _offset += take;
                    _ready -= take;
                    written += take;
                }
                return written;
            }

            private bool TryEnterPassThrough()
            {
                if (_rate != 1 || _ready != 0 || _hasTail) return false;
                if (_inputFrames == 0) return true;
                long next = _inputStart + _inputFrames;
                if ((long)Math.Floor(_contentFrame) != next) return false;
                _inputStart = next;
                _inputFrames = 0;
                _contentFrame = next;
                return true;
            }

            private bool ProduceHop()
            {
                Compact();
                long ideal = (long)Math.Floor(_contentFrame);
                bool unity = _rate == 1;
                bool returnBlend = unity && _hasTail;
                long wantedEnd = ideal + (unity ? _hop : _hop * 2 + _search);
                FillUntil(wantedEnd);
                long end = _inputStart + _inputFrames;
                if (!_inner.Exhausted && end < wantedEnd)
                {
                    if (!unity || end <= ideal) return false;
                }
                if (_contentFrame >= end) return false;
                _hopRate = _rate;
                int count = _inner.Exhausted
                    ? Math.Min(_hop, (int)Math.Ceiling((end - _contentFrame) / _rate)) : _hop;
                if (unity) count = (int)Math.Min(count, end - ideal);
                if (count <= 0) return false;
                long chosen = unity || !_hasTail ? ideal : FindMatch(ideal, end);
                int ramp = returnBlend ? count : _hop;
                for (int f = 0; f < count; f++)
                {
                    float blend = (float)f / ramp;
                    for (int c = 0; c < _channels; c++)
                    {
                        float next = Sample(chosen + f, c);
                        _output[f * _channels + c] = !_hasTail ? next
                            : _tail[f * _channels + c] * (1 - blend) + next * blend;
                    }
                }
                if (!unity)
                    for (int f = 0; f < _hop; f++)
                        for (int c = 0; c < _channels; c++)
                            _tail[f * _channels + c] = Sample(chosen + _hop + f, c);
                _hasTail = !unity;
                _ready = count;
                _offset = 0;
                return true;
            }

            private long FindMatch(long ideal, long end)
            {
                long lo = Math.Max(_inputStart, ideal - _search);
                long hi = Math.Min(ideal + _search, Math.Max(lo, end - _hop * 2));
                long best = Math.Clamp(ideal, lo, hi);
                double score = Correlation(best);
                const int stride = 4;
                for (long p = lo; p <= hi; p += stride)
                {
                    double s = Correlation(p);
                    if (s > score + 1e-12) { score = s; best = p; }
                }
                long fineLo = Math.Max(lo, best - stride), fineHi = Math.Min(hi, best + stride);
                for (long p = fineLo; p <= fineHi; p++)
                {
                    double s = Correlation(p);
                    if (s > score + 1e-12) { score = s; best = p; }
                }
                return best;
            }

            private double Correlation(long start)
            {
                double dot = 0, aa = 0, bb = 0;
                for (int f = 0; f < _hop; f += 4)
                    for (int c = 0; c < _channels; c++)
                    {
                        double a = _tail[f * _channels + c], b = Sample(start + f, c);
                        dot += a * b; aa += a * a; bb += b * b;
                    }
                return aa * bb <= 1e-20 ? 0 : dot / Math.Sqrt(aa * bb);
            }

            private float Sample(long frame, int channel)
            {
                long index = frame - _inputStart;
                return index >= 0 && index < _inputFrames ? _input[(int)index * _channels + channel] : 0;
            }

            private void Compact()
            {
                long keepFrom = Math.Max(InitialSourceFrame, (long)_contentFrame - _search - _hop);
                int drop = (int)Math.Clamp(keepFrom - _inputStart, 0, _inputFrames);
                if (drop == 0) return;
                _input.AsSpan(drop * _channels, (_inputFrames - drop) * _channels).CopyTo(_input);
                _inputStart += drop;
                _inputFrames -= drop;
            }

            private void FillUntil(long end)
            {
                while (_inputStart + _inputFrames < end && !_inner.Exhausted)
                {
                    int room = _input.Length / _channels - _inputFrames;
                    int take = (int)Math.Min(room, end - _inputStart - _inputFrames);
                    if (take <= 0) break;
                    int got = _inner.Read(_input.AsSpan(_inputFrames * _channels, take * _channels), _channels);
                    if (got <= 0) break;
                    _inputFrames += got;
                }
            }

            private void RecordMapping(long output, double source, double rate)
            {
                long sequence = _mappingCount;
                if (sequence > 0 && rate == _lastMappingRate &&
                    Math.Abs(source - (_lastMappingSource + (output - _lastMappingOutput) * rate)) < 0.00001) return;
                _lastMappingOutput = output;
                _lastMappingSource = source;
                _lastMappingRate = rate;
                ref var slot = ref _mapping[sequence & (_mapping.Length - 1)];
                Interlocked.Exchange(ref slot.Version, sequence * 2 + 1);
                slot.Output = output; slot.Source = source; slot.Rate = rate;
                Volatile.Write(ref slot.Version, sequence * 2 + 2);
                Volatile.Write(ref _mappingCount, sequence + 1);
            }

            public double SourceFrameAt(double outputFrame)
            {
                long count = Volatile.Read(ref _mappingCount);
                if (count == 0 || outputFrame <= 0) return InitialSourceFrame;
                double oldest = InitialSourceFrame;
                for (long sequence = count - 1, limit = Math.Max(0, count - _mapping.Length); sequence >= limit; sequence--)
                {
                    ref var slot = ref _mapping[sequence & (_mapping.Length - 1)];
                    long version = Volatile.Read(ref slot.Version);
                    if (version != sequence * 2 + 2) continue;
                    long output = slot.Output;
                    double source = slot.Source, rate = slot.Rate;
                    Thread.MemoryBarrier();
                    if (Volatile.Read(ref slot.Version) != version) continue;
                    oldest = source;
                    if (outputFrame >= output)
                        return Math.Min(_contentFrame, source + (outputFrame - output) * rate);
                }
                return oldest;
            }
        }
    }
}
