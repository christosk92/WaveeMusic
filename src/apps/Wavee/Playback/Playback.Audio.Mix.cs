// ── Playback/Playback.Audio.Mix.cs — the pump half of #48 Mix (plan §3.6, §3.6a item 4) ───────────────────────────────
//
// The host's planner (Playback.Mix.cs) hands the pump ONE timing for the playing pair (SetMix). From it:
//
//   prepare   B opens at the in-point (PrepareContext.StartSourceFrame, engine E3); the achieved start is kept
//   endgame   the ask window opens 8 s before the MIX-OUT, not the file's end (fade 0)
//   schedule  within 2 s of the mix-out, at an EXACT mixer frame: B joins with its curve envelope and automation,
//             A gets its curve envelope and automation, one AudioTransitionGate shared by both (engines E1, E2)
//   announce  when A is over (startFrame + outFrames, decision D14 (b)): the gapless join's identity swap, B already
//             InStart + OutDuration in
//   cancel    a seek, a next-row change, a device rebuild before the announce: the gate is cancelled, A is put back
//             to unity with no automation, B is silenced and its bytes cut (the gapless abandon)
//   refuse    a cross-kind slot or a tick that came too late: the base hand-off runs, the duration goes back to the file's
//
// With no mix planned every existing path is unchanged: the hooks in Playback.Audio.cs test MixArmed first.

using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>A join scheduled closer than this to its start frame is refused (the commands might land late).</summary>
        const int MixMinLeadMs = 300;
        /// <summary>A join is committed this far ahead of its start frame (the pump ticks every 200 ms).</summary>
        const int MixCommitLeadMs = 2_000;

        // All under s_gate.
        static MixTiming s_mixPlan;
        static EntityId s_mixActive, s_mixNext;
        static EntityRef s_mixNextRow;
        static bool s_mixRefused;
        static uint s_mixDurationPosted;
        /// <summary>The prepared slot's achieved start (ms into B; 0 when its source could not seek).</summary>
        static long s_prepStartMs;
        /// <summary>The scheduled join is a MIX (its gate, A's voice, B's anchor after the announce).</summary>
        static bool s_joinMix;
        static AudioTransitionGate? s_mixGate;
        static long s_mixOutVoice, s_joinStartMs;

        static long s_mMixSaved, s_mMixAuto, s_mMixRefused, s_mMixCancelled;
        public static long MixSavedCount => Interlocked.Read(ref s_mMixSaved);
        public static long MixAutoCount => Interlocked.Read(ref s_mMixAuto);
        public static long MixRefusedCount => Interlocked.Read(ref s_mMixRefused);
        public static long MixCancelledCount => Interlocked.Read(ref s_mMixCancelled);

        /// <summary>The live mix window, for the player bar (UI thread reads; written by the pump): the mixer frame the
        /// overlap started at, 0 when none is sounding.</summary>
        public static bool MixSounding => Volatile.Read(ref s_mixSounding) != 0;
        static int s_mixSounding;

        /// <summary>UI thread (the host's follow): the timing for the pair (<paramref name="active"/> →
        /// <paramref name="next"/>). A refused timing clears the plan. When the in-point of a slot already prepared for
        /// <paramref name="next"/> changed, the slot is rebuilt at the new offset. The playing row's effective duration
        /// (its audible end, D14 (b)) is posted to the reducer while a mix is planned, and the file's again when not.</summary>
        public static void SetMix(EntityId active, EntityId next, EntityRef nextRow, in MixTiming timing)
        {
            bool reprepare = false;
            uint epoch;
            long postDuration = -1;
            lock (s_gate)
            {
                bool samePair = s_mixActive == active && s_mixNext == next;
                if (!samePair) s_mixRefused = false;
                if (s_joinPending && s_joinMix) return;            // a join is scheduled: its timing stands (a cancel re-plans)
                var before = s_mixPlan;
                s_mixPlan = timing;
                s_mixActive = active;
                s_mixNext = next;
                s_mixNextRow = nextRow;
                epoch = s_loadEpoch;
                if (timing.Mix && !next.IsEmpty && (s_prepId == next || s_prepBuildingId == next)
                    && before.Mix && before.InStartMs != timing.InStartMs)
                    reprepare = true;
                if (active == s_id && !active.IsEmpty)
                {
                    uint wanted = timing.Mix && !s_mixRefused ? timing.AudibleEndMs : 0;
                    if (wanted != s_mixDurationPosted)
                    {
                        postDuration = wanted != 0 ? wanted : s_activeDurMs;
                        s_mixDurationPosted = wanted;
                    }
                }
                if (reprepare) s_prepBuildingId = default;
            }
            if (postDuration > 0) PostSignal(AudioSignal.Duration, epoch, postDuration);
            if (reprepare) { Log.Info("audio", "[mix] in-point changed; preparing the next row again"); Prepare(nextRow, next); }
        }

        /// <summary>Under s_gate: a mix is planned for the deck's row and not refused.</summary>
        static bool MixArmedLocked => s_mixPlan.Mix && !s_mixRefused && s_mixActive == s_id && !s_id.IsEmpty;

        /// <summary>The source frame the prepare of <paramref name="id"/> should start at (0 unless it is the planned B).</summary>
        static long MixStartFramesFor(EntityId id, int rate)
        {
            lock (s_gate)
                return s_mixPlan.Mix && s_mixNext == id && s_mixPlan.InStartMs > 0
                    ? GaplessJoinClock.MsToFrames(s_mixPlan.InStartMs, rate) : 0;
        }

        /// <summary>The endgame's boundary and fade: the mix-out with no fade while a mix is armed, else the arguments.</summary>
        static void MixEndgame(ref long boundaryMs, ref int fadeMs)
        {
            lock (s_gate)
            {
                if (!MixArmedLocked) return;
                boundaryMs = s_mixPlan.OutStartMs;
                fadeMs = 0;
            }
        }

        /// <summary>The tick's commit step while a mix is armed. True = the mix owns this boundary (the base
        /// <see cref="HandOffAt"/> switch is skipped).</summary>
        static bool TickMix(PcmAudioSession sess, IPreparedItem item)
        {
            MixTiming plan;
            long startFrame, clock = sess.SampleClock;
            int rate = sess.Format.SampleRate;
            lock (s_gate)
            {
                if (!MixArmedLocked) return false;
                if (s_joinPending) return true;
                if (s_prepId != s_mixNext) return false;          // the slot holds another row: the base path decides
                plan = s_mixPlan;
                startFrame = s_anchorClock + GaplessJoinClock.MsToFrames(Math.Max(0, plan.OutStartMs - s_anchorPlayheadMs), rate);
            }
            if (!s_prepOverlap) { RefuseMix(MixWhyNot.CrossKind); return false; }
            if (s_clockStale) return true;                         // wait for a clock this track owns
            long lead = startFrame - clock;
            if (lead < GaplessJoinClock.MsToFrames(MixMinLeadMs, rate)) { RefuseMix(MixWhyNot.Late); return false; }
            if (lead > GaplessJoinClock.MsToFrames(MixCommitLeadMs, rate)) return true;
            if (!ScheduleMix(sess, item, in plan, startFrame)) RefuseMix(MixWhyNot.Late);
            return true;
        }

        static void RefuseMix(MixWhyNot why)
        {
            uint epoch;
            long dur;
            lock (s_gate)
            {
                if (s_mixRefused) return;
                s_mixRefused = true;
                epoch = s_loadEpoch;
                dur = s_activeDurMs;
                bool posted = s_mixDurationPosted != 0;
                s_mixDurationPosted = 0;
                if (!posted) dur = 0;
            }
            Interlocked.Increment(ref s_mMixRefused);
            Log.Info("audio", "[mix] refused why=" + why + " (the base hand-off runs)");
            if (dur > 0) PostSignal(AudioSignal.Duration, epoch, dur);
        }

        /// <summary>Arm the join at <paramref name="startFrame"/>: B's voice with its curve envelope and automation, A's
        /// envelope and automation, one gate. The app's identity does not change until A is over.</summary>
        static bool ScheduleMix(PcmAudioSession sess, IPreparedItem item, in MixTiming plan, long startFrame)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate)) return false;
            int rate = sess.Format.SampleRate;
            int outFrames = (int)GaplessJoinClock.MsToFrames(plan.OutDurationMs, rate);
            int inFrames = (int)GaplessJoinClock.MsToFrames(plan.InDurationMs, rate);
            if (outFrames <= 0 || inFrames <= 0) return false;
            MixRender.Build(in plan, rate, startFrame, outFrames, inFrames,
                out var outTable, out var inTable, out var outAuto, out var inAuto);
            var gate = new AudioTransitionGate(startFrame);
            long id = ++s_nextVoiceId;
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!, GainEnvelope.FromCurve(FadeKind.In, startFrame, inFrames, inTable).WithTransition(gate),
                        startFrame, rg, sess.BuildVoiceChain(), id))
                    return false;
                bool inArmed = inAuto is null || sess.SetVoiceAutomation(id, inAuto);
                sess.SetVoiceEnvelope(s_activePrimaryId, GainEnvelope.FromCurve(FadeKind.Out, startFrame, outFrames, outTable).WithTransition(gate));
                bool outArmed = outAuto is null || sess.SetVoiceAutomation(s_activePrimaryId, outAuto);
                if (!inArmed || !outArmed) Log.Warn("audio", $"[mix] automation not admitted in={inArmed} out={outArmed}");
                NoteBakedFactor(id, s_prepDecoder, in s_prepOpened);
                s_joinExact = true;
                s_joinPending = true;
                s_joinFrame = startFrame + outFrames;              // the announce: A is over (D14 (b))
                s_joinVoiceId = id;
                s_joinVoice = item.AudioVoice;
                s_joinTotalFrames = item.TotalFrames;
                s_joinId = s_prepId;
                s_joinDurMs = s_prepDurMs;
                s_joinBytes = s_prepBytes;
                s_joinDecoder = s_prepDecoder;
                s_joinOpened = s_prepOpened;
                s_joinStartMs = s_prepStartMs + plan.OutDurationMs;
                s_joinMix = true;
                s_mixGate = gate;
                s_mixOutVoice = s_activePrimaryId;
                s_mixStartFrameValue = startFrame;
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDecoder = null;
                s_prepId = default;
                s_prepDurMs = 0;
                s_prepOverlap = false;
                s_prepStartMs = 0;
            }
            if (plan.Source == MixSource.Saved) Interlocked.Increment(ref s_mMixSaved); else Interlocked.Increment(ref s_mMixAuto);
            // #154's timeline tap (plan §3.9, decision D14 (b)): the overlap is audible between these two mixer frames and the timecode keeps
            // naming the OUTGOING track until it is over; the announce publishes the incoming identity, which drops the transition.
            EntityId incoming;
            lock (s_gate) incoming = s_joinId;
            Timeline.SetTransition(incoming, startFrame, startFrame + outFrames, Timeline.Master.Outgoing);
            Log.Info("audio", $"[mix] scheduled source={plan.Source} preset={plan.Preset} out={plan.OutStartMs} outDur={plan.OutDurationMs} "
                + $"in={plan.InStartMs} inDur={plan.InDurationMs} start={startFrame} lead={startFrame - sess.SampleClock} "
                + $"auto={(outAuto is null ? "-" : "out")}{(inAuto is null ? "" : "+in")}");
            return true;
        }

        static long s_mixStartFrameValue;

        /// <summary>The tick, between the schedule and the announce: note when the overlap starts sounding (the player
        /// bar's "Mixing into …").</summary>
        static void TickMixSounding(PcmAudioSession sess)
        {
            bool sounding;
            lock (s_gate) sounding = s_joinPending && s_joinMix && sess.SampleClock >= s_mixStartFrameValue;
            int now = sounding ? 1 : 0;
            if (Interlocked.Exchange(ref s_mixSounding, now) != now && sounding) Log.Info("audio", "[mix] overlap sounding");
        }

        /// <summary>Under s_gate, from <see cref="AbandonPendingJoin"/>: undo a mix join's A side. True when the join was a mix.</summary>
        static bool CancelMixLocked(PcmAudioSession? sess)
        {
            if (!s_joinMix) return false;
            bool beforeStart = s_mixGate?.TryCancel() ?? true;
            try
            {
                // A is put back to unity and neutral either way: before the start the gate already reads its start value;
                // inside the overlap (D14 (b): a seek is in the song on screen, which is A) A plays on at full level.
                sess?.SetVoiceEnvelope(s_mixOutVoice, GainEnvelope.Constant);
                sess?.SetVoiceAutomation(s_mixOutVoice, null);
            }
            catch { }
            s_joinMix = false;
            s_mixGate = null;
            s_joinStartMs = 0;
            Volatile.Write(ref s_mixSounding, 0);
            Interlocked.Increment(ref s_mMixCancelled);
            Log.Info("audio", "[mix] cancelled " + (beforeStart ? "before the overlap" : "inside the overlap"));
            return true;
        }

        /// <summary>Under s_gate, from the announce: the mix join went live. The anchor B starts at.</summary>
        static long AnnouncedMixLocked()
        {
            if (!s_joinMix) return 0;
            long at = s_joinStartMs;
            s_joinMix = false;
            s_mixGate = null;
            s_joinStartMs = 0;
            s_mixPlan = default;
            s_mixDurationPosted = 0;
            Volatile.Write(ref s_mixSounding, 0);
            return at;
        }
    }

    /// <summary>MixTiming → the engine's inputs: two gain tables and two automations, sampled from the preset's curves
    /// (MixCurves) and mapped to dB, Hz and Q (plan §3.4). Pump thread, once per join (allocates its tables).</summary>
    public static class MixRender
    {
        public static void Build(in MixTiming t, int sampleRate, long startFrame, int outFrames, int inFrames,
            out float[] outTable, out float[] inTable, out VoiceAutomation? outAuto, out VoiceAutomation? inAuto)
        {
            int n = GainEnvelope.TablePoints;
            byte preset = t.Preset == (byte)MixPreset.Rise ? (byte)MixPreset.Rise : (byte)MixPreset.Fade;   // unknown ids play as Fade
            Span<CurveSegment> seg = stackalloc CurveSegment[4];
            int outUnits = MixCurves.UnitsFor(t.OutDurationMs), inUnits = MixCurves.UnitsFor(t.OutDurationMs);
            outTable = new float[n];
            inTable = new float[n];
            MixCurves.Sample(seg[..MixCurves.Segments(preset, MixLane.Volume, false, outUnits, seg)], outTable);
            MixCurves.Sample(seg[..MixCurves.Segments(preset, MixLane.Volume, true, inUnits, seg)], inTable);
            outAuto = Automation(preset, false, outUnits, sampleRate, startFrame, outFrames, seg);
            inAuto = Automation(preset, true, inUnits, sampleRate, startFrame, inFrames, seg);
        }

        static VoiceAutomation? Automation(byte preset, bool incoming, int units, int rate, long start, int frames, Span<CurveSegment> seg)
        {
            int n = GainEnvelope.TablePoints;
            int eq = MixCurves.Segments(preset, MixLane.LowEq, incoming, units, seg);
            float[]? shelf = null;
            if (eq > 0)
            {
                shelf = new float[n];
                MixCurves.Sample(seg[..eq], shelf);
                for (int i = 0; i < n; i++) shelf[i] = MixCurves.LowShelfDb(shelf[i]);
            }
            Span<CurveSegment> res = stackalloc CurveSegment[4];
            int rc = MixCurves.Segments(preset, MixLane.FilterResonance, incoming, units, res);
            int cut = MixCurves.Segments(preset, MixLane.FilterCutoff, incoming, units, seg);
            SweepPoint[]? sweep = null;
            if (cut > 0)
            {
                sweep = new SweepPoint[n];
                for (int i = 0; i < n; i++)
                {
                    float x = i / (float)(n - 1);
                    float y = MixCurves.Evaluate(seg[..cut], x);
                    float r = rc > 0 ? MixCurves.Evaluate(res[..rc], x) : 0.5f;
                    var (mode, hz, q) = MixCurves.Sweep(y, r);
                    sweep[i] = new SweepPoint(mode, hz, q);
                }
            }
            if (shelf is null && sweep is null) return null;
            return VoiceAutomation.Build(rate, start, frames, MixCurves.LowShelfHz, shelf ?? [], sweep ?? []);
        }
    }
}
