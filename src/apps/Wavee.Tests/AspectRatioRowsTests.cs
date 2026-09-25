// ── Wavee.Tests/AspectRatioRowsTests.cs — which numeric-aspect row the on-media menu checks ─────────────────────────
//
// `Video.AspectRatioRows` is the pure rule behind the four Custom rows the on-media ⋯ → Aspect ratio cascade adds
// below Fit/Crop/Stretch/Native (16:9, 4:3, 21:9, 2.39:1 — the same list the engine's own, suppressed transport
// offers). Pure `double` math, no `Platform.Settings` and no menu: `OnMediaMenuShapeTests` covers the built menu.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AspectRatioRowsTests
{
    [Fact]
    public void Standard_lists_the_four_ratios_in_menu_order()
    {
        Assert.Equal(4, Video.AspectRatioRows.Standard.Length);
        Assert.Equal(16.0 / 9.0, Video.AspectRatioRows.Standard[0], 6);
        Assert.Equal(4.0 / 3.0, Video.AspectRatioRows.Standard[1], 6);
        Assert.Equal(21.0 / 9.0, Video.AspectRatioRows.Standard[2], 6);
        Assert.Equal(2.39, Video.AspectRatioRows.Standard[3], 6);
    }

    [Fact]
    public void Custom_at_4_3_checks_exactly_the_4_3_row()
    {
        const double current = 4.0 / 3.0;
        foreach (double r in Video.AspectRatioRows.Standard)
        {
            bool expected = System.Math.Abs(r - (4.0 / 3.0)) < 0.01;
            Assert.Equal(expected, Video.AspectRatioRows.IsChecked(Video.AspectPreference.Custom, current, r));
        }
    }

    [Theory]
    [InlineData(Video.AspectPreference.Fit)]
    [InlineData(Video.AspectPreference.Crop)]
    [InlineData(Video.AspectPreference.Stretch)]
    [InlineData(Video.AspectPreference.Native)]
    public void Any_non_custom_mode_checks_no_ratio_row(Video.AspectPreference mode)
    {
        // "current" carries whatever the last Custom ratio was, on purpose: the mode itself is what gates a ratio
        // row, not the stored number, so Fit/Crop/Stretch/Native must never light one up.
        foreach (double r in Video.AspectRatioRows.Standard)
            Assert.False(Video.AspectRatioRows.IsChecked(mode, r, r));
    }

    /// <summary>A persisted ratio a hair off 2.39 (float round-trip through settings storage) still lands on the
    /// 2.39 row — the same 0.01 tolerance <see cref="Video.AspectRatioRows.IsChecked"/> uses everywhere.</summary>
    [Fact]
    public void A_ratio_close_to_2_39_checks_the_2_39_row()
    {
        const double current = 2.3900001;
        Assert.True(Video.AspectRatioRows.IsChecked(Video.AspectPreference.Custom, current, 2.39));
        Assert.False(Video.AspectRatioRows.IsChecked(Video.AspectPreference.Custom, current, 16.0 / 9.0));
        Assert.False(Video.AspectRatioRows.IsChecked(Video.AspectPreference.Custom, current, 4.0 / 3.0));
        Assert.False(Video.AspectRatioRows.IsChecked(Video.AspectPreference.Custom, current, 21.0 / 9.0));
    }
}
