// ── Wavee.Tests/BrowseMastheadMetricsTests.cs — the overlay masthead's constant reserve (ch 13 §8) ─────────────────
//
// Ported verbatim from 0.2.9 `BrowseMastheadMetricsTests`. `DetailVerticalLayout.StickyFadeBand` is
// `Detail.VerticalLayout.StickyFadeBand` in 0.3; everything else keeps its name.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class BrowseMastheadMetricsTests
{
    [Fact]
    public void Reserve_IsFrameTopPlusTitleLargeLine()
    {
        Assert.Equal(52f, Ui.TitleLarge("x").LineHeight);
        Assert.Equal(BrowseMastheadMetrics.TitleLine, Ui.TitleLarge("x").LineHeight);
        Assert.Equal(BrowseMastheadMetrics.Reserve, Spacing.XXXL + Ui.TitleLarge("x").LineHeight);
        // 0.3: the masthead's title IS the SurfaceDisplay face, which keeps the TitleLarge line.
        Assert.Equal(BrowseMastheadMetrics.TitleLine, Design.Type.SurfaceDisplay("x").LineHeight);
    }

    [Fact]
    public void FamilyBodyPad_ClearsTheOverlayThenTheOldBandGap()
    {
        Assert.Equal(BrowseMastheadMetrics.Reserve + Spacing.L, BrowseMastheadMetrics.BodyTop);
        var pad = BrowseMastheadMetrics.FamilyBodyPad(Spacing.L);
        Assert.Equal(Spacing.PageWide, pad.Left);
        Assert.Equal(BrowseMastheadMetrics.BodyTop, pad.Top);
        Assert.Equal(Spacing.PageWide, pad.Right);
        Assert.Equal(Spacing.L, pad.Bottom);
    }

    // Finding #3: the band paints nothing, so a scrolling family page cuts its content at the band's lower edge —
    // the reserve itself, never a second number — and feathers that cut with the one fade band every surface uses.
    [Fact]
    public void ClipInset_IsTheReserve_AndTheFadeIsTheSharedStickyBand()
    {
        Assert.Equal(BrowseMastheadMetrics.Reserve, BrowseMastheadMetrics.ClipInset);
        Assert.Equal(Detail.VerticalLayout.StickyFadeBand, BrowseMastheadMetrics.ClipFadeBand);
        Assert.True(BrowseMastheadMetrics.ClipFadeBand > 0f);
    }

    // The under-band pad drops the top: the reserve becomes a spacer ABOVE the clipped node, so the spacer + pad
    // together still clear exactly what FamilyBodyPad cleared.
    [Fact]
    public void FamilyUnderBandPad_HasNoTop_SoTheSpacerCarriesTheReserve()
    {
        var pad = BrowseMastheadMetrics.FamilyUnderBandPad(Spacing.XXL);
        Assert.Equal(0f, pad.Top);
        Assert.Equal(Spacing.PageWide, pad.Left);
        Assert.Equal(Spacing.PageWide, pad.Right);
        Assert.Equal(Spacing.XXL, pad.Bottom);
        Assert.Equal(BrowseMastheadMetrics.FamilyBodyPad(Spacing.XXL).Top, BrowseMastheadMetrics.BodyTop + pad.Top);
    }

    [Fact]
    public void TheContractNumbers_Reserve84_BodyTop100_ClipInset84_FadeBand24()
    {
        // WP-5.P contract §4 pins these as numbers: a token re-point that moved them would re-pad every parked page.
        Assert.Equal(84f, BrowseMastheadMetrics.Reserve);
        Assert.Equal(100f, BrowseMastheadMetrics.BodyTop);
        Assert.Equal(84f, BrowseMastheadMetrics.ClipInset);
        Assert.Equal(24f, BrowseMastheadMetrics.ClipFadeBand);
        Assert.Equal(BrowseMastheadMetrics.Reserve, BrowseLayout.MastheadReserve);
    }
}
