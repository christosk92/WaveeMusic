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
//   THE SEARCH LANE IS WHAT THE ROW CAN GIVE, NOT WHAT THE SEARCH WANTS. `PreferredSearchWidth` clamps UP to the
//   280-DIP minimum, so it can never be the test for whether a field fits; `Chrome.SearchLane` is. And a published
//   field is a PROMISE — `Chrome.FieldWidthFor` is the only place a measurement may narrow it, and never below the
//   minimum, because the bar's centre column HUGS the field and measuring against it is otherwise a floorless
//   ratchet (the clipped "Sear" stub, #88).
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

    // ══ THE SEARCH LANE (#88) ═══════════════════════════════════════════════════════════════════════════════════════
    //
    // The defect these cover: the row chose `Field` against a width it could not actually deliver, and NOTHING then
    // stopped the field being laid out below its own 280-DIP minimum — the view clamped it down to the bar's measured
    // centre width with no floor, and under the elastic tabs lane that measurement is a feedback of the field's own
    // width, so one bad frame latched it at ~40 DIP and the placeholder rendered as a clipped "Sear" stub.
    //
    // The two halves of the contract, asserted over a sweep of real window widths rather than at hand-picked points:
    //   1. a published Field is SEATABLE  — FixedBudget + SearchWidth + RequiredTabExtent ≤ width;
    //   2. a laid-out Field is USABLE     — never below ChromeSearchMinW, whatever the measurement says.

    static readonly float[] SweepExtents = [110f, 220f, 330f, 440f, 660f, 990f];

    /// <summary>Every form <see cref="Shell.FrameRules.ChipFor"/> can select. The budget used to know only the avatar,
    /// so the other three were 52-68 DIP of unpriced row for the whole of a sign-in or a resume (#88).</summary>
    static readonly Shell.FrameRules.ChipForm[] SweepChips =
    [
        Shell.FrameRules.ChipForm.Profile, Shell.FrameRules.ChipForm.Connecting,
        Shell.FrameRules.ChipForm.Reconnect, Shell.FrameRules.ChipForm.SignIn,
    ];

    [Theory]
    // width · measured tab extent · the mode the row can honestly seat · the field width it publishes
    [InlineData(847f, 110f, false, Shell.Layout.ChromeSearchIconW)]   // one tab: 847 − 458 − 110 = 279, one DIP short
    [InlineData(848f, 110f, true, Shell.Layout.ChromeSearchMinW)]     // …and 280 exactly seats the minimum field
    [InlineData(957f, 220f, false, Shell.Layout.ChromeSearchIconW)]   // two tabs cost 110 more, so the flip moves 110 up
    [InlineData(958f, 220f, true, Shell.Layout.ChromeSearchMinW)]
    [InlineData(1250f, 270f, true, 340f)]                             // the lane (346) is under the DESIRE (350): take the lane
    [InlineData(2400f, 330f, true, Shell.Layout.ChromeSearchMaxW)]    // plenty spare: the desire caps at the max
    public void The_field_is_chosen_and_sized_by_the_lane_the_row_can_really_give(
        float width, float extent, bool field, float searchWidth)
    {
        var c = Shell.Chrome.Resolve(width, extent);
        Assert.Equal(field ? Shell.MergedSearchMode.Field : Shell.MergedSearchMode.Icon, c.SearchMode);
        Assert.Equal(searchWidth, c.SearchWidth, 3);
    }

    [Fact]
    public void Every_field_the_ladder_publishes_is_one_the_row_can_actually_seat()
    {
        foreach (var chip in SweepChips)
        foreach (float extent in SweepExtents)
        {
            for (float w = 200f; w <= 2600f; w += 1f)
            {
                var c = Shell.Chrome.Resolve(w, extent, null, chip);
                Assert.Equal(chip, c.Chip);
                if (c.SearchMode == Shell.MergedSearchMode.Icon)
                {
                    Assert.Equal(Shell.Layout.ChromeSearchIconW, c.SearchWidth);
                    continue;
                }
                // 1. the row's own arithmetic seats it, tabs and the chip form ON SCREEN included…
                Assert.True(c.FootprintFor(extent) <= w,
                    $"Field at width {w} (extent {extent}, chip {chip}) needs {c.FootprintFor(extent)} DIP.");
                // …which is the same statement as a non-negative lane.
                Assert.True(Shell.Chrome.SearchLane(w, extent, c.ShowName, c.ShowActions, c.ShowForward, c.ShowBack,
                    c.ShowNewTab, c.ShowTrailing, c.Chip) >= c.SearchWidth);
                // 2. and it is a width the field can actually use.
                Assert.InRange(c.SearchWidth, Shell.Layout.ChromeSearchMinW, Shell.Layout.ChromeSearchMaxW);
                Assert.Equal(0f, c.SearchWidth % Shell.Layout.ChromeWidthQuantumW, 3);
            }
        }
    }

    [Fact]
    public void No_stage_the_row_enters_costs_more_than_the_row_can_seat()
    {
        // The other half of the seating invariant, and the one the raw thresholds broke: an OPTIONAL island is only ever
        // in the row when the fixed budget it produced, plus the search at its 280-DIP minimum, plus the tabs at their
        // required extent, still fit inside the window. Which is why a promotion can never demote the field.
        foreach (var chip in SweepChips)
        foreach (float extent in SweepExtents)
        {
            for (float w = 200f; w <= 2600f; w += 1f)
            {
                var c = Shell.Chrome.Resolve(w, extent, null, chip);
                if (!c.ShowActions && !c.ShowName) continue;
                Assert.True(
                    c.FixedBudgetFor() + Shell.Layout.ChromeSearchMinW + Shell.Chrome.RequiredTabExtent(extent) <= w,
                    $"Stage at width {w} (extent {extent}, chip {chip}) costs "
                    + $"{c.FixedBudgetFor() + Shell.Layout.ChromeSearchMinW + Shell.Chrome.RequiredTabExtent(extent)} DIP.");
                Assert.Equal(Shell.MergedSearchMode.Field, c.SearchMode);   // …so the field is standing, by construction
                // The ladder's priority order: the NAME is the first thing to go, so it never shows while the four
                // trailing actions are still folded into the profile menu.
                if (c.ShowName) Assert.True(c.ShowActions);
                // And only the avatar chip has a name column to open at all.
                if (c.ShowName) Assert.Equal(Shell.FrameRules.ChipForm.Profile, c.Chip);
            }
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-50f)]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(40f)]        // the stub the bug actually produced
    [InlineData(279f)]
    [InlineData(280f)]
    [InlineData(320f)]
    [InlineData(10_000f)]
    public void A_measured_centre_column_may_trim_the_field_but_can_never_clip_it(float measured)
    {
        var field = Shell.Chrome.Resolve(1600f, 220f);
        Assert.Equal(Shell.MergedSearchMode.Field, field.SearchMode);

        float laid = field.LaidOutSearchWidth(measured);
        Assert.InRange(laid, Shell.Layout.ChromeSearchMinW, Shell.Layout.ChromeSearchMaxW);
        Assert.True(laid <= field.SearchWidth);                 // a measurement only ever trims
        if (measured >= Shell.Layout.ChromeSearchMinW) Assert.Equal(MathF.Min(field.SearchWidth, measured), laid, 3);
        else Assert.Equal(field.SearchWidth, laid, 3);          // below the minimum the MEASUREMENT is what is wrong

        // Icon mode ignores the measurement outright — the magnifier is a fixed 44.
        var icon = Shell.Chrome.Resolve(700f, 220f);
        Assert.Equal(Shell.MergedSearchMode.Icon, icon.SearchMode);
        Assert.Equal(Shell.Layout.ChromeSearchIconW, icon.LaidOutSearchWidth(measured));
    }

    [Fact]
    public void The_measurement_feedback_loop_has_a_floor_so_the_field_cannot_ratchet_itself_shut()
    {
        // The bar's centre column HUGS the field under the elastic tabs lane, so what it measures next frame is what the
        // field asked for this frame. Feeding the output back in is therefore the real loop — it must settle, not decay.
        var field = Shell.Chrome.Resolve(1600f, 220f);
        float w = 40f;                                          // seeded by one bad frame
        for (int i = 0; i < 16; i++) w = field.LaidOutSearchWidth(w);
        Assert.Equal(field.SearchWidth, w, 3);
        Assert.True(w >= Shell.Layout.ChromeSearchMinW);
    }

    [Fact]
    public void The_search_promotes_after_the_reserve_and_demotes_at_once()
    {
        var icon = Shell.Chrome.Resolve(700f, 110f);
        Assert.Equal(Shell.MergedSearchMode.Icon, icon.SearchMode);

        // 848 is the first width whose lane seats the minimum; a PROMOTION re-resolves at (width − 40) and so waits.
        Assert.Equal(Shell.MergedSearchMode.Icon, Shell.Chrome.Resolve(848f, 110f, icon).SearchMode);
        Assert.Equal(Shell.MergedSearchMode.Icon, Shell.Chrome.Resolve(887f, 110f, icon).SearchMode);
        var promoted = Shell.Chrome.Resolve(888f, 110f, icon);
        Assert.Equal(Shell.MergedSearchMode.Field, promoted.SearchMode);

        // Coming back down, a demotion is immediate — a field is never left in a row that cannot seat it.
        Assert.Equal(Shell.MergedSearchMode.Field, Shell.Chrome.Resolve(848f, 110f, promoted).SearchMode);
        Assert.Equal(Shell.MergedSearchMode.Icon, Shell.Chrome.Resolve(847f, 110f, promoted).SearchMode);
    }

    [Fact]
    public void Dragging_the_window_edge_across_the_band_commits_one_flip_not_a_flicker()
    {
        // The band is [848, 888). Inside it the mode must depend only on where the drag came FROM, and re-resolving at
        // a standing width must never move it again (the per-frame resolve is a fixed point).
        var state = Shell.Chrome.Resolve(700f, 110f);
        int flips = 0;
        var mode = state.SearchMode;
        for (int pass = 0; pass < 4; pass++)
        {
            bool up = pass % 2 == 0;
            for (float w = up ? 830f : 910f; up ? w <= 910f : w >= 830f; w += up ? 1f : -1f)
            {
                state = Shell.Chrome.Resolve(w, 110f, state);
                Assert.Equal(state, Shell.Chrome.Resolve(w, 110f, state));   // idempotent at a standing width
                if (state.SearchMode != mode) { flips++; mode = state.SearchMode; }
            }
        }
        Assert.Equal(4, flips);   // exactly one per sweep — never two inside a single pass
    }

    [Fact]
    public void A_wider_window_never_takes_the_search_field_away()
    {
        // Monotonicity, now unconditional. This test used to PERMIT a Field → Icon step wherever the fixed budget grew,
        // which is exactly the defect: the trailing actions (1200, +176 DIP) and the profile name (1360, +90) entered on
        // the RAW width with nothing asking whether the row could afford them, so widening past 1200 took the search box
        // away — across ≈[1200, 1244) with three tabs, ≈[1200, 1354) with four — and past 1360 again. Both promotions
        // are budget-checked now, so growing the window is a one-way improvement for the search at every width.
        foreach (var chip in SweepChips)
        foreach (float extent in SweepExtents)
        {
            var prev = Shell.Chrome.Resolve(200f, extent, null, chip);
            for (float w = 201f; w <= 2600f; w += 1f)
            {
                var next = Shell.Chrome.Resolve(w, extent, null, chip);
                Assert.False(prev.SearchMode == Shell.MergedSearchMode.Field
                             && next.SearchMode == Shell.MergedSearchMode.Icon,
                    $"Widening to {w} (extent {extent}, chip {chip}) demoted the field.");
                prev = next;
            }
        }
    }

    // ══ THE PROMOTION RULE: A THRESHOLD IS A PERMISSION, NOT A DECISION (#88) ═══════════════════════════════════════

    [Theory]
    // measured tab extent · the RAW threshold · the width the row can first afford the stage at
    [InlineData(330f, Shell.Layout.ChromeActionsEnterW, 1244f)]   // three tabs: 634 + 280 + 330
    [InlineData(440f, Shell.Layout.ChromeActionsEnterW, 1354f)]   // four tabs:  634 + 280 + 440
    public void The_trailing_actions_wait_until_the_row_can_seat_them_beside_the_field(
        float extent, float raw, float afford)
    {
        // Across the whole band the four buttons stay FOLDED (bell and friends are profile-menu rows; pin is on the tab
        // menu) and the search keeps its field — the trade the ladder always meant to make and the raw threshold broke.
        for (float w = raw; w < afford; w += 1f)
        {
            var c = Shell.Chrome.Resolve(w, extent);
            Assert.False(c.ShowActions, $"Actions entered at {w} (extent {extent}) without the row affording them.");
            Assert.True(c.ActionsInMenu);
            Assert.Equal(Shell.MergedSearchMode.Field, c.SearchMode);
        }
        var at = Shell.Chrome.Resolve(afford, extent);
        Assert.True(at.ShowActions);
        Assert.Equal(Shell.MergedSearchMode.Field, at.SearchMode);   // …and buying them still leaves the field standing
    }

    [Fact]
    public void The_profile_name_waits_for_the_same_check_on_top_of_the_actions()
    {
        // Four tabs: the name's raw threshold is 1360 but the row cannot seat 724 + 280 + 440 until 1444.
        for (float w = Shell.Layout.ChromeNameEnterW; w < 1444f; w += 1f)
        {
            var c = Shell.Chrome.Resolve(w, 440f);
            Assert.False(c.ShowName, $"The name entered at {w} without the row affording it.");
            Assert.Equal(Shell.MergedSearchMode.Field, c.SearchMode);
        }
        var at = Shell.Chrome.Resolve(1444f, 440f);
        Assert.True(at.ShowName);
        Assert.Equal(Shell.MergedSearchMode.Field, at.SearchMode);

        // The common single-tab case is untouched: both stages still land exactly on their own thresholds.
        Assert.False(Shell.Chrome.Resolve(1199f, 110f).ShowActions);
        Assert.True(Shell.Chrome.Resolve(1200f, 110f).ShowActions);
        Assert.False(Shell.Chrome.Resolve(1359f, 110f).ShowName);
        Assert.True(Shell.Chrome.Resolve(1360f, 110f).ShowName);
    }

    [Fact]
    public void The_budget_checked_actions_stage_still_promotes_late_and_demotes_at_once()
    {
        // The hysteresis POLARITY is unchanged by the budget check: a promotion re-resolves at (width − 40), a demotion
        // is immediate — so a resize drag across the new band commits one flip instead of oscillating.
        var narrow = Shell.Chrome.Resolve(Shell.Layout.ChromeActionsEnterW, 440f);
        Assert.False(narrow.ShowActions);

        Assert.False(Shell.Chrome.Resolve(1354f, 440f, narrow).ShowActions);   // at the affordable width: still held
        Assert.False(Shell.Chrome.Resolve(1393f, 440f, narrow).ShowActions);
        var promoted = Shell.Chrome.Resolve(1394f, 440f, narrow);              // …a full reserve past it
        Assert.True(promoted.ShowActions);
        Assert.Equal(Shell.MergedSearchMode.Field, promoted.SearchMode);

        Assert.True(Shell.Chrome.Resolve(1354f, 440f, promoted).ShowActions);  // coming back down it holds…
        Assert.False(Shell.Chrome.Resolve(1353f, 440f, promoted).ShowActions); // …and then goes at once
    }

    // ══ THE AUTH CHIP'S REAL WIDTH (#88) ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void The_avatar_still_costs_what_it_did_and_the_other_three_forms_cost_what_they_are()
    {
        Assert.Equal(Shell.Layout.ChromeProfileChipW, Shell.Chrome.ChipWidth(Shell.FrameRules.ChipForm.Profile));
        Assert.Equal(Shell.Layout.ChromeConnectingChipW, Shell.Chrome.ChipWidth(Shell.FrameRules.ChipForm.Connecting));
        Assert.Equal(Shell.Layout.ChromeReconnectChipW, Shell.Chrome.ChipWidth(Shell.FrameRules.ChipForm.Reconnect));
        Assert.Equal(Shell.Layout.ChromeSignInChipW, Shell.Chrome.ChipWidth(Shell.FrameRules.ChipForm.SignIn));

        // Why guessing the avatar was not a rounding error: every other form overruns it by more than the whole gutter
        // cushion, so the row genuinely overflowed into the clipped tabs island for the duration of a sign-in.
        foreach (var chip in SweepChips)
        {
            if (chip == Shell.FrameRules.ChipForm.Profile) continue;
            Assert.True(Shell.Chrome.ChipWidth(chip) - Shell.Layout.ChromeProfileChipW > 2f * Shell.Layout.ChromeGutterMinW);
        }
    }

    [Theory]
    // the chip on screen · the first width whose lane seats a minimum field beside one tab
    [InlineData(Shell.FrameRules.ChipForm.Profile, 848f)]
    [InlineData(Shell.FrameRules.ChipForm.SignIn, 900f)]
    [InlineData(Shell.FrameRules.ChipForm.Connecting, 906f)]
    [InlineData(Shell.FrameRules.ChipForm.Reconnect, 916f)]
    public void The_field_flip_moves_by_exactly_what_the_chip_on_screen_costs(Shell.FrameRules.ChipForm chip, float flip)
    {
        Assert.Equal(Shell.MergedSearchMode.Icon, Shell.Chrome.Resolve(flip - 1f, 110f, null, chip).SearchMode);
        Assert.Equal(Shell.MergedSearchMode.Field, Shell.Chrome.Resolve(flip, 110f, null, chip).SearchMode);
        // The avatar's 848 is the baseline, and every other form's flip is that plus the DIPs it really occupies —
        // nothing else about the ladder moved.
        Assert.Equal(flip - 848f, Shell.Chrome.ChipWidth(chip) - Shell.Layout.ChromeProfileChipW, 3);
    }

    [Fact]
    public void The_chip_form_is_part_of_the_allocation_so_the_tree_and_the_budget_cannot_disagree()
    {
        // The view reads the form back off the resolved row, so an auth transition is a re-allocation, not a silent
        // 58-DIP overdraft on a row that was already sized for an avatar.
        var live = Shell.Chrome.Resolve(1000f, 110f, null, Shell.FrameRules.ChipForm.Profile);
        var connecting = Shell.Chrome.Resolve(1000f, 110f, live, Shell.FrameRules.ChipForm.Connecting);
        Assert.Equal(Shell.FrameRules.ChipForm.Connecting, connecting.Chip);
        Assert.NotEqual(live, connecting);
        Assert.Equal(Shell.Layout.ChromeConnectingChipW - Shell.Layout.ChromeProfileChipW,
            connecting.FixedBudgetFor() - live.FixedBudgetFor(), 3);
    }

    // ══ THE ESTIMATED EXTENT IS AN UPWARD-ONLY SEED (correct as designed) ═══════════════════════════════════════════

    [Fact]
    public void The_estimated_extent_is_a_pre_measure_seed_and_only_ever_raises()
    {
        // EstimatedTabExtent puts every tab at its 110-DIP floor, so it is optimistic by up to 90 DIP a tab until the
        // strip's own measurement lands. That direction is the right one — the seed RAISES the extent, never lowers it,
        // so the row collapses the search in the same turn a tab is added rather than a frame late.
        Assert.Equal(3f * Shell.Layout.ChromeTabMinW, Shell.Chrome.EstimatedTabExtent(3));
        Assert.True(Shell.Chrome.EstimatedTabExtent(3) <= 3f * Shell.Layout.ChromeTabMaxW);
        Assert.Equal(900f, Shell.Chrome.SeedTabExtent(900f, tabCount: 3, pinnedCount: 0));

        // …and FromWidth IS that seed, nothing more: it is the pre-measure entry point only (Shell.Host's initial
        // ChromeLayout), never a stand-in for the measured path the shell's viewport effect uses.
        Assert.Equal(Shell.Chrome.Resolve(1600f, Shell.Chrome.EstimatedTabExtent(3)), Shell.Chrome.FromWidth(1600f, 3));
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
