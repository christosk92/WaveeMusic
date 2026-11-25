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
//   cancel    a seek, a next-row change, a device rebuild before the announce: before the overlap the gate is
//             cancelled, A is put back to unity and B (never heard) cut; inside it A ramps back to unity and B fades out
//             over 30 ms (MixRender.Cancel). A's automation ramps to neutral, B's bytes are cut (the gapless abandon). A
//             load drops the join with its session (DropMixJoinLocked)
//   refuse    a cross-kind slot or a tick that came too late: the base hand-off runs, the duration goes back to the file's
//   none      a pair Spotify saved as "no transition": the base hand-off with no fade (gapless), whatever the user's
//             Crossfade says (a mixed list ignores the setting); a local "No mix" keeps the user's Crossfade
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
        /// <summary>The two envelopes the scheduled join installed (A's out, B's in): a cancel inside the overlap ramps from them.</summary>
        static GainEnvelope? s_mixOutEnv, s_mixInEnv;
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

        /// <summary>The fade the BASE hand-off uses for a pair the planner did not mix: the user's Crossfade, except a pair Spotify
        /// saved as "no transition" (<see cref="MixWhyNot.NoTransition"/>, planned only inside a mixed playlist with Mix on), which
        /// is a hard, gapless change: a mixed list carries <c>automix.ignore_setting=true</c>, the user's crossfade does not apply
        /// inside it. A local "No mix" (<see cref="MixWhyNot.PairOptOut"/>) keeps the user's Crossfade ("a normal track change"). Pure.</summary>
        public static int BaseFadeMs(in MixTiming plan, int userFadeMs) => plan.WhyNot == MixWhyNot.NoTransition ? 0 : userFadeMs;

        /// <summary>The tick's fade selector: <see cref="BaseFadeMs"/> for the deck's planned pair, the user's fade when the plan is
        /// another pair's or the prepared slot holds a row the plan is not about.</summary>
        static int MixBaseFadeMs(int userFadeMs)
        {
            lock (s_gate)
            {
                if (s_id.IsEmpty || s_mixActive != s_id) return userFadeMs;
                if (!s_prepId.IsEmpty && s_prepId != s_mixNext) return userFadeMs;
                return BaseFadeMs(in s_mixPlan, userFadeMs);
            }
        }

        /// <summary>Diagnostics (the `mix` verb): the fade the base hand-off would use now, as the tick selects it.</summary>
        public static int HandOffFadeMsNow => MixBaseFadeMs(EffectiveFadeMs);

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
            // Where the mix meets the crossfade fix (GaplessJoinClock.ScheduleFade): when A's length is decoded truth, the
            // mix-out never runs past A's last sample. A plan whose out-end lies beyond it (a catalogue length longer than
            // the file) is shortened to end on that frame, as a late fade is; a start at or past it is too late.
            long outMs = plan.OutDurationMs;
            long exactFrames = ActiveExactFrames(sess);
            if (exactFrames >= 0)
            {
                long activeEnd;
                lock (s_gate) activeEnd = JoinFrameExact(s_anchorClock, s_anchorPlayheadMs, exactFrames, rate);
                if (startFrame >= activeEnd) return false;
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
            var inEnv = GainEnvelope.FromCurve(FadeKind.In, startFrame, inFrames, inTable).WithTransition(gate);
            var outEnv = GainEnvelope.FromCurve(FadeKind.Out, startFrame, outFrames, outTable).WithTransition(gate);
            long id = ++s_nextVoiceId;
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            lock (s_gate)
            {
                if (!sess.TryAddCrossfadeVoice(item.AudioVoice!, inEnv, startFrame, rg, sess.BuildVoiceChain(), id))
                    return false;
                bool inArmed = inAuto is null || sess.SetVoiceAutomation(id, inAuto);
                sess.SetVoiceEnvelope(s_activePrimaryId, outEnv);
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
                s_joinStartMs = s_prepStartMs + outMs;
                s_joinMix = true;
                // The base hand-off's join state (#284 / local files): the joining kind, and no plain fade (the mix
                // carries its own curves; AnnounceGaplessJoin publishes no fade transition, AbandonPendingJoin restores
                // no fade: CancelMixLocked puts A back).
                s_joinKind = s_prepKind;
                s_joinFadeMs = 0;
                s_joinFadeFrames = 0;
                s_joinOutgoingVoiceId = s_activePrimaryId;
                s_mixGate = gate;
                s_mixOutEnv = outEnv;
                s_mixInEnv = inEnv;
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
            Log.Info("audio", $"[mix] scheduled source={plan.Source} preset={plan.Preset} bars={plan.Bars} out={plan.OutStartMs} outDur={plan.OutDurationMs} "
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

        /// <summary>Under s_gate, from <see cref="AbandonPendingJoin"/>: undo a mix join's A side. True when the join was a mix.
        /// <paramref name="incomingFade"/>: B's envelope when the cancel lands inside the overlap (a 30 ms fade-out from where B
        /// is, <see cref="MixRender.Cancel"/>); null before it, where the abandon's one-frame cut of a B that never sounded stands.</summary>
        static bool CancelMixLocked(PcmAudioSession? sess, out GainEnvelope? incomingFade)
        {
            incomingFade = null;
            if (!s_joinMix) return false;
            bool beforeStart = true;
            try
            {
                // Before the start the gate already reads its start values (A unity, B silent): A gets the plain envelope back. Inside
                // the overlap (D14 (b): a seek is in the song on screen, which is A) A ramps back to unity and B fades out over
                // 30 ms from where each is; the ramps start a mixer block ahead, so the commands land before them.
                beforeStart = MixRender.Cancel(s_mixGate, s_mixOutEnv, s_mixInEnv, sess?.SampleClock ?? 0, sess?.MixerRef.MaxBlock ?? 0,
                    sess?.Format.SampleRate ?? 0, out var outgoing, out incomingFade);
                sess?.SetVoiceEnvelope(s_mixOutVoice, outgoing);
                sess?.SetVoiceAutomation(s_mixOutVoice, null);   // A's EQ and filter ramp back to neutral (the stage's declick)
            }
            catch { }
            ForgetMixJoinLocked();
            Interlocked.Increment(ref s_mMixCancelled);
            Log.Info("audio", "[mix] cancelled " + (beforeStart ? "before the overlap" : "inside the overlap (A back to unity, B out, over "
                + MixRender.CancelRampMs + " ms)"));
            return true;
        }

        /// <summary>Under s_gate, from <see cref="DisposeSessionAsync"/>: the session goes with a mix join still pending (a load
        /// during the overlap: a load supersedes the reducer's CancelPrepared, so <see cref="AbandonPendingJoin"/> never runs). Its
        /// voices go with the session; the join's state must not outlive it, or the next plain gapless announce reads the dropped
        /// mix's B anchor as its own playhead (<see cref="AnnouncedMixLocked"/>).</summary>
        static void DropMixJoinLocked()
        {
            if (!s_joinMix) return;
            ForgetMixJoinLocked();
            Interlocked.Increment(ref s_mMixCancelled);
            Log.Info("audio", "[mix] join dropped with its session");
        }

        static void ForgetMixJoinLocked()
        {
            s_joinMix = false;
            s_mixGate = null;
            s_mixOutEnv = s_mixInEnv = null;
            s_joinStartMs = 0;
            Volatile.Write(ref s_mixSounding, 0);
        }

        /// <summary>Under s_gate, from the announce: the mix join went live. The anchor B starts at.</summary>
        static long AnnouncedMixLocked()
        {
            if (!s_joinMix) return 0;
            long at = s_joinStartMs;
            ForgetMixJoinLocked();
            s_mixPlan = default;
            s_mixDurationPosted = 0;
            return at;
        }
    }

    /// <summary>MixTiming → the engine's inputs: two gain tables and two automations, sampled from the plan's styles (the style
    /// table, <see cref="MixStyles"/>, at the plan's bars) and mapped to dB, Hz and Q by the official client's value map
    /// (MixCurves.EqGainDb / Sweep). A's tables span its window (outFrames), B's its own (inFrames), from the same start frame.
    /// The pinned engine renders four lanes (<see cref="Renders"/>); the others are resolved in the plan and listed by
    /// <see cref="Lanes"/> until M4's lanes land. Pump thread, once per join (allocates its tables).</summary>
    public static class MixRender
    {
        public static void Build(in MixTiming t, int sampleRate, long startFrame, int outFrames, int inFrames,
            out float[] outTable, out float[] inTable, out VoiceAutomation? outAuto, out VoiceAutomation? inAuto)
        {
            int n = GainEnvelope.TablePoints;
            outTable = new float[n];
            inTable = new float[n];
            // A volume style of None (only an override can say so) leaves both decks at full level: A ends at its window's end.
            if (!SampleLane(in t, MixCurveLane.Volume, false, outTable)) outTable.AsSpan().Fill(1f);
            if (!SampleLane(in t, MixCurveLane.Volume, true, inTable)) inTable.AsSpan().Fill(1f);
            outAuto = Automation(in t, false, sampleRate, startFrame, outFrames);
            inAuto = Automation(in t, true, sampleRate, startFrame, inFrames);
        }

        /// <summary>One lane's raw curve (0..1, the style table's units) sampled evenly over its side's window into
        /// <paramref name="table"/>: what <see cref="Build"/> feeds the engine before the value map. False when the plan's styles
        /// leave the lane alone (the table is untouched).</summary>
        public static bool SampleLane(in MixTiming t, MixCurveLane lane, bool incoming, Span<float> table)
            => MixStyles.Sample(t.Styles, lane, incoming, t.Bars, table);

        /// <summary>The lanes the pinned engine renders: the gain envelope (volume), the low shelf (EQ low) and the sweep (filter
        /// cutoff and resonance). EQ mid and high (E5), noise colour (E6), the effects' dry/wet (E7) and looping (E8) wait for M4.</summary>
        public static bool Renders(MixCurveLane lane)
            => lane is MixCurveLane.Volume or MixCurveLane.EqLow or MixCurveLane.FilterCutoff or MixCurveLane.FilterResonance;

        /// <summary>The lanes the plan's styles move, as <c>volume.out eqLow.in …</c>: the ones the engine renders
        /// (<paramref name="rendered"/>) or the ones it skips (and <c>looping</c> when the looping style writes blocks). "" when
        /// none. Diagnostics and the once-per-plan log; allocates.</summary>
        public static string Lanes(in MixTiming t, bool rendered)
        {
            if (!t.Mix) return "";
            var sb = new System.Text.StringBuilder();
            Span<CurveSegment> seg = stackalloc CurveSegment[MixStyles.MaxSegments];
            foreach (MixCurveLane lane in Enum.GetValues<MixCurveLane>())
            {
                if (Renders(lane) != rendered) continue;
                foreach (bool incoming in (ReadOnlySpan<bool>)[false, true])
                {
                    if (MixStyles.Segments(t.Styles, lane, incoming, t.Bars, seg) == 0) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(LaneName(lane)).Append(incoming ? ".in" : ".out");
                }
            }
            if (!rendered && t.Styles.Looping is not (MixLoopingStyle.None or MixLoopingStyle.Unknown))
                sb.Append(sb.Length > 0 ? " " : "").Append("looping(").Append(t.Styles.Looping).Append(')');
            return sb.ToString();
        }

        static string LaneName(MixCurveLane lane) => lane switch
        {
            MixCurveLane.Volume => "volume", MixCurveLane.EqLow => "eqLow", MixCurveLane.EqMid => "eqMid", MixCurveLane.EqHigh => "eqHigh",
            MixCurveLane.FilterCutoff => "cutoff", MixCurveLane.FilterResonance => "resonance", MixCurveLane.NoiseColor => "noise",
            MixCurveLane.EchoDryWet => "echo", MixCurveLane.ReverbDryWet => "reverb", _ => "delay",
        };

        /// <summary>The shelf lane is sampled this many times denser than a gain table (see <see cref="Automation"/>).</summary>
        public const int ShelfDensity = 4;

        /// <summary>A cancel inside the overlap ramps A back to unity and fades B out over this long (plan §2.3.3).</summary>
        public const int CancelRampMs = 30;

        /// <summary>A mix join's cancel (pure; the pump's <c>CancelMixLocked</c> and the inspector's offline pull call it alike): try to
        /// cancel the <paramref name="gate"/>. Before the overlap started (true): A gets the constant envelope back and
        /// <paramref name="incoming"/> is null (B never sounded). Inside it (false): from <paramref name="clock"/> + <paramref name="lead"/>
        /// (one mixer block: a command lands at the next block, so the ramps never start in the past) A ramps from its gain there to
        /// unity and B from its gain there to silence, linearly over <see cref="CancelRampMs"/>. B's is an Out envelope, so the mixer
        /// retires B when it ends; A's holds unity after it.</summary>
        public static bool Cancel(AudioTransitionGate? gate, GainEnvelope? outEnv, GainEnvelope? inEnv, long clock, int lead, int rate,
            out GainEnvelope outgoing, out GainEnvelope? incoming)
        {
            bool beforeStart = gate?.TryCancel() ?? true;
            if (beforeStart || outEnv is null || inEnv is null || rate <= 0)
            {
                outgoing = GainEnvelope.Constant;
                incoming = null;
                return beforeStart;
            }
            long at = clock + Math.Max(0, lead);
            int frames = (int)GaplessJoinClock.MsToFrames(CancelRampMs, rate);
            outgoing = GainEnvelope.FromCurve(FadeKind.In, at, frames, [outEnv.GainAt(at), 1f]);
            incoming = GainEnvelope.FromCurve(FadeKind.Out, at, frames, [inEnv.GainAt(at), 0f]);
            return false;
        }

        static VoiceAutomation? Automation(in MixTiming t, bool incoming, int rate, long start, int frames)
        {
            int n = GainEnvelope.TablePoints;
            // The client's EQ map: −48 dB (kill) up to +6 dB. The lane places its steps (at most VoiceAutomation.MaxSteps) by equal dB
            // change, but only ON the points it is given, and a bass swap is 1/(16 × bars) of the window (1/32 at 2 bars, 1/512 at 32):
            // at the gain table's 1,025 points that is 32 points or fewer for a 48 dB swing. Four times as many points give the placer
            // four times the resolution inside the swap.
            float[]? shelf = new float[(n - 1) * ShelfDensity + 1];
            if (SampleLane(in t, MixCurveLane.EqLow, incoming, shelf))
                for (int i = 0; i < shelf.Length; i++) shelf[i] = MixCurves.EqGainDb(shelf[i]);
            else shelf = null;
            SweepPoint[]? sweep = null;
            var cutoff = new float[n];
            if (SampleLane(in t, MixCurveLane.FilterCutoff, incoming, cutoff))
            {
                // Resonance has no value map in the client's table; a style that moves the cutoff holds it flat at 0.5.
                var resonance = new float[n];
                if (!SampleLane(in t, MixCurveLane.FilterResonance, incoming, resonance)) resonance.AsSpan().Fill(0.5f);
                sweep = new SweepPoint[n];
                for (int i = 0; i < n; i++)
                {
                    var (mode, hz, q) = MixCurves.Sweep(cutoff[i], resonance[i]);
                    sweep[i] = new SweepPoint(mode, hz, q);
                }
            }
            if (shelf is null && sweep is null) return null;
            return VoiceAutomation.Build(rate, start, frames, MixCurves.LowShelfHz, shelf ?? [], sweep ?? []);
        }
    }
}
