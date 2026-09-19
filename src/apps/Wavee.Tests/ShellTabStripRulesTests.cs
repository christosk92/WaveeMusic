// ── Wavee.Tests/ShellTabStripRulesTests.cs — the tab strip's measurement feed and its menu gates ────────────────────
//
// Wave 4 stage B's gate for the tab-strip rules the merged chrome row reads (Shell/Shell.Chrome.cs): the strip's
// measured extent as the allocator consumes it, the upward seed a new tab applies before the strip re-measures, and the
// enabled-state of every row of the tab context menu.
//
//   THE SEED ONLY EVER RAISES. The strip's own measurement lowers the extent; a seed that could lower it would squeeze a
//   freshly-added tab behind a stale, smaller number for a frame.

using Xunit;

namespace Wavee.Tests;

public class ShellTabStripRulesTests
{
    [Fact]
    public void The_measured_extent_rounds_up_without_clipping_and_has_one_tab_floor()
    {
        Assert.Equal(Shell.Layout.ChromeTabMinW, Shell.Chrome.TabExtentFromMetrics(0f));
        Assert.Equal(340f, Shell.Chrome.TabExtentFromMetrics(334f));
        Assert.Equal(340f, Shell.Chrome.TabExtentFromMetrics(336f));
    }

    [Theory]
    [InlineData(1600f, 640f)]
    [InlineData(2400f, 1000f)]
    [InlineData(1900f, 334f)]
    public void Natural_tab_content_uses_available_room_without_the_old_fraction_or_cap(float width, float measured)
    {
        float extent = Shell.Chrome.TabExtentFromMetrics(measured);
        var chrome = Shell.Chrome.Resolve(width, extent);
        float available = width - chrome.FixedBudgetFor() - chrome.SearchWidth;
        Assert.True(available >= extent);
        Assert.True(chrome.LeadClusterW >= extent);
        Assert.True(chrome.LeadClusterW <= available);
        Assert.Equal(extent, chrome.LeadClusterW);
    }

    [Fact]
    public void The_add_button_has_its_own_width_beside_the_full_tab_viewport()
    {
        var chrome = Shell.Chrome.Resolve(1900f, 640f);
        float nav = (chrome.ShowBack ? Shell.Layout.ChromeNavButtonW : 0f)
            + (chrome.ShowForward ? Shell.Layout.ChromeNavButtonW : 0f);
        Assert.True(chrome.ShowNewTab);
        Assert.Equal(chrome.LeadClusterW + nav + Shell.Layout.ChromeAddSlotW, chrome.TabIslandWidth);
    }

    [Fact]
    public void Real_pressure_collapses_search_before_scrolling_tabs_and_preserves_fixed_chrome()
    {
        var chrome = Shell.Chrome.Resolve(900f, 900f);
        Assert.Equal(Shell.MergedSearchMode.Icon, chrome.SearchMode);
        Assert.True(chrome.LeadClusterW < 900f);
        Assert.InRange(chrome.FixedBudgetFor() + chrome.SearchWidth + chrome.LeadClusterW, 0f, 900f);
    }

    [Fact]
    public void The_seed_raises_to_the_estimate_and_never_lowers_a_measurement()
    {
        float estimate = Shell.Layout.ChromePinnedTabW + 2f * Shell.Layout.ChromeTabMinW;
        Assert.Equal(estimate, Shell.Chrome.SeedTabExtent(Shell.Layout.ChromeTabMinW, tabCount: 3, pinnedCount: 1));
        Assert.Equal(900f, Shell.Chrome.SeedTabExtent(900f, tabCount: 3, pinnedCount: 1));
    }

    [Fact]
    public void Neither_a_pinned_tab_nor_the_last_tab_shows_a_close_button()
    {
        var route = new Shell.Route(Shell.RouteKind.Home);
        Assert.False(Shell.TabWorkspace.IsClosable(new Shell.WorkspaceTab(1, route, Pinned: true), tabCount: 3));
        Assert.False(Shell.TabWorkspace.IsClosable(new Shell.WorkspaceTab(1, route, Pinned: false), tabCount: 1));
        Assert.True(Shell.TabWorkspace.IsClosable(new Shell.WorkspaceTab(1, route, Pinned: false), tabCount: 2));
    }

    [Fact]
    public void The_close_rows_are_enabled_by_what_they_would_close()
    {
        var ws = new Shell.TabWorkspace();                        // [home]
        int home = ws.Tabs[0].Id;
        Assert.Equal(0, ws.PinnedCount);
        Assert.True(ws.HasAnyUnpinned());
        Assert.False(ws.HasOtherUnpinned(home));
        Assert.False(ws.HasUnpinnedToRight(0));

        ws.Open(new Shell.Route(Shell.RouteKind.Browse));         // [home, browse]
        int browse = ws.Tabs[1].Id;
        ws.SetPinned(home, true);                                 // [home*, browse]
        Assert.Equal(1, ws.PinnedCount);
        Assert.True(ws.HasOtherUnpinned(home));
        Assert.False(ws.HasOtherUnpinned(browse));                // the only unpinned tab is the one asking
        Assert.True(ws.HasUnpinnedToRight(0));
        Assert.False(ws.HasUnpinnedToRight(1));

        ws.SetPinned(browse, true);                               // [home*, browse*]
        Assert.Equal(2, ws.PinnedCount);
        Assert.False(ws.HasAnyUnpinned());
    }
}

public class ShellPlayerBarArtSizeTests
{
    [Theory]
    [InlineData(Shell.PlayerBarTier.Minimal, 40f)]
    [InlineData(Shell.PlayerBarTier.Compact, 40f)]
    [InlineData(Shell.PlayerBarTier.Medium, Design.Size.ArtPlayerBar)]
    [InlineData(Shell.PlayerBarTier.Comfortable, Design.Size.ArtPlayerBar)]
    [InlineData(Shell.PlayerBarTier.Wide, Design.Size.ArtPlayerBar)]
    [InlineData(Shell.PlayerBarTier.Full, Design.Size.ArtPlayerBar)]
    public void The_dock_cover_is_48_from_medium_up_and_40_below(Shell.PlayerBarTier tier, float expected)
    {
        Assert.Equal(48f, Design.Size.ArtPlayerBar);   // ch 20 / 0.2.9 `WaveeSize.ArtPlayerBar`
        Assert.Equal(expected, Shell.PlayerBarLayout.ForTier(tier).ArtSize);
    }
}
