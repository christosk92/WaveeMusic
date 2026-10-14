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

    static ArtistPageProps Props(bool pending, bool failed, Action<RectF>? measure = null)
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
            BodyReady = false,
            Width = ArtistHeroLayout.WideWidth,
            Metrics = ArtistHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide),
            Measure = measure ?? (static _ => { }),
            Accent = accent,
            AccentFn = () => accent,
            AccentPeek = () => accent,
            AccentSignal = new Signal<ColorF>(accent),
            PageAccent = null,
            Washes = false,
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
            RowsClassic = true,
            ShowTrackArt = true,
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
