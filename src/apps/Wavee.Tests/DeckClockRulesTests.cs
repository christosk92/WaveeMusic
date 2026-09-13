// ── Wavee.Tests/DeckClockRulesTests.cs — the deck clock's gates, as values ────────────────────────────────────────────
//
// `Deck.ClockRules` is what `Deck.UI.cs`'s clock component asks before it runs a timer, synthesizes a remote seek or
// releases a committed one. The run gate is the idle-GPU rule for this surface (ch 23 §0(6), item 56): a paused,
// settled, ended or reduced-motion deck runs no timer and therefore requests no frame. `Models.ModelOptionSlug` is the
// rule behind ch 23 §6.4(4) (a model option must reach a mounted deck).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DeckClockRulesTests
{
    [Fact]
    public void A_paused_settled_deck_runs_no_timer()
        => Assert.False(Deck.ClockRules.ShouldTick(reducedMotion: false, railOpen: true, playing: false, playWhenReady: false,
            buffering: false, settled: true));

    [Fact]
    public void A_paused_deck_that_is_still_braking_keeps_ticking_until_it_settles()
        => Assert.True(Deck.ClockRules.ShouldTick(false, true, playing: false, playWhenReady: false, buffering: false, settled: false));

    [Theory]
    [InlineData(true, false, false)]   // playing
    [InlineData(false, true, false)]   // a load still resolving
    [InlineData(false, false, true)]   // buffering
    public void Any_transport_intent_runs_the_timer(bool playing, bool playWhenReady, bool buffering)
        => Assert.True(Deck.ClockRules.ShouldTick(false, true, playing, playWhenReady, buffering, settled: true));

    [Fact]
    public void Reduced_motion_and_a_closed_rail_each_stop_the_timer_even_while_playing()
    {
        Assert.False(Deck.ClockRules.ShouldTick(reducedMotion: true, railOpen: true, true, true, false, false));
        Assert.False(Deck.ClockRules.ShouldTick(reducedMotion: false, railOpen: false, true, true, false, false));
    }

    [Fact]
    public void A_paused_scope_deck_settles_so_the_gate_closes()
    {
        // ch 23 §6.4(5): 0.2.9's oscilloscope parked at 0.5, above the spectrum floor, and never settled.
        var scope = new Deck.LevelModel(Deck.Models.WinampBands, scope: true, levels: static () => null, seed: 1f);
        var input = DeckIn.Make(playWhenReady: false, advancing: false, phase: Deck.TransportPhase.Paused);
        for (int i = 0; i < 4; i++) scope.Tick(in input, 0.033f);
        Assert.True(scope.IsSettled);
        Assert.False(Deck.ClockRules.ShouldTick(false, true, false, false, false, scope.IsSettled));
    }

    [Fact]
    public void An_ended_record_ticks_through_the_auto_return_and_then_stops()
    {
        var seed = DeckIn.Make(positionMs: DeckIn.TrackMs - 500);
        var model = new Deck.RecordModel(in seed, Deck.RecordVariant.Record);
        long now = DeckIn.T0;
        bool stopped = false;
        for (int i = 0; i < 400 && !stopped; i++)
        {
            now += DeckIn.StepMs;
            var input = DeckIn.Make(nowMs: now, phase: Deck.TransportPhase.Ended, playWhenReady: false, advancing: false,
                queueEnded: true, positionMs: DeckIn.TrackMs - 200);
            model.Tick(in input, 0.033f);
            stopped = !Deck.ClockRules.ShouldTick(false, true, false, false, false, model.IsSettled);
        }
        Assert.True(stopped);
        Assert.Equal(Deck.TonearmPhase.Stopped, model.State.Phase);
    }

    [Fact]
    public void Looping_face_motion_needs_playing_an_open_rail_an_active_window_and_full_motion()
    {
        Assert.True(Deck.ClockRules.LoopsMayRun(false, true, true, true));
        Assert.False(Deck.ClockRules.LoopsMayRun(false, true, true, playing: false));
        Assert.False(Deck.ClockRules.LoopsMayRun(false, true, active: false, true));
        Assert.False(Deck.ClockRules.LoopsMayRun(false, railOpen: false, true, true));
        Assert.False(Deck.ClockRules.LoopsMayRun(reducedMotion: true, true, true, true));
    }

    [Fact]
    public void The_step_is_nominal_first_and_clamped_after()
    {
        Assert.Equal(Deck.ClockRules.TickMs / 1000f, Deck.ClockRules.DeltaSec(0, 5_000), 5);
        Assert.Equal(Deck.ClockRules.DtMaxSec, Deck.ClockRules.DeltaSec(1_000, 601_000), 5);
        Assert.Equal(Deck.ClockRules.DtMinSec, Deck.ClockRules.DeltaSec(1_000, 1_000), 5);
        Assert.Equal(0.033f, Deck.ClockRules.DeltaSec(1_000, 1_033), 4);
    }

    [Fact]
    public void A_foreign_jump_is_a_seek_but_the_first_report_and_our_own_seek_are_not()
    {
        Assert.True(Deck.ClockRules.IsRemoteJump(90_000, 30_000, lastDurationMs: 200_000, seekPending: false));
        Assert.False(Deck.ClockRules.IsRemoteJump(90_000, 30_000, lastDurationMs: 0, seekPending: false));
        Assert.False(Deck.ClockRules.IsRemoteJump(90_000, 30_000, lastDurationMs: 200_000, seekPending: true));
        Assert.False(Deck.ClockRules.IsRemoteJump(32_000, 30_000, lastDurationMs: 200_000, seekPending: false));
    }

    [Fact]
    public void A_committed_seek_holds_until_the_report_lands_or_the_latch_expires()
    {
        Assert.False(Deck.ClockRules.ReleaseSeekLatch(10_000, 60_000, nowMs: 5_100, committedAtMs: 5_000));
        Assert.True(Deck.ClockRules.ReleaseSeekLatch(59_800, 60_000, nowMs: 5_100, committedAtMs: 5_000));
        Assert.True(Deck.ClockRules.ReleaseSeekLatch(10_000, 60_000, nowMs: 7_100, committedAtMs: 5_000));
        // No stamp: only a landing releases (0.2.9 compared against an unset stamp and released on the first tick).
        Assert.False(Deck.ClockRules.ReleaseSeekLatch(10_000, 60_000, nowMs: 900_000, committedAtMs: 0));
    }

    [Fact]
    public void The_angle_quantum_is_one_rim_pixel_at_each_rail_breakpoint()
    {
        DeckIn.Near(0.890f, Deck.ClockRules.AngleQuantumDeg(184f));
        DeckIn.Near(0.505f, Deck.ClockRules.AngleQuantumDeg(324f));
        DeckIn.Near(0.338f, Deck.ClockRules.AngleQuantumDeg(484f));
    }

    [Fact]
    public void The_side_is_the_rail_content_width_on_the_four_dip_grid()
    {
        Assert.Equal(324f, Deck.ClockRules.SideFor(340f, 8f));
        Assert.Equal(184f, Deck.ClockRules.SideFor(200f, 8f));
        Assert.Equal(484f, Deck.ClockRules.SideFor(500f, 8f));
        Assert.Equal(328f, Deck.ClockRules.SideFor(343f, 8f));
        Assert.Equal(Deck.ClockRules.SideQuantum, Deck.ClockRules.SideFor(0f, 8f));
    }

    [Fact]
    public void Quantizing_lands_on_whole_steps_and_a_zero_quantum_passes_through()
    {
        Assert.Equal(0.5f, Deck.ClockRules.Quantize(0.52f, 0.5f), 5);
        Assert.Equal(0.123f, Deck.ClockRules.Quantize(0.123f, 0f), 5);
    }

    [Fact]
    public void Only_the_45_slug_speeds_the_platter()
    {
        Assert.Equal(45f, Deck.ClockRules.RpmFor("45"));
        Assert.Equal(Deck.ClockRules.Rpm33, Deck.ClockRules.RpmFor("33"));
        Assert.Equal(Deck.ClockRules.Rpm33, Deck.ClockRules.RpmFor(null));
    }

    [Fact]
    public void A_track_and_an_episode_on_the_same_slot_are_different_rows()
    {
        int track = Deck.ClockRules.RowKey((byte)EntityKind.Track, 5);
        int episode = Deck.ClockRules.RowKey((byte)EntityKind.Episode, 5);
        Assert.NotEqual(track, episode);
        Assert.True(track > 0 && episode > 0);
        Assert.Equal(0, Deck.ClockRules.RowKey((byte)EntityKind.Track, 0));
        Assert.Equal(0, Deck.ClockRules.RowKey((byte)EntityKind.Unknown, 5));
    }
}

public class DeckModelOptionsTests
{
    [Fact]
    public void Exactly_the_vu_needles_and_the_winamp_analyser_are_model_options()
    {
        Assert.Equal("ballistics", Deck.Models.ModelOptionSlug(Rail.PlayerCatalog.Vu));
        Assert.Equal("vis", Deck.Models.ModelOptionSlug(Rail.PlayerCatalog.Winamp));
        for (int id = 0; id < Rail.PlayerCatalog.Presets.Length; id++)
            if (id is not (Rail.PlayerCatalog.Vu or Rail.PlayerCatalog.Winamp))
                Assert.Null(Deck.Models.ModelOptionSlug(id));
    }

    [Fact]
    public void Every_model_option_is_a_real_option_row_of_its_preset()
    {
        // Option() has no "absent" answer (ch 23 §6.4(7)): a slug the preset does not carry would silently read another
        // row's choice.
        for (int id = 0; id < Rail.PlayerCatalog.Presets.Length; id++)
        {
            if (Deck.Models.ModelOptionSlug(id) is not { } slug) continue;
            var preset = Rail.PlayerCatalog.ById(id);
            bool found = false;
            foreach (var o in preset.Options) found |= o.Slug == slug;
            Assert.True(found, preset.Slug + " carries no '" + slug + "' option");
        }
    }
}
