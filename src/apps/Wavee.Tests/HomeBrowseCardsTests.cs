using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee;
using Wavee.Core;
using Wavee.Features.Browse;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// HomeBrowseCards is the ONE mapping from a browseSection response (<see cref="BrowseSection"/> /
/// <see cref="BrowseCard"/>) to the section page's presentation model (<see cref="HomeSection"/> /
/// <see cref="HomeCard"/>) — split out of <c>HomeSectionPage</c> (which is engine-bound and deliberately NOT
/// source-included) precisely so it can be pinned here against the REAL production code. These tests guard three
/// regressions: a dropped/renamed field on the <see cref="HomeBrowseCards.Card"/> projection, the
/// <c>KindOf</c> fallback getting "fixed" into a throw — that fallback to <see cref="HomeCardKind.Playlist"/> for
/// anything the uri parser cannot identify (a playlist uri included) is DELIBERATE, per HomeBrowseCards.cs's own
/// comment — and <see cref="HomeBrowseCards.LoadChartDeckAsync"/> keeping one HomeSection per
/// <see cref="ChartSections.All"/> uri (never fanning Featured into one tile per playlist).
/// </summary>
public class HomeBrowseCardsTests
{
    static BrowseCard MakeCard(string uri, string title = "Title", string? subtitle = "Subtitle",
        Image? image = null, uint? accent = null)
        => new(uri, title, subtitle, image, accent);

    // ── Card: field mapping ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Card_MapsUriTitleSubtitleImage_AndCarriesAccentIntoMeta()
    {
        var image = new Image("https://example/cover.jpg", 300, 300);
        var browseCard = MakeCard("spotify:playlist:abc123", "Chill Vibes", "50 songs", image, accent: 0xFF00FF00);

        var card = HomeBrowseCards.Card(browseCard);

        Assert.Equal("spotify:playlist:abc123", card.Uri);
        Assert.Equal("Chill Vibes", card.Title);
        Assert.Equal("50 songs", card.Subtitle);
        Assert.Same(image, card.Image);
        Assert.NotNull(card.Meta);
        Assert.Equal(0xFF00FF00u, card.Meta!.Accent);
    }

    [Fact]
    public void Card_NullAccent_MapsToZero()
    {
        var card = HomeBrowseCards.Card(MakeCard("spotify:playlist:abc", accent: null));
        Assert.NotNull(card.Meta);
        Assert.Equal(0u, card.Meta!.Accent);
    }

    // ── Card -> KindOf ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:artist:1", HomeCardKind.Artist)]
    [InlineData("spotify:album:1", HomeCardKind.Album)]
    [InlineData("spotify:show:1", HomeCardKind.Podcast)]
    [InlineData("spotify:episode:1", HomeCardKind.Episode)]
    [InlineData("spotify:track:1", HomeCardKind.Track)]
    [InlineData("spotify:playlist:1", HomeCardKind.Playlist)]
    [InlineData("not-a-real-uri-at-all", HomeCardKind.Playlist)]
    public void Card_KindOf_MapsEntityKind_AndFallsBackToPlaylistForAnythingElse(string uri, HomeCardKind expected)
    {
        // The Playlist fallback (a playlist uri AND a junk string both land here) is deliberate — nobody should
        // "fix" this into a throw; see the class doc-comment.
        var card = HomeBrowseCards.Card(MakeCard(uri));
        Assert.Equal(expected, card.Kind);
    }

    // ── Section(s, routeTitle) ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Section_UsesTheSectionsOwnTitle_WhenItHasOne()
    {
        var browseSection = new BrowseSection("spotify:section:x", "Server Title", BrowseSectionKind.Shelf,
            [MakeCard("spotify:playlist:a")], [], Total: 42);

        var section = HomeBrowseCards.Section(browseSection, routeTitle: "Route Title");

        Assert.Equal("Server Title", section.Title);
        Assert.Equal(42, section.TotalCount);
    }

    [Fact]
    public void Section_FallsBackToRouteTitle_WhenTheSectionCarriesNone()
    {
        var browseSection = new BrowseSection("spotify:section:x", null, BrowseSectionKind.Shelf,
            [MakeCard("spotify:playlist:a")], [], Total: 7);

        var section = HomeBrowseCards.Section(browseSection, routeTitle: "Route Title");

        Assert.Equal("Route Title", section.Title);
        Assert.Equal(7, section.TotalCount);
    }

    [Fact]
    public void Section_MapsEveryCardThroughTheSharedCardProjection()
    {
        var browseSection = new BrowseSection("spotify:section:x", "T", BrowseSectionKind.Shelf,
            [MakeCard("spotify:playlist:a", "A"), MakeCard("spotify:album:b", "B")], [], Total: 2);

        var section = HomeBrowseCards.Section(browseSection, null);

        Assert.Equal(2, section.Cards.Count);
        Assert.Equal("A", section.Cards[0].Title);
        Assert.Equal("B", section.Cards[1].Title);
        Assert.Equal(HomeCardKind.Album, section.Cards[1].Kind);
    }

    [Fact]
    public void Section_TotalCountIsAtLeastTheCardCount()
    {
        var browseSection = new BrowseSection("spotify:section:x", "T", BrowseSectionKind.Shelf,
            [MakeCard("spotify:playlist:a"), MakeCard("spotify:playlist:b")], [], Total: 0);

        var section = HomeBrowseCards.Section(browseSection, null);

        Assert.Equal(2, section.TotalCount);
        Assert.Equal(2, section.RawItemCount);
    }

    // ── LoadChartDeckAsync: one HomeSection per ChartSections.All uri, never a per-playlist fan ─────────────────

    [Fact]
    public async Task LoadChartDeckAsync_KeepsEveryCardOnThatSection_InAllOrder()
    {
        var browse = new FakeBrowse();
        browse.Sections[ChartSections.Featured] = Shelf(ChartSections.Featured, "Featured Charts", 4,
            MakeCard("spotify:playlist:a", "Top Songs — Global"),
            MakeCard("spotify:playlist:b", "Top Songs — USA"),
            MakeCard("spotify:playlist:c", "Viral 50"),
            MakeCard("spotify:playlist:d", "Top Songs — Netherlands"));
        browse.Sections[ChartSections.Weekly] = Shelf(ChartSections.Weekly, "Weekly Song Charts", 74,
            MakeCard("spotify:playlist:w1", "Top Songs — Global"));
        browse.Sections[ChartSections.Daily] = Shelf(ChartSections.Daily, "Daily Song Charts", 10,
            MakeCard("spotify:playlist:d1", "Top Songs — Netherlands"));

        var deck = await HomeBrowseCards.LoadChartDeckAsync(browse);

        Assert.Equal(3, deck.Count);
        Assert.Equal(ChartSections.Featured, deck[0].Uri);
        Assert.Equal("Featured Charts", deck[0].Title);
        Assert.Equal(4, deck[0].Cards.Count);
        Assert.Equal(4, deck[0].TotalCount);
        Assert.Equal(ChartSections.Weekly, deck[1].Uri);
        Assert.Equal(74, deck[1].TotalCount);
        Assert.Equal(ChartSections.Daily, deck[2].Uri);
    }

    [Fact]
    public async Task LoadChartDeckAsync_OmitsNullAndEmptyShelves_AfterFeatured()
    {
        var browse = new FakeBrowse();
        browse.Sections[ChartSections.Featured] = Shelf(ChartSections.Featured, "Featured Charts", 1,
            MakeCard("spotify:playlist:a", "Top Songs — Global"));
        browse.Sections[ChartSections.Weekly] = null;
        browse.Sections[ChartSections.Daily] = Shelf(ChartSections.Daily, "Daily", 0);
        browse.Sections[ChartSections.NowAvailable] = Shelf(ChartSections.NowAvailable, "Now available", 1,
            MakeCard("spotify:playlist:now", "Now available"));
        browse.Sections[ChartSections.Podcast] = null;

        var deck = await HomeBrowseCards.LoadChartDeckAsync(browse);

        Assert.Equal(2, deck.Count);
        Assert.Equal(ChartSections.Featured, deck[0].Uri);
        Assert.Equal(ChartSections.NowAvailable, deck[1].Uri);
    }

    [Fact]
    public async Task LoadChartDeckAsync_StampsTheRequestedTaxonomyUri_WhenTheResponseUriDiffers()
    {
        var browse = new FakeBrowse();
        browse.Sections[ChartSections.Featured] = Shelf("spotify:section:other", "Featured Charts", 1,
            MakeCard("spotify:playlist:a", "Top Songs — Global"));

        var deck = await HomeBrowseCards.LoadChartDeckAsync(browse);

        Assert.Equal(ChartSections.Featured, Assert.Single(deck).Uri);
    }

    [Fact]
    public async Task LoadChartDeckAsync_FeaturedNull_Throws()
    {
        var browse = new FakeBrowse();
        browse.Sections[ChartSections.Weekly] = Shelf(ChartSections.Weekly, "Weekly", 1,
            MakeCard("spotify:playlist:w", "W"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HomeBrowseCards.LoadChartDeckAsync(browse));
        Assert.Contains(ChartSections.Featured, ex.Message);
    }

    static BrowseSection Shelf(string uri, string title, int total, params BrowseCard[] cards)
        => new(uri, title, BrowseSectionKind.Shelf, cards, [], total);

    sealed class FakeBrowse : IBrowseService
    {
        public Dictionary<string, BrowseSection?> Sections { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrowseCategory>>(Array.Empty<BrowseCategory>());

        public Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default)
            => Task.FromResult<BrowsePageModel?>(null);

        public Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
            => Task.FromResult(Sections.TryGetValue(sectionUri, out var s) ? s : null);
    }
}
