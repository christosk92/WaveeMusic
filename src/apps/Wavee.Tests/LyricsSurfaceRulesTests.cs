// ── Wavee.Tests/LyricsSurfaceRulesTests.cs — the lyrics surface's geometry, ramps and overlays (stage B) ─────────────
//
// The rules `Lyrics.cs` §12b gained when the view was ported (stage B lifted 0.2.9's private `LyricsView` constants and
// helpers into CORE so `Lyrics.UI.cs` re-derives nothing): `Surface` (the two surfaces' metrics), `Follow` (the latch
// target and the resync countdown), `DofRamp` (the directional σ step), `Interlude` (the dots' anchor, fill, breath
// and lift), `Glow` (the voice halo envelopes) and `SampleClock` (the host sample → QPC seam). Plus the stage-B fix to
// `Wipe.GlowOutMs` (320, not the 240 hand-off fade).
//
// Every expected number is ch 22 §2/§3/§5's (audit-confirmed against 0.2.9). Pure values only: no scene, no clock.

using Wavee;
using Xunit;

namespace Wavee.Tests;

static class LyrNear
{
    public static void Eq(float expected, float actual, float tol = 0.01f)
        => Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected} ± {tol}, got {actual}");
}

public class LyricsSurfaceMetricsTests
{
    [Fact]
    public void The_rail_row_is_26_33_with_7_22_pads_and_a_47_dip_seed()
    {
        var m = Lyrics.Surface.Timed(large: false);
        Assert.Equal(new Lyrics.RowMetrics(26f, 33f, 7f, 22f), m);
        Assert.Equal(47f, m.Estimate);
    }

    [Fact]
    public void The_stage_row_is_36_46_with_9_64_pads_and_a_64_dip_seed()
    {
        var m = Lyrics.Surface.Timed(large: true);
        Assert.Equal(new Lyrics.RowMetrics(36f, 46f, 9f, 64f), m);
        Assert.Equal(64f, m.Estimate);
    }

    [Fact]
    public void The_unsynced_block_takes_reading_type_on_the_same_gutter()
    {
        Assert.Equal(new Lyrics.RowMetrics(19f, 26f, 5f, 22f), Lyrics.Surface.Unsynced(false));
        Assert.Equal(new Lyrics.RowMetrics(28f, 36f, 7f, 64f), Lyrics.Surface.Unsynced(true));
        Assert.Equal(26f, Lyrics.Surface.UnsyncedBlockPad(false));
        Assert.Equal(44f, Lyrics.Surface.UnsyncedBlockPad(true));
        Assert.Equal(0.88f, Lyrics.Surface.UnsyncedAlpha);
    }

    [Fact]
    public void The_stage_band_sits_higher_than_the_rails()
    {
        Assert.Equal(0.40f, Lyrics.Surface.FocalBand(false));
        Assert.Equal(0.38f, Lyrics.Surface.FocalBand(true));
    }

    [Fact]
    public void Inactive_rows_are_a_flat_098_and_reduced_motion_flattens_everything_to_1()
    {
        Assert.Equal(0.98f, Lyrics.Surface.ScaleFor(active: false, reducedMotion: false));
        Assert.Equal(1f, Lyrics.Surface.ScaleFor(active: true, reducedMotion: false));
        Assert.Equal(1f, Lyrics.Surface.ScaleFor(active: false, reducedMotion: true));
    }

    [Theory]
    [InlineData(false, 0, 219.3f)]
    [InlineData(false, 1, 183.6f)]
    [InlineData(false, 2, 204.0f)]
    [InlineData(false, 3, 158.1f)]
    [InlineData(false, 4, 193.8f)]
    [InlineData(false, 5, 147.9f)]
    [InlineData(true, 0, 426.4f)]
    [InlineData(true, 1, 343.2f)]
    [InlineData(true, 2, 384.8f)]
    [InlineData(true, 3, 301.6f)]
    [InlineData(true, 4, 364.0f)]
    [InlineData(true, 5, 260.0f)]
    public void The_shimmer_bars_are_a_different_set_per_surface_not_the_rail_scaled(bool large, int row, float width)
        => LyrNear.Eq(width, Lyrics.Surface.ShimmerBarW(large, row), 0.05f);

    [Fact]
    public void The_shimmer_rhythm_is_22_18_110_on_the_rail_and_32_24_150_on_the_stage()
    {
        Assert.Equal(6, Lyrics.Surface.ShimmerRows);
        Assert.Equal((22f, 18f, 110f), (Lyrics.Surface.ShimmerRowH(false), Lyrics.Surface.ShimmerGap(false), Lyrics.Surface.ShimmerPadTop(false)));
        Assert.Equal((32f, 24f, 150f), (Lyrics.Surface.ShimmerRowH(true), Lyrics.Surface.ShimmerGap(true), Lyrics.Surface.ShimmerPadTop(true)));
    }

    [Fact]
    public void The_secondary_run_is_062_of_the_lyric_three_dip_under_it()
    {
        Assert.Equal(0.62f, Lyrics.Surface.SecondaryFontRatio);
        Assert.Equal(3f, Lyrics.Surface.SecondaryGapDip);
    }
}

public class LyricsFollowTests
{
    const float Vp = 596f, Content = 5000f, Band = 0.40f;

    [Fact]
    public void The_active_rows_centre_lands_on_the_band()
        => LyrNear.Eq(1000f + 23.5f - Vp * Band, Lyrics.Follow.Target(1000f, 47f, 0f, Vp, Content, Band));

    [Fact]
    public void The_reserve_opens_above_the_line_and_never_moves_it()
    {
        // A 47-DIP line carrying a 25-DIP reserve centres exactly where the same line without it, 25 DIP lower, would.
        LyrNear.Eq(Lyrics.Follow.Target(1025f, 47f, 0f, Vp, Content, Band),
                Lyrics.Follow.Target(1000f, 72f, 25f, Vp, Content, Band));
    }

    [Fact]
    public void The_target_is_clamped_to_the_scrollable_range()
    {
        Assert.Equal(0f, Lyrics.Follow.Target(10f, 47f, 0f, Vp, Content, Band));
        Assert.Equal(1200f - Vp, Lyrics.Follow.Target(5000f, 47f, 0f, Vp, 1200f, Band));
        Assert.Equal(0f, Lyrics.Follow.Target(5000f, 47f, 0f, Vp, 100f, Band));   // content shorter than the viewport
    }

    [Fact]
    public void The_resync_ring_drains_over_four_seconds_in_120_rungs_quantised_up()
    {
        Assert.Equal(1f, Lyrics.Follow.ResyncProgress(4000, 0));
        Assert.Equal(1f, Lyrics.Follow.ResyncProgress(4000, 10));        // 0.9975 rounds UP to the full rung
        LyrNear.Eq(0.5f, Lyrics.Follow.ResyncProgress(4000, 2000), 0.0001f);
        LyrNear.Eq(1f / 120f, Lyrics.Follow.ResyncProgress(4000, 3999), 0.0001f);
        Assert.Equal(0f, Lyrics.Follow.ResyncProgress(4000, 4000));
        Assert.Equal(0f, Lyrics.Follow.ResyncProgress(4000, 9000));
    }

    [Fact]
    public void The_resync_is_due_at_the_deadline_and_not_before()
    {
        Assert.False(Lyrics.Follow.ResyncDue(4000, 3999));
        Assert.True(Lyrics.Follow.ResyncDue(4000, 4000));
        Assert.True(Lyrics.Follow.ResyncDue(4000, 4001));
    }
}

public class LyricsDofRampTests
{
    [Fact]
    public void A_line_never_driven_adopts_its_target()
    {
        Assert.Equal(2.5f, Lyrics.DofRamp.Step(float.NaN, 2.5f, 16f, out bool landed));
        Assert.True(landed);
    }

    [Fact]
    public void An_increase_snaps()
    {
        Assert.Equal(6.5f, Lyrics.DofRamp.Step(1.25f, 6.5f, 16f, out bool landed));
        Assert.True(landed);
    }

    [Fact]
    public void A_decrease_eases_with_a_65ms_time_constant_95_percent_in_200ms()
    {
        // Two 100 ms frames: each step's dt is clamped to DtMaxMs (0.2.9 LyricsView.cs:1625), so 200 ms of easing is
        // two steps, and together they cover exp(-200/65).
        float c = Lyrics.DofRamp.Step(4f, 0f, 100f, out bool landed);
        Assert.False(landed);
        c = Lyrics.DofRamp.Step(c, 0f, 100f, out landed);
        Assert.False(landed);
        LyrNear.Eq(4f * MathF.Exp(-200f / 65f), c, 0.001f);
        Assert.True(c < 4f * 0.05f);
    }

    [Fact]
    public void A_long_frame_gap_is_clamped_to_one_hundred_ms_of_easing()
    {
        float c = Lyrics.DofRamp.Step(4f, 0f, 200f, out _);
        LyrNear.Eq(4f * MathF.Exp(-Lyrics.DofRamp.DtMaxMs / Lyrics.DofRamp.TauMs), c, 0.001f);
    }

    [Fact]
    public void A_decrease_lands_exactly_inside_the_landing_gate()
    {
        Assert.Equal(0f, Lyrics.DofRamp.Step(0.005f, 0f, 16f, out bool landed));
        Assert.True(landed);
    }

    [Fact]
    public void A_ticker_gap_is_spent_as_at_most_one_100ms_step()
        => LyrNear.Eq(4f * MathF.Exp(-100f / 65f), Lyrics.DofRamp.Step(4f, 0f, 10_000f, out _), 0.001f);

    [Fact]
    public void The_target_is_the_surfaces_ladder_times_the_strength()
    {
        Assert.Equal(2.5f, Lyrics.DofRamp.Target(3, 1, large: false, strengthScale: 1f, suppressed: false));
        LyrNear.Eq(1.0f, Lyrics.DofRamp.Target(3, 1, large: false, strengthScale: 0.4f, suppressed: false), 0.0001f);
        Assert.Equal(2.0f, Lyrics.DofRamp.Target(3, 1, large: true, strengthScale: 1f, suppressed: false));
        Assert.Equal(6.5f, Lyrics.DofRamp.Target(40, 1, large: false, strengthScale: 1f, suppressed: false));
    }

    [Fact]
    public void No_active_line_and_a_detached_follow_both_mean_no_blur()
    {
        Assert.Equal(0f, Lyrics.DofRamp.Target(5, -1, large: false, strengthScale: 1f, suppressed: false));
        Assert.Equal(0f, Lyrics.DofRamp.Target(5, 1, large: false, strengthScale: 1f, suppressed: true));
    }

    [Fact]
    public void In_flight_writes_are_gated_at_the_pin_bucket_and_landings_are_exact()
    {
        Assert.False(Lyrics.DofRamp.ShouldWrite(2.0f, 2.3f, landed: false));
        Assert.True(Lyrics.DofRamp.ShouldWrite(2.0f, 2.5f, landed: false));
        Assert.True(Lyrics.DofRamp.ShouldWrite(0.01f, 0f, landed: true));
        Assert.False(Lyrics.DofRamp.ShouldWrite(0f, 0f, landed: true));
    }
}

public class LyricsInterludeTests
{
    [Fact]
    public void The_reserve_is_the_dot_plus_two_airs()
    {
        Assert.Equal(25f, Lyrics.Interlude.ReserveDip(false));
        Assert.Equal(32f, Lyrics.Interlude.ReserveDip(true));
    }

    [Fact]
    public void The_anchor_margin_is_viewport_independent_1035_rail_1459_stage()
    {
        LyrNear.Eq(103.5f, Lyrics.Interlude.AnchorMargin(false, Lyrics.Surface.FocalBand(false)));
        LyrNear.Eq(145.9f, Lyrics.Interlude.AnchorMargin(true, Lyrics.Surface.FocalBand(true)), 0.05f);
    }

    [Fact]
    public void The_dots_are_up_through_the_break_and_gone_a_second_before_the_next_line()
    {
        Assert.True(Lyrics.Interlude.DotsUp(true, 10_000, 20_000, 10_000));
        Assert.True(Lyrics.Interlude.DotsUp(true, 10_000, 20_000, 18_999));
        Assert.False(Lyrics.Interlude.DotsUp(true, 10_000, 20_000, 19_000));
        Assert.False(Lyrics.Interlude.DotsUp(false, 10_000, 20_000, 12_000));   // follow-gated
        Assert.False(Lyrics.Interlude.DotsUp(true, 0, 0, 0));                 // not in a break
    }

    [Fact]
    public void The_fill_spans_the_break_up_to_the_dots_exit()
    {
        Assert.Equal(0f, Lyrics.Interlude.Progress(10_000, 10_000, 20_000));
        LyrNear.Eq(0.5f, Lyrics.Interlude.Progress(14_500, 10_000, 20_000), 0.0001f);
        Assert.Equal(1f, Lyrics.Interlude.Progress(19_000, 10_000, 20_000));
        Assert.Equal(1f, Lyrics.Interlude.Progress(25_000, 10_000, 20_000));
    }

    [Fact]
    public void Each_dot_brightens_from_the_unsung_alpha_across_its_third()
    {
        LyrNear.Eq(0.58f, Lyrics.Interlude.DotAlpha(0, 0f, 1f), 0.0001f);
        Assert.Equal(1f, Lyrics.Interlude.DotAlpha(2, 1f, 1f));
        Assert.Equal(1f, Lyrics.Interlude.DotAlpha(0, 0.5f, 1f));
        LyrNear.Eq(0.79f, Lyrics.Interlude.DotAlpha(1, 0.5f, 1f), 0.0001f);
        LyrNear.Eq(0.58f, Lyrics.Interlude.DotAlpha(2, 0.5f, 1f), 0.0001f);
        LyrNear.Eq(0.9f, Lyrics.Interlude.DotAlpha(2, 1f, 0f), 0.0001f);          // the breath's trough
    }

    [Fact]
    public void The_breath_peaks_at_one_on_a_2600ms_period_and_reduced_motion_holds_the_peak()
    {
        LyrNear.Eq(0.5f, Lyrics.Interlude.Pulse(1000, 1000, false), 0.0001f);
        LyrNear.Eq(1f, Lyrics.Interlude.Pulse(1650, 1000, false), 0.0001f);
        Assert.Equal(1f, Lyrics.Interlude.Pulse(1000, 1000, true));
        Assert.Equal(1f, Lyrics.Interlude.BreathScale(1f));
        LyrNear.Eq(0.94f, Lyrics.Interlude.BreathScale(0f), 0.0001f);
    }

    [Fact]
    public void Only_a_wrapped_anchor_lifts_the_dots_further()
    {
        Assert.Equal(0f, Lyrics.Interlude.ExtraLift(47f, false));
        Assert.Equal(32f, Lyrics.Interlude.ExtraLift(120f, false));
    }
}

public class LyricsGlowTests
{
    [Fact]
    public void The_note_melt_is_320ms_and_the_hand_off_fade_is_240ms()
    {
        Assert.Equal(320f, Lyrics.Wipe.GlowOutMs);
        Assert.Equal(240f, Lyrics.Wipe.GlowFadeMs);
    }

    [Fact]
    public void A_line_synced_halo_fades_in_over_240_and_melts_over_320_into_the_line_end()
    {
        var line = Lyr.Timed(1000, 5000, "line");
        Assert.Equal(0f, Lyrics.Glow.VoiceAlpha(line, 1000, 5000, 1000));
        Assert.Equal(1f, Lyrics.Glow.VoiceAlpha(line, 1000, 5000, 1240));
        LyrNear.Eq(MathF.Sin(MathF.PI * 0.25f), Lyrics.Glow.VoiceAlpha(line, 1000, 5000, 4840), 0.0001f);
        Assert.Equal(0f, Lyrics.Glow.VoiceAlpha(line, 1000, 5000, 5000));
    }

    [Fact]
    public void A_held_note_blooms_at_three_quarters()
    {
        var line = Lyr.Line(1000, "la", Lyr.S(1000, 3000, "la"));
        LyrNear.Eq(0.75f, Lyrics.Glow.VoiceAlpha(line, 1000, 3000, 2000), 0.0001f);
    }

    [Fact]
    public void A_held_note_that_ends_the_line_melts_on_the_lines_320ms_clock()
    {
        var line = Lyr.Line(1000, "la", Lyr.S(1000, 3000, "la"));
        float expected = 0.75f * MathF.Sin(100f / 320f * MathF.PI * 0.5f);
        LyrNear.Eq(expected, Lyrics.Glow.VoiceAlpha(line, 1000, 3000, 2900), 0.0001f);
    }

    [Fact]
    public void A_short_syllable_never_glows()
    {
        var line = Lyr.Line(1000, "ta", Lyr.S(1000, 1300, "ta"));
        Assert.Equal(0f, Lyrics.Glow.VoiceAlpha(line, 1000, 1300, 1150));
    }

    [Fact]
    public void An_outgoing_halo_fades_from_its_live_alpha_over_240ms()
    {
        Assert.Equal(0.8f, Lyrics.Glow.FadeOut(0.8f, 0f));
        LyrNear.Eq(1f - MathF.Sin(MathF.PI * 0.25f), Lyrics.Glow.FadeOut(1f, 120f), 0.0001f);
        Assert.Equal(0f, Lyrics.Glow.FadeOut(0.8f, 240f));
    }
}

public class LyricsSampleClockTests
{
    [Fact]
    public void A_sample_is_placed_at_now_minus_its_age()
        => Assert.Equal(7_500_000L, Lyrics.SampleClock.SampleQpc(1000, 1250, 10_000_000, 10_000_000));

    [Fact]
    public void A_stamp_from_the_future_reads_as_now()
        => Assert.Equal(10_000_000L, Lyrics.SampleClock.SampleQpc(2000, 1000, 10_000_000, 10_000_000));

    [Fact]
    public void An_unusable_frequency_reads_qpc_as_milliseconds()
        => Assert.Equal(750L, Lyrics.SampleClock.SampleQpc(1000, 1250, 1000, 0));

    [Fact]
    public void Two_hosts_on_different_epochs_agree_on_the_same_age()
        => Assert.Equal(Lyrics.SampleClock.SampleQpc(1000, 1250, 5_000, 1000),
                        Lyrics.SampleClock.SampleQpc(90_000_000, 90_000_250, 5_000, 1000));
}
