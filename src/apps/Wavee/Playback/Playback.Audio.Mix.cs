// ── Playback/Playback.Audio.Mix.cs — the pump half of #48 Mix (plan §3.6, §3.6a item 4) ───────────────────────────────
//
// The host's planner (Playback.Mix.cs) hands the pump ONE timing for the playing pair (SetMix). From it:
//
//   prepare   B opens at the in-point (PrepareContext.StartSourceFrame, engine E3); the achieved start is kept
//   endgame   the ask window opens 8 s before the MIX-OUT, not the file's end (fade 0)
//   schedule  within 2 s of the mix-out, at an EXACT mixer frame: B joins with its curve envelope, its automation and (beatmatched)
//             its own rate, B's speed gliding back to 1 after its fade-in (E4); A gets its curve envelope, its automation (EQ,
//             filter, noise, echo / delay / reverb: E2, E5-E7) and its beat loop (E8); one AudioTransitionGate shared by both (E1).
//             Every lane of the plan's styles is built by MixRender.Join, off the render thread
//   speed     while the active voice carries a rate plan (B after a beatmatched join), each step the engine applies is published
//             as the deck's playback_speed (protocol §6.2), read on the 200 ms tick (TickMixSpeed)
//   announce  when A is over (startFrame + outFrames, decision D14 (b)): the gapless join's identity swap, B already
//             InStart + OutDuration in
//   cancel    a seek, a next-row change, a device rebuild before the announce: before the overlap the gate is
//             cancelled, A is put back to unity and B (never heard) cut; inside it A ramps back to unity and B fades out
//             over 30 ms (MixRender.Cancel). A's automation ramps to neutral (its echo or reverb closes its send and rings out),
//             A's loop ends with a seam back to the original, B's bytes are cut (the gapless abandon; B's rate plan goes with its
//             voice: its glide starts only after its fade-in, which a cancel never reaches). A load drops the join with its session
//             (DropMixJoinLocked)
//   rate      a seam whose A is still gliding (the previous beatmatched B) is placed by A's own rate plan: the mix-out's mixer
//             frame, A's window and A's exact end come from the engine's source <-> mixer map (ActiveFrameAtMsLocked), not rate-1 math
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
        /// <summary>A's voice id when the scheduled join armed a beat loop on it (a cancel ends it), else 0.</summary>
        static long s_mixLoopVoice;

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
                startFrame = ActiveFrameAtMsLocked(sess, plan.OutStartMs, rate);
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

        /// <summary>Under s_gate: the mixer frame at which the ACTIVE voice reaches <paramref name="ms"/> of its own media. A voice with
        /// its own rate plan (the previous join's beatmatched B, still at its speed or gliding back to 1) is followed along its plan by
        /// the engine's source ↔ mixer map; any other voice plays at 1 from the anchor the pump keeps.</summary>
        static long ActiveFrameAtMsLocked(PcmAudioSession sess, long ms, int rate)
        {
            if (ActiveRatedLocked(sess)
                && sess.TryMixerFrameAtSourceFrame(s_activePrimaryId, GaplessJoinClock.MsToFrames(Math.Max(0, ms), rate), out long frame))
                return frame;
            return s_anchorClock + GaplessJoinClock.MsToFrames(Math.Max(0, ms - s_anchorPlayheadMs), rate);
        }

        /// <summary>Under s_gate: the mixer frame of the active voice's exact end (<paramref name="exactFrames"/>, its last source frame
        /// + 1): along its rate plan when it has one (<see cref="ActiveFrameAtMsLocked"/>), else <see cref="JoinFrameExact"/>.</summary>
        static long ActiveEndFrameLocked(PcmAudioSession sess, long exactFrames, int rate)
        {
            if (ActiveRatedLocked(sess) && sess.TryMixerFrameAtSourceFrame(s_activePrimaryId, exactFrames, out long frame)) return frame;
            return JoinFrameExact(s_anchorClock, s_anchorPlayheadMs, exactFrames, rate);
        }

        /// <summary>Under s_gate: the active voice plays by its own rate plan (#48 E4: a beatmatched B, carried across a seek).</summary>
        static bool ActiveRatedLocked(PcmAudioSession sess) => s_activePrimaryId != 0 && sess.VoiceRatePlanOf(s_activePrimaryId) is not null;

        /// <summary>Arm the join at <paramref name="startFrame"/>: B's voice with its curve envelope, automation and rate plan; A's
        /// envelope, automation and beat loop; one gate. The app's identity does not change until A is over.</summary>
        static bool ScheduleMix(PcmAudioSession sess, IPreparedItem item, in MixTiming plan, long startFrame)
        {
            if (!GaplessJoinClock.PrimedSlotMatches(item.MixRate, sess.Format.SampleRate)) return false;
            int rate = sess.Format.SampleRate;
            long outStartSource = GaplessJoinClock.MsToFrames(plan.OutStartMs, rate);
            long outSource = GaplessJoinClock.MsToFrames(plan.OutDurationMs, rate);   // A's window in A's own media
            int outFrames;
            bool aRated;
            lock (s_gate)
            {
                aRated = ActiveRatedLocked(sess);
                // A at 1: its window is its media length. A still gliding: the mixer frames its plan takes to cover it.
                outFrames = aRated ? (int)(ActiveFrameAtMsLocked(sess, plan.AudibleEndMs, rate) - startFrame) : (int)outSource;
            }
            if (outFrames <= 0 || plan.InDurationMs == 0) return false;
            // Where the mix meets the crossfade fix (GaplessJoinClock.ScheduleFade): when A's length is decoded truth, the
            // mix-out never runs past A's last sample. A plan whose out-end lies beyond it (a catalogue length longer than
            // the file) is shortened to end on that frame, as a late fade is; a start at or past it is too late.
            long exactFrames = ActiveExactFrames(sess);
            bool shortened = false;
            if (exactFrames >= 0)
            {
                long activeEnd;
                lock (s_gate) activeEnd = ActiveEndFrameLocked(sess, exactFrames, rate);
                if (startFrame >= activeEnd) return false;
                if (startFrame + outFrames > activeEnd)
                {
                    Log.Info("audio", $"[mix] out shortened to A's exact end: {outFrames} -> {activeEnd - startFrame} frames");
                    outFrames = (int)(activeEnd - startFrame);
                    outSource = Math.Max(1, exactFrames - outStartSource);
                    shortened = true;
                }
            }
            var join = MixRender.Join(in plan, rate, sess.Format.Channels, startFrame, outFrames, outStartSource, outSource);
            if (join.InFrames <= 0) return false;
            // Where B is when A is over (the announce's playhead): what B covers of itself over A's window, at its own rate.
            long bAdvancedMs = join.InRate is { } bRate ? (long)Math.Round(bRate.SourceFramesBetween(startFrame, startFrame + outFrames) * 1000.0 / rate)
                             : shortened || aRated ? outFrames * 1000L / rate : plan.OutDurationMs;
            long id = ++s_nextVoiceId;
            float rg = sess.ReplayGainScalarFor(item.Loudness);
            MixArm armed;
            lock (s_gate)
            {
                // The loop is armed with the rest, at least MixMinLeadMs before the window (its first block starts at or after it).
                if (!MixRender.Arm(sess, join, item.AudioVoice!, id, s_activePrimaryId, startFrame, outFrames, rg, out armed)) return false;
                s_mixLoopVoice = armed.LoopArmed ? s_activePrimaryId : 0;
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
                s_joinStartMs = s_prepStartMs + bAdvancedMs;
                s_joinMix = true;
                // The base hand-off's join state (#284 / local files): the joining kind, and no plain fade (the mix
                // carries its own curves; AnnounceGaplessJoin publishes no fade transition, AbandonPendingJoin restores
                // no fade: CancelMixLocked puts A back).
                s_joinKind = s_prepKind;
                s_joinFadeMs = 0;
                s_joinFadeFrames = 0;
                s_joinOutgoingVoiceId = s_activePrimaryId;
                s_mixGate = armed.Gate;
                s_mixOutEnv = armed.OutEnv;
                s_mixInEnv = armed.InEnv;
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
                + $"in={plan.InStartMs} inDur={plan.InDurationMs} start={startFrame} lead={startFrame - sess.SampleClock} outFrames={outFrames} "
                + $"inFrames={join.InFrames} auto={(join.OutAuto is null ? "-" : "out")}{(join.InAuto is null ? "" : "+in")} lanes=[{join.Lanes}]"
                + (join.InRate is { } r ? $" speedB={r.Initial:0.######} glideAt={r.GlideStartFrame} steps={r.StepCount}" : "")
                + (join.OutLoop is { } l ? $" loop={l.Blocks.Length}blocks end={l.End} armed={armed.LoopArmed}" : ""));
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
                sess?.SetVoiceAutomation(s_mixOutVoice, null);   // A's lanes ramp back to neutral (the stage's declick); effects ring out
                if (s_mixLoopVoice != 0) sess?.SetVoiceLoop(s_mixLoopVoice, null);   // A's loop ends, a seam back to the original
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
            s_mixLoopVoice = 0;
            s_joinStartMs = 0;
            Volatile.Write(ref s_mixSounding, 0);
        }

        // The last speed published: the voice, the load epoch it went out under and the engine's change count then (pump thread).
        static long s_speedVoice = -1, s_speedChanges = -1;
        static uint s_speedEpoch;

        /// <summary>The tick: publish the active voice's own speed while it has a rate plan (a beatmatched B): B's <c>speedB</c> once it
        /// is the deck's row, then every 0.005 step of its glide back to 1 as the engine applies it (protocol §6.2: the official player
        /// publishes each step as <c>playback_speed</c>). A changed <see cref="VoiceRateReading.Changes"/> (or a new voice or load
        /// epoch: the hand-off's adoption, a seek's swapped-in voice) posts <see cref="AudioSignal.MixSpeed"/>; the reducer carries it
        /// to the PUT. A few volatile reads per tick; only a published step allocates (its log line).</summary>
        static void TickMixSpeed(PcmAudioSession sess)
        {
            long id;
            uint epoch;
            lock (s_gate)
            {
                if (s_joinPending && s_joinMix) return;            // A is still the deck's row: B's speed goes out when B is
                if (!ActiveRatedLocked(sess)) return;
                id = s_activePrimaryId;
                epoch = s_loadEpoch;
            }
            if (!sess.TryReadVoiceRate(id, out var reading) || reading.Changes == 0) return;
            if (id == s_speedVoice && epoch == s_speedEpoch && reading.Changes == s_speedChanges) return;
            s_speedVoice = id;
            s_speedEpoch = epoch;
            s_speedChanges = reading.Changes;
            PostSignal(AudioSignal.MixSpeed, epoch, BitConverter.DoubleToInt64Bits(reading.Factor));
            Log.Info("audio", "[mix] playback_speed " + reading.Factor.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)
                + " (change " + reading.Changes + " at mixer frame " + reading.LastChangeMixerFrame + ", clock " + sess.SampleClock + ")");
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

    /// <summary>One join's engine inputs (<see cref="MixRender.Join"/>): the two gain tables, each side's automation, B's window in
    /// mixer frames, B's rate plan and A's beat loop. Built on the pump thread, once per join; the engine copies what it keeps.</summary>
    public sealed class MixJoin
    {
        /// <summary>A's and B's gain tables (<see cref="GainEnvelope.TablePoints"/> points over each side's window).</summary>
        public required float[] OutTable, InTable;
        /// <summary>Each side's automation (EQ, filter sweep, noise, echo / delay / reverb), null when its styles move none.</summary>
        public VoiceAutomation? OutAuto, InAuto;
        /// <summary>B's window in MIXER frames: its media window (<see cref="MixTiming.InDurationMs"/>) at its own rate.</summary>
        public int InFrames;
        /// <summary>B's own rate: <c>speedB</c> from its first frame, back to 1 in 0.005 steps a second from
        /// <see cref="MixRender.GlideDelayMs"/> after its window (protocol §6.2); null for a pair that is not beatmatched.</summary>
        public VoiceRatePlan? InRate;
        /// <summary>A's beat loop (the looping style's blocks on A's beat grid), null when the style loops nothing.</summary>
        public BeatLoopSchedule? OutLoop;
        /// <summary>The lanes armed, as <see cref="MixRender.Lanes"/> spells them (the scheduled line).</summary>
        public string Lanes = "";
    }

    /// <summary>What <see cref="MixRender.Arm"/> installed: the join's gate and the two envelopes (a cancel ramps from them), and
    /// whether A's loop was admitted.</summary>
    public readonly record struct MixArm(AudioTransitionGate Gate, GainEnvelope OutEnv, GainEnvelope InEnv, bool LoopArmed);

    /// <summary>MixTiming → the engine's inputs: two gain tables and two automations, sampled from the plan's styles (the style
    /// table, <see cref="MixStyles"/>, at the plan's bars) and mapped to dB, Hz, Q and wet by the official client's value map
    /// (<see cref="MixCurves"/>); B's rate plan; A's beat loop. A's tables span its window (outFrames), B's its own (inFrames), from
    /// the same start frame. Every lane the styles move is rendered: volume (the gain envelope), EQ low / mid / high (the 3-band
    /// EQ, E5), filter cutoff and resonance (the sweep, E2), noise colour (E6), the echo, delay and reverb dry/wet (E7), looping
    /// (E8) and B's beatmatch speed (E4). Pump thread, once per join (allocates its tables).</summary>
    public static class MixRender
    {
        /// <summary>B holds its beatmatch speed this long after its window ends before the first 0.005 step (protocol §6.2: "about
        /// 0.5 s after B's fade-in ends").</summary>
        public const int GlideDelayMs = 500;
        /// <summary>The glide's step (protocol §6.2) and its interval: one step a second.</summary>
        public const double GlideStep = VoiceRatePlan.DefaultGlideStep;
        /// <summary>The seam every loop point is crossfaded over (the engine's default, equal power).</summary>
        public const double LoopSeamMs = 5.0;
        /// <summary>The reverb's decay (RT60, s). The client's tables give the dry/wet only; the room is Wavee's: 2 s with the engine's
        /// default damping (6 kHz) and low cut (80 Hz), long enough for an "out" to bloom, short enough not to wash over B.</summary>
        public const float ReverbDecaySeconds = 2f;

        /// <summary>The join for <paramref name="t"/> starting at mixer frame <paramref name="startFrame"/>: A's window is
        /// <paramref name="outFrames"/> mixer frames (and <paramref name="outSourceFrames"/> of its own media from source frame
        /// <paramref name="outStartSource"/>: the loop's grid); B's is its media window at its own rate (<see cref="RatePlan"/>).</summary>
        public static MixJoin Join(in MixTiming t, int sampleRate, int channels, long startFrame, int outFrames, long outStartSource, long outSourceFrames)
        {
            long inMedia = GaplessJoinClock.MsToFrames(t.InDurationMs, sampleRate);
            var rate = RatePlan(in t, sampleRate, startFrame, inMedia, out int inFrames);
            Build(in t, sampleRate, startFrame, outFrames, inFrames, out var outTable, out var inTable, out var outAuto, out var inAuto, channels);
            return new MixJoin
            {
                OutTable = outTable, InTable = inTable, OutAuto = outAuto, InAuto = inAuto, InFrames = inFrames, InRate = rate,
                OutLoop = Loop(in t, sampleRate, outStartSource, outSourceFrames), Lanes = Lanes(in t, rendered: true),
            };
        }

        /// <summary>Install <paramref name="join"/> on <paramref name="sess"/>: B (<paramref name="incoming"/>, voice
        /// <paramref name="inId"/>) joins at <paramref name="startFrame"/> with its curve envelope, its automation and its rate plan from
        /// its first frame; A (voice <paramref name="outId"/>) gets its curve envelope over <paramref name="outFrames"/>, its automation and
        /// its beat loop; one gate holds both envelopes. The pump's ScheduleMix and the offline proofs (`--mix-bench --scenario render`,
        /// `mix show=speed`) call it alike. False when B was not admitted (nothing was installed). Control thread.</summary>
        public static bool Arm(PcmAudioSession sess, MixJoin join, IAudioSource incoming, long inId, long outId, long startFrame, int outFrames,
            float replayGain, out MixArm armed)
        {
            var gate = new AudioTransitionGate(startFrame);
            var inEnv = GainEnvelope.FromCurve(FadeKind.In, startFrame, join.InFrames, join.InTable).WithTransition(gate);
            var outEnv = GainEnvelope.FromCurve(FadeKind.Out, startFrame, outFrames, join.OutTable).WithTransition(gate);
            armed = new MixArm(gate, outEnv, inEnv, false);
            if (!sess.TryAddCrossfadeVoice(incoming, inEnv, startFrame, replayGain, sess.BuildVoiceChain(), inId, join.InRate)) return false;
            bool inArmed = join.InAuto is null || sess.SetVoiceAutomation(inId, join.InAuto);
            sess.SetVoiceEnvelope(outId, outEnv);
            bool outArmed = join.OutAuto is null || sess.SetVoiceAutomation(outId, join.OutAuto);
            if (!inArmed || !outArmed) Log.Warn("audio", $"[mix] automation not admitted in={inArmed} out={outArmed}");
            bool loop = join.OutLoop is not null && sess.SetVoiceLoop(outId, join.OutLoop);
            if (join.OutLoop is not null && !loop) Log.Warn("audio", "[mix] loop not admitted");
            armed = armed with { LoopArmed = loop };
            return true;
        }

        /// <summary>B's rate plan for a beatmatched plan (<see cref="MixTiming.SpeedB"/> ≠ 1): <c>speedB</c> over B's window, then
        /// back to 1 in <see cref="GlideStep"/> steps one second apart, the first <see cref="GlideDelayMs"/> after the window ends
        /// (the official player: 0.901696 → 0.906696 → … → 1, each step published; a difference under a step goes in one, 1.00008 →
        /// 1). <paramref name="inFrames"/>: B's window in mixer frames, <paramref name="inMediaFrames"/> of B covered at
        /// <c>speedB</c>. Null (and the window 1:1) for a plan at speed 1.</summary>
        public static VoiceRatePlan? RatePlan(in MixTiming t, int sampleRate, long startFrame, long inMediaFrames, out int inFrames)
        {
            if (t.SpeedB == 1f || !float.IsFinite(t.SpeedB) || t.SpeedB <= 0f)
            {
                inFrames = (int)inMediaFrames;
                return null;
            }
            // The window runs at the constant speedB (the glide starts after it), so its mixer length is the media length / speedB.
            var hold = VoiceRatePlan.Constant(t.SpeedB);
            inFrames = (int)Math.Round(hold.MixerFramesFor(inMediaFrames, startFrame));
            long glideAt = startFrame + inFrames + (long)GlideDelayMs * sampleRate / 1000;
            return VoiceRatePlan.Glide(hold.Initial, glideAt, 1.0, GlideStep, sampleRate);
        }

        /// <summary>A's beat loop: the looping style's blocks (<see cref="MixStyles.LoopBlocks"/>, fractions of A's window and lengths in
        /// beats) placed on A's beat grid: a block starts on the beat of A's tempo (<see cref="MixTiming.BpmA"/>, recipe 1.16; the window
        /// starts on a beat) nearest its fraction, loops its beats and ends where the original reaches its end fraction, a seam before
        /// the next block. The last block running to the window's end stops A (loop-out: A's envelope is at 0 there). A block the window
        /// cannot hold (its loop longer than its span: a 16-beat loop in a 2.9 s Auto window) is left out: A plays on. Null when nothing
        /// loops.</summary>
        public static BeatLoopSchedule? Loop(in MixTiming t, int sampleRate, long windowStart, long windowFrames)
        {
            Span<MixLoopBlock> blocks = stackalloc MixLoopBlock[MixStyles.MaxLoopBlocks];
            int n = MixStyles.LoopBlocks(t.Styles.Looping, blocks);
            if (n == 0 || windowFrames <= 0) return null;
            double beat = 60.0 * sampleRate / (t.BpmA > 0 ? t.BpmA : MixCurves.FallbackBpm);
            int seam = Math.Max(1, (int)Math.Round(LoopSeamMs * sampleRate / 1000.0));
            Span<long> starts = stackalloc long[MixStyles.MaxLoopBlocks];
            for (int i = 0; i < n; i++) starts[i] = windowStart + (long)Math.Round(Math.Round(blocks[i].Start * windowFrames / beat) * beat);
            var kept = new List<LoopBlock>(n);
            long previousEnd = long.MinValue;
            bool toEnd = false;
            for (int i = 0; i < n; i++)
            {
                var b = blocks[i];
                if (b.Type != MixLoopBlockType.Loop) continue;           // no tabled style uses Spinback or VinylStop (the engine renders neither)
                long start = starts[i];
                int length = (int)Math.Round(b.Beats * beat);
                long until = windowStart + (long)Math.Round(b.End * windowFrames);
                if (i + 1 < n) until = Math.Min(until, starts[i + 1] - seam);
                if (length < 2 * seam || until <= start + length || previousEnd != long.MinValue && start < previousEnd + seam) continue;
                kept.Add(LoopBlock.Until(start, length, until));
                previousEnd = until;
                toEnd = b.End >= 1f;
            }
            if (kept.Count == 0) return null;
            return new BeatLoopSchedule(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(kept), toEnd ? LoopEnd.Stop : LoopEnd.Continue,
                sampleRate, LoopSeamMs);
        }

        /// <summary>The gain tables and the automations for windows of <paramref name="outFrames"/> (A) and <paramref name="inFrames"/>
        /// (B) mixer frames from <paramref name="startFrame"/>. <see cref="Join"/> calls it; diagnostics call it alone.</summary>
        public static void Build(in MixTiming t, int sampleRate, long startFrame, int outFrames, int inFrames,
            out float[] outTable, out float[] inTable, out VoiceAutomation? outAuto, out VoiceAutomation? inAuto, int channels = 2)
        {
            int n = GainEnvelope.TablePoints;
            outTable = new float[n];
            inTable = new float[n];
            // A volume style of None (only an override can say so) leaves both decks at full level: A ends at its window's end.
            if (!SampleLane(in t, MixCurveLane.Volume, false, outTable)) outTable.AsSpan().Fill(1f);
            if (!SampleLane(in t, MixCurveLane.Volume, true, inTable)) inTable.AsSpan().Fill(1f);
            outAuto = Automation(in t, false, sampleRate, channels, startFrame, outFrames);
            inAuto = Automation(in t, true, sampleRate, channels, startFrame, inFrames);
        }

        /// <summary>One lane's raw curve (0..1, the style table's units) sampled evenly over its side's window into
        /// <paramref name="table"/>: what <see cref="Build"/> feeds the engine before the value map. False when the plan's styles
        /// leave the lane alone (the table is untouched).</summary>
        public static bool SampleLane(in MixTiming t, MixCurveLane lane, bool incoming, Span<float> table)
            => t.Sample(lane, incoming, table);

        /// <summary>The lanes the plan's styles move, as <c>volume.out eqLow.in … looping(TwoBeat)</c>: the ones the engine renders
        /// (<paramref name="rendered"/>: every curve lane, a known looping style, B's speed as <c>speed</c>) or the ones it skips (a
        /// looping style the table does not know, a jog-wheel style: no list of them is known). "" when none. Diagnostics and the
        /// once-per-plan log; allocates.</summary>
        public static string Lanes(in MixTiming t, bool rendered)
        {
            if (!t.Mix) return "";
            var sb = new System.Text.StringBuilder();
            if (rendered)
            {
                Span<CurveSegment> seg = stackalloc CurveSegment[MixStyles.MaxSegments];
                foreach (MixCurveLane lane in Enum.GetValues<MixCurveLane>())
                    foreach (bool incoming in (ReadOnlySpan<bool>)[false, true])
                    {
                        if (t.Segments(lane, incoming, seg) == 0) continue;
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(LaneName(lane)).Append(incoming ? ".in" : ".out");
                    }
                Span<MixLoopBlock> blocks = stackalloc MixLoopBlock[MixStyles.MaxLoopBlocks];
                if (MixStyles.LoopBlocks(t.Styles.Looping, blocks) > 0) sb.Append(sb.Length > 0 ? " " : "").Append("looping(").Append(t.Styles.Looping).Append(')');
                if (t.SpeedB != 1f) sb.Append(sb.Length > 0 ? " " : "").Append("speed.in");
                return sb.ToString();
            }
            if (t.Styles.Looping is not (MixLoopingStyle.None or MixLoopingStyle.OneBeat or MixLoopingStyle.TwoBeat or MixLoopingStyle.FourBeat
                or MixLoopingStyle.EightBeat or MixLoopingStyle.SixteenBeat or MixLoopingStyle.HalvingEnd))
                sb.Append("looping(").Append((int)t.Styles.Looping).Append(')');
            if (t.Styles.Jogwheel != 0) sb.Append(sb.Length > 0 ? " " : "").Append("jogwheel(").Append(t.Styles.Jogwheel).Append(')');
            return sb.ToString();
        }

        static string LaneName(MixCurveLane lane) => lane switch
        {
            MixCurveLane.Volume => "volume", MixCurveLane.EqLow => "eqLow", MixCurveLane.EqMid => "eqMid", MixCurveLane.EqHigh => "eqHigh",
            MixCurveLane.FilterCutoff => "cutoff", MixCurveLane.FilterResonance => "resonance", MixCurveLane.NoiseColor => "noise",
            MixCurveLane.EchoDryWet => "echo", MixCurveLane.ReverbDryWet => "reverb", _ => "delay",
        };

        /// <summary>The EQ lanes are sampled this many times denser than a gain table: a bass swap is 1/(16 × bars) of the window (1/32
        /// at 2 bars, 1/512 at 32), so at the gain table's 1,025 points a 32-bar swap would get 2 points; the engine reads the band
        /// gains per sample between table points, and four times the points keep the swap's S.</summary>
        public const int EqDensity = 4;

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

        /// <summary>One side's automation: every lane the styles move on that side, through the official client's value map, as one
        /// <see cref="VoiceAutomationSpec"/> (one per voice: an automation with effects holds that voice's delay lines).</summary>
        static VoiceAutomation? Automation(in MixTiming t, bool incoming, int rate, int channels, long start, int frames)
        {
            int n = GainEnvelope.TablePoints;
            // EQ: the 3-band split, each band's gain in dB (0 = kill, −∞); a band the style leaves alone holds 0 dB (empty table).
            int dense = (n - 1) * EqDensity + 1;
            float[]? low = EqBand(in t, MixCurveLane.EqLow, incoming, dense);
            float[]? mid = EqBand(in t, MixCurveLane.EqMid, incoming, dense);
            float[]? high = EqBand(in t, MixCurveLane.EqHigh, incoming, dense);
            EqLaneSpec? eq = low is null && mid is null && high is null ? null
                : new EqLaneSpec(low ?? ReadOnlyMemory<float>.Empty, mid ?? ReadOnlyMemory<float>.Empty, high ?? ReadOnlyMemory<float>.Empty);
            // Filter: cutoff and resonance as one sweep (mode, Hz, Q).
            SweepPoint[]? sweep = null;
            var table = new float[n];
            if (SampleLane(in t, MixCurveLane.FilterCutoff, incoming, table))
            {
                // Resonance has no value map in the client's table; a style that moves the cutoff holds it flat at 0.5.
                var resonance = new float[n];
                if (!SampleLane(in t, MixCurveLane.FilterResonance, incoming, resonance)) resonance.AsSpan().Fill(0.5f);
                sweep = new SweepPoint[n];
                for (int i = 0; i < n; i++)
                {
                    var (mode, hz, q) = MixCurves.Sweep(table[i], resonance[i]);
                    sweep[i] = new SweepPoint(mode, hz, q);
                }
            }
            // Noise: the colour curve's "off" band is silence, elsewhere white noise at MixCurves.NoiseLevelDb through its colour filter.
            NoiseLaneSpec? noise = null;
            if (SampleLane(in t, MixCurveLane.NoiseColor, incoming, table))
            {
                var level = new float[n];
                var colour = new SweepPoint[n];
                bool sounds = false;
                for (int i = 0; i < n; i++)
                {
                    if (MixCurves.NoiseOff(table[i])) { level[i] = float.NegativeInfinity; colour[i] = new SweepPoint(SweepPoint.Bypass, 1000f, 0.707f); continue; }
                    var (mode, hz) = MixCurves.NoiseColour(table[i]);
                    level[i] = MixCurves.NoiseLevelDb;
                    colour[i] = new SweepPoint(mode, hz, 0.707f);
                    sounds = true;
                }
                if (sounds) noise = new NoiseLaneSpec(level) { Colour = colour };
            }
            // Effects: the style's one dry/wet lane (% / 100); the echo and delay timed in beats of A's tempo (recipe 1.16).
            DelayLaneSpec? echo = null, delay = null;
            ReverbLaneSpec? reverb = null;
            if (MixStyles.FxLane(t.Styles.Fx) is { } fxLane)
            {
                var wet = new float[n];
                if (SampleLane(in t, fxLane, incoming, wet))
                {
                    for (int i = 0; i < n; i++) wet[i] = MixCurves.FxWet(wet[i]);
                    int delayFrames = MixCurves.FxDelayFrames(MixStyles.FxBeats(t.Styles.Fx), t.BpmA, rate);
                    switch (fxLane)
                    {
                        case MixCurveLane.EchoDryWet: echo = new DelayLaneSpec(delayFrames, wet) { Feedback = DelayLaneSpec.DefaultEchoFeedback }; break;
                        case MixCurveLane.DelayDryWet: delay = new DelayLaneSpec(delayFrames, wet) { Feedback = DelayLaneSpec.DefaultDelayFeedback }; break;
                        default: reverb = new ReverbLaneSpec(ReverbDecaySeconds, wet); break;
                    }
                }
            }
            if (eq is null && sweep is null && noise is null && echo is null && delay is null && reverb is null) return null;
            return VoiceAutomation.Build(new VoiceAutomationSpec(rate, start, frames)
            {
                Eq = eq, Sweep = sweep, Noise = noise, Echo = echo, Delay = delay, Reverb = reverb, Channels = channels,
            });
        }

        /// <summary>One EQ band's gains in dB over <paramref name="points"/> points (<see cref="MixCurves.EqGainDb"/>: 0 kills), or null
        /// when the style leaves the band alone.</summary>
        static float[]? EqBand(in MixTiming t, MixCurveLane band, bool incoming, int points)
        {
            var db = new float[points];
            if (!SampleLane(in t, band, incoming, db)) return null;
            for (int i = 0; i < db.Length; i++) db[i] = MixCurves.EqGainDb(db[i]);
            return db;
        }
    }
}
