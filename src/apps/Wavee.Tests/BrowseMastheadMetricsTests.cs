using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Features.Browse;
using Wavee.Features.Detail;
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
        Assert.Equal(DetailVerticalLayout.StickyFadeBand, BrowseMastheadMetrics.ClipFadeBand);
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
}
