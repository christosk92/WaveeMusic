// ── Wavee.Tests/EqCurveRulesTests.cs — the equalizer curve's geometry and rules ─────────────────────────────────────
//
// The curve (`Controls.EqualizerCurve`, Platform/Controls.Picker.cs) draws and hit-tests through `EqCurveGeometry` and
// `EqCurveRules` (Platform/EqCurve.Rules.cs). These facts pin the decisions behind the three shipped bugs:
//
//   · it did not scale — the geometry is a function of the MEASURED width, spans exactly its plot, has no width floor,
//     and maps a pointer through whatever size it is handed;
//   · the band labels sat left of their gridlines — a label box is centred on the SAME x as its node and gridline;
//   · the value badge never moved — the badge band follows the pointer and rests on the active band, and the badge
//     is centred on its node and clamped inside the plot.
//
// No window, no engine, no element is rendered. That the component actually stretches to its card lane is a layout
// fact only a live run shows.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EqCurveRulesTests
{
    const int Bands = EqCurveGeometry.BandCount;
    const int Last = EqCurveGeometry.LastBand;
    const float Eps = 1e-3f;

    static readonly float[] Widths = [120f, 151f, 200f, 260f, 344f, 416f, 560f, 720f, 898f, 1280f, 1600f];
    static readonly float[] Vocal = [-2f, -1f, 0f, 2f, 4f, 4f, 2f, 0f, -1f, -2f];
    static readonly float[] Proof = [12f, -12f, 12f, -12f, 12f, -12f, 12f, -12f, 12f, -12f];

    static void Near(float expected, float actual)
        => Assert.True(MathF.Abs(expected - actual) <= Eps, $"expected {expected}, got {actual}");

    // ── the shape ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Band_count_and_range_match_the_settings_store_and_the_labels()
    {
        Assert.Equal(Settings.Eq.BandCount, Bands);
        Assert.Equal(Settings.Eq.MinGainDb, EqCurveGeometry.MinGain);
        Assert.Equal(Settings.Eq.MaxGainDb, EqCurveGeometry.MaxGain);
        Assert.Equal(Bands, Controls.EqualizerBands.Length);
        Assert.Equal(EqCurveRules.GainRungs.Length, EqCurveRules.GainRungLabels.Length);
        Assert.Equal("1k", Controls.EqualizerBands[EqCurveRules.DefaultActiveBand]);
    }

    // ── bug 1: it scales with its lane ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bands_span_exactly_the_plot_of_the_measured_width()
    {
        foreach (float w in Widths)
        {
            var g = EqCurveGeometry.For(w);
            Assert.Equal(w, g.Width);
            Near(EqCurveGeometry.PadLeft, g.BandX(0));
            Near(w - EqCurveGeometry.PadRight, g.BandX(Last));
            for (int i = 1; i < Bands; i++) Assert.True(g.BandX(i) > g.BandX(i - 1), $"w={w} band {i}");
            Assert.True(g.PlotRight <= g.Width, $"w={w}");
        }
    }

    [Fact]
    public void A_wider_lane_spreads_the_bands_and_a_narrower_one_never_overflows()
    {
        var narrow = EqCurveGeometry.For(200f);
        var wide = EqCurveGeometry.For(1280f);
        Assert.True(wide.BandPitch > narrow.BandPitch);
        Near(1280f - EqCurveGeometry.PadRight, wide.BandX(Last));
        // The old surface was floored at 260 DIP (and its plot at 120): a lane narrower than that overflowed by design.
        Assert.Equal(200f, narrow.Width);
        Near(200f - EqCurveGeometry.PadRight, narrow.BandX(Last));
    }

    [Fact]
    public void Height_follows_the_width_inside_its_band_unless_fixed()
    {
        Assert.Equal(EqCurveGeometry.MinAutoHeight, EqCurveGeometry.For(300f).Height);
        Near(898f * EqCurveGeometry.HeightPerWidth, EqCurveGeometry.For(898f).Height);
        Assert.Equal(EqCurveGeometry.MaxAutoHeight, EqCurveGeometry.For(1600f).Height);
        Assert.Equal(300f, EqCurveGeometry.For(1600f, 300f).Height);
        Assert.Equal(EqCurveGeometry.AutoHeight(720f), EqCurveGeometry.For(720f, 0f).Height);
    }

    [Fact]
    public void An_unmeasured_or_garbage_width_plots_nothing()
    {
        foreach (float w in new[] { 0f, -5f, float.NaN, float.PositiveInfinity })
        {
            var g = EqCurveGeometry.For(w);
            Assert.Equal(0f, g.Width);
            Assert.Equal(0f, g.PlotWidth);
            Assert.Equal(0, g.NearestBand(100f));
        }
    }

    [Fact]
    public void A_pointer_maps_through_the_size_it_is_handed()
    {
        foreach (float w in Widths)
        {
            var g = EqCurveGeometry.For(w);
            for (int i = 0; i < Bands; i++)
            {
                Assert.Equal(i, g.NearestBand(g.BandX(i)));
                Assert.Equal(i, g.NearestBand(g.BandX(i) + g.BandPitch * 0.45f * (i == Last ? -1f : 1f)));
            }
            Assert.Equal(0, g.NearestBand(-100f));
            Assert.Equal(Last, g.NearestBand(w + 500f));
        }
        // The SAME pointer x names a different band at a different width: a handler that mapped through a stale size
        // would edit the wrong band after the card was resized.
        Assert.Equal(8, EqCurveGeometry.For(560f).NearestBand(500f));
        Assert.Equal(4, EqCurveGeometry.For(1120f).NearestBand(500f));
    }

    [Fact]
    public void A_drag_maps_y_to_a_half_decibel_inside_the_range()
    {
        var g = EqCurveGeometry.For(720f);
        Assert.Equal(12f, g.SnappedGainAt(g.PlotTop));
        Assert.Equal(-12f, g.SnappedGainAt(g.PlotBottom));
        Assert.Equal(0f, g.SnappedGainAt(g.ZeroY));
        Assert.Equal(12f, g.SnappedGainAt(-500f));     // above the plot: clamped, never extrapolated
        Assert.Equal(-12f, g.SnappedGainAt(5000f));
        for (float gain = -12f; gain <= 12f; gain += 0.5f)
            Assert.Equal(gain, g.SnappedGainAt(g.Y(gain)));
        for (float y = g.PlotTop; y <= g.PlotBottom; y += 0.37f)
        {
            float s = g.SnappedGainAt(y);
            Assert.Equal(MathF.Round(s * 2f), s * 2f);
        }
    }

    // ── bug 2: every label sits under its own gridline ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_band_label_is_centred_on_its_node_and_gridline()
    {
        foreach (float w in Widths)
        {
            var g = EqCurveGeometry.For(w);
            for (int i = 0; i < Bands; i++)
            {
                if (!g.ShowsBandLabel(i)) continue;
                Near(g.BandX(i), g.BandLabelLeft(i) + EqCurveGeometry.BandLabelWidth * 0.5f);
            }
        }
    }

    [Fact]
    public void Band_labels_stay_inside_the_surface_below_the_plot_and_never_overlap()
    {
        foreach (float w in Widths)
        {
            var g = EqCurveGeometry.For(w);
            Assert.True(g.ShowsBandLabel(0));
            Assert.True(g.ShowsBandLabel(Last));   // the right edge is what says the axis is complete
            Assert.True(g.BandLabelTop >= g.PlotBottom);
            Assert.True(g.BandLabelTop + EqCurveGeometry.LabelHeight <= g.Height);
            float lastRight = float.NegativeInfinity;
            for (int i = 0; i < Bands; i++)
            {
                if (!g.ShowsBandLabel(i)) continue;
                float left = g.BandLabelLeft(i);
                Assert.True(left >= 0f && left + EqCurveGeometry.BandLabelWidth <= g.Width + Eps, $"w={w} band {i}");
                Assert.True(left >= lastRight - Eps, $"w={w} band {i} overlaps its neighbour");
                lastRight = left + EqCurveGeometry.BandLabelWidth;
            }
        }
    }

    [Theory]
    [InlineData(1600f, 1)]
    [InlineData(344f, 1)]
    [InlineData(343f, 3)]
    [InlineData(200f, 3)]
    [InlineData(160f, 3)]
    [InlineData(151f, Last)]
    public void The_label_stride_thins_evenly(float width, int stride)
    {
        var g = EqCurveGeometry.For(width);
        Assert.Equal(stride, g.BandLabelStride);
        for (int i = 0; i < Bands; i++) Assert.Equal(i % stride == 0, g.ShowsBandLabel(i));
    }

    [Fact]
    public void Gain_labels_centre_on_their_rung_and_end_before_the_plot_origin()
    {
        var g = EqCurveGeometry.For(720f);
        foreach (float gain in EqCurveRules.GainRungs)
            Near(g.Y(gain), g.GainLabelTop(gain) + EqCurveGeometry.LabelHeight * 0.5f);
        Near(g.PlotLeft - EqCurveGeometry.GainLabelGap, g.GainLabelWidth);
        Near(g.PlotTop, g.Y(EqCurveGeometry.MaxGain));
        Near(g.PlotBottom, g.Y(EqCurveGeometry.MinGain));
        Near((g.PlotTop + g.PlotBottom) * 0.5f, g.ZeroY);
        Assert.Equal("0 dB", EqCurveRules.GainRungLabels[Array.IndexOf(EqCurveRules.GainRungs, 0f)]);
    }

    // ── the curve and the nodes share one mapping ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_curve_passes_through_every_node_on_the_same_x_mapping()
    {
        var g = EqCurveGeometry.For(898f);
        for (int i = 0; i < Bands; i++)
        {
            Near(g.BandX(i), g.X(i));
            Near(EqCurveRules.GainAt(Vocal, i), EqCurveRules.Sample(Vocal, i));
        }
        Near(g.PlotLeft, g.X(-3f));    // the spline parameter clamps to the plot like the nodes do
        Near(g.PlotRight, g.X(42f));
    }

    [Fact]
    public void The_spline_stays_inside_the_gain_range_and_a_flat_curve_is_flat()
    {
        float[] flat = new float[Bands];
        for (float u = 0f; u <= Last; u += 0.05f)
        {
            Assert.InRange(EqCurveRules.Sample(Proof, u), EqCurveGeometry.MinGain, EqCurveGeometry.MaxGain);
            Assert.Equal(0f, EqCurveRules.Sample(flat, u));
        }
    }

    [Fact]
    public void A_short_or_garbage_gain_vector_reads_flat_where_it_is_missing()
    {
        float[] gains = [20f, float.NaN, -30f];
        Assert.Equal(12f, EqCurveRules.GainAt(gains, 0));
        Assert.Equal(0f, EqCurveRules.GainAt(gains, 1));
        Assert.Equal(-12f, EqCurveRules.GainAt(gains, 2));
        Assert.Equal(0f, EqCurveRules.GainAt(gains, 5));
        Assert.Equal(0f, EqCurveRules.GainAt(gains, -1));
        Assert.Equal(0f, EqCurveRules.GainAt(gains, Bands));
    }

    // ── bug 3: the badge names and follows a band ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 5, true, 2)]      // the hovered (or dragged — a drag writes the hover) band wins
    [InlineData(-1, 5, true, 5)]     // no pointer on the curve: it rests on the active, keyboard band
    [InlineData(-1, 8, true, 8)]
    [InlineData(9, 0, true, 9)]
    [InlineData(10, 3, true, 3)]     // an out-of-range hover is no hover
    [InlineData(2, 5, false, -1)]    // disabled: no hot node, so no badge
    [InlineData(-1, -1, true, -1)]
    public void The_badge_names_the_hovered_band_else_the_active_one(int hover, int active, bool enabled, int expected)
        => Assert.Equal(expected, EqCurveRules.BadgeBand(hover, active, enabled));

    [Fact]
    public void Hot_nodes_are_the_active_and_hovered_bands_only_while_enabled()
    {
        for (int i = 0; i < Bands; i++)
        {
            Assert.Equal(i == 5 || i == 2, EqCurveRules.IsHot(i, hover: 2, active: 5, enabled: true));
            Assert.False(EqCurveRules.IsHot(i, hover: 2, active: 5, enabled: false));
        }
    }

    [Fact]
    public void The_badge_sits_centred_above_a_mid_plot_node()
    {
        var g = EqCurveGeometry.For(898f);
        var (x, y) = g.BadgeAt(5, 4f);
        Near(g.BandX(5), x + EqCurveGeometry.BadgeWidth * 0.5f);
        Near(g.Y(4f) - EqCurveGeometry.BadgeClearance, y + EqCurveGeometry.BadgeHeight);
    }

    [Fact]
    public void The_badge_moves_with_its_band()
    {
        var g = EqCurveGeometry.For(898f);
        float prev = float.NegativeInfinity;
        for (int i = 0; i < Bands; i++)
        {
            float x = g.BadgeAt(i, 0f).X;
            Assert.True(x >= prev, $"band {i}");
            prev = x;
        }
        Assert.True(g.BadgeAt(2, 0f).X < g.BadgeAt(7, 0f).X);
        Assert.True(g.BadgeAt(4, 6f).Y < g.BadgeAt(4, -6f).Y);   // and with its gain
    }

    [Fact]
    public void The_edge_bands_badges_slide_inward_instead_of_hanging_off_the_card()
    {
        var g = EqCurveGeometry.For(720f);
        Near(g.PlotLeft, g.BadgeAt(0, 0f).X);
        Near(g.PlotRight - EqCurveGeometry.BadgeWidth, g.BadgeAt(Last, 0f).X);
    }

    [Fact]
    public void A_badge_for_a_node_at_the_top_drops_below_it_instead_of_clipping()
    {
        var g = EqCurveGeometry.For(720f);
        Assert.True(g.BadgeBelow(EqCurveGeometry.MaxGain));
        Assert.False(g.BadgeBelow(0f));
        var (_, y) = g.BadgeAt(3, EqCurveGeometry.MaxGain);
        Near(g.Y(EqCurveGeometry.MaxGain) + EqCurveGeometry.BadgeClearance, y);
    }

    [Fact]
    public void The_badge_never_leaves_the_plot_or_the_surface()
    {
        float[] gains = [-12f, -6f, 0f, 6f, 9f, 11.5f, 12f];
        foreach (float w in Widths)
        {
            var g = EqCurveGeometry.For(w);
            bool fitsPlot = g.PlotWidth >= EqCurveGeometry.BadgeWidth;
            for (int band = 0; band < Bands; band++)
                foreach (float gain in gains)
                {
                    var (x, y) = g.BadgeAt(band, gain);
                    if (fitsPlot)
                        Assert.InRange(x, g.PlotLeft - Eps, g.PlotRight - EqCurveGeometry.BadgeWidth + Eps);
                    Assert.InRange(x, -Eps, g.Width - EqCurveGeometry.BadgeWidth + Eps);
                    Assert.InRange(y, EqCurveGeometry.EdgeInset - Eps, g.PlotBottom - EqCurveGeometry.BadgeHeight + Eps);
                }
        }
    }

    [Theory]
    [InlineData(2f, "+2 dB")]
    [InlineData(0f, "0 dB")]
    [InlineData(-1.5f, "-1.5 dB")]
    [InlineData(12f, "+12 dB")]
    [InlineData(-12f, "-12 dB")]
    [InlineData(float.NaN, "0 dB")]
    public void The_badge_value_reads_as_signed_decibels(float gain, string expected)
        => Assert.Equal(expected, EqCurveRules.DbText(gain));

    [Fact]
    public void The_badge_value_of_a_half_decibel_step_is_one_cached_string()
        => Assert.Same(EqCurveRules.DbText(3.5f), EqCurveRules.DbText(3.5f));
}
