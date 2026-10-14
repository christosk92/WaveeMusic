// ── Wavee.ShellTests/StageLayersMountedTests.cs — the stage's layout layers, mounted (themes plan 6.1, T7a2) ────────────────────
//
// The four layouts' layers are the structure's since T7a2: the backdrop, the Artist layout's hero photo and name, the face and its scrim, the
// spectrum, the now-playing card, the identity (the morphing cover and its titles) and the Artist pane. Which layout is drawn stays core
// (`Wavee.Tests\StageLayoutTests.cs`, `StageLayoutAxisTests.cs` pin the allocator; `LiveOptionBehaviourTests` pins the cover's FLIP spring on the
// real stage). What only a mounted tree can say:
//
//   registered   Classic registers every layer key, and the real stage draws its stack through them (the backdrop's field, the identity's cover and
//                titles at the allocator's size, the spectrum's strip in a layout)
//   identity     the cover is the allocator's size for the look and declares the bounds spring; a layout switch springs it; the titles follow
//   artist pane  nothing playing says so (no scroller), a playing artist with no facts yet shows an empty scroller (no section, no gap), facts add
//                their sections; the demand runs once per (artist, track, scope)

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class StageLayersMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    [Fact]
    public void Classic_is_the_registered_template_of_every_layer_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.StageBackdrop.Id, "classic.stage.backdrop"),
                (ViewKeys.StageHero.Id, "classic.stage.hero"),
                (ViewKeys.StageArtistName.Id, "classic.stage.artist-name"),
                (ViewKeys.StageFace.Id, "classic.stage.face"),
                (ViewKeys.StageFaceScrim.Id, "classic.stage.face-scrim"),
                (ViewKeys.StageSpectrum.Id, "classic.stage.spectrum"),
                (ViewKeys.StageNowPlayingCard.Id, "classic.stage.now-playing-card"),
                (ViewKeys.StageIdentity.Id, "classic.stage.identity"),
                (ViewKeys.StageArtistPane.Id, "classic.stage.artist-pane"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    [Fact]
    public void The_real_stage_draws_the_cover_at_the_allocators_size_through_the_registered_identity()
        => host.Mounted(shell =>
        {
            Prefs.Stage.SetMode((int)Stage.Mode.Visualizer);
            Prefs.Stage.SetLayout((int)Stage.VizLayout.Card);
            Stage.Open(null, "test");
            try
            {
                Until(shell, () => !shell.Find("stage:cover").IsNull, "the stage's cover mounted");
                shell.RunFrames(120);   // the entrance and every spring settle
                Node(shell, "stage:field");
                Node(shell, "stage:titles");
                var cover = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "stage:cover"));
                Assert.True(cover.W > 0f, "the cover has the allocator's size");
                Assert.Equal(cover.W, cover.H, 0.5f);
            }
            finally
            {
                Stage.Close(null, "test");
                shell.RunFrames(60);
                Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
                Prefs.Stage.SetLayout(Platform.Keys.StageLayout.Default);
            }
        });

    // ── the identity ─────────────────────────────────────────────────────────────────────────────────────────────────

    static StageIdentityProps Identity(Stage.StageCtx stage, Action? opened = null) => new()
    {
        Stage = stage,
        Title = static () => "A title",
        Meta = static _ => new StageMetaFacts("An artist · An album", true),
        Chips = static () => [new StageChipFact(null, "120 BPM")],
        NowPlayingMenu = static () => null,
        OpenArtist = _ => opened?.Invoke(),
    };

    [Fact]
    public void The_cover_is_the_allocators_size_and_a_layout_switch_springs_it()
    {
        var stage = new Stage.StageCtx();
        ViewHarness.With(host, ViewKeys.StageIdentity, Identity(stage), harness =>
        {
            harness.RunFrames(4);
            var cover = harness.Node("stage:cover");
            var card = stage.Look.Peek();
            Assert.Equal(stage.Layout.Peek().CoverSize(in card), harness.AbsoluteBounds(cover).W, 0.5f);
            Assert.True(harness.Shell.Host.Animation.TryGetTransition(cover, out LayoutTransition flip), "stage:cover declares no layout transition");
            Assert.Equal(TransitionChannels.Bounds, flip.Channels);
            harness.Node("stage:titles");

            var large = new Stage.Look(Stage.Mode.Visualizer, Stage.VizLayout.LargeArt, Stage.SpectrumStyle.Off, false, false);
            stage.Look.Value = large;
            harness.RunFrames(2);
            cover = harness.Node("stage:cover");
            Assert.True(harness.Shell.Host.Animation.HasTracks(cover), "Card → Large art snapped the cover instead of springing it");
            harness.RunFrames(180);
            Assert.Equal(stage.Layout.Peek().CoverSize(in large), harness.AbsoluteBounds(cover).W, 0.5f);
        }, UnderClassic());
    }

    [Fact]
    public void A_meta_line_without_a_page_opens_nothing_and_the_titles_still_draw()
    {
        int opened = 0;
        var stage = new Stage.StageCtx();
        var props = Identity(stage, () => opened++);
        ViewHarness.With(host, ViewKeys.StageIdentity, props with { Meta = static _ => new StageMetaFacts("An artist", false) }, harness =>
        {
            harness.RunFrames(2);
            Assert.Equal(0, opened);
            Assert.True(harness.Contains("stage:titles"));
        }, UnderClassic());
    }

    // ── the spectrum and the backdrop ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_layout_draws_its_strip_and_card_draws_none()
    {
        var stage = new Stage.StageCtx();
        stage.Look.Value = new Stage.Look(Stage.Mode.Visualizer, Stage.VizLayout.LargeArt, Stage.SpectrumStyle.Bars, false, false);
        ViewHarness.With(host, ViewKeys.StageSpectrum, new StageLayerProps { Stage = stage }, harness =>
        {
            harness.RunFrames(2);
            Assert.Single(harness.FindPrefix("strip:"));
            stage.Look.Value = Stage.Look.Plain(Stage.Mode.Visualizer);
            harness.RunFrames(120);
            Assert.Empty(harness.FindPrefix("strip:"));
        }, UnderClassic());
    }

    [Fact]
    public void The_backdrop_mounts_its_field()
    {
        var stage = new Stage.StageCtx();
        ViewHarness.With(host, ViewKeys.StageBackdrop, new StageBackdropProps { Stage = stage, Shown = static () => true }, harness =>
        {
            harness.RunFrames(2);
            Assert.False(harness.Node("stage:field").IsNull);
        }, UnderClassic());
    }

    // ── the Artist pane ──────────────────────────────────────────────────────────────────────────────────────────────

    static StageArtistPaneProps ArtistPane(Stage.StageCtx stage, bool valid, Action<int, int>? demand = null,
        StageArtistPopularFacts? popular = null) => new()
    {
        Stage = stage,
        Target = () => new StageArtistTarget(valid, valid ? 4 : 0, valid ? 9 : 0, 1u),
        Demand = demand ?? ((_, _) => { }),
        Header = static _ => null,
        About = static _ => null,
        Song = static _ => null,
        Popular = _ => popular ?? new StageArtistPopularFacts(1u, 1u, false, []),
        Cities = static _ => null,
        Fans = static _ => new StageArtistFansFacts(1u, 1u, 0, []),
        EnsurePopular = static _ => { },
        EnsureRelated = static _ => { },
        GoToArtist = static (_, _) => { },
        OpenLink = static (_, _) => { },
    };

    [Fact]
    public void Nothing_playing_says_so_and_a_playing_artist_without_facts_shows_an_empty_pane()
    {
        int quiet = -1, empty = -1;
        ViewHarness.With(host, ViewKeys.StageArtistPane, ArtistPane(new Stage.StageCtx(), valid: false), harness =>
        {
            harness.RunFrames(2);
            Assert.True(ScrollUnder(harness.Shell, harness.Shell.Host.Scene.Root).IsNull, "nothing playing draws no scroller");
            quiet = CountUnder(harness.Shell, harness.Shell.Host.Scene.Root);
        }, UnderClassic());
        ViewHarness.With(host, ViewKeys.StageArtistPane, ArtistPane(new Stage.StageCtx(), valid: true), harness =>
        {
            harness.RunFrames(2);
            Assert.False(ScrollUnder(harness.Shell, harness.Shell.Host.Scene.Root).IsNull, "a playing artist draws the scroller");
            empty = CountUnder(harness.Shell, harness.Shell.Host.Scene.Root);
        }, UnderClassic());
        Assert.True(empty > quiet, "the empty pane is the scroller, the nothing-playing pane one sentence");
    }

    [Fact]
    public void Facts_add_their_sections_and_the_demand_runs_once_per_artist_and_track()
    {
        var demands = new List<(int Artist, int Track)>();
        var rows = new[]
        {
            new StageArtistPopularRow(0, true, true, null, "First", false, "1.2M plays", "3:20", static () => { }),
            new StageArtistPopularRow(1, false, false, null, "Second", true, "", "4:01", static () => { }),
        };
        int without = 0, with = 0;
        ViewHarness.With(host, ViewKeys.StageArtistPane, ArtistPane(new Stage.StageCtx(), valid: true, (a, t) => demands.Add((a, t))), harness =>
        {
            harness.RunFrames(4);
            without = CountUnder(harness.Shell, harness.Shell.Host.Scene.Root);
        }, UnderClassic());
        ViewHarness.With(host, ViewKeys.StageArtistPane,
            ArtistPane(new Stage.StageCtx(), valid: true, (a, t) => demands.Add((a, t)), new StageArtistPopularFacts(1u, 1u, false, rows)), harness =>
        {
            harness.RunFrames(4);
            with = CountUnder(harness.Shell, harness.Shell.Host.Scene.Root);
        }, UnderClassic());
        Assert.True(with > without, "the popular rows add nodes");
        Assert.Equal(2, demands.Count);   // once per mount: the effect is keyed on (artist, track, epoch)
        Assert.All(demands, d => Assert.Equal((4, 9), (d.Artist, d.Track)));
    }
}
