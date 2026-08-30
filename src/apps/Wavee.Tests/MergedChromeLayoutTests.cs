using System;
using Xunit;

namespace Wavee.Tests;

/// <summary>Locks the single-row chrome allocator. Tabs are a measured scrolling lane now, never a projected subset;
/// this suite therefore tests the search/tab trade, fixed-island shedding and the shared promotion reserve.</summary>
public class MergedChromeLayoutTests
{
    static float FirstWidthWhere(float tabExtent, Func<MergedChromeLayout, bool> predicate)
    {
        for (float width = 0f; width <= 4000f; width += 1f)
            if (predicate(MergedChromeLayout.Resolve(width, tabExtent))) return width;
        return -1f;
    }

    [Fact]
    public void FixedBudget_AlwaysReservesTheHeaderThemeToggle()
    {
        float essential = MergedChromeLayout.FixedBudget(
            name: false, friends: false, forward: false, back: false, newTab: false, trailing: false);
        Assert.Equal(
            ShellResponsiveLayout.ChromeBarLeadW
            + ShellResponsiveLayout.ChromeThemeToggleW
            + 2f * ShellResponsiveLayout.ChromeGutterMinW
            + ShellResponsiveLayout.ChromeMinDragStripW
            + ShellResponsiveLayout.ChromeCaptionClusterW,
            essential);
    }

    [Theory]
    [InlineData(0, 0, 110f)]
    [InlineData(1, 0, 110f)]
    [InlineData(4, 0, 440f)]
    [InlineData(4, 2, 300f)]
    [InlineData(4, 4, 160f)]
    public void EstimatedTabExtent_CountsPinnedTabsAtTheirCompactWidth(
        int tabCount, int pinnedCount, float expected)
        => Assert.Equal(expected, MergedChromeLayout.EstimatedTabExtent(tabCount, pinnedCount));

    [Theory]
    [InlineData(400f, 280f)]
    [InlineData(1000f, 280f)]
    [InlineData(1500f, 420f)]
    [InlineData(4000f, 420f)]
    public void PreferredSearchWidth_IsAggressiveQuantisedAndClamped(float width, float expected)
        => Assert.Equal(expected, MergedChromeLayout.PreferredSearchWidth(width));

    [Fact]
    public void MoreTabsCollapseSearchInsteadOfRemovingTabs()
    {
        const float width = 1000f;
        var twoTabs = MergedChromeLayout.Resolve(width, MergedChromeLayout.EstimatedTabExtent(2));
        var twelveTabs = MergedChromeLayout.Resolve(width, MergedChromeLayout.EstimatedTabExtent(12));

        Assert.Equal(MergedSearchMode.Field, twoTabs.SearchMode);
        Assert.Equal(MergedSearchMode.Icon, twelveTabs.SearchMode);
        Assert.Equal(ShellResponsiveLayout.ChromeSearchIconW, twelveTabs.SearchWidth);
    }

    [Fact]
    public void PinnedTabsCanBuyBackTheSearchField()
    {
        const float width = 1000f;
        var regular = MergedChromeLayout.Resolve(width, MergedChromeLayout.EstimatedTabExtent(6));
        var pinned = MergedChromeLayout.Resolve(width, MergedChromeLayout.EstimatedTabExtent(6, 6));

        Assert.Equal(MergedSearchMode.Icon, regular.SearchMode);
        Assert.Equal(MergedSearchMode.Field, pinned.SearchMode);
    }

    [Fact]
    public void FieldBoundaryUsesMeasuredNaturalExtentRatherThanTabCount()
    {
        const float shortLabels = 360f;
        const float longLabels = 900f;
        float shortBoundary = FirstWidthWhere(shortLabels, x => x.SearchMode == MergedSearchMode.Field);
        float longBoundary = FirstWidthWhere(longLabels, x => x.SearchMode == MergedSearchMode.Field);

        Assert.True(shortBoundary > 0f);
        Assert.True(longBoundary > shortBoundary);
    }

    [Fact]
    public void SearchPromotionWaitsForTheSharedReserve()
    {
        const float extent = 660f;
        float boundary = FirstWidthWhere(extent, x => x.SearchMode == MergedSearchMode.Field);
        Assert.True(boundary > 0f);

        var icon = MergedChromeLayout.Resolve(boundary - 1f, extent);
        Assert.Equal(MergedSearchMode.Icon, icon.SearchMode);
        Assert.Equal(MergedSearchMode.Icon,
            MergedChromeLayout.Resolve(boundary + ShellResponsiveLayout.ChromePromotionHysteresisW - 1f,
                extent, icon).SearchMode);

        float promotedAt = -1f;
        for (float width = boundary + ShellResponsiveLayout.ChromePromotionHysteresisW; width <= 4000f; width += 1f)
        {
            if (MergedChromeLayout.Resolve(width, extent, icon).SearchMode != MergedSearchMode.Field) continue;
            promotedAt = width;
            break;
        }
        Assert.True(promotedAt >= boundary + ShellResponsiveLayout.ChromePromotionHysteresisW);
        Assert.Equal(MergedSearchMode.Field,
            MergedChromeLayout.Resolve(promotedAt, extent, icon).SearchMode);
    }

    [Fact]
    public void SearchDemotionIsImmediate()
    {
        const float extent = 660f;
        float boundary = FirstWidthWhere(extent, x => x.SearchMode == MergedSearchMode.Field);
        var field = MergedChromeLayout.Resolve(boundary + 100f, extent);

        Assert.Equal(MergedSearchMode.Icon,
            MergedChromeLayout.Resolve(boundary - 1f, extent, field).SearchMode);
    }

    [Fact]
    public void ExtremePressureShedsFixedIslandsBeforeTheTabViewport()
    {
        float extent = MergedChromeLayout.EstimatedTabExtent(8);
        var roomy = MergedChromeLayout.Resolve(900f, extent);
        var narrow = MergedChromeLayout.Resolve(260f, extent);

        Assert.True(roomy.ShowBack);
        Assert.True(roomy.ShowNewTab);
        Assert.True(roomy.ShowTrailing);
        Assert.False(narrow.ShowBack);
        Assert.False(narrow.ShowNewTab);
        Assert.False(narrow.ShowTrailing);
        Assert.Equal(MergedSearchMode.Icon, narrow.SearchMode);
    }

    [Fact]
    public void IdentityAffordancesMoveRatherThanVanish()
    {
        for (float width = 300f; width <= 2400f; width += 7f)
        {
            var layout = MergedChromeLayout.Resolve(width, 500f);
            Assert.NotEqual(layout.FriendsInRow, layout.FriendsInMenu);
        }
    }

    [Fact]
    public void SearchWidthNeverChangesInsideIconMode()
    {
        for (float width = 300f; width <= 1800f; width += 3f)
        {
            var layout = MergedChromeLayout.Resolve(width, 1200f);
            if (layout.SearchMode == MergedSearchMode.Icon)
                Assert.Equal(ShellResponsiveLayout.ChromeSearchIconW, layout.SearchWidth);
        }
    }

    [Fact]
    public void ComfortableTabExtent_IsQuantisedAndBounded()
    {
        Assert.Equal(ShellResponsiveLayout.ChromeTabComfortMinW,
            MergedChromeLayout.ComfortableTabExtent(40f));
        Assert.Equal(520f, MergedChromeLayout.ComfortableTabExtent(660f));
        Assert.Equal(ShellResponsiveLayout.ChromeTabComfortMaxW,
            MergedChromeLayout.ComfortableTabExtent(4000f));
    }
}
