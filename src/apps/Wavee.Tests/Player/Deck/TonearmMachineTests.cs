using System;
using Wavee.Core;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>
/// Pins the record family's tonearm: <see cref="TonearmMachine"/>'s transition table, its timings and the frame it
/// hands the face. Everything here is driven the way the deck's ticker drives it — a 33 ms stepper over a
/// <see cref="DeckInput"/> whose <c>NowMs</c> advances — so a rule that only holds when the caller lands exactly on a
/// deadline is not a rule this suite will believe.
///
/// <para>Every expected time is DERIVED from the machine's own constants (never a literal), so retuning a phase
/// retunes the tests with it; the one test that writes the numbers down is
/// <see cref="Constants_PinTheTimingTable"/>, which is the point of pinning them.</para>
/// </summary>
public sealed class TonearmMachineTests
{
    const long T0 = 100_000;          // a plausible Environment.TickCount64: never 0, so the brake sentinel is unambiguous
    const long StepMs = 33;           // the deck's 30 Hz cadence
    const long TrackMs = 236_000;
    const float Rpm33 = 33.333f, Rpm45 = 45f;

    // ── the stepper ────────────────────────────────────────────────────────────────────────────────────────────────

    static DeckInput MakeInput(
        long nowMs = T0, bool hasTrack = true, PlaybackPhase phase = PlaybackPhase.Playing, bool playWhenReady = true,
        bool advancing = true, bool buffering = false, bool error = false, bool queueEnded = false,
        DeckBoundary boundary = DeckBoundary.None, long? seekTargetMs = null, long? scrubTargetMs = null,
        long positionMs = 0, long durationMs = TrackMs, bool repeatOne = false, float rpm = Rpm33, bool reducedMotion = false)
        => new(nowMs, hasTrack, phase, playWhenReady, advancing, buffering, error, queueEnded, boundary,
               seekTargetMs, scrubTargetMs, positionMs, durationMs, repeatOne, rpm, reducedMotion);

    /// <summary>One fold at an exact wall clock.</summary>
    static void StepAt(ref TonearmState s, DeckInput baseInput, long nowMs)
    {
        var i = baseInput with { NowMs = nowMs };
        s = TonearmMachine.Step(s, in i);
    }

    /// <summary>
    /// Step every 33 ms from <paramref name="fromMs"/> through <paramref name="untilMs"/>, ALWAYS landing exactly on
    /// <paramref name="untilMs"/> so a deadline assertion is about the machine and not about the stride.
    /// </summary>
    static void Run(ref TonearmState s, DeckInput baseInput, long fromMs, long untilMs, Func<long, DeckInput, DeckInput>? mutate = null)
    {
        for (long t = fromMs; ; )
        {
            var i = baseInput with { NowMs = t };
            if (mutate is not null) i = mutate(t, i);
            s = TonearmMachine.Step(s, in i);
            if (t >= untilMs) break;
            t = Math.Min(t + StepMs, untilMs);
        }
    }

    /// <summary>The same walk, counting the frames on which the stylus lands.</summary>
    static int CountThumps(ref TonearmState s, DeckInput baseInput, long fromMs, long untilMs)
    {
        int n = 0;
        for (long t = fromMs; ; )
        {
            var i = baseInput with { NowMs = t };
            s = TonearmMachine.Step(s, in i);
            if (TonearmMachine.Sample(in s, in i).Thump) n++;
            if (t >= untilMs) break;
            t = Math.Min(t + StepMs, untilMs);
        }
        return n;
    }

    static TonearmFrame FrameAt(in TonearmState s, DeckInput baseInput, long nowMs)
    {
        var i = baseInput with { NowMs = nowMs };
        return TonearmMachine.Sample(in s, in i);
    }

    static long PosOf(float frac) => (long)(frac * TrackMs);

    /// <summary>A deck mid-song: seeded straight into the groove, exactly like a Cover → Player swap.</summary>
    static (TonearmState State, DeckInput Input) TrackingAt(float frac, float rpm = Rpm33, bool reducedMotion = false)
    {
        var play = MakeInput(positionMs: PosOf(frac), rpm: rpm, reducedMotion: reducedMotion);
        return (TonearmMachine.Seed(in play), play);
    }

    /// <summary>…and the same deck after the user pressed pause and the arm finished lifting.</summary>
    static (TonearmState State, DeckInput Input) PausedAt(float frac)
    {
        var (s, play) = TrackingAt(frac);
        var pause = play with { PlayWhenReady = false, Advancing = false, Phase = PlaybackPhase.Paused };
        StepAt(ref s, pause, T0);
        long settled = T0 + (long)TonearmMachine.LiftMs;
        StepAt(ref s, pause, settled);
        return (s, pause with { NowMs = settled });
    }

    static void Near(float expected, float actual, float tol = 0.001f)
        => Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual} (tolerance {tol})");

    // ── constants ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The timings are the DESIGN — an SL-1200's 0.7 s spin-up, a 1.8 s locked groove, a 1.4 s auto-return — and every
    /// other test derives from them, so this is the one place they are written down.
    /// </summary>
    [Fact]
    public void Constants_PinTheTimingTable()
    {
        Assert.Equal(600f, TonearmMachine.SlideMs);
        Assert.Equal(300f, TonearmMachine.CueHoldMs);
        Assert.Equal(1200f, TonearmMachine.SwingLeadMs);
        Assert.Equal(900f, TonearmMachine.LowerMs);
        Assert.Equal(450f, TonearmMachine.LiftMs);
        Assert.Equal(700f, TonearmMachine.SpinUpMs);
        Assert.Equal(400f, TonearmMachine.SeekLiftMs);
        Assert.Equal(300f, TonearmMachine.SeekSwingBaseMs);
        Assert.Equal(600f, TonearmMachine.SeekSwingPerSpanMs);
        Assert.Equal(700f, TonearmMachine.SeekLowerMs);
        Assert.Equal(900f, TonearmMachine.RecueSwingMs);
        Assert.Equal(300f, TonearmMachine.SkipLiftMs);
        Assert.Equal(1200f, TonearmMachine.ChangeSwingMs);
        Assert.Equal(700f, TonearmMachine.ChangePlatterOffMs);
        Assert.Equal(600f, TonearmMachine.SleeveInMs);
        Assert.Equal(350f, TonearmMachine.CoverSwapMs);
        Assert.Equal(400f, TonearmMachine.RunOutBaseMs);
        Assert.Equal(800f, TonearmMachine.RunOutPerSpanMs);
        Assert.Equal(1800f, TonearmMachine.LockedGroove33Ms);
        Assert.Equal(1333f, TonearmMachine.LockedGroove45Ms);
        Assert.Equal(500f, TonearmMachine.AutoLiftMs);
        Assert.Equal(1400f, TonearmMachine.AutoSwingMs);
        Assert.Equal(500f, TonearmMachine.AutoPlatterOffMs);
        Assert.Equal(1200f, TonearmMachine.ErrorSwingMs);
        Assert.Equal(1800f, TonearmMachine.BobPeriodMs);
        Assert.Equal(400f, TonearmMachine.BufferLiftMs);
        Assert.Equal(150f, TonearmMachine.ReducedMs);
        Assert.Equal(120f, TonearmMachine.ThumpMs);
        Assert.Equal(0.02f, TonearmMachine.BufferLeadInFrac);
    }

    /// <summary>The geometry the whole face is drawn against: rest, lead-in, the sweep and the run-out.</summary>
    [Fact]
    public void Constants_PinTheAngleTable()
    {
        Assert.Equal(-34f, TonearmMachine.RestDeg);
        Assert.Equal(-20f, TonearmMachine.LeadInDeg);
        Assert.Equal(17f, TonearmMachine.SpanDeg);
        Assert.Equal(-3f, TonearmMachine.RunOutDeg);
        Near(TonearmMachine.LeadInDeg, TonearmMachine.AngleOf(0f));
        Near(TonearmMachine.RunOutDeg, TonearmMachine.AngleOf(1f));
        Near(TonearmMachine.LeadInDeg, TonearmMachine.AngleOf(-5f));      // clamped
        Near(TonearmMachine.RunOutDeg, TonearmMachine.AngleOf(5f));
    }

    // ── cueing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Play from a cold deck: slide out (600) · cue (300) · swing (1200) · lower (900) · tracking = 3.0 s.</summary>
    [Fact]
    public void IdleToPlay_RunsTheWholeCueingTimeline()
    {
        var s = TonearmState.Initial;
        var play = MakeInput();

        StepAt(ref s, play, T0);
        Assert.Equal(TonearmPhase.SlideOut, s.Phase);
        Assert.Equal(TonearmMachine.SlideMs, s.PhaseMs);
        Assert.False(s.RecordOut);

        Run(ref s, play, T0 + StepMs, T0 + 599);
        Assert.Equal(TonearmPhase.SlideOut, s.Phase);

        StepAt(ref s, play, T0 + 600);
        Assert.Equal(TonearmPhase.Cue, s.Phase);
        Assert.Equal(TonearmMachine.CueHoldMs, s.PhaseMs);
        Assert.True(s.RecordOut);
        Assert.True(s.PlatterOn);                                          // the platter is up before the arm moves

        Run(ref s, play, T0 + 633, T0 + 899);
        Assert.Equal(TonearmPhase.Cue, s.Phase);

        StepAt(ref s, play, T0 + 900);
        Assert.Equal(TonearmPhase.SwingToLead, s.Phase);
        Assert.Equal(TonearmMachine.SwingLeadMs, s.PhaseMs);

        Run(ref s, play, T0 + 933, T0 + 2099);
        Assert.Equal(TonearmPhase.SwingToLead, s.Phase);

        StepAt(ref s, play, T0 + 2100);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.LowerMs, s.PhaseMs);

        Run(ref s, play, T0 + 2133, T0 + 2999);
        Assert.Equal(TonearmPhase.Lower, s.Phase);

        StepAt(ref s, play, T0 + 3000);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        Assert.Equal(0f, FrameAt(in s, play, T0 + 3000).Lift);
        Assert.Equal(1f, FrameAt(in s, play, T0 + 3000).Slide);
    }

    /// <summary>Riding the groove, the arm angle IS the position: −20 + 17·p.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Tracking_ArmAngleIsLeadInPlusSpanTimesPosition(float frac)
    {
        var (s, play) = TrackingAt(frac);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        var f = FrameAt(in s, play, T0);
        Near(TonearmMachine.LeadInDeg + TonearmMachine.SpanDeg * frac, f.ArmDeg, 0.01f);
        Assert.Equal(0f, f.Lift);
        Assert.Equal(DeckPhaseName.Playing, f.Name);
    }

    /// <summary>…and it keeps following as the transport advances: no rule fires, the arm just tracks.</summary>
    [Fact]
    public void Tracking_FollowsAnAdvancingPosition()
    {
        var (s, play) = TrackingAt(0f);
        Run(ref s, play, T0, T0 + 5000, (t, i) => i with { PositionMs = t - T0 });
        Assert.Equal(TonearmPhase.Tracking, s.Phase);

        var last = play with { NowMs = T0 + 5000, PositionMs = 5000 };
        Near(TonearmMachine.AngleOf(5000f / TrackMs), TonearmMachine.Sample(in s, in last).ArmDeg, 0.01f);
    }

    // ── pause / resume ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Pause is a cue lever, not a stop: the arm lifts for 450 ms over the SAME groove, then the platter brakes.</summary>
    [Fact]
    public void Pause_LiftsForLiftMs_ThenBrakes_AndLeavesTheArmWhereItWas()
    {
        var (s, play) = TrackingAt(0.5f);
        float armBefore = FrameAt(in s, play, T0).ArmDeg;
        var pause = play with { PlayWhenReady = false, Advancing = false, Phase = PlaybackPhase.Paused };

        StepAt(ref s, pause, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.LiftMs, s.PhaseMs);
        Assert.True(s.PlatterOn);                                          // still turning while the arm rises

        Run(ref s, pause, T0 + StepMs, T0 + 449);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.True(s.PlatterOn);
        Assert.True(FrameAt(in s, pause, T0 + 449).Lift > 0.9f);

        StepAt(ref s, pause, T0 + (long)TonearmMachine.LiftMs);
        Assert.Equal(TonearmPhase.Paused, s.Phase);
        Assert.False(s.PlatterOn);
        var f = FrameAt(in s, pause, T0 + (long)TonearmMachine.LiftMs);
        Near(armBefore, f.ArmDeg, 0.01f);
        Assert.Equal(1f, f.Lift);
        Assert.Equal(0f, f.PlatterTargetDegPerSec);
        Assert.Equal(DeckPhaseName.Paused, f.Name);
    }

    /// <summary>Resume: 700 ms back up to speed under a lifted arm, then a 900 ms damped descent.</summary>
    [Fact]
    public void Resume_SpinsUpThenLowers()
    {
        var (s, paused) = PausedAt(0.5f);
        long t0 = paused.NowMs;
        var play = paused with { PlayWhenReady = true, Advancing = true, Phase = PlaybackPhase.Playing };

        StepAt(ref s, play, t0);
        Assert.Equal(TonearmPhase.SpinUp, s.Phase);
        Assert.Equal(TonearmMachine.SpinUpMs, s.PhaseMs);
        Assert.True(s.PlatterOn);
        Assert.Equal(1f, FrameAt(in s, play, t0).Lift);                    // still up while the platter recovers

        StepAt(ref s, play, t0 + (long)TonearmMachine.SpinUpMs);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.LowerMs, s.PhaseMs);

        StepAt(ref s, play, t0 + (long)(TonearmMachine.SpinUpMs + TonearmMachine.LowerMs));
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
    }

    /// <summary>The stylus lands ONCE. A thump that repeated for the whole 120 ms window would fire the dust puff four times.</summary>
    [Fact]
    public void Resume_DropsTheStylusExactlyOnce()
    {
        var (s, paused) = PausedAt(0.5f);
        long t0 = paused.NowMs;
        var play = paused with { PlayWhenReady = true, Advancing = true, Phase = PlaybackPhase.Playing };

        int thumps = CountThumps(ref s, play, t0, t0 + 3000);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        Assert.Equal(1, thumps);
    }

    // ── seeking ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A seek costs 300 ms of setup plus 600 ms per full span: the arm's travel READS as distance.</summary>
    [Theory]
    [InlineData(0f, 1f, 900f)]
    [InlineData(0f, 0.25f, 450f)]
    [InlineData(1f, 0.5f, 600f)]
    public void CommittedSeek_SwingDurationScalesWithDistance(float fromFrac, float toFrac, float expectedSwingMs)
    {
        var (s, play) = TrackingAt(fromFrac);
        var seek = play with { SeekTargetMs = PosOf(toFrac) };

        StepAt(ref s, seek, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.SeekLiftMs, s.PhaseMs);
        Assert.Equal(DeckPhaseName.Seeking, FrameAt(in s, seek, T0).Name);

        StepAt(ref s, seek, T0 + (long)TonearmMachine.SeekLiftMs);
        Assert.Equal(TonearmPhase.SeekSwing, s.Phase);
        Near(expectedSwingMs, s.PhaseMs, 0.05f);

        long swungAt = T0 + (long)(TonearmMachine.SeekLiftMs + expectedSwingMs);
        StepAt(ref s, seek, swungAt);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.SeekLowerMs, s.PhaseMs);                // a seek lands faster than a cold cue
        Near(TonearmMachine.AngleOf(toFrac), FrameAt(in s, seek, swungAt).ArmDeg, 0.01f);
    }

    /// <summary>Seeking while paused never puts the stylus down — the arm swings across and stays up.</summary>
    [Fact]
    public void SeekWhilePaused_SwingsWithoutLowering_AndStaysUp()
    {
        var (s, paused) = PausedAt(0.2f);
        long t0 = paused.NowMs;
        var seek = paused with { SeekTargetMs = PosOf(0.8f) };

        StepAt(ref s, seek, t0);
        Assert.Equal(TonearmPhase.SeekSwing, s.Phase);                      // no lift: it is already up
        Assert.False(s.ResumeDown);
        Assert.False(s.PlatterOn);
        float swing = s.PhaseMs;
        Assert.Equal(1f, FrameAt(in s, seek, t0 + (long)(swing / 2)).Lift);

        StepAt(ref s, seek, t0 + (long)swing);
        Assert.Equal(TonearmPhase.Paused, s.Phase);
        var f = FrameAt(in s, seek, t0 + (long)swing);
        Assert.Equal(1f, f.Lift);
        Near(TonearmMachine.AngleOf(0.8f), f.ArmDeg, 0.01f);
        Assert.False(s.PlatterOn);
    }

    /// <summary>A seek shorter than a rendered quantum is not a journey: the arm must not twitch for it.</summary>
    [Fact]
    public void SubQuantumSeek_IsIgnored()
    {
        var (s, play) = TrackingAt(0.5f);
        var before = s;
        var seek = play with { SeekTargetMs = PosOf(0.5f) + 300 };          // ≈0.02° of arm travel

        StepAt(ref s, seek, T0);
        Assert.Equal(before, s);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
    }

    /// <summary>Dragging the headshell: a short lift, then the arm is the pointer; release swings to the commit and lands.</summary>
    [Fact]
    public void HeadshellDrag_LiftsShort_FollowsTheScrub_AndLandsOnRelease()
    {
        var (s, play) = TrackingAt(0.5f);
        var drag = play with { ScrubTargetMs = PosOf(0.5f) };

        StepAt(ref s, drag, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.SkipLiftMs, s.PhaseMs);                 // a drag lifts fast

        long dragging = T0 + (long)TonearmMachine.SkipLiftMs;
        StepAt(ref s, drag, dragging);
        Assert.Equal(TonearmPhase.Dragging, s.Phase);
        Assert.Equal(DeckPhaseName.Seeking, FrameAt(in s, drag, dragging).Name);

        // the arm is the pointer
        var far = drag with { ScrubTargetMs = PosOf(0.9f) };
        StepAt(ref s, far, dragging + StepMs);
        Assert.Equal(TonearmPhase.Dragging, s.Phase);
        Near(TonearmMachine.AngleOf(0.9f), FrameAt(in s, far, dragging + StepMs).ArmDeg, 0.01f);

        var near = drag with { ScrubTargetMs = PosOf(0.1f) };
        StepAt(ref s, near, dragging + 2 * StepMs);
        Near(TonearmMachine.AngleOf(0.1f), FrameAt(in s, near, dragging + 2 * StepMs).ArmDeg, 0.01f);
        Assert.Equal(1f, FrameAt(in s, near, dragging + 2 * StepMs).Lift);

        // release: the swing starts from where the headshell WAS, not from the (stale) transport position
        long released = dragging + 3 * StepMs;
        var commit = play with { ScrubTargetMs = null, SeekTargetMs = PosOf(0.1f) };
        StepAt(ref s, commit, released);
        Assert.Equal(TonearmPhase.SeekSwing, s.Phase);
        Near(TonearmMachine.AngleOf(0.1f), s.FromDeg, 0.01f);
        Near(TonearmMachine.AngleOf(0.1f), s.ToDeg, 0.01f);
        Assert.True(s.ResumeDown);

        StepAt(ref s, commit, released + (long)s.PhaseMs);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.SeekLowerMs, s.PhaseMs);
    }

    // ── track boundaries ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Same record, next track: the platter NEVER stops — that is the whole difference from a record change.</summary>
    [Theory]
    [InlineData(DeckBoundary.NaturalSameAlbum, 450f)]
    [InlineData(DeckBoundary.SkipSameAlbum, 300f)]
    [InlineData(DeckBoundary.RepeatOne, 450f)]
    public void SameAlbumBoundary_RecuesToTheLeadIn_WithoutEverStoppingThePlatter(DeckBoundary boundary, float expectedLiftMs)
    {
        var (s, play) = TrackingAt(0.98f);
        var edge = play with { Boundary = boundary };                       // the edge is folded on the OLD position

        StepAt(ref s, edge, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(expectedLiftMs, s.PhaseMs);
        Assert.Equal(TonearmPhase.RecueSwing, s.AfterLift);
        Near(TonearmMachine.AngleOf(0.98f), s.FromDeg, 0.01f);              // it lifts from where the last track ended
        Assert.Equal(0L, s.PlatterOffAtMs);                                 // no brake is ever scheduled
        Assert.Equal(DeckPhaseName.NextTrack, FrameAt(in s, edge, T0).Name);

        var next = play with { PositionMs = 0 };
        long swingAt = T0 + (long)expectedLiftMs;
        StepAt(ref s, next, swingAt);
        Assert.Equal(TonearmPhase.RecueSwing, s.Phase);
        Assert.Equal(TonearmMachine.RecueSwingMs, s.PhaseMs);
        Near(TonearmMachine.LeadInDeg, s.ToDeg);

        long lowerAt = swingAt + (long)TonearmMachine.RecueSwingMs;
        StepAt(ref s, next, lowerAt);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.LowerMs, s.PhaseMs);

        StepAt(ref s, next, lowerAt + (long)TonearmMachine.LowerMs);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);

        // …and the platter was driven on every single one of those folds.
        var (again, replay) = TrackingAt(0.98f);
        StepAt(ref again, replay with { Boundary = boundary }, T0);
        for (long t = T0 + StepMs; t <= T0 + 4000; t += StepMs)
        {
            StepAt(ref again, next, t);
            Assert.True(again.PlatterOn, $"platter stopped at +{t - T0} ms");
        }
        Assert.Equal(TonearmPhase.Tracking, again.Phase);
    }

    /// <summary>
    /// A different album is a RECORD CHANGE: lift · return · sleeve · cover swap · slide out · cue · swing · lower.
    /// The brake trips 700 ms into the return swing, and the artwork ticks over exactly at the cover swap.
    /// </summary>
    [Theory]
    [InlineData(DeckBoundary.NaturalNewAlbum, 450L)]
    [InlineData(DeckBoundary.SkipNewAlbum, 300L)]
    public void NewAlbumBoundary_ChangesTheRecord_CoverGenAtTheSwap_BrakeAtLiftPlus700(DeckBoundary boundary, long liftMs)
    {
        var (s, play) = TrackingAt(0.98f);
        var edge = play with { Boundary = boundary };

        StepAt(ref s, edge, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal((float)liftMs, s.PhaseMs);
        Assert.Equal(TonearmPhase.SleeveIn, s.AfterRest);
        Assert.Equal(T0 + liftMs + (long)TonearmMachine.ChangePlatterOffMs, s.PlatterOffAtMs);
        Assert.Equal(0, s.CoverGen);
        Assert.Equal(DeckPhaseName.ChangingRecord, FrameAt(in s, edge, T0).Name);

        var next = play with { PositionMs = 0 };

        long swingAt = T0 + liftMs;
        StepAt(ref s, next, swingAt);
        Assert.Equal(TonearmPhase.SwingToRest, s.Phase);
        Assert.Equal(TonearmMachine.ChangeSwingMs, s.PhaseMs);
        Assert.True(s.PlatterOn);

        long brakeAt = swingAt + (long)TonearmMachine.ChangePlatterOffMs;
        StepAt(ref s, next, brakeAt - 1);
        Assert.True(s.PlatterOn);                                           // still coasting while the arm travels
        StepAt(ref s, next, brakeAt);
        Assert.False(s.PlatterOn);
        Assert.Equal(0L, s.PlatterOffAtMs);

        long sleeveAt = swingAt + (long)TonearmMachine.ChangeSwingMs;
        StepAt(ref s, next, sleeveAt);
        Assert.Equal(TonearmPhase.SleeveIn, s.Phase);
        Assert.Equal(TonearmMachine.SleeveInMs, s.PhaseMs);
        Assert.Equal(0, s.CoverGen);
        Assert.True(s.RecordOut);

        long swapAt = sleeveAt + (long)TonearmMachine.SleeveInMs;
        StepAt(ref s, next, swapAt);
        Assert.Equal(TonearmPhase.CoverSwap, s.Phase);
        Assert.Equal(TonearmMachine.CoverSwapMs, s.PhaseMs);
        Assert.Equal(1, s.CoverGen);                                        // exactly here, and nowhere else
        Assert.False(s.RecordOut);
        Assert.Equal(0f, FrameAt(in s, next, swapAt).Slide);

        long slideAt = swapAt + (long)TonearmMachine.CoverSwapMs;
        StepAt(ref s, next, slideAt);
        Assert.Equal(TonearmPhase.SlideOut, s.Phase);
        Assert.Equal(TonearmMachine.SlideMs, s.PhaseMs);

        long cueAt = slideAt + (long)TonearmMachine.SlideMs;
        StepAt(ref s, next, cueAt);
        Assert.Equal(TonearmPhase.Cue, s.Phase);
        Assert.True(s.RecordOut);
        Assert.True(s.PlatterOn);

        long leadAt = cueAt + (long)TonearmMachine.CueHoldMs;
        StepAt(ref s, next, leadAt);
        Assert.Equal(TonearmPhase.SwingToLead, s.Phase);

        long lowerAt = leadAt + (long)TonearmMachine.SwingLeadMs;
        StepAt(ref s, next, lowerAt);
        Assert.Equal(TonearmPhase.Lower, s.Phase);

        StepAt(ref s, next, lowerAt + (long)TonearmMachine.LowerMs);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        Assert.Equal(1, s.CoverGen);
    }

    /// <summary>Nothing on the platter yet: a new album is only an artwork change, not a record change.</summary>
    [Fact]
    public void NewAlbumBoundary_WithTheRecordSleeved_OnlySwapsTheCover()
    {
        var s = TonearmState.Initial;
        var edge = MakeInput(boundary: DeckBoundary.NaturalNewAlbum, playWhenReady: false, advancing: false);

        StepAt(ref s, edge, T0);
        Assert.Equal(TonearmPhase.CoverSwap, s.Phase);
        Assert.Equal(1, s.CoverGen);
        Assert.False(s.RecordOut);

        StepAt(ref s, edge with { Boundary = DeckBoundary.None }, T0 + (long)TonearmMachine.CoverSwapMs);
        Assert.Equal(TonearmPhase.Idle, s.Phase);                            // nobody asked to play
    }

    /// <summary>A boundary on a deck that already auto-returned is a plain cue: the record never left the platter.</summary>
    [Fact]
    public void BoundaryWhileStopped_IsAPlainCue_WithNoSlideOut()
    {
        var stoppedSeed = MakeInput(playWhenReady: false, advancing: false, queueEnded: true, phase: PlaybackPhase.Ended);
        var s = TonearmMachine.Seed(in stoppedSeed);
        Assert.Equal(TonearmPhase.Stopped, s.Phase);
        Assert.True(s.RecordOut);
        Assert.False(s.PlatterOn);

        var edge = MakeInput(boundary: DeckBoundary.NaturalSameAlbum);
        StepAt(ref s, edge, T0);
        Assert.Equal(TonearmPhase.Cue, s.Phase);
        Assert.True(s.PlatterOn);

        StepAt(ref s, edge with { Boundary = DeckBoundary.None }, T0 + (long)TonearmMachine.CueHoldMs);
        Assert.Equal(TonearmPhase.SwingToLead, s.Phase);
    }

    /// <summary>Play on an idle deck that still has its record out skips the slide-out too.</summary>
    [Fact]
    public void PlayFromStopped_CuesWithoutSlidingTheRecordOut()
    {
        var stoppedSeed = MakeInput(playWhenReady: false, advancing: false, queueEnded: true, phase: PlaybackPhase.Ended);
        var s = TonearmMachine.Seed(in stoppedSeed);

        StepAt(ref s, MakeInput(), T0);
        Assert.Equal(TonearmPhase.Cue, s.Phase);
        Assert.True(s.PlatterOn);
    }

    // ── queue end ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The end of the queue is not a stop button: run-out · locked groove · auto-return · park.</summary>
    [Theory]
    [InlineData(Rpm33, 1800f)]
    [InlineData(Rpm45, 1333f)]
    public void QueueEnd_RidesTheRunOut_ParksInTheLockedGroove_ThenAutoReturns(float rpm, float lockedMs)
    {
        var (s, play) = TrackingAt(0.5f, rpm);
        float armBefore = FrameAt(in s, play, T0).ArmDeg;
        var ended = play with { QueueEnded = true, PlayWhenReady = false, Advancing = false, Phase = PlaybackPhase.Ended };

        StepAt(ref s, ended, T0);
        Assert.Equal(TonearmPhase.RunOut, s.Phase);
        float runOut = TonearmMachine.RunOutBaseMs
                     + TonearmMachine.RunOutPerSpanMs * MathF.Abs(TonearmMachine.RunOutDeg - armBefore) / TonearmMachine.SpanDeg;
        Near(runOut, s.PhaseMs, 0.05f);
        Assert.Equal(DeckPhaseName.RunOut, FrameAt(in s, ended, T0).Name);

        long lockedAt = T0 + (long)runOut;
        StepAt(ref s, ended, lockedAt);
        Assert.Equal(TonearmPhase.LockedGroove, s.Phase);
        Assert.Equal(lockedMs, s.PhaseMs);
        Near(TonearmMachine.RunOutDeg, FrameAt(in s, ended, lockedAt).ArmDeg);
        Assert.Equal(0f, FrameAt(in s, ended, lockedAt).Lift);              // still in the groove, going nowhere

        long liftAt = lockedAt + (long)lockedMs;
        StepAt(ref s, ended, liftAt);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.AutoLiftMs, s.PhaseMs);
        Assert.Equal(TonearmPhase.Stopped, s.AfterRest);
        Assert.Equal(TonearmMachine.AutoSwingMs, s.SwingMs);
        Assert.Equal(liftAt + (long)(TonearmMachine.AutoLiftMs + TonearmMachine.AutoPlatterOffMs), s.PlatterOffAtMs);
        Assert.Equal(DeckPhaseName.AutoReturn, FrameAt(in s, ended, liftAt).Name);

        long swingAt = liftAt + (long)TonearmMachine.AutoLiftMs;
        StepAt(ref s, ended, swingAt);
        Assert.Equal(TonearmPhase.SwingToRest, s.Phase);
        Assert.Equal(TonearmMachine.AutoSwingMs, s.PhaseMs);

        long brakeAt = swingAt + (long)TonearmMachine.AutoPlatterOffMs;
        StepAt(ref s, ended, brakeAt - 1);
        Assert.True(s.PlatterOn);
        StepAt(ref s, ended, brakeAt);
        Assert.False(s.PlatterOn);

        long parkedAt = swingAt + (long)TonearmMachine.AutoSwingMs;
        StepAt(ref s, ended, parkedAt);
        Assert.Equal(TonearmPhase.Stopped, s.Phase);
        Assert.False(s.PlatterOn);
        Assert.True(s.RecordOut);                                           // it stays on the platter
        var f = FrameAt(in s, ended, parkedAt);
        Near(TonearmMachine.RestDeg, f.ArmDeg);
        Assert.Equal(1f, f.Lift);
        Assert.Equal(DeckPhaseName.Stopped, f.Name);
    }

    /// <summary>The run-out is a GROOVE, so the arm crosses it at constant speed — no easing.</summary>
    [Fact]
    public void RunOut_IsLinear()
    {
        var (s, play) = TrackingAt(0.5f);
        float from = FrameAt(in s, play, T0).ArmDeg;
        var ended = play with { QueueEnded = true, PlayWhenReady = false, Advancing = false, Phase = PlaybackPhase.Ended };
        StepAt(ref s, ended, T0);

        float span = TonearmMachine.RunOutDeg - from, dur = s.PhaseMs;
        foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
            Near(from + span * t, FrameAt(in s, ended, T0 + (long)(dur * t)).ArmDeg, 0.02f);
    }

    // ── buffering ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stall at the very start waits over the LEAD-IN, so the arm is where the music will resume from.</summary>
    [Fact]
    public void BufferingAtTheLeadIn_HoversOverTheLeadIn()
    {
        var (s, play) = TrackingAt(0.005f);
        var stalled = play with { Buffering = true, Advancing = false, Phase = PlaybackPhase.Buffering };

        StepAt(ref s, stalled, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.BufferLiftMs, s.PhaseMs);
        Assert.True(s.PlatterOn);                                           // the platter never stops for a stall

        long hoverAt = T0 + (long)TonearmMachine.BufferLiftMs;
        StepAt(ref s, stalled, hoverAt);
        Assert.Equal(TonearmPhase.Hover, s.Phase);
        Near(TonearmMachine.LeadInDeg, FrameAt(in s, stalled, hoverAt).ArmDeg);
        Assert.Equal(DeckPhaseName.Buffering, FrameAt(in s, stalled, hoverAt).Name);
    }

    /// <summary>A mid-track stall waits exactly where it stalled.</summary>
    [Fact]
    public void BufferingMidTrack_HoversInPlace()
    {
        var (s, play) = TrackingAt(0.5f);
        float armBefore = FrameAt(in s, play, T0).ArmDeg;
        var stalled = play with { Buffering = true, Advancing = false, Phase = PlaybackPhase.Buffering };

        StepAt(ref s, stalled, T0);
        long hoverAt = T0 + (long)TonearmMachine.BufferLiftMs;
        StepAt(ref s, stalled, hoverAt);
        Assert.Equal(TonearmPhase.Hover, s.Phase);
        Near(armBefore, FrameAt(in s, stalled, hoverAt).ArmDeg, 0.01f);
    }

    /// <summary>The hover BOB: a 1.8 s breath peaking at 1.4 half a period in, and flat at the ends.</summary>
    [Fact]
    public void HoverBob_PeaksAtHalfAPeriod()
    {
        var (s, stalled) = Hovering();
        long since = s.SinceMs;
        Near(1f, FrameAt(in s, stalled, since).Lift, 0.001f);
        Near(1.4f, FrameAt(in s, stalled, since + (long)(TonearmMachine.BobPeriodMs / 2)).Lift, 0.001f);
        Near(1f, FrameAt(in s, stalled, since + (long)TonearmMachine.BobPeriodMs).Lift, 0.01f);
    }

    /// <summary>Reduced motion removes the breath entirely — a lifted arm, held still.</summary>
    [Fact]
    public void HoverBob_IsFlatUnderReducedMotion()
    {
        var (s, stalled) = Hovering();
        var calm = stalled with { ReducedMotion = true };
        Assert.Equal(1f, FrameAt(in s, calm, s.SinceMs + (long)(TonearmMachine.BobPeriodMs / 2)).Lift);
    }

    /// <summary>Buffered is not resumed: the hover clears when audio is ADVANCING again, and not one tick sooner.</summary>
    [Fact]
    public void Hover_ClearsOnlyWhenAdvancing()
    {
        var (s, stalled) = Hovering();
        long t = s.SinceMs;

        var readyButStill = stalled with { Buffering = false, Advancing = false };
        Run(ref s, readyButStill, t + StepMs, t + 3000);
        Assert.Equal(TonearmPhase.Hover, s.Phase);

        var moving = stalled with { Buffering = false, Advancing = true };
        StepAt(ref s, moving, t + 3033);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.LowerMs, s.PhaseMs);
    }

    /// <summary>Pausing out of a stall brakes the platter and drops the bob.</summary>
    [Fact]
    public void Hover_PausesIntoAStillLiftedArm()
    {
        var (s, stalled) = Hovering();
        var paused = stalled with { PlayWhenReady = false, Buffering = false, Advancing = false, Phase = PlaybackPhase.Paused };
        StepAt(ref s, paused, s.SinceMs + StepMs);
        Assert.Equal(TonearmPhase.Paused, s.Phase);
        Assert.False(s.PlatterOn);
        Assert.Equal(1f, FrameAt(in s, paused, s.SinceMs).Lift);
    }

    static (TonearmState State, DeckInput Input) Hovering()
    {
        var (s, play) = TrackingAt(0.5f);
        var stalled = play with { Buffering = true, Advancing = false, Phase = PlaybackPhase.Buffering };
        StepAt(ref s, stalled, T0);
        StepAt(ref s, stalled, T0 + (long)TonearmMachine.BufferLiftMs);
        return (s, stalled);
    }

    // ── error ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An error retreats the arm and brakes WITH the return swing; recovery is a plain cue, not a reload.</summary>
    [Fact]
    public void Error_RetreatsToUnavailable_AndRecoversWithAPlainCue()
    {
        var (s, play) = TrackingAt(0.5f);
        var failed = play with { Error = true, Advancing = false, Phase = PlaybackPhase.Failed };

        StepAt(ref s, failed, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.LiftMs, s.PhaseMs);
        Assert.Equal(TonearmPhase.Unavailable, s.AfterRest);
        Assert.Equal(T0 + (long)TonearmMachine.LiftMs, s.PlatterOffAtMs);   // "with the swing" = at the swing's START
        Assert.Equal(DeckPhaseName.Error, FrameAt(in s, failed, T0).Name);

        StepAt(ref s, failed, T0 + (long)TonearmMachine.LiftMs - 1);
        Assert.True(s.PlatterOn);

        long swingAt = T0 + (long)TonearmMachine.LiftMs;
        StepAt(ref s, failed, swingAt);
        Assert.Equal(TonearmPhase.SwingToRest, s.Phase);
        Assert.Equal(TonearmMachine.ErrorSwingMs, s.PhaseMs);
        Assert.False(s.PlatterOn);

        long deadAt = swingAt + (long)TonearmMachine.ErrorSwingMs;
        StepAt(ref s, failed, deadAt);
        Assert.Equal(TonearmPhase.Unavailable, s.Phase);
        Assert.False(s.PlatterOn);
        Assert.True(s.RecordOut);                                           // the record never went back in its sleeve
        var f = FrameAt(in s, failed, deadAt);
        Near(TonearmMachine.RestDeg, f.ArmDeg);
        Assert.Equal(DeckPhaseName.Unavailable, f.Name);

        // it stays there while the error stands
        Run(ref s, failed, deadAt + StepMs, deadAt + 5000);
        Assert.Equal(TonearmPhase.Unavailable, s.Phase);

        // …and recovers with a cue, because there is nothing to slide out
        long healedAt = deadAt + 5033;
        StepAt(ref s, play, healedAt);
        Assert.Equal(TonearmPhase.Cue, s.Phase);
        Assert.True(s.PlatterOn);
        StepAt(ref s, play, healedAt + (long)TonearmMachine.CueHoldMs);
        Assert.Equal(TonearmPhase.SwingToLead, s.Phase);
    }

    // ── speed ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>33⅓ and 45 are 200 and 270 deg/s. Changing the option must not disturb the arm.</summary>
    [Fact]
    public void RpmChange_LeavesTheArmAlone_AndRetargetsThePlatter()
    {
        var (s, play) = TrackingAt(0.5f);
        Near(200f, FrameAt(in s, play, T0).PlatterTargetDegPerSec, 0.02f);

        var fortyFive = play with { Rpm = Rpm45 };
        var before = s;
        StepAt(ref s, fortyFive, T0 + StepMs);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        Near(before.ToDeg, s.ToDeg);
        Near(270f, FrameAt(in s, fortyFive, T0 + StepMs).PlatterTargetDegPerSec, 0.02f);
        Near(FrameAt(in before, play, T0).ArmDeg, FrameAt(in s, fortyFive, T0 + StepMs).ArmDeg, 0.001f);
    }

    // ── reduced motion ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reduced motion keeps the STORY and drops the choreography: every leg collapses to 150 ms.</summary>
    [Fact]
    public void ReducedMotion_CollapsesEveryPhaseToTheSnapDuration()
    {
        var s = TonearmState.Initial;
        var play = MakeInput(reducedMotion: true);
        long snap = (long)TonearmMachine.ReducedMs;

        StepAt(ref s, play, T0);
        Assert.Equal(TonearmPhase.SlideOut, s.Phase);
        Assert.Equal(TonearmMachine.ReducedMs, s.PhaseMs);

        StepAt(ref s, play, T0 + snap);
        Assert.Equal(TonearmPhase.Cue, s.Phase);
        Assert.Equal(TonearmMachine.ReducedMs, s.PhaseMs);

        StepAt(ref s, play, T0 + 2 * snap);
        Assert.Equal(TonearmPhase.SwingToLead, s.Phase);
        Assert.Equal(TonearmMachine.ReducedMs, s.PhaseMs);

        StepAt(ref s, play, T0 + 3 * snap);
        Assert.Equal(TonearmPhase.Lower, s.Phase);
        Assert.Equal(TonearmMachine.ReducedMs, s.PhaseMs);

        StepAt(ref s, play, T0 + 4 * snap);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);

        // …and nothing spins, and no dust puffs
        var f = FrameAt(in s, play, T0 + 4 * snap);
        Assert.Equal(0f, f.PlatterTargetDegPerSec);
        Assert.False(f.Thump);
    }

    /// <summary>…and progress is still readable: the arm angle is still the position.</summary>
    [Fact]
    public void ReducedMotion_TrackingStillYieldsAngleOfFrac()
    {
        var (s, play) = TrackingAt(0.75f, reducedMotion: true);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        Near(TonearmMachine.AngleOf(0.75f), FrameAt(in s, play, T0).ArmDeg, 0.01f);
    }

    /// <summary>Even the long legs of a record change collapse — and the brake still lands AFTER the swing starts.</summary>
    [Fact]
    public void ReducedMotion_KeepsTheBrakeAfterTheSwingStarts()
    {
        var (s, play) = TrackingAt(0.98f, reducedMotion: true);
        var edge = play with { Boundary = DeckBoundary.NaturalNewAlbum };
        StepAt(ref s, edge, T0);
        Assert.Equal(TonearmMachine.ReducedMs, s.PhaseMs);
        Assert.Equal(T0 + 2 * (long)TonearmMachine.ReducedMs, s.PlatterOffAtMs);
    }

    // ── seeding ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mounted over a playing track: straight into the groove, no cueing sequence to sit through.</summary>
    [Fact]
    public void Seed_Playing_StartsInTheGroove()
    {
        var play = MakeInput(positionMs: PosOf(0.4f));
        var s = TonearmMachine.Seed(in play);
        Assert.Equal(TonearmPhase.Tracking, s.Phase);
        Assert.True(s.PlatterOn);
        Assert.True(s.RecordOut);
        var f = TonearmMachine.Sample(in s, in play);
        Near(TonearmMachine.AngleOf(0.4f), f.ArmDeg, 0.01f);
        Assert.Equal(0f, f.Lift);
        Assert.Equal(1f, f.Slide);
    }

    /// <summary>Mounted over a paused track: arm up over the groove, platter still.</summary>
    [Fact]
    public void Seed_Paused_StartsLiftedOverTheGroove()
    {
        var paused = MakeInput(positionMs: PosOf(0.4f), playWhenReady: false, advancing: false, phase: PlaybackPhase.Paused);
        var s = TonearmMachine.Seed(in paused);
        Assert.Equal(TonearmPhase.Paused, s.Phase);
        Assert.False(s.PlatterOn);
        Assert.True(s.RecordOut);
        var f = TonearmMachine.Sample(in s, in paused);
        Near(TonearmMachine.AngleOf(0.4f), f.ArmDeg, 0.01f);
        Assert.Equal(1f, f.Lift);
    }

    /// <summary>Mounted mid-stall: hovering, with the platter already up.</summary>
    [Fact]
    public void Seed_Buffering_StartsHovering()
    {
        var stalled = MakeInput(positionMs: PosOf(0.4f), buffering: true, advancing: false, phase: PlaybackPhase.Buffering);
        var s = TonearmMachine.Seed(in stalled);
        Assert.Equal(TonearmPhase.Hover, s.Phase);
        Assert.True(s.PlatterOn);
        Assert.True(s.RecordOut);
    }

    /// <summary>No track: an empty platter and an arm on its rest.</summary>
    [Fact]
    public void Seed_NoTrack_StartsIdleAndSleeved()
    {
        var empty = MakeInput(hasTrack: false, playWhenReady: false, advancing: false, phase: PlaybackPhase.Idle);
        var s = TonearmMachine.Seed(in empty);
        Assert.Equal(TonearmState.Initial, s);
        var f = TonearmMachine.Sample(in s, in empty);
        Near(TonearmMachine.RestDeg, f.ArmDeg);
        Assert.Equal(0f, f.Slide);
        Assert.Equal(0f, f.PlatterTargetDegPerSec);
    }

    /// <summary>Mounted over a failure, or over a queue that already ran out: the two terminal poses.</summary>
    [Fact]
    public void Seed_ErrorAndQueueEnd_StartInTheirTerminalPose()
    {
        var failed = MakeInput(error: true, advancing: false, playWhenReady: false, phase: PlaybackPhase.Failed);
        var e = TonearmMachine.Seed(in failed);
        Assert.Equal(TonearmPhase.Unavailable, e.Phase);
        Assert.True(e.RecordOut);
        Assert.False(e.PlatterOn);

        var ended = MakeInput(queueEnded: true, advancing: false, playWhenReady: false, phase: PlaybackPhase.Ended);
        var q = TonearmMachine.Seed(in ended);
        Assert.Equal(TonearmPhase.Stopped, q.Phase);
        Assert.True(q.RecordOut);
        Assert.False(q.PlatterOn);
        Near(TonearmMachine.RestDeg, TonearmMachine.Sample(in q, in ended).ArmDeg);
    }

    // ── the track going away ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Clearing the queue puts the record AWAY: lift · return · sleeve · idle, with no cover swap on the way.</summary>
    [Fact]
    public void TrackRemoved_LiftsReturnsSleevesAndGoesIdle()
    {
        var (s, play) = TrackingAt(0.5f);
        var gone = play with { HasTrack = false, PlayWhenReady = false, Advancing = false, Phase = PlaybackPhase.Idle };

        StepAt(ref s, gone, T0);
        Assert.Equal(TonearmPhase.Lift, s.Phase);
        Assert.Equal(TonearmMachine.LiftMs, s.PhaseMs);
        Assert.Equal(TonearmPhase.SleeveIn, s.AfterRest);

        long swingAt = T0 + (long)TonearmMachine.LiftMs;
        StepAt(ref s, gone, swingAt);
        Assert.Equal(TonearmPhase.SwingToRest, s.Phase);
        Assert.Equal(TonearmMachine.ChangeSwingMs, s.PhaseMs);

        long sleeveAt = swingAt + (long)TonearmMachine.ChangeSwingMs;
        StepAt(ref s, gone, sleeveAt);
        Assert.Equal(TonearmPhase.SleeveIn, s.Phase);
        Assert.False(s.PlatterOn);

        long idleAt = sleeveAt + (long)TonearmMachine.SleeveInMs;
        StepAt(ref s, gone, idleAt);
        Assert.Equal(TonearmPhase.Idle, s.Phase);
        Assert.False(s.RecordOut);
        Assert.Equal(0, s.CoverGen);                                        // no album arrived, so no artwork ticked
        var f = FrameAt(in s, gone, idleAt);
        Near(TonearmMachine.RestDeg, f.ArmDeg);
        Assert.Equal(0f, f.Slide);

        // …and it stays put
        Run(ref s, gone, idleAt + StepMs, idleAt + 3000);
        Assert.Equal(TonearmPhase.Idle, s.Phase);
    }

    // ── names ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every phase has a name in the shared deck vocabulary — none of them silently falls through to Idle.</summary>
    [Fact]
    public void PhaseNames_CoverEveryPhase()
    {
        Assert.Equal(DeckPhaseName.Idle, TonearmMachine.NameOf(TonearmPhase.Idle));
        Assert.Equal(DeckPhaseName.Cueing, TonearmMachine.NameOf(TonearmPhase.SlideOut));
        Assert.Equal(DeckPhaseName.Cueing, TonearmMachine.NameOf(TonearmPhase.Cue));
        Assert.Equal(DeckPhaseName.Cueing, TonearmMachine.NameOf(TonearmPhase.SwingToLead));
        Assert.Equal(DeckPhaseName.NeedleDown, TonearmMachine.NameOf(TonearmPhase.Lower));
        Assert.Equal(DeckPhaseName.Playing, TonearmMachine.NameOf(TonearmPhase.Tracking));
        Assert.Equal(DeckPhaseName.Pausing, TonearmMachine.NameOf(TonearmPhase.Lift));
        Assert.Equal(DeckPhaseName.Paused, TonearmMachine.NameOf(TonearmPhase.Paused));
        Assert.Equal(DeckPhaseName.SpinningUp, TonearmMachine.NameOf(TonearmPhase.SpinUp));
        Assert.Equal(DeckPhaseName.Seeking, TonearmMachine.NameOf(TonearmPhase.SeekSwing));
        Assert.Equal(DeckPhaseName.Seeking, TonearmMachine.NameOf(TonearmPhase.Dragging));
        Assert.Equal(DeckPhaseName.NextTrack, TonearmMachine.NameOf(TonearmPhase.RecueSwing));
        Assert.Equal(DeckPhaseName.ChangingRecord, TonearmMachine.NameOf(TonearmPhase.SwingToRest));
        Assert.Equal(DeckPhaseName.ChangingRecord, TonearmMachine.NameOf(TonearmPhase.SleeveIn));
        Assert.Equal(DeckPhaseName.ChangingRecord, TonearmMachine.NameOf(TonearmPhase.CoverSwap));
        Assert.Equal(DeckPhaseName.RunOut, TonearmMachine.NameOf(TonearmPhase.RunOut));
        Assert.Equal(DeckPhaseName.LockedGroove, TonearmMachine.NameOf(TonearmPhase.LockedGroove));
        Assert.Equal(DeckPhaseName.Buffering, TonearmMachine.NameOf(TonearmPhase.Hover));
        Assert.Equal(DeckPhaseName.Stopped, TonearmMachine.NameOf(TonearmPhase.Stopped));
        Assert.Equal(DeckPhaseName.Unavailable, TonearmMachine.NameOf(TonearmPhase.Unavailable));
    }

    /// <summary>A lift is only "pausing" when that is what it is FOR: the destination disambiguates the shared legs.</summary>
    [Fact]
    public void PhaseNames_ReadTheLiftsIntent()
    {
        var lift = TonearmState.Initial with { Phase = TonearmPhase.Lift };
        Assert.Equal(DeckPhaseName.Pausing, TonearmMachine.NameOf(lift with { AfterLift = TonearmPhase.Paused }));
        Assert.Equal(DeckPhaseName.NextTrack, TonearmMachine.NameOf(lift with { AfterLift = TonearmPhase.RecueSwing }));
        Assert.Equal(DeckPhaseName.Seeking, TonearmMachine.NameOf(lift with { AfterLift = TonearmPhase.SeekSwing }));
        Assert.Equal(DeckPhaseName.Buffering, TonearmMachine.NameOf(lift with { AfterLift = TonearmPhase.Hover }));
        Assert.Equal(DeckPhaseName.ChangingRecord, TonearmMachine.NameOf(lift with { AfterRest = TonearmPhase.SleeveIn }));
        Assert.Equal(DeckPhaseName.Error, TonearmMachine.NameOf(lift with { AfterRest = TonearmPhase.Unavailable }));
        Assert.Equal(DeckPhaseName.AutoReturn, TonearmMachine.NameOf(lift with { AfterRest = TonearmPhase.Stopped }));

        var home = TonearmState.Initial with { Phase = TonearmPhase.SwingToRest };
        Assert.Equal(DeckPhaseName.ChangingRecord, TonearmMachine.NameOf(home with { AfterRest = TonearmPhase.SleeveIn }));
        Assert.Equal(DeckPhaseName.Error, TonearmMachine.NameOf(home with { AfterRest = TonearmPhase.Unavailable }));
        Assert.Equal(DeckPhaseName.AutoReturn, TonearmMachine.NameOf(home with { AfterRest = TonearmPhase.Stopped }));
    }

    // ── the contract the ticker relies on ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The clock folds a boundary edge EAGERLY and then ticks; both calls can land on the same
    /// <c>Environment.TickCount64</c>. Folding the same input twice must therefore be the same as folding it once —
    /// most of all for <c>CoverGen</c>, which a repeat would double-count into a second artwork crossfade.
    /// </summary>
    [Fact]
    public void Step_IsIdempotentWithinATick()
    {
        var (tracking, play) = TrackingAt(0.5f);
        AssertIdempotent(tracking, play with { PlayWhenReady = false, Advancing = false });                 // pause edge
        AssertIdempotent(tracking, play with { Boundary = DeckBoundary.NaturalSameAlbum });                 // recue edge
        AssertIdempotent(tracking, play with { Boundary = DeckBoundary.NaturalNewAlbum });                  // record change
        AssertIdempotent(tracking, play with { SeekTargetMs = PosOf(0.9f) });                               // seek edge
        AssertIdempotent(tracking, play with { ScrubTargetMs = PosOf(0.9f) });                              // drag edge
        AssertIdempotent(tracking, play with { Buffering = true, Advancing = false });                      // stall edge
        AssertIdempotent(tracking, play with { QueueEnded = true, PlayWhenReady = false, Advancing = false });
        AssertIdempotent(tracking, play with { Error = true, Advancing = false });
        AssertIdempotent(tracking, play with { HasTrack = false, PlayWhenReady = false, Advancing = false });
        AssertIdempotent(TonearmState.Initial, play);                                                       // cold cue

        // the one genuinely destructive repeat: a cover swap on a sleeved deck
        var sleeved = TonearmState.Initial;
        var edge = play with { Boundary = DeckBoundary.NaturalNewAlbum, PlayWhenReady = false, Advancing = false };
        var once = TonearmMachine.Step(sleeved, in edge);
        var twice = TonearmMachine.Step(once, in edge);
        Assert.Equal(1, once.CoverGen);
        Assert.Equal(1, twice.CoverGen);
        Assert.Equal(once, twice);
    }

    static void AssertIdempotent(TonearmState s, DeckInput i)
    {
        var once = TonearmMachine.Step(s, in i);
        var twice = TonearmMachine.Step(once, in i);
        Assert.Equal(once, twice);
    }

    /// <summary>
    /// The deck's ticker runs at 30 Hz inside the frame budget. Stepping and sampling the machine must cost the GC
    /// nothing at all — every type on this path is a value type, and that is exactly what this pins.
    /// </summary>
    [Fact]
    public void NoAllocation_OverAThousandStepAndSample()
    {
        var play = MakeInput(positionMs: PosOf(0.05f));

        var warm = TonearmState.Initial;
        Drive(ref warm, play, 1000);                                        // JIT the whole graph, Advance included

        var s = TonearmState.Initial;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Drive(ref s, play, 1000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(TonearmPhase.Tracking, s.Phase);                       // the run really did walk the whole cue
        Assert.True(bytes == 0, $"1000 Step+Sample allocated {bytes} bytes.");
    }

    /// <summary>Keeps the sampled frame alive without allocating anything to hold it.</summary>
    public static float Sink;

    static void Drive(ref TonearmState s, DeckInput baseInput, int steps)
    {
        for (int k = 0; k < steps; k++)
        {
            var i = baseInput with { NowMs = T0 + k * StepMs };
            s = TonearmMachine.Step(s, in i);
            Sink = TonearmMachine.Sample(in s, in i).ArmDeg;
        }
    }
}

/// <summary>
/// The record family's <see cref="IDeckModel"/>: the tonearm machine plus a platter integrator, folded into the one
/// <see cref="DeckFrame"/> the face binds.
/// </summary>
public sealed class RecordModelTests
{
    const long T0 = 100_000, TrackMs = 236_000;

    static DeckInput Input(long nowMs = T0, long positionMs = 0, bool playWhenReady = true, bool advancing = true,
                           float rpm = 33.333f, bool reducedMotion = false)
        => new(nowMs, true, PlaybackPhase.Playing, playWhenReady, advancing, false, false, false, DeckBoundary.None,
               null, null, positionMs, TrackMs, false, rpm, reducedMotion);

    /// <summary>Seeded mid-song, the model reports the groove immediately: platter turning, arm down, record out.</summary>
    [Fact]
    public void SeededMidSong_ReportsTheGrooveWithoutCueing()
    {
        var seed = Input(positionMs: TrackMs / 2);
        var model = new RecordModel(in seed, RecordVariant.Record);
        Assert.Equal(TonearmPhase.Tracking, model.State.Phase);

        var f = model.Tick(in seed, 1f / 30f);
        Assert.Equal(DeckPhaseName.Playing, f.Phase);
        Assert.Equal(0f, f.Lift);
        Assert.Equal(1f, f.Slide);
        Assert.True(MathF.Abs(f.Angle1 - TonearmMachine.AngleOf(0.5f)) < 0.01f);
        Assert.True(MathF.Abs(f.Frac - 0.5f) < 0.001f);
        Assert.False(model.IsSettled);
        Assert.True(model.Bands.IsEmpty);
        Assert.True(model.Peaks.IsEmpty);
    }

    /// <summary>The Zune draws no arm, and runs the very same machine anyway.</summary>
    [Fact]
    public void ZuneVariant_RunsTheSameMachine()
    {
        var seed = Input(positionMs: TrackMs / 4);
        var record = new RecordModel(in seed, RecordVariant.Record);
        var zune = new RecordModel(in seed, RecordVariant.Zune);
        Assert.Equal(RecordVariant.Zune, zune.Variant);

        for (int k = 0; k < 60; k++)
        {
            var i = Input(nowMs: T0 + k * 33, positionMs: TrackMs / 4);
            Assert.Equal(record.Tick(in i, 1f / 30f), zune.Tick(in i, 1f / 30f));
        }
    }

    /// <summary>A speed change is a pitch SLEW: both time constants tighten for 600 ms, then the deck goes back to normal.</summary>
    [Fact]
    public void SpeedChange_TightensBothTimeConstantsForTheRampWindow()
    {
        var seed = Input();
        var model = new RecordModel(in seed, RecordVariant.Turntable);
        Assert.False(model.IsSpeedRamping);

        var faster = Input(nowMs: T0 + 33, rpm: 45f);
        model.Tick(in faster, 0.033f);
        Assert.True(model.IsSpeedRamping);

        var mid = Input(nowMs: T0 + 33 + RecordModel.SpeedRampMs - 1, rpm: 45f);
        model.Tick(in mid, 0.033f);
        Assert.True(model.IsSpeedRamping);

        var after = Input(nowMs: T0 + 33 + RecordModel.SpeedRampMs, rpm: 45f);
        model.Tick(in after, 0.033f);
        Assert.False(model.IsSpeedRamping);

        // …and the platter reached the new target
        for (int k = 0; k < 60; k++) model.Tick(in after, 1f / 30f);
        Assert.True(MathF.Abs(model.PlatterOmegaDegPerSec - 270f) < 1f);
    }

    /// <summary>Settled means the ticker may stop: a terminal phase AND a platter that has actually come to rest.</summary>
    [Fact]
    public void IsSettled_NeedsBothATerminalPhaseAndAStoppedPlatter()
    {
        var seed = Input(positionMs: TrackMs / 2);
        var model = new RecordModel(in seed, RecordVariant.Record);

        var pause = Input(nowMs: T0, positionMs: TrackMs / 2, playWhenReady: false, advancing: false);
        for (long t = T0; t <= T0 + 1000; t += 33) model.Tick(pause with { NowMs = t }, 0.033f);
        Assert.Equal(TonearmPhase.Paused, model.State.Phase);
        Assert.False(model.IsSettled);                                       // the platter is still coasting down

        for (long t = T0 + 1033; t <= T0 + 6000; t += 33) model.Tick(pause with { NowMs = t }, 0.033f);
        Assert.True(model.IsSettled);
    }

    /// <summary>The ticker's hot path allocates nothing.</summary>
    [Fact]
    public void Tick_AllocatesNothing()
    {
        var seed = Input(positionMs: TrackMs / 3);
        var model = new RecordModel(in seed, RecordVariant.Picture);
        for (int k = 0; k < 500; k++) model.Tick(Input(nowMs: T0 + k * 33, positionMs: TrackMs / 3), 1f / 30f);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 500; k < 1500; k++) model.Tick(Input(nowMs: T0 + k * 33, positionMs: TrackMs / 3), 1f / 30f);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes == 0, $"1000 RecordModel ticks allocated {bytes} bytes.");
    }
}
