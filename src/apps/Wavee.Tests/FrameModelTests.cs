// ── Wavee.Tests/FrameModelTests.cs — the frame's pure decisions (Shell/FrameModel.cs, PR C8) ───────────────────────
//
// `FrameRoot` used to seed the sidebar's band and mode, derive the band from the window width and resolve the mode and the
// presented width inline. These tests drive the REAL decisions (`FrameModel`) over the sidebar's own constants, so any
// future tier policy that replaces the constants changes one place and these say what the frame does with them today.

using Xunit;

namespace Wavee.Tests;

public class FrameModelTests
{
    const float Expanded = 300f;

    // ── the first values ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1180f, SidebarWindowBand.Wide, SidebarPaneMode.Expanded)]
    [InlineData(700f, SidebarWindowBand.Wide, SidebarPaneMode.Expanded)]
    [InlineData(600f, SidebarWindowBand.Narrow, SidebarPaneMode.Compact)]     // a fresh launch at 600 is Narrow: the entering edge applies
    [InlineData(500f, SidebarWindowBand.Tiny, SidebarPaneMode.Minimal)]
    public void A_launch_seeds_the_band_and_mode_for_its_first_width(float width, SidebarWindowBand band, SidebarPaneMode mode)
    {
        var pane = FrameModel.Seed(new WindowFacts(width, 760f), userCollapsed: false, editing: false, Expanded);
        Assert.Equal(band, pane.Band);
        Assert.Equal(mode, pane.Mode);
        Assert.Equal(SidebarPaneModeRules.PresentedWidth(mode, Expanded, width), pane.PresentedWidth);
        Assert.Equal(1f, pane.Fade);
    }

    [Fact]
    public void The_users_collapse_decides_only_in_the_wide_band_and_editing_overrides_it()
    {
        var collapsed = FrameModel.Seed(new WindowFacts(1180f, 760f), userCollapsed: true, editing: false, Expanded);
        Assert.Equal(SidebarPaneMode.Compact, collapsed.Mode);

        var editing = FrameModel.Seed(new WindowFacts(1180f, 760f), userCollapsed: true, editing: true, Expanded);
        Assert.Equal(SidebarPaneMode.Expanded, editing.Mode);

        var narrow = FrameModel.Seed(new WindowFacts(600f, 760f), userCollapsed: false, editing: false, Expanded);
        Assert.Equal(SidebarPaneMode.Compact, narrow.Mode);   // a forced mode never writes the preference
    }

    // ── the band follows the width, with hysteresis ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarWindowBand.Wide, 650f, SidebarWindowBand.Narrow)]
    [InlineData(SidebarWindowBand.Narrow, 680f, SidebarWindowBand.Narrow)]    // 660 < w < 700: no flip back yet
    [InlineData(SidebarWindowBand.Narrow, 700f, SidebarWindowBand.Wide)]
    [InlineData(SidebarWindowBand.Narrow, 500f, SidebarWindowBand.Tiny)]
    [InlineData(SidebarWindowBand.Tiny, 550f, SidebarWindowBand.Tiny)]       // leaves at 568
    [InlineData(SidebarWindowBand.Tiny, 568f, SidebarWindowBand.Narrow)]
    [InlineData(SidebarWindowBand.Tiny, 0f, SidebarWindowBand.Tiny)]          // a minimized window never moves it
    public void The_band_is_the_sidebars_hysteretic_function_of_the_width(SidebarWindowBand from, float width, SidebarWindowBand expected)
        => Assert.Equal(expected, FrameModel.NextBand(new WindowFacts(width, 700f), from));

    [Theory]
    [InlineData(SidebarWindowBand.Wide)]
    [InlineData(SidebarWindowBand.Narrow)]
    [InlineData(SidebarWindowBand.Tiny)]
    public void Only_a_band_with_no_overlay_pane_closes_the_overlay(SidebarWindowBand band)
        => Assert.Equal(!SidebarPaneModeRules.HasOverlay(band), FrameModel.ClosesOverlay(band));

    // ── the one presentation decision ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarWindowBand.Wide, false, false)]
    [InlineData(SidebarWindowBand.Wide, true, true)]
    [InlineData(SidebarWindowBand.Narrow, true, false)]    // no drag is presented where the seam is hidden
    [InlineData(SidebarWindowBand.Tiny, true, false)]
    public void A_drag_is_presented_only_while_dragging_in_the_wide_band(SidebarWindowBand band, bool dragging, bool expected)
        => Assert.Equal(expected, FrameModel.PresentsDrag(dragging, band));

    [Fact]
    public void Resting_presents_the_resolved_mode_at_full_fade()
    {
        var pane = FrameModel.Present(new WindowFacts(1180f, 760f), SidebarWindowBand.Wide, userCollapsed: false, editing: false,
            Expanded, dragging: false, seam: 0f);
        Assert.Equal(SidebarPaneMode.Expanded, pane.Mode);
        Assert.Equal(Expanded, pane.PresentedWidth);
        Assert.Equal(1f, pane.Fade);
    }

    [Theory]
    [InlineData(320f)]    // a plain resize of the expanded pane
    [InlineData(100f)]    // dragged below the rail edge: collapses to the rail
    [InlineData(150f)]    // between the rail edge and the minimum: clamped, fading
    public void A_live_seam_drag_presents_the_tracked_seam_and_never_the_preference(float seam)
    {
        var state = new SidebarResizeRules.State(false, Expanded);
        var live = SidebarResizeRules.Track(seam, in state, false);

        var pane = FrameModel.Present(new WindowFacts(1180f, 760f), SidebarWindowBand.Wide, userCollapsed: false, editing: false,
            Expanded, dragging: true, seam);

        Assert.Equal(live.Collapsed ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded, pane.Mode);
        Assert.Equal(live.PresentedWidth, pane.PresentedWidth);
        Assert.Equal(live.Fade, pane.Fade);
    }

    [Fact]
    public void A_drag_where_the_seam_is_hidden_presents_the_resolved_mode()
    {
        var pane = FrameModel.Present(new WindowFacts(600f, 760f), SidebarWindowBand.Narrow, userCollapsed: false, editing: false,
            Expanded, dragging: true, seam: 320f);
        Assert.Equal(SidebarPaneMode.Compact, pane.Mode);
        Assert.Equal(1f, pane.Fade);
    }

    // ── the chrome ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Video.SurfacePlacement.Docked, false, true)]
    [InlineData(Video.SurfacePlacement.Docked, true, false)]
    [InlineData(Video.SurfacePlacement.Fullscreen, false, false)]
    [InlineData(Video.SurfacePlacement.Fullscreen, true, false)]
    public void Fullscreen_video_or_the_immersive_stage_collapses_the_chrome(Video.SurfacePlacement resolved, bool immersive, bool mounted)
        => Assert.Equal(mounted, FrameModel.ChromeMounted(resolved, immersive));

    // ── the unknown-route note ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_route_is_reported_once_per_key()
    {
        string key = "unknown|" + Guid.NewGuid().ToString("N");
        var seen = new List<string>();
        var before = FrameModel.UnknownRouteSink;
        FrameModel.UnknownRouteSink = seen.Add;
        try
        {
            Assert.True(FrameModel.NoteUnknownRoute(key));
            Assert.False(FrameModel.NoteUnknownRoute(key));
            Assert.Equal(new[] { key }, seen);
        }
        finally { FrameModel.UnknownRouteSink = before; }
    }
}
