using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// Where the tonearm is in its choreography. This is the RECORD family's transport story told in the medium's own
/// vocabulary — a turntable does not "pause", it lifts the cue lever and brakes the platter — and every one of these
/// is reachable from the transition table in <see cref="TonearmMachine.Step"/>.
/// </summary>
public enum TonearmPhase : byte
{
    /// <summary>Nothing on the platter: the record is in its sleeve and the arm sits on its rest.</summary>
    Idle,
    /// <summary>The record slides out of the sleeve onto the platter.</summary>
    SlideOut,
    /// <summary>The beat of stillness with the platter already turning, before the arm moves.</summary>
    Cue,
    /// <summary>Arm swings from its rest to the lead-in groove.</summary>
    SwingToLead,
    /// <summary>Silicone-damped cue-lever descent onto the groove.</summary>
    Lower,
    /// <summary>Riding the groove: the arm angle IS the position.</summary>
    Tracking,
    /// <summary>Cue lever up. <see cref="TonearmState.AfterLift"/> says what happens at the top.</summary>
    Lift,
    /// <summary>Lifted, platter braked.</summary>
    Paused,
    /// <summary>Platter coming back up to speed under a lifted arm.</summary>
    SpinUp,
    /// <summary>Lifted arm travelling to a seek target.</summary>
    SeekSwing,
    /// <summary>The headshell is under the pointer; the arm follows the drag.</summary>
    Dragging,
    /// <summary>Lifted arm travelling back to the lead-in for the next track on the SAME record.</summary>
    RecueSwing,
    /// <summary>Lifted arm travelling back to its rest. <see cref="TonearmState.AfterRest"/> says why.</summary>
    SwingToRest,
    /// <summary>The record slides back into its sleeve.</summary>
    SleeveIn,
    /// <summary>The sleeve art crossfades to the new album (<see cref="TonearmState.CoverGen"/> ticks here).</summary>
    CoverSwap,
    /// <summary>Riding the run-out groove after the queue ended.</summary>
    RunOut,
    /// <summary>Parked in the locked groove, waiting for the auto-return to trip.</summary>
    LockedGroove,
    /// <summary>Lifted and bobbing over the groove while the stream buffers.</summary>
    Hover,
    /// <summary>Auto-returned: record still on the platter, arm on its rest, platter braked.</summary>
    Stopped,
    /// <summary>Playback failed; the arm retreated and the platter stopped.</summary>
    Unavailable,
}

/// <summary>
/// The tonearm's whole state as one value. Pure data — <see cref="TonearmMachine.Step"/> maps
/// (state, input) → state and <see cref="TonearmMachine.Sample"/> maps (state, input) → a drawable frame, so the
/// machine can be driven from a test at any cadence without a clock, a signal or a window.
/// </summary>
/// <param name="Phase">Which leg of the choreography.</param>
/// <param name="SinceMs">Wall clock at which <paramref name="Phase"/> was entered.</param>
/// <param name="PhaseMs">How long this leg lasts; 0 = it has no deadline (Tracking, Paused, Hover, …).</param>
/// <param name="FromDeg">Arm angle the leg starts at.</param>
/// <param name="ToDeg">Arm angle the leg ends at (and, while Dragging, where the headshell currently is).</param>
/// <param name="LiftFrom">Lift the leg starts at — a lift interrupted halfway resumes from where it was.</param>
/// <param name="PlatterOn">Is the platter driven?</param>
/// <param name="RecordOut">Is the record on the platter (as opposed to in its sleeve)?</param>
/// <param name="CoverGen">Bumped once per sleeve/label artwork swap; the face crossfades on the edge.</param>
/// <param name="AfterLift">Which leg a <see cref="TonearmPhase.Lift"/> hands over to at the top.</param>
/// <param name="AfterRest">Which leg a <see cref="TonearmPhase.SwingToRest"/> hands over to at the rest.</param>
/// <param name="SwingMs">Duration stashed for the swing that FOLLOWS a lift (recue / change / auto-return / error).</param>
/// <param name="ResumeDown">Does the arm come back down after a seek swing, or stay up (seek while paused)?</param>
/// <param name="ThumpAtMs">Wall clock of the stylus drop; the face fires its dust puff on that one frame.</param>
/// <param name="PlatterOffAtMs">Deferred brake: 0 = none, else the wall clock at which the platter stops.</param>
public readonly record struct TonearmState(
    TonearmPhase Phase, long SinceMs, float PhaseMs, float FromDeg, float ToDeg, float LiftFrom,
    bool PlatterOn, bool RecordOut, int CoverGen,
    TonearmPhase AfterLift, TonearmPhase AfterRest, float SwingMs,
    bool ResumeDown, long ThumpAtMs, long PlatterOffAtMs)
{
    /// <summary>Cold start: empty platter, arm on its rest, nothing turning.</summary>
    public static TonearmState Initial => new(TonearmPhase.Idle, 0, 0, TonearmMachine.RestDeg, TonearmMachine.RestDeg,
        1f, false, false, 0, TonearmPhase.Idle, TonearmPhase.Idle, 0, false, 0, 0);
}

/// <summary>One drawable tonearm frame. <c>Lift</c> is 0 (in the groove) .. 1 (cue lever up), and above 1 for the
/// buffering bob. <c>Slide</c> is 0 (in the sleeve) .. 1 (on the platter).</summary>
public readonly record struct TonearmFrame(float ArmDeg, float Lift, float PlatterTargetDegPerSec, float Slide, bool Thump, DeckPhaseName Name);

/// <summary>
/// The SL-1200 in a value type: 0.7 s spin-up, a silicone-damped cue-lever descent, a 1.8 s locked groove and an
/// auto-return. Engine-free by construction (System only) — the face binds what <see cref="Sample"/> hands back and
/// never asks the machine a question.
/// </summary>
public static class TonearmMachine
{
    /// <summary>Arm on its rest.</summary>
    public const float RestDeg = -34f;
    /// <summary>Arm over the lead-in groove (position 0).</summary>
    public const float LeadInDeg = -20f;
    /// <summary>Lead-in → run-out sweep.</summary>
    public const float SpanDeg = 17f;
    /// <summary>Arm over the run-out groove (position 1).</summary>
    public const float RunOutDeg = LeadInDeg + SpanDeg;

    public const float SlideMs = 600, CueHoldMs = 300, SwingLeadMs = 1200, LowerMs = 900, LiftMs = 450, SpinUpMs = 700,
        SeekLiftMs = 400, SeekSwingBaseMs = 300, SeekSwingPerSpanMs = 600, SeekLowerMs = 700, RecueSwingMs = 900,
        SkipLiftMs = 300, ChangeSwingMs = 1200, ChangePlatterOffMs = 700, SleeveInMs = 600, CoverSwapMs = 350,
        RunOutBaseMs = 400, RunOutPerSpanMs = 800, LockedGroove33Ms = 1800, LockedGroove45Ms = 1333, AutoLiftMs = 500,
        AutoSwingMs = 1400, AutoPlatterOffMs = 500, ErrorSwingMs = 1200, BobPeriodMs = 1800, BufferLiftMs = 400,
        ReducedMs = 150, ThumpMs = 120, BufferLeadInFrac = 0.02f;

    /// <summary>Above this the machine treats the record as a 45 (shorter locked groove).</summary>
    public const float Rpm45Threshold = 40f;
    /// <summary>A seek shorter than this many degrees is below one rendered quantum — the arm does not move for it.</summary>
    public const float SeekDeadZoneDeg = 0.15f;

    /// <summary>Where the arm sits for a normalized position.</summary>
    public static float AngleOf(float frac) => LeadInDeg + SpanDeg * DeckEase.Clamp01(frac);

    /// <summary>Reduced motion collapses every leg to a snap.</summary>
    static float Dur(float ms, in DeckInput i) => i.ReducedMotion ? MathF.Min(ms, ReducedMs) : ms;

    /// <summary>A swing costs a fixed setup plus a per-span travel time, so a long seek reads as a long journey.</summary>
    static float SwingMsFor(float from, float to, float baseMs, float perSpanMs, in DeckInput i)
        => Dur(baseMs + perSpanMs * MathF.Abs(to - from) / SpanDeg, i);

    /// <summary>
    /// Mounted mid-song (Cover → Player, or a rail reopened): the record was playing all along, so there is no cueing
    /// sequence to watch — the machine starts where the transport already is.
    /// </summary>
    public static TonearmState Seed(in DeckInput i)
    {
        if (!i.HasTrack) return TonearmState.Initial;
        float a = AngleOf(i.Frac);
        if (i.Error) return TonearmState.Initial with { Phase = TonearmPhase.Unavailable, RecordOut = true, SinceMs = i.NowMs };
        if (i.Advancing || (i.PlayWhenReady && !i.Buffering))
            return TonearmState.Initial with { Phase = TonearmPhase.Tracking, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, LiftFrom = 0f, PlatterOn = true, RecordOut = true };
        if (i.Buffering)
            return TonearmState.Initial with { Phase = TonearmPhase.Hover, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, PlatterOn = true, RecordOut = true };
        // The queue already ran out before we mounted: the deck has auto-returned, it is not merely paused mid-groove.
        if (i.QueueEnded) return TonearmState.Initial with { Phase = TonearmPhase.Stopped, SinceMs = i.NowMs, RecordOut = true };
        return TonearmState.Initial with { Phase = TonearmPhase.Paused, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, RecordOut = true };
    }

    /// <summary>
    /// One fold. Rules are ordered by authority: the record leaving, an error, a track boundary, the queue ending, a
    /// live drag, a committed seek, buffering, pause/resume, play-from-rest, and finally the current leg's timer.
    ///
    /// <para>Idempotent within a tick: calling this twice with the SAME input returns the same state both times, so a
    /// caller that folds a boundary edge eagerly and then ticks on the same clock value cannot double-count it.</para>
    /// </summary>
    public static TonearmState Step(TonearmState s, in DeckInput i)
    {
        long now = i.NowMs;

        // Deferred brake and the one-shot stylus drop are applied FIRST, so every rule below — including the ones that
        // return `s` untouched — carries them.
        if (s.PlatterOffAtMs != 0 && now >= s.PlatterOffAtMs) s = s with { PlatterOn = false, PlatterOffAtMs = 0 };
        if (s.ThumpAtMs != 0 && now > s.ThumpAtMs) s = s with { ThumpAtMs = 0 };

        var cur = Sample(in s, in i);
        float armNow = cur.ArmDeg, liftNow = MathF.Min(cur.Lift, 1f);

        // 1. the track went away → back into the sleeve from any pose
        if (!i.HasTrack)
        {
            if (s.Phase is TonearmPhase.Idle or TonearmPhase.SleeveIn) return Timers(s, in i);
            // Already retreating towards the sleeve: let it finish rather than restarting the lift.
            if ((s.Phase is TonearmPhase.Lift or TonearmPhase.SwingToRest) && s.AfterRest == TonearmPhase.SleeveIn) return Timers(s, in i);
            return s.RecordOut
                ? LiftThen(s, in i, LiftMs, TonearmPhase.SwingToRest, TonearmPhase.SleeveIn, armNow, liftNow, null, ChangeSwingMs)
                : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false };
        }

        // 2. error → lift · rest · brake (once); the platter stops WITH the swing, not at the end of it
        if (i.Error && s.Phase is not (TonearmPhase.Unavailable or TonearmPhase.Lift or TonearmPhase.SwingToRest))
            return LiftThen(s, in i, LiftMs, TonearmPhase.SwingToRest, TonearmPhase.Unavailable, armNow, liftNow, 0f, ErrorSwingMs);
        if (s.Phase == TonearmPhase.Unavailable)
            // The record never left the platter, so recovery is a plain cue — no sleeve, no slide-out.
            return !i.Error && i.PlayWhenReady
                ? Enter(s, TonearmPhase.Cue, now, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = true }
                : s;

        // 3. boundary edges (the caller sets these for exactly one fold)
        switch (i.Boundary)
        {
            case DeckBoundary.NaturalSameAlbum or DeckBoundary.SkipSameAlbum or DeckBoundary.RepeatOne:
                if (LiftStaged(in s, now, TonearmPhase.RecueSwing, TonearmPhase.Idle)) return s;
                // Cold deck (in the sleeve, or braked after an auto-return): just cue it again.
                if (!s.RecordOut || !s.PlatterOn) return CueFrom(s, in i);
                // Same record, next track: lift, swing back to the lead-in, drop. The platter NEVER stops.
                return LiftThen(s, in i, i.Boundary == DeckBoundary.SkipSameAlbum ? SkipLiftMs : LiftMs,
                    TonearmPhase.RecueSwing, TonearmPhase.Idle, armNow, liftNow, null, RecueSwingMs);

            case DeckBoundary.NaturalNewAlbum or DeckBoundary.SkipNewAlbum:
                if (LiftStaged(in s, now, TonearmPhase.SwingToRest, TonearmPhase.SleeveIn)) return s;
                // Nothing on the platter: the only visible change is the artwork. CoverGen is the one thing in this
                // machine a repeated fold could double-count, so it is the one thing guarded against it.
                if (!s.RecordOut)
                    return s.Phase == TonearmPhase.CoverSwap && s.SinceMs == now
                        ? s
                        : Enter(s, TonearmPhase.CoverSwap, now, Dur(CoverSwapMs, i), RestDeg, RestDeg, 1f) with { CoverGen = s.CoverGen + 1 };
                // A different record: lift, return, sleeve it, swap the cover, slide the new one out. The brake trips
                // 700 ms into the return swing — the platter is still coasting while the arm travels.
                return LiftThen(s, in i, i.Boundary == DeckBoundary.SkipNewAlbum ? SkipLiftMs : LiftMs,
                    TonearmPhase.SwingToRest, TonearmPhase.SleeveIn, armNow, liftNow, ChangePlatterOffMs, ChangeSwingMs);
        }

        // 4. queue end → ride the run-out
        if (i.QueueEnded && s.Phase is TonearmPhase.Tracking or TonearmPhase.Lower)
            return Enter(s, TonearmPhase.RunOut, now, SwingMsFor(armNow, RunOutDeg, RunOutBaseMs, RunOutPerSpanMs, in i), armNow, RunOutDeg, 0f);

        // 5. headshell drag
        if (i.ScrubTargetMs is not null && s.RecordOut && s.Phase is not (TonearmPhase.Dragging or TonearmPhase.Lift))
            return LiftThen(s, in i, SkipLiftMs, TonearmPhase.Dragging, TonearmPhase.Idle, armNow, liftNow, null, 0);
        if (s.Phase == TonearmPhase.Dragging)
        {
            if (i.ScrubTargetMs is null)
            {
                // Released: swing from where the headshell WAS (ToDeg tracks the drag below — `armNow` has already
                // fallen back to the transport position now that the scrub target is gone) to the committed target.
                float from = s.ToDeg, to = AngleOf(i.FracOf(i.SeekTargetMs ?? i.PositionMs));
                return Enter(s, TonearmPhase.SeekSwing, now, SwingMsFor(from, to, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), from, to, 1f)
                    with { ResumeDown = i.PlayWhenReady, PlatterOn = i.PlayWhenReady || s.PlatterOn };
            }
            return s.ToDeg == armNow ? s : s with { ToDeg = armNow };   // remember where the headshell is
        }

        // 6. committed seek
        if (i.SeekTargetMs is { } target && s.Phase is TonearmPhase.Tracking or TonearmPhase.Paused)
        {
            float to = AngleOf(i.FracOf(target));
            if (MathF.Abs(to - armNow) < SeekDeadZoneDeg) return s;     // below a rendered quantum: do not twitch
            var st = s with { ToDeg = to, ResumeDown = i.PlayWhenReady };
            return s.Phase == TonearmPhase.Paused
                // Already up: swing straight across and stay up.
                ? Enter(st, TonearmPhase.SeekSwing, now, SwingMsFor(armNow, to, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), armNow, to, 1f)
                : LiftThen(st, in i, SeekLiftMs, TonearmPhase.SeekSwing, TonearmPhase.Idle, armNow, liftNow, null, 0);
        }

        // 7. buffering → hover (the platter keeps turning; only the stylus leaves the groove)
        if (i.Buffering && s.Phase is TonearmPhase.Tracking or TonearmPhase.Lower or TonearmPhase.SpinUp)
        {
            float to = i.Frac < BufferLeadInFrac ? LeadInDeg : armNow;   // a lead-in stall waits over the lead-in
            return LiftThen(s with { ToDeg = to }, in i, BufferLiftMs, TonearmPhase.Hover, TonearmPhase.Idle, armNow, liftNow, null, 0)
                with { PlatterOn = true };
        }
        if (s.Phase == TonearmPhase.Hover)
        {
            if (!i.PlayWhenReady) return Enter(s, TonearmPhase.Paused, now, 0, armNow, armNow, 1f) with { PlatterOn = false };
            // Buffered is not enough — the hover clears when audio is actually ADVANCING again.
            if (!i.Buffering && i.Advancing) { float a = AngleOf(i.Frac); return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), a, a, 1f); }
            return s;
        }

        // 8. pause / resume
        if (s.Phase == TonearmPhase.Tracking && !i.PlayWhenReady && !i.QueueEnded)
            return LiftThen(s, in i, LiftMs, TonearmPhase.Paused, TonearmPhase.Idle, armNow, liftNow, null, 0);
        if (s.Phase == TonearmPhase.Paused && i.PlayWhenReady && !i.Buffering)
            return Enter(s, TonearmPhase.SpinUp, now, Dur(SpinUpMs, i), armNow, armNow, 1f) with { PlatterOn = true };

        // 9. idle / stopped → play
        if ((s.Phase is TonearmPhase.Idle or TonearmPhase.Stopped) && i.PlayWhenReady) return CueFrom(s, in i);

        // 10. the current leg's timer
        return Timers(s, in i);
    }

    /// <summary>
    /// Is the reaction this rule is about to stage ALREADY staged, on this very tick? The clock folds a boundary edge
    /// eagerly and then ticks, and both calls can land on the same <c>NowMs</c> (one frame's time); matching on the
    /// lift's destination (rather than merely on "a lift") keeps a pause-lift from swallowing a boundary.
    /// </summary>
    static bool LiftStaged(in TonearmState s, long now, TonearmPhase afterLift, TonearmPhase afterRest)
        => s.Phase == TonearmPhase.Lift && s.SinceMs == now && s.AfterLift == afterLift && s.AfterRest == afterRest;

    static TonearmState Timers(TonearmState s, in DeckInput i)
        => s.PhaseMs > 0 && i.NowMs - s.SinceMs >= s.PhaseMs ? Advance(s, in i) : s;

    /// <summary>The current leg's deadline passed: hand over to the next one. Public so tests can pin the chain.</summary>
    public static TonearmState Advance(TonearmState s, in DeckInput i)
    {
        long now = i.NowMs;
        switch (s.Phase)
        {
            case TonearmPhase.SlideOut:    return Enter(s, TonearmPhase.Cue, now, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { RecordOut = true, PlatterOn = true };
            case TonearmPhase.Cue:         return Enter(s, TonearmPhase.SwingToLead, now, Dur(SwingLeadMs, i), RestDeg, LeadInDeg, 1f);
            case TonearmPhase.SwingToLead: return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), LeadInDeg, LeadInDeg, 1f);
            case TonearmPhase.RecueSwing:  return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), LeadInDeg, LeadInDeg, 1f);
            case TonearmPhase.SpinUp:      return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), s.ToDeg, s.ToDeg, 1f);
            case TonearmPhase.SeekSwing:   return s.ResumeDown
                ? Enter(s, TonearmPhase.Lower, now, Dur(SeekLowerMs, i), s.ToDeg, s.ToDeg, 1f)
                : Enter(s, TonearmPhase.Paused, now, 0, s.ToDeg, s.ToDeg, 1f);
            case TonearmPhase.Lower:       return Enter(s, TonearmPhase.Tracking, now, 0, s.ToDeg, s.ToDeg, 0f) with { ThumpAtMs = now };

            case TonearmPhase.Lift: return s.AfterLift switch
            {
                TonearmPhase.SeekSwing   => Enter(s, TonearmPhase.SeekSwing, now, SwingMsFor(s.FromDeg, s.ToDeg, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), s.FromDeg, s.ToDeg, 1f),
                TonearmPhase.RecueSwing  => Enter(s, TonearmPhase.RecueSwing, now, s.SwingMs, s.FromDeg, LeadInDeg, 1f),
                TonearmPhase.SwingToRest => Enter(s, TonearmPhase.SwingToRest, now, s.SwingMs, s.FromDeg, RestDeg, 1f),
                TonearmPhase.Hover       => Enter(s, TonearmPhase.Hover, now, 0, s.ToDeg, s.ToDeg, 1f),
                TonearmPhase.Dragging    => Enter(s, TonearmPhase.Dragging, now, 0, s.FromDeg, s.FromDeg, 1f),
                _                        => Enter(s, TonearmPhase.Paused, now, 0, s.FromDeg, s.FromDeg, 1f) with { PlatterOn = false },
            };

            case TonearmPhase.SwingToRest: return s.AfterRest switch
            {
                TonearmPhase.SleeveIn    => Enter(s, TonearmPhase.SleeveIn, now, Dur(SleeveInMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = false },
                TonearmPhase.Unavailable => Enter(s, TonearmPhase.Unavailable, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false },
                TonearmPhase.Stopped     => Enter(s, TonearmPhase.Stopped, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false },
                _                        => Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false, RecordOut = false },
            };

            case TonearmPhase.SleeveIn: return i.HasTrack
                ? Enter(s, TonearmPhase.CoverSwap, now, Dur(CoverSwapMs, i), RestDeg, RestDeg, 1f) with { RecordOut = false, CoverGen = s.CoverGen + 1 }
                : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { RecordOut = false, PlatterOn = false };

            case TonearmPhase.CoverSwap: return i.PlayWhenReady && !i.Error
                ? Enter(s, TonearmPhase.SlideOut, now, Dur(SlideMs, i), RestDeg, RestDeg, 1f)
                : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f);

            case TonearmPhase.RunOut: return Enter(s, TonearmPhase.LockedGroove, now,
                Dur(i.Rpm > Rpm45Threshold ? LockedGroove45Ms : LockedGroove33Ms, i), RunOutDeg, RunOutDeg, 0f);

            // Auto-return: lift off the locked groove, swing home, brake 500 ms into the swing, park.
            case TonearmPhase.LockedGroove: return Enter(s, TonearmPhase.Lift, now, Dur(AutoLiftMs, i), RunOutDeg, RestDeg, 0f) with
            {
                AfterLift = TonearmPhase.SwingToRest, AfterRest = TonearmPhase.Stopped, SwingMs = Dur(AutoSwingMs, i),
                PlatterOffAtMs = Math.Max(1L, now + (long)(Dur(AutoLiftMs, i) + Dur(AutoPlatterOffMs, i))),
            };

            default: return s;
        }
    }

    static TonearmState Enter(TonearmState s, TonearmPhase p, long now, float ms, float from, float to, float liftFrom)
        => s with { Phase = p, SinceMs = now, PhaseMs = ms, FromDeg = from, ToDeg = to, LiftFrom = liftFrom };

    /// <summary>
    /// Lift (<paramref name="liftMs"/>, resuming from wherever the arm already is) and then hand over to
    /// <paramref name="afterLift"/>. <paramref name="platterOffAfterSwingMs"/> schedules the brake relative to the
    /// FOLLOWING swing's START — <c>0</c> is "with the swing", <c>null</c> is "never".
    /// </summary>
    static TonearmState LiftThen(TonearmState s, in DeckInput i, float liftMs, TonearmPhase afterLift, TonearmPhase afterRest,
                                 float armNow, float liftNow, float? platterOffAfterSwingMs, float swingMs)
    {
        float lift = Dur(liftMs, i);
        long off = platterOffAfterSwingMs is { } d ? Math.Max(1L, i.NowMs + (long)(lift + Dur(d, i))) : 0L;
        return Enter(s, TonearmPhase.Lift, i.NowMs, lift, armNow, afterLift == TonearmPhase.SwingToRest ? RestDeg : s.ToDeg, liftNow) with
        { AfterLift = afterLift, AfterRest = afterRest, SwingMs = Dur(swingMs, i), PlatterOffAtMs = off };
    }

    /// <summary>Start playing: a record already on the platter only needs a cue; one in its sleeve slides out first.</summary>
    static TonearmState CueFrom(TonearmState s, in DeckInput i) => s.RecordOut
        ? Enter(s, TonearmPhase.Cue, i.NowMs, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = true }
        : Enter(s, TonearmPhase.SlideOut, i.NowMs, Dur(SlideMs, i), RestDeg, RestDeg, 1f);

    /// <summary>The drawable frame for a state at a moment. Pure: no clock, no state mutation.</summary>
    public static TonearmFrame Sample(in TonearmState s, in DeckInput i)
    {
        long elapsed = i.NowMs - s.SinceMs; if (elapsed < 0) elapsed = 0;
        float t = s.PhaseMs > 0 ? DeckEase.Clamp01(elapsed / s.PhaseMs) : 1f;
        float arm, lift, slide = s.RecordOut ? 1f : 0f;
        switch (s.Phase)
        {
            case TonearmPhase.Tracking:     arm = AngleOf(i.Frac); lift = 0f; break;
            case TonearmPhase.Dragging:     arm = AngleOf(i.FracOf(i.ScrubTargetMs ?? i.PositionMs)); lift = 1f; break;
            case TonearmPhase.RunOut:       arm = s.FromDeg + (s.ToDeg - s.FromDeg) * t; lift = 0f; break;   // linear: a groove, not a swing
            case TonearmPhase.LockedGroove: arm = RunOutDeg; lift = 0f; break;
            case TonearmPhase.SwingToLead or TonearmPhase.SeekSwing or TonearmPhase.RecueSwing or TonearmPhase.SwingToRest:
                                            arm = s.FromDeg + (s.ToDeg - s.FromDeg) * DeckEase.Std(t); lift = 1f; break;
            case TonearmPhase.Lift:         arm = s.FromDeg; lift = s.LiftFrom + (1f - s.LiftFrom) * DeckEase.LiftUp(t); break;
            case TonearmPhase.Lower:        arm = s.ToDeg; lift = 1f - DeckEase.Damped(t); break;
            case TonearmPhase.Hover:        arm = s.ToDeg; lift = i.ReducedMotion ? 1f : 1f + 0.4f * (0.5f - 0.5f * MathF.Cos(2f * MathF.PI * (elapsed % (long)BobPeriodMs) / BobPeriodMs)); break;
            case TonearmPhase.SlideOut:     arm = RestDeg; lift = 1f; slide = t; break;
            case TonearmPhase.SleeveIn:     arm = RestDeg; lift = 1f; slide = 1f - t; break;
            default:                        arm = s.Phase is TonearmPhase.Paused or TonearmPhase.SpinUp ? s.ToDeg : RestDeg; lift = 1f; break;
        }
        float platter = s.PlatterOn && !i.ReducedMotion ? i.Rpm * 6f : 0f;
        bool thump = s.ThumpAtMs != 0 && i.NowMs - s.ThumpAtMs < ThumpMs && !i.ReducedMotion;
        return new TonearmFrame(arm, lift, platter, slide, thump, NameOf(in s));
    }

    /// <summary>The phase's name in the shared deck vocabulary, ignoring what it is on its way to.</summary>
    public static DeckPhaseName NameOf(TonearmPhase p) => p switch
    {
        TonearmPhase.SlideOut or TonearmPhase.Cue or TonearmPhase.SwingToLead        => DeckPhaseName.Cueing,
        TonearmPhase.Lower                                                           => DeckPhaseName.NeedleDown,
        TonearmPhase.Tracking                                                        => DeckPhaseName.Playing,
        TonearmPhase.Lift                                                            => DeckPhaseName.Pausing,
        TonearmPhase.Paused                                                          => DeckPhaseName.Paused,
        TonearmPhase.SpinUp                                                          => DeckPhaseName.SpinningUp,
        TonearmPhase.SeekSwing or TonearmPhase.Dragging                              => DeckPhaseName.Seeking,
        TonearmPhase.RecueSwing                                                      => DeckPhaseName.NextTrack,
        TonearmPhase.SwingToRest or TonearmPhase.SleeveIn or TonearmPhase.CoverSwap  => DeckPhaseName.ChangingRecord,
        TonearmPhase.RunOut                                                          => DeckPhaseName.RunOut,
        TonearmPhase.LockedGroove                                                    => DeckPhaseName.LockedGroove,
        TonearmPhase.Hover                                                           => DeckPhaseName.Buffering,
        TonearmPhase.Stopped                                                         => DeckPhaseName.Stopped,
        TonearmPhase.Unavailable                                                     => DeckPhaseName.Unavailable,
        _                                                                            => DeckPhaseName.Idle,
    };

    /// <summary>
    /// The phase's name told with its INTENT: a lift is only "pausing" when it is not the first beat of a recue, a
    /// seek, a record change, an error retreat or the auto-return. Both retreats share <see cref="TonearmPhase.Lift"/>
    /// and <see cref="TonearmPhase.SwingToRest"/>, so the destination is what distinguishes them.
    /// </summary>
    public static DeckPhaseName NameOf(in TonearmState s) => s.Phase switch
    {
        TonearmPhase.Lift => s.AfterRest switch
        {
            TonearmPhase.Stopped     => DeckPhaseName.AutoReturn,
            TonearmPhase.SleeveIn    => DeckPhaseName.ChangingRecord,
            TonearmPhase.Unavailable => DeckPhaseName.Error,
            _ => s.AfterLift switch
            {
                TonearmPhase.RecueSwing                     => DeckPhaseName.NextTrack,
                TonearmPhase.SeekSwing or TonearmPhase.Dragging => DeckPhaseName.Seeking,
                TonearmPhase.Hover                          => DeckPhaseName.Buffering,
                _                                           => DeckPhaseName.Pausing,
            },
        },
        TonearmPhase.SwingToRest => s.AfterRest switch
        {
            TonearmPhase.Stopped     => DeckPhaseName.AutoReturn,
            TonearmPhase.Unavailable => DeckPhaseName.Error,
            _                        => DeckPhaseName.ChangingRecord,
        },
        _ => NameOf(s.Phase),
    };
}

/// <summary>
/// The headshell drag's arithmetic: deck-space point → normalized position, and arm-local point → deck space. Pure
/// geometry, shared by the record family's faces and unit-tested without a pointer.
/// </summary>
public static class TonearmGeometry
{
    /// <summary>Inner edge of the label as a fraction of the record's radius — the groove band starts here.</summary>
    public const float LabelRadiusFrac = 0.34f;
    /// <summary>Width of the groove band as a fraction of the radius (label edge → rim).</summary>
    public const float GrooveBandFrac = 1f - LabelRadiusFrac;

    /// <summary>
    /// Where a deck-space point falls in the track: the RIM is position 0 (the lead-in) and the LABEL edge is
    /// position 1 (the run-out), because that is the direction a stylus actually travels.
    /// </summary>
    public static float FracFromDeckPoint(float x, float y, float platterCx, float platterCy, float recordD)
    {
        float dx = x - platterCx, dy = y - platterCy;
        float d = MathF.Sqrt(dx * dx + dy * dy) / (recordD * 0.5f);
        return DeckEase.Clamp01(1f - (d - LabelRadiusFrac) / GrooveBandFrac);
    }

    /// <summary>
    /// Map a point inside the arm's own box to deck space, honouring the arm's rotation about its pivot
    /// (the face's <c>TransformOrigin (.5, .08)</c>).
    /// </summary>
    public static (float X, float Y) ArmLocalToDeck(float lx, float ly, float armDeg, float armX, float armY, float armW, float armH)
    {
        float px = armW * 0.5f, py = armH * 0.08f;
        float r = armDeg * (MathF.PI / 180f), c = MathF.Cos(r), s = MathF.Sin(r);
        float dx = lx - px, dy = ly - py;
        return (armX + px + c * dx - s * dy, armY + py + s * dx + c * dy);
    }
}
