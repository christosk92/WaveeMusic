// ── Wavee.Tests/PageHeadRulesTests.cs — the page head's height is a function of its ROUTE KIND ─────────────────────────
//
// Platform/Page.Rules.cs (PageHeadKind, PageHeadRules). Layout stability: a head is Extent(kind) tall, and the kind is
// decided by three route-static facts, so the body's top edge lands at the same y on every page of the same kind.

using Xunit;

namespace Wavee.Tests;

public sealed class PageHeadRulesTests
{
    [Theory]
    [InlineData(PageHeadKind.Title, 120f)]
    [InlineData(PageHeadKind.TitleViews, 164f)]
    [InlineData(PageHeadKind.CrumbTitle, 156f)]
    [InlineData(PageHeadKind.CrumbTitleViews, 200f)]
    [InlineData(PageHeadKind.Hoisted, 24f)]
    public void Extent_IsTheDocumentedHeight(PageHeadKind kind, float expected)
        => Assert.Equal(expected, PageHeadRules.Extent(kind));

    [Theory]
    [InlineData(PageHeadKind.Title, 100f)]
    [InlineData(PageHeadKind.TitleViews, 144f)]
    [InlineData(PageHeadKind.CrumbTitle, 136f)]
    [InlineData(PageHeadKind.CrumbTitleViews, 180f)]
    [InlineData(PageHeadKind.Hoisted, 24f)]
    public void Extent_WithoutAMetaLine_IsTwentyShorter(PageHeadKind kind, float expected)
    {
        Assert.Equal(expected, PageHeadRules.Extent(kind, hasMeta: false));
        Assert.Equal(PageHeadRules.Extent(kind, hasMeta: true) - (kind == PageHeadKind.Hoisted ? 0f : 20f), expected);
    }

    [Fact] public void Lead_WithoutAMetaLine_DropsTheReservedMetaBlock()
    {
        Assert.Equal(76f, PageHeadRules.Lead(PageHeadKind.Title, hasMeta: false));
        Assert.Equal(112f, PageHeadRules.Lead(PageHeadKind.CrumbTitleViews, hasMeta: false));
        Assert.Equal(PageGeometry.HeadTop, PageHeadRules.Lead(PageHeadKind.Hoisted, hasMeta: false));
        Assert.Equal(PageGeometry.TitleToMeta + PageGeometry.MetaLine, PageHeadRules.MetaBlock(true));
        Assert.Equal(0f, PageHeadRules.MetaBlock(false));
    }

    [Fact] public void Lead_IsTheHeightAboveTheViewsRow()
    {
        Assert.Equal(96f, PageHeadRules.Lead(PageHeadKind.Title));
        Assert.Equal(96f, PageHeadRules.Lead(PageHeadKind.TitleViews));
        Assert.Equal(132f, PageHeadRules.Lead(PageHeadKind.CrumbTitle));
        Assert.Equal(132f, PageHeadRules.Lead(PageHeadKind.CrumbTitleViews));
        Assert.Equal(PageGeometry.HeadTop, PageHeadRules.Lead(PageHeadKind.Hoisted));
    }

    [Fact] public void TitleBottom_ToViewsItemTop_Is32()
    {
        // title bottom = HeadTop + TitleLine; the views ITEM top = Lead + HeadToViewsGap + the bar's own top padding.
        float titleBottom = PageGeometry.HeadTop + PageGeometry.TitleLine;
        float itemTop = PageHeadRules.Lead(PageHeadKind.TitleViews) + PageGeometry.HeadToViewsGap + PageGeometry.ViewsBarPadY;
        Assert.Equal(32f, itemTop - titleBottom);
    }

    [Fact] public void Hoisted_IsTheHeadTopAndNothingElse()
    {
        Assert.Equal(PageGeometry.HeadTop, PageHeadRules.Extent(PageHeadKind.Hoisted));
        Assert.Equal(PageHeadRules.Lead(PageHeadKind.Hoisted), PageHeadRules.Extent(PageHeadKind.Hoisted));
    }

    [Fact] public void KindOf_HoistedWinsOverTheOtherFacts()
    {
        Assert.Equal(PageHeadKind.Hoisted, PageHeadRules.KindOf(true, true, true));
        Assert.Equal(PageHeadKind.Hoisted, PageHeadRules.KindOf(true, false, false));
    }

    [Fact] public void KindOf_NonHoisted_FollowsAboveAndViews()
    {
        Assert.Equal(PageHeadKind.Title, PageHeadRules.KindOf(false, false, false));
        Assert.Equal(PageHeadKind.TitleViews, PageHeadRules.KindOf(false, false, true));
        Assert.Equal(PageHeadKind.CrumbTitle, PageHeadRules.KindOf(false, true, false));
        Assert.Equal(PageHeadKind.CrumbTitleViews, PageHeadRules.KindOf(false, true, true));
    }

    [Theory]
    [InlineData("home")] [InlineData("browse")] [InlineData("recents")] [InlineData("liked")] [InlineData("albums")]
    [InlineData("artists")] [InlineData("podcasts")] [InlineData("audiobooks")]
    public void Hoisted_TrueForEveryPivotDestinationUnderZune(string route)
        => Assert.True(PageHeadRules.Hoisted(ShellNavStyle.Zune, route));

    [Theory]
    [InlineData("settings")] [InlineData("album:x")] [InlineData("browse:pop")] [InlineData("browse-section:x")]
    [InlineData("search")] [InlineData("local")] [InlineData("home-section:x")]
    public void Hoisted_FalseForEveryOtherRouteUnderZune(string route)
        => Assert.False(PageHeadRules.Hoisted(ShellNavStyle.Zune, route));

    [Theory]
    [InlineData(ShellNavStyle.Classic)] [InlineData(ShellNavStyle.Library)]
    public void Hoisted_NeverOutsideZune(ShellNavStyle style)
    {
        foreach (var route in new[] { "home", "browse", "recents", "liked", "albums", "artists", "podcasts", "audiobooks", "settings", "search" })
            Assert.False(PageHeadRules.Hoisted(style, route));
    }

    // ── views in the band (non-pivot pages whose row 2 carries their views) ──

    [Theory]
    [InlineData("settings")] [InlineData("people:0:spotify:user:abc")] [InlineData("disco:1:spotify:artist:abc")]
    public void ViewsInBand_TrueUnderZuneForThePagesWhoseRowTwoCarriesViews(string name)
        => Assert.True(PageHeadRules.ViewsInBand(ShellNavStyle.Zune, Shell.Parse(name)));

    [Theory]
    [InlineData(ShellNavStyle.Classic)] [InlineData(ShellNavStyle.Library)]
    public void ViewsInBand_NeverOutsideZune(ShellNavStyle style)
    {
        foreach (var name in new[] { "settings", "search", "people:0:spotify:user:abc", "disco:1:spotify:artist:abc", "home", "recents" })
            Assert.False(PageHeadRules.ViewsInBand(style, Shell.Parse(name)));
    }

    [Theory]
    [InlineData("home")] [InlineData("recents")] [InlineData("browse")] [InlineData("liked")]   // pivot destinations hoist whole instead
    [InlineData("history")] [InlineData("artist:spotify:artist:abc")] [InlineData("album:spotify:album:abc")]
    public void ViewsInBand_FalseForPivotDestinationsAndPagesWithoutViews(string name)
        => Assert.False(PageHeadRules.ViewsInBand(ShellNavStyle.Zune, Shell.Parse(name)));

    [Fact] public void ViewsInBand_SearchWithAQueryMovesItsFacetsToTheBandUnderZune()
    {
        var search = Shell.Parse("search".AsSpan(), "abc".AsSpan());
        Assert.True(PageHeadRules.ViewsInBand(ShellNavStyle.Zune, search));
        Assert.False(PageHeadRules.ViewsInBand(ShellNavStyle.Classic, search));
    }

    [Fact] public void TheHeadHeightFollowsOnlyThePresentedStyle_NeverData()
    {
        // Settings: views in the head (164) outside Zune, in the band (120) under it. The inputs are the style and the route.
        var settings = Shell.Parse("settings");
        foreach (var style in new[] { ShellNavStyle.Classic, ShellNavStyle.Library, ShellNavStyle.Zune })
        {
            bool inBand = PageHeadRules.ViewsInBand(style, settings);
            var kind = PageHeadRules.KindOf(PageHeadRules.Hoisted(style, "settings"), false, !inBand);
            Assert.Equal(inBand ? 120f : 164f, PageHeadRules.Extent(kind));
        }
        Assert.Equal(120f, PageHeadRules.Extent(PageHeadRules.KindOf(false, false, false)));
        Assert.Equal(164f, PageHeadRules.Extent(PageHeadRules.KindOf(false, false, true)));
        // A breadcrumb page (people lists, discography) loses the same 44 DIP.
        Assert.Equal(156f, PageHeadRules.Extent(PageHeadRules.KindOf(false, true, false)));
        Assert.Equal(200f, PageHeadRules.Extent(PageHeadRules.KindOf(false, true, true)));
        // Settings has no meta line at all (PageHeadSpec.HasMeta false): TitleViews 144 outside Zune, Title 100 inside it.
        Assert.Equal(144f, PageHeadRules.Extent(PageHeadRules.KindOf(false, false, true), hasMeta: false));
        Assert.Equal(100f, PageHeadRules.Extent(PageHeadRules.KindOf(false, false, false), hasMeta: false));
    }
}
