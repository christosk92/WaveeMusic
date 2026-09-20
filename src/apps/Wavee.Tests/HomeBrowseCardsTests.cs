// ── Wavee.Tests/HomeBrowseCardsTests.cs — the Charts deck, its seed, the kind fallback and card navigation ──────────
//
// Wave 5, owner P. 0.2.9's `HomeBrowseCards` mapped a browseSection RECORD to a HomeSection and loaded the deck with
// one awaited request per chart uri. In 0.3 a chart section is a ROW (`Entities.BrowseSection(uri)`), a page lands on it
// through the commit, and `HomeBrowseCards.ChartDeck(hasLiveCatalog, out state)` reads the five rows: one view per
// `ChartSections.All` uri that answered with cards, Featured first, never fanned into one tile per playlist; Featured's
// concluded non-answer is the fail-loud state (0.2.9's throw), unless there is no live catalog.
//
// "Answered" is staged through HomeFixtures (the same commit a browseSection page takes). "Asked and not answered" is the
// planner's Asked bit with nothing known — written here the way `Fetch` records an ask, because the fake scope registers
// no provider that could answer.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeBrowseCardsTests
{
    public HomeBrowseCardsTests() => TestScope.Fresh();

    static Section Chart(string uri, string title, int total, params HomeFixtures.Spec[] cards)
        => HomeFixtures.Band(uri, title, SectionKind.BrowseShelf, total, SectionPaging.NoCursor, SectionFlags.Chart, cards);

    static HomeFixtures.Spec P(string id, string title, uint accent = 0)
        => HomeFixtures.Playlist("spotify:playlist:charts-" + id, title, accent: accent);

    /// <summary>The planner asked for the row and nothing answered (a null browseSection).</summary>
    static void AskedWithoutAnswer(string uri)
    {
        var row = Entities.BrowseSection(uri.AsSpan());
        Entities.Current.Sections.Asked[row.Slot] |= (uint)SectionFields.Identity;
    }

    // ── KindOf ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:artist:1", HomeCardKind.Artist)]
    [InlineData("spotify:album:1", HomeCardKind.Album)]
    [InlineData("spotify:show:1", HomeCardKind.Podcast)]
    [InlineData("spotify:episode:1", HomeCardKind.Episode)]
    [InlineData("spotify:track:1", HomeCardKind.Track)]
    [InlineData("spotify:playlist:1", HomeCardKind.Playlist)]
    [InlineData("not-a-real-uri-at-all", HomeCardKind.Playlist)]
    public void KindOf_MapsEntityKind_AndFallsBackToPlaylistForAnythingElse(string uri, HomeCardKind expected)
        => Assert.Equal(expected, HomeBrowseCards.KindOf(EntityUri.KindOf(uri.AsSpan())));

    // ── the section view the deck is built from ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sections_total_is_at_least_its_card_count()
    {
        var view = HomeSectionView.Of(Chart("spotify:section:charts-floor", "T", total: 0, P("a", "A"), P("b", "B")));
        Assert.Equal(2, view.TotalCount);
        Assert.Equal(2, view.RawItemCount);
        Assert.True(view.IsChart);
    }

    [Fact]
    public void A_deck_card_reads_its_title_kind_and_accent_from_the_rows()
    {
        Chart(ChartSections.Featured, "Featured Charts", 2, P("a", "Chill Vibes", accent: 0xFF00FF00), HomeFixtures.Album("spotify:album:charts-b", "B"));

        var deck = HomeBrowseCards.ChartDeck(hasLiveCatalog: false, out _);

        var cards = Assert.Single(deck).Cards;
        Assert.Equal("Chill Vibes", cards[0].Title);
        Assert.Equal(0xFF00FF00u, cards[0].Accent);
        Assert.Equal(HomeCardKind.Album, cards[1].Kind);
    }

    // ── ChartDeck: one view per ChartSections.All uri, never a per-playlist fan ──────────────────────────────────────

    [Fact]
    public void ChartDeck_KeepsEveryCardOnThatSection_InAllOrder()
    {
        Chart(ChartSections.Daily, "Daily Song Charts", 10, P("d1", "Top Songs — Netherlands"));
        Chart(ChartSections.Featured, "Featured Charts", 4,
            P("a", "Top Songs — Global"), P("b", "Top Songs — USA"), P("c", "Viral 50"), P("d", "Top Songs — Netherlands"));
        Chart(ChartSections.Weekly, "Weekly Song Charts", 74, P("w1", "Top Songs — Global"));

        var deck = HomeBrowseCards.ChartDeck(hasLiveCatalog: true, out var state);

        Assert.Equal(3, deck.Count);
        Assert.Equal(ChartSections.Featured, deck[0].Uri);
        Assert.Equal("Featured Charts", deck[0].Title);
        Assert.Equal(4, deck[0].Cards.Count);
        Assert.Equal(4, deck[0].TotalCount);
        Assert.Equal(ChartSections.Weekly, deck[1].Uri);
        Assert.Equal(74, deck[1].TotalCount);
        Assert.Equal(ChartSections.Daily, deck[2].Uri);
        // Now available and Podcast were never asked: the deck is still arriving.
        Assert.Equal(HomeLoad.Pending, state);
    }

    [Fact]
    public void ChartDeck_OmitsNullAndEmptyShelves_AfterFeatured()
    {
        Chart(ChartSections.Featured, "Featured Charts", 1, P("a", "Top Songs — Global"));
        AskedWithoutAnswer(ChartSections.Weekly);
        Chart(ChartSections.Daily, "Daily", 0);
        Chart(ChartSections.NowAvailable, "Now available", 1, P("now", "Now available"));
        AskedWithoutAnswer(ChartSections.Podcast);

        var deck = HomeBrowseCards.ChartDeck(hasLiveCatalog: true, out var state);

        Assert.Equal(2, deck.Count);
        Assert.Equal(ChartSections.Featured, deck[0].Uri);
        Assert.Equal(ChartSections.NowAvailable, deck[1].Uri);
        Assert.Equal(HomeLoad.Ready, state);   // every row has concluded
    }

    [Fact]
    public void ChartDeck_FeaturedAskedAndUnanswered_WithALiveCatalog_Fails()
    {
        // 0.2.9 threw "browseSection returned no Charts section for <Featured>". A row cannot throw; it FAILS loudly.
        Chart(ChartSections.Weekly, "Weekly", 1, P("w", "W"));
        AskedWithoutAnswer(ChartSections.Featured);

        var deck = HomeBrowseCards.ChartDeck(hasLiveCatalog: true, out var state);

        Assert.Empty(deck);
        Assert.Equal(HomeLoad.Failed, state);
    }

    [Fact]
    public void ChartDeck_FeaturedUnanswered_NoLiveCatalog_IsAnEmptyReadyDeck()
    {
        AskedWithoutAnswer(ChartSections.Featured);

        var deck = HomeBrowseCards.ChartDeck(hasLiveCatalog: false, out var state);

        Assert.Empty(deck);
        Assert.Equal(HomeLoad.Ready, state);
    }

    [Fact]
    public void ChartDeck_FeaturedNeverAsked_IsPendingOnlyWithALiveCatalog()
    {
        Assert.Empty(HomeBrowseCards.ChartDeck(hasLiveCatalog: true, out var live));
        Assert.Equal(HomeLoad.Pending, live);

        Assert.Empty(HomeBrowseCards.ChartDeck(hasLiveCatalog: false, out var offline));
        Assert.Equal(HomeLoad.Ready, offline);
    }

    [Fact]
    public void ChartDeck_IsTheSameInstanceUntilARowMoves()
    {
        Chart(ChartSections.Featured, "Featured Charts", 1, P("a", "A"));
        var first = HomeBrowseCards.ChartDeck(hasLiveCatalog: false, out _);
        Assert.Same(first, HomeBrowseCards.ChartDeck(hasLiveCatalog: false, out _));

        Chart(ChartSections.Weekly, "Weekly Song Charts", 1, P("w", "W"));
        var second = HomeBrowseCards.ChartDeck(hasLiveCatalog: false, out _);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void ChartSections_are_stamped_browse_shelves_by_the_row_factory()
    {
        var weekly = Entities.BrowseSection(ChartSections.Weekly.AsSpan());
        Assert.Equal(SectionKind.BrowseShelf, weekly.Kind);
    }

    // ── the shimmer seed ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChartDeckSeed_IsThreeBlankFoldTiles_WithDistinctBlankCards()
    {
        var seed = HomeBrowseCards.ChartDeckSeed;
        Assert.Equal(3, seed.Count);
        var keys = new HashSet<long>();
        foreach (var tile in seed)
        {
            Assert.Equal(" ", tile.Title);
            Assert.Equal(Table.None, tile.Slot);
            Assert.Equal(3, tile.Cards.Count);
            foreach (var card in tile.Cards)
            {
                Assert.True(card.IsBlank);
                Assert.True(keys.Add(card.DedupeKey));
            }
        }
    }

    // ── card navigation (the CORE half) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneCardOpensCard_IsBrowseOnly_AndExactlyOneCard()
    {
        Assert.True(HomeCardNav.OneCardOpensCard(1, browse: true));
        Assert.False(HomeCardNav.OneCardOpensCard(1, browse: false));
        Assert.False(HomeCardNav.OneCardOpensCard(2, browse: true));
        Assert.False(HomeCardNav.OneCardOpensCard(0, browse: true));
    }

    [Fact]
    public void RouteFor_OpensContainers_AndLeavesPlayablesToPlay()
    {
        var cards = HomeFixtures.Cards("Mixed", SectionKind.HomeGeneric,
            HomeFixtures.Playlist("spotify:playlist:nav-p", "A Playlist"),
            HomeFixtures.Album("spotify:album:nav-a", "An Album"),
            new HomeFixtures.Spec("spotify:artist:nav-r", "An Artist"),
            new HomeFixtures.Spec("spotify:show:nav-s", "A Podcast"),
            new HomeFixtures.Spec("spotify:show:nav-b", "A Book", Audiobook: true),
            new HomeFixtures.Spec("spotify:track:nav-t", "A Track"),
            new HomeFixtures.Spec("spotify:episode:nav-e", "An Episode"));

        var playlist = HomeCardNav.RouteFor(cards[0]);
        Assert.Equal(Shell.RouteKind.Playlist, playlist.Kind);
        Assert.Equal("spotify:playlist:nav-p", playlist.Subject.Text);
        Assert.Equal("A Playlist", Entities.Strings.Resolve(playlist.Arg));

        Assert.Equal(Shell.RouteKind.Album, HomeCardNav.RouteFor(cards[1]).Kind);
        Assert.Equal(Shell.RouteKind.Artist, HomeCardNav.RouteFor(cards[2]).Kind);
        Assert.Equal(HomeCardKind.Podcast, cards[3].Kind);
        Assert.Equal(Shell.RouteKind.Show, HomeCardNav.RouteFor(cards[3]).Kind);
        Assert.Equal(HomeCardKind.Audiobook, cards[4].Kind);
        Assert.Equal(Shell.RouteKind.Show, HomeCardNav.RouteFor(cards[4]).Kind);
        Assert.True(HomeCardNav.RouteFor(cards[5]).IsNone);        // a track PLAYS
        Assert.True(HomeCardNav.RouteFor(cards[6]).IsNone);        // an episode PLAYS
        Assert.True(HomeCardNav.RouteFor(HomeCard.Blank()).IsNone);
    }

    [Fact]
    public void RouteFor_TheLikedCollection_OpensLiked()
    {
        // Liked lives in the PLAYLIST table under a collection id — the one kind `Entities.TableFor` answers null for.
        int liked = Entities.Current.Playlists.Slot(EntityUri.LikedCollection.AsSpan());
        var card = new HomeCard(new EntityRef(EntityKind.Collection, liked));

        Assert.Equal(HomeCardKind.Liked, card.Kind);
        Assert.Equal(EntityUri.LikedCollection, card.Uri);
        Assert.Equal(Shell.RouteKind.Liked, HomeCardNav.RouteFor(card).Kind);
    }

    [Fact]
    public void SectionRoute_FollowsTheCallersFamily_AndCarriesTheTitle()
    {
        const string uri = "spotify:section:nav-drill";
        var view = HomeSectionView.Of(HomeFixtures.Band(uri, "Made For You", SectionKind.HomeGeneric,
            HomeFixtures.Playlist("spotify:playlist:nav-drill-1", "One")));

        var home = HomeCardNav.SectionRoute(view, browse: false);
        Assert.Equal(Shell.RouteKind.HomeSection, home.Kind);
        Assert.Equal(uri, home.Subject.Text);
        Assert.Equal("Made For You", Entities.Strings.Resolve(home.Arg));

        Assert.Equal(Shell.RouteKind.BrowseSection, HomeCardNav.SectionRoute(view, browse: true).Kind);
        Assert.True(HomeCardNav.SectionRoute(HomeBrowseCards.ChartDeckSeed[0], browse: true).IsNone);
    }
}
