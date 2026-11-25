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
//
// The AI DJ's joins (plan-dj D3, D3b; Playback.Dj.TalkOver.cs) ride the same machinery with three additions: B may start after A's
// window starts (`InDelayMs`: an outro once the duck has landed, a radio B in A's last 350 ms), B may become current as soon as it starts
// (`AnnounceAt == InStart`: a jump clip's target, an outro, a radio B) while A plays on under it to its own window's end (its voice is an
// Out envelope, retired when the window ends; its bytes are closed only then), and a talk-over whose B started under the voice tells the
// reducer so (`AudioSignal.TargetStarted`: the narration restrictions lift while the voice is still current). A talk-over that comes
// too late for its frame starts B as early as it still can instead of falling back to the butt-join.

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
        /// <summary>The scheduled join's timing (its shape, its announce, its delay).</summary>
        static MixTiming s_joinPlan;
        /// <summary>A join announced when B starts: the mixer frame A's window ends on (its voice and bytes live until then); 0 otherwise.</summary>
        static long s_joinOutEndFrame;
        /// <summary>The announced join's A outlives it until this mixer frame: the retiring bytes close only then (0: at once).</summary>
        static long s_retireAtFrame;
        /// <summary>The talk-over's TargetStarted was posted for the scheduled join.</summary>
        static bool s_targetStartedPosted;

        static long s_mMixSaved, s_mMixAuto, s_mMixRefused, s_mMixCancelled, s_mMixDj;
        public static long MixSavedCount => Interlocked.Read(ref s_mMixSaved);
        public static long MixAutoCount => Interlocked.Read(ref s_mMixAuto);
        public static long MixRefusedCount => Interlocked.Read(ref s_mMixRefused);
        public static long MixCancelledCount => Interlocked.Read(ref s_mMixCancelled);
        /// <summary>AI DJ joins scheduled (talk-overs and radio).</summary>
        public static long MixDjCount => Interlocked.Read(ref s_mMixDj);

        /// <summary>One AI DJ join as the pump scheduled and ran it (the inspector's evidence, plan-dj §2.10): the plan, and A's position on its
        /// own clock where A's window starts, where B starts, where the join is announced and where A's window ends, from the mixer frames.</summary>
        public sealed class DjJoin
        {
            public EntityId A, B;
            public MixTiming Plan;
            public long StartFrame, InFrame, AnnounceFrame, OutEndFrame;
            public int Rate;
            /// <summary>A's playhead (ms) at the frames above, from the anchor the schedule used.</summary>
            public long APosAtWindow, APosAtIn, APosAtAnnounce, APosAtOutEnd;
            /// <summary>The mixer clock when the pump saw B sounding and when it announced (−1: not yet).</summary>
            public long SoundingClock = -1, AnnouncedClock = -1;
            public bool Cancelled, LateShifted;
        }

        static readonly List<DjJoin> s_djJoins = new();
        static DjJoin? s_djJoin;

        /// <summary>The last AI DJ joins (oldest first), a copy. Any thread.</summary>
        public static DjJoin[] DjJoins() { lock (s_gate) return s_djJoins.ToArray(); }

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
                // At least 1 ms: an AI DJ voice that fits before its track's cue starts the track with the voice (out = 0), and a boundary
                // of 0 reads as "no length" (the endgame would never ask for B; it asks at once instead, and the join comes as early as it can).
                boundaryMs = Math.Max(1, s_mixPlan.OutStartMs);
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
            long minLead = GaplessJoinClock.MsToFrames(MixMinLeadMs, rate);
            bool lateShifted = false;
            if (lead < minLead && plan.Source == MixSource.TalkOver && plan.InDelayMs == 0)
            {
                // A voice over its target came too late for its frame (a jump clip whose target opened late): B starts as early as it still
                // can, the voice's window shortened by as much, so the voice still ends where it ends and B still joins under it.
                long shiftMs = (minLead - lead) * 1000 / Math.Max(1, rate) + 1;
                if (plan.OutDurationMs > shiftMs + MixMinLeadMs)
                {
                    plan = plan with { OutStartMs = (uint)(plan.OutStartMs + shiftMs), OutDurationMs = (uint)(plan.OutDurationMs - shiftMs) };
                    startFrame += GaplessJoinClock.MsToFrames(shiftMs, rate);
                    lead = startFrame - clock;
                    lateShifted = true;
                    Log.Info("audio", $"[mix] talk-over late by {shiftMs} ms: the track starts {shiftMs} ms later under the voice");
                }
            }
            if (lead < minLead) { RefuseMix(MixWhyNot.Late); return false; }
            if (lead > GaplessJoinClock.MsToFrames(MixCommitLeadMs, rate)) return true;
            if (!ScheduleMix(sess, item, in plan, startFrame, lateShifted)) RefuseMix(MixWhyNot.Late);
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
        static bool ScheduleMix(PcmAudioSession sess, IPreparedItem item, in MixTiming plan, long startFrame, bool lateShifted = false)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate)) return false;
            int rate = sess.Format.SampleRate;
            int outFrames = (int)GaplessJoinClock.MsToFrames(plan.OutDurationMs, rate);
            int inFrames = (int)GaplessJoinClock.MsToFrames(plan.InDurationMs, rate);
            long inDelayFrames = GaplessJoinClock.MsToFrames(plan.InDelayMs, rate);
            if (outFrames <= 0 || inFrames <= 0 || inDelayFrames >= outFrames) return false;
            // Where the mix meets the crossfade fix (GaplessJoinClock.ScheduleFade): when A's length is decoded truth, the
            // mix-out never runs past A's last sample. A plan whose out-end lies beyond it (a catalogue length longer than
            // the file) is shortened to end on that frame, as a late fade is; a start at or past it is too late.
            long outMs = plan.OutDurationMs;
            long exactFrames = ActiveExactFrames(sess);
            if (exactFrames >= 0)
            {
                long activeEnd;
                lock (s_gate) activeEnd = JoinFrameExact(s_anchorClock, s_anchorPlayheadMs, exactFrames, rate);
                if (startFrame + inDelayFrames >= activeEnd) return false;
                if (startFrame + outFrames > activeEnd)
                {
                    Log.Info("audio", $"[mix] out shortened to A's exact end: {outFrames} -> {activeEnd - startFrame} frames");
                    outFrames = (int)(activeEnd - startFrame);
                    outMs = outFrames * 1000L / rate;
                }
            }
            MixRender.Build(in plan, rate, startFrame, outFrames, inFrames,
                out var outTable, out var inTable, out var outAuto, out var inAuto);
            var gate = new AudioTransitionGate(startFrame);
            long id = ++s_nextVoiceId;
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            long inFrame = startFrame + inDelayFrames;                 // B's first frame (an AI DJ outro, a blind radio B: later than A's window)
            bool announceAtIn = plan.AnnounceAt == MixAnnounceAt.InStart;
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!, GainEnvelope.FromCurve(FadeKind.In, inFrame, inFrames, inTable).WithTransition(gate),
                        inFrame, rg, sess.BuildVoiceChain(), id))
                    return false;
                bool inArmed = inAuto is null || sess.SetVoiceAutomation(id, inAuto);
                sess.SetVoiceEnvelope(s_activePrimaryId, GainEnvelope.FromCurve(FadeKind.Out, startFrame, outFrames, outTable).WithTransition(gate));
                bool outArmed = outAuto is null || sess.SetVoiceAutomation(s_activePrimaryId, outAuto);
                if (!inArmed || !outArmed) Log.Warn("audio", $"[mix] automation not admitted in={inArmed} out={outArmed}");
                NoteBakedFactor(id, s_prepDecoder, in s_prepOpened);
                s_joinExact = true;
                s_joinPending = true;
                s_joinFrame = announceAtIn ? inFrame : startFrame + outFrames;   // the announce: A is over (D14 (b)), or B started (AI DJ)
                s_joinOutEndFrame = announceAtIn ? startFrame + outFrames : 0;
                s_joinPlan = plan;
                s_targetStartedPosted = false;
                s_joinVoiceId = id;
                s_joinVoice = item.AudioVoice;
                s_joinTotalFrames = item.TotalFrames;
                s_joinId = s_prepId;
                s_joinDurMs = s_prepDurMs;
                s_joinBytes = s_prepBytes;
                s_joinDecoder = s_prepDecoder;
                s_joinOpened = s_prepOpened;
                s_joinStartMs = announceAtIn ? s_prepStartMs : s_prepStartMs + outMs;
                s_joinMix = true;
                // The base hand-off's join state (#284 / local files): the joining kind, and no plain fade (the mix
                // carries its own curves; AnnounceGaplessJoin publishes no fade transition, AbandonPendingJoin restores
                // no fade: CancelMixLocked puts A back).
                s_joinKind = s_prepKind;
                s_joinFadeMs = 0;
                s_joinFadeFrames = 0;
                s_joinOutgoingVoiceId = s_activePrimaryId;
                s_mixGate = gate;
                s_mixOutVoice = s_activePrimaryId;
                s_mixStartFrameValue = inFrame;
                if (!plan.IsPlaylistMix)
                {
                    long anchorPlayhead = s_anchorPlayheadMs, anchorClock = s_anchorClock;
                    long PosAt(long frame) => anchorPlayhead + (frame - anchorClock) * 1000 / Math.Max(1, rate);
                    s_djJoin = new DjJoin
                    {
                        A = s_id, B = s_joinId, Plan = plan, StartFrame = startFrame, InFrame = inFrame, AnnounceFrame = s_joinFrame,
                        OutEndFrame = startFrame + outFrames, Rate = rate, APosAtWindow = PosAt(startFrame), APosAtIn = PosAt(inFrame),
                        APosAtAnnounce = PosAt(s_joinFrame), APosAtOutEnd = PosAt(startFrame + outFrames), LateShifted = lateShifted,
                    };
                    if (s_djJoins.Count >= 32) s_djJoins.RemoveAt(0);
                    s_djJoins.Add(s_djJoin);
                }
                s_prepItem = null;
                s_prepBytes = null;
                s_prepDecoder = null;
                s_prepId = default;
                s_prepDurMs = 0;
                s_prepOverlap = false;
                s_prepStartMs = 0;
            }
            if (plan.Source == MixSource.Saved) Interlocked.Increment(ref s_mMixSaved);
            else if (plan.IsPlaylistMix) Interlocked.Increment(ref s_mMixAuto);
            else Interlocked.Increment(ref s_mMixDj);
            // #154's timeline tap (plan §3.9, decision D14 (b)): the overlap is audible between these two mixer frames and the timecode keeps
            // naming the OUTGOING track until it is over; the announce publishes the incoming identity, which drops the transition.
            EntityId incoming;
            lock (s_gate) incoming = s_joinId;
            Timeline.SetTransition(incoming, startFrame, startFrame + outFrames, Timeline.Master.Outgoing);
            Log.Info("audio", $"[mix] scheduled source={plan.Source} preset={plan.Preset} out={plan.OutStartMs} outDur={plan.OutDurationMs} "
                + $"in={plan.InStartMs} inDur={plan.InDurationMs} start={startFrame} lead={startFrame - sess.SampleClock} "
                + $"auto={(outAuto is null ? "-" : "out")}{(inAuto is null ? "" : "+in")}"
                + (plan.IsPlaylistMix ? "" : $" shape={plan.Shape} inFrame={inFrame} announce={plan.AnnounceAt}@{(announceAtIn ? inFrame : startFrame + outFrames)}"));
            return true;
        }

        static long s_mixStartFrameValue;

        /// <summary>The tick, between the schedule and the announce: note when the overlap starts sounding (the player
        /// bar's "Mixing into …").</summary>
        static void TickMixSounding(PcmAudioSession sess)
        {
            bool sounding, targetStarted = false;
            uint epoch;
            lock (s_gate)
            {
                sounding = s_joinPending && s_joinMix && sess.SampleClock >= s_mixStartFrameValue;
                epoch = s_loadEpoch;
                if (sounding && s_djJoin is { SoundingClock: < 0 } join) join.SoundingClock = sess.SampleClock;
                // AI DJ: an intro's (a welcome's) target started under the voice, which stays the current item to its end (plan-dj §1.6).
                if (sounding && !s_targetStartedPosted && s_joinPlan.Source == MixSource.TalkOver && s_joinPlan.AnnounceAt == MixAnnounceAt.OutEnd)
                {
                    s_targetStartedPosted = true;
                    targetStarted = true;
                }
            }
            int now = sounding ? 1 : 0;
            if (Interlocked.Exchange(ref s_mixSounding, now) != now && sounding) Log.Info("audio", "[mix] overlap sounding");
            if (targetStarted)
            {
                Log.Info("audio", "[mix] talk-over: the target started under the voice");
                PostSignal(AudioSignal.TargetStarted, epoch, 0);
            }
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
            s_joinOutEndFrame = 0;
            s_joinPlan = default;
            if (s_djJoin is { } join) { join.Cancelled = true; s_djJoin = null; }
            Volatile.Write(ref s_mixSounding, 0);
            Interlocked.Increment(ref s_mMixCancelled);
            Log.Info("audio", "[mix] cancelled " + (beforeStart ? "before the overlap" : "inside the overlap"));
            return true;
        }

        /// <summary>Under s_gate, from a load or a session teardown: a mix join still pending dies with the voices it joined (no gate to
        /// cancel, nothing to put back: the session goes), so its state must not reach the next join's announce.</summary>
        static void DropMixJoinLocked()
        {
            if (s_djJoin is { } join) { join.Cancelled = true; s_djJoin = null; }
            s_joinMix = false;
            s_mixGate = null;
            s_joinStartMs = 0;
            s_joinOutEndFrame = 0;
            s_joinPlan = default;
            Volatile.Write(ref s_mixSounding, 0);
        }

        /// <summary>Under s_gate, from the announce: the mix join went live. The anchor B starts at; −1 when the join was not a mix.
        /// A join announced when B starts keeps A's bytes until A's window ends (<see cref="s_retireAtFrame"/>).</summary>
        static long AnnouncedMixLocked(long clock)
        {
            s_retireAtFrame = 0;
            if (!s_joinMix) return -1;
            long at = s_joinStartMs;
            s_retireAtFrame = s_joinOutEndFrame;
            if (s_djJoin is { } join) { join.AnnouncedClock = clock; s_djJoin = null; }
            s_joinMix = false;
            s_mixGate = null;
            s_joinStartMs = 0;
            s_joinOutEndFrame = 0;
            s_joinPlan = default;
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
            if (!t.IsPlaylistMix)
            {
                // The AI DJ's joins: volume only (the duck, the outro, the radio pair: Playback.Dj.TalkOver.cs), no EQ or filter.
                Span<CurveSegment> dj = stackalloc CurveSegment[3];
                outTable = new float[n];
                inTable = new float[n];
                MixCurves.Sample(dj[..DjCurves.Segments(in t, incoming: false, dj)], outTable);
                MixCurves.Sample(dj[..DjCurves.Segments(in t, incoming: true, dj)], inTable);
                outAuto = null;
                inAuto = null;
                return;
            }
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
