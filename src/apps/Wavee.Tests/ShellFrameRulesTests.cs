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
        Assert.Equal(752f, Shell.FrameRules.CardWidth(1400f, 280f, 8f, 360f));
        // A window narrower than its columns clamps at 0 rather than going negative.
        Assert.Equal(0f, Shell.FrameRules.CardWidth(300f, 280f, 8f, 360f));
        // The Zune frame: no pane and no left gap (the page bleeds to the window edge), no rail.
        Assert.Equal(1200f, Shell.FrameRules.CardWidth(1200f, 0f, 0f, 0f));
    }

    [Fact]
    public void The_card_motion_is_one_300_ms_tween_shared_by_the_pane_the_card_and_the_band()
        => Assert.Equal(300f, Shell.FrameRules.CardMotionMs);

    [Fact]
    public void There_is_one_frame_gap_and_the_rail_gap_is_it()
        => Assert.Equal(Shell.FrameRules.FrameGap, Shell.FrameRules.RailGapW);

    [Theory]
    [InlineData(SidebarPaneMode.Expanded, true)]
    [InlineData(SidebarPaneMode.Compact, true)]
    [InlineData(SidebarPaneMode.Minimal, false)]
    public void A_pane_is_docked_beside_the_card_in_the_Expanded_and_Compact_modes_only(SidebarPaneMode mode, bool docked)
        => Assert.Equal(docked, Shell.FrameRules.PaneDocked(mode));

    [Fact]
    public void The_rail_coat_top_equals_the_content_card_top()
    {
        // Both read Shell.StrokeOverhang, whose top is this constant: no offset, so the rail's top lines up with the card's.
        Assert.Equal(0f, Shell.FrameRules.StrokeOverhangTop);
    }

    [Fact]
    public void With_no_pane_docked_the_card_is_flush_with_a_square_corner_and_no_left_stroke()
    {
        // Zune always presents Minimal; Classic and Library do in the Tiny band or with the pane hidden.
        bool docked = Shell.FrameRules.PaneDocked(SidebarPaneMode.Minimal);
        Assert.Equal(0f, Shell.FrameRules.ContentCardX(0f));
        Assert.Equal(default, Shell.FrameRules.ContentCorners(docked));
        Assert.Equal(0f, Shell.FrameRules.ContentCorners(docked).TopLeft);
        // The stroke box is shifted by its own width: the left stroke leaves the card's clip.
        Assert.Equal(-Shell.FrameRules.StrokeW, Shell.FrameRules.StrokeLeftShift(docked));
    }

    [Theory]
    [InlineData(SidebarPaneMode.Expanded)]
    [InlineData(SidebarPaneMode.Compact)]
    public void With_a_docked_pane_the_card_keeps_its_rounded_top_left_corner_and_stroke(SidebarPaneMode mode)
    {
        bool docked = Shell.FrameRules.PaneDocked(mode);
        Assert.Equal(Design.Size.ContentPaneCorners, Shell.FrameRules.ContentCorners(docked));
        Assert.Equal(FluentGpu.Dsl.Radii.Card, Shell.FrameRules.ContentCorners(docked).TopLeft);
        Assert.Equal(0f, Shell.FrameRules.StrokeLeftShift(docked));
    }

    [Fact]
    public void The_Zune_style_presents_no_pane_so_it_is_always_undocked()
    {
        foreach (var band in new[] { SidebarWindowBand.Wide, SidebarWindowBand.Narrow, SidebarWindowBand.Tiny })
        {
            var mode = SidebarPaneModeRules.Resolve(band, userCollapsed: false, editing: false, paneHidden: true);
            Assert.False(Shell.FrameRules.PaneDocked(mode));
        }
    }

    [Fact]
    public void The_chrome_edge_snap_window_is_shorter_than_a_card_tween()
    {
        // Long enough for the edge's commit, short enough that a nav-style switch right after still animates.
        Assert.InRange(Shell.FrameRules.ChromeEdgeSnapMs, 1f, Shell.FrameRules.CardMotionMs - 1f);
    }

    [Fact]
    public void The_Zune_band_inset_is_the_gutter_because_the_band_is_card_relative()
    {
        Assert.Equal(36f, Shell.FrameRules.ZuneBandInset(36f));
        Assert.Equal(32f, Shell.FrameRules.ZuneBandInset(32f));
        Assert.Equal(PageGeometry.GutterWide, Shell.FrameRules.ZuneBandInset(PageGeometry.GutterWide));
    }

    [Theory]
    [InlineData(true, 84f, 0f)]
    [InlineData(false, 84f, 84f)]
    [InlineData(false, 52f, 52f)]
    [InlineData(false, 0f, 0f)]
    [InlineData(true, 0f, 0f)]
    public void The_rail_overlay_starts_under_the_band_only_when_it_floats(bool fits, float band, float expected)
    {
        Assert.Equal(expected, Shell.FrameRules.RailOverlayTop(fits, band));
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
        // The pane is hidden under Zune, so the sidebar column is 0 wide whatever the band; the card starts at the window edge.
        var band = SidebarPaneModeRules.BandOf(w, SidebarWindowBand.Wide);
        var mode = SidebarPaneModeRules.Resolve(band, false, false, paneHidden: true);
        float column = SidebarPaneModeRules.PresentedWidth(mode, 280f, w);
        Assert.Equal(0f, column);

        float railGap = Shell.FrameRules.RailGapWidth(railOpen, fits: true);
        float railReserved = Shell.FrameRules.RailReservedWidth(railOpen, fits: true, 360f);
        float cardW = Shell.FrameRules.CardWidth(w, column, railGap, railReserved);
        float gutter = PageGeometry.GutterFor(cardW);

        float pageTitleX = Shell.FrameRules.ContentCardX(column) + gutter;
        Assert.Equal(0f, Shell.FrameRules.ContentCardX(column));
        // The band is the page column's first child, so its inset is card-relative: card x (0 here) plus the same inset.
        Assert.Equal(pageTitleX, Shell.FrameRules.ContentCardX(column) + Shell.FrameRules.ZuneBandInset(gutter));
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

/// <summary>P10: the Zune title bar. The wordmark stands in for the tab strip at one tab, the search is a compact field
/// before the avatar, Forward is never priced, and the theme toggle left the row in every style.</summary>
public class ShellZuneChromeTests
{
    static readonly float[] Widths = [900f, 1100f, 1400f, 1800f];
    static readonly float[] Extents = [110f, 400f];

    static readonly Shell.FrameRules.ChipForm[] Chips =
    [
        Shell.FrameRules.ChipForm.Profile, Shell.FrameRules.ChipForm.Connecting,
        Shell.FrameRules.ChipForm.Reconnect, Shell.FrameRules.ChipForm.SignIn,
    ];

    [Theory]
    [InlineData(ShellNavStyle.Zune, 1, true)]
    [InlineData(ShellNavStyle.Zune, 0, true)]
    [InlineData(ShellNavStyle.Zune, 2, false)]
    [InlineData(ShellNavStyle.Zune, 7, false)]
    [InlineData(ShellNavStyle.Classic, 1, false)]
    [InlineData(ShellNavStyle.Library, 1, false)]
    [InlineData(ShellNavStyle.Classic, 0, false)]
    public void The_wordmark_replaces_the_strip_only_under_zune_with_at_most_one_tab(ShellNavStyle style, int tabs, bool wordmark)
        => Assert.Equal(wordmark, Shell.FrameRules.ShowsWordmark(style, tabs));

    [Fact]
    public void A_compact_search_is_exactly_the_minimum_and_the_row_still_seats_it()
    {
        foreach (var chip in Chips)
        foreach (float w in Widths)
        foreach (float extent in Extents)
        {
            var compact = Shell.Chrome.Resolve(w, extent, null, chip, zune: true, compactSearch: true);
            if (compact.SearchMode != Shell.MergedSearchMode.Field) continue;
            Assert.Equal(Shell.Layout.ChromeSearchMinW, compact.SearchWidth);
            Assert.True(compact.FootprintFor(extent) <= w,
                $"Compact field at {w} (extent {extent}, chip {chip}) needs {compact.FootprintFor(extent)} DIP.");
        }
    }

    [Fact]
    public void Compact_search_never_yields_a_field_where_the_ladder_yields_an_icon()
    {
        // The compact width is the minimum the field stage is already admitted on, so it changes the width and nothing else.
        foreach (var chip in Chips)
        for (float w = 200f; w <= 2600f; w += 1f)
        foreach (float extent in Extents)
        {
            var classic = Shell.Chrome.Resolve(w, extent, null, chip);
            var compact = Shell.Chrome.Resolve(w, extent, null, chip, zune: false, compactSearch: true);
            Assert.Equal(classic.SearchMode, compact.SearchMode);
            Assert.False(classic.SearchMode == Shell.MergedSearchMode.Icon && compact.SearchMode == Shell.MergedSearchMode.Field);
            Assert.Equal(classic.ShowActions, compact.ShowActions);
            Assert.Equal(classic.ShowName, compact.ShowName);
        }
    }

    [Fact]
    public void The_default_flags_are_the_classic_resolution()
    {
        foreach (var chip in Chips)
        foreach (float w in Widths)
        foreach (float extent in Extents)
            Assert.Equal(Shell.Chrome.Resolve(w, extent, null, chip),
                Shell.Chrome.Resolve(w, extent, null, chip, zune: false, compactSearch: false));
    }

    [Fact]
    public void Zune_never_shows_forward_and_never_prices_it()
    {
        for (float w = 200f; w <= 2600f; w += 1f)
        {
            var z = Shell.Chrome.Resolve(w, 220f, null, Shell.FrameRules.ChipForm.Profile, zune: true);
            Assert.False(z.ShowForward, $"Forward shown under Zune at {w}.");
            // Not pricing Forward can only free room: the field is never LATER than the classic one.
            var classic = Shell.Chrome.Resolve(w, 220f);
            Assert.False(classic.SearchMode == Shell.MergedSearchMode.Field && z.SearchMode == Shell.MergedSearchMode.Icon);
        }
    }

    [Fact]
    public void The_theme_toggle_is_no_longer_in_the_budget()
    {
        // The baseline row: lead, back, forward, add slot, the avatar, two gutters, the drag strip and the caption cluster.
        float expected = Shell.Layout.ChromeBarLeadW + 2f * Shell.Layout.ChromeNavButtonW + Shell.Layout.ChromeAddSlotW
                       + Shell.Layout.ChromeProfileChipW + 2f * Shell.Layout.ChromeGutterMinW
                       + Shell.Layout.ChromeMinDragStripW + Shell.Layout.ChromeCaptionClusterW;
        Assert.Equal(expected, Shell.Chrome.FixedBudget(name: false, actionsInRow: false, forward: true, back: true,
            newTab: true, trailing: true), 3);
        Assert.Equal(414f, expected, 3);
    }

    [Theory]
    [InlineData(false, true, 400f, true)]     // unfocused and empty with room: shown
    [InlineData(true, true, 400f, false)]     // focused: the ghost completion owns the end of the field
    [InlineData(false, false, 400f, false)]   // typed text can never run under the chip
    [InlineData(true, false, 400f, false)]
    public void The_hint_shows_only_while_the_field_is_unfocused_and_empty(bool focused, bool empty, float width, bool shown)
        => Assert.Equal(shown, Shell.Chrome.ShowSearchHint(focused, empty, width));

    [Fact]
    public void The_hint_is_dropped_by_rule_below_the_pill_floor()
    {
        float floor = Shell.Chrome.SearchPillMinW;
        Assert.True(Shell.Chrome.ShowSearchHint(false, true, floor));
        Assert.False(Shell.Chrome.ShowSearchHint(false, true, floor - 1f));
        Assert.Equal("Ctrl+F", Shell.Chrome.SearchHintChord);
    }

    [Fact]
    public void The_row_never_sizes_the_pill_below_its_floor()
    {
        Assert.True(Shell.Layout.ChromeSearchMinW >= Shell.Chrome.SearchPillMinW);
        foreach (float w in Widths)
        foreach (float extent in Extents)
        {
            var c = Shell.Chrome.Resolve(w, extent);
            if (c.SearchMode == Shell.MergedSearchMode.Field) Assert.True(c.SearchWidth >= Shell.Chrome.SearchPillMinW);
        }
    }

    [Fact]
    public void A_non_finite_allocation_takes_the_compact_width()
    {
        Assert.Equal(Shell.Layout.ChromeSearchMinW, Shell.Chrome.FieldWidthFor(float.NaN, float.PositiveInfinity));
        Assert.Equal(Shell.Layout.ChromeSearchMinW, Shell.Chrome.FieldWidthFor(float.PositiveInfinity, float.PositiveInfinity));
        Assert.Equal(Shell.Layout.ChromeSearchMinW, Shell.Chrome.FieldWidthFor(280f, float.PositiveInfinity));
    }
}

// ── F5: the focused pill widens (allocator-authoritative) ─────────────────────────────────────────────────────────────
//
// `Chrome.SearchExpandW` is the width the pill eases to while its field has focus. It is bounded by the RESERVED tab
// cluster, so `FixedBudget + SearchExpandW + LeadClusterW <= width` and the tabs never move; focus is not an input to
// `Resolve`, so focusing the field cannot republish the layout. While expanded the laid-out width comes from the
// allocator (`ExpandedFieldWidth`), never from the elastic lane's measured centre width.
public class ShellChromeSearchExpandTests
{
    static readonly float[] Widths = [900f, 1280f, 1920f];
    static readonly float[] Extents = [110f, 220f, 600f];

    static readonly Shell.FrameRules.ChipForm[] Chips =
    [
        Shell.FrameRules.ChipForm.Profile, Shell.FrameRules.ChipForm.Connecting,
        Shell.FrameRules.ChipForm.Reconnect, Shell.FrameRules.ChipForm.SignIn,
    ];

    static void AssertExpandInvariants(Shell.Chrome c, float width)
    {
        if (c.SearchMode == Shell.MergedSearchMode.Field)
        {
            Assert.InRange(c.SearchExpandW, c.SearchWidth, Shell.Layout.ChromeSearchMaxW);
            Assert.True(c.FixedBudgetFor() + c.SearchExpandW + c.LeadClusterW <= width + 0.001f,
                $"width {width}: {c.FixedBudgetFor()} + {c.SearchExpandW} + {c.LeadClusterW} overruns the row");
        }
        else Assert.Equal(Shell.Layout.ChromeSearchIconW, c.SearchExpandW);
    }

    [Fact]
    public void The_expanded_width_is_seatable_beside_the_reserved_tab_cluster_in_both_styles()
    {
        foreach (var chip in Chips)
        foreach (bool zune in new[] { false, true })
        foreach (float w in Widths)
        foreach (float extent in Extents)
        {
            var c = Shell.Chrome.Resolve(w, extent, null, chip, zune, compactSearch: zune);
            AssertExpandInvariants(c, w);
        }
    }

    [Fact]
    public void The_expanded_width_stays_seatable_after_a_narrow_later_hold()
    {
        // The hold keeps LeadClusterW ABOVE the required extent after the tabs shrink; the bound is the reserved cluster.
        foreach (bool zune in new[] { false, true })
        foreach (float w in Widths)
        {
            var wide = Shell.Chrome.Resolve(w, 600f, null, Shell.FrameRules.ChipForm.Profile, zune, compactSearch: zune);
            // 600 -> 590: a shrink inside the hysteresis band, so the reservation is HELD above the new required extent.
            var held = Shell.Chrome.Resolve(w, 590f, wide, Shell.FrameRules.ChipForm.Profile, zune, compactSearch: zune);
            AssertExpandInvariants(held, w);
            // ...and a shrink past the band releases it; the bound still holds.
            AssertExpandInvariants(Shell.Chrome.Resolve(w, 120f, held, Shell.FrameRules.ChipForm.Profile, zune, compactSearch: zune), w);
        }
    }

    [Fact]
    public void A_wide_zune_window_rests_at_280_and_expands_to_the_ceiling()
    {
        var c = Shell.Chrome.Resolve(1920f, 220f, null, Shell.FrameRules.ChipForm.Profile, zune: true, compactSearch: true);
        Assert.Equal(Shell.MergedSearchMode.Field, c.SearchMode);
        Assert.Equal(280f, c.SearchWidth);
        Assert.Equal(420f, c.SearchExpandW);
        Assert.Equal(420f, Shell.Chrome.ExpandedFieldWidth(c.SearchExpandW, Shell.Chrome.FieldWidthFor(c.SearchWidth, 280f)));
    }

    [Fact]
    public void A_classic_window_widens_beyond_its_rest_width_whatever_the_measured_lane_says()
    {
        var c = Shell.Chrome.Resolve(1280f, 220f);
        Assert.Equal(Shell.MergedSearchMode.Field, c.SearchMode);
        Assert.True(c.SearchExpandW > c.SearchWidth);
        float rest = Shell.Chrome.FieldWidthFor(c.SearchWidth, measuredCentreAvail: c.SearchWidth);
        Assert.Equal(c.SearchWidth, rest);
        Assert.True(Shell.Chrome.ExpandedFieldWidth(c.SearchExpandW, rest) > rest);
    }

    [Fact]
    public void A_lane_of_exactly_280_expands_to_nothing_and_icon_mode_stays_44()
    {
        bool found = false;
        for (float w = 700f; w <= 2600f; w += 1f)
        {
            var c = Shell.Chrome.Resolve(w, 220f);
            if (c.SearchMode != Shell.MergedSearchMode.Field) continue;
            float lane = Shell.Chrome.SearchLane(w, 220f, c.ShowName, c.ShowActions, c.ShowForward, c.ShowBack,
                c.ShowNewTab, c.ShowTrailing, c.Chip);
            if (lane < 280f || lane >= 290f) continue;
            Assert.Equal(280f, c.SearchExpandW);
            found = true;
        }
        Assert.True(found, "no width in the sweep seats a 280 lane");

        var icon = Shell.Chrome.Resolve(700f, naturalTabExtent: 110f);
        Assert.Equal(Shell.MergedSearchMode.Icon, icon.SearchMode);
        Assert.Equal(Shell.Layout.ChromeSearchIconW, icon.SearchExpandW);
    }

    [Fact]
    public void The_expanded_width_ignores_the_measured_lane_and_takes_the_compact_width_for_a_bad_allocation()
    {
        Assert.Equal(Shell.Layout.ChromeSearchMinW, Shell.Chrome.ExpandedFieldWidth(float.NaN, 300f));
        Assert.Equal(Shell.Layout.ChromeSearchMinW, Shell.Chrome.ExpandedFieldWidth(float.PositiveInfinity, 300f));
        // Never narrower than the rest width, never beyond the ceiling.
        Assert.Equal(350f, Shell.Chrome.ExpandedFieldWidth(300f, 350f));
        Assert.Equal(420f, Shell.Chrome.ExpandedFieldWidth(10_000f, 280f));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void The_pill_expands_while_focused_or_while_a_suggestions_list_is_open(bool focused, bool listOpen, bool expands)
        => Assert.Equal(expands, Shell.FrameRules.SearchExpands(focused, listOpen));

    [Fact]
    public void The_hint_chord_is_unchanged()
        => Assert.Equal("Ctrl+F", Shell.Chrome.SearchHintChord);
}
