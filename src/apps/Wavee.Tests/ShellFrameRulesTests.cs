// ── Wavee.Tests/ShellFrameRulesTests.cs — the frame's geometry, precedence and gating rules ──────────────────────────
//
// Wave 4 stage B's gate for `Shell.FrameRules` (Shell/Shell.cs §12): every decision `Shell.UI.cs` would otherwise make
// inline. The two that matter most are the ones ch 18 §9 records as regressions-in-waiting:
//
//   THE DRAG PEEK IS IN THE PANE WIDTH. A drag that starts on a collapsed rail presents the pane expanded; the column is
//   clipped, so a width derived from "presented compact" alone renders 56 DIP of art with every label cut off (W12).
//
//   THE AUTO-ZOOM LOOP DETECTS A MANUAL MOVE AGAINST ITS OWN LAST PICK. Comparing the live zoom against the suggestion
//   instead makes every Ctrl+± silently undone on the next resize tick.

using FluentGpu.Animation;
using Xunit;

namespace Wavee.Tests;

public class ShellFrameGeometryTests
{
    [Fact]
    public void A_drag_peek_presents_the_expanded_width_even_while_a_rail()
    {
        // Resting on a Large rail (presented 80): a peek drag shows the expanded pane at its remembered width.
        Assert.Equal(80f, Shell.FrameRules.SidebarPaneWidth(dragPeek: false, expanded: 300f, presented: 80f));
        Assert.Equal(300f, Shell.FrameRules.SidebarPaneWidth(dragPeek: true, expanded: 300f, presented: 80f));
        // Expanded and yielding to the content floor (756 window): the column follows the presented width.
        Assert.Equal(276f, Shell.FrameRules.SidebarPaneWidth(dragPeek: false, expanded: 300f, presented: 276f));
    }

    [Fact]
    public void The_sidebar_seam_vanishes_only_outside_the_wide_band()
    {
        Assert.Equal(0f, Shell.FrameRules.SidebarSeamWidth(seamVisible: false));
        Assert.Equal(Shell.FrameRules.SeamStripW, Shell.FrameRules.SidebarSeamWidth(seamVisible: true));
    }

    [Fact]
    public void The_rail_reserves_its_gap_and_width_only_while_inline()
    {
        Assert.Equal(Shell.FrameRules.RailGapW, Shell.FrameRules.RailGapWidth(open: true, fits: true));
        Assert.Equal(0f, Shell.FrameRules.RailGapWidth(open: true, fits: false));
        Assert.Equal(0f, Shell.FrameRules.RailGapWidth(open: false, fits: true));
        Assert.Equal(340f, Shell.FrameRules.RailReservedWidth(open: true, fits: true, 340f));
        Assert.Equal(0f, Shell.FrameRules.RailReservedWidth(open: true, fits: false, 340f));
        Assert.Equal(0f, Shell.FrameRules.RailReservedWidth(open: false, fits: true, 340f));
    }

    [Fact]
    public void Only_an_open_rail_that_does_not_fit_floats()
    {
        Assert.True(Shell.FrameRules.RailFloats(open: true, fits: false));
        Assert.False(Shell.FrameRules.RailFloats(open: true, fits: true));
        Assert.False(Shell.FrameRules.RailFloats(open: false, fits: false));
    }

    [Fact]
    public void The_rail_seam_sits_on_the_content_side_of_the_rail_edge_and_only_while_open()
    {
        Assert.Equal(1200f - 340f - Shell.FrameRules.SeamStripW, Shell.FrameRules.RailSeamX(1200f, 340f));
        Assert.Equal(Shell.FrameRules.SeamStripW, Shell.FrameRules.RailSeamWidth(open: true));
        Assert.Equal(0f, Shell.FrameRules.RailSeamWidth(open: false));
    }

    [Fact]
    public void The_card_width_is_the_viewport_less_the_columns_beside_it_and_never_negative()
    {
        // 1400 window, 280 pane, an inline 360 rail with its 8 gap: the seam strips are overlays and take no width.
        Assert.Equal(752f, Shell.FrameRules.CardWidth(1400f, 280f, 0f, 8f, 360f));
        // A window narrower than its columns clamps at 0 rather than going negative.
        Assert.Equal(0f, Shell.FrameRules.CardWidth(300f, 280f, 8f, 8f, 360f));
        // The Zune frame: no pane, an 8 lead gap, no rail.
        Assert.Equal(1192f, Shell.FrameRules.CardWidth(1200f, 0f, 8f, 0f, 0f));
    }

    [Fact]
    public void The_card_motion_is_one_300_ms_tween_shared_by_the_pane_the_card_and_the_band()
        => Assert.Equal(300f, Shell.FrameRules.CardMotionMs);

    [Fact]
    public void There_is_one_frame_gap_and_the_rail_gap_is_it()
        => Assert.Equal(Shell.FrameRules.FrameGap, Shell.FrameRules.RailGapW);

    [Fact]
    public void Only_the_Zune_style_has_a_content_lead_gap_and_it_is_the_frame_gap()
    {
        Assert.Equal(Shell.FrameRules.FrameGap, Shell.FrameRules.ContentLeadGap(ShellNavStyle.Zune));
        Assert.Equal(0f, Shell.FrameRules.ContentLeadGap(ShellNavStyle.Classic));
        Assert.Equal(0f, Shell.FrameRules.ContentLeadGap(ShellNavStyle.Library));
    }

    [Fact]
    public void The_content_card_has_no_top_left_corner_under_zune()
    {
        Assert.Equal(default, Shell.FrameRules.ContentCorners(ShellNavStyle.Zune));
        Assert.Equal(Design.Size.ContentPaneCorners, Shell.FrameRules.ContentCorners(ShellNavStyle.Classic));
        Assert.Equal(Design.Size.ContentPaneCorners, Shell.FrameRules.ContentCorners(ShellNavStyle.Library));
    }

    [Fact]
    public void The_chrome_edge_snap_window_is_shorter_than_a_card_tween()
    {
        // Long enough for the edge's commit, short enough that a nav-style switch right after still animates.
        Assert.InRange(Shell.FrameRules.ChromeEdgeSnapMs, 1f, Shell.FrameRules.CardMotionMs - 1f);
    }

    [Fact]
    public void The_Zune_band_inset_is_the_frame_gap_plus_the_gutter()
    {
        Assert.Equal(40f, Shell.FrameRules.ZuneBandInset(32f));
        Assert.Equal(Shell.FrameRules.FrameGap + PageGeometry.GutterWide, Shell.FrameRules.ZuneBandInset(PageGeometry.GutterWide));
    }

    [Theory]
    [InlineData(500f, false)]
    [InlineData(500f, true)]
    [InlineData(700f, false)]
    [InlineData(700f, true)]
    [InlineData(1200f, false)]
    [InlineData(1200f, true)]
    public void Under_Zune_the_first_pivot_word_and_the_page_title_share_an_x(float w, bool railOpen)
    {
        // The pane is hidden under Zune, so the sidebar column is 0 wide whatever the band; the card starts at the lead gap.
        var band = SidebarPaneModeRules.BandOf(w, SidebarWindowBand.Wide);
        var mode = SidebarPaneModeRules.Resolve(band, false, false, paneHidden: true);
        float column = SidebarPaneModeRules.PresentedWidth(mode, 280f, w);
        Assert.Equal(0f, column);

        float railGap = Shell.FrameRules.RailGapWidth(railOpen, fits: true);
        float railReserved = Shell.FrameRules.RailReservedWidth(railOpen, fits: true, 360f);
        float lead = Shell.FrameRules.ContentLeadGap(ShellNavStyle.Zune);
        float cardW = Shell.FrameRules.CardWidth(w, column, lead, railGap, railReserved);
        float gutter = PageGeometry.GutterFor(cardW);

        float pageTitleX = column + lead + gutter;
        Assert.Equal(pageTitleX, Shell.FrameRules.ZuneBandInset(gutter));
    }

    [Theory]
    [InlineData(Video.SurfacePlacement.None, false, true)]
    [InlineData(Video.SurfacePlacement.Docked, false, true)]
    [InlineData(Video.SurfacePlacement.Floating, false, true)]
    [InlineData(Video.SurfacePlacement.Detached, false, true)]
    [InlineData(Video.SurfacePlacement.Fullscreen, false, false)]
    [InlineData(Video.SurfacePlacement.None, true, false)]
    [InlineData(Video.SurfacePlacement.Docked, true, false)]
    [InlineData(Video.SurfacePlacement.Floating, true, false)]
    [InlineData(Video.SurfacePlacement.Detached, true, false)]
    [InlineData(Video.SurfacePlacement.Fullscreen, true, false)]
    public void Fullscreen_video_or_the_fullscreen_stage_collapses_the_chrome_and_nothing_else_does(
        Video.SurfacePlacement resolved, bool immersive, bool mounted)
        => Assert.Equal(mounted, Shell.FrameRules.ChromeMounted(resolved, immersive));
}

public class ShellFrameKeyRulesTests
{
    [Fact]
    public void Escape_is_nothing_once_handled_or_while_the_palette_owns_it()
    {
        Assert.Equal(Shell.FrameRules.EscapeAction.None, Shell.FrameRules.Escape(handled: true, false, true, true));
        Assert.Equal(Shell.FrameRules.EscapeAction.None, Shell.FrameRules.Escape(false, paletteOpen: true, true, true));
        Assert.Equal(Shell.FrameRules.EscapeAction.None, Shell.FrameRules.Escape(false, false, false, false));
    }

    [Fact]
    public void Escape_closes_immersive_lyrics_before_video_fullscreen()
    {
        Assert.Equal(Shell.FrameRules.EscapeAction.CloseImmersiveLyrics,
            Shell.FrameRules.Escape(false, false, immersiveLyrics: true, videoFullscreen: true));
        Assert.Equal(Shell.FrameRules.EscapeAction.ExitVideoFullscreen,
            Shell.FrameRules.Escape(false, false, immersiveLyrics: false, videoFullscreen: true));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Space_toggles_playback_only_when_nobody_took_it_and_no_editor_has_focus(bool handled, bool editor, bool expected)
        => Assert.Equal(expected, Shell.FrameRules.SpaceTogglesPlayback(handled, editor));

    [Fact]
    public void F11_opens_the_stage_when_there_is_no_video_to_toggle()
    {
        Assert.Equal(Shell.FrameRules.FullscreenToggle.OpenStage, Shell.FrameRules.F11(isFullscreen: false, videoActive: false, stageUp: false));
        Assert.Equal(Shell.FrameRules.FullscreenToggle.Enter, Shell.FrameRules.F11(isFullscreen: false, videoActive: true, stageUp: false));
        Assert.Equal(Shell.FrameRules.FullscreenToggle.Exit, Shell.FrameRules.F11(isFullscreen: true, videoActive: false, stageUp: false));
    }

    [Fact]
    public void F11_closes_the_stage_before_anything_else()
    {
        Assert.Equal(Shell.FrameRules.FullscreenToggle.CloseStage, Shell.FrameRules.F11(isFullscreen: false, videoActive: false, stageUp: true));
        Assert.Equal(Shell.FrameRules.FullscreenToggle.CloseStage, Shell.FrameRules.F11(isFullscreen: true, videoActive: true, stageUp: true));
        Assert.Equal(Shell.FrameRules.FullscreenToggle.CloseStage, Shell.FrameRules.F11(isFullscreen: true, videoActive: false, stageUp: true));
        Assert.Equal(Shell.FrameRules.FullscreenToggle.CloseStage, Shell.FrameRules.F11(isFullscreen: false, videoActive: true, stageUp: true));
    }

    [Fact]
    public void A_search_mode_flip_hands_the_caret_over_only_when_the_user_was_in_the_search()
    {
        var field = Shell.MergedSearchMode.Field;
        var icon = Shell.MergedSearchMode.Icon;
        Assert.True(Shell.FrameRules.ReissueSearchFocus(field, icon, fieldFocused: true, flyoutOpen: false));
        Assert.False(Shell.FrameRules.ReissueSearchFocus(field, icon, fieldFocused: false, flyoutOpen: true));
        Assert.True(Shell.FrameRules.ReissueSearchFocus(icon, field, fieldFocused: false, flyoutOpen: true));
        Assert.False(Shell.FrameRules.ReissueSearchFocus(icon, field, fieldFocused: true, flyoutOpen: false));
        Assert.False(Shell.FrameRules.ReissueSearchFocus(field, field, fieldFocused: true, flyoutOpen: true));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(5, 5, false)]
    [InlineData(8, 8, false)]
    [InlineData(9, 8, true)]
    [InlineData(200, 8, true)]
    public void The_history_flyout_caps_at_eight_and_offers_view_all_beyond(int stack, int rows, bool hasMore)
        => Assert.Equal((rows, hasMore), Shell.FrameRules.HistoryMenu(stack));

    [Fact]
    public void The_history_flyout_lists_the_most_recent_first()
    {
        Assert.Equal(4, Shell.FrameRules.HistoryMenuIndex(stackCount: 5, row: 0));
        Assert.Equal(0, Shell.FrameRules.HistoryMenuIndex(stackCount: 5, row: 4));
    }
}

public class ShellFrameBodyRulesTests
{
    [Fact]
    public void A_pin_id_is_the_route_key_screened_by_the_sidebar()
    {
        Assert.Equal("search", Shell.FrameRules.PinIdFor(new Shell.Route(Shell.RouteKind.Search)));
        Assert.Null(Shell.FrameRules.PinIdFor(new Shell.Route(Shell.RouteKind.Home)));   // Home is always first, never a pin (Q1a)
        Assert.Null(Shell.FrameRules.PinIdFor(new Shell.Route(Shell.RouteKind.Settings)));   // a tool, never a pin
        Assert.Null(Shell.FrameRules.PinIdFor(Shell.Route.None));
        const string album = "album:spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE";
        Assert.Equal(album, Shell.FrameRules.PinIdFor(Shell.Parse(album, "OK Computer")));
    }

    [Fact]
    public void A_known_destination_without_a_page_is_empty_never_not_found()
    {
        var home = new Shell.Route(Shell.RouteKind.Home);
        Assert.Equal(Shell.FrameRules.BodyKind.Page, Shell.FrameRules.BodyFor(home, hasPage: true, developerMode: false));
        Assert.Equal(Shell.FrameRules.BodyKind.Empty, Shell.FrameRules.BodyFor(home, hasPage: false, developerMode: false));
        Assert.Equal(Shell.FrameRules.BodyKind.NotFound, Shell.FrameRules.BodyFor(Shell.Route.None, hasPage: false, developerMode: true));
    }

    [Fact]
    public void A_developer_route_is_not_found_with_developer_mode_off()
    {
        var diag = new Shell.Route(Shell.RouteKind.PlaybackDiagnostics);
        Assert.Equal(Shell.FrameRules.BodyKind.NotFound, Shell.FrameRules.BodyFor(diag, hasPage: true, developerMode: false));
        Assert.Equal(Shell.FrameRules.BodyKind.Page, Shell.FrameRules.BodyFor(diag, hasPage: true, developerMode: true));
    }

    [Fact]
    public void The_stage_claim_is_cleared_only_off_module_routes_and_only_when_set()
    {
        Assert.True(Shell.FrameRules.ClearsStagePlayable(new Shell.Route(Shell.RouteKind.Home), "spotify:track:x"));
        Assert.False(Shell.FrameRules.ClearsStagePlayable(new Shell.Route(Shell.RouteKind.Home), ""));   // value-gated
        Assert.False(Shell.FrameRules.ClearsStagePlayable(new Shell.Route(Shell.RouteKind.Module), "spotify:track:x"));
    }

    [Theory]
    [InlineData(Shell.AuthState.Live, Shell.FrameRules.ChipForm.Profile, false)]
    [InlineData(Shell.AuthState.Connecting, Shell.FrameRules.ChipForm.Connecting, false)]
    [InlineData(Shell.AuthState.Offline, Shell.FrameRules.ChipForm.Reconnect, true)]
    [InlineData(Shell.AuthState.SignInRequired, Shell.FrameRules.ChipForm.SignIn, false)]
    public void The_identity_form_and_the_offline_strip_follow_the_auth_fold(Shell.AuthState auth,
        Shell.FrameRules.ChipForm form, bool strip)
    {
        Assert.Equal(form, Shell.FrameRules.ChipFor(auth));
        Assert.Equal(strip, Shell.FrameRules.ShowsOfflineStrip(auth));
    }
}

public class ShellAutoZoomLoopTests
{
    // 3200 × 1800 base DIPs is exactly twice the design box on both axes: the policy suggests 200 %.
    const float BigW = 3200f, BigH = 1800f;

    [Fact]
    public void Manual_mode_and_a_degenerate_extent_touch_nothing()
    {
        var manual = Shell.FrameRules.AutoZoom(ZoomAutoMode.Manual, BigW, BigH, liveZoom: 1f, lastAuto: 0f, seeded: false);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.None, manual.Kind);
        Assert.False(manual.Seeded);
        var degenerate = Shell.FrameRules.AutoZoom(ZoomAutoMode.Auto, 0f, BigH, 1f, 0f, false);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.None, degenerate.Kind);
    }

    [Fact]
    public void A_suggestion_that_already_holds_seeds_the_loop_without_applying()
    {
        var d = Shell.FrameRules.AutoZoom(ZoomAutoMode.Auto, 1920f, 1080f, liveZoom: 1f, lastAuto: 0f, seeded: false);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.None, d.Kind);
        Assert.True(d.Seeded);
        Assert.Equal(1f, d.LastAuto);
    }

    [Fact]
    public void A_bigger_display_applies_and_its_own_reentrant_tick_is_a_no_op()
    {
        var apply = Shell.FrameRules.AutoZoom(ZoomAutoMode.Auto, BigW, BigH, liveZoom: 1f, lastAuto: 1f, seeded: true);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.Apply, apply.Kind);
        Assert.Equal(2f, apply.Zoom);
        Assert.Equal(2f, apply.LastAuto);

        // Our own SetZoom re-fires the effect: the base extent is zoom-invariant, so the same suggestion comes back.
        var again = Shell.FrameRules.AutoZoom(ZoomAutoMode.Auto, BigW, BigH, liveZoom: apply.Zoom, apply.LastAuto, apply.Seeded);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.None, again.Kind);
    }

    [Fact]
    public void Someone_else_moving_the_zoom_pins_manual_instead_of_being_clobbered()
    {
        // Auto last applied 200 %; a Ctrl+− took the live zoom to 175 % while the suggestion is still 200 %.
        var d = Shell.FrameRules.AutoZoom(ZoomAutoMode.Auto, BigW, BigH, liveZoom: 1.75f, lastAuto: 2f, seeded: true);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.PinManual, d.Kind);
        Assert.Equal(1.75f, d.Zoom);
    }

    [Fact]
    public void A_monitor_hop_while_auto_owns_the_zoom_applies_the_new_suggestion()
    {
        var d = Shell.FrameRules.AutoZoom(ZoomAutoMode.Auto, 1920f, 1080f, liveZoom: 2f, lastAuto: 2f, seeded: true);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.Apply, d.Kind);
        Assert.Equal(1f, d.Zoom);
    }

    [Fact]
    public void Dense_may_suggest_below_one_hundred_percent()
    {
        var d = Shell.FrameRules.AutoZoom(ZoomAutoMode.Dense, 1200f, 675f, liveZoom: 1f, lastAuto: 0f, seeded: false);
        Assert.Equal(Shell.FrameRules.ZoomStepKind.Apply, d.Kind);
        Assert.Equal(0.75f, d.Zoom);
    }
}

// The clipped "Sear" stub (#88): a MOUNTED search field must never be laid out at the icon's 44 DIP, no matter which
// frame it renders on relative to the allocator flipping `SearchMode`. `SearchField.Render()` (Shell.Masthead.UI.cs)
// used to size itself with `Chrome.LaidOutSearchWidth`, which dispatches on `SearchMode` and answers 44 outright once
// the mode is Icon — reachable even while the field is still the thing on screen, because the field's own re-render
// and the host's mount/unmount decision are driven by different signals (`ChromeLayout` vs. `ContentVersion`) and can
// disagree for a frame. The fix is a FORM-VS-WIDTH split: a field view asks `Chrome.FieldWidthFor` directly, which
// never reads `SearchMode` and floors even the icon form's own published width back up to `ChromeSearchMinW`.
public class ShellChromeSearchFieldWidthTests
{
    static readonly Shell.FrameRules.ChipForm[] Chips =
    [
        Shell.FrameRules.ChipForm.Profile, Shell.FrameRules.ChipForm.Connecting,
        Shell.FrameRules.ChipForm.Reconnect, Shell.FrameRules.ChipForm.SignIn,
    ];

    // A field view's own candidate measurements: the stub value the bug actually produced, degenerate inputs a bad
    // frame could hand back, and a spread either side of the floor and the ceiling.
    static readonly float[] Measurements =
        [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -50f, 0f, 40f, 44f, 279f, 280f, 320f, 420f, 10_000f];

    [Fact]
    public void A_field_view_is_never_laid_out_below_the_floor_for_any_allocation_the_pressure_allocator_can_produce()
    {
        // Sweeps every width band the allocator resolves to — including the ones that land on `MergedSearchMode.Icon`,
        // exactly the allocation that used to hand a still-mounted `SearchField` the 44-DIP magnifier width. The test
        // calls `Chrome.FieldWidthFor` the same way `SearchField.Render()` now does: it is never told the mode, only
        // the allocation's own `SearchWidth` and a candidate measurement.
        foreach (var chip in Chips)
        for (float w = 200f; w <= 2600f; w += 25f)
        {
            var c = Shell.Chrome.Resolve(w, 220f, null, chip);
            foreach (float measured in Measurements)
            {
                float fieldWidth = Shell.Chrome.FieldWidthFor(c.SearchWidth, measured);
                Assert.InRange(fieldWidth, Shell.Layout.ChromeSearchMinW, Shell.Layout.ChromeSearchMaxW);
            }
        }
    }

    [Fact]
    public void The_icon_forms_own_published_width_floors_back_up_when_read_as_a_field()
    {
        // The concrete regression, isolated: an allocation resolved to Icon mode publishes SearchWidth == 44 DIP
        // (`Chrome.SearchWidthFor`'s `if (!stage.Field) return Layout.ChromeSearchIconW;`). Feeding that value straight
        // into `FieldWidthFor` — precisely what a stray field-side read of it does on the frame `SearchMode` flips
        // ahead of the host unmounting the field — must clamp it back up to the floor, never lay out a real 44-DIP
        // text field.
        var icon = Shell.Chrome.Resolve(700f, naturalTabExtent: 110f);
        Assert.Equal(Shell.MergedSearchMode.Icon, icon.SearchMode);
        Assert.Equal(Shell.Layout.ChromeSearchIconW, icon.SearchWidth);

        float fieldWidth = Shell.Chrome.FieldWidthFor(icon.SearchWidth, measuredCentreAvail: icon.SearchWidth);
        Assert.Equal(Shell.Layout.ChromeSearchMinW, fieldWidth);
    }

    [Fact]
    public void Form_and_width_cannot_disagree_so_there_is_only_one_reading_to_key_the_mount_decision_on()
    {
        // `CenterIsland` mounts/unmounts the field off `SearchMode`; nothing else in the allocation is allowed to say
        // something different, or a caller reading the "other" field could be one frame stale relative to the mount
        // decision without any test ever noticing. Pin the single invariant that removes that possibility: the icon
        // form ALWAYS publishes exactly the icon width, and the field form NEVER publishes below the floor — so
        // `SearchMode` and `SearchWidth` are one fact, not two that could drift apart.
        foreach (var chip in Chips)
        for (float w = 200f; w <= 2600f; w += 10f)
        {
            var c = Shell.Chrome.Resolve(w, 220f, null, chip);
            if (c.SearchMode == Shell.MergedSearchMode.Icon)
                Assert.Equal(Shell.Layout.ChromeSearchIconW, c.SearchWidth);
            else
                Assert.InRange(c.SearchWidth, Shell.Layout.ChromeSearchMinW, Shell.Layout.ChromeSearchMaxW);
        }
    }
}

public class ShellHoistSettleTests
{
    [Fact]
    public void The_hoist_lands_after_the_card_tween()
        => Assert.True(Shell.FrameRules.HoistSettleMs > Shell.FrameRules.CardMotionMs);

    [Fact]
    public void The_hoist_never_lands_while_a_frame_motion_is_in_flight()
    {
        Assert.True(Shell.FrameRules.HoistSettleMs >= MotionTok.PaneOpen.DurationMs);
        Assert.True(Shell.FrameRules.HoistSettleMs >= MotionTok.PaneClose.DurationMs);
    }
}
