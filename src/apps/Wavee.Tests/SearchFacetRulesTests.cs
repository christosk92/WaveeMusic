// ── Wavee.Tests/SearchFacetRulesTests.cs — ch 13 §8's search rules, extracted from 0.2.9 SearchPage and pinned ─────
//
// 0.2.9 kept these decisions private inside `SearchPage.cs` / `SearchAllList.cs` / `SearchHitsGrid.cs`
// (`FacetsFrom`, `FacetCount`, `FallbackRows`, `ColsFor`, `PlaylistShelfItems`, `PlaylistOwnerOf`, `OpenGenre`) and had
// no tests for them. 0.3 ports them verbatim onto `Search` (Entities/Search.cs §7) over table-shaped inputs (facet-indexed
// totals and local counts, `EntityRef` hits, playlist slots) — every expectation below is the 0.2.9 algorithm walked by
// hand, not a number read back off the port.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SearchFacetRulesTests
{
    static int[] Totals() { var t = new int[SearchTable.FacetCount]; Array.Fill(t, Search.NoTotal); return t; }

    static SearchFacet[] Facets(ReadOnlySpan<SearchFacet> chips, int[] totals, int[] local, int capacity = SearchTable.FacetCount)
    {
        var into = new SearchFacet[capacity];
        int n = Search.FacetsFrom(chips, totals, local, into);
        return into.AsSpan(0, n).ToArray();
    }

    // ── FacetsFrom ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FacetsFrom_IsAllThenTheChipOrderThenTheFilteredFallback()
    {
        // chipOrder is the server's tab list and is taken as-is (a chip is real even when its nested list is empty);
        // the fallback superset adds only what the local list holds OR a total claims.
        var totals = Totals();
        totals[(int)SearchFacet.Artists] = 12;
        totals[(int)SearchFacet.Episodes] = 0;                          // a real zero claims nothing
        var local = new int[SearchTable.FacetCount];
        local[(int)SearchFacet.Albums] = 3;

        var facets = Facets([SearchFacet.Playlists, SearchFacet.Tracks], totals, local);

        Assert.Equal([SearchFacet.All, SearchFacet.Playlists, SearchFacet.Tracks, SearchFacet.Albums, SearchFacet.Artists], facets);
    }

    [Fact]
    public void FacetsFrom_DedupesTheChipOrder_AndNeverRepeatsAll()
    {
        var facets = Facets([SearchFacet.All, SearchFacet.Genres, SearchFacet.Genres, SearchFacet.Podcasts],
                            Totals(), new int[SearchTable.FacetCount]);
        Assert.Equal([SearchFacet.All, SearchFacet.Genres, SearchFacet.Podcasts], facets);
    }

    [Fact]
    public void FacetsFrom_WithNothingToFacetOn_IsTheLoneAllTab()
        => Assert.Equal([SearchFacet.All], Facets([], Totals(), new int[SearchTable.FacetCount]));

    [Fact]
    public void FacetsFrom_TheFallbackFollowsTheStaticOrder()
    {
        // Every fallback facet claims rows: the list is the fixed superset order, not the enum's.
        var local = new int[SearchTable.FacetCount];
        Array.Fill(local, 1);
        Assert.Equal([SearchFacet.All, .. Search.FallbackFacets], Facets([], Totals(), local));
    }

    [Fact]
    public void FacetsFrom_StopsAtTheCapacityItIsGiven()
    {
        var local = new int[SearchTable.FacetCount];
        Array.Fill(local, 1);
        Assert.Equal([SearchFacet.All, SearchFacet.Tracks], Facets([], Totals(), local, capacity: 2));
        Assert.Empty(Facets([SearchFacet.Tracks], Totals(), local, capacity: 0));
    }

    // ── FacetCount ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Search.NoTotal, 4, 4)]      // no server total: the local list's own count
    [InlineData(Search.NoTotal, 0, 0)]
    [InlineData(0, 4, 0)]                   // a real zero wins, and a tab omits a zero
    [InlineData(12, 0, 12)]
    public void FacetCount_IsTheResolvedTotalWhenPositive(int stored, int local, int expected)
        => Assert.Equal(expected, Search.FacetCount(stored, local));

    // ── FallbackRows (0.2.9 SearchAllList.FallbackRows) ─────────────────────────────────────────────────────────────

    static Search.FallbackRow[] Rows(int tracks, int artists, int albums, int playlists)
    {
        var into = new Search.FallbackRow[Search.FallbackMaxRows];
        int n = Search.FallbackRows(tracks, artists, albums, playlists, into);
        return into.AsSpan(0, n).ToArray();
    }

    static Search.FallbackRow R(SearchFacet f, int i, bool large = false) => new(f, i, large);

    [Fact]
    public void FallbackRows_TheTopArtistIsLarge_ArtistsSpliceAfterTheThirdAndSixthTrack_ThenAlbumsAndPlaylists()
    {
        Assert.Equal(
        [
            R(SearchFacet.Artists, 0, true),
            R(SearchFacet.Tracks, 0), R(SearchFacet.Tracks, 1), R(SearchFacet.Tracks, 2), R(SearchFacet.Artists, 1),
            R(SearchFacet.Tracks, 3), R(SearchFacet.Tracks, 4), R(SearchFacet.Tracks, 5), R(SearchFacet.Artists, 2),
            R(SearchFacet.Tracks, 6), R(SearchFacet.Tracks, 7),
            R(SearchFacet.Artists, 3), R(SearchFacet.Artists, 4),                        // topped up (cap 14)
            R(SearchFacet.Albums, 0), R(SearchFacet.Albums, 1), R(SearchFacet.Albums, 2), R(SearchFacet.Albums, 3),
            R(SearchFacet.Playlists, 0), R(SearchFacet.Playlists, 1), R(SearchFacet.Playlists, 2), R(SearchFacet.Playlists, 3),
        ], Rows(tracks: 10, artists: 5, albums: 6, playlists: 6));
    }

    [Fact]
    public void FallbackRows_ArtistTopUpStopsAtFourteenRows()
    {
        var rows = Rows(tracks: 8, artists: 20, albums: 0, playlists: 0);
        Assert.Equal(14, rows.Length);
        Assert.Equal(R(SearchFacet.Artists, 5), rows[^1]);
    }

    [Fact]
    public void FallbackRows_WithNoArtist_TheTopAlbumIsLargeAndIsNotRepeated()
        => Assert.Equal(
            [R(SearchFacet.Albums, 0, true), R(SearchFacet.Albums, 1), R(SearchFacet.Albums, 2),
             R(SearchFacet.Playlists, 0), R(SearchFacet.Playlists, 1)],
            Rows(tracks: 0, artists: 0, albums: 3, playlists: 2));

    [Fact]
    public void FallbackRows_WithOnlyPlaylists_TheTopPlaylistIsLargeAndIsNotRepeated()
        => Assert.Equal(
            [R(SearchFacet.Playlists, 0, true), R(SearchFacet.Tracks, 0), R(SearchFacet.Tracks, 1),
             R(SearchFacet.Playlists, 1), R(SearchFacet.Playlists, 2)],
            Rows(tracks: 2, artists: 0, albums: 0, playlists: 3));

    [Fact]
    public void FallbackRows_NothingIsNoRows()
        => Assert.Empty(Rows(0, 0, 0, 0));

    // ── ColsFor / RowsFor / MaxColumns (0.2.9 SearchHitsGrid) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(900f, 1, false, 3)]   // never measured: the nominal count outright, floor((w + 12) / 292)
    [InlineData(291f, 3, false, 1)]
    [InlineData(584f, 1, true, 2)]    // growing is immediate
    [InlineData(900f, 1, true, 3)]
    [InlineData(860f, 3, true, 3)]    // shrinking holds until 24 DIP past three columns' own need (864)
    [InlineData(840f, 3, true, 3)]
    [InlineData(839f, 3, true, 2)]
    [InlineData(5000f, 3, true, 3)]   // capped at three
    public void ColsFor_GrowsAtOnceAndShrinksWithHysteresis(float width, int prev, bool initialized, int expected)
        => Assert.Equal(expected, Search.ColsFor(width, prev, initialized));

    [Theory]
    [InlineData(1, 3)]                // one column keeps three rows: never a one-cell pager
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void RowsFor_IsSquareExceptOneColumn(int cols, int expected) => Assert.Equal(expected, Search.RowsFor(cols));

    [Theory]
    [InlineData(3, 3, 20, 3)]
    [InlineData(3, 3, 4, 2)]          // a short list narrows the page: ceil(4 / 3) = 2
    [InlineData(3, 3, 0, 1)]          // at least one
    [InlineData(2, 2, 1, 1)]
    public void MaxColumns_NarrowsAShortList(int cols, int rows, int count, int expected)
        => Assert.Equal(expected, Search.MaxColumns(cols, rows, count));

    // ── the playlist rail (0.2.9 PlaylistShelfItems / PlaylistOwnerOf) ──────────────────────────────────────────────

    [Fact]
    public void PlaylistShelfItems_TopHitPlaylistsFirst_ThenTheFacet_DedupedAndNeverTheHero()
    {
        EntityRef[] hits =
        [
            new(EntityKind.Track, 11), new(EntityKind.Playlist, 7), new(EntityKind.Playlist, 9), new(EntityKind.Playlist, 7),
        ];
        int[] playlists = [9, 11, 12, 0];
        var into = new int[8];

        int n = Search.PlaylistShelfItems(hits, playlists, skip: new EntityRef(EntityKind.Playlist, 9), into);

        // 9 is the hero's own playlist; track slot 11 is NOT playlist slot 11; slot 0 is "none".
        Assert.Equal([7, 11, 12], into.AsSpan(0, n).ToArray());
    }

    [Fact]
    public void PlaylistShelfItems_StopsAtTheBufferItIsGiven()
    {
        int[] playlists = [1, 2, 3, 4];
        var into = new int[2];
        Assert.Equal(2, Search.PlaylistShelfItems([], playlists, default, into));
        Assert.Equal([1, 2], into);
    }

    [Theory]
    [InlineData("Playlist • Spotify", "Spotify")]
    [InlineData("Playlist • Big • Owner", "Big • Owner")]      // only the FIRST separator splits
    [InlineData("Spotify", "Spotify")]
    [InlineData("", "")]
    public void PlaylistOwnerOf_IsTheTextAfterTheFirstBullet(string subtitle, string expected)
        => Assert.Equal(expected, Search.PlaylistOwnerOf(subtitle));

    // ── related links, gates, shimmer, slide, keys, the facet grid ──────────────────────────────────────────────────

    [Theory]
    [InlineData("daft punk remix", "daft punk", true)]
    [InlineData(" Daft Punk ", "daft punk", false)]     // repeats the typed query (trimmed, OrdinalIgnoreCase)
    [InlineData("", "daft punk", false)]
    public void KeepsRelated_DropsTheTypedQuery(string related, string typed, bool expected)
        => Assert.Equal(expected, Search.KeepsRelated(related.AsSpan(), typed.AsSpan()));

    [Theory]
    [InlineData(EntityKind.Track, true, false, true, false)]
    [InlineData(EntityKind.Album, true, true, true, false)]
    [InlineData(EntityKind.Artist, true, true, true, true)]
    [InlineData(EntityKind.Playlist, true, true, true, false)]
    [InlineData(EntityKind.Show, true, true, true, false)]
    [InlineData(EntityKind.Episode, true, true, false, false)]
    [InlineData(EntityKind.User, false, false, false, true)]
    [InlineData(EntityKind.Unknown, false, false, false, false)]
    public void TheHitGatesFollowTheKind(EntityKind kind, bool play, bool open, bool menu, bool round)
    {
        Assert.Equal(play, Search.CanPlay(kind));
        Assert.Equal(open, Search.CanOpen(kind));
        Assert.Equal(menu, Search.HasMenu(kind));
        Assert.Equal(round, Search.RoundArt(kind));
    }

    [Theory]
    [InlineData(SearchFacet.All, Search.ShimmerShape.Hero)]
    [InlineData(SearchFacet.Albums, Search.ShimmerShape.CardGrid)]
    [InlineData(SearchFacet.Playlists, Search.ShimmerShape.CardGrid)]
    [InlineData(SearchFacet.Genres, Search.ShimmerShape.CardGrid)]
    [InlineData(SearchFacet.Tracks, Search.ShimmerShape.Rows)]
    [InlineData(SearchFacet.Artists, Search.ShimmerShape.Rows)]
    [InlineData(SearchFacet.Episodes, Search.ShimmerShape.Rows)]
    public void ShimmerFor_HasThreeGeometries(SearchFacet facet, Search.ShimmerShape expected)
        => Assert.Equal(expected, Search.ShimmerFor(facet));

    [Theory]
    [InlineData(false, 0, 2, false)]  // the first render never slides
    [InlineData(true, 1, 1, false)]   // a re-render of the same chip never slides
    [InlineData(true, 1, 2, true)]
    public void Slides_OnlyOnARealSwitch(bool armed, int previous, int chip, bool expected)
        => Assert.Equal(expected, Search.Slides(armed, previous, chip));

    [Theory]
    [InlineData(SearchFacet.All, "search.all")]
    [InlineData(SearchFacet.Tracks, "search.songs")]
    [InlineData(SearchFacet.Podcasts, "search.podcastsShows")]
    [InlineData(SearchFacet.Profiles, "search.profiles")]
    public void FacetNameKey_NamesAnExistingLocKey(SearchFacet facet, string key)
        => Assert.Equal(key, Search.FacetNameKey(facet));

    [Theory]
    [InlineData(SearchFacet.Audiobooks, "search.noAudiobookResults")]
    [InlineData(SearchFacet.Podcasts, "search.noPodcastResults")]
    [InlineData(SearchFacet.Episodes, "search.noEpisodeResults")]
    [InlineData(SearchFacet.Profiles, "search.noProfileResults")]
    [InlineData(SearchFacet.Authors, "search.noAuthorResults")]
    [InlineData(SearchFacet.All, null)]         // ch 13 §9 gap 1: the generic captioned empty
    [InlineData(SearchFacet.Tracks, null)]
    [InlineData(SearchFacet.Albums, null)]
    [InlineData(SearchFacet.Genres, null)]
    public void EmptyKey_OnlyTheDedicatedFacetsHaveTheirOwnSentence(SearchFacet facet, string? key)
        => Assert.Equal(key, Search.EmptyKey(facet));

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(179f, 1)]
    [InlineData(376f, 2)]             // floor((376 + 16) / 196)
    [InlineData(375f, 1)]
    [InlineData(1000f, 5)]
    public void FacetGridColumns_FloorFitsAt180PlusTheGap(float width, int expected)
        => Assert.Equal(expected, Search.FacetGridColumns(width));

    [Fact]
    public void FacetGridCellWidth_SharesTheRowAndNeverCollapses()
    {
        Assert.Equal(180f, Search.FacetGridCellWidth(376f, 2));
        Assert.Equal(90f, Search.FacetGridCellWidth(50f, 3));
    }

    // ── where a genre result goes (0.2.9 SearchRoutes.OpenGenre) ────────────────────────────────────────────────────

    [Fact]
    public void GenreRoute_AGenreOrPageUriOpensItsBrowsePage()
    {
        TestScope.Fresh();
        var route = Search.GenreRoute("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw", "Pop");
        Assert.Equal(Shell.RouteKind.BrowseCategory, route.Kind);
        Assert.Equal("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw", route.Subject.Text);
        Assert.Equal("Pop", Entities.Strings.Resolve(route.Arg));

        Assert.Equal(Shell.RouteKind.BrowseCategory, Search.GenreRoute("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", "Rock").Kind);
    }

    [Fact]
    public void GenreRoute_AnythingElseReCommitsTheNameAsASearch()
    {
        TestScope.Fresh();
        var route = Search.GenreRoute("spotify:genre:", "Sleep");            // a bare prefix is not a page
        Assert.Equal(Shell.RouteKind.Search, route.Kind);
        Assert.Equal("Sleep", Entities.Strings.Resolve(route.Arg));
        Assert.Equal(Shell.RouteKind.Browse, Search.GenreRoute("", " ").Kind); // a blank name is the directory
    }
}
