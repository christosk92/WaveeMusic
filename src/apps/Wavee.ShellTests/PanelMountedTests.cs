// ── Wavee.ShellTests/PanelMountedTests.cs — the Classic right panel and the Player family's T6b views, mounted (themes plan 6.1) ─
//
// The panel is the structure's since T6b: the core keeps the state machine (`Shell.PanelHost`: open, mode, committed width, floats, the
// docked video; the width and fit rules; the coupling) and hands the structure's `Shell.Panel` one decided `PanelView` per render. The
// hero, the friends body, the deck's style flyout, the device picker's menu and the on-media strip are keys of their own. What only a
// mounted tree can say:
//
//   the registered views   Classic registers all nine keys
//   the real shell         the panel opened on the now-playing mode draws the hero's cover at the window's right edge at the side the
//                          width derives; switching modes keeps the tile where it was; a window too narrow to keep the content wide enough
//                          floats the panel, still at the right edge; the friends body is mounted exactly while it is on show
//   the arms               `ViewHarness` mounts the panel alone with fixture bodies: each mode shows its own body and header (the video
//                          header keeps all three buttons), the docked card mounts unconditionally in the other arms and is the pinned
//                          slot of the now-playing arm only when the video is docked; a closed panel is seeded off-canvas (no fly-in)
//   the faces              the friends body's skeleton, offline, error (Retry only when the host can retry) and empty faces; the device
//                          picker's rows follow its entries and re-key on a change; the on-media strip's buttons stand
//   the small views        the lyrics header composes its toggle, sparkle and inspector from the facts and the gates (leftovers P3);
//                          the "Next up" view draws a row per published row; the lyrics peek is nothing, the note or the reel
//
// The decisions themselves (the pinned slot's occupant, the picker's entries) are `Wavee.Tests\PanelContractTests.cs`.

using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class PanelMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static NodeHandle Root(ViewHarness harness) => harness.Shell.Host.Scene.Root;

    static int Buttons(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(Root(harness)).Count(n => scene.Interaction(n).Role == AutomationRole.Button);
    }

    static int MenuRows(ViewHarness harness)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(Root(harness)).Count(n => scene.Interaction(n).Role is AutomationRole.MenuItem or AutomationRole.RadioButton);   // a radio row is a RadioButton
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FixtureStyleVerbs : IDeckStyleVerbs
    {
        public void SetStyle(int id) { }
        public void SetChoice(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option, int index) { }
        public Signal<int> ChoiceMirror(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option) => new(0);
        public void SetPresentation(int presentation, DeckStyleSource source) { }
        public void OpenStyleFlyout() { }
        public void ToggleStyleFlyout() { }
    }

    sealed class FixtureFeed(Signal<Rail.Friends.FeedState> state) : IFriendsFeed
    {
        public ReadOnlySpan<FriendEdge> Read(out Rail.Friends.FeedState feedState)
        {
            feedState = state.Value;
            return default;
        }
    }

    static NowPlayingHeroProps HeroProps() => new()
    {
        NowTrack = static () => default,
        NowArtUrl = static () => null,
        RailWidth = new FloatSignal(340f),
        Deck = static () => new BoxEl { Key = "test:deck", Height = 100f },
        Style = new FixtureStyleVerbs(),
        Flyout = new DeckStyleFlyoutProps { Verbs = new FixtureStyleVerbs(), NowArtUrl = static () => null },
        SubscribeEntities = static () => { },
        DemandNowPlaying = static (_, _) => { },
        HasNextUp = static () => true,   // the video body (the structure's since T7c) places the "Next up" rows: their fixture is what says it mounted
        NextUp = new NextUpProps { Read = static () => [] },
        LyricsPeek = static () => new BoxEl { Key = "test:peek", Height = 20f },
        ResolveString = static _ => "",
        GoTo = static (_, _) => { },
    };

    static FriendsProps FriendsFixture(Signal<Rail.Friends.FeedState> state, List<bool>? active = null, Action? refresh = null) => new()
    {
        Feed = new FixtureFeed(state),
        SetActive = a => active?.Add(a),
        NowMs = static () => 0L,
        Refresh = () => refresh,
        GoTo = static (_, _) => { },
        OpenProfile = static _ => null,
        ResolveString = static _ => "",
    };

    static PanelProps Props(Signal<PanelView> view, FloatSignal? dockedHeight = null, Signal<Rail.Friends.FeedState>? friends = null, List<bool>? active = null) => new()
    {
        Read = () => view.Value,
        InlineAndOpen = static () => false,
        BodyHeightChanged = static _ => { },
        DockedVideoHeight = dockedHeight ?? new FloatSignal(200f),
        DockedVideoMin = static w => w * 9f / 16f,
        DockedVideoMax = static () => 600f,
        CommitDockedVideo = static () => { },
        Close = static () => { },
        ShowArt = static () => { },
        ShowVideoDocked = static () => { },
        VideoToMiniPlayer = static () => { },
        VideoToFullScreen = static () => { },
        DockedCard = static () => new BoxEl { Key = "test:docked", Height = 10f },
        LyricsHeader = HeaderFixture(),
        LyricsBody = static () => new BoxEl { Key = "test:lyrics", Grow = 1f },
        QueueBody = static () => new BoxEl { Key = "test:queue", Grow = 1f },
        SubscribeVideoBody = static () => { },
        NowPlaying = HeroProps(),
        Friends = FriendsFixture(friends ?? new Signal<Rail.Friends.FeedState>(Rail.Friends.FeedState.Offline), active),
    };

    static PanelLyricsHeaderProps HeaderFixture(Signal<LyricsHeaderFacts>? facts = null, Signal<LyricsSparkleFacts>? sparkle = null,
        Signal<bool>? inspector = null) => new()
    {
        Read = facts is null ? static () => new LyricsHeaderFacts("Lyrics", false, false, "", "Expand") : () => facts.Value,
        ToggleSecondLine = static () => { },
        Expand = static _ => { },
        Sparkle = sparkle is null ? static () => new LyricsSparkleFacts(false, false, false, "", "", false, false, false) : () => sparkle.Value,
        SparkleClick = static _ => { },
        InspectorShown = inspector is null ? static () => false : () => inspector.Value,
        OpenInspector = static _ => { },
        Close = static () => { },
    };

    static PanelView View(PanelMode body, bool open = true, bool dockedHere = false, PanelPinned pinned = PanelPinned.Art) => new()
    {
        Open = open, Width = 340f, Floats = false, Body = body, DockedHere = dockedHere, Pinned = pinned,
    };

    // ── the registered views ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_panel_view()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.ShellPanel.Id, "classic.shell.panel"),
                (ViewKeys.PanelFriends.Id, "classic.panel.friends"),
                (ViewKeys.NowPlayingHero.Id, "classic.player.now-playing-hero"),
                (ViewKeys.DeckStyleFlyout.Id, "classic.player.deck-style-flyout"),
                (ViewKeys.DevicePicker.Id, "classic.player.device-picker"),
                (ViewKeys.OnMediaTransport.Id, "classic.player.on-media-transport"),
                (ViewKeys.PanelLyricsHeader.Id, "classic.panel.lyrics-header"),
                (ViewKeys.PanelNextUp.Id, "classic.panel.next-up"),
                (ViewKeys.LyricsPeek.Id, "classic.player.lyrics-peek"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the real shell ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static void CloseRail()
    {
        Shell.Ui.RailOpen.Value = false;
        Shell.Ui.Mode.Value = Shell.RailMode.Lyrics;
    }

    [Fact]
    public void The_now_playing_panel_draws_the_hero_cover_at_the_right_edge_and_keeps_it_across_mode_switches()
        => host.Mounted(shell =>
        {
            try
            {
                Play("spotify:track:tr40");
                Shell.Ui.Mode.Value = Shell.RailMode.NowPlaying;
                Shell.Ui.RailOpen.Value = true;
                shell.RunFrames(30);

                var scene = shell.Host.Scene;
                var cover = Node(shell, "cover");
                var rect = scene.AbsoluteRect(cover);
                float width = Shell.Ui.RailWidth.Peek();
                Assert.Equal(width - 2f * Rail.Hero.Inset, rect.W, 0.5f);               // the tile's width is derived from the committed width
                Assert.True(rect.X >= 1180f - width - 0.5f, "the tile stands inside the panel at the window's right edge");
                Assert.True(rect.X + rect.W <= 1180f + 0.5f);

                // A mode switch and back: the tile comes back exactly where it was (the arm is rebuilt from the same elements).
                Shell.Ui.Mode.Value = Shell.RailMode.Queue;
                shell.RunFrames(10);
                Shell.Ui.Mode.Value = Shell.RailMode.NowPlaying;
                shell.RunFrames(30);
                var again = scene.AbsoluteRect(Node(shell, "cover"));
                Assert.Equal(rect.X, again.X, 0.5f);
                Assert.Equal(rect.Y, again.Y, 0.5f);
                Assert.Equal(rect.W, again.W, 0.5f);
            }
            finally
            {
                CloseRail();
                shell.RunFrames(30);
            }
        });

    [Fact]
    public void A_small_window_floats_the_panel_340_wide_inset_from_the_right_edge()
        => host.Mounted(shell =>
        {
            try
            {
                Play("spotify:track:tr40");
                Shell.Ui.Mode.Value = Shell.RailMode.NowPlaying;
                Shell.Ui.RailOpen.Value = true;
                shell.RunFrames(20);
                Assert.True(Shell.Ui.RailFits.Peek());                                  // a Standard window docks the panel

                shell.Resize(760f, 760f);
                shell.Host.Paint(0, keepAlive: true);
                shell.Host.RunFrame();
                shell.RunFrames(3);
                Until(shell, () => !Shell.Ui.RailFits.Peek(), "a Small window floats the panel (TC2)");
                Assert.Equal(Wavee.Theming.Contracts.PanelPlacement.Float, Shell.Ui.PanelPlacement.Peek());

                shell.RunFrames(30);
                var host = shell.Host.Scene.AbsoluteRect(Node(shell, Wavee.Themes.Classic.ClassicPanelTiers.HostKey));
                Assert.Equal(Wavee.Themes.Classic.ClassicPanelTiers.FloatW, host.W, 0.5f);
                Assert.Equal(760f - Wavee.Themes.Classic.ClassicPanelTiers.Gap, host.X + host.W, 0.5f);   // 8 in from the right edge
                var rect = shell.Host.Scene.AbsoluteRect(Node(shell, "cover"));
                Assert.True(rect.X >= host.X - 0.5f && rect.X + rect.W <= host.X + host.W + 0.5f, "the hero sits in the floating panel");
            }
            finally
            {
                CloseRail();
                shell.Resize(1180f, 760f);
                shell.RunFrames(30);
            }
        });

    [Fact]
    public void The_friends_body_is_mounted_exactly_while_it_is_on_show()
        => host.Mounted(shell =>
        {
            var calls = new List<bool>();
            var previous = Rail.Friends.SetActive;
            Rail.Friends.SetActive = calls.Add;
            try
            {
                Shell.Ui.Mode.Value = Shell.RailMode.Friends;
                Shell.Ui.RailOpen.Value = true;
                shell.RunFrames(20);
                Assert.Equal(new[] { true }, calls);                                            // mount == visible

                Shell.Ui.Mode.Value = Shell.RailMode.Queue;
                shell.RunFrames(20);
                Assert.Equal(new[] { true, false }, calls);                                     // another mode took the body off
            }
            finally
            {
                Rail.Friends.SetActive = previous;
                CloseRail();
                shell.RunFrames(30);
            }
        });

    // ── the arms (the panel alone) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_queue_arm_has_the_queue_body_the_close_button_and_an_always_mounted_docked_card()
    {
        var view = new Signal<PanelView>(View(PanelMode.Queue));
        ViewHarness.With(host, ViewKeys.ShellPanel, Props(view), harness =>
        {
            Assert.True(harness.Contains("test:queue"));
            Assert.False(harness.Contains("test:lyrics"));
            Assert.True(harness.Contains("test:docked"));                               // the card's own gate hides it: it mounts unconditionally
            Assert.Equal(1, Buttons(harness));                                          // the close button

            view.Value = View(PanelMode.Lyrics);                                        // a mode switch: the bodies swap, the card stays
            harness.RunFrames(3);
            Assert.True(harness.Contains("test:lyrics"));
            Assert.False(harness.Contains("test:queue"));
            Assert.True(harness.Contains("test:docked"));
            Assert.Equal(2, Buttons(harness));                                          // the lyrics header (`Panel.LyricsHeader`): expand and close

            view.Value = View(PanelMode.Video);
            harness.RunFrames(3);
            Assert.False(harness.Contains("test:lyrics"));
            Assert.Equal(3, Buttons(harness));                                          // mini player, full screen and close: all three stay
        }, UnderClassic());
    }

    [Fact]
    public void A_docked_video_gets_the_cap_the_height_cell_the_splitter_writes()
    {
        var view = new Signal<PanelView>(View(PanelMode.Video, dockedHere: true));
        ViewHarness.With(host, ViewKeys.ShellPanel, Props(view, new FloatSignal(220f)), harness =>
        {
            var card = harness.Node("test:docked");
            var cap = harness.Shell.Host.Scene.Parent(card);
            Assert.Equal(220f, harness.AbsoluteBounds(cap).H, 0.5f);
        }, UnderClassic());
    }

    [Fact]
    public void The_now_playing_arm_pins_the_docked_video_only_when_it_is_docked()
    {
        var view = new Signal<PanelView>(View(PanelMode.NowPlaying, pinned: PanelPinned.Art));
        ViewHarness.With(host, ViewKeys.ShellPanel, Props(view), harness =>
        {
            Assert.False(harness.Contains("test:docked"));                              // this arm has no unconditional card
            Assert.Equal(0, Buttons(harness));                                          // no header, no switch

            view.Value = View(PanelMode.NowPlaying, pinned: PanelPinned.ArtWithVideoToggle);
            harness.RunFrames(3);
            Assert.False(harness.Contains("test:docked"));
            Assert.Equal(2, Buttons(harness));                                          // the Art | Video switch over the tile's corner

            view.Value = View(PanelMode.NowPlaying, dockedHere: true, pinned: PanelPinned.DockedVideo);
            harness.RunFrames(3);
            Assert.True(harness.Contains("test:docked"));                               // the docked video IS the slot
            Assert.Equal(0, Buttons(harness));
        }, UnderClassic());
    }

    [Fact]
    public void A_closed_panel_is_seeded_off_canvas_and_an_open_one_in_place()
    {
        ViewHarness.With(host, ViewKeys.ShellPanel, Props(new Signal<PanelView>(View(PanelMode.Queue, open: false))), harness =>
        {
            var closed = harness.AbsoluteBounds(harness.Node("test:queue"));
            Assert.True(closed.X >= 340f - 0.5f, "a closed panel waits one width to the right: no fly-in at startup");
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.ShellPanel, Props(new Signal<PanelView>(View(PanelMode.Queue, open: true))), harness =>
        {
            Assert.Equal(0f, harness.AbsoluteBounds(harness.Node("test:queue")).X, 0.5f);
        }, UnderClassic());
    }

    // ── the small views (leftovers P3) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lyrics_header_composes_the_toggle_the_sparkle_and_the_inspector_from_its_facts_and_gates()
    {
        var facts = new Signal<LyricsHeaderFacts>(new LyricsHeaderFacts("Lyrics", false, false, "", "Expand"));
        var sparkle = new Signal<LyricsSparkleFacts>(new LyricsSparkleFacts(false, false, false, "", "", false, false, false));
        var inspector = new Signal<bool>(false);
        ViewHarness.With(host, ViewKeys.PanelLyricsHeader, HeaderFixture(facts, sparkle, inspector), harness =>
        {
            Assert.Equal(2, Buttons(harness));                                           // expand and close

            facts.Value = new LyricsHeaderFacts("Lyrics", true, true, "Romanization", "Expand");
            harness.RunFrames(3);
            Assert.Equal(3, Buttons(harness));                                           // the globe joins when the document has a second layer

            sparkle.Value = new LyricsSparkleFacts(true, false, false, "Timed by AI", "t", false, false, true);
            harness.RunFrames(3);
            Assert.Equal(4, Buttons(harness));                                           // the sparkle shows while the feature can run

            inspector.Value = true;
            harness.RunFrames(3);
            Assert.Equal(5, Buttons(harness));                                           // the developer inspector, gated live

            sparkle.Value = new LyricsSparkleFacts(false, false, false, "", "", false, false, false);
            inspector.Value = false;
            harness.RunFrames(3);
            Assert.Equal(3, Buttons(harness));
        }, UnderClassic());
    }

    [Fact]
    public void The_next_up_view_draws_a_row_per_published_row()
    {
        static NextUpRow Row(int i) => new("vrp:e" + i + ":noart", false,
            new Controls.CardData("spotify:track:fixture" + i, "Title " + i, null, null, static () => { }));
        var rows = new Signal<NextUpRow[]>([Row(0)]);
        ViewHarness.With(host, ViewKeys.PanelNextUp, new NextUpProps { Read = () => rows.Value }, harness =>
        {
            int one = Buttons(harness);
            Assert.True(one > 0, "a row is a pressable surface");

            rows.Value = [Row(0), Row(1)];
            harness.RunFrames(3);
            Assert.Equal(2 * one, Buttons(harness));

            rows.Value = [];
            harness.RunFrames(3);
            Assert.Equal(0, Buttons(harness));
        }, UnderClassic());
    }

    [Fact]
    public void The_lyrics_peek_is_nothing_the_note_or_the_reel()
    {
        Action open = static () => { };
        ViewHarness.With(host, ViewKeys.LyricsPeek, new LyricsPeekProps { Face = LyricsPeekFace.None, OpenLyrics = open }, harness =>
        {
            Assert.Equal(0, Buttons(harness));
            Assert.Empty(harness.FindPrefix("l:"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.LyricsPeek, new LyricsPeekProps { Face = LyricsPeekFace.Note, OpenLyrics = open }, harness =>
        {
            Assert.Equal(1, Buttons(harness));                                           // the note still opens the panel on lyrics
            Assert.Empty(harness.FindPrefix("l:"));
        }, UnderClassic());

        var reel = new LyricsPeekProps { Face = LyricsPeekFace.Reel, Active = 3, ActiveText = "the sung line", Peek = 4, PeekText = "the next", OpenLyrics = open };
        ViewHarness.With(host, ViewKeys.LyricsPeek, reel, harness =>
        {
            harness.RunFrames(30);                                                       // run the rows' flip-in out
            Assert.Equal(1, Buttons(harness));
            Assert.True(harness.Contains("l:3"));
            Assert.True(harness.Contains("l:4"));
            Assert.Equal(56f, harness.AbsoluteBounds(harness.Node("l:3")).H, 0.5f);       // one peek row (ClassicHeroLayout.PeekRowH)
        }, UnderClassic());
    }

    // ── the friends body's faces ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_friends_body_has_a_face_for_every_state_and_offers_retry_only_when_the_host_can_retry()
    {
        var state = new Signal<Rail.Friends.FeedState>(Rail.Friends.FeedState.Loading);
        var active = new List<bool>();
        bool retried = false;
        var props = FriendsFixture(state, active, refresh: () => retried = true);
        ViewHarness.With(host, ViewKeys.PanelFriends, props, harness =>
        {
            int Nodes() => harness.Subtree(Root(harness)).Count();

            Assert.Equal(new[] { true }, active);                                        // mounted == visible
            Assert.Equal(0, Buttons(harness));
            int skeleton = Nodes();                                                      // five skeleton rows

            state.Value = Rail.Friends.FeedState.Idle;
            harness.RunFrames(3);
            Assert.Equal(skeleton, Nodes());                                             // idle and loading are one face

            state.Value = Rail.Friends.FeedState.Ready;
            harness.RunFrames(3);
            int empty = Nodes();
            Assert.True(skeleton > empty, "the skeleton is the tallest face");

            state.Value = Rail.Friends.FeedState.Offline;
            harness.RunFrames(3);
            Assert.True(skeleton > Nodes());
            Assert.Equal(0, Buttons(harness));

            state.Value = Rail.Friends.FeedState.Error;
            harness.RunFrames(3);
            Assert.Equal(1, Buttons(harness));                                           // the stock Retry
            Assert.False(retried);
        }, UnderClassic());
    }

    [Fact]
    public void The_error_face_has_no_retry_when_the_host_cannot_retry()
    {
        var state = new Signal<Rail.Friends.FeedState>(Rail.Friends.FeedState.Error);
        ViewHarness.With(host, ViewKeys.PanelFriends, FriendsFixture(state), harness => Assert.Equal(0, Buttons(harness)), UnderClassic());
    }

    // ── the device picker ────────────────────────────────────────────────────────────────────────────────────────────────────

    static DevicePickerEntry Info(string label) => new(DevicePickerEntryKind.Info, label, "", false, false, null, null);

    static DevicePickerView Picker(int stamp, int radios)
    {
        var entries = new List<DevicePickerEntry>
        {
            Info("This computer"),
            new(DevicePickerEntryKind.Command, "System default", Icons.ThisPc, true, false, null, static () => { }),
            Info("Playing 320 kbps"),
            new(DevicePickerEntryKind.Separator, "", "", false, false, null, null),
            Info("Spotify Connect"),
        };
        for (int i = 0; i < radios; i++)
            entries.Add(new(DevicePickerEntryKind.Radio, "Phone " + i, Icons.CellPhone, true, i == 0, null, static () => { }));
        return new DevicePickerView(entries, stamp);
    }

    [Fact]
    public void The_device_picker_draws_a_row_per_entry_but_the_separator_and_follows_a_change_while_open()
    {
        var view = new Signal<DevicePickerView>(Picker(1, 1));
        var props = new DevicePickerProps { Read = () => view.Value, MaxHeight = static h => Shell.DeviceRoster.PickerMaxHeight(h), Close = static () => { } };
        ViewHarness.With(host, ViewKeys.DevicePicker, props, harness =>
        {
            Assert.Equal(5, MenuRows(harness));                                          // 3 information rows, the command and the radio

            view.Value = Picker(2, 3);                                                   // a transfer landed, the roster grew
            harness.RunFrames(3);
            Assert.Equal(7, MenuRows(harness));
        }, UnderClassic());
    }

    [Fact]
    public void The_empty_roster_picker_states_it_with_disabled_rows_and_no_radio()
    {
        var props = new DevicePickerProps { Read = static () => Picker(1, 0), MaxHeight = static h => h, Close = static () => { } };
        ViewHarness.With(host, ViewKeys.DevicePicker, props, harness => Assert.Equal(4, MenuRows(harness)), UnderClassic());
    }

    // ── the on-media strip ───────────────────────────────────────────────────────────────────────────────────────────────────

    static OnMediaTransportProps OnMediaFixture(Signal<bool> visible, Signal<OnMediaFacts> facts) => new()
    {
        Visible = visible,
        Feed = new PlayerChromeFeed(),
        Read = () => facts.Value,
        Volume = new Signal<float>(1f),
        VolumeCell = new FloatSignal(1f),
        Previous = static () => { },
        Next = static () => { },
        TogglePlay = static () => { },
        ToggleMute = static () => { },
        SetVolume = static _ => { },
        MoreMenu = static () => [],
        SeekBar = static (_, _) => new BoxEl { Key = "test:seek", Height = 32f },
        TimeText = static (remaining, _, _) => new BoxEl { Key = remaining ? "test:remaining" : "test:elapsed", Width = 44f, Height = 16f },
        FadeChrome = static (_, _, _) => { },
    };

    [Fact]
    public void The_on_media_strip_stands_with_its_transport_its_seek_rail_its_times_and_its_three_trailing_buttons()
    {
        var visible = new Signal<bool>(true);
        var facts = new Signal<OnMediaFacts>(new OnMediaFacts(Playing: true, CanPlay: true, PrevEnabled: true, NextEnabled: true, Muted: false));
        ViewHarness.With(host, ViewKeys.OnMediaTransport, OnMediaFixture(visible, facts), harness =>
        {
            Assert.True(harness.Contains("test:seek"));
            Assert.True(harness.Contains("test:elapsed"));
            Assert.True(harness.Contains("test:remaining"));
            Assert.Equal(6, Buttons(harness));                                           // previous, play, next, mute, volume, more

            // Concealed, the strip stays mounted (the seek rail and the times keep their place) and takes the fade's terminal.
            visible.Value = false;
            harness.RunFrames(3);
            Assert.True(harness.Contains("test:seek"));
        }, UnderClassic());
    }
}
