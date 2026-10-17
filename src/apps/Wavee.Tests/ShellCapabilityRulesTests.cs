// ── Wavee.Tests/ShellCapabilityRulesTests.cs — the shell capabilities the core honours, as pure rules (P5 ZK) ──────────────────
//
// `ShellCapabilities` (Theming\Policies\ThemePolicies.cs) says what the shell offers; Classic offers everything and every rule below is
// a no-op for it. The facts:
//
//   NO FORWARD BUTTON: THE BUDGET NEVER ADMITS IT. The row neither draws Forward nor pays for it, at any width; Classic's budget is the
//   same instance it always was.
//
//   NO TABS: NO TAB ISLAND AT ALL. No strip, no count button, no "+" slot, nothing priced, so the search gets the room; the hysteresis
//   cannot bring the island back.
//
//   NO TABS: THE WORKSPACE COLLAPSES AND COMES BACK WHOLE (owner Q4). One tab while collapsed, the others parked with their ids, order
//   and pins; "open in a new tab" navigates in place; close, close-others and pin are refused; the persisted pins are the WHOLE
//   workspace; expanding puts every tab back around the active one, which keeps the route it went to.
//
//   NO NAV TREE: THE PANE IS NEVER PRESENTED, AND THE PALETTE HIDES ITS ROWS. Edit sidebar and New folder go; tab rows (`tabs.*`) go
//   without tabs; every other row stays.

using Wavee.Theming;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ShellCapabilityRulesTests
{
    static readonly ShellCapabilities Zune = new(Tabs: false, NavTree: false, FriendsPanel: true, ForwardButton: false, HistoryMenu: true, MaxBandPins: 6);

    // ── the chrome budget ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_keeps_its_budget_instance()
    {
        var b = ChromeBudget.Default;
        Assert.Same(b, ShellCaps.Budget(b, ShellCapabilities.Unrestricted));
    }

    [Theory]
    [InlineData(640f)]
    [InlineData(1180f)]
    [InlineData(1440f)]
    [InlineData(2560f)]
    public void Without_a_forward_button_the_row_never_seats_or_prices_forward(float width)
    {
        var b = ChromeBudget.Default;
        var noForward = ShellCaps.Budget(b, ShellCapabilities.Unrestricted with { ForwardButton = false });

        var classic = Shell.Chrome.Resolve(width, 1, null, budget: b);
        var without = Shell.Chrome.Resolve(width, 1, null, budget: noForward);
        var held = Shell.Chrome.Resolve(width, 1, without, budget: noForward);   // the hysteresis cannot bring it back

        Assert.True(classic.ShowForward, "Classic seats Forward above 520");
        Assert.False(without.ShowForward);
        Assert.False(held.ShowForward);
        Assert.Equal(b.NavButtonW, classic.FixedBudgetFor(b) - without.FixedBudgetFor(noForward), 0.01f);
        Assert.True(without.SearchWidth >= classic.SearchWidth, "the room Forward left goes to the row");
    }

    [Theory]
    [InlineData(400f)]
    [InlineData(1180f)]
    [InlineData(1920f)]
    public void Without_tabs_the_row_has_no_tab_island_and_prices_none(float width)
    {
        var b = ChromeBudget.Default;
        var classic = Shell.Chrome.Resolve(width, 340f, null, budget: b);
        var without = Shell.Chrome.Resolve(width, 340f, null, budget: b, tabs: false);
        var held = Shell.Chrome.Resolve(width, 340f, without, budget: b, tabs: false);

        foreach (var c in new[] { without, held })
        {
            Assert.True(c.TabCount);
            Assert.True(c.TabsHidden, "no count button");
            Assert.False(c.ShowNewTab, "no \"+\" slot");
            Assert.Equal(0f, c.LeadClusterW);
            Assert.Equal((c.ShowBack ? b.NavButtonW : 0f) + (c.ShowForward ? b.NavButtonW : 0f), c.TabIslandWidth, 0.01f);
        }
        Assert.True(without.SearchWidth >= classic.SearchWidth, "the tabs' room goes to the search");
        // Classic's row is unchanged by the new parameter's default.
        Assert.Equal(classic, Shell.Chrome.Resolve(width, 340f, null, budget: b, tabs: true));
    }

    // ── the workspace (owner Q4) ───────────────────────────────────────────────────────────────────────────────────────────

    static Shell.TabWorkspace ThreeTabs(out int home, out int browse, out int recents)
    {
        var w = new Shell.TabWorkspace();
        home = w.ActiveId;
        w.Open(new Shell.Route(Shell.RouteKind.Browse));
        browse = w.ActiveId;
        w.Open(new Shell.Route(Shell.RouteKind.Recents));
        recents = w.ActiveId;
        w.SetPinned(home, true);
        w.ActivateById(browse);
        return w;
    }

    [Fact]
    public void Collapsing_keeps_only_the_active_tab_and_parks_the_rest()
    {
        var w = ThreeTabs(out int home, out int browse, out int recents);
        var before = w.Tabs.Select(t => t.Id).ToArray();

        Assert.True(w.Collapse());
        Assert.False(w.Collapse(), "a second collapse is a no-op");
        Assert.True(w.Collapsed);
        Assert.Equal(browse, Assert.Single(w.Tabs).Id);
        Assert.Equal(browse, w.ActiveId);
        Assert.Equal(2, w.ParkedCount);
        Assert.Equal(new[] { home, browse, recents }, before);
    }

    [Fact]
    public void While_collapsed_a_new_tab_opens_in_place_and_close_and_pin_are_refused()
    {
        var w = ThreeTabs(out _, out int browse, out _);
        w.Collapse();
        int revision = w.PinnedRevision;

        var open = w.Open(new Shell.Route(Shell.RouteKind.History));
        Assert.Equal(Shell.TabNavIntent.Push, open.Intent);
        Assert.Equal(Shell.RouteKind.History, open.Route!.Value.Kind);
        Assert.Single(w.Tabs);
        Assert.Equal(browse, w.ActiveId);

        Assert.Equal(Shell.TabNavIntent.None, w.Close(0).Intent);
        Assert.Equal(Shell.TabNavIntent.None, w.CloseWhere(static _ => true).Intent);
        Assert.Equal(Shell.TabNavIntent.None, w.SetPinned(browse, true).Intent);
        Assert.Single(w.Tabs);
        Assert.False(w.Active.Pinned);
        Assert.Equal(revision, w.PinnedRevision);
    }

    [Fact]
    public void The_persisted_pins_are_the_whole_workspace_while_collapsed()
    {
        var w = ThreeTabs(out int home, out _, out _);
        var before = w.PinnedSnapshot();
        int pinnedIndexBefore = w.ActivePinnedIndex;

        w.Collapse();
        var during = w.PinnedSnapshot();

        Assert.Equal(before.Tabs.Length, during.Tabs.Length);
        Assert.Equal(before.Tabs.Select(t => t.Route).ToArray(), during.Tabs.Select(t => t.Route).ToArray());
        Assert.Equal(before.LastSelected, during.LastSelected);
        Assert.Equal(pinnedIndexBefore, w.ActivePinnedIndex);
        Assert.Single(during.Tabs);   // the one pin: Home, parked
        Assert.Equal(Shell.NameOf(new Shell.Route(Shell.RouteKind.Home)), during.Tabs[0].Route);
        _ = home;
    }

    [Fact]
    public void Expanding_puts_every_tab_back_in_order_around_the_active_one_which_keeps_its_new_route()
    {
        var w = ThreeTabs(out int home, out int browse, out int recents);
        w.Collapse();
        w.SetActiveRoute(new Shell.Route(Shell.RouteKind.History, Tab: browse));

        Assert.True(w.Expand());
        Assert.False(w.Expand(), "a second expand is a no-op");
        Assert.False(w.Collapsed);
        Assert.Equal(new[] { home, browse, recents }, w.Tabs.Select(t => t.Id).ToArray());
        Assert.Equal(browse, w.ActiveId);
        Assert.Equal(Shell.RouteKind.History, w.Active.Route.Kind);
        Assert.Equal(Shell.RouteKind.Recents, w.Tabs[2].Route.Kind);
        Assert.True(w.Tabs[0].Pinned);
        Assert.Equal(0, w.ParkedCount);

        // The ops work again.
        Assert.Equal(Shell.TabNavIntent.Restore, w.Activate(2).Intent);
    }

    [Fact]
    public void A_cold_restore_replaces_a_collapsed_workspace()
    {
        var w = ThreeTabs(out _, out _, out _);
        w.Collapse();
        w.RestorePinned(new Shell.WorkspaceTabsSnapshot([new Shell.PersistedTab("home", null)], 0));
        Assert.False(w.Collapsed);
        Assert.Single(w.Tabs);
        Assert.True(w.Tabs[0].Pinned);
    }

    // ── the nav tree ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_nav_tree_the_pane_is_never_presented()
    {
        var expanded = new NavPaneState(SidebarWindowBand.Wide, SidebarPaneMode.Expanded, 300f, 0.5f);
        Assert.Equal(expanded, FrameModel.Gate(in expanded, navTree: true));
        var gated = FrameModel.Gate(in expanded, navTree: false);
        Assert.Equal(SidebarPaneMode.Minimal, gated.Mode);
        Assert.Equal(0f, gated.PresentedWidth);
        Assert.Equal(1f, gated.Fade);
        Assert.Equal(SidebarWindowBand.Wide, gated.Band);
    }

    // ── the palette ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_palette_hides_the_nav_tree_rows_without_a_tree_and_keeps_every_other_row()
    {
        var all = Shell.CommandTable.BuildIndex(null, null, caps: ShellCapabilities.Unrestricted).Select(e => e.Id).ToArray();
        var zune = Shell.CommandTable.BuildIndex(null, null, caps: Zune).Select(e => e.Id).ToArray();
        var none = Shell.CommandTable.BuildIndex(null, null).Select(e => e.Id).ToArray();

        Assert.Equal(none, all);   // Classic: unchanged
        Assert.Contains("sidebar.edit", all);
        Assert.Contains("library.newFolder", all);
        Assert.DoesNotContain("sidebar.edit", zune);
        Assert.DoesNotContain("library.newFolder", zune);
        Assert.Equal(all.Where(id => id is not ("sidebar.edit" or "library.newFolder")).ToArray(), zune);
    }

    [Fact]
    public void Tab_rows_hide_without_tabs_and_nav_tree_rows_without_a_tree()
    {
        var noTabs = ShellCapabilities.Unrestricted with { Tabs = false };
        var noTree = ShellCapabilities.Unrestricted with { NavTree = false };

        Assert.True(Shell.CommandTable.HiddenBy("tabs.new", noTabs));
        Assert.False(Shell.CommandTable.HiddenBy("tabs.new", noTree));
        Assert.True(Shell.CommandTable.HiddenBy("sidebar.edit", noTree));
        Assert.False(Shell.CommandTable.HiddenBy("sidebar.edit", noTabs));
        Assert.False(Shell.CommandTable.HiddenBy("nav.home", Zune));
        Assert.False(Shell.CommandTable.HiddenBy("sidebar.edit", ShellCapabilities.Unrestricted));
    }

    // ── the band's pin cap ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_pin_cap_is_the_capability_and_never_negative()
    {
        Assert.Equal(6, ShellCaps.PinCap(Zune));
        Assert.Equal(int.MaxValue, ShellCaps.PinCap(ShellCapabilities.Unrestricted));
        Assert.Equal(0, ShellCaps.PinCap(Zune with { MaxBandPins = -3 }));
    }
}
