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
//                         transport, the podcast bar's narrowest level (the cover alone), a video track's split, the error state
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
        ArtistsLineFull = static () => new FixtureLine(),
        ArtistsLineCompact = static () => new BoxEl { Key = "test:credits", Height = 14f },
        OverflowRow = static (_, _) => new FluentGpu.Controls.MenuFlyoutItem("row", default, true),
        VideoPlacementMenu = static () => [],
    };

    sealed class FixtureLine : Component
    {
        public override Element Render() => new BoxEl { Key = "test:credits", Height = 14f };
    }

    // ── the registered view ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_player_bar()
        => host.Run(() => Assert.Equal("classic.player.bar", ThemeHost.Registry.TemplateIdOf(ViewKeys.PlayerBar.Id)));

    [Fact]
    public void The_mounted_shell_draws_the_bar_through_the_classic_template()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
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
}
