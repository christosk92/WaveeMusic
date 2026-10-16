// ── Wavee.ShellTests/ArtistPageMountedTests.cs — the artist page's view, mounted (themes plan 6.1, T10b1) ────────────────────────
//
// The artist page's scroll column, skeleton region, hero, context band, magazine, banners and pick card are the structure's since T10b1; `Artist.PageHost`
// keeps the demands, the readiness, the section plan, the hero metrics, the accent, the scroll spy and the verbs (`Wavee.Tests` pins the pure ones:
// `ArtistPageRulesTests`, `ArtistHeroLayoutTests`, `ArtistPageContractTests`). What only a mounted tree can say:
//
//   registered   Classic is the registered template of `Pages.Artist`
//   pending      the page is the keyed column over its keyed scroller and a body child, the body is NOT composed (no magazine) and the host's size observer
//                hears the page's width
//   failed       the error arm keeps the same column and composes no body
//   hero copy    tuned Classic D18: on the photo tiers the hero's copy column is min(CopyMax, max(360, 0.56 w - gutter)) wide at the gutter, so it ends
//                near 56 % of the hero (it ran to ~90 % at Medium and ~94 % at a 1440 window's Wide) — the loading hero's too (X2)

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ArtistPageMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    /// <summary>The metrics the core host latches for a <paramref name="width"/>-wide page in a <paramref name="window"/>-wide harness (its content region
    /// is the window): the structure's record through the policy (TC6), at the page gutter.</summary>
    static ArtistHeroMetrics MetricsAt(float width, float window)
    {
        var facts = WindowTiers.Resolve(ClassicTiers.Policy, window, 760f, window);
        return ClassicArtistTiers.Policy.Hero(ClassicArtistTiers.HeroTierFor(width), in facts, width);
    }

    static ClassicArtistHeroForm FormAt(float width, float window)
        => ClassicArtistTiers.HeroForm(ClassicArtistTiers.HeroTierFor(width), WindowTiers.Resolve(ClassicTiers.Policy, window, 760f, window), width);

    /// <summary>The harness's default window width (`ViewHarnessOptions.WidthDip`).</summary>
    const float HarnessWidth = 1180f;

    static ArtistPageProps Props(bool pending, bool failed, Action<RectF>? measure = null, float width = 880f,
                                 bool bodyReady = false, float window = HarnessWidth)
    {
        var accent = ColorF.FromRgba(30, 144, 255, 255);
        var compact = new Signal<bool>(false);
        return new ArtistPageProps
        {
            RouteKey = "fixture",
            ScopeKey = "7",
            Artist = default,
            ScrollKey = "test:artist:fixture",
            Pending = () => pending,
            Failed = () => failed,
            Retry = static () => { },
            BodyReady = bodyReady,
            Width = width,
            Metrics = MetricsAt(width, window),
            Measure = measure ?? (static _ => { }),
            Accent = accent,
            AccentFn = () => accent,
            AccentPeek = () => accent,
            AccentSignal = new Signal<ColorF>(accent),
            PageAccent = null,
            Tint = new BoxEl(),
            Compact = false,
            CompactEdge = () => compact,
            Pivot = [],
            Active = new Signal<int>(-1),
            Scroll = new ScrollHandle(),
            CaptureViewport = static _ => { },
            ScrollY = new Signal<float>(0f),
            Sections = [],
            Fans = [],
            RelatedCount = 0,
            Play = static () => { },
            Shuffle = static () => { },
            Radio = static () => { },
            ScrollToTop = static () => { },
            OpenGallery = static (_, _) => { },
        };
    }

    [Fact]
    public void Classic_is_the_registered_template_of_the_artist_page()
        => host.Run(() => Assert.Equal("classic.artist.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.ArtistPage.Id)));

    [Fact]
    public void A_pending_page_is_the_keyed_column_over_its_scroller_and_composes_no_body()
    {
        float measured = 0f;
        ViewHarness.With(host, ViewKeys.ArtistPage, Props(pending: true, failed: false, r => measured = r.W),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("artist-page:fixture"), "the page column is keyed by the route");
                Assert.True(harness.Contains("artist-scroll:fixture"), "the scroller is keyed by the route");
                Assert.True(harness.Contains("artist-body:7"), "the body child is keyed by the scope epoch");
                Assert.False(harness.Contains("artist-under-band"), "a pending page composes no magazine");
                Assert.True(measured > 0f, "the page column reports its width to the host");
            }, UnderClassic());
    }

    [Theory]
    [InlineData(752f, 560f)]       // a 752 window is Small: C-Artist-Small's Medium hero sets a fixed 560 measure at the bottom-left (TC6)
    [InlineData(1120f, 591.2f)]    // Wide at Standard (D18): 0.56 x 1120 - 36 (was 1120 - 2 x 36 = 1048)
    public void The_hero_copy_column_is_the_tiers_measure(float width, float copy)
        => ViewHarness.With(host, ViewKeys.ArtistPage, Props(pending: false, failed: false, width: width, bodyReady: true, window: width),
            harness =>
            {
                harness.RunFrames(30);
                var m = MetricsAt(width, width);
                Assert.Equal(copy, ClassicArtistRules.HeroCopyWidth(width, in m, FormAt(width, width)), 2);
                var node = harness.Find("artist-hero-copy");
                Assert.False(node.IsNull, "the hero's copy column is mounted");
                var r = harness.LocalBounds(node);
                Assert.Equal(copy, r.W, 1);
                Assert.Equal(m.Gutter, harness.AbsoluteBounds(node).X - harness.AbsoluteBounds(harness.Find("artist-page:fixture")).X, 1);
            }, UnderClassic() with { WidthDip = width });   // the content region IS the page: its gutter tier is the hero's (TC1)

    /// <summary>Tuned Classic X2: the loading hero is the loaded hero's composition over a placeholder, so its copy column stands at the same D18 width
    /// and gutter.</summary>
    [Fact]
    public void The_loading_heros_copy_column_is_the_loaded_ones()
        => ViewHarness.With(host, ViewKeys.ArtistPage, Props(pending: true, failed: false, width: 1120f),
            harness =>
            {
                harness.RunFrames(30);
                var m = MetricsAt(1120f, HarnessWidth);
                var node = harness.Find("artist-hero-copy");
                Assert.False(node.IsNull, "the shimmer's copy column is mounted");
                Assert.Equal(ClassicArtistRules.HeroCopyWidth(1120f, in m, FormAt(1120f, HarnessWidth)), harness.LocalBounds(node).W, 1);
                Assert.Equal(m.Gutter, harness.AbsoluteBounds(node).X - harness.AbsoluteBounds(harness.Find("artist-page:fixture")).X, 1);
            }, UnderClassic());

    [Fact]
    public void A_failed_page_keeps_the_column_and_composes_no_body()
        => ViewHarness.With(host, ViewKeys.ArtistPage, Props(pending: false, failed: true),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("artist-page:fixture"));
                Assert.True(harness.Contains("artist-scroll:fixture"));
                Assert.False(harness.Contains("artist-under-band"), "the error arm has no magazine");
            }, UnderClassic());
}
