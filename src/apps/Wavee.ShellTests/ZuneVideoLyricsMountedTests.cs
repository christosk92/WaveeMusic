// ── Wavee.ShellTests/ZuneVideoLyricsMountedTests.cs — the rest of Zune's stage, the lyrics view and video, mounted (P5 Z4c) ─────────
//
// The real shell under Zune (`ZuneHost`) at 1440 x 960 (the Standard window tier) with the stage open through its own API, and the video views
// mounted alone through the view harness under Zune's composition (`ZuneTheme.Create()`):
//
//   registered   every key Z4c owns is drawn by a `zune.*` template, ahead of Classic's stand-in
//   lyrics       the stage's reading column draws its rows, and exactly ONE line is in another ink than the rest: the sung line, the accent
//   up next      the stage's Up next column mounts through Zune's template (the autoplay word, the empty lane)
//   artist       the Artist pivot's pane mounts its scroller; the Artist layout opens without the hero or the name throwing
//   video        the poster says nothing inside the join budget and the ring and the fault past it; the docked card's reservation fills the
//                cell the splitter writes; the watch page's frame shows the play disc until the card is live; the docked overlay has three
//                hover controls and no gesture zones, the mini player the close glyph, ONE drag surface and EIGHT resize zones; the fullscreen
//                bar is one way out, in lowercase words; the pop-out shows the stage over the poster; every decided placement row is a menu row

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneVideoLyricsMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static ViewHarnessOptions UnderZune() => new() { Composition = ZuneTheme.Create(), WidthDip = W, HeightDip = H };

    /// <summary>Mount one view alone under Zune's composition with Zune ACTIVE (its roles, so the case roles and the shapes are Zune's), run
    /// <paramref name="body"/>, then put Classic back. UI thread, like <c>ViewHarness.With</c>.</summary>
    void WithZune<TProps>(ShellHost shellHost, ViewKey<TProps> key, TProps props, Action<ViewHarness> body) where TProps : class
        => shellHost.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                using (var harness = ViewHarness.Mount(key, props, UnderZune())) body(harness);
                ShellHost.AssertNoNetwork();
            }
            finally { ZuneHost.Restore(); }
        });

    static void CloseStage(Shell.HeadlessShell shell)
    {
        Stage.Close(null, "test");
        shell.RunFrames(60);
        Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
        Prefs.Stage.SetLayout(Platform.Keys.StageLayout.Default);
    }

    /// <summary>The nodes of the document's rows (their keys start with <c>ll</c>) that live under <paramref name="root"/>.</summary>
    static List<NodeHandle> RowsUnder(Shell.HeadlessShell shell, NodeHandle root)
    {
        var inside = new HashSet<NodeHandle>(Subtree(shell.Host.Scene, root));
        var rows = new List<NodeHandle>();
        foreach (var (node, _) in shell.FindPrefix("ll"))
            if (inside.Contains(node)) rows.Add(node);
        return rows;
    }

    static int Buttons(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => scene.Interaction(n).Role == AutomationRole.Button);
    }

    static int MenuRows(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => scene.Interaction(n).Role is AutomationRole.MenuItem or AutomationRole.RadioButton);
    }

    /// <summary>The text leaves under the harness's root set in the lowercase case role.</summary>
    static int LowercaseWords(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => scene.Paint(n).VisualKind == VisualKind.Text && scene.Layout(n).TextStyle.Case == TextCase.Lower);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static VideoJoinPosterProps PosterProps(Video.JoinVisual visual) => new()
    {
        Visual = visual, Shown = true, ArtUrl = static () => null, FaultTitle = static () => "The licence was refused",
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

    static VideoPopOutProps PopOutProps(Signal<Size2> size, bool hasPlayer, Video.JoinVisual join) => new()
    {
        Size = size,
        HasPlayer = hasPlayer,
        Join = join,
        BandShown = true,
        Generation = 1L,
        Stage = static () => new BoxEl { Key = "test:stage", Grow = 1f },
        Poster = static _ => new BoxEl { Key = "test:poster", Grow = 1f },
        Title = static () => "A music video",
        BandRealized = static _ => { },
        BandPointerMoved = static _ => { },
        BandPointerLeft = static () => { },
    };

    // ── the registered views ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_key_of_its_lane()
        => host.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                (string Id, string Template)[] expected =
                [
                    (ViewKeys.StageHero.Id, "zune.stage.hero"),
                    (ViewKeys.StageArtistName.Id, "zune.stage.artist-name"),
                    (ViewKeys.StageArtistPane.Id, "zune.stage.artist-pane"),
                    (ViewKeys.QueueStagePane.Id, "zune.queue.stage-pane"),
                    (ViewKeys.LyricsView.Id, "zune.lyrics.view"),
                    (ViewKeys.VideoDocked.Id, "zune.video.docked"),
                    (ViewKeys.VideoPip.Id, "zune.video.pip"),
                    (ViewKeys.VideoPopOut.Id, "zune.video.pop-out"),
                    (ViewKeys.VideoFullscreen.Id, "zune.video.fullscreen"),
                    (ViewKeys.VideoJoinPoster.Id, "zune.video.join-poster"),
                    (ViewKeys.VideoPlacementMenu.Id, "zune.video.placement-menu"),
                ];
                foreach (var (id, template) in expected)
                    Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
            }
            finally { ZuneHost.Restore(); }
        });

    // ── the lyrics view ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lyrics_view_mounts_with_the_sung_line_in_the_accent()
        => ZuneHost.Mounted(host, shell =>
        {
            try
            {
                Prefs.Stage.SetMode((int)Stage.Mode.Lyrics);
                // Past the fixture's first line (Lyrics.Fixtures: 4 s), so a line is in focus.
                Play("spotify:track:tr40", fromMs: 20_000);
                Stage.Open(null, "test");
                Until(shell, () => !shell.Find("lyrics:stage").IsNull, "the stage's lyrics column mounted", 900);
                var column = shell.Find("lyrics:stage");
                UntilBackground(shell, () => RowsUnder(shell, column).Count > 2, "the stage's lyrics rows were realized");
                shell.RunFrames(90);   // the follow settles, the rest <-> accent flip finishes

                var scene = shell.Host.Scene;
                var inks = new List<ColorF>();
                foreach (var row in RowsUnder(shell, column))
                {
                    var text = Subtree(scene, row).First(n => scene.Paint(n).VisualKind == VisualKind.Text);
                    inks.Add(scene.Paint(text).TextColor);
                }
                Assert.True(inks.Count > 2, "rows: " + inks.Count);
                var odd = inks.GroupBy(c => c).Where(g => g.Count() == 1).ToList();
                Assert.True(odd.Count == 1, "exactly one line (the sung one) is in another ink than the rest; distinct inks: " + inks.Distinct().Count());
            }
            finally { CloseStage(shell); }
        }, W, H);

    // ── the Up next column ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_up_next_column_mounts_through_zunes_template()
        => ZuneHost.Mounted(host, shell =>
        {
            try
            {
                Assert.Equal("zune.queue.stage-pane", ThemeHost.Registry.TemplateIdOf(ViewKeys.QueueStagePane.Id));
                Prefs.Stage.SetMode((int)Stage.Mode.Queue);
                Play("spotify:track:tr40", fromMs: 20_000);
                Stage.Open(null, "test");
                Until(shell, () => !shell.Find("stagelane:empty").IsNull, "the stage's queue lane mounted", 900);
                shell.RunFrames(30);

                Assert.False(shell.Find("stage:upnext:autoplay").IsNull, "the autoplay word is the column's toggle");
                Assert.True(shell.Find("stagelane:upcoming").IsNull, "nothing is upcoming, so only the empty lane is mounted");
            }
            finally { CloseStage(shell); }
        }, W, H);

    // ── the Artist pane, hero and name ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_artist_pane_mounts_its_scroller_in_the_pane_region()
        => ZuneHost.Mounted(host, shell =>
        {
            try
            {
                Prefs.Stage.SetMode((int)Stage.Mode.Artist);
                Play("spotify:track:tr40", fromMs: 20_000);
                Stage.Open(null, "test");
                Until(shell, () => !shell.Find("stage:cover").IsNull, "the stage mounted", 900);
                UntilBackground(shell, () => !ScrollUnder(shell, shell.Find("stage:pane")).IsNull, "the artist pane's scroller was realized");
                shell.RunFrames(60);

                Assert.False(shell.Find("stage:pane").IsNull, "the pane region is mounted");
                Assert.Equal("zune.stage.artist-pane", ThemeHost.Registry.TemplateIdOf(ViewKeys.StageArtistPane.Id));
            }
            finally { CloseStage(shell); }
        }, W, H);

    [Fact]
    public void The_artist_layout_opens_without_its_hero_or_name_throwing()
        => ZuneHost.Mounted(host, shell =>
        {
            try
            {
                Prefs.Stage.SetMode((int)Stage.Mode.Visualizer);
                Prefs.Stage.SetLayout((int)Stage.VizLayout.Artist);
                Play("spotify:track:tr40", fromMs: 20_000);
                Stage.Open(null, "test");
                Until(shell, () => !shell.Find("stage:cover").IsNull, "the stage mounted", 900);
                shell.RunFrames(120);   // the overview answers, the hero decides its header; the layers mount if it has one

                Assert.False(shell.Find("stage:cover").IsNull, "the stage kept its identity");
                Assert.Equal("zune.stage.hero", ThemeHost.Registry.TemplateIdOf(ViewKeys.StageHero.Id));
                Assert.Equal("zune.stage.artist-name", ThemeHost.Registry.TemplateIdOf(ViewKeys.StageArtistName.Id));
            }
            finally { CloseStage(shell); }
        }, W, H);

    // ── the poster ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_poster_says_nothing_inside_the_join_budget_and_the_ring_or_the_fault_past_it()
    {
        WithZune(host, ViewKeys.VideoJoinPoster, PosterProps(Video.JoinVisual.Poster), harness =>
        {
            Assert.False(harness.Contains("join:working"));
            Assert.False(harness.Contains("join:failed"));
            Assert.Equal(0, Buttons(harness));
        });

        WithZune(host, ViewKeys.VideoJoinPoster, PosterProps(Video.JoinVisual.Working), harness =>
        {
            Assert.True(harness.Contains("join:working"));
            Assert.False(harness.Contains("join:failed"));
            Assert.True(LowercaseWords(harness) >= 1, "the notice's word is lowercase through the label case");
        });

        WithZune(host, ViewKeys.VideoJoinPoster, PosterProps(Video.JoinVisual.Failed), harness =>
        {
            Assert.True(harness.Contains("join:failed"));
            Assert.False(harness.Contains("join:working"));
        });
    }

    // ── the docked card ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_docked_card_mounts_and_its_reservation_fills_the_cell_the_splitter_writes()
    {
        var reserved = new List<NodeHandle>();
        var props = new VideoDockedProps
        {
            Card = new VideoDockedCard(Video.DockedFace.Cap, new FloatSignal(220f), reserved.Add, static _ => { }, static () => { }),
        };
        WithZune(host, ViewKeys.VideoDocked, props, harness =>
        {
            var node = Assert.Single(reserved);                                         // the presenter follows this node
            Assert.Equal(220f, harness.AbsoluteBounds(node).H, 0.5f);
        });
    }

    [Fact]
    public void The_watch_pages_frame_shows_the_play_disc_until_the_card_is_live_and_caps_its_height()
    {
        var live = new Signal<bool>(false);
        var props = new VideoDockedProps
        {
            PageFrame = new VideoPageFrame(null, static () => { }, () => live.Value, static () => new BoxEl { Key = "test:card", Grow = 1f }, 300f),
        };
        WithZune(host, ViewKeys.VideoDocked, props, harness =>
        {
            Assert.True(harness.Contains("module-stage-cta"));
            Assert.False(harness.Contains("test:card"));
            Assert.Equal(1, Buttons(harness));                                          // the play disc
            Assert.True(harness.AbsoluteBounds(harness.Node("module-stage")).H <= 300.5f, "the page's ceiling caps the 16:9");

            live.Value = true;
            harness.RunFrames(5);
            Assert.True(harness.Contains("test:card"));
            Assert.False(harness.Contains("module-stage-cta"));
        });
    }

    // ── the in-window overlay ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_docked_overlay_has_three_hover_controls_and_no_gesture_zones()
    {
        var gestures = new RecordingGestures();
        WithZune(host, ViewKeys.VideoPip, PipProps(Video.SurfacePlacement.Docked, gestures), harness =>
        {
            Assert.True(harness.Contains("test:area"));
            Assert.Equal(3, Buttons(harness));                                          // "mini player", "full screen", turn off
            Assert.True(LowercaseWords(harness) >= 2, "the two words are lowercase through the command case");
            Assert.Equal(0, gestures.Drags);
            Assert.Empty(gestures.Zones);
        });
    }

    [Fact]
    public void The_mini_player_has_the_close_glyph_one_drag_surface_and_eight_resize_zones_at_the_bound_size()
    {
        var gestures = new RecordingGestures();
        WithZune(host, ViewKeys.VideoPip, PipProps(Video.SurfacePlacement.Floating, gestures), harness =>
        {
            Assert.Equal(1, Buttons(harness));                                          // the close glyph
            Assert.Equal(1, gestures.Drags);
            Assert.Equal(Enumerable.Range(0, 8), gestures.Zones.Order());
            var area = harness.AbsoluteBounds(harness.Node("test:area"));
            Assert.Equal(360f, area.W, 0.5f);
            Assert.Equal(202f, area.H, 0.5f);
        });
    }

    [Fact]
    public void The_fullscreen_overlay_is_the_picture_alone()
    {
        var gestures = new RecordingGestures();
        WithZune(host, ViewKeys.VideoPip, PipProps(Video.SurfacePlacement.Fullscreen, gestures), harness =>
        {
            Assert.True(harness.Contains("test:area"));
            Assert.Equal(0, Buttons(harness));
            Assert.Empty(gestures.Zones);
        });
    }

    // ── the pop-out and the fullscreen bar ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_pop_out_shows_the_stage_over_the_poster_and_the_poster_alone_with_no_player()
    {
        var size = new Signal<Size2>(new Size2(640f, 360f));
        WithZune(host, ViewKeys.VideoPopOut, PopOutProps(size, hasPlayer: true, Video.JoinVisual.Video), harness =>
        {
            Assert.True(harness.Contains("test:stage"));
            Assert.False(harness.Contains("test:poster"));                              // nothing over a live picture
            Assert.Equal(640f, harness.AbsoluteBounds(harness.Node("test:stage")).W, 0.5f);
        });

        WithZune(host, ViewKeys.VideoPopOut, PopOutProps(size, hasPlayer: true, Video.JoinVisual.Working), harness =>
        {
            Assert.True(harness.Contains("test:stage"));
            Assert.True(harness.Contains("test:poster"));
        });

        WithZune(host, ViewKeys.VideoPopOut, PopOutProps(size, hasPlayer: false, Video.JoinVisual.Poster), harness =>
        {
            Assert.False(harness.Contains("test:stage"));
            Assert.True(harness.Contains("test:poster"));
        });
    }

    [Fact]
    public void The_fullscreen_bar_is_one_way_out_in_lowercase_words()
    {
        int exits = 0;
        var props = new VideoFullscreenProps { Title = static () => "A music video", Exit = () => exits++ };
        WithZune(host, ViewKeys.VideoFullscreen, props, harness =>
        {
            Assert.Equal(1, Buttons(harness));
            Assert.Equal(0, exits);
            Assert.True(LowercaseWords(harness) >= 2, "the bar's label and the way out are lowercase through the case roles");
        });
    }

    // ── the placement flyout ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_decided_row_is_a_menu_row()
    {
        var props = new VideoPlacementMenuProps
        {
            Items =
            [
                MenuFlyoutItem.RadioItem("Dock in panel", true, static () => { }),
                MenuFlyoutItem.RadioItem("Mini player", false, static () => { }),
                MenuFlyoutItem.Separator,
                MenuFlyoutItem.RadioItem("Separate window", false, static () => { }),
            ],
            Close = static () => { },
        };
        WithZune(host, ViewKeys.VideoPlacementMenu, props, harness => Assert.Equal(3, MenuRows(harness)));
    }
}
