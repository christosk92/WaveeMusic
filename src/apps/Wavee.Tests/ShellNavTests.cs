// ── Wavee.Tests/ShellNavTests.cs — the nav reducer, the chrome allocator, the tabs, and the shell's other rules ────
//
// Wave 4's gate for the rest of `Shell/Shell.cs` + `Shell/Shell.Chrome.cs`. Ported from _old/Wavee.Tests's
// {ShellNavTests, DrillTrailTests, ContentHostPageTransitionTests, ShellResponsiveLayoutTests, MergedChromeLayoutTests,
// TabWorkspaceTests, PlayerBarResponsiveLayoutTests, DevicePickerItemsTests, WaveeTipsCoreTests}.cs (G6), plus the
// four decisions ch 16 and ch 18 record as untested in 0.2.9: the history log's most-visited fold, its filter/search,
// the tint hand-over and the auth fold.
//
// The rules each of these exists to stop being "simplified" away:
//
//   THE CHROME ROW IS ARITHMETIC. Replace `MergedChromeLayout` with an `if (width < X)` ladder and every threshold
//   that remains stops being a cost input to a budget and starts being a guess.
//
//   THE TAB LANE IS A RESERVATION WITH THE OPPOSITE HYSTERESIS POLARITY. A hug makes the centred search box jump by
//   half a title's length whenever a tab title changes (#88).
//
//   A TAB SWITCH IS NOT A GO. No back-stack push (Back must still LEAVE the page, not undo the switch), no origin
//   write (the arrival's crumb stands), no history record.
//
//   MOST-VISITED IS A *STABLE* SORT. The word "stable" in 0.2.9's comment IS the whole rule, and `List.Sort` is not.

using FluentGpu.Localization;

using Xunit;

namespace Wavee.Tests;

// Shares the ENTITIES collection: `Shell.Parse(uri, name)` interns the label through `Entities.Strings`, and a
// `TestScope.Fresh()` running in parallel resets that interner mid-test — the label id then resolves to nothing
// and a trail silently loses a crumb. The failure only shows in a full run, never in isolation.
[Collection(EntitiesCollection.Name)]
public class ShellNavReducerTests
{
    static Shell.Nav Fresh() => new(new Shell.Route(Shell.RouteKind.Home));

    [Fact]
    public void Go_pushes_back_and_clears_forward()
    {
        var n = Fresh();
        Assert.True(Shell.Go(ref n, Shell.Parse("search", "pop")));
        Assert.True(Shell.Go(ref n, Shell.Parse("browse")));
        Assert.True(Shell.BackStep(ref n));
        Assert.True(n.CanForward);

        Assert.True(Shell.Go(ref n, Shell.Parse("settings")));
        Assert.False(n.CanForward);   // a new destination discards the forward branch
    }

    [Fact]
    public void Go_to_the_showing_route_is_a_no_op()
    {
        var n = Fresh();
        Assert.False(Shell.Go(ref n, new Shell.Route(Shell.RouteKind.Home)));
        Assert.Empty(n.Back);
    }

    [Fact]
    public void Back_and_forward_walk_the_same_path()
    {
        var n = Fresh();
        Shell.Go(ref n, Shell.Parse("browse"));
        Shell.Go(ref n, Shell.Parse("settings"));
        Assert.True(Shell.BackStep(ref n));
        Assert.Equal(Shell.RouteKind.Browse, n.Current.Kind);
        Assert.True(Shell.BackStep(ref n));
        Assert.Equal(Shell.RouteKind.Home, n.Current.Kind);
        Assert.False(Shell.BackStep(ref n));

        Assert.True(Shell.ForwardStep(ref n));
        Assert.Equal(Shell.RouteKind.Browse, n.Current.Kind);
        Assert.True(Shell.ForwardStep(ref n));
        Assert.Equal(Shell.RouteKind.Settings, n.Current.Kind);
        Assert.False(Shell.ForwardStep(ref n));
    }

    [Fact]
    public void Both_stacks_are_bounded()
    {
        var n = Fresh();
        for (int i = 0; i < Shell.MaxBackStack + 50; i++) Shell.Go(ref n, Shell.Parse("search", "q" + i));
        Assert.Equal(Shell.MaxBackStack, n.Back.Count);
    }

    [Fact]
    public void Restore_is_not_a_go()
    {
        var n = Fresh();
        Shell.Go(ref n, Shell.Parse("browse"));
        int backBefore = n.Back.Count;
        Shell.Restore(ref n, Shell.Parse("settings"));
        Assert.Equal(Shell.RouteKind.Settings, n.Current.Kind);
        Assert.Equal(backBefore, n.Back.Count);   // a tab switch is not a new place
    }
}

// Shares the ENTITIES collection: `Shell.Parse(uri, name)` interns the label through `Entities.Strings`, and a
// `TestScope.Fresh()` running in parallel resets that interner mid-test — the label id then resolves to nothing
// and a trail silently loses a crumb. The failure only shows in a full run, never in isolation.
[Collection(EntitiesCollection.Name)]
public class ShellTrailTests
{
    static Shell.Route Section(string uri, string name) => Shell.Parse("home-section:" + uri, name);
    static Shell.Route BrowseSection(string uri, string name) => Shell.Parse("browse-section:" + uri, name);

    [Fact]
    public void No_label_means_an_empty_trail()
        => Assert.Empty(Shell.Trail(Shell.Parse("home-section:spotify:section:abc"), default));

    [Fact]
    public void A_home_section_drills_to_home()
    {
        var trail = Shell.Trail(Section("spotify:section:abc", "Made for you"), default);
        Assert.Equal(2, trail.Count);
        Assert.Equal(Loc.Get(Strings.Nav.Home), trail[0].Label);
        Assert.Null(trail[^1].Route);   // the current page is never clickable
    }

    [Fact]
    public void A_browse_section_lives_under_browse_even_when_it_was_opened_from_home()
    {
        var trail = Shell.Trail(BrowseSection("spotify:section:abc", "Weekly Song Charts"), default);
        Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), trail[0].Label);
    }

    [Fact]
    public void A_foreign_origin_on_a_browse_page_is_prepended_to_the_whole_ia()
    {
        // Home is where the user came from, Browse is where the page lives, and BOTH are true.
        var origin = new Shell.NavOrigin("Home", new Shell.Route(Shell.RouteKind.Home));
        var trail = Shell.Trail(BrowseSection("spotify:section:abc", "Weekly Song Charts"), default, origin);
        Assert.Equal(3, trail.Count);
        Assert.Equal("Home", trail[0].Label);
        Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), trail[1].Label);
        Assert.Equal("Weekly Song Charts", trail[2].Label);
    }

    [Fact]
    public void A_search_lookup_stands_alone_because_it_invented_no_visit()
    {
        var origin = new Shell.NavOrigin("pop", Shell.Parse("search", "pop"));
        var trail = Shell.Trail(Shell.Parse("browse:spotify:genre:pop", "Pop"), default, origin);
        Assert.Equal(2, trail.Count);
        Assert.Equal("pop", trail[0].Label);
        Assert.Equal("Pop", trail[1].Label);
    }

    [Fact]
    public void An_origin_that_is_the_ia_root_adds_nothing()
    {
        var origin = new Shell.NavOrigin("Browse", new Shell.Route(Shell.RouteKind.Browse));
        var trail = Shell.Trail(BrowseSection("spotify:section:abc", "Weekly Song Charts"), default, origin);
        Assert.Equal(2, trail.Count);
    }

    [Fact]
    public void A_same_family_origin_inserts_between_the_root_and_the_current_page()
    {
        var origin = new Shell.NavOrigin("Netflix", Shell.Parse("browse:spotify:genre:netflix", "Netflix"));
        var trail = Shell.Trail(BrowseSection("spotify:section:abc", "New on Netflix"), default, origin);
        Assert.Equal(3, trail.Count);
        Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), trail[0].Label);
        Assert.Equal("Netflix", trail[1].Label);
        Assert.Equal("New on Netflix", trail[2].Label);
    }

    [Fact]
    public void The_concert_family_resolves_with_zero_page_code()
    {
        var hub = Shell.Trail(Shell.Parse("concerts"), default);
        Assert.Equal(2, hub.Count);
        Assert.Equal(Loc.Get(Strings.Concerts.Title), hub[1].Label);

        var detail = Shell.Trail(Shell.Parse("concert:abc", "Daft Punk"), default);
        Assert.Equal(3, detail.Count);
        Assert.Equal("Daft Punk", detail[2].Label);
    }

    [Fact]
    public void Only_the_families_that_have_a_masthead_resolve_one()
    {
        Assert.True(Shell.TryMasthead(Shell.Parse("browse"), default, null, out _, out _));
        Assert.False(Shell.TryMasthead(Shell.Parse("settings"), default, null, out _, out _));
        Assert.False(Shell.TryMasthead(Shell.Parse("album:spotify:album:abc", "X"), default, null, out _, out _));
    }
}

// Shares the ENTITIES collection: `Shell.Parse(uri, name)` interns the label through `Entities.Strings`, and a
// `TestScope.Fresh()` running in parallel resets that interner mid-test — the label id then resolves to nothing
// and a trail silently loses a crumb. The failure only shows in a full run, never in isolation.
[Collection(EntitiesCollection.Name)]
public class ShellOriginsTests
{
    [Fact]
    public void The_latest_arrival_wins_including_a_null_that_clears()
    {
        var dest = Shell.Parse("browse:spotify:genre:pop", "Pop");
        Shell.Origins.Write(dest, new Shell.NavOrigin("Home", new Shell.Route(Shell.RouteKind.Home)));
        Assert.Equal("Home", Shell.Origins.Peek(dest)!.Value.Label);

        Shell.Origins.Write(dest, null);
        Assert.Null(Shell.Origins.Peek(dest));
    }

    [Fact]
    public void The_store_is_bounded()
    {
        for (int i = 0; i < Shell.Origins.Capacity * 2; i++)
            Shell.Origins.Write(Shell.Parse("search", "q" + i), new Shell.NavOrigin("o" + i, new Shell.Route(Shell.RouteKind.Home)));
        // The oldest are gone; the newest survives.
        Assert.Null(Shell.Origins.Peek(Shell.Parse("search", "q0")));
        Assert.NotNull(Shell.Origins.Peek(Shell.Parse("search", "q" + (Shell.Origins.Capacity * 2 - 1))));
    }
}

// Shares the ENTITIES collection: `Shell.Parse(uri, name)` interns the label through `Entities.Strings`, and a
// `TestScope.Fresh()` running in parallel resets that interner mid-test — the label id then resolves to nothing
// and a trail silently loses a crumb. The failure only shows in a full run, never in isolation.
[Collection(EntitiesCollection.Name)]
public class ShellPageMotionTests
{
    [Fact]
    public void Every_recipe_keeps_its_exit_active_so_the_card_is_never_empty()
    {
        Assert.True(Shell.RecipeFor(Shell.NavTransitionKind.Forward).Exit.Active);
        Assert.True(Shell.RecipeFor(Shell.NavTransitionKind.Back).Exit.Active);
        Assert.True(Shell.PageSlideSafeForward.Exit.Active);
        Assert.True(Shell.PageSlideSafeBack.Exit.Active);
    }

    [Fact]
    public void Home_to_album_is_DrillIn_and_artist_to_artist_is_Sibling()
    {
        var home = new Shell.Route(Shell.RouteKind.Home);
        var album = Shell.Parse("album:spotify:album:1");
        var a = Shell.Parse("artist:spotify:artist:a");
        var b = Shell.Parse("artist:spotify:artist:b");
        Assert.Equal(Design.NavRelation.DrillIn, Shell.RelationOf(home, album, Shell.NavTransitionKind.Forward));
        Assert.Equal(Design.NavRelation.Sibling, Shell.RelationOf(a, b, Shell.NavTransitionKind.Forward));
        Assert.Equal(Design.NavRelation.Entrance, Shell.RelationOf(home, new Shell.Route(Shell.RouteKind.LibraryAlbums),
            Shell.NavTransitionKind.Forward));
    }

    [Fact]
    public void Forward_and_back_sibling_enter_from_opposite_sides()
        => Assert.Equal(-Shell.PageSlideSafeForward.Enter.Dx, Shell.PageSlideSafeBack.Enter.Dx);

    [Fact]
    public void The_video_safe_pair_is_position_only_and_neutral_is_an_honest_cut()
    {
        Assert.Equal(FluentGpu.Foundation.TransitionChannels.Position, Shell.PageSlideSafeForward.Channels);
        Assert.Null(Shell.RecipeForVideoSafe(Shell.NavTransitionKind.Neutral));
    }

    [Fact]
    public void Both_sides_of_a_swap_are_classified()
    {
        var module = Shell.Parse("module:wavee:module:youtube");
        var home = new Shell.Route(Shell.RouteKind.Home);
        Assert.True(Shell.NeedsVideoSafe(module, home));   // the OUTGOING root is still drawing
        Assert.True(Shell.NeedsVideoSafe(home, module));
        Assert.False(Shell.NeedsVideoSafe(home, home));
    }
}

public class ShellResponsiveLayoutTests
{
    [Fact]
    public void The_narrow_band_is_hysteretic()
    {
        Assert.True(Shell.Layout.NarrowFor(700f, current: false, initialized: true));
        Assert.True(Shell.Layout.NarrowFor(740f, current: true, initialized: true));    // holds until 760
        Assert.False(Shell.Layout.NarrowFor(760f, current: true, initialized: true));
        Assert.False(Shell.Layout.NarrowFor(740f, current: false, initialized: true));  // does not enter until 720
    }

    [Fact]
    public void A_zero_width_never_moves_a_band()
        => Assert.True(Shell.Layout.NarrowFor(0f, current: true, initialized: true));

    [Fact]
    public void The_nav_pane_widens_at_once_and_shrinks_after_the_dip()
    {
        Assert.Equal(Shell.Layout.NavPaneMidW, Shell.Layout.NavPaneDefaultFor(1400f, Shell.Layout.NavPaneNarrowW, true));
        Assert.Equal(Shell.Layout.NavPaneMidW, Shell.Layout.NavPaneDefaultFor(1380f, Shell.Layout.NavPaneMidW, true));
        Assert.Equal(Shell.Layout.NavPaneNarrowW, Shell.Layout.NavPaneDefaultFor(1370f, Shell.Layout.NavPaneMidW, true));
    }

    [Fact]
    public void An_unmeasured_viewport_takes_the_narrow_tier()
        => Assert.Equal(Shell.Layout.NavPaneNarrowW, Shell.Layout.InitialNavPaneDefaultForViewport(0f));

    [Fact]
    public void The_drawer_never_outgrows_the_window()
    {
        Assert.Equal(Shell.Layout.DrawerMinW, Shell.Layout.DrawerWidth(1200f, 100f));
        Assert.Equal(320f - Shell.Layout.DrawerViewportInset, Shell.Layout.DrawerWidth(320f, 400f));
    }

    [Fact]
    public void The_rail_clamps_through_one_pair()
    {
        Assert.Equal(Shell.RailMinW, Shell.ClampRailWidth(10f));
        Assert.Equal(Shell.RailMaxW, Shell.ClampRailWidth(10_000f));
    }

    [Fact]
    public void A_content_fitted_cap_is_not_floored_at_sixteen_by_nine()
    {
        // Flooring a CONTENT fit at 16:9 forces every wider-than-16:9 stream 32 %+ taller than its own aspect —
        // guaranteed black bars, the exact thing the fit exists to remove.
        float ultrawide = Shell.FitDockedVideoHeight(340f, 2350, 1000);
        Assert.True(ultrawide < Shell.DockedVideoNaturalH(340f));
        Assert.True(ultrawide >= Shell.DockedVideoFitMinH);
        // An unreported size answers 16:9, so the tile never flashes at a wrong shape on the way in.
        Assert.Equal(Shell.DockedVideoNaturalH(340f), Shell.FitDockedVideoHeight(340f, 0, 0), 3);
    }

    [Fact]
    public void The_splitter_floor_is_sixteen_by_nine_and_a_zero_height_takes_it()
        => Assert.Equal(Shell.DockedVideoNaturalH(340f), Shell.ClampDockedVideoHeight(0f, 340f), 3);

    [Fact]
    public void Can_fit_rail_is_the_one_answer()
    {
        Assert.True(Shell.CanFitRail(1600f, 280f));
        Assert.False(Shell.CanFitRail(900f, 280f));
    }
}

public class MergedChromeLayoutTests
{
    [Fact]
    public void The_name_and_the_actions_enter_at_their_own_widths()
    {
        Assert.False(Shell.Chrome.FromWidth(1100f, 2).ShowName);
        Assert.True(Shell.Chrome.FromWidth(1400f, 2).ShowName);
        Assert.False(Shell.Chrome.FromWidth(1100f, 2).ActionsInRow);
        Assert.True(Shell.Chrome.FromWidth(1400f, 2).ActionsInRow);
        // Below the stage every one of the four FOLDS rather than vanishing.
        Assert.True(Shell.Chrome.FromWidth(1100f, 2).ActionsInMenu);
    }

    [Fact]
    public void A_promotion_waits_the_reserve_and_a_demotion_is_immediate()
    {
        var narrow = Shell.Chrome.FromWidth(1300f, 2);
        Assert.False(narrow.ShowName);
        // At exactly the threshold the promotion is held: it re-resolves at (width − reserve).
        Assert.False(Shell.Chrome.Resolve(Shell.Layout.ChromeNameEnterW, 2, narrow).ShowName);
        Assert.True(Shell.Chrome.Resolve(Shell.Layout.ChromeNameEnterW + Shell.Layout.ChromePromotionHysteresisW, 2, narrow).ShowName);

        var wide = Shell.Chrome.FromWidth(1500f, 2);
        Assert.True(wide.ShowName);
        Assert.False(Shell.Chrome.Resolve(1350f, 2, wide).ShowName);   // demotions are immediate
    }

    [Fact]
    public void The_search_yields_all_the_way_to_an_icon_before_a_tab_is_shed()
    {
        // Tabs are measured against the 44-DIP magnifier, not against the field.
        var tight = Shell.Chrome.FromWidth(760f, 6);
        Assert.Equal(Shell.MergedSearchMode.Icon, tight.SearchMode);
        Assert.True(tight.ShowBack);
        Assert.True(tight.ShowTrailing);
    }

    [Fact]
    public void Under_extreme_pressure_the_fixed_islands_are_shed_in_order()
    {
        var floorChrome = Shell.Chrome.FromWidth(300f, 1);
        // New tab first, then identity, then Back — and the tab viewport never disappears.
        Assert.False(floorChrome.ShowNewTab);
        Assert.True(floorChrome.LeadClusterW >= Shell.Layout.ChromeTabViewportMinW);
    }

    [Fact]
    public void Forward_hides_below_its_own_band()
    {
        Assert.False(Shell.Chrome.FromWidth(500f, 1).ShowForward);
        Assert.True(Shell.Chrome.FromWidth(900f, 1).ShowForward);
    }

    [Fact]
    public void The_budget_charges_for_four_trailing_buttons_unconditionally()
    {
        // A page that gains or loses a pin row must not reflow the whole trailing island.
        float withActions = Shell.Chrome.FixedBudget(name: true, actionsInRow: true, forward: true, back: true, newTab: true, trailing: true);
        float without = Shell.Chrome.FixedBudget(name: true, actionsInRow: false, forward: true, back: true, newTab: true, trailing: true);
        Assert.Equal(4f * Shell.Layout.ChromeNavButtonW, withActions - without, 3);
    }

    [Fact]
    public void Every_published_width_is_quantised_so_a_resize_drag_cannot_render_per_pixel()
    {
        for (float w = 900f; w < 1000f; w += 7f)
        {
            var c = Shell.Chrome.FromWidth(w, 3);
            if (c.SearchMode == Shell.MergedSearchMode.Field)
                Assert.Equal(0f, c.SearchWidth % Shell.Layout.ChromeWidthQuantumW, 3);
            Assert.Equal(0f, c.LeadClusterW % Shell.Layout.ChromeWidthQuantumW, 3);
        }
    }

    [Fact]
    public void The_lead_cluster_widens_at_once_and_narrows_only_after_the_reserve()
    {
        // The OPPOSITE polarity from the boolean stages: growth must never clip a longer title, and shrinking must not
        // reclaim space only to hand it straight back next frame.
        var wide = Shell.Chrome.Resolve(1600f, 720f, null);
        var slightlyShorter = Shell.Chrome.Resolve(1600f, 700f, wide);
        Assert.Equal(wide.LeadClusterW, slightlyShorter.LeadClusterW);

        var muchShorter = Shell.Chrome.Resolve(1600f, 240f, wide);
        Assert.True(muchShorter.LeadClusterW < wide.LeadClusterW);
    }

    [Fact]
    public void A_structural_shrink_always_beats_the_hold()
    {
        var wide = Shell.Chrome.Resolve(1900f, 900f, null);
        var narrow = Shell.Chrome.Resolve(700f, 900f, wide);
        Assert.True(narrow.LeadClusterW < wide.LeadClusterW);   // the hold never overflows the row
    }
}

public class TabWorkspaceTests
{
    [Fact]
    public void A_fresh_workspace_is_one_home_tab()
    {
        var w = new Shell.TabWorkspace();
        Assert.Single(w.Tabs);
        Assert.Equal(Shell.RouteKind.Home, w.Active.Route.Kind);
    }

    [Fact]
    public void Open_is_a_push_and_activate_is_a_restore()
    {
        var w = new Shell.TabWorkspace();
        Assert.Equal(Shell.TabNavIntent.Push, w.Open(Shell.Parse("browse")).Intent);
        Assert.Equal(Shell.TabNavIntent.Restore, w.Activate(0).Intent);
        Assert.Equal(Shell.TabNavIntent.None, w.Activate(0).Intent);   // already active
    }

    [Fact]
    public void Ids_are_never_reused_so_a_reorder_keeps_every_identity()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("browse"));
        int id = w.ActiveId;
        w.SetPinned(id, true);
        Assert.Equal(id, w.ActiveId);
        Assert.Equal(0, w.IndexOf(id));   // pins are always the leading block
    }

    [Fact]
    public void The_last_tab_is_never_closed()
    {
        var w = new Shell.TabWorkspace();
        Assert.Equal(Shell.TabNavIntent.None, w.Close(0).Intent);
        Assert.Single(w.Tabs);
    }

    [Fact]
    public void Closing_the_active_tab_restores_its_right_neighbour()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("browse"));
        w.Open(Shell.Parse("settings"));
        w.Activate(1);                         // browse
        var r = w.Close(1);
        Assert.Equal(Shell.TabNavIntent.Restore, r.Intent);
        Assert.Equal(Shell.RouteKind.Settings, r.Route!.Value.Kind);
    }

    [Fact]
    public void Closing_a_background_tab_changes_nothing_on_screen()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("browse"));
        Assert.Equal(Shell.TabNavIntent.None, w.Close(0).Intent);
    }

    [Fact]
    public void An_emptied_workspace_reseeds_home()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("browse"));
        var r = w.CloseWhere(static _ => true);
        Assert.Single(w.Tabs);
        Assert.Equal(Shell.RouteKind.Home, r.Route!.Value.Kind);
    }

    [Fact]
    public void The_pinned_revision_bumps_only_when_the_persisted_subset_changes()
    {
        var w = new Shell.TabWorkspace();
        int before = w.PinnedRevision;
        w.Open(Shell.Parse("browse"));            // unpinned: nothing persisted changed
        Assert.Equal(before, w.PinnedRevision);
        w.SetPinned(w.ActiveId, true);
        Assert.True(w.PinnedRevision > before);
    }

    [Fact]
    public void The_last_selected_pin_survives_switching_to_a_session_only_tab()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("browse"), pinned: true);
        int pin = w.ActiveId;
        w.Open(Shell.Parse("settings"));          // unpinned
        Assert.Equal(pin, w.LastSelectedPinnedId);
    }

    [Fact]
    public void Restore_pinned_normalises_a_legacy_key_before_the_router_sees_it()
    {
        var w = new Shell.TabWorkspace();
        var snapshot = new Shell.WorkspaceTabsSnapshot(
            [new Shell.PersistedTab(Shell.LegacyRecentsRoute, null)], 0);
        var route = w.RestorePinned(in snapshot);
        Assert.Equal(Shell.RouteKind.Recents, route.Kind);
    }

    [Fact]
    public void The_pinned_snapshot_round_trips()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", "RAM"), pinned: true);
        var snap = w.PinnedSnapshot();
        Assert.Single(snap.Tabs);
        Assert.Equal("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", snap.Tabs[0].Route);
        Assert.Equal(0, snap.LastSelected);
    }

    // ── the session restore onto the workspace (G-258) ────────────────────────────────────────────────────────────────

    const string AlbumKey = "album:spotify:album:1TSZDcvlPtAnekTaItI3qO";

    [Fact]
    public void A_restored_route_lands_on_the_active_tab_without_rewriting_pins()
    {
        // The relaunch onto an album with no pins: the strip must read the album, not the seeded Home.
        var w = new Shell.TabWorkspace();
        w.RestorePinned(new Shell.WorkspaceTabsSnapshot([], -1));
        int revision = w.PinnedRevision;

        int id = w.RestoreActiveRoute(Shell.Parse(AlbumKey, "RAM"));

        Assert.Single(w.Tabs);
        Assert.Equal(w.ActiveId, id);
        Assert.Equal(Shell.RouteKind.Album, w.Active.Route.Kind);
        Assert.Equal(revision, w.PinnedRevision);
    }

    [Fact]
    public void Restoring_onto_a_pinned_tab_does_not_bump_the_pinned_revision()
    {
        var w = new Shell.TabWorkspace();
        w.RestorePinned(new Shell.WorkspaceTabsSnapshot([new Shell.PersistedTab("browse", null)], 0));
        int revision = w.PinnedRevision;

        w.RestoreActiveRoute(Shell.Parse(AlbumKey, "RAM"));

        Assert.True(w.Active.Pinned);
        Assert.Equal(Shell.RouteKind.Album, w.Active.Route.Kind);
        Assert.Equal(revision, w.PinnedRevision);
    }

    [Fact]
    public void The_session_selects_a_surviving_pin_by_its_pinned_index()
    {
        var w = new Shell.TabWorkspace();
        w.RestorePinned(new Shell.WorkspaceTabsSnapshot(
            [new Shell.PersistedTab("browse", null), new Shell.PersistedTab(AlbumKey, "RAM")], 0));
        int revision = w.PinnedRevision;
        int lastPinned = w.LastSelectedPinnedId;

        Assert.True(w.TrySelectPinned(1));

        Assert.Equal(1, w.ActiveIndex);
        Assert.Equal(1, w.ActivePinnedIndex);
        Assert.Equal(Shell.RouteKind.Album, w.Active.Route.Kind);
        Assert.Equal(revision, w.PinnedRevision);          // restore never rewrites pins
        Assert.Equal(lastPinned, w.LastSelectedPinnedId);
    }

    [Fact]
    public void A_missing_pinned_index_keeps_the_pinned_default()
    {
        var w = new Shell.TabWorkspace();
        w.RestorePinned(new Shell.WorkspaceTabsSnapshot([new Shell.PersistedTab("browse", null)], 0));
        int active = w.ActiveId;

        Assert.False(w.TrySelectPinned(-1));               // the session's tab was session-only
        Assert.False(w.TrySelectPinned(1));                // the pins changed since the session was written
        Assert.Equal(active, w.ActiveId);
    }

    [Fact]
    public void A_session_only_active_tab_persists_as_no_pinned_index()
    {
        var w = new Shell.TabWorkspace();
        w.Open(Shell.Parse("browse"), pinned: true);
        Assert.Equal(0, w.ActivePinnedIndex);

        w.Open(Shell.Parse("settings"));                   // unpinned, appended, active
        Assert.Equal(-1, w.ActivePinnedIndex);
    }

    [Fact]
    public void A_pinned_index_survives_the_id_reminting_of_a_relaunch()
    {
        // Last launch: the album pin is active. Ids are per process — the next launch mints new ones.
        var before = new Shell.TabWorkspace();
        before.Open(Shell.Parse("browse"), pinned: true);
        before.Open(Shell.Parse(AlbumKey, "RAM"), pinned: true);
        int savedIndex = before.ActivePinnedIndex;
        var pins = before.PinnedSnapshot();

        var after = new Shell.TabWorkspace();
        after.RestorePinned(in pins);
        Assert.True(after.TrySelectPinned(savedIndex));
        Assert.Equal(Shell.RouteKind.Album, after.Active.Route.Kind);
    }
}

public class PlayerBarLayoutTests
{
    [Theory]
    [InlineData(300f, Shell.PlayerBarTier.Minimal)]
    [InlineData(440f, Shell.PlayerBarTier.Compact)]
    [InlineData(760f, Shell.PlayerBarTier.Medium)]
    [InlineData(900f, Shell.PlayerBarTier.Comfortable)]
    [InlineData(1100f, Shell.PlayerBarTier.Wide)]
    [InlineData(1240f, Shell.PlayerBarTier.Full)]
    public void The_tier_ladder(float width, Shell.PlayerBarTier expected)
        => Assert.Equal(expected, Shell.PlayerBar.Nominal(width));

    [Fact]
    public void Widening_commits_at_once_and_narrowing_holds_through_the_dip()
    {
        Assert.Equal(Shell.PlayerBarTier.Wide,
            Shell.PlayerBar.Resolve(1100f, Shell.PlayerBarTier.Comfortable, initialized: true));
        Assert.Equal(Shell.PlayerBarTier.Wide,
            Shell.PlayerBar.Resolve(1090f, Shell.PlayerBarTier.Wide, initialized: true));
        Assert.Equal(Shell.PlayerBarTier.Comfortable,
            Shell.PlayerBar.Resolve(1070f, Shell.PlayerBarTier.Wide, initialized: true));
    }

    [Fact]
    public void Every_tier_honours_the_hit_target_floors()
    {
        // Three of six tiers once shipped SUB-MINIMUM targets — at the narrowest window, where a mis-click costs most.
        for (int t = 0; t <= (int)Shell.PlayerBarTier.Full; t++)
        {
            var layout = Shell.PlayerBarLayout.ForTier((Shell.PlayerBarTier)t);
            Assert.True(layout.ButtonBox >= Shell.PlayerBarLayout.MinButtonBox);
            Assert.True(layout.ButtonGlyph >= Shell.PlayerBarLayout.MinButtonGlyph);
            Assert.True(layout.PrimaryBox >= Shell.PlayerBarLayout.MinPrimaryBox);
            Assert.True(layout.PrimaryGlyph >= Shell.PlayerBarLayout.MinPrimaryGlyph);
        }
    }

    [Fact]
    public void The_button_metrics_are_flat_across_the_ladder()
    {
        var minimal = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal);
        var full = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full);
        Assert.Equal(minimal.ButtonBox, full.ButtonBox);
        Assert.Equal(minimal.ButtonGlyph, full.ButtonGlyph);
    }

    [Fact]
    public void Identity_survives_to_the_floor_and_the_device_picker_never_drops()
    {
        var minimal = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal);
        Assert.True(minimal.ShowLikeSlot);     // identity-first
        Assert.True(minimal.ShowSubtitle);     // title + artist are one indivisible block
        Assert.True(minimal.ShowDevices);      // the only route when local playback is unavailable
        Assert.False(minimal.ShowPrevNext);    // pressure is absorbed by what the row DROPS
    }

    [Fact]
    public void The_sweep_spans_the_real_bar_on_an_ultrawide()
    {
        Assert.Equal(Shell.PlayerBarLayout.TopEdgeWidthFloor,
            Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full, 1600f).TopEdgeWidth);
        Assert.Equal(3440f, Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Full, 3440f).TopEdgeWidth);
    }
}

public class DevicePickerTests
{
    static Playback.Audio.LocalAudioDevice Local(string id, string name)
        => new(id, name, 0, IsDefault: false);

    [Fact]
    public void The_picker_is_two_sections_with_a_separator()
    {
        var rows = Shell.DevicePickerRows([Local("a", "Speakers")], null, true, true, default, null, default, default);
        Assert.Equal(Shell.DevicePickerRowKind.Header, rows[0].Kind);
        Assert.Equal(Shell.DevicePickerRowKind.LocalDefault, rows[1].Kind);
        Assert.Equal(Shell.DevicePickerRowKind.LocalDevice, rows[2].Kind);
        Assert.Equal(Shell.DevicePickerRowKind.Quality, rows[3].Kind);
        Assert.Equal(Shell.DevicePickerRowKind.Separator, rows[4].Kind);
        Assert.Equal(Shell.DevicePickerRowKind.Header, rows[5].Kind);
    }

    [Fact]
    public void An_empty_connect_roster_gets_the_two_hint_rows()
    {
        var rows = Shell.DevicePickerRows([], null, true, true, default, null, default, default);
        Assert.Equal(2, rows.FindAll(static r => r.Kind == Shell.DevicePickerRowKind.Empty).Count);
    }

    [Fact]
    public void Unsupported_local_playback_disables_the_rows_and_says_why()
    {
        var rows = Shell.DevicePickerRows([Local("a", "Speakers")], null, localSupported: false, true, default, null,
            default, default);
        var def = rows.Find(static r => r.Kind == Shell.DevicePickerRowKind.LocalDefault);
        Assert.False(def.Enabled);
        Assert.NotNull(def.Accelerator);
    }

    [Fact]
    public void A_long_name_is_capped_so_the_flyout_cannot_grow_past_the_window()
    {
        string huge = new('x', 200);
        var rows = Shell.DevicePickerRows([Local("a", huge)], "a", true, true, default, null, default, default);
        var row = rows.Find(static r => r.Kind == Shell.DevicePickerRowKind.LocalDevice);
        Assert.Equal(Shell.DevicePickerMaxLabelChars, row.Label.Length);
        Assert.EndsWith("…", row.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_default_is_checked_only_when_we_are_the_active_output()
    {
        var live = Shell.DevicePickerRows([], null, true, weAreActiveOutput: true, default, null, default, default);
        Assert.True(live.Find(static r => r.Kind == Shell.DevicePickerRowKind.LocalDefault).IsChecked);

        var remote = Shell.DevicePickerRows([], null, true, weAreActiveOutput: false, default, null, default, default);
        Assert.False(remote.Find(static r => r.Kind == Shell.DevicePickerRowKind.LocalDefault).IsChecked);
    }

    // ── the quality row: the player's own echo, never a loopback of the setting ────────────────────────────────────

    [Fact]
    public void The_quality_row_lives_in_this_computer_before_the_separator()
    {
        var rows = Shell.DevicePickerRows([Local("a", "Speakers")], null, true, true, default, null, default, default);
        int quality = rows.FindIndex(static r => r.Kind == Shell.DevicePickerRowKind.Quality);
        int separator = rows.FindIndex(static r => r.Kind == Shell.DevicePickerRowKind.Separator);
        Assert.True(quality >= 0);
        Assert.True(quality < separator);
    }

    [Fact]
    public void Lossless_asked_but_ogg_320_observed_reports_320_and_says_so_is_below_setting()
    {
        var observed = new Playback.Audio.Opened(Spotify.Audio.Format.OggVorbis320, 200_000, 0f,
            Playback.Audio.LabelFor(Spotify.Audio.Format.OggVorbis320, 0, 0), 320, false);
        var rows = Shell.DevicePickerRows([], null, true, true, default, null, observed, Spotify.Audio.Quality.Lossless);
        var row = rows.Find(static r => r.Kind == Shell.DevicePickerRowKind.Quality);

        // The rung the row reports is the PLAYER's, not the setting's. Asserted through `LabelFor` (pure) rather than
        // the rendered row: this host never loads the loc tables, so every `Strings.*` call answers "[key]" and a
        // substring assertion on `row.Label` would be testing the localizer, not the decision.
        Assert.Contains("320", observed.Label, StringComparison.Ordinal);
        Assert.Equal(
            Strings.Player.QualityBelowSetting(observed.Label, Loc.Get(Strings.Settings.Playback.QualityLossless)),
            row.Label);
        // …and it chose the DISAGREEMENT wording, not the contented one. "[key]" differs per key, so this still bites
        // with no tables loaded — it is the assertion that would catch the row quietly echoing the setting.
        Assert.NotEqual(Strings.Player.QualityPlaying(observed.Label), row.Label);
    }

    [Fact]
    public void Nothing_playing_reports_the_idle_wording()
    {
        var rows = Shell.DevicePickerRows([], null, true, true, default, null, default, default);
        var row = rows.Find(static r => r.Kind == Shell.DevicePickerRowKind.Quality);
        Assert.Equal(Loc.Get(Strings.Player.QualityIdle), row.Label);
    }
}

public class ShellTintOwnershipTests
{
    [Fact]
    public void An_ungraded_claim_holds_the_previous_colour_rather_than_flashing_neutral()
    {
        var held = Shell.Claim(Shell.TintOwner.Neutral, "album:a", 0xFF112233u, definite: true);
        var next = Shell.Claim(held, "album:b", 0u, definite: false);
        Assert.Equal("album:b", next.Claimant);
        Assert.Equal(0xFF112233u, next.Argb);   // the chrome never dips to neutral between two coloured pages
        Assert.False(next.Definite);
    }

    [Fact]
    public void A_definite_grading_replaces_an_indefinite_one_and_never_the_other_way()
    {
        var indefinite = Shell.Claim(Shell.TintOwner.Neutral, "album:a", 0u, definite: false);
        var definite = Shell.Claim(indefinite, "album:a", 0xFF445566u, definite: true);
        Assert.True(definite.Definite);
        Assert.Equal(0xFF445566u, definite.Argb);

        var demote = Shell.Claim(definite, "album:a", 0xFF000000u, definite: false);
        Assert.Equal(definite, demote);
    }

    [Fact]
    public void A_grading_for_a_page_the_user_has_left_is_refused()
    {
        var owner = Shell.Claim(Shell.TintOwner.Neutral, "album:b", 0u, definite: false);
        Assert.False(Shell.AcceptsRefresh(owner, "album:a"));
        Assert.True(Shell.AcceptsRefresh(owner, "album:b"));
    }

    [Fact]
    public void An_empty_claimant_is_the_neutral_value()
        => Assert.True(Shell.Claim(Shell.TintOwner.Neutral, "", 0u, true).IsNeutral);

    [Fact]
    public void Two_pages_never_share_a_claimant_key()
    {
        var a = Shell.Parse("whatsnew", "0.2.9");
        var b = Shell.Parse("whatsnew", "0.3.0");
        Assert.NotEqual(Shell.TintClaimantOf(a), Shell.TintClaimantOf(b));
        // …and the same page in two tabs reclaims its OWN plane.
        Assert.Equal(Shell.TintClaimantOf(a), Shell.TintClaimantOf(a with { Tab = 7 }));
    }
}

public class ShellAuthFoldTests
{
    [Fact]
    public void No_credential_means_the_sign_in_surface_owns_the_window()
        => Assert.Equal(Shell.AuthState.SignInRequired,
            Shell.FoldAuth(Spotify.SessionPhase.Online, Spotify.SessionFault.None, hasStoredCredential: false));

    [Fact]
    public void A_connectivity_blip_never_pushes_a_signed_in_user_at_a_login_screen()
        => Assert.Equal(Shell.AuthState.Offline,
            Shell.FoldAuth(Spotify.SessionPhase.Failed, Spotify.SessionFault.Network, hasStoredCredential: true));

    [Fact]
    public void Only_a_rejected_credential_reaches_sign_in_required()
        => Assert.Equal(Shell.AuthState.SignInRequired,
            Shell.FoldAuth(Spotify.SessionPhase.Failed, Spotify.SessionFault.CredentialRejected, true));

    [Theory]
    [InlineData(Spotify.SessionPhase.Resolving)]
    [InlineData(Spotify.SessionPhase.Connecting)]
    [InlineData(Spotify.SessionPhase.Handshaking)]
    [InlineData(Spotify.SessionPhase.Authenticating)]
    [InlineData(Spotify.SessionPhase.Minting)]
    [InlineData(Spotify.SessionPhase.Reconnecting)]
    public void Everything_in_flight_reads_as_connecting(Spotify.SessionPhase phase)
        => Assert.Equal(Shell.AuthState.Connecting, Shell.FoldAuth(phase, Spotify.SessionFault.None, true));

    [Fact]
    public void Online_with_a_credential_is_live()
        => Assert.Equal(Shell.AuthState.Live,
            Shell.FoldAuth(Spotify.SessionPhase.Online, Spotify.SessionFault.None, true));
}

public class ShellTipsCoreTests
{
    [Fact]
    public void The_codec_is_idempotent_and_order_preserving()
    {
        string set = Shell.TipsCore.Add(null, "a.b");
        Assert.Equal("a.b", set);
        Assert.Same(set, Shell.TipsCore.Add(set, "a.b"));
        set = Shell.TipsCore.Add(set, "c.d");
        Assert.Equal(["a.b", "c.d"], Shell.TipsCore.Parse(set));
    }

    [Fact]
    public void Contains_scans_in_place_and_does_not_match_a_prefix()
    {
        Assert.True(Shell.TipsCore.Contains("a.b\nc.d", "c.d"));
        Assert.False(Shell.TipsCore.Contains("a.bc", "a.b"));
        Assert.False(Shell.TipsCore.Contains(null, "a.b"));
    }

    [Fact]
    public void A_tip_whose_acknowledgement_cannot_be_persisted_is_never_shown()
        => Assert.False(Shell.TipsCore.ShouldShow("", "a.b", armedThisSession: false, anotherTipActive: false,
            canPresent: false));

    [Fact]
    public void One_tip_at_a_time_and_one_appearance_per_launch()
    {
        Assert.True(Shell.TipsCore.ShouldShow("", "a.b", false, false, true));
        Assert.False(Shell.TipsCore.ShouldShow("", "a.b", armedThisSession: true, false, true));
        Assert.False(Shell.TipsCore.ShouldShow("", "a.b", false, anotherTipActive: true, true));
        Assert.False(Shell.TipsCore.ShouldShow("a.b", "a.b", false, false, true));
    }
}

public class ShellHistoryRulesTests
{
    static Shell.HistoryEntry E(string key, string? arg, DateTime at) => new(Shell.Parse(key, arg ?? ""), at);

    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void A_show_is_an_entity_visit_not_a_generic_page()
    {
        Assert.Equal("show", Shell.History.KindOf(Shell.Parse("show:spotify:show:abc")));
        Assert.Equal("playlist", Shell.History.KindOf(Shell.Parse("pl:spotify:playlist:abc")));
        Assert.Equal("library", Shell.History.KindOf(Shell.Parse("liked")));
        Assert.Equal("search", Shell.History.KindOf(Shell.Parse("search", "pop")));
        Assert.Equal("browse", Shell.History.KindOf(Shell.Parse("browse")));
        Assert.Equal("page", Shell.History.KindOf(Shell.Parse("settings")));
    }

    [Fact]
    public void The_visible_list_is_newest_first_then_filtered_then_searched()
    {
        var entries = new List<Shell.HistoryEntry>
        {
            E("home", null, Now.AddHours(-3)),
            E("pl:spotify:playlist:a", "Chill", Now.AddHours(-2)),
            E("settings", null, Now.AddHours(-1)),
        };
        var all = Shell.History.Visible(entries, Shell.History.Filter.All, "");
        Assert.Equal(Shell.RouteKind.Settings, all[0].Route.Kind);   // newest first

        var playlists = Shell.History.Visible(entries, Shell.History.Filter.Playlists, "");
        Assert.Single(playlists);

        var searched = Shell.History.Visible(entries, Shell.History.Filter.All, "chill");
        Assert.Single(searched);
    }

    [Fact]
    public void Most_visited_counts_the_full_unfiltered_log_and_is_stable_within_a_count()
    {
        var entries = new List<Shell.HistoryEntry>
        {
            E("home", null, Now.AddHours(-5)),
            E("browse", null, Now.AddHours(-4)),
            E("home", null, Now.AddHours(-3)),
            E("settings", null, Now.AddHours(-2)),
            E("browse", null, Now.AddHours(-1)),
        };
        var counts = Shell.History.VisitCounts(entries);
        Assert.Equal(2, counts["home"]);
        Assert.Equal(2, counts["browse"]);
        Assert.Equal(1, counts["settings"]);

        var rows = Shell.History.MostVisited(Shell.History.Visible(entries, Shell.History.Filter.All, ""), counts);
        Assert.Equal(3, rows.Count);                                  // deduped by route
        // Two 2-count routes: the one whose MOST RECENT visit is newer comes first, and within a route the first row
        // kept is the most recent visit.
        Assert.Equal(Shell.RouteKind.Browse, rows[0].Route.Kind);
        Assert.Equal(Shell.RouteKind.Home, rows[1].Route.Kind);
        Assert.Equal(Shell.RouteKind.Settings, rows[2].Route.Kind);
    }

    [Fact]
    public void The_date_group_ladder()
    {
        Assert.Equal(Loc.Get(Strings.Nav.History.Group.Today), Shell.History.DateGroupLabel(Now, Now));
        Assert.Equal(Loc.Get(Strings.Nav.History.Group.Yesterday), Shell.History.DateGroupLabel(Now.AddDays(-1), Now));
        Assert.Equal(Loc.Get(Strings.Nav.History.Group.ThisWeek), Shell.History.DateGroupLabel(Now.AddDays(-3), Now));
        Assert.Equal(Loc.Get(Strings.Nav.History.Group.ThisMonth), Shell.History.DateGroupLabel(Now.AddDays(-20), Now));
        Assert.Equal(Loc.Get(Strings.Nav.History.Group.Earlier), Shell.History.DateGroupLabel(Now.AddDays(-90), Now));
    }

    [Fact]
    public void Only_a_real_destination_earns_a_taskbar_row_and_the_walk_is_newest_first()
    {
        var entries = new List<Shell.HistoryEntry>
        {
            E("settings", null, Now.AddHours(-4)),                       // never a jump-list row
            E("album:spotify:album:a", "A", Now.AddHours(-3)),
            E("album:spotify:album:b", "B", Now.AddHours(-2)),
            E("album:spotify:album:a", "A", Now.AddHours(-1)),           // deduped
        };
        var rows = Shell.History.RecentSurfaces(entries, 6);
        Assert.Equal(2, rows.Length);
        Assert.Equal("album:spotify:album:a", rows[0].Route);            // newest first
        Assert.Equal("album:spotify:album:b", rows[1].Route);
    }
}
