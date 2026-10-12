// ── Wavee.ShellTests/StageFrameMountedTests.cs — the stage's frame, mounted (themes plan 6.1, T7a1) ─────────────────────────────
//
// The fullscreen stage's frame is the structure's since T7a1: the root (the layer stack, the chrome's placement), the top bar, the transport
// card, the options popover, the gallery and the caption. The core surface keeps the window, the focus scope, the idle machine, the layout
// classes and the key map (`Wavee.Tests\StageModelTests.cs`, `StageLayoutTests.cs` pin the pure ones; `LiveOptionBehaviourTests` pins the
// cover's FLIP spring on the same stage). What only a mounted tree can say:
//
//   registered   Classic registers every stage key, and the real shell opened on the stage draws its stack through them: the backdrop, the
//                smoke under the Card face, the chrome's three placed layers, no hairline while the chrome shows
//   chrome       the top bar is the allocator's 48 DIP tall, the transport card 112 on the desktop class and 96 on Compact (which has no
//                gallery), the gallery a right-docked column on the desktop class and a full-width sheet on Portrait
//   keys         Escape and F11 close the stage; the shell body is collapsed while it is up and back after
//   fixtures     `ViewHarness` mounts the top bar, the transport card (the play disc follows the class), the options popover, the gallery
//                (one keyed tile per shown face) and the caption (its keyed slots) alone with fixture props

using System.Text.RegularExpressions;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class StageFrameMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── the real stage ───────────────────────────────────────────────────────────────────────────────────────────────

    static void OpenStage(Shell.HeadlessShell shell)
    {
        Prefs.Stage.SetMode((int)Stage.Mode.Visualizer);
        Prefs.Stage.SetLayout((int)Stage.VizLayout.Card);
        Prefs.Stage.SetGalleryOpen(true);
        Stage.Open(null, "test");
        Until(shell, () => !shell.Find("stage:cover").IsNull, "the stage's cover mounted");
        shell.RunFrames(120);   // the entrance and every spring settle
    }

    static void CloseStage(Shell.HeadlessShell shell)
    {
        Stage.Close(null, "test");
        shell.RunFrames(60);
        Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
        Prefs.Stage.SetLayout(Platform.Keys.StageLayout.Default);
        Prefs.Stage.SetGalleryOpen(Platform.Keys.StageGalleryOpen.Default);
    }

    /// <summary>The stage's key map lives on its root box; a click on the stage leaves focus inside it.</summary>
    static void FocusStage(Shell.HeadlessShell shell)
    {
        shell.Host.Input.SetFocus(shell.Host.Scene.Parent(Node(shell, "stage:backdrop")));
        shell.RunFrames(1);
    }

    /// <summary>The placed box under a keyed chrome layer (the layer is full-bleed; its first child is the bar, the card or the pane).</summary>
    static RectF Placed(Shell.HeadlessShell shell, string layerKey)
        => shell.Host.Scene.PresentedAbsoluteRect(shell.Host.Scene.FirstChild(Node(shell, layerKey)));

    [Fact]
    public void Classic_is_the_registered_template_of_every_stage_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.StageRoot.Id, "classic.stage.root"),
                (ViewKeys.StageTopBar.Id, "classic.stage.top-bar"),
                (ViewKeys.StageTransport.Id, "classic.stage.transport"),
                (ViewKeys.StageOptions.Id, "classic.stage.options"),
                (ViewKeys.StageGallery.Id, "classic.stage.gallery"),
                (ViewKeys.StageCaption.Id, "classic.stage.caption"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    [Fact]
    public void The_real_stage_draws_its_stack_and_chrome_through_the_registered_root()
        => host.Mounted(shell =>
        {
            Assert.True(FrameFacts.ChromeMounted(), "the shell body is mounted before the stage opens");
            OpenStage(shell);
            try
            {
                Assert.False(FrameFacts.ChromeMounted(), "the stage collapses the shell body (title row, content region, player bar)");
                foreach (string key in new[] { "stage:backdrop", "stage:cover", "stage:smoke", "stage:chrome", "chrome:top", "chrome:transport" })
                    Node(shell, key);
                Assert.True(shell.Find("stage:hairline").IsNull, "the hairline shows only while the chrome rests");

                var stage = shell.Host.Scene.PresentedAbsoluteRect(shell.Host.Scene.Parent(Node(shell, "stage:backdrop")));
                Assert.Equal(1180f, stage.W, 0.5f);   // the root box is the allocator's W x H
                Assert.Equal(760f, stage.H, 0.5f);

                Assert.Equal(Stage.Layout.TopBarH, Placed(shell, "chrome:top").H, 0.5f);
                Assert.Equal(Stage.Layout.TransportDesktopH, Placed(shell, "chrome:transport").H, 0.5f);
                var gallery = Placed(shell, "chrome:gallery");
                Assert.Equal(Stage.Layout.GalleryDesktopW, gallery.W, 0.5f);   // the right-docked column
            }
            finally { CloseStage(shell); }
            Assert.True(FrameFacts.ChromeMounted(), "the shell body is back after the stage closes");
        });

    [Fact]
    public void Compact_has_the_short_card_and_no_gallery()
        => host.Mounted(shell =>
        {
            OpenStage(shell);
            try
            {
                Assert.Equal(Stage.Aspect.Compact, Stage.Layout.ClassOf(1000f, 420f, null));
                Assert.Equal(Stage.Layout.TransportCompactH, Placed(shell, "chrome:transport").H, 0.5f);
                Assert.True(shell.Find("chrome:gallery").IsNull, "Compact has no gallery pane");
            }
            finally { CloseStage(shell); }
        }, widthDip: 1000f, heightDip: 420f);

    [Fact]
    public void Portrait_parks_the_gallery_as_a_full_width_sheet()
        => host.Mounted(shell =>
        {
            OpenStage(shell);
            try
            {
                var sheet = Placed(shell, "chrome:gallery");
                Assert.Equal(700f, sheet.W, 0.5f);
                Assert.True(sheet.Y + sheet.H <= 1000f - 2f * Stage.Layout.Pad - Stage.Layout.TransportDesktopH + 0.5f, "the sheet is parked above the transport card and its two gutters");
            }
            finally { CloseStage(shell); }
        }, widthDip: 700f, heightDip: 1000f);

    [Fact]
    public void Escape_and_F11_close_the_stage()
        => host.Mounted(shell =>
        {
            foreach (int key in new[] { Keys.Escape, Keys.F11 })
            {
                OpenStage(shell);
                try
                {
                    FocusStage(shell);
                    Key(shell, key);
                    shell.RunFrames(2);
                    Assert.False(Shell.Ui.ImmersiveLyrics.Peek(), "key " + key + " left the stage open");
                }
                finally { CloseStage(shell); }
            }
        });

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static int Boxes(ViewHarness harness, float w, float h)
    {
        int n = 0;
        foreach (var node in harness.Subtree(harness.Shell.Host.Scene.Root))
        {
            var r = harness.AbsoluteBounds(node);
            if (MathF.Abs(r.W - w) < 0.5f && MathF.Abs(r.H - h) < 0.5f) n++;
        }
        return n;
    }

    static StageTransportProps Transport(Stage.StageCtx stage, StageTransportFacts facts) => new()
    {
        Stage = stage,
        Facts = () => facts,
        Verbs = PlayerVerbs.Default,
        SeekBar = static _ => new BoxEl { Key = "fixture:seek", Grow = 1f, Height = 32f },
        TimeText = static (_, _) => new BoxEl { Width = 40f, Height = 20f },
        Muted = static () => false,
        ToggleMute = static () => { },
        Volume = static () => 0.5f,
        SetVolume = static _ => { },
        Device = static () => new StageDeviceFacts(Icons.ThisPc, "This PC"),
        DeviceItems = static () => [],
        OpenDevicePicker = static () => { },
    };

    static readonly StageTransportFacts Facts = new(Playing: true, PrimaryEnabled: true, CanTransport: true, PrevEnabled: true, NextEnabled: true,
        Shuffle: false, RepeatOn: false, RepeatOne: false, Saved: false, Like: null, PlayingFrom: "Playing from album", Source: "An album");

    [Fact]
    public void The_transport_card_draws_the_class_s_play_disc_and_keeps_the_seek_row()
    {
        int desktopDiscs = 0, compactDiscs = 0, compactOldDiscs = 0;
        var desktop = new Stage.StageCtx();
        ViewHarness.With(host, ViewKeys.StageTransport, Transport(desktop, Facts), harness =>
        {
            Assert.True(harness.Contains("fixture:seek"), "the bar's own seek rail is placed in the card's first row");
            desktopDiscs = Boxes(harness, 56f, 56f);
        }, UnderClassic());

        var compact = new Stage.StageCtx();
        compact.Layout.Value = Stage.Layout.Resolve(1000f, 420f, null);
        Assert.Equal(Stage.Aspect.Compact, compact.Layout.Peek().Aspect);
        ViewHarness.With(host, ViewKeys.StageTransport, Transport(compact, Facts), harness =>
        {
            compactDiscs = Boxes(harness, 52f, 52f);
            compactOldDiscs = Boxes(harness, 56f, 56f);
        }, UnderClassic());

        Assert.True(desktopDiscs >= 1, "the 56 DIP play disc");
        Assert.True(compactDiscs >= 1, "Compact's 52 DIP play disc");
        Assert.Equal(0, compactOldDiscs);
    }

    [Fact]
    public void The_top_bar_mounts_alone_at_the_allocators_height()
    {
        var stage = new Stage.StageCtx();
        ViewHarness.With(host, ViewKeys.StageTopBar, new StageTopBarProps { Stage = stage }, harness =>
            Assert.True(harness.Subtree(harness.Shell.Host.Scene.Root).Any(n =>
            {
                var r = harness.AbsoluteBounds(n);
                return MathF.Abs(r.H - Stage.Layout.TopBarH) < 0.5f && r.W >= 600f;
            }), "the bar fills its wrapper at the allocator's height"),
            UnderClassic());
    }

    [Fact]
    public void The_options_popover_is_a_card_the_size_the_allocator_leaves()
    {
        var stage = new Stage.StageCtx();
        int closed = 0;
        ViewHarness.With(host, ViewKeys.StageOptions,
            new StageOptionsProps { Stage = stage, Verbs = StageSettingsVerbs.Shared, Close = () => closed++ }, harness =>
            {
                // 440 wide, 24 DIP from each edge of the 1920 x 1080 seed layout: 720 tall
                Assert.True(Boxes(harness, 440f, 720f) >= 1, "the popover card");
                Assert.Equal(0, closed);
            }, UnderClassic());
    }

    [Fact]
    public void The_gallery_draws_one_keyed_tile_per_shown_face()
    {
        var stage = new Stage.StageCtx();
        ViewHarness.With(host, ViewKeys.StageGallery, new StageGalleryProps { Stage = stage, Verbs = StageSettingsVerbs.Shared }, harness =>
        {
            harness.RunFrames(4);
            int tiles = harness.FindPrefix("viz:").Count(t => Regex.IsMatch(t.Key, @"^viz:\d+$"));
            Assert.Equal(Visualizer.Catalog.ShownCount, tiles);
            Assert.True(Boxes(harness, Stage.Layout.GalleryDesktopW, stage.Layout.Peek().GalleryH) >= 1, "the pane is this layout's gallery size");
        }, UnderClassic());
    }

    static StageCaptionProps Caption(bool dots) => new()
    {
        Width = 800f, X = 100f, Bottom = 180f, BlockMaxHeight = 220f,
        ActiveSize = 48f, ActiveLineHeight = 56f, ContextSize = 22f, ContextLineHeight = 29f,
        Generation = "7",
        PreviousIndex = 3, PreviousText = "before",
        NextIndex = 5, NextText = "after",
        ActiveIndex = 4, ActiveText = "now",
        Dots = dots, Anchor = 4, Bloom = new ColorF(1f, 0.5f, 0.5f, 1f),
        OnMain = static _ => { }, OnGlow = static _ => { }, OnDots = static _ => { },
        DotAlpha = [new FloatSignal(1f), new FloatSignal(1f), new FloatSignal(1f)],
        Ticker = [],
    };

    [Fact]
    public void The_caption_keys_its_slots_by_role_line_and_generation()
        => ViewHarness.With(host, ViewKeys.StageCaption, Caption(dots: false), harness =>
        {
            Assert.True(harness.Contains("cap:p:7:3"));
            Assert.True(harness.Contains("cap:a:7:4"));
            Assert.True(harness.Contains("cap:n:7:5"));
            Assert.False(harness.Contains("cap:d:7:4"));
        }, UnderClassic());

    [Fact]
    public void A_break_puts_the_dots_in_the_active_slot()
        => ViewHarness.With(host, ViewKeys.StageCaption, Caption(dots: true), harness =>
        {
            Assert.True(harness.Contains("cap:d:7:4"));
            Assert.False(harness.Contains("cap:a:7:4"));
            Assert.True(harness.Contains("cap:p:7:3"), "the line just sung stays above the dots");
        }, UnderClassic());
}
