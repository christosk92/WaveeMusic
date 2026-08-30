using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Features.Browse;
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
}
