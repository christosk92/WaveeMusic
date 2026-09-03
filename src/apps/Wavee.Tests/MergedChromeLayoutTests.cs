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
            name: false, actionsInRow: false, forward: false, back: false, newTab: false, trailing: false);
        Assert.Equal(
            ShellResponsiveLayout.ChromeBarLeadW
            + ShellResponsiveLayout.ChromeThemeToggleW
            + 2f * ShellResponsiveLayout.ChromeGutterMinW
            + ShellResponsiveLayout.ChromeMinDragStripW
            + ShellResponsiveLayout.ChromeCaptionClusterW,
            essential);
    }

    [Fact]
    public void FixedBudget_ActionsInRowReservesFourNavButtonsNotZero()
    {
        float without = MergedChromeLayout.FixedBudget(
            name: false, actionsInRow: false, forward: false, back: false, newTab: false, trailing: true);
        float with = MergedChromeLayout.FixedBudget(
            name: false, actionsInRow: true, forward: false, back: false, newTab: false, trailing: true);
        Assert.Equal(0f, with - without - 4f * ShellResponsiveLayout.ChromeNavButtonW);

        var narrow = MergedChromeLayout.Resolve(ShellResponsiveLayout.ChromeActionsEnterW - 1f, 500f);
        var wide = MergedChromeLayout.Resolve(ShellResponsiveLayout.ChromeActionsEnterW, 500f);
        Assert.False(narrow.ActionsInRow);
        Assert.True(wide.ActionsInRow);
        Assert.Equal(narrow.FixedBudgetFor() + 4f * ShellResponsiveLayout.ChromeNavButtonW, wide.FixedBudgetFor());
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
            Assert.NotEqual(layout.ActionsInRow, layout.ActionsInMenu);
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

    // ── Issue #88: the search box must hold still while a tab's title changes width ────────────────────────────────

    /// <summary>The tabs island's reserved width must not track the tab strip's natural extent one-for-one: sweeping
    /// the "current tab title" width at a FIXED window width (no `previous`, so no hysteresis smoothing is in play)
    /// must only move <see cref="MergedChromeLayout.LeadClusterW"/> in whole <see
    /// cref="ShellResponsiveLayout.ChromeWidthQuantumW"/> steps — never by a fraction of one, and it must plateau
    /// once the comfortable extent exceeds what the row can spare, rather than keep growing with the title.</summary>
    [Fact]
    public void LeadClusterW_OnlyMovesAtQuantumBoundariesAcrossATabExtentSweep()
    {
        const float width = 1400f;
        float? previous = null;
        for (float extent = ShellResponsiveLayout.ChromeTabViewportMinW; extent <= 2000f; extent += 1f)
        {
            float current = MergedChromeLayout.Resolve(width, extent).LeadClusterW;
            if (previous is { } last && current != last)
                Assert.True(MathF.Abs(current - last) >= ShellResponsiveLayout.ChromeWidthQuantumW - 0.001f,
                    $"LeadClusterW moved by {current - last} at extent {extent}, smaller than one quantum.");
            previous = current;
        }
    }

    /// <summary>The centre-stability invariant itself: two DIFFERENT tab-title widths that both resolve to the same
    /// boolean stage (so the fixed budget and search allotment are identical) must reserve the SAME lead-cluster
    /// width once each is resolved fresh (no `previous` to smooth over anything) — a short "Pony" tab and a long
    /// "Rex Orange County" tab must not shove the centred search box.</summary>
    [Fact]
    public void LeadClusterW_IsUnaffectedByTitleWidthWithinTheSameComfortBand()
    {
        const float width = 1400f;
        // Both extents quantise (× 0.78, rounded up to 10) to the SAME ComfortableTabExtent, so — with no `previous`
        // to hold anything — the resolved LeadClusterW must be identical: the box no longer hugs the raw extent.
        var shortTitle = MergedChromeLayout.Resolve(width, 300f);
        var longTitle = MergedChromeLayout.Resolve(width, 305f);
        Assert.Equal(MergedChromeLayout.ComfortableTabExtent(300f), MergedChromeLayout.ComfortableTabExtent(305f));
        Assert.Equal(shortTitle.LeadClusterW, longTitle.LeadClusterW);
    }

    /// <summary>A GROWING extent (a longer title swapped in) must widen the reservation immediately — never delayed
    /// by the hysteresis reserve — so a longer tab title is never clipped for even one resolve.</summary>
    [Fact]
    public void LeadClusterW_WidensImmediatelyWhenTheExtentGrows()
    {
        const float width = 1400f;
        var narrow = MergedChromeLayout.Resolve(width, 300f);
        var wider = MergedChromeLayout.Resolve(width, 900f, narrow);
        Assert.True(wider.LeadClusterW > narrow.LeadClusterW);
    }

    /// <summary>A SHRINKING extent must NOT immediately give the space back — only once the drop clears the shared
    /// <see cref="ShellResponsiveLayout.ChromePromotionHysteresisW"/> reserve — so a tab briefly renaming through a
    /// shorter string and back does not read as the search box twitching.</summary>
    [Fact]
    public void LeadClusterW_HoldsThroughASmallShrinkThenReleasesPastTheHysteresis()
    {
        // A wide window keeps the reservation clamped by ComfortableTabExtent rather than by leftover row space, so
        // the numbers below isolate the extent → LeadClusterW relationship cleanly (comfort(900)=710, comfort(880)=
        // 690 — a 20-DIP drop, under the 40-DIP hysteresis reserve; comfort(300)=240 — a 470-DIP drop, well past it).
        const float width = 2000f;
        var wide = MergedChromeLayout.Resolve(width, 900f);
        float wideLead = wide.LeadClusterW;

        // A small drop (comfortably inside the hysteresis reserve) must hold at the previous width.
        var slightlyNarrower = MergedChromeLayout.Resolve(width, 880f, wide);
        Assert.Equal(wideLead, slightlyNarrower.LeadClusterW);

        // A large drop must eventually release, once it clears the hysteresis band.
        var muchNarrower = MergedChromeLayout.Resolve(width, 300f, wide);
        Assert.True(muchNarrower.LeadClusterW < wideLead);
    }
}
