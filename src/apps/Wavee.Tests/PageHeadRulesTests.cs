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
    [InlineData(PageHeadKind.Hoisted, 72f)]
    public void Extent_IsTheDocumentedHeight(PageHeadKind kind, float expected)
        => Assert.Equal(expected, PageHeadRules.Extent(kind));

    [Fact] public void Lead_IsTheHeightAboveTheViewsRow()
    {
        Assert.Equal(96f, PageHeadRules.Lead(PageHeadKind.Title));
        Assert.Equal(96f, PageHeadRules.Lead(PageHeadKind.TitleViews));
        Assert.Equal(132f, PageHeadRules.Lead(PageHeadKind.CrumbTitle));
        Assert.Equal(132f, PageHeadRules.Lead(PageHeadKind.CrumbTitleViews));
        Assert.Equal(12f, PageHeadRules.Lead(PageHeadKind.Hoisted));
    }

    [Fact] public void TitleBottom_ToViewsItemTop_Is32()
    {
        // title bottom = HeadTop + TitleLine; the views ITEM top = Lead + HeadToViewsGap + the bar's own top padding.
        float titleBottom = PageGeometry.HeadTop + PageGeometry.TitleLine;
        float itemTop = PageHeadRules.Lead(PageHeadKind.TitleViews) + PageGeometry.HeadToViewsGap + PageGeometry.ViewsBarPadY;
        Assert.Equal(32f, itemTop - titleBottom);
    }

    [Fact] public void Hoisted_IsTheTopAirTheBarAndTheBodyGap()
        => Assert.Equal(PageGeometry.HoistedTop + PageGeometry.ViewsBarH + PageGeometry.ViewsToBodyGap, PageHeadRules.Extent(PageHeadKind.Hoisted));

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
}
