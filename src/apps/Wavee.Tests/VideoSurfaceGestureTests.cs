// ── Wavee.Tests/VideoSurfaceGestureTests.cs — the video surfaces' stage-B rules ─────────────────────────────────────────
//
// The rules `Video.cs` gained when the surfaces were ported (stage B lifted them out of 0.2.9's `InWindowVideoPip` and
// `WaveeShell` so `Video.UI.cs` decides nothing): `PipGesture` (drag clamp, eight-zone resize, the layout reservation,
// the content refit), `Scrub` (a preview is a COARSE seek through the planner), `Joining` (no spinner under the join
// budget) and `FullscreenEntry` (the focus-steal guard). Pure values only.

using Wavee;
using Xunit;

using static Wavee.Video;

namespace Wavee.Tests;

public class VideoPipGestureTests
{
    const float VpW = 1440f, VpH = 900f;

    [Fact]
    public void A_drag_is_clamped_sixteen_from_every_edge_and_above_the_player_bar()
    {
        Assert.Equal(1064f, PipGesture.ClampX(5000f, VpW, 360f));
        Assert.Equal(16f, PipGesture.ClampX(-40f, VpW, 360f));
        Assert.Equal(610f, PipGesture.ClampY(5000f, VpH, 202f));
        Assert.Equal(16f, PipGesture.ClampY(-40f, VpH, 202f));
    }

    [Fact]
    public void A_card_wider_than_the_window_pins_to_the_margin_instead_of_throwing()
    {
        Assert.Equal(16f, PipGesture.ClampX(500f, 300f, 360f));
        Assert.Equal(16f, PipGesture.ClampY(500f, 200f, 202f));
    }

    [Fact]
    public void The_anchored_home_is_already_inside_the_drag_clamp()
    {
        var (x, y) = Pip.Anchor(360f, 202f, VpW, VpH);
        Assert.Equal((1064f, 610f), (x, y));
        Assert.Equal(x, PipGesture.ClampX(x, VpW, 360f));
        Assert.Equal(y, PipGesture.ClampY(y, VpH, 202f));
    }

    [Fact]
    public void An_anchored_card_reserves_its_height_plus_the_gap_and_a_placed_one_reserves_nothing()
    {
        Assert.Equal(218f, PipGesture.Reserve(mounted: true, placed: false, 202f));
        Assert.Equal(0f, PipGesture.Reserve(mounted: true, placed: true, 202f));
        Assert.Equal(0f, PipGesture.Reserve(mounted: false, placed: false, 202f));
    }

    [Fact]
    public void The_height_follows_the_content_until_the_user_sizes_it_then_only_on_a_new_shape()
    {
        Assert.True(PipGesture.ShouldRefit(userSized: false, 0f, 0.5625f));
        Assert.False(PipGesture.ShouldRefit(userSized: true, 0.5625f, 0.5625f));
        Assert.False(PipGesture.ShouldRefit(userSized: true, 0.5625f, 0.57f));      // within 0.01
        Assert.True(PipGesture.ShouldRefit(userSized: true, 0.5625f, 0.4255f));     // a 2.35:1 stream arrived
        Assert.False(PipGesture.ShouldRefit(userSized: true, 0f, 0.4255f));         // nothing fitted yet: the size stands
    }

    [Fact]
    public void A_north_west_resize_keeps_the_south_east_corner_anchored()
    {
        var r = PipGesture.Resize(PipEdge.Left | PipEdge.Top, 1064f, 610f, 360f, 202f, -100f, -50f, VpW, VpH);
        Assert.Equal((964f, 560f, 460f, 252f), r);
        Assert.Equal(1064f + 360f, r.X + r.W);
        Assert.Equal(610f + 202f, r.Y + r.H);
    }

    [Fact]
    public void A_resize_never_goes_under_240_by_135()
    {
        var r = PipGesture.Resize(PipEdge.Right | PipEdge.Bottom, 1064f, 610f, 360f, 202f, -500f, -500f, VpW, VpH);
        Assert.Equal((1064f, 610f, Pip.MinW, Pip.MinH), r);
    }

    [Fact]
    public void Growing_right_and_down_is_bounded_by_the_window_and_the_player_bar()
    {
        var r = PipGesture.Resize(PipEdge.Right | PipEdge.Bottom, 1064f, 610f, 360f, 202f, 500f, 500f, VpW, VpH);
        Assert.Equal(VpW - 16f - 1064f, r.W);
        Assert.Equal(VpH - 16f - Pip.ReserveBottom - 610f, r.H);
    }

    [Fact]
    public void Growing_left_and_up_is_bounded_by_the_margin()
    {
        var r = PipGesture.Resize(PipEdge.Left | PipEdge.Top, 1064f, 610f, 360f, 202f, -5000f, -5000f, VpW, VpH);
        Assert.Equal(16f, r.X);
        Assert.Equal(16f, r.Y);
    }

    [Fact]
    public void An_edge_band_moves_only_its_own_axis()
    {
        var r = PipGesture.Resize(PipEdge.Top, 1064f, 610f, 360f, 202f, 999f, -20f, VpW, VpH);
        Assert.Equal((1064f, 590f, 360f, 222f), r);
    }
}

public class VideoScrubTests
{
    [Fact]
    public void A_preview_never_asks_for_an_accurate_decode()
        => Assert.False(Scrub.PreviewAccurate);

    [Fact]
    public void A_fetch_lands_on_the_segment_start_only_when_the_grid_is_known()
    {
        var plan = new Playback.Video.SeekPlan(Playback.Video.SeekVerb.Fetch, 8_000, 2, 0);
        Assert.Equal(8_000L, Scrub.PreviewTargetMs(in plan, 9_500, 4_000));
        Assert.Equal(9_500L, Scrub.PreviewTargetMs(in plan, 9_500, 0));
    }

    [Fact]
    public void A_buffered_keyframe_is_where_a_coarse_or_instant_preview_lands()
    {
        var coarse = new Playback.Video.SeekPlan(Playback.Video.SeekVerb.Coarse, 6_000, 1, 0);
        var instant = new Playback.Video.SeekPlan(Playback.Video.SeekVerb.Instant, 7_000, 1, 2_500);
        Assert.Equal(6_000L, Scrub.PreviewTargetMs(in coarse, 9_500, 4_000));
        Assert.Equal(7_000L, Scrub.PreviewTargetMs(in instant, 9_500, 4_000));
    }

    [Fact]
    public void A_host_with_no_index_yet_previews_at_the_raw_target_not_at_zero()
    {
        var ix = new Playback.Video.SeekIndex(ReadOnlySpan<long>.Empty, ReadOnlySpan<long>.Empty, 0, 200_000, 1_000, true);
        var plan = Playback.Video.SeekPlanner.Plan(in ix, 50_000, Playback.Video.SeekIntent.Preview);
        Assert.Equal(50_000L, Scrub.PreviewTargetMs(in plan, 50_000, 0));
    }

    [Fact]
    public void With_an_index_the_preview_snaps_to_the_buffered_keyframe_under_the_pointer()
    {
        ReadOnlySpan<long> keyframes = [0, 4_000, 8_000, 12_000];
        ReadOnlySpan<long> buffered = [0, 16_000];
        var ix = new Playback.Video.SeekIndex(keyframes, buffered, 4_000, 200_000, 1_000, true);
        var plan = Playback.Video.SeekPlanner.Plan(in ix, 9_500, Playback.Video.SeekIntent.Preview);
        Assert.Equal(8_000L, Scrub.PreviewTargetMs(in plan, 9_500, 4_000));
    }
}

public class VideoJoiningTests
{
    [Fact]
    public void The_spinner_waits_out_the_join_budget()
    {
        Assert.Equal(Playback.Video.Budgets.JoiningNoSpinnerMs, Joining.SpinnerDelayMs);
        Assert.False(Joining.ShowsSpinner(0));
        Assert.False(Joining.ShowsSpinner(399));
        Assert.True(Joining.ShowsSpinner(400));
    }
}

public class VideoFullscreenEntryTests
{
    static PlacementState Off => PlacementState.Music with
    {
        Available = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen,
    };

    [Fact]
    public void An_explicit_request_from_a_docked_video_is_a_user_entry()
    {
        var docked = PlacementCore.OpenAt(Off, SurfacePlacement.Docked);
        var full = PlacementCore.EnterFullscreen(docked);
        Assert.True(FullscreenEntry.Entered(docked, full));
        Assert.True(FullscreenEntry.UserInitiated(docked, full));
    }

    [Fact]
    public void Availability_returning_under_a_standing_fullscreen_request_is_not_a_user_entry()
    {
        var requested = PlacementCore.EnterFullscreen(PlacementCore.OpenAt(Off, SurfacePlacement.Docked));
        var noVideo = PlacementCore.WithAvailability(requested, PlacementSet.None);
        Assert.False(FullscreenEntry.Entered(requested, noVideo));
        Assert.True(FullscreenEntry.Entered(noVideo, requested));
        Assert.False(FullscreenEntry.UserInitiated(noVideo, requested));
    }

    [Fact]
    public void Leaving_fullscreen_is_not_an_entry()
    {
        var full = PlacementCore.EnterFullscreen(PlacementCore.OpenAt(Off, SurfacePlacement.Docked));
        Assert.False(FullscreenEntry.Entered(full, PlacementCore.ExitFullscreen(full)));
    }
}
