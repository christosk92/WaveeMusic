// ── Wavee.Tests/DeckModelTests.cs — the deck's physics, every class a function of values ───────────────────────────
//
// Ported from the ten `Player/Deck/*Tests.cs` files (`DeckEaseTests`, `DeckBoundaryRulesTests`,
// `PositionInterpolatorTests`, `SpinIntegratorTests`, `TonearmMachineTests`, `TonearmGeometryTests`, `TapeMachineTests`,
// `DiscMachineTests`, `MeterBallisticsTests`, `LevelSynthTests`), plus the four facts ch 23 §8 says were MISSING and must
// land with the port:
//   • `RecordModel` gets its own tests (mount-time "already at speed" seed, the 33⇄45 slew, IsSettled);
//   • `LevelModel.IsSettled` in SCOPE mode — the case that let "a paused Winamp oscilloscope never settles" ship (§6.4(5));
//   • `ProgressModel` must NOT bump CoverGen on a same-album advance;
//   • `DriftPath` keys run 0 → 1 without a seam;
// and the new PURE `Deck.Fold`, which 0.2.9 could not test because it read the bridge and the frame clock directly.
//
// The tonearm facts are stepped at the deck's own 33 ms cadence and every expectation is derived from the machine's
// constants, so retuning the choreography retunes the test.

using Wavee;
using Xunit;

using TM = Wavee.Deck.TonearmMachine;
using TP = Wavee.Deck.TonearmPhase;

namespace Wavee.Tests;

static class DeckIn
{
    public const long T0 = 100_000;       // a plausible frame clock: never 0, so the brake sentinel is unambiguous
    public const long StepMs = 33;        // the deck's 30 Hz cadence
    public const long TrackMs = 236_000;
    public const float Rpm33 = 33.333f, Rpm45 = 45f;

    public static Deck.Input Make(
        long nowMs = T0, bool hasTrack = true, Deck.TransportPhase phase = Deck.TransportPhase.Playing,
        bool playWhenReady = true, bool advancing = true, bool buffering = false, bool error = false,
        bool queueEnded = false, Deck.Boundary boundary = Deck.Boundary.None, long? seekTargetMs = null,
        long? scrubTargetMs = null, long positionMs = 0, long durationMs = TrackMs, bool repeatOne = false,
        float rpm = Rpm33, bool reducedMotion = false)
        => new(nowMs, hasTrack, phase, playWhenReady, advancing, buffering, error, queueEnded, boundary,
               seekTargetMs, scrubTargetMs, positionMs, durationMs, repeatOne, rpm, reducedMotion);

    public static void Near(float expected, float actual, float tol = 0.001f)
        => Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual} (tolerance {tol})");
}

public class DeckEaseTests
{
    static float Smoothstep(float x) => x * x * (3f - 2f * x);

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.99f)]
    public void The_solver_matches_an_exact_oracle(float x)
        => Assert.Equal(Smoothstep(x), Deck.Ease.CubicBezier(1f / 3f, 0f, 2f / 3f, 1f, x), 3);

    [Fact]
    public void The_identity_curve_is_the_identity() => Assert.Equal(0.3f, Deck.Ease.CubicBezier(0.25f, 0.25f, 0.75f, 0.75f, 0.3f), 4);

    [Fact]
    public void A_flat_tangent_at_both_ends_still_converges()
        => Assert.Equal(0.5f, Deck.Ease.CubicBezier(0f, 0.5f, 1f, 0.5f, 0.5f), 3);

    [Fact]
    public void Out_of_range_and_NaN_inputs_clamp()
    {
        Assert.Equal(0f, Deck.Ease.CubicBezier(0.4f, 0f, 0.2f, 1f, -1f));
        Assert.Equal(1f, Deck.Ease.CubicBezier(0.4f, 0f, 0.2f, 1f, 2f));
        Assert.Equal(0f, Deck.Ease.CubicBezier(0.4f, 0f, 0.2f, 1f, float.NaN));
    }

    [Fact]
    public void The_four_named_curves_are_monotone_from_zero_to_one()
    {
        Func<float, float>[] curves = [Deck.Ease.Std, Deck.Ease.LiftUp, Deck.Ease.Damped, Deck.Ease.SlideOut];
        foreach (var c in curves)
        {
            Assert.Equal(0f, c(0f), 4);
            Assert.Equal(1f, c(1f), 4);
            float prev = 0f;
            for (float t = 0f; t <= 1f; t += 0.01f)
            {
                float v = c(t);
                Assert.InRange(v, -0.0001f, 1.0001f);
                Assert.True(v >= prev - 0.0001f);
                prev = v;
            }
        }
    }

    [Fact]
    public void Damped_drops_most_of_the_way_up_front_and_lift_up_leaves_before_the_swing_does()
    {
        // Worked values: Damped(.3) ≈ 0.71, LiftUp(.3) ≈ 0.69, Std(.3) ≈ 0.36.
        Assert.True(Deck.Ease.Damped(0.3f) > 0.6f);
        Assert.True(Deck.Ease.LiftUp(0.3f) > 0.6f);
        Assert.True(Deck.Ease.Std(0.3f) < Deck.Ease.LiftUp(0.3f));
    }
}

public class DeckBoundaryRulesTests
{
    const int TrackA = 10, TrackB = 11, AlbumX = 50, AlbumY = 51;
    const long Dur = 200_000;

    static Deck.Boundary Classify(int prevTrack, int prevAlbum, long prevPos, long prevDur, Deck.TransportPhase prevPhase,
                                  int track, int album, long pos, bool repeatOne = false)
        => Deck.BoundaryRules.Classify(prevTrack, prevAlbum, prevPos, prevDur, prevPhase, track, album, pos, repeatOne);

    [Fact]
    public void The_windows_are_pinned()
    {
        Assert.Equal(1_500L, Deck.BoundaryRules.NaturalEndWindowMs);
        Assert.Equal(2_000L, Deck.BoundaryRules.RepeatRewindMs);
        Assert.Equal(Deck.BoundaryRules.NaturalEndWindowMs, Deck.EndedWindowMs);
    }

    [Fact]
    public void The_same_track_ticking_along_is_no_edge()
        => Assert.Equal(Deck.Boundary.None, Classify(TrackA, AlbumX, 1000, Dur, Deck.TransportPhase.Playing, TrackA, AlbumX, 2000));

    [Fact]
    public void Repeat_one_rewound_from_the_end_is_a_repeat_one_edge()
    {
        Assert.Equal(Deck.Boundary.RepeatOne,
            Classify(TrackA, AlbumX, Dur - 500, Dur, Deck.TransportPhase.Playing, TrackA, AlbumX, 100, repeatOne: true));
        Assert.Equal(Deck.Boundary.RepeatOne,
            Classify(TrackA, AlbumX, Dur - 1500, Dur, Deck.TransportPhase.Playing, TrackA, AlbumX, 100, repeatOne: true));
    }

    [Fact]
    public void Repeat_one_rewound_from_mid_track_or_landing_late_is_not()
    {
        Assert.Equal(Deck.Boundary.None, Classify(TrackA, AlbumX, 90_000, Dur, Deck.TransportPhase.Playing, TrackA, AlbumX, 100, true));
        Assert.Equal(Deck.Boundary.None, Classify(TrackA, AlbumX, Dur - 500, Dur, Deck.TransportPhase.Playing, TrackA, AlbumX, 2500, true));
        Assert.Equal(Deck.Boundary.None, Classify(TrackA, AlbumX, Dur - 500, 0, Deck.TransportPhase.Playing, TrackA, AlbumX, 100, true));
        Assert.Equal(Deck.Boundary.None, Classify(TrackA, AlbumX, Dur - 500, Dur, Deck.TransportPhase.Playing, TrackA, AlbumX, 100, false));
    }

    [Fact]
    public void An_unknown_slot_on_either_side_is_never_a_synthesized_skip()
    {
        Assert.Equal(Deck.Boundary.None, Classify(0, 0, 0, 0, Deck.TransportPhase.Idle, TrackA, AlbumX, 0));        // first fold
        Assert.Equal(Deck.Boundary.None, Classify(TrackA, AlbumX, 1000, Dur, Deck.TransportPhase.Playing, 0, 0, 0)); // removed
        Assert.Equal(Deck.Boundary.None, Classify(-1, 0, 0, 0, Deck.TransportPhase.Idle, TrackA, AlbumX, 0));
    }

    [Fact]
    public void Running_out_is_natural_and_jumping_is_a_skip()
    {
        Assert.Equal(Deck.Boundary.NaturalSameAlbum, Classify(TrackA, AlbumX, Dur - 200, Dur, Deck.TransportPhase.Playing, TrackB, AlbumX, 0));
        Assert.Equal(Deck.Boundary.NaturalSameAlbum, Classify(TrackA, AlbumX, Dur - 1500, Dur, Deck.TransportPhase.Playing, TrackB, AlbumX, 0));
        Assert.Equal(Deck.Boundary.NaturalNewAlbum, Classify(TrackA, AlbumX, Dur - 200, Dur, Deck.TransportPhase.Playing, TrackB, AlbumY, 0));
        Assert.Equal(Deck.Boundary.SkipSameAlbum, Classify(TrackA, AlbumX, 60_000, Dur, Deck.TransportPhase.Playing, TrackB, AlbumX, 0));
        Assert.Equal(Deck.Boundary.SkipNewAlbum, Classify(TrackA, AlbumX, 60_000, Dur, Deck.TransportPhase.Playing, TrackB, AlbumY, 0));
        Assert.Equal(Deck.Boundary.SkipSameAlbum, Classify(TrackA, AlbumX, Dur - 1501, Dur, Deck.TransportPhase.Playing, TrackB, AlbumX, 0));
    }

    [Fact]
    public void Transitioning_counts_as_natural_even_from_mid_track()
        => Assert.Equal(Deck.Boundary.NaturalNewAlbum, Classify(TrackA, AlbumX, 60_000, Dur, Deck.TransportPhase.Transitioning, TrackB, AlbumY, 0));

    [Fact]
    public void An_unknown_duration_can_never_be_natural()
        => Assert.Equal(Deck.Boundary.SkipSameAlbum, Classify(TrackA, AlbumX, 60_000, 0, Deck.TransportPhase.Playing, TrackB, AlbumX, 0));

    [Fact]
    public void An_unknown_album_slot_is_never_the_same_album()
    {
        Assert.Equal(Deck.Boundary.SkipNewAlbum, Classify(TrackA, 0, 60_000, Dur, Deck.TransportPhase.Playing, TrackB, 0, 0));
        Assert.Equal(Deck.Boundary.NaturalNewAlbum, Classify(TrackA, AlbumX, Dur - 10, Dur, Deck.TransportPhase.Playing, TrackB, 0, 0));
    }
}

public class DeckPositionInterpolatorTests
{
    [Fact]
    public void Advancing_extrapolates_the_wall_clock_from_the_anchor()
    {
        var p = default(Deck.PositionInterpolator);
        p.Anchor(1000, 5000);
        Assert.Equal(5500, p.Estimate(1500, advancing: true, null, null, 200_000));
        Assert.Equal(5000, p.Estimate(1500, advancing: false, null, null, 200_000));
        Assert.Equal(5000, p.AnchorPositionMs);
    }

    [Fact]
    public void A_committed_seek_wins_outright_and_is_still_clamped()
    {
        var p = default(Deck.PositionInterpolator);
        p.Anchor(1000, 5000);
        Assert.Equal(90_000, p.Estimate(1500, true, 90_000, null, 200_000));
        Assert.Equal(200_000, p.Estimate(1500, true, 999_999, null, 200_000));
    }

    [Fact]
    public void The_upper_bound_caps_a_runaway_but_never_raises_an_estimate()
    {
        var p = default(Deck.PositionInterpolator);
        p.Anchor(0, 0);
        Assert.Equal(3000, p.Estimate(10_000, true, null, 3000, 200_000));
        Assert.Equal(1000, p.Estimate(1000, true, null, 9000, 200_000));
    }

    [Fact]
    public void The_estimate_clamps_to_the_duration_and_an_unknown_duration_only_at_zero()
    {
        var p = default(Deck.PositionInterpolator);
        p.Anchor(0, 199_000);
        Assert.Equal(200_000, p.Estimate(5000, true, null, null, 200_000));
        p.Anchor(0, -50);
        Assert.Equal(0, p.Estimate(0, false, null, null, 0));
    }

    [Fact]
    public void An_UNANCHORED_interpolator_extrapolates_machine_uptime_which_is_why_every_caller_anchors_first()
    {
        var p = default(Deck.PositionInterpolator);
        Assert.Equal(50_000, p.Estimate(50_000, true, null, null, 0));
    }
}

public class DeckSpinIntegratorTests
{
    static float Run(ref Deck.SpinIntegrator s, float target, float seconds, float dt = 1f / 30f)
    {
        for (float t = 0f; t < seconds; t += dt) s.Step(target, dt);
        return s.Omega;
    }

    [Fact]
    public void Spin_up_reaches_95_percent_of_33_in_700ms_and_is_not_instant()
    {
        var s = new Deck.SpinIntegrator(Deck.RecordModel.PlatterTauUpSec, Deck.RecordModel.PlatterTauDownSec);
        s.Step(200f, 1f / 30f);
        Assert.True(s.Omega < 200f * 0.5f, "a motor does not reach speed in one tick");
        Run(ref s, 200f, 0.7f);
        Assert.True(s.Omega >= 190f, $"only {s.Omega} °/s after 700 ms");
    }

    [Fact]
    public void Spin_down_brakes_95_percent_in_1600ms_and_settles_to_an_exact_zero()
    {
        var s = new Deck.SpinIntegrator(Deck.RecordModel.PlatterTauUpSec, Deck.RecordModel.PlatterTauDownSec) { Omega = 200f };
        Run(ref s, 0f, 1.6f);
        Assert.True(s.Omega <= 10f, $"still {s.Omega} °/s after 1.6 s");
        Run(ref s, 0f, 10f);
        Assert.Equal(0f, s.Omega);
        Assert.True(s.AtRest);
    }

    [Fact]
    public void Braking_lags_spin_up()
    {
        var up = new Deck.SpinIntegrator(0.23f, 0.53f);
        var down = new Deck.SpinIntegrator(0.23f, 0.53f) { Omega = 200f };
        Run(ref up, 200f, 0.3f);
        Run(ref down, 0f, 0.3f);
        Assert.True(200f - up.Omega < down.Omega, "the brake must be slower than the motor");
    }

    [Fact]
    public void The_angle_always_wraps_into_zero_to_360_in_either_direction()
    {
        var s = new Deck.SpinIntegrator(0f, 0f);
        for (int i = 0; i < 1000; i++)
        {
            s.Step(270f, 1f / 30f, dir: i % 2 == 0 ? 1 : -1);
            Assert.InRange(s.Angle, 0f, 360f);
        }
        var r = new Deck.SpinIntegrator(0f, 0f);
        for (int i = 0; i < 100; i++) r.Step(270f, 1f / 30f, dir: -1);
        Assert.InRange(r.Angle, 0f, 360f);
    }

    [Fact]
    public void A_default_constructed_integrator_has_no_lag()
    {
        var s = default(Deck.SpinIntegrator);
        s.Step(123f, 1f / 30f);
        Assert.Equal(123f, s.Omega);
    }
}

public class DeckTonearmMachineTests
{
    static void StepAt(ref Deck.TonearmState s, Deck.Input baseInput, long nowMs)
    {
        var i = baseInput with { NowMs = nowMs };
        s = TM.Step(s, in i);
    }

    static void Run(ref Deck.TonearmState s, Deck.Input baseInput, long fromMs, long untilMs)
    {
        for (long t = fromMs; ; )
        {
            StepAt(ref s, baseInput, t);
            if (t >= untilMs) break;
            t = Math.Min(t + DeckIn.StepMs, untilMs);
        }
    }

    static Deck.TonearmFrame FrameAt(in Deck.TonearmState s, Deck.Input baseInput, long nowMs)
    {
        var i = baseInput with { NowMs = nowMs };
        return TM.Sample(in s, in i);
    }

    static long PosOf(float frac) => (long)(frac * DeckIn.TrackMs);

    static (Deck.TonearmState State, Deck.Input Input) TrackingAt(float frac, float rpm = DeckIn.Rpm33, bool reduced = false)
    {
        var play = DeckIn.Make(positionMs: PosOf(frac), rpm: rpm, reducedMotion: reduced);
        return (TM.Seed(in play), play);
    }

    [Fact]
    public void The_angle_table_is_pinned()
    {
        Assert.Equal(-34f, TM.RestDeg);
        Assert.Equal(-20f, TM.LeadInDeg);
        Assert.Equal(17f, TM.SpanDeg);
        Assert.Equal(TM.LeadInDeg + TM.SpanDeg, TM.RunOutDeg);
    }

    [Fact]
    public void Idle_to_play_runs_the_whole_cueing_timeline()
    {
        var s = Deck.TonearmState.Initial;
        var play = DeckIn.Make();
        long T0 = DeckIn.T0;

        StepAt(ref s, play, T0);
        Assert.Equal(TP.SlideOut, s.Phase);
        Assert.False(s.RecordOut);

        StepAt(ref s, play, T0 + (long)TM.SlideMs);
        Assert.Equal(TP.Cue, s.Phase);
        Assert.True(s.RecordOut);
        Assert.True(s.PlatterOn);                                          // the platter is up before the arm moves

        long swing = T0 + (long)(TM.SlideMs + TM.CueHoldMs);
        StepAt(ref s, play, swing);
        Assert.Equal(TP.SwingToLead, s.Phase);

        long lower = swing + (long)TM.SwingLeadMs;
        StepAt(ref s, play, lower);
        Assert.Equal(TP.Lower, s.Phase);

        StepAt(ref s, play, lower + (long)TM.LowerMs);
        Assert.Equal(TP.Tracking, s.Phase);
        Assert.Equal(0f, FrameAt(in s, play, lower + (long)TM.LowerMs).Lift);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Tracking_rides_lead_in_plus_span_times_position(float frac)
    {
        var (s, play) = TrackingAt(frac);
        var f = FrameAt(in s, play, DeckIn.T0);
        DeckIn.Near(TM.LeadInDeg + TM.SpanDeg * frac, f.ArmDeg, 0.01f);
        Assert.Equal(Deck.PhaseName.Playing, f.Name);
    }

    [Fact]
    public void Pause_lifts_then_brakes_and_leaves_the_arm_where_it_was()
    {
        var (s, play) = TrackingAt(0.5f);
        float armBefore = FrameAt(in s, play, DeckIn.T0).ArmDeg;
        var pause = play with { PlayWhenReady = false, Advancing = false, Phase = Deck.TransportPhase.Paused };

        StepAt(ref s, pause, DeckIn.T0);
        Assert.Equal(TP.Lift, s.Phase);
        Assert.True(s.PlatterOn);                                          // still turning while the arm rises

        long settled = DeckIn.T0 + (long)TM.LiftMs;
        StepAt(ref s, pause, settled);
        Assert.Equal(TP.Paused, s.Phase);
        Assert.False(s.PlatterOn);
        DeckIn.Near(armBefore, FrameAt(in s, pause, settled).ArmDeg, 0.01f);
    }

    [Fact]
    public void Resume_spins_up_then_lowers_and_drops_the_stylus_exactly_once()
    {
        var (s, play) = TrackingAt(0.5f);
        var pause = play with { PlayWhenReady = false, Advancing = false, Phase = Deck.TransportPhase.Paused };
        StepAt(ref s, pause, DeckIn.T0);
        long t0 = DeckIn.T0 + (long)TM.LiftMs;
        StepAt(ref s, pause, t0);

        int thumps = 0;
        for (long t = t0; t <= t0 + 3000; t += DeckIn.StepMs)
        {
            StepAt(ref s, play, t);
            if (FrameAt(in s, play, t).Thump) thumps++;
        }
        Assert.Equal(TP.Tracking, s.Phase);
        Assert.Equal(1, thumps);
    }

    [Fact]
    public void A_sub_quantum_seek_does_not_twitch_the_arm()
    {
        var (s, play) = TrackingAt(0.5f);
        var seek = play with { SeekTargetMs = PosOf(0.5f) + 10 };
        StepAt(ref s, seek, DeckIn.T0);
        Assert.Equal(TP.Tracking, s.Phase);
    }

    [Theory]
    [InlineData(Deck.Boundary.NaturalSameAlbum, 450f)]
    [InlineData(Deck.Boundary.SkipSameAlbum, 300f)]
    public void A_same_album_boundary_recues_WITHOUT_ever_stopping_the_platter(Deck.Boundary boundary, float liftMs)
    {
        var (s, play) = TrackingAt(0.98f);
        StepAt(ref s, play with { Boundary = boundary }, DeckIn.T0);
        Assert.Equal(TP.Lift, s.Phase);
        Assert.Equal(liftMs, s.PhaseMs);
        Assert.Equal(TP.RecueSwing, s.AfterLift);
        Assert.Equal(0L, s.PlatterOffAtMs);                                 // no brake is ever scheduled

        var next = play with { PositionMs = 0 };
        for (long t = DeckIn.T0 + DeckIn.StepMs; t <= DeckIn.T0 + 4000; t += DeckIn.StepMs)
        {
            StepAt(ref s, next, t);
            Assert.True(s.PlatterOn, $"platter stopped at +{t - DeckIn.T0} ms");
        }
        Assert.Equal(TP.Tracking, s.Phase);
    }

    [Theory]
    [InlineData(Deck.Boundary.NaturalNewAlbum, 450L)]
    [InlineData(Deck.Boundary.SkipNewAlbum, 300L)]
    public void A_new_album_changes_the_record_with_the_brake_at_lift_plus_700_and_one_cover_swap(Deck.Boundary boundary, long liftMs)
    {
        var (s, play) = TrackingAt(0.98f);
        StepAt(ref s, play with { Boundary = boundary }, DeckIn.T0);
        Assert.Equal(TP.SleeveIn, s.AfterRest);
        Assert.Equal(DeckIn.T0 + liftMs + (long)TM.ChangePlatterOffMs, s.PlatterOffAtMs);
        Assert.Equal(0, s.CoverGen);

        var next = play with { PositionMs = 0 };
        Run(ref s, next, DeckIn.T0 + DeckIn.StepMs, DeckIn.T0 + 8000);
        Assert.Equal(1, s.CoverGen);
        Assert.Equal(TP.Tracking, s.Phase);
    }

    [Fact]
    public void Folding_a_boundary_twice_on_the_same_tick_is_idempotent()
    {
        var (s, play) = TrackingAt(0.98f);
        var edge = play with { Boundary = Deck.Boundary.NaturalNewAlbum };
        StepAt(ref s, edge, DeckIn.T0);
        var once = s;
        StepAt(ref s, edge, DeckIn.T0);
        Assert.Equal(once, s);
    }

    [Fact]
    public void The_queue_end_rides_the_run_out_parks_in_the_locked_groove_then_auto_returns()
    {
        var (s, play) = TrackingAt(0.5f);
        var ended = play with { QueueEnded = true, PlayWhenReady = false, Advancing = false, Phase = Deck.TransportPhase.Ended };
        StepAt(ref s, ended, DeckIn.T0);
        Assert.Equal(TP.RunOut, s.Phase);
        Run(ref s, ended, DeckIn.T0 + DeckIn.StepMs, DeckIn.T0 + 10_000);
        Assert.Equal(TP.Stopped, s.Phase);
        Assert.False(s.PlatterOn);
        Assert.True(s.RecordOut);                                           // it stays on the platter
        DeckIn.Near(TM.RestDeg, FrameAt(in s, ended, DeckIn.T0 + 10_000).ArmDeg);
    }

    [Fact]
    public void Buffering_hovers_the_platter_keeps_turning_and_the_hover_clears_only_when_advancing()
    {
        var (s, play) = TrackingAt(0.5f);
        var stalled = play with { Buffering = true, Advancing = false, Phase = Deck.TransportPhase.Buffering };
        StepAt(ref s, stalled, DeckIn.T0);
        long hover = DeckIn.T0 + (long)TM.BufferLiftMs;
        StepAt(ref s, stalled, hover);
        Assert.Equal(TP.Hover, s.Phase);
        Assert.True(s.PlatterOn);
        DeckIn.Near(1.4f, FrameAt(in s, stalled, s.SinceMs + (long)(TM.BobPeriodMs / 2)).Lift);
        Assert.Equal(1f, FrameAt(in s, stalled with { ReducedMotion = true }, s.SinceMs + (long)(TM.BobPeriodMs / 2)).Lift);

        Run(ref s, stalled with { Buffering = false, Advancing = false }, hover + DeckIn.StepMs, hover + 2000);
        Assert.Equal(TP.Hover, s.Phase);                                    // buffered is not enough
        StepAt(ref s, stalled with { Buffering = false, Advancing = true }, hover + 2033);
        Assert.Equal(TP.Lower, s.Phase);
    }

    [Fact]
    public void An_error_retreats_with_the_brake_at_the_swing_and_recovers_with_a_plain_cue()
    {
        var (s, play) = TrackingAt(0.5f);
        var failed = play with { Error = true, Advancing = false, Phase = Deck.TransportPhase.Failed };
        StepAt(ref s, failed, DeckIn.T0);
        Assert.Equal(TP.Unavailable, s.AfterRest);
        Assert.Equal(DeckIn.T0 + (long)TM.LiftMs, s.PlatterOffAtMs);       // "with the swing" = at the swing's START
        Run(ref s, failed, DeckIn.T0 + DeckIn.StepMs, DeckIn.T0 + 5000);
        Assert.Equal(TP.Unavailable, s.Phase);
        Assert.True(s.RecordOut);

        StepAt(ref s, play, DeckIn.T0 + 5033);
        Assert.Equal(TP.Cue, s.Phase);                                      // nothing to slide out
    }

    [Fact]
    public void Reduced_motion_collapses_every_leg_to_the_snap_and_nothing_spins()
    {
        var s = Deck.TonearmState.Initial;
        var play = DeckIn.Make(reducedMotion: true);
        long snap = (long)TM.ReducedMs;
        for (int k = 0; k <= 4; k++) StepAt(ref s, play, DeckIn.T0 + k * snap);
        Assert.Equal(TP.Tracking, s.Phase);
        var f = FrameAt(in s, play, DeckIn.T0 + 4 * snap);
        Assert.Equal(0f, f.PlatterTargetDegPerSec);
        Assert.False(f.Thump);
    }

    [Fact]
    public void Seeding_mid_song_never_replays_the_cue()
    {
        Assert.Equal(TP.Tracking, TM.Seed(DeckIn.Make(positionMs: PosOf(0.4f))).Phase);
        Assert.Equal(TP.Hover, TM.Seed(DeckIn.Make(buffering: true, advancing: false, playWhenReady: false)).Phase);
        Assert.Equal(TP.Paused, TM.Seed(DeckIn.Make(playWhenReady: false, advancing: false)).Phase);
        Assert.Equal(TP.Stopped, TM.Seed(DeckIn.Make(playWhenReady: false, advancing: false, queueEnded: true)).Phase);
        Assert.Equal(TP.Unavailable, TM.Seed(DeckIn.Make(error: true)).Phase);
        Assert.Equal(TP.Idle, TM.Seed(DeckIn.Make(hasTrack: false)).Phase);
    }

    [Fact]
    public void A_lift_is_named_by_its_intent()
    {
        var s = Deck.TonearmState.Initial with { Phase = TP.Lift };
        Assert.Equal(Deck.PhaseName.Pausing, TM.NameOf(in s));
        Assert.Equal(Deck.PhaseName.AutoReturn, TM.NameOf(s with { AfterRest = TP.Stopped }));
        Assert.Equal(Deck.PhaseName.ChangingRecord, TM.NameOf(s with { AfterRest = TP.SleeveIn }));
        Assert.Equal(Deck.PhaseName.Error, TM.NameOf(s with { AfterRest = TP.Unavailable }));
        Assert.Equal(Deck.PhaseName.NextTrack, TM.NameOf(s with { AfterLift = TP.RecueSwing }));
        Assert.Equal(Deck.PhaseName.Buffering, TM.NameOf(s with { AfterLift = TP.Hover }));
    }
}

public class DeckTonearmGeometryTests
{
    [Fact]
    public void The_rim_is_position_zero_and_the_label_edge_position_one()
    {
        const float D = 300f, cx = 150f, cy = 150f;
        Assert.Equal(0f, Deck.TonearmGeometry.FracFromDeckPoint(cx + D * 0.5f, cy, cx, cy, D), 3);
        Assert.Equal(1f, Deck.TonearmGeometry.FracFromDeckPoint(cx + D * 0.5f * Deck.TonearmGeometry.LabelRadiusFrac, cy, cx, cy, D), 3);
        Assert.Equal(1f, Deck.TonearmGeometry.FracFromDeckPoint(cx, cy, cx, cy, D));        // inside the label clamps
        Assert.Equal(0f, Deck.TonearmGeometry.FracFromDeckPoint(cx + D, cy, cx, cy, D));    // outside the rim clamps
    }

    [Fact]
    public void It_depends_only_on_the_distance_from_the_spindle()
    {
        const float D = 300f;
        float r = D * 0.5f * 0.7f;
        float a = Deck.TonearmGeometry.FracFromDeckPoint(r, 0f, 0f, 0f, D);
        float b = Deck.TonearmGeometry.FracFromDeckPoint(0f, -r, 0f, 0f, D);
        Assert.Equal(a, b, 4);
    }

    [Fact]
    public void Arm_local_to_deck_is_a_translation_at_zero_degrees_and_keeps_the_pivot_fixed()
    {
        var (x, y) = Deck.TonearmGeometry.ArmLocalToDeck(5f, 7f, 0f, 100f, 200f, 40f, 180f);
        Assert.Equal(105f, x, 3);
        Assert.Equal(207f, y, 3);
        var (px, py) = Deck.TonearmGeometry.ArmLocalToDeck(20f, 180f * 0.08f, 33f, 100f, 200f, 40f, 180f);
        Assert.Equal(120f, px, 3);
        Assert.Equal(200f + 180f * 0.08f, py, 3);
    }

    [Fact]
    public void Rotation_preserves_the_distance_from_the_pivot()
    {
        float pivotX = 20f, pivotY = 180f * 0.08f;
        var (x, y) = Deck.TonearmGeometry.ArmLocalToDeck(20f, 150f, -27f, 0f, 0f, 40f, 180f);
        float d0 = 150f - pivotY;
        float d1 = MathF.Sqrt((x - pivotX) * (x - pivotX) + (y - pivotY) * (y - pivotY));
        Assert.Equal(d0, d1, 2);
    }
}

public class DeckRecordModelTests
{
    [Fact]
    public void Mounted_mid_song_the_platter_is_already_at_speed()
    {
        var m = new Deck.RecordModel(DeckIn.Make(positionMs: 60_000), Deck.RecordVariant.Record);
        Assert.Equal(DeckIn.Rpm33 * 6f, m.PlatterOmegaDegPerSec, 2);
        Assert.Equal(Deck.TonearmPhase.Tracking, m.State.Phase);
    }

    [Fact]
    public void Mounted_under_reduced_motion_the_platter_does_not_spin()
        => Assert.Equal(0f, new Deck.RecordModel(DeckIn.Make(positionMs: 60_000, reducedMotion: true), Deck.RecordVariant.Zune).PlatterOmegaDegPerSec);

    [Fact]
    public void A_33_to_45_change_is_a_pitch_slew_that_expires()
    {
        var m = new Deck.RecordModel(DeckIn.Make(positionMs: 60_000), Deck.RecordVariant.Turntable);
        m.Tick(DeckIn.Make(nowMs: DeckIn.T0 + 33, positionMs: 60_033, rpm: DeckIn.Rpm45), 0.033f);
        Assert.True(m.IsSpeedRamping);
        m.Tick(DeckIn.Make(nowMs: DeckIn.T0 + 33 + Deck.RecordModel.SpeedRampMs, positionMs: 61_000, rpm: DeckIn.Rpm45), 0.033f);
        Assert.False(m.IsSpeedRamping);
    }

    [Fact]
    public void A_paused_record_settles_once_the_platter_is_at_rest()
    {
        var m = new Deck.RecordModel(DeckIn.Make(positionMs: 60_000), Deck.RecordVariant.Record);
        Assert.False(m.IsSettled);
        var pause = DeckIn.Make(playWhenReady: false, advancing: false, phase: Deck.TransportPhase.Paused, positionMs: 60_000);
        for (long t = DeckIn.T0; t < DeckIn.T0 + 10_000; t += 33) m.Tick(pause with { NowMs = t }, 0.033f);
        Assert.True(m.IsSettled);
        Assert.Equal(Deck.PhaseName.Paused, m.Phase);
        Assert.True(m.Bands.IsEmpty && m.Peaks.IsEmpty);
    }
}

public class DeckTapeDiscMeterLevelTests
{
    [Theory]
    [InlineData(0f, 26f, 13f)]
    [InlineData(0.5f, 19.5f, 19.5f)]
    [InlineData(1f, 13f, 26f)]
    public void Cassette_pack_radii_trade_places_across_the_tape(float p, float l, float r)
    {
        var (rl, rr) = Deck.TapeModel.PackRadii(Deck.TapeKind.Cassette, p);
        Assert.Equal(l, rl, 3);
        Assert.Equal(r, rr, 3);
    }

    [Fact]
    public void Tape_angular_speed_is_inverse_to_the_radius_and_zero_when_stopped()
    {
        float small = Deck.TapeModel.Omega(Deck.TapeKind.Cassette, 13f, 1f, true);
        float big = Deck.TapeModel.Omega(Deck.TapeKind.Cassette, 26f, 1f, true);
        Assert.Equal(small, big * 2f, 2);
        Assert.Equal(0f, Deck.TapeModel.Omega(Deck.TapeKind.Reel, 50f, 1f, false));
    }

    [Fact]
    public void A_committed_seek_fast_winds_for_900ms_then_settles()
    {
        var m = new Deck.TapeModel(Deck.TapeKind.Cassette);
        m.Tick(DeckIn.Make(nowMs: 1000, positionMs: 1000), 0.033f);
        // The fold puts the playhead on the committed target on the same tick (a seek wins outright).
        var f = m.Tick(DeckIn.Make(nowMs: 1033, positionMs: 90_000, seekTargetMs: 90_000), 0.033f);
        Assert.Equal(Deck.PhaseName.Winding, f.Phase);
        var later = m.Tick(DeckIn.Make(nowMs: 1033 + 950, positionMs: 90_950), 0.033f);
        Assert.Equal(Deck.PhaseName.Playing, later.Phase);
    }

    [Fact]
    public void A_new_album_eject_bumps_the_cover_exactly_once_for_tape_and_disc()
    {
        Deck.IModel[] models = [new Deck.TapeModel(Deck.TapeKind.Cassette), new Deck.DiscModel()];
        foreach (var m in models)
        {
            int gen = 0;
            m.Tick(DeckIn.Make(nowMs: 0, boundary: Deck.Boundary.SkipNewAlbum), 0.033f);
            for (int i = 1; i < 60; i++) gen = m.Tick(DeckIn.Make(nowMs: i * 33), 0.033f).CoverGen;
            Assert.Equal(1, gen);
        }
    }

    [Fact]
    public void The_disc_spindle_is_constant_linear_velocity()
    {
        Assert.Equal(3000f, Deck.DiscModel.ClvTargetDegPerSec(0f, true));
        Assert.Equal(1200f, Deck.DiscModel.ClvTargetDegPerSec(1f, true));
        Assert.Equal(0f, Deck.DiscModel.ClvTargetDegPerSec(0.5f, false));
    }

    [Fact]
    public void The_meter_maps_minus_18_dbfs_to_zero_db_and_is_monotone()
    {
        Assert.Equal(0f, Deck.MeterModel.RmsToDb(MathF.Pow(10f, -18f / 20f)), 2);
        float prev = float.MinValue;
        for (float db = -20f; db <= 3f; db += 0.5f)
        {
            float deg = Deck.MeterModel.DbToDeg(db);
            Assert.InRange(deg, -48f, 48f);
            Assert.True(deg >= prev);
            prev = deg;
        }
    }

    [Fact]
    public void A_ppm_is_faster_than_a_vu_and_both_settle_at_rest_when_stopped()
    {
        var ppm = new Deck.MeterModel(ppm: true, static () => (0.5f, 0.5f));
        var vu = new Deck.MeterModel(ppm: false, static () => (0.5f, 0.5f));
        var play = DeckIn.Make();
        var fp = ppm.Tick(play, 0.1f);
        var fv = vu.Tick(play, 0.1f);
        Assert.True(fp.Angle0 > fv.Angle0, "the PPM needle must move further in the same 100 ms");

        var stop = DeckIn.Make(playWhenReady: false, advancing: false);
        for (int i = 0; i < 200; i++) vu.Tick(stop, 0.033f);
        Assert.True(vu.IsSettled);
    }

    [Fact]
    public void Level_bands_stay_in_range_and_peaks_never_sit_below_their_band()
    {
        var m = new Deck.LevelModel(24, scope: false, static () => (0.3f, 0.6f), seed: 1f);
        for (int i = 0; i < 300; i++)
        {
            m.Tick(DeckIn.Make(nowMs: i * 33), 0.033f);
            for (int b = 0; b < 24; b++)
            {
                Assert.InRange(m.Bands[b], 0f, 1f);
                Assert.True(m.Peaks[b] >= m.Bands[b] - 0.0001f);
            }
        }
    }

    [Fact]
    public void A_silent_spectrum_relaxes_to_the_floor_and_settles()
    {
        var m = new Deck.LevelModel(19, scope: false, static () => null, seed: 1f);
        for (int i = 0; i < 60; i++) m.Tick(DeckIn.Make(nowMs: i * 33), 0.033f);
        Assert.False(m.IsSettled);
        var stop = DeckIn.Make(playWhenReady: false, advancing: false);
        for (int i = 0; i < 300; i++) m.Tick(stop with { NowMs = 2000 + i * 33 }, 0.033f);
        Assert.True(m.IsSettled);
    }

    [Fact]
    public void A_paused_SCOPE_deck_settles_too()
    {
        // ch 23 §6.4(5): the scope branch parks every band at 0.5, and 0.2.9's rule demanded the 0.02 floor, so a paused
        // oscilloscope burned the 30 Hz ticker forever. The rule is now "not playing AND the trace is flat".
        var m = new Deck.LevelModel(19, scope: true, static () => null, seed: 1f);
        var stop = DeckIn.Make(playWhenReady: false, advancing: false);
        for (int i = 0; i < 10; i++) m.Tick(stop with { NowMs = i * 33 }, 0.033f);
        Assert.True(m.IsSettled);
        m.Tick(DeckIn.Make(nowMs: 999), 0.033f);
        Assert.False(m.IsSettled);
    }

    [Fact]
    public void A_null_level_tap_is_a_real_answer()
    {
        var m = new Deck.LevelModel(24, scope: false, static () => null, seed: 3f);
        for (int i = 0; i < 30; i++) m.Tick(DeckIn.Make(nowMs: i * 33), 0.033f);
        for (int b = 0; b < 24; b++) Assert.InRange(m.Bands[b], 0f, 1f);
    }
}

public class DeckProgressAndDriftTests
{
    [Fact]
    public void A_same_album_advance_must_NOT_bump_the_cover()
    {
        var m = new Deck.ProgressModel();
        Assert.Equal(0, m.Tick(DeckIn.Make(boundary: Deck.Boundary.NaturalSameAlbum), 0.033f).CoverGen);
        Assert.Equal(0, m.Tick(DeckIn.Make(boundary: Deck.Boundary.SkipSameAlbum), 0.033f).CoverGen);
        Assert.Equal(0, m.Tick(DeckIn.Make(boundary: Deck.Boundary.RepeatOne), 0.033f).CoverGen);
        Assert.Equal(1, m.Tick(DeckIn.Make(boundary: Deck.Boundary.SkipNewAlbum), 0.033f).CoverGen);
        Assert.True(m.IsSettled);
    }

    [Fact]
    public void The_progress_phase_names_follow_the_transport()
    {
        var m = new Deck.ProgressModel();
        Assert.Equal(Deck.PhaseName.Error, m.Tick(DeckIn.Make(error: true), 0f).Phase);
        Assert.Equal(Deck.PhaseName.Idle, m.Tick(DeckIn.Make(hasTrack: false), 0f).Phase);
        Assert.Equal(Deck.PhaseName.Seeking, m.Tick(DeckIn.Make(scrubTargetMs: 5), 0f).Phase);
        Assert.Equal(Deck.PhaseName.Paused, m.Tick(DeckIn.Make(playWhenReady: false, advancing: false), 0f).Phase);
    }

    [Fact]
    public void The_drift_keys_run_zero_to_one_without_a_seam()
    {
        var keys = Deck.DriftPath.Keys;
        Assert.Equal(0f, keys[0].Offset);
        Assert.Equal(1f, keys[^1].Offset);
        for (int i = 1; i < keys.Length; i++) Assert.True(keys[i].Offset > keys[i - 1].Offset);
        foreach (var k in keys) Assert.True(k.Scale >= 1f, "a zoom below 1 exposes the frame edge");
    }
}

public class DeckFoldTests
{
    static Deck.TransportFacts Facts(bool playing = true, bool buffering = false, bool error = false, long dur = 200_000,
                                     long reported = 10_000, long? seek = null, long? scrub = null, long? synthetic = null)
        => new(HasTrack: true, IsPlaying: playing, Buffering: buffering, Error: error, DurationMs: dur,
               ReportedPositionMs: reported, SeekTargetMs: seek, ScrubTargetMs: scrub, SyntheticSeekMs: synthetic,
               RepeatOne: false, Rpm: 33.333f, ReducedMotion: false);

    [Theory]
    [InlineData(true, false, false, 10_000L, Deck.TransportPhase.Failed)]
    [InlineData(false, true, false, 10_000L, Deck.TransportPhase.Buffering)]
    [InlineData(false, false, true, 10_000L, Deck.TransportPhase.Playing)]
    [InlineData(false, false, false, 199_000L, Deck.TransportPhase.Ended)]
    [InlineData(false, false, false, 10_000L, Deck.TransportPhase.Paused)]
    public void The_phase_is_derived_in_priority_order(bool error, bool buffering, bool playing, long pos, Deck.TransportPhase expected)
        => Assert.Equal(expected, Deck.PhaseOf(playing, buffering, error, pos, 200_000));

    [Fact]
    public void An_unknown_duration_can_never_read_as_ended()
        => Assert.Equal(Deck.TransportPhase.Paused, Deck.PhaseOf(false, false, false, 999_999, 0));

    [Fact]
    public void Mounted_mid_song_and_ticked_mid_song_see_the_identical_input()
    {
        var f = Facts();
        var seeded = Deck.Seed(in f, 5000);
        var interp = default(Deck.PositionInterpolator);
        interp.Anchor(5000, f.ReportedPositionMs);
        var folded = Deck.Fold(in f, ref interp, 5000, Deck.Boundary.None);
        Assert.Equal(seeded, folded);
    }

    [Fact]
    public void The_fold_interpolates_and_a_committed_seek_wins()
    {
        var f = Facts();
        var interp = default(Deck.PositionInterpolator);
        interp.Anchor(1000, 10_000);
        Assert.Equal(10_500, Deck.Fold(in f, ref interp, 1500, Deck.Boundary.None).PositionMs);

        var seeking = Facts(seek: 150_000);
        var s = Deck.Fold(in seeking, ref interp, 1500, Deck.Boundary.None);
        Assert.Equal(150_000, s.PositionMs);
        Assert.Equal(150_000, s.SeekTargetMs);
    }

    [Fact]
    public void A_synthesized_remote_jump_folds_in_beside_a_real_seek_but_never_over_one()
    {
        var interp = default(Deck.PositionInterpolator);
        interp.Anchor(0, 0);
        var jump = Facts(synthetic: 80_000);
        Assert.Equal(80_000, Deck.Fold(in jump, ref interp, 0, Deck.Boundary.None).PositionMs);
        var both = Facts(seek: 20_000, synthetic: 80_000);
        Assert.Equal(20_000, Deck.Fold(in both, ref interp, 0, Deck.Boundary.None).PositionMs);
    }

    [Fact]
    public void Buffering_never_advances_the_playhead_and_the_edge_rides_one_fold()
    {
        var f = Facts(buffering: true);
        var interp = default(Deck.PositionInterpolator);
        interp.Anchor(1000, 10_000);
        var i = Deck.Fold(in f, ref interp, 9000, Deck.Boundary.SkipNewAlbum);
        Assert.False(i.Advancing);
        Assert.Equal(10_000, i.PositionMs);
        Assert.Equal(Deck.Boundary.SkipNewAlbum, i.Boundary);
        Assert.False(i.QueueEnded);
    }
}
