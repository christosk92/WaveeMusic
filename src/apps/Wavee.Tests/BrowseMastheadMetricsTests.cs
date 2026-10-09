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
    public void Reserve_IsHeadTopPlusTitleLine()
    {
        Assert.Equal(52f, Ui.TitleLarge("x").LineHeight);
        Assert.Equal(BrowseMastheadMetrics.TitleLine, Ui.TitleLarge("x").LineHeight);
        Assert.Equal(PageGeometry.TitleLine, BrowseMastheadMetrics.TitleLine);
        Assert.Equal(BrowseMastheadMetrics.Reserve, PageGeometry.HeadTop + Ui.TitleLarge("x").LineHeight);
        Assert.Equal(76f, BrowseMastheadMetrics.Reserve);
        // 0.3: the masthead's title IS the SurfaceDisplay face, which keeps the TitleLarge line (PageTitle holds it to one line).
        Assert.Equal(BrowseMastheadMetrics.TitleLine, Design.Type.SurfaceDisplay("x").LineHeight);
        Assert.Equal(BrowseMastheadMetrics.TitleLine, Design.Type.PageTitle("x").LineHeight);
    }

    // The family's body starts where a Title-kind PageHead's body starts: the shared 120, so a masthead page and a title
    // page land their first row at the same y (it was 100, Reserve + Spacing.L, before the shared geometry).
    [Fact]
    public void BodyTop_IsTheTitleHeadExtent()
    {
        Assert.Equal(PageHeadRules.Extent(PageHeadKind.Title), BrowseMastheadMetrics.BodyTop);
        Assert.Equal(120f, BrowseMastheadMetrics.BodyTop);
    }

    [Fact]
    public void FamilyBodyPad_CarriesThePassedGutterOnBothSides_UnderTheBodyTop()
    {
        var pad = BrowseMastheadMetrics.FamilyBodyPad(36f, 16f);
        Assert.Equal(36f, pad.Left);
        Assert.Equal(BrowseMastheadMetrics.BodyTop, pad.Top);
        Assert.Equal(36f, pad.Right);
        Assert.Equal(16f, pad.Bottom);
        Assert.Equal(16f, BrowseMastheadMetrics.FamilyBodyPad(PageGeometry.GutterNarrow, 0f).Left);
    }

    // Finding #3: the band paints nothing, so a scrolling family page cuts its content at the band's lower edge —
    // the reserve itself, never a second number — and feathers that cut with the one fade band every surface uses.
    [Fact]
    public void ClipInset_IsTheReserve_AndTheFadeIsTheSharedStickyBand()
    {
        Assert.Equal(BrowseMastheadMetrics.Reserve, BrowseMastheadMetrics.ClipInset);
        Assert.True(BrowseMastheadMetrics.ClipInset <= BrowseMastheadMetrics.BodyTop);
        Assert.Equal(Detail.VerticalLayout.StickyFadeBand, BrowseMastheadMetrics.ClipFadeBand);
        Assert.True(BrowseMastheadMetrics.ClipFadeBand > 0f);
    }

    // The under-band pad drops the top: the reserve becomes a spacer ABOVE the clipped node, so the spacer + pad
    // together still clear exactly what FamilyBodyPad cleared.
    [Fact]
    public void FamilyUnderBandPad_HasNoTop_SoTheSpacerCarriesTheReserve()
    {
        var pad = BrowseMastheadMetrics.FamilyUnderBandPad(32f, 24f);
        Assert.Equal(0f, pad.Top);
        Assert.Equal(32f, pad.Left);
        Assert.Equal(32f, pad.Right);
        Assert.Equal(24f, pad.Bottom);
        Assert.Equal(BrowseMastheadMetrics.FamilyBodyPad(32f, 24f).Top, BrowseMastheadMetrics.BodyTop + pad.Top);
    }

    // The browse root under the Zune band hoists to the fixed 72-DIP strip; the clip is off because the band paints nothing.
    [Fact]
    public void Hoisted_BodyTopIsTheFixedStrip_AndTheClipIsOff()
    {
        Assert.Equal(PageHeadRules.Extent(PageHeadKind.Hoisted), BrowseMastheadMetrics.BodyTopFor(true));
        Assert.Equal(72f, BrowseMastheadMetrics.BodyTopFor(true));
        Assert.Equal(BrowseMastheadMetrics.BodyTop, BrowseMastheadMetrics.BodyTopFor(false));
        Assert.Equal(0f, BrowseMastheadMetrics.ClipInsetFor(true));
        Assert.Equal(BrowseMastheadMetrics.ClipInset, BrowseMastheadMetrics.ClipInsetFor(false));
    }

    [Fact]
    public void TheContractNumbers_Reserve76_BodyTop120_ClipInset76_FadeBand24()
    {
        // A token re-point that moved these would re-pad every parked page.
        Assert.Equal(76f, BrowseMastheadMetrics.Reserve);
        Assert.Equal(120f, BrowseMastheadMetrics.BodyTop);
        Assert.Equal(76f, BrowseMastheadMetrics.ClipInset);
        Assert.Equal(24f, BrowseMastheadMetrics.ClipFadeBand);
    }
}
