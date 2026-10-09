// ── Wavee.Tests/HomeUi/FacetBandTests.cs — Home's head geometry (Home/Facet.UI.cs `Facet`, Zones.UI.cs) ───────────
//
// Home's head is the standard TitleViews head: the lead (title + reserved meta slot), the 51-DIP band (views row + busy
// bar) and the gap under the bar add up to PageHeadRules.Extent(TitleViews), or Extent(Hoisted) when hoisted. Every number
// is a pure function of `hoisted`; none takes data, so the body's top edge cannot move when the words or a failure arrive.

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class FacetBandTests
{
    [Fact]
    public void The_views_row_is_the_page_views_bars_box()
        => Assert.Equal(PageGeometry.ViewsBarH, Facet.SelectorH);

    [Fact]
    public void The_band_is_the_views_row_plus_the_busy_bar()
        => Assert.Equal(PageGeometry.ViewsBarH + Facet.BarH, Facet.FacetRowH);

    [Fact]
    public void The_pinned_bands_lower_edge_is_its_inset_plus_its_height()
        => Assert.Equal(Facet.StuckInset + Facet.FacetRowH, Facet.StuckBottom);

    [Fact]
    public void The_body_top_is_the_title_views_extent_when_not_hoisted()
        => Assert.Equal(PageHeadRules.Extent(PageHeadKind.TitleViews),
            Facet.LeadFor(false) + Facet.FacetRowH + Facet.BelowBarGap);

    [Fact]
    public void The_body_top_is_the_hoisted_extent_when_hoisted()
        => Assert.Equal(PageHeadRules.Extent(PageHeadKind.Hoisted),
            Facet.LeadFor(true) + Facet.FacetRowH + Facet.BelowBarGap);

    [Fact]
    public void The_body_tops_are_164_and_72()
    {
        Assert.Equal(164f, Facet.LeadFor(false) + Facet.FacetRowH + Facet.BelowBarGap);
        Assert.Equal(72f, Facet.LeadFor(true) + Facet.FacetRowH + Facet.BelowBarGap);
    }

    [Fact]
    public void The_gap_under_the_busy_bar_is_never_negative()
        => Assert.True(Facet.BelowBarGap >= 0f);

    [Fact]
    public void Hoisted_chapter_headers_pin_where_the_band_would()
        => Assert.Equal(Facet.StuckInset, Facet.StuckBottomFor(true));

    [Fact]
    public void Unhoisted_chapter_headers_pin_under_the_band()
        => Assert.Equal(Facet.StuckBottom, Facet.StuckBottomFor(false));

    [Fact]
    public void The_content_clip_is_zero_when_nothing_is_pinned()
    {
        Assert.Equal(0f, Facet.ContentClipFor(true));
        Assert.Equal(Facet.StuckBottom, Facet.ContentClipFor(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_zone_bodys_clip_line_is_its_headers_stick_line_plus_the_header_and_its_gap(bool hoisted)
        => Assert.Equal(Facet.StuckBottomFor(hoisted) + Zones.HeaderH + Zones.HeaderGap, Zones.BodyClipInsetFor(hoisted));

    [Fact]
    public void The_zune_views_bar_fits_the_bands_second_row()
    {
        var st = Design.ZuneViewsStyle;
        Assert.Equal(ZuneNavRules.SubRowHeight, st.ItemHeight + 2f * PageGeometry.ViewsBarPadY);
        Assert.True(st.ShowPill);
        Assert.Equal(0f, st.LeadingInset);
    }
}
