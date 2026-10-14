// ── Wavee.ShellTests/VideoMountedTests.cs — the video surfaces' views, mounted (themes plan 6.1, T7c) ───────────────────────────
//
// The video surfaces' pictures are the structure's since T7c: `Video.Docked`, `.Pip`, `.PopOut`, `.Fullscreen`, `.JoinPoster` and
// `.PlacementMenu`. The core keeps the media pipeline (the stay-mounted presenter and its engine element, the hollow reservation, the join
// fold, the PiP gestures, the pop-out's caption regions, the fullscreen mode); `ViewHarness` mounts each view alone with fixture props. What only
// a mounted tree can say:
//
//   registered   Classic is the registered template of all six keys
//   poster       the poster alone while the join is inside its budget or the picture is live; the ring and "Loading…" once it outlasts the
//                budget; the fault line and NO ring on a failure
//   docked       the card's height is the cell the splitter writes and the hollow reservation fills it (and reports its node); the watch
//                page's frame shows the idle play disc until the card is live, then the card, capped at the page's ceiling
//   overlay      docked: the three hover buttons and no gesture zones; floating: the close button, ONE drag surface and EIGHT resize zones, at the
//                size the binds say; fullscreen: the picture alone
//   pop-out      the stage over the poster (nothing over a live picture), the poster alone with no player, the band realized
//   fullscreen   the way out is one button
//   menu         every row of the decided list is a menu row

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.ShellTests;

public sealed class VideoMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static int Buttons(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(harness.Shell.Host.Scene.Root).Count(n => scene.Interaction(n).Role == AutomationRole.Button);
    }

    static int MenuRows(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => scene.Interaction(n).Role is AutomationRole.MenuItem or AutomationRole.RadioButton);   // a radio row is a RadioButton
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static VideoJoinPosterProps PosterProps(Video.JoinVisual visual, bool shown = true) => new()
    {
        Visual = visual, Shown = shown, ArtUrl = static () => null, FaultTitle = static () => "The licence was refused",
    };

    sealed class RecordingGestures : IVideoPipGestures
    {
        public int Drags;
        public readonly HashSet<int> Zones = [];
        public void DragRealized(NodeHandle node) => Drags++;
        public void DragDown(Point2 local) { }
        public void DragMove(Point2 local) { }
        public void ZoneRealized(int slot, NodeHandle node) => Zones.Add(slot);
        public void ZoneDown(int slot, Point2 local) { }
        public void ZoneMove(int slot, Video.PipEdge edge, Point2 local) { }
        public void Commit() { }
    }

    static VideoPipProps PipProps(Video.SurfacePlacement placement, IVideoPipGestures gestures) => new()
    {
        Placement = placement,
        Visible = true,
        FollowRect = static () => default,
        Width = 360f,
        Height = 202f,
        Transform = Affine2D.Identity,
        Area = static () => new BoxEl { Key = "test:area", Grow = 1f },
        Gestures = gestures,
        ToMiniPlayer = static () => { },
        ToFullscreen = static () => { },
        TurnOffDocked = static () => { },
        ClosePip = static () => { },
    };

    static VideoPopOutProps PopOutProps(Signal<Size2> size, bool hasPlayer, Video.JoinVisual join, List<NodeHandle>? band = null) => new()
    {
        Size = size,
        HasPlayer = hasPlayer,
        Join = join,
        BandShown = true,
        Generation = 1L,
        Stage = static () => new BoxEl { Key = "test:stage", Grow = 1f },
        Poster = static _ => new BoxEl { Key = "test:poster", Grow = 1f },
        Title = static () => "A music video",
        BandRealized = h => band?.Add(h),
        BandPointerMoved = static _ => { },
        BandPointerLeft = static () => { },
    };

    // ── the registered views ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_video_view()
        => host.Run(() =>
        {
            Assert.Equal("classic.video.docked", ThemeHost.Registry.TemplateIdOf(ViewKeys.VideoDocked.Id));
            Assert.Equal("classic.video.pip", ThemeHost.Registry.TemplateIdOf(ViewKeys.VideoPip.Id));
            Assert.Equal("classic.video.pop-out", ThemeHost.Registry.TemplateIdOf(ViewKeys.VideoPopOut.Id));
            Assert.Equal("classic.video.fullscreen", ThemeHost.Registry.TemplateIdOf(ViewKeys.VideoFullscreen.Id));
            Assert.Equal("classic.video.join-poster", ThemeHost.Registry.TemplateIdOf(ViewKeys.VideoJoinPoster.Id));
            Assert.Equal("classic.video.placement-menu", ThemeHost.Registry.TemplateIdOf(ViewKeys.VideoPlacementMenu.Id));
        });

    // ── the poster ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_poster_says_nothing_inside_the_join_budget_and_over_a_live_picture()
        => ViewHarness.With(host, ViewKeys.VideoJoinPoster, PosterProps(Video.JoinVisual.Poster), harness =>
        {
            Assert.False(harness.Contains("join:working"));
            Assert.False(harness.Contains("join:failed"));
            Assert.Equal(0, Buttons(harness));
        }, UnderClassic());

    [Fact]
    public void A_join_that_outlasted_its_budget_shows_the_ring_and_a_failure_the_fault_and_no_ring()
    {
        ViewHarness.With(host, ViewKeys.VideoJoinPoster, PosterProps(Video.JoinVisual.Working), harness =>
        {
            Assert.True(harness.Contains("join:working"));
            Assert.False(harness.Contains("join:failed"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.VideoJoinPoster, PosterProps(Video.JoinVisual.Failed), harness =>
        {
            Assert.True(harness.Contains("join:failed"));
            Assert.False(harness.Contains("join:working"));
        }, UnderClassic());
    }

    // ── the docked card ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_docked_cap_has_the_height_cell_the_splitter_writes_and_the_reservation_fills_it()
    {
        var reserved = new List<NodeHandle>();
        var props = new VideoDockedProps
        {
            Card = new VideoDockedCard(Video.DockedFace.Cap, new FloatSignal(220f), reserved.Add, static _ => { }, static () => { }),
        };
        ViewHarness.With(host, ViewKeys.VideoDocked, props, harness =>
        {
            var node = Assert.Single(reserved);                                         // the presenter follows this node
            Assert.Equal(220f, harness.AbsoluteBounds(node).H, 0.5f);
        }, UnderClassic());
    }

    [Fact]
    public void The_watch_pages_frame_shows_the_play_disc_until_the_card_is_live_and_caps_its_height()
    {
        var live = new Signal<bool>(false);
        var props = new VideoDockedProps
        {
            PageFrame = new VideoPageFrame(null, static () => { }, () => live.Value, static () => new BoxEl { Key = "test:card", Grow = 1f }, 300f),
        };
        ViewHarness.With(host, ViewKeys.VideoDocked, props, harness =>
        {
            Assert.True(harness.Contains("module-stage-cta"));
            Assert.False(harness.Contains("test:card"));
            Assert.Equal(1, Buttons(harness));                                          // the play disc
            Assert.True(harness.AbsoluteBounds(harness.Node("module-stage")).H <= 300.5f, "the page's ceiling caps the 16:9");

            live.Value = true;
            harness.RunFrames(5);
            Assert.True(harness.Contains("test:card"));
            Assert.False(harness.Contains("module-stage-cta"));
        }, UnderClassic());
    }

    // ── the in-window overlay ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_docked_overlay_has_the_three_hover_buttons_and_no_gesture_zones()
    {
        var gestures = new RecordingGestures();
        ViewHarness.With(host, ViewKeys.VideoPip, PipProps(Video.SurfacePlacement.Docked, gestures), harness =>
        {
            Assert.True(harness.Contains("test:area"));
            Assert.Equal(3, Buttons(harness));                                          // mini player, full screen, turn off
            Assert.Equal(0, gestures.Drags);
            Assert.Empty(gestures.Zones);
        }, UnderClassic());
    }

    [Fact]
    public void The_mini_player_has_the_close_button_one_drag_surface_and_eight_resize_zones_at_the_bound_size()
    {
        var gestures = new RecordingGestures();
        ViewHarness.With(host, ViewKeys.VideoPip, PipProps(Video.SurfacePlacement.Floating, gestures), harness =>
        {
            Assert.Equal(1, Buttons(harness));                                          // the close button
            Assert.Equal(1, gestures.Drags);
            Assert.Equal(Enumerable.Range(0, 8), gestures.Zones.Order());
            var area = harness.AbsoluteBounds(harness.Node("test:area"));
            Assert.Equal(360f, area.W, 0.5f);
            Assert.Equal(202f, area.H, 0.5f);
        }, UnderClassic());
    }

    [Fact]
    public void The_fullscreen_overlay_is_the_picture_alone()
    {
        var gestures = new RecordingGestures();
        ViewHarness.With(host, ViewKeys.VideoPip, PipProps(Video.SurfacePlacement.Fullscreen, gestures), harness =>
        {
            Assert.True(harness.Contains("test:area"));
            Assert.Equal(0, Buttons(harness));
            Assert.Empty(gestures.Zones);
        }, UnderClassic());
    }

    // ── the pop-out and the fullscreen chrome ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_pop_out_shows_the_stage_over_the_poster_and_the_poster_alone_with_no_player()
    {
        var size = new Signal<Size2>(new Size2(640f, 360f));
        var band = new List<NodeHandle>();
        ViewHarness.With(host, ViewKeys.VideoPopOut, PopOutProps(size, hasPlayer: true, Video.JoinVisual.Video, band), harness =>
        {
            Assert.True(harness.Contains("test:stage"));
            Assert.False(harness.Contains("test:poster"));                              // nothing over a live picture
            Assert.Single(band);                                                        // the band is realized: the core fades it
            Assert.Equal(640f, harness.AbsoluteBounds(harness.Node("test:stage")).W, 0.5f);
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.VideoPopOut, PopOutProps(size, hasPlayer: true, Video.JoinVisual.Working), harness =>
        {
            Assert.True(harness.Contains("test:stage"));
            Assert.True(harness.Contains("test:poster"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.VideoPopOut, PopOutProps(size, hasPlayer: false, Video.JoinVisual.Poster), harness =>
        {
            Assert.False(harness.Contains("test:stage"));
            Assert.True(harness.Contains("test:poster"));
        }, UnderClassic());
    }

    [Fact]
    public void The_fullscreen_chrome_is_one_way_out()
    {
        int exits = 0;
        var props = new VideoFullscreenProps { Title = static () => "A music video", Exit = () => exits++ };
        ViewHarness.With(host, ViewKeys.VideoFullscreen, props, harness =>
        {
            Assert.Equal(1, Buttons(harness));
            Assert.Equal(0, exits);
        }, UnderClassic());
    }

    // ── the placement flyout ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_decided_row_is_a_menu_row()
    {
        var props = new VideoPlacementMenuProps
        {
            Items =
            [
                MenuFlyoutItem.RadioItem("Dock in rail", true, static () => { }),
                MenuFlyoutItem.RadioItem("Mini player", false, static () => { }),
                MenuFlyoutItem.Separator,
                MenuFlyoutItem.RadioItem("Separate window", false, static () => { }),
            ],
            Close = static () => { },
        };
        ViewHarness.With(host, ViewKeys.VideoPlacementMenu, props, harness => Assert.Equal(3, MenuRows(harness)), UnderClassic());
    }
}
