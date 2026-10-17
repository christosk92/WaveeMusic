// ── Wavee.ShellTests/PlayerBarMountedTests.cs — the Classic player bar, mounted (themes plan 6.1, T6a) ─────────────────────
//
// The bar is the structure's since T6a: the frame mounts `Player.Bar`, Classic's template runs the core half first
// (`IPlayerBarHost.Use`: the level machine, the face hold's bookkeeping, the volume mirror) and then draws the view it decided.
// What only a mounted tree can say:
//
//   the registered view   Classic registers `Player.Bar`; the real shell mounted on the installed theme draws it: a 72 DIP dock the
//                         width of the window, the three clusters, the slots of the level the window is at
//   the level machine     widening commits at once and narrowing holds through the policy's 24 DIP dip, on the mounted bar (the
//                         pure rule is `ThemePoliciesTests`; this is the host driving it from the viewport)
//   the picture           `ViewHarness` mounts the view alone with a fixture host: the music bar's slots, the podcast bar's
//                         transport, the podcast bar's narrowest level (the cover alone), a video track's split, the error state,
//                         the credits line (compact with its "+N" chip, full names under the marquee, inert names)
//   the transport pieces  `Player.SeekRail` (enabled, disabled, the DVR edge, the live line), `Player.SeekChapters`, the two bubbles and
//                         `Player.TimeLabel` (the digits' slot, the live slot's LIVE mark and GO LIVE action), each alone with fixture props
//
// The decisions themselves (the fold, the face hold, the slots, the overflow rows, the podcast map) are `Wavee.Tests`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class PlayerBarMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── a fixture host: the view is whatever the test says, the hooks are not run ──────────────────────────────────────────

    sealed class FixtureHost(PlayerBarView view) : IPlayerBarHost
    {
        public PlayerBarView Use(RenderContext hooks) => view;
    }

    static PlayerBarMetrics Metrics(float leftW = 260f, float artSize = 48f) => new()
    {
        ButtonBox = 32f, ButtonGlyph = 16f, PrimaryBox = 40f, PrimaryGlyph = 20f,
        LeftW = leftW, ArtSize = artSize, RowGap = 8f, RowPad = 12f, ClusterGap = 4f, LeftGap = 8f, SeekGap = 6f, RightGap = 2f,
        TopEdgeWidth = 2400f, Height = 72f, VolumeSliderW = 96f, SplitChevronW = 20f, EpisodeSpeedButtonW = 48f, MinButtonBox = 32f,
    };

    static float WidthOf(RightSlot slot) => slot switch { RightSlot.VolumeSlider => 96f, RightSlot.Video => 52f, _ => 32f };

    /// <summary>The Full level's music bar: every slot but the video split, every face lit.</summary>
    static PlayerBarView MusicView(RightSlot slots = RightSlot.Shuffle | RightSlot.Repeat | RightSlot.Volume | RightSlot.VolumeSlider
        | RightSlot.Lyrics | RightSlot.Queue | RightSlot.Devices | RightSlot.Expand | RightSlot.More) => new()
    {
        Metrics = Metrics(),
        OwnsTransport = true,
        CanTransport = true, PrevEnabled = true, NextEnabled = true,
        Primary = PrimaryVerb.TogglePlay, RepeatGlyph = "r",
        ShowTitleBlock = true, ShowLikeSlot = true, ShowArtistLine = true, ShowPrevNext = true,
        ShowTimesElapsed = true, ShowTimesRemaining = true,
        Slots = slots, Lit = slots, SlotWidth = WidthOf,
        RightWidth = Sum(slots, 2f),
        Overflow = [OverflowCommand.NowPlaying],
        LikeLit = true, LikeEnabled = true,
    };

    static float Sum(RightSlot slots, float gap)
    {
        float w = 0f;
        int n = 0;
        for (int bit = 1; bit <= (int)RightSlot.More; bit <<= 1)
        {
            if ((slots & (RightSlot)bit) == 0) continue;
            w += WidthOf((RightSlot)bit);
            n++;
        }
        return n == 0 ? 0f : w + gap * (n - 1);
    }

    static PlayerBarProps Props(PlayerBarView view) => new()
    {
        Host = new FixtureHost(view),
        Verbs = PlayerVerbs.Default,
        TitleText = static () => "A song",
        TitleInk = static () => NowPlayingInk.Primary,
        TitleHasRoute = static () => false,
        DragPayload = static () => null,
        DeviceIsRemote = static () => false,
        RemoteDeviceName = static () => "Kitchen",
        DevicePickerRequest = new Signal<int>(0),
        Muted = new Signal<bool>(false),
        EpisodeSpeed = new Signal<float>(1f),
        Volume = new FloatSignal(1f),
        NavigateTitle = static () => { },
        NavigateArt = static () => { },
        OpenLyrics = static () => { },
        OpenQueue = static () => { },
        ToggleMute = static () => { },
        SetVolume = static _ => { },
        OnKey = static (_, _) => { },
        ToggleDevicePicker = static (_, _, _) => { },
        LikePops = static (_, _, _, _) => false,
        ArtMorphKey = "test.art",
        VolumeText = static v => v.ToString("0.00"),
        RateText = static r => r.ToString("0.0"),
        SeekBar = static () => new BoxEl { Key = "test:seek-rail", Grow = 1f, Height = 32f },
        TimeText = static remaining => new BoxEl { Key = remaining ? "test:remaining" : "test:elapsed", Width = 44f, Height = 16f },
        CreditsStamp = static () => 0L,
        Credits = static () => PlayerCredits.None,
        OverflowRow = static (_, _) => new FluentGpu.Controls.MenuFlyoutItem("row", default, true),
        VideoPlacementMenu = static () => [],
    };

    // ── the registered view ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_player_bar()
        => host.Run(() => Assert.Equal("classic.player.bar", ThemeHost.Registry.TemplateIdOf(ViewKeys.PlayerBar.Id)));

    [Fact]
    public void The_mounted_shell_draws_the_bar_through_the_classic_template()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            // the bar's level settles on the dock's measured width (a previous fact may leave the window at another size): wait for the Wide row
            Until(shell, () => !shell.Find("queue").IsNull && shell.Find("expand").IsNull, "the Wide level's row", maxFrames: 120);
            var scene = shell.Host.Scene;

            var row = Node(shell, "player-row");
            var bar = scene.Parent(row);
            var rect = scene.AbsoluteRect(bar);
            Assert.Equal(72f, rect.H, 0.5f);                 // the policy's height
            Assert.Equal(1180f, rect.W, 0.5f);               // the dock spans the window

            foreach (string cluster in new[] { "left", "centre", "right" })
                Assert.False(shell.Find(cluster).IsNull, cluster + " cluster");
            Assert.False(shell.Find("np-title").IsNull);

            // 1180 DIP is the Wide level: the queue and the inline volume rail are in the row, the expand button is not.
            foreach (string slot in new[] { "shuffle", "repeat", "volume", "volume-slider", "lyrics", "queue", "devices", "more" })
                Assert.False(shell.Find(slot).IsNull, slot + " slot");
            Assert.True(shell.Find("expand").IsNull);
            Assert.True(shell.Find("video").IsNull);
        });

    [Fact]
    public void The_level_machine_widens_at_once_and_narrows_through_the_policys_dip()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            bool Inline() => !shell.Find("volume-slider").IsNull;   // the Wide level's inline volume rail

            shell.Resize(1100f, 760f);
            shell.RunFrames(8);
            Assert.True(Inline());                                  // Wide starts at 1100

            shell.Resize(1090f, 760f);
            shell.RunFrames(8);
            Assert.True(Inline());                                  // 1090 + 24 still reaches 1100: the level holds

            shell.Resize(1070f, 760f);
            shell.RunFrames(8);
            Assert.False(Inline());                                 // 1070 + 24 does not: Comfortable

            shell.Resize(1100f, 760f);
            shell.RunFrames(8);
            Assert.True(Inline());                                  // widening commits at once
        });

    // ── the picture (the view alone, under the Classic composition) ─────────────────────────────────────────────────────────

    [Fact]
    public void The_music_bar_is_the_dock_with_three_clusters_and_every_slot_of_its_level()
        => ViewHarness.With(host, ViewKeys.PlayerBar, Props(MusicView()),
            harness =>
            {
                var row = harness.Node("player-row");
                var bar = harness.Shell.Host.Scene.Parent(row);
                var rect = harness.AbsoluteBounds(bar);
                Assert.Equal(72f, rect.H, 0.5f);
                Assert.Equal(1180f, rect.W, 0.5f);

                var right = harness.AbsoluteBounds(harness.Node("right"));
                Assert.Equal(Sum(RightSlot.Shuffle | RightSlot.Repeat | RightSlot.Volume | RightSlot.VolumeSlider | RightSlot.Lyrics
                    | RightSlot.Queue | RightSlot.Devices | RightSlot.Expand | RightSlot.More, 2f), right.W, 0.5f);
                Assert.Equal(rect.X + rect.W - 12f, right.X + right.W, 1f);   // the row's padding

                foreach (string slot in new[] { "shuffle", "repeat", "volume", "volume-slider", "lyrics", "queue", "devices", "expand", "more", "like" })
                    Assert.True(harness.Contains(slot), slot + " slot");
                Assert.False(harness.Contains("video"));
                Assert.True(harness.Contains("prev"));
                Assert.True(harness.Contains("primary"));
                Assert.True(harness.Contains("next"));
                Assert.True(harness.Contains("test:elapsed"));
                Assert.True(harness.Contains("test:remaining"));
                Assert.True(harness.Contains("test:seek-rail"));
                Assert.False(harness.Contains("seek-back-10"));
            }, UnderClassic());

    [Fact]
    public void A_dark_slot_stays_in_the_row_at_its_width_and_takes_no_hits()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView() with { Lit = MusicView().Slots & ~RightSlot.Lyrics }),
            harness =>
            {
                var lyrics = harness.AbsoluteBounds(harness.Node("lyrics"));
                Assert.Equal(32f, lyrics.W, 0.5f);                 // reserved: the row does not move when a track starts
                var scene = harness.Shell.Host.Scene;
                Assert.Equal(NodeFlags.None, scene.Flags(harness.Node("lyrics")) & NodeFlags.HitTestVisible);
                Assert.Equal(NodeFlags.HitTestVisible, scene.Flags(harness.Node("queue")) & NodeFlags.HitTestVisible);
            }, UnderClassic());

    [Fact]
    public void The_podcast_bar_adds_the_step_buttons_and_the_speed_button_to_the_transport()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView(RightSlot.Volume | RightSlot.Lyrics | RightSlot.Devices | RightSlot.More)
                with { Episode = true, SeekStepEnabled = true, ShowPrevNext = false, VolumePopup = true }),
            harness =>
            {
                Assert.True(harness.Contains("seek-back-10"));
                Assert.True(harness.Contains("seek-forward-10"));
                Assert.True(harness.Contains("episode-speed"));
                Assert.False(harness.Contains("prev"));
                Assert.False(harness.Contains("shuffle"));
                Assert.False(harness.Contains("volume-slider"));
                Assert.True(harness.Contains("volume"));

                var back = harness.AbsoluteBounds(harness.Node("seek-back-10"));
                var primary = harness.AbsoluteBounds(harness.Node("primary"));
                var forward = harness.AbsoluteBounds(harness.Node("seek-forward-10"));
                var speed = harness.AbsoluteBounds(harness.Node("episode-speed"));
                Assert.True(back.X < primary.X && primary.X < forward.X && forward.X < speed.X, "−10 · play · +10 · speed, left to right");
                Assert.Equal(48f, speed.W, 0.5f);
            }, UnderClassic());

    [Fact]
    public void The_podcast_bars_narrowest_level_shows_the_cover_alone()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView(RightSlot.Devices | RightSlot.More)
                with { Metrics = Metrics(leftW: 40f, artSize: 40f), Episode = true, ShowTitleBlock = false, ShowLikeSlot = false,
                       ShowPrevNext = false, ShowTimesElapsed = false, ShowTimesRemaining = false }),
            harness =>
            {
                Assert.True(harness.Contains("art"));
                Assert.False(harness.Contains("meta"));
                Assert.False(harness.Contains("like"));
                Assert.Equal(40f, harness.AbsoluteBounds(harness.Node("left")).W, 0.5f);
            }, UnderClassic());

    [Fact]
    public void A_video_track_adds_the_split_button_and_a_remote_device_adds_its_line()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView(RightSlot.Video | RightSlot.Devices | RightSlot.More) with { HasVideo = true, RemoteLine = true }),
            harness =>
            {
                Assert.True(harness.Contains("video"));
                Assert.Equal(52f, harness.AbsoluteBounds(harness.Node("video")).W, 0.5f);   // the glyph and the 20 DIP chevron
                Assert.True(harness.Contains("remote-device-line"));
            }, UnderClassic());

    [Fact]
    public void A_latched_video_split_wears_one_checked_plate_across_both_halves()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView(RightSlot.Lyrics | RightSlot.Video | RightSlot.Queue | RightSlot.Devices | RightSlot.More)
                with { HasVideo = true, VideoLive = true, LyricsOpen = true }),
            harness =>
            {
                // tuned P2: the one checked plate — a latched Lyrics plate is its 32 box, the latched video split's spans the 52 split
                var scene = harness.Shell.Host.Scene;
                List<RectF> Plates(string slot)
                {
                    var plates = new List<RectF>();
                    foreach (var node in harness.Subtree(harness.Node(slot)))
                        if (scene.Paint(node).Fill.A > 0f) plates.Add(harness.AbsoluteBounds(node));
                    return plates;
                }
                var video = Plates("video");
                Assert.Single(video);
                Assert.Equal(52f, video[0].W, 0.5f);
                Assert.Equal(32f, video[0].H, 0.5f);
                var lyrics = Plates("lyrics");
                Assert.Single(lyrics);
                Assert.Equal(32f, lyrics[0].W, 0.5f);
                Assert.Equal(scene.Paint(harness.Subtree(harness.Node("lyrics")).First(n => scene.Paint(n).Fill.A > 0f)).Fill,
                    scene.Paint(harness.Subtree(harness.Node("video")).First(n => scene.Paint(n).Fill.A > 0f)).Fill);
                Assert.Empty(Plates("queue"));   // an unlatched button is unpainted
            }, UnderClassic());

    [Fact]
    public void At_the_compact_level_the_queue_is_in_the_row_and_the_seek_bar_gives_the_room()
        => host.Mounted(shell =>
        {
            // tuned P1: 600 DIP is the Compact level (440-759): the queue stays, lyrics / volume / shuffle / repeat fold into the "⋯"
            shell.RunFrames(10);
            var scene = shell.Host.Scene;
            Assert.False(shell.Find("queue").IsNull, "queue slot");
            foreach (string gone in new[] { "lyrics", "volume", "shuffle", "repeat", "volume-slider", "expand" })
                Assert.True(shell.Find(gone).IsNull, gone + " folded");
            var queue = scene.PresentedAbsoluteRect(Node(shell, "queue"));
            Assert.Equal(32f, queue.W, 0.5f);   // never a smaller button
            Assert.Equal(32f, queue.H, 0.5f);
            Assert.True(scene.PresentedAbsoluteRect(Node(shell, "seek")).W >= 40f, "the seek rail keeps the board's 40 floor");
        }, widthDip: 600f);

    [Fact]
    public void The_overflow_button_is_remounted_by_its_command_set()
    {
        string KeyOf(params OverflowCommand[] commands)
        {
            string? key = null;
            ViewHarness.With(host, ViewKeys.PlayerBar,
                Props(MusicView(RightSlot.Devices | RightSlot.More) with { Overflow = commands }),
                harness =>
                {
                    var found = harness.FindPrefix("more#");
                    Assert.Single(found);
                    key = found[0].Key;
                }, UnderClassic());
            return key!;
        }

        var narrow = KeyOf(OverflowCommand.NowPlaying);
        var wide = KeyOf(OverflowCommand.Shuffle, OverflowCommand.Repeat, OverflowCommand.NowPlaying);
        Assert.NotEqual(narrow, wide);
        Assert.Equal(narrow, KeyOf(OverflowCommand.NowPlaying));   // the same set, the same key
    }

    [Fact]
    public void An_errored_bar_keeps_the_play_glyph_and_stays_a_retry()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView() with { Errored = true, Primary = PrimaryVerb.Retry, PauseGlyph = true }),
            harness =>
            {
                var primary = harness.Node("primary");
                var glyph = harness.Shell.Host.Scene.FirstChild(primary);
                Assert.False(glyph.IsNull);
                Assert.True(harness.AbsoluteBounds(primary).W > 0f);
            }, UnderClassic());

    // ── the credits line (the bar's own picture over the facts the core states) ─────────────────────────────────────────────

    static PlayerCredits TwoArtists() => new()
    {
        Artists = [new PlayerCreditLink("Ann", static () => { }, "artist:1"), new PlayerCreditLink("Bob", null, "artist:2")],
        OverflowKey = "npmore:1:2",
    };

    static int WithRole(ViewHarness harness, AutomationRole role)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => scene.Interaction(n).Role == role);
    }

    [Fact]
    public void The_compact_credits_line_is_the_first_name_and_the_more_chip()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView()) with { Credits = static () => TwoArtists() },
            harness =>
            {
                Assert.True(harness.Contains("artist:1:Ann"));
                Assert.True(harness.Contains("npmore:1:2"));
                Assert.False(harness.Contains("artist:2:Bob"));
                Assert.Equal(1, WithRole(harness, AutomationRole.Hyperlink));   // the first name links; the title has no route here
            }, UnderClassic());

    [Fact]
    public void The_marquee_credits_line_lists_every_name_and_an_inert_name_is_text()
        => ViewHarness.With(host, ViewKeys.PlayerBar,
            Props(MusicView() with { Marquee = true }) with { Credits = static () => TwoArtists() },
            harness =>
            {
                Assert.True(harness.Contains("artist:1:Ann"));
                Assert.True(harness.Contains("artist:2:Bob"));
                Assert.False(harness.Contains("npmore:1:2"));
                Assert.Equal(1, WithRole(harness, AutomationRole.Hyperlink));   // Bob has no route: inert, not a dead link
            }, UnderClassic());

    // ── the transport pieces, each alone ────────────────────────────────────────────────────────────────────────────────────

    sealed class FixtureRail : ISeekRailHost
    {
        public Prop<Affine2D> FillTransform => Affine2D.Identity;
        public Prop<Affine2D> ThumbTransform(float ringDiameter) => Affine2D.Identity;
        public Action<Point2> PointerDown { get; } = static _ => { };
        public Action<Point2> DragMove { get; } = static _ => { };
        public Action Commit { get; } = static () => { };
        public Action Cancel { get; } = static () => { };
        public Action<Point2> HoverMove { get; } = static _ => { };
        public Action PointerExit { get; } = static () => { };
        public Action<RectF> BoundsChanged { get; } = static _ => { };
        public Action<NodeHandle> ThumbRealized { get; } = static _ => { };
        public IReadSignal<bool> Playing { get; } = new Signal<bool>(false);
        public Element KeyLadderTick() => new BoxEl { Key = "test:key-ladder", Width = 0f, Height = 0f };
    }

    [Fact]
    public void Classic_draws_every_transport_piece()
        => host.Run(() =>
        {
            Assert.Equal("classic.player.seek-rail", ThemeHost.Registry.TemplateIdOf(ViewKeys.SeekRail.Id));
            Assert.Equal("classic.player.seek-chapters", ThemeHost.Registry.TemplateIdOf(ViewKeys.SeekChapters.Id));
            Assert.Equal("classic.player.chapter-tip", ThemeHost.Registry.TemplateIdOf(ViewKeys.ChapterTip.Id));
            Assert.Equal("classic.player.seek-readout", ThemeHost.Registry.TemplateIdOf(ViewKeys.SeekReadout.Id));
            Assert.Equal("classic.player.time-label", ThemeHost.Registry.TemplateIdOf(ViewKeys.TimeLabel.Id));
        });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void The_seek_rail_is_one_32_dip_slider_with_its_chapter_layer_and_the_ladder_clock(bool enabled, bool dvr)
        => ViewHarness.With(host, ViewKeys.SeekRail,
            new SeekRailProps { Host = new FixtureRail(), Enabled = enabled, Dvr = dvr, Chapters = new BoxEl { Key = "test:chapters" } },
            harness =>
            {
                var scene = harness.Shell.Host.Scene;
                var sliders = harness.Subtree(scene.Root).Where(n => scene.Interaction(n).Role == AutomationRole.Slider).ToList();
                Assert.Single(sliders);
                Assert.Equal(32f, harness.AbsoluteBounds(sliders[0]).H, 0.5f);
                Assert.True(harness.Contains("test:chapters"));
                Assert.True(harness.Contains("test:key-ladder"));
            }, UnderClassic());

    [Fact]
    public void With_nothing_to_rewind_the_rail_is_the_live_line_with_no_role()
        => ViewHarness.With(host, ViewKeys.SeekRail,
            new SeekRailProps { Host = new FixtureRail(), Enabled = true, Line = true },
            harness =>
            {
                Assert.Equal(0, WithRole(harness, AutomationRole.Slider));
                Assert.False(harness.Contains("test:key-ladder"));
            }, UnderClassic());

    [Fact]
    public void The_chapter_layer_keys_a_tick_per_chapter_and_the_tips_anchor()
        => ViewHarness.With(host, ViewKeys.SeekChapters,
            new SeekChaptersProps { Fractions = [0.25f, 0.5f], Width = new FloatSignal(400f), TipFraction = static () => 0.3f, TipAnchorRealized = static _ => { } },
            harness =>
            {
                Assert.True(harness.Contains("chapter-tick:0"));
                Assert.True(harness.Contains("chapter-tick:1"));
                Assert.False(harness.Contains("chapter-tick:2"));
                Assert.True(harness.Contains("chapter-tip-anchor"));
            }, UnderClassic());

    [Fact]
    public void The_chapter_tip_is_the_260_dip_bubble()
        => ViewHarness.With(host, ViewKeys.ChapterTip,
            new ChapterTipProps { Time = static () => "1:00", Title = static () => "Intro", Range = static () => "0:00 – 2:00", HasChapter = static () => true },
            harness =>
            {
                var scene = harness.Shell.Host.Scene;
                Assert.Contains(harness.Subtree(scene.Root), n => Math.Abs(harness.AbsoluteBounds(n).W - 260f) < 0.5f);
            }, UnderClassic());

    [Fact]
    public void The_readout_mounts_its_bubble()
        => ViewHarness.With(host, ViewKeys.SeekReadout, new SeekReadoutProps { Text = static () => "1:23" },
            harness =>
            {
                var scene = harness.Shell.Host.Scene;
                Assert.Contains(harness.Subtree(scene.Root), n => harness.AbsoluteBounds(n).H > 0f && harness.AbsoluteBounds(n).W < 200f);
            }, UnderClassic());

    static TimeLabelProps Label(bool remaining, bool live = false, bool liveSlot = false, bool offers = false) => new()
    {
        Remaining = remaining, IsLive = live, LiveSlot = liveSlot,
        Label = static () => "1:23", ToggleRemaining = static () => { },
        OffersGoLive = offers ? static () => true : static () => false,
        GoLiveText = static () => "GO LIVE", GoLive = static () => { },
    };

    static bool HasWidth(ViewHarness harness, float width)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Any(n => Math.Abs(harness.AbsoluteBounds(n).W - width) < 0.5f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_time_label_is_the_44_dip_slot(bool remaining)
        => ViewHarness.With(host, ViewKeys.TimeLabel, Label(remaining),
            harness => Assert.True(HasWidth(harness, 44f)), UnderClassic());

    [Theory]
    [InlineData(false, 0)]     // at the live edge: the LIVE word-mark, no action
    [InlineData(true, 1)]      // behind a rewindable window: the GO LIVE text button
    public void The_live_slot_is_104_dip_and_offers_go_live_only_behind_the_edge(bool offers, int buttons)
        => ViewHarness.With(host, ViewKeys.TimeLabel, Label(remaining: true, live: true, liveSlot: true, offers: offers),
            harness =>
            {
                Assert.True(HasWidth(harness, 104f));
                Assert.Equal(buttons, WithRole(harness, AutomationRole.Button));
            }, UnderClassic());
}
