// ── Wavee.Tests/VisualizerFluentGeometryTests.cs — the Fluent faces' pure arithmetic (Shell/Visualizer.Fluent.UI.cs) ─────
//
// Pure: `Visualizer.FluentGeometry` takes numbers and spans and touches no engine, no signal and no table. The faces that
// bind these numbers over the slab (Bloom, Bars, Ring, Orbit, Aurora, Timeline) are covered by the `--fake` and
// real-account walks, not a unit test. Plan: stage visualizers app plan §1.3 (shared geometry), §2.2-§2.7.

using Wavee;
using Xunit;

using Geo = Wavee.Visualizer.FluentGeometry;

namespace Wavee.Tests;

public class VisualizerFluentGeometryTests
{
    const float Eps = 1e-4f;

    // ── Bloom ──

    [Fact]
    public void Clouds_breathe_between_rest_and_rest_plus_gain_and_rest_at_mid_under_reduced_motion()
    {
        foreach (var c in Geo.Clouds)
        {
            Assert.Equal(c.Base, Geo.CloudScale(0f, c.Base, c.Gain, 1f), 5);
            Assert.Equal(c.Base + c.Gain, Geo.CloudScale(1f, c.Base, c.Gain, 1f), 5);
            Assert.Equal(c.Base + c.Gain, Geo.CloudScale(7f, c.Base, c.Gain, 1f), 5);
            Assert.Equal(c.Base + 0.5f * c.Gain, Geo.CloudScale(1f, c.Base, c.Gain, 0f), 5);
        }
        Assert.Equal(0.55f, Geo.CloudOpacity(0f, 1f), 5);
        Assert.Equal(1f, Geo.CloudOpacity(1f, 1f), 5);
        Assert.Equal(Geo.CloudOpacity(0.5f, 1f), Geo.CloudOpacity(0.9f, 0f), 5);
        Assert.Equal(0.55f, Geo.CloudOpacity(float.NaN, 1f), 5);
    }

    [Fact]
    public void Bloom_shadow_swells_from_its_rest_alpha_to_the_authored_one_on_the_lows()
    {
        Assert.Equal(0.6f, Geo.BloomShadowOpacity(0f, 1f), 5);
        Assert.Equal(1f, Geo.BloomShadowOpacity(1f, 1f), 5);
        Assert.Equal(0.6f, Geo.BloomShadowOpacity(1f, 0f), 5);
    }

    // ── Bars ──

    [Theory]
    [InlineData(40)]
    [InlineData(20)]
    public void Bar_band_map_is_mirrored_about_the_centre_bass_in_the_middle(int n)
    {
        for (int k = 0; k < n; k++)
        {
            Assert.Equal(Geo.BarBand(k, n), Geo.BarBand(n - 1 - k, n));
            Assert.InRange(Geo.BarBand(k, n), 0, Visualizer.Bands.Count - 1);
        }
        Assert.True(Geo.BarBand(n / 2, n) < Geo.BarBand(0, n));
        for (int k = n / 2; k < n - 1; k++) Assert.True(Geo.BarBand(k + 1, n) >= Geo.BarBand(k, n));
    }

    [Fact]
    public void Bar_level_never_drops_below_the_floor_and_rests_there_under_reduced_motion()
    {
        Assert.Equal(Geo.RestBar, Geo.BarLevel(0f, 1f));
        Assert.Equal(0.5f, Geo.BarLevel(0.5f, 1f));
        Assert.Equal(1f, Geo.BarLevel(3f, 1f));
        Assert.Equal(Geo.RestBar, Geo.BarLevel(0.8f, 0f));
        Assert.Equal(Geo.RestBar, Geo.BarLevel(float.NaN, 1f));
    }

    [Fact]
    public void Bar_tint_stays_on_A_for_short_bars_and_moves_toward_B_monotonically()
    {
        Assert.Equal(0f, Geo.BarTint(Geo.RestBar));
        Assert.Equal(0f, Geo.BarTint(0.35f));
        Assert.Equal(0.7f, Geo.BarTint(1f), 5);
        float last = 0f;
        for (float v = 0f; v <= 1f; v += 0.05f) { float t = Geo.BarTint(v); Assert.True(t >= last); last = t; }
    }

    // ── Ring ──

    [Theory]
    [InlineData(96)]
    [InlineData(36)]
    public void Ring_band_map_is_mirrored_about_the_top(int n)
    {
        Assert.Equal(0, Geo.RingBand(0, n));
        Assert.Equal(Visualizer.Bands.Count - 10, Geo.RingBand(n / 2, n));
        for (int k = 1; k < n; k++) Assert.Equal(Geo.RingBand(k, n), Geo.RingBand(n - k, n));
    }

    [Fact]
    public void Ring_starts_at_twelve_o_clock_and_turns_clockwise()
    {
        Assert.Equal(-MathF.PI / 2f, Geo.RingAngle(0, 96), 5);
        Assert.Equal(0f, Geo.RingAngle(24, 96), 5);            // a quarter turn later: 3 o'clock (y down)
        Assert.True(Geo.RingAngle(95, 96) < 1.5f * MathF.PI);
    }

    [Fact]
    public void Ring_capsules_rest_at_their_base_length_under_reduced_motion()
    {
        Assert.Equal(12f, Geo.RingLength(1f, 12f, 120f, 0f));
        Assert.Equal(132f, Geo.RingLength(1f, 12f, 120f, 1f));
        Assert.Equal(12f, Geo.RingLength(float.NaN, 12f, 120f, 1f));
    }

    [Fact]
    public void Ripples_decelerate_out_to_their_growth_and_fade_to_nothing()
    {
        Assert.Equal(1f, Geo.RippleScale(0f), 5);
        Assert.Equal(Geo.RippleGrowth, Geo.RippleScale(1f), 5);
        float last = 0f, lastStep = float.MaxValue;
        for (int i = 0; i <= 20; i++)
        {
            float s = Geo.RippleScale(i / 20f);
            Assert.True(s >= last);
            if (i > 0) { Assert.True(s - last <= lastStep + Eps); lastStep = s - last; }   // decelerating
            last = s;
        }
        Assert.Equal(0.45f, Geo.RippleOpacity(0f, 1f), 5);
        Assert.Equal(0f, Geo.RippleOpacity(1f, 1f));
        Assert.Equal(0f, Geo.RippleOpacity(0.3f, 0f));
        Assert.Equal(0f, Geo.RippleOpacity(float.NaN, 1f));
    }

    // ── Orbit ──

    [Fact]
    public void Orbit_sweep_spans_eight_to_eighty_eight_percent_and_rests_at_thirty()
    {
        Assert.Equal(0.08f, Geo.OrbitSweep(0f, 1f), 5);
        Assert.Equal(0.88f, Geo.OrbitSweep(1f, 1f), 5);
        Assert.Equal(0.88f, Geo.OrbitSweep(4f, 1f), 5);
        Assert.Equal(0.3f, Geo.OrbitSweep(1f, 0f), 5);
    }

    [Fact]
    public void Spin_is_beat_locked_continuous_across_a_beat_and_wrapped()
    {
        Assert.Equal(0f, Geo.SpinDeg(0, 0f, 3.5f));
        Assert.Equal(0f, Geo.SpinDeg(1000, 0.4f, 0f));
        float before = Geo.SpinDeg(41, 1f, 3.5f), after = Geo.SpinDeg(42, 0f, 3.5f);
        Assert.Equal(before, after, 3);
        for (int b = 0; b < 100_000; b += 977)
        {
            float d = Geo.SpinDeg(b, 0.5f, -5.3f);
            Assert.True(d > -360f && d < 360f);
        }
    }

    [Theory]
    [InlineData(0.08f, 0.07f)]
    [InlineData(1.5f, 0.07f)]
    [InlineData(5.5292f, 0.07f)]
    [InlineData(2f, 0.2f)]
    public void Arc_chain_covers_the_sweep_end_to_end_without_gaps(float sweep, float half)
    {
        int n = Geo.ArcChainCount(sweep, half);
        float straight = Geo.ArcChainStraight(n, sweep, half), start = 0.3f;
        float first = Geo.ArcChainAngle(0, n, start, sweep, half), last = Geo.ArcChainAngle(n - 1, n, start, sweep, half);
        Assert.Equal(start, first - 0.5f * straight, 4);                 // the first straight part starts at the arc's start
        Assert.Equal(start + sweep, last + 0.5f * straight, 4);          // the last one ends at its end
        for (int j = 1; j < n; j++)
        {
            float step = Geo.ArcChainAngle(j, n, start, sweep, half) - Geo.ArcChainAngle(j - 1, n, start, sweep, half);
            Assert.True(step > 0f && step <= Geo.ChainStep * half + Eps);   // straight parts overlap: no gap, no facet
        }
    }

    [Fact]
    public void A_short_arc_is_one_capsule_spanning_the_whole_sweep()
    {
        Assert.Equal(1, Geo.ArcChainCount(0.1f, 0.07f));
        Assert.Equal(0.1f, Geo.ArcChainStraight(1, 0.1f, 0.07f), 5);
        Assert.Equal(1f + 0.05f, Geo.ArcChainAngle(0, 1, 1f, 0.1f, 0.07f), 5);
        Assert.Equal(1, Geo.ArcChainCount(float.NaN, 0.07f));
    }

    [Fact]
    public void Bpm_label_rounds_the_tempo_and_dashes_an_unknown_one()
    {
        Assert.Equal("—", Geo.BpmLabel(0));
        Assert.Equal("128", Geo.BpmLabel(1284));
        Assert.Equal("118", Geo.BpmLabel(1175));
        Assert.Equal("90", Geo.BpmLabel(900));
    }

    // ── Aurora ──

    [Fact]
    public void Aurora_reach_follows_the_energy_and_rests_low()
    {
        Assert.Equal(Geo.AuroraAmpMin, Geo.AuroraAmp(0f, 1f), 5);
        Assert.Equal(Geo.AuroraAmpMin + Geo.AuroraAmpGain, Geo.AuroraAmp(1f, 1f), 5);
        Assert.Equal(Geo.AuroraAmpMin, Geo.AuroraAmp(1f, 0f), 5);
    }

    [Fact]
    public void Aurora_drift_is_slow_in_silence_faster_with_energy_and_frozen_under_reduced_motion()
    {
        float quiet = Geo.AuroraAdvance(0f, 16f, 0.0004f, 0f, 1f), loud = Geo.AuroraAdvance(0f, 16f, 0.0004f, 1f, 1f);
        Assert.True(quiet > 0f && loud > quiet * 7f);
        Assert.Equal(1.25f, Geo.AuroraAdvance(1.25f, 16f, 0.0004f, 1f, 0f));
        Assert.Equal(1.25f, Geo.AuroraAdvance(1.25f, 0f, 0.0004f, 1f, 1f));
        float p = 0f;
        for (int i = 0; i < 100_000; i++) p = Geo.AuroraAdvance(p, 50f, 0.0009f, 1f, 1f);
        Assert.True(float.IsFinite(p) && MathF.Abs(p) < 20f * MathF.PI);
    }

    [Fact]
    public void Aurora_ribbon_meets_its_base_line_at_both_ends_and_stays_in_the_box()
    {
        var into = new float[256];
        Geo.AuroraRibbon(into, 0.15f, 0.4f, 2.3f, 1);
        Assert.Equal(0.15f, into[0], 4);
        Assert.Equal(0.15f, into[^1], 4);
        foreach (float v in into) Assert.InRange(v, 0f, 1f);
        var again = new float[256];
        Geo.AuroraRibbon(again, 0.15f, 0.4f, 2.3f, 1);
        Assert.Equal(into, again);                                       // deterministic: the same phase draws the same curtain
    }

    [Fact]
    public void Aurora_stops_are_strictly_increasing_inside_the_unit_range()
    {
        foreach (var (f, r) in new[] { (0.15f, 0.4f), (0.0f, 0.0f), (0.9f, 2f), (float.NaN, float.NaN), (0.13f, 0.35f) })
        {
            var (floor, peak) = Geo.AuroraStops(f, r);
            Assert.True(floor > 0f && floor < peak && peak < 1f);
        }
    }

    // ── Timeline ──

    [Fact]
    public void Timeline_height_is_zero_for_silence_full_for_full_and_monotone()
    {
        Assert.Equal(0f, Geo.TimelineHeight(0f, 0f, 0f));
        Assert.Equal(1f, Geo.TimelineHeight(1f, 1f, 1f), 5);
        Assert.True(Geo.TimelineHeight(0.5f, 0.2f, 0.1f) > Geo.TimelineHeight(0.4f, 0.2f, 0.1f));
        Assert.Equal(0f, Geo.TimelineHeight(float.NaN, float.NaN, float.NaN));
    }

    [Theory]
    [InlineData(360, 360)]
    [InlineData(360, 90)]
    [InlineData(100, 360)]
    public void Timeline_buckets_cover_every_sample_and_keep_the_peaks(int samples, int capsules)
    {
        var src = new float[samples];
        for (int i = 0; i < samples; i++) src[i] = (i % 7) / 7f;
        src[samples / 3] = 1f;
        float peak = 0f;
        for (int k = 0; k < capsules; k++) peak = MathF.Max(peak, Geo.Bucket(src, k, capsules));
        Assert.Equal(1f, peak);
        Assert.Equal(0f, Geo.Bucket(ReadOnlySpan<float>.Empty, 3, capsules));
    }

    [Fact]
    public void Playhead_x_clamps_to_the_timeline()
    {
        Assert.Equal(100f, Geo.PlayedX(-1f, 100f, 800f));
        Assert.Equal(500f, Geo.PlayedX(0.5f, 100f, 800f));
        Assert.Equal(900f, Geo.PlayedX(2f, 100f, 800f));
        Assert.Equal(100f, Geo.PlayedX(float.NaN, 100f, 800f));
    }

    [Fact]
    public void Beat_ruler_marks_every_fourth_beat_from_the_bar_phase()
    {
        int inBar = Geo.BeatInBar(0f, 0f);
        Assert.Equal(0, inBar);
        Assert.True(Geo.IsDownbeat(inBar, 0));
        Assert.True(Geo.IsDownbeat(inBar, 4));
        Assert.True(Geo.IsDownbeat(inBar, -4));
        Assert.False(Geo.IsDownbeat(inBar, 1));
        Assert.False(Geo.IsDownbeat(inBar, -3));

        Assert.Equal(2, Geo.BeatInBar(0.5f, 0f));                         // half way through the bar: the third beat
        Assert.Equal(2, Geo.BeatInBar(0.5f + 0.3f / 4f, 0.3f));            // … still, 30 % into it
        Assert.True(Geo.IsDownbeat(2, 2));
        Assert.True(Geo.IsDownbeat(2, -2));
        Assert.Equal(3, Geo.BeatInBar(0.99f, 0.96f));
    }

    [Fact]
    public void Beat_ruler_marks_a_three_beat_bar_from_the_phase_over_its_span()
    {
        Assert.Equal(0, Geo.BeatInBar(0f, 0f, 3));
        Assert.Equal(1, Geo.BeatInBar(1f / 3f, 0f, 3));
        Assert.Equal(2, Geo.BeatInBar(2f / 3f + 0.4f / 3f, 0.4f, 3));
        Assert.True(Geo.IsDownbeat(0, 3, 3));
        Assert.True(Geo.IsDownbeat(1, -1, 3));
        Assert.False(Geo.IsDownbeat(0, 4, 3));
        Assert.Equal(Geo.IsDownbeat(2, 2), Geo.IsDownbeat(2, 2, Geo.BeatsPerBar));
    }

    /// <summary>Feeds a BarCounter a played grid of <paramref name="beatsPerBar"/>-beat bars, beat by beat, the way the
    /// slab moves: the bar index and the bar phase at each beat's start.</summary>
    static Geo.BarCounter Play(int beatsPerBar, int beats, int firstBeat = 0)
    {
        var c = new Geo.BarCounter();
        for (int b = firstBeat; b < firstBeat + beats; b++) c.Observe(b / beatsPerBar, b, (b % beatsPerBar) / (float)beatsPerBar);
        return c;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    public void Bar_counter_learns_the_real_bar_from_two_crossed_downbeats(int beatsPerBar)
    {
        var c = Play(beatsPerBar, 3 * beatsPerBar + 2);
        Assert.Equal(beatsPerBar, c.Span);
        int beat = 3 * beatsPerBar + 1;                              // the second beat of the fourth bar
        Assert.Equal(1, c.BeatInBar(beat, 1f / beatsPerBar, 0f));
        Assert.True(Geo.IsDownbeat(c.BeatInBar(beat, 1f / beatsPerBar, 0f), beatsPerBar - 1, c.Span));
    }

    [Fact]
    public void Bar_counter_starts_at_four_and_a_seek_drops_the_anchor_but_keeps_the_span()
    {
        var fresh = new Geo.BarCounter();
        Assert.Equal(Geo.BeatsPerBar, fresh.Span);
        Assert.Equal(2, fresh.BeatInBar(10, 0.5f, 0f));               // no edge seen yet: the phase over 4 beats

        var c = Play(3, 10);
        Assert.Equal(3, c.Span);
        c.Observe(40, 121, 0.66f);                                    // a seek into the middle of bar 40: not a crossing
        Assert.Equal(3, c.Span);
        Assert.Equal(2, c.BeatInBar(121, 2f / 3f, 0f));              // un-anchored: the phase over the learned span
        c.Observe(41, 123, 0f);                                       // the next downbeat crossed re-anchors
        Assert.Equal(0, c.BeatInBar(123, 0f, 0f));
        Assert.Equal(1, c.BeatInBar(124, 1f / 3f, 0f));
        Assert.Equal(3, c.Span);                                      // one anchored edge never re-measures
    }

    [Fact]
    public void Bar_counter_ignores_a_gap_longer_than_any_bar()
    {
        var c = Play(4, 9);
        c.Observe(3, 9 + Geo.MaxBeatsPerBar + 4, 0f);                 // bar 2 → 3 after far too many beats
        Assert.Equal(4, c.Span);
    }

    [Fact]
    public void Ruler_ticks_fade_away_from_the_playhead()
    {
        Assert.Equal(1f, Geo.TickFade(0f, Geo.TickSpan));
        float last = 1f;
        for (float d = 0.5f; d <= Geo.TickSpan + 1f; d += 0.5f)
        {
            float a = Geo.TickFade(d, Geo.TickSpan);
            Assert.True(a <= last && a >= 0f);
            Assert.Equal(a, Geo.TickFade(-d, Geo.TickSpan));
            last = a;
        }
        Assert.Equal(0f, Geo.TickFade(Geo.TickSpan + 1f, Geo.TickSpan), 5);
    }

    [Fact]
    public void Playhead_glow_rests_lit_and_follows_the_level()
    {
        Assert.Equal(0.30f, Geo.PlayheadGlow(0f, 1f, dark: true), 5);
        Assert.Equal(0.75f, Geo.PlayheadGlow(1f, 1f, dark: true), 5);
        Assert.Equal(0.30f, Geo.PlayheadGlow(1f, 0f, dark: true), 5);
        Assert.True(Geo.PlayheadGlow(1f, 1f, dark: false) < Geo.PlayheadGlow(1f, 1f, dark: true));
    }
}
