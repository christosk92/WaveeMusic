// ── Wavee.Tests/SearchDecodeTests.cs — the search folds of Spotify.Decode.Browse.cs (Wave 5, P3) ────────────────────
//
// The gate for `Decode.SearchPage` / `SearchGenres` / `SearchRelated` / `SearchSuggestions` and the `Search.Commit` they
// land through (Entities/Search.cs §6, chained from `Entities.CommitBrowse`). Every fact decodes a wire answer, commits
// it the way a UI drain does, and reads HANDLES — never a staged row.
//
// The fixtures (`Fixtures/search/`) are 0.2.9's `SearchSuggestionMapperTests` payloads (the rich suggestion rows, the
// TopHits wrapper/matchedFields shapes, the video-association track list, the chip order and its aliases), re-keyed onto
// real 22-character gids where a row must land in a table, plus the related-query and genre answers 0.3 folds into the
// one search demand (G-045). What changed from 0.2.9 is the shape of the RESULT, not of the wire:
//
//   • hits are `Edges.SearchResult` edges (kind + slot), the chrome rides `Edges.SearchHits` (matched title / lyrics,
//     video media), and the chip strip is the row's strided chip slab — not a `SearchResults` record;
//   • a facet answer is a PAGE at its offset (B1b gap 3), never a whole-list rewrite;
//   • an audiobook / author / genre HIT has no table in 0.3 and is dropped, exactly as a home card with no table is.

using System.IO;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SearchDecodeTests
{
    static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "search", name));

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    static string Resolve(StringId id) => Entities.Strings.Resolve(id);

    static void Page(byte[] json, string subject, int offset)
    {
        var s = Staging.Rent();
        Spotify.Decode.SearchPage(json, Utf8(subject), offset, s);
        TestScope.CommitAndPublish(s);
    }

    static EntityRef TrackRef(string uri) => new(EntityKind.Track, Entities.Track(EntityUri.Parse(uri.AsSpan())).Slot);
    static EntityRef AlbumRef(string uri) => new(EntityKind.Album, Entities.Album(EntityUri.Parse(uri.AsSpan())).Slot);
    static EntityRef ArtistRef(string uri) => new(EntityKind.Artist, Entities.Artist(EntityUri.Parse(uri.AsSpan())).Slot);
    static EntityRef PlaylistRef(string uri) => new(EntityKind.Playlist, Entities.Playlist(EntityUri.Parse(uri.AsSpan())).Slot);
    static EntityRef UserRef(string uri) => new(EntityKind.User, Entities.User(EntityUri.Parse(uri.AsSpan())).Slot);

    const string LyricsTrack = "spotify:track:0TDLuuLlV54CkRRUOahJb4";
    const string SiaArtist = "spotify:artist:5WUlDfRSoLAfcVSX1WnrxN";
    const string DiscoveryAlbum = "spotify:album:2noRn2Aes5aoNVsU6iWThc";
    const string DirectPlaylist = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
    const string SmokeUser = "spotify:user:smokeuser";

    // ── the subject uri ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("wavee:search:00:daft punk", true, SearchFacet.All)]
    [InlineData("wavee:search:01:daft punk", true, SearchFacet.Tracks)]
    [InlineData("wavee:search:10:x", true, SearchFacet.Authors)]
    [InlineData("wavee:search:08:", true, SearchFacet.Profiles)]
    [InlineData("wavee:search:11:x", false, SearchFacet.All)]      // past the eleven facets
    [InlineData("wavee:search:0x:x", false, SearchFacet.All)]
    [InlineData("wavee:search:00x", false, SearchFacet.All)]
    [InlineData("spotify:track:0TDLuuLlV54CkRRUOahJb4", false, SearchFacet.All)]
    [InlineData("", false, SearchFacet.All)]
    public void A_search_subject_names_its_facet_in_its_uri(string uri, bool ok, SearchFacet facet)
    {
        Assert.Equal(ok, Spotify.Decode.SearchSubjectFacet(Utf8(uri), out var parsed));
        Assert.Equal(facet, parsed);
    }

    [Fact]
    public void A_subject_that_is_not_a_search_row_stages_nothing()
    {
        var s = Staging.Rent();
        Spotify.Decode.SearchPage(Fixture("top-results-ranked.json"), "spotify:track:0TDLuuLlV54CkRRUOahJb4"u8, 0, s);
        Assert.Equal(0, s.Searches.Count);
        Assert.Equal(0, s.Edges.RunCount);
        Staging.Return(s);
    }

    // ── the ranked All answer (0.2.9 TopHitsFromV2) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_ranked_all_answer_lands_the_hits_in_server_order_and_drops_the_kind_with_no_table()
    {
        TestScope.Fresh();
        var all = Entities.Search("smoke".AsSpan());
        Page(Fixture("top-results-ranked.json"), "wavee:search:00:smoke", 0);

        Assert.True(all.Knows(SearchFields.Chips | SearchFields.Results));
        Assert.False(all.Knows(SearchFields.Genres));
        Assert.False(all.Knows(SearchFields.Related));
        Assert.Equal(SearchRowFlags.None, all.RowFlags);             // ranked: hit 0 IS the Top Result

        // Track, artist, album, (audiobook: no table, dropped — not a hole), playlist, user. The facet lists of a
        // ranked answer are NOT collected: tracksV2's own row is not a sixth hit.
        Assert.Equal(5, all.ResultCount);
        Assert.Equal(EdgeState.Complete, all.ResultState);
        Assert.Equal(TrackRef(LyricsTrack), all.ResultRef(0));
        Assert.Equal(ArtistRef(SiaArtist), all.ResultRef(1));
        Assert.Equal(AlbumRef(DiscoveryAlbum), all.ResultRef(2));
        Assert.Equal(PlaylistRef(DirectPlaylist), all.ResultRef(3));   // a direct `item` with no `data` wrapper
        Assert.Equal(UserRef(SmokeUser), all.ResultRef(4));

        Assert.Equal("BIRDS OF A FEATHER", Resolve(Entities.Track(EntityUri.Parse(LyricsTrack.AsSpan())).TitleId));
        Assert.Equal("Sia", Resolve(Entities.Artist(EntityUri.Parse(SiaArtist.AsSpan())).NameId));
        Assert.Equal("Direct Playlist", Resolve(Entities.Playlist(EntityUri.Parse(DirectPlaylist.AsSpan())).TitleId));
        Assert.Equal("Smoke User", Resolve(Entities.User(EntityUri.Parse(SmokeUser.AsSpan())).NameId));   // displayName wins
        Assert.Equal(1, all.CountOf(EntityKind.Track));
        Assert.Equal(0, all.CountOf(EntityKind.Show));
    }

    [Fact]
    public void A_ranked_hit_carries_its_matched_fields_and_its_media_type_as_chrome()
    {
        TestScope.Fresh();
        var all = Entities.Search("smoke".AsSpan());
        Page(Fixture("top-results-ranked.json"), "wavee:search:00:smoke", 0);

        // 0.2.9: hits[1].MatchedLyrics + TypeLabel "Music video"; hits[0].MatchedTitle from NAME; the direct playlist's
        // LYRICS. A hit with no matchedFields carries nothing.
        Assert.Equal(SearchHitFlags.MatchedLyrics | SearchHitFlags.VideoMedia, all.FlagsOf(all.ResultRef(0)));
        Assert.Equal(SearchHitFlags.MatchedTitle, all.FlagsOf(all.ResultRef(1)));
        Assert.Equal(SearchHitFlags.None, all.FlagsOf(all.ResultRef(2)));
        Assert.Equal(SearchHitFlags.MatchedLyrics, all.FlagsOf(all.ResultRef(3)));
        Assert.Equal(SearchHitFlags.None, all.FlagsOf(all.ResultRef(4)));
        Assert.Equal(SearchHitFlags.None, all.FlagsOf(default));
    }

    [Fact]
    public void A_ranked_all_answer_lands_the_chip_strip_in_server_rank_with_each_lists_own_total()
    {
        TestScope.Fresh();
        var all = Entities.Search("smoke".AsSpan());
        Page(Fixture("top-results-ranked.json"), "wavee:search:00:smoke", 0);

        // chipOrder: TRACKS, ARTISTS, ALBUMS, PLAYLISTS (its own chip total 7), USERS, TRACKS (a repeat, dropped).
        Assert.Equal(0, all.RankOf(SearchFacet.Tracks));
        Assert.Equal(1, all.RankOf(SearchFacet.Artists));
        Assert.Equal(2, all.RankOf(SearchFacet.Albums));
        Assert.Equal(3, all.RankOf(SearchFacet.Playlists));
        Assert.Equal(4, all.RankOf(SearchFacet.Profiles));             // USERS is Profiles
        Assert.Equal(5, all.RankOf(SearchFacet.Podcasts));             // counted by the server, not chipped: after the strip

        Assert.Equal(120, all.RawTotalOf(SearchFacet.Tracks));         // the list's own totalCount
        Assert.Equal(40, all.RawTotalOf(SearchFacet.Artists));
        Assert.Equal(Search.NoTotal, all.RawTotalOf(SearchFacet.Albums));   // chipped with no total: an omission, not 0
        Assert.Equal(7, all.RawTotalOf(SearchFacet.Playlists));        // no list total: the chip's own
        Assert.Equal(Search.NoTotal, all.RawTotalOf(SearchFacet.Profiles));
        Assert.Equal(3, all.RawTotalOf(SearchFacet.Podcasts));

        Assert.False(all.Advertises(SearchFacet.Episodes));
        Assert.Equal(Search.Unranked, all.RankOf(SearchFacet.Episodes));
        Assert.Equal(Search.NoTotal, all.RawTotalOf(SearchFacet.Episodes));

        // The tab row the page renders from this row (Search.FacetsFrom over the committed columns).
        Span<SearchFacet> tabs = stackalloc SearchFacet[SearchTable.FacetCount];
        int n = all.FacetsFrom(tabs);
        Assert.Equal(
            [SearchFacet.All, SearchFacet.Tracks, SearchFacet.Artists, SearchFacet.Albums, SearchFacet.Playlists,
             SearchFacet.Profiles, SearchFacet.Podcasts],
            tabs[..n].ToArray());
    }

    // ── the unranked All answer (0.2.9 SearchFromV2 + the no-top-hits fallback) ─────────────────────────────────────

    [Fact]
    public void An_all_answer_with_no_ranked_list_collects_the_four_lists_in_wire_order()
    {
        TestScope.Fresh();
        var all = Entities.Search("collected".AsSpan());
        Page(Fixture("top-results-collected.json"), "wavee:search:00:collected", 0);

        Assert.True(all.Knows(SearchFields.Chips | SearchFields.Results));
        Assert.Equal(SearchRowFlags.Collected, all.RowFlags);        // the page interleaves (Search.FallbackRows)
        // playlists, tracksV2 (2), albumsV2, artists — in the order the wire wrote them; episodes and genres are not
        // collected into the All list.
        Assert.Equal(5, all.ResultCount);
        Assert.Equal(PlaylistRef("spotify:playlist:37i9dQZF1DX0XUsuxWHRQd"), all.ResultRef(0));
        Assert.Equal(TrackRef("spotify:track:2plbrEY59IikOBgBGLjaoe"), all.ResultRef(1));
        Assert.Equal(TrackRef("spotify:track:6habFhsOp2NvshLv26DqMb"), all.ResultRef(2));
        Assert.Equal(AlbumRef(DiscoveryAlbum), all.ResultRef(3));
        Assert.Equal(ArtistRef("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi"), all.ResultRef(4));
        Assert.Equal(0, all.CountOf(EntityKind.Episode));
        Assert.Equal(2, all.CountOf(EntityKind.Track));
        Assert.Equal("Daft Punk", Resolve(Entities.Artist(EntityUri.Parse("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi".AsSpan())).NameId));
    }

    [Fact]
    public void SearchFromV2_ReadsChipOrderPlaylistsFirst()
    {
        // 0.2.9 SearchSuggestionMapperTests, same payload: the chip ORDER is the server's and the totals are the lists'.
        TestScope.Fresh();
        var all = Entities.Search("chips".AsSpan());
        Page(Fixture("top-results-collected.json"), "wavee:search:00:chips", 0);

        Assert.Equal(0, all.RankOf(SearchFacet.Playlists));
        Assert.Equal(1, all.RankOf(SearchFacet.Tracks));
        Assert.Equal(3, all.RankOf(SearchFacet.Genres));
        Assert.Equal(128, all.RawTotalOf(SearchFacet.Playlists));
        Assert.Equal(40, all.RawTotalOf(SearchFacet.Tracks));
        Assert.Equal(8, all.RawTotalOf(SearchFacet.Genres));
        Assert.Equal(9, all.RankOf(SearchFacet.Profiles));
        Assert.Equal(Search.NoTotal, all.RawTotalOf(SearchFacet.Authors));
        // 0.2.9 also asserted the genre tile's name and accent here; in 0.3 genre TILES are the SearchGenres answer
        // (A_genres_answer_lands_the_tiles_as_browse_nodes), and the search page decodes no tile colour.
    }

    [Fact]
    public void SearchFromV2_MapsGenreAndPodcastChipAliases()
    {
        TestScope.Fresh();
        var all = Entities.Search("aliases".AsSpan());
        Page("""
        { "data": { "searchV2": {
          "chipOrder": { "items": [
            { "typeName": "GENRES_AND_MOODS" },
            { "typeName": "PODCASTS_AND_SHOWS" },
            { "typeName": "SONGS" },
            { "typeName": "PROFILES" },
            { "typeName": "SOMETHING_NEW" }
          ] },
          "genres": { "totalCount": 3, "items": [] },
          "podcasts": { "totalCount": 5, "items": [] }
        } } }
        """u8.ToArray(), "wavee:search:00:aliases", 0);

        Assert.Equal(0, all.RankOf(SearchFacet.Genres));
        Assert.Equal(1, all.RankOf(SearchFacet.Podcasts));
        Assert.Equal(2, all.RankOf(SearchFacet.Tracks));
        Assert.Equal(3, all.RankOf(SearchFacet.Profiles));
        Assert.Equal(3, all.RawTotalOf(SearchFacet.Genres));
        Assert.Equal(5, all.RawTotalOf(SearchFacet.Podcasts));
        int advertised = 0;
        for (int f = 0; f < SearchTable.FacetCount; f++) if (all.Advertises((SearchFacet)f)) advertised++;
        Assert.Equal(4, advertised);                                  // an unknown chip name is dropped, not guessed
        Assert.True(all.Knows(SearchFields.Results));                  // a real, empty answer
        Assert.Equal(0, all.ResultCount);
    }

    // ── a facet answer is a PAGE (B1b gap 3) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_facet_page_lands_at_its_offset_and_the_next_page_appends()
    {
        TestScope.Fresh();
        var songs = Entities.Search("paged".AsSpan(), SearchFacet.Tracks);
        Page(Fixture("tracks-page-0.json"), "wavee:search:01:paged", 0);

        Assert.True(songs.Knows(SearchFields.Results));
        Assert.False(songs.Knows(SearchFields.Chips));                // only the All row carries the strip
        Assert.Equal(SearchRowFlags.None, songs.RowFlags);
        // 0.2.9 SearchFromV2_IgnoresVideoAssociationNodes: all three rows survive, in order, whatever the association.
        Assert.Equal(3, songs.ResultCount);
        Assert.Equal(5, songs.ResultTotal);
        Assert.Equal(EdgeState.Partial, songs.ResultState);
        var first = TrackRef("spotify:track:0TDLuuLlV54CkRRUOahJb4");
        Assert.Equal(first, songs.ResultRef(0));
        Assert.Equal(TrackRef("spotify:track:2plbrEY59IikOBgBGLjaoe"), songs.ResultRef(1));
        Assert.Equal(TrackRef("spotify:track:6habFhsOp2NvshLv26DqMb"), songs.ResultRef(2));
        Assert.Equal("Has A Video", Resolve(Entities.Track(EntityUri.Parse("spotify:track:0TDLuuLlV54CkRRUOahJb4".AsSpan())).TitleId));
        Assert.Equal(SearchHitFlags.MatchedTitle, songs.FlagsOf(first));

        Page(Fixture("tracks-page-1.json"), "wavee:search:01:paged", 3);

        Assert.Equal(5, songs.ResultCount);
        Assert.Equal(EdgeState.Complete, songs.ResultState);
        Assert.Equal(first, songs.ResultRef(0));                       // page 0 is untouched — never a whole-list rewrite
        Assert.Equal(TrackRef("spotify:track:7qiZfU4dY1lWllzX7mPBI3"), songs.ResultRef(3));
        Assert.Equal(TrackRef("spotify:track:3n3Ppam7vgaVa1iaRUc9Lp"), songs.ResultRef(4));
        // The chrome run is page 0's: a later page neither wipes page 0's flags nor lands its own.
        Assert.Equal(SearchHitFlags.MatchedTitle, songs.FlagsOf(first));
        Assert.Equal(SearchHitFlags.None, songs.FlagsOf(songs.ResultRef(4)));
    }

    [Fact]
    public void A_facet_pages_total_is_never_less_than_what_it_already_holds()
    {
        TestScope.Fresh();
        var songs = Entities.Search("short".AsSpan(), SearchFacet.Tracks);
        // A page at offset 5 whose server total shrank to 4: max(totalCount, offset + n) = 7.
        Page("""
        { "data": { "searchV2": { "tracksV2": { "totalCount": 4, "items": [
          { "item": { "data": { "uri": "spotify:track:7qiZfU4dY1lWllzX7mPBI3", "name": "Sixth" } } },
          { "item": { "data": { "uri": "spotify:track:3n3Ppam7vgaVa1iaRUc9Lp", "name": "Seventh" } } }
        ] } } } }
        """u8.ToArray(), "wavee:search:01:short", 5);

        Assert.Equal(7, songs.ResultTotal);
        Assert.Equal(7, songs.ResultCount);
        Assert.True(songs.ResultRef(0).IsNone);                         // the hole a page has not landed in yet
        Assert.Equal(TrackRef("spotify:track:7qiZfU4dY1lWllzX7mPBI3"), songs.ResultRef(5));
    }

    [Fact]
    public void A_facet_answer_ignores_every_other_list_in_the_body()
    {
        TestScope.Fresh();
        var albums = Entities.Search("smoke".AsSpan(), SearchFacet.Albums);
        // The ranked fixture holds a tracksV2 list and no albumsV2: the Albums row answers with nothing of its own.
        Page(Fixture("top-results-ranked.json"), "wavee:search:02:smoke", 0);

        Assert.True(albums.Knows(SearchFields.Results));
        Assert.Equal(0, albums.ResultCount);
        Assert.Equal(SearchRowFlags.None, albums.RowFlags);
    }

    // ── searchGenres (G-045) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_genres_answer_lands_the_tiles_as_browse_nodes()
    {
        TestScope.Fresh();
        var all = Entities.Search("genres".AsSpan());
        var s = Staging.Rent();
        Spotify.Decode.SearchGenres(Fixture("genres.json"), Utf8("wavee:search:00:genres"), s);
        TestScope.CommitAndPublish(s);

        Assert.True(all.Knows(SearchFields.Genres));
        Assert.False(all.Knows(SearchFields.Results));
        Assert.Equal(2, all.GenreSlots.Length);                        // the NotFound tile is dropped

        var pop = Entities.BrowseNode("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan());
        Assert.Equal(pop.Slot, all.GenreSlots[0]);
        Assert.Equal(Entities.BrowseNode("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan()).Slot, all.GenreSlots[0]);   // folded
        Assert.Equal("Pop", Resolve(pop.TitleId));
        Assert.Equal("https://i.scdn.co/image/pop", Resolve(pop.ImageId));
        Assert.True(pop.Knows(BrowseFields.Identity));
        Assert.False(pop.Knows(BrowseFields.Page));

        var rock = Entities.BrowseNode("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr".AsSpan());
        Assert.Equal(rock.Slot, all.GenreSlots[1]);
        Assert.Equal("Rock", Resolve(rock.TitleId));

        // The All row counts its genre tiles as the Genres tab's local list: with no chip strip, the fallback shows it.
        Span<SearchFacet> tabs = stackalloc SearchFacet[SearchTable.FacetCount];
        int n = all.FacetsFrom(tabs);
        Assert.Equal([SearchFacet.All, SearchFacet.Genres], tabs[..n].ToArray());
    }

    [Fact]
    public void A_genre_tile_is_thin_so_a_listed_directory_tile_keeps_its_own_title()
    {
        TestScope.Fresh();
        var dir = Staging.Rent();
        Spotify.Decode.BrowseAll(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", "browse-all.json")), dir);
        TestScope.CommitAndPublish(dir);

        var all = Entities.Search("thin".AsSpan());
        var s = Staging.Rent();
        Spotify.Decode.SearchGenres("""
        { "data": { "searchV2": { "genres": { "items": [
          { "data": { "uri": "spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw", "name": "Pop (as search named it)" } }
        ] } } } }
        """u8, Utf8("wavee:search:00:thin"), s);
        TestScope.CommitAndPublish(s);

        var pop = Entities.BrowseNode("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan());
        Assert.Equal(pop.Slot, Assert.Single(all.GenreSlots.ToArray()));
        Assert.Equal("Pop", Resolve(pop.TitleId));
    }

    [Fact]
    public void A_later_genres_answer_replaces_the_tiles()
    {
        TestScope.Fresh();
        var all = Entities.Search("again".AsSpan());
        var s = Staging.Rent();
        Spotify.Decode.SearchGenres(Fixture("genres.json"), Utf8("wavee:search:00:again"), s);
        TestScope.CommitAndPublish(s);
        Assert.Equal(2, all.GenreSlots.Length);

        var empty = Staging.Rent();
        Spotify.Decode.SearchGenres("""{"data":{"searchV2":{"genres":{"items":[]}}}}"""u8, Utf8("wavee:search:00:again"), empty);
        TestScope.CommitAndPublish(empty);
        Assert.Equal(0, all.GenreSlots.Length);
        Assert.True(all.Knows(SearchFields.Genres));
    }

    // ── searchSuggestions as the page's related queries (G-045) ─────────────────────────────────────────────────────

    static string[] RelatedOf(Search row)
    {
        var related = row.Related;
        var texts = new string[related.Length];
        for (int i = 0; i < related.Length; i++) texts[i] = Resolve(related[i].Query);
        return texts;
    }

    [Fact]
    public void A_suggestions_answer_lands_the_related_queries_deduped_and_capped()
    {
        TestScope.Fresh();
        var all = Entities.Search("a".AsSpan());
        var s = Staging.Rent();
        Spotify.Decode.SearchRelated(Fixture("suggestions-related.json"), Utf8("wavee:search:00:a"), s);
        TestScope.CommitAndPublish(s);

        Assert.True(all.Knows(SearchFields.Related));
        // "A HA" repeats "a ha" (ASCII case-insensitive, the first spelling wins); the rich track row is not a query;
        // "aurora" and "alan walker" are past the cap.
        Assert.Equal(
            ["a ha", "adele", "ariana grande", "arctic monkeys", "avicii", "abba", "ac/dc", "aerosmith", "alicia keys", "anne-marie"],
            RelatedOf(all));
        Assert.Equal(Spotify.Decode.MaxRelated, all.Related.Length);
    }

    [Fact]
    public void A_later_suggestions_answer_replaces_the_related_queries()
    {
        TestScope.Fresh();
        var all = Entities.Search("b".AsSpan());
        var s = Staging.Rent();
        Spotify.Decode.SearchRelated(Fixture("suggestions-related.json"), Utf8("wavee:search:00:b"), s);
        TestScope.CommitAndPublish(s);

        var next = Staging.Rent();
        Spotify.Decode.SearchRelated("""
        {"data":{"searchV2":{"topResultsV2":{"itemsV2":[
          {"item":{"__typename":"SearchAutoCompleteEntity","data":{"text":"beyonce"}}}
        ]}}}}
        """u8, Utf8("wavee:search:00:b"), next);
        TestScope.CommitAndPublish(next);

        Assert.Equal(["beyonce"], RelatedOf(all));

        var none = Staging.Rent();
        Spotify.Decode.SearchRelated("""{"data":{"searchV2":{"topResultsV2":{"itemsV2":[]}}}}"""u8, Utf8("wavee:search:00:b"), none);
        TestScope.CommitAndPublish(none);
        Assert.Empty(RelatedOf(all));                                   // a real, empty answer
        Assert.True(all.Knows(SearchFields.Related));
    }
}

/// <summary>The omnibar's suggestion answer (`Decode.SearchSuggestions`, 0.2.9 `SuggestionsFromV2`) — a plain value,
/// not table data. Ported from 0.2.9 `SearchSuggestionMapperTests`; the item record is `Shell.Omnibar.Item` (Kind, Uri,
/// Title, Subtitle, ImageUrl), the kind enum `Shell.Omnibar.ItemKind`.</summary>
[Collection(EntitiesCollection.Name)]
public class SearchSuggestionDecodeTests
{
    static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "search", name));

    [Fact]
    public void SuggestionsFromV2_SplitsAutocompleteQueriesAndRichHits()
    {
        var suggestions = Spotify.Decode.SearchSuggestions(Fixture("suggestions-rich.json"));

        Assert.Equal("david guetta", Assert.Single(suggestions.Queries));
        Assert.Equal(2, suggestions.Items.Count);
        Assert.Equal(Shell.Omnibar.ItemKind.Track, suggestions.Items[0].Kind);
        Assert.Equal("Titanium (feat. Sia)", suggestions.Items[0].Title);
        Assert.Contains("David Guetta", suggestions.Items[0].Subtitle);
        Assert.Equal(Loc.Get(Strings.Search.SubtitleSong) + " - David Guetta, Sia", suggestions.Items[0].Subtitle);
        Assert.Equal("https://i.scdn.co/image/cover", suggestions.Items[0].ImageUrl);
        Assert.Equal("spotify:track:0TDLuuLlV54CkRRUOahJb4", suggestions.Items[0].Uri.Text);
        Assert.Equal(Shell.Omnibar.ItemKind.Artist, suggestions.Items[1].Kind);
        Assert.Equal("David Guetta", suggestions.Items[1].Title);
        Assert.Equal(Loc.Get(Strings.Search.TypeArtist), suggestions.Items[1].Subtitle);
        Assert.Equal("https://i.scdn.co/image/avatar", suggestions.Items[1].ImageUrl);
    }

    [Fact]
    public void SuggestionsFromV2_MapsGenreEpisodeUser()
    {
        var suggestions = Spotify.Decode.SearchSuggestions(Fixture("suggestions-genre-episode-user.json"));

        Assert.Equal(new[] { "koffie", "loffler", "lofi sleep" }, suggestions.Queries);
        Assert.Equal("loffler", Shell.Omnibar.Suggestions.GhostFor("loff", suggestions.Queries));
        Assert.Equal(4, suggestions.Items.Count);

        Assert.Equal(Shell.Omnibar.ItemKind.Genre, suggestions.Items[0].Kind);
        Assert.Equal("spotify:genre:0JQ5DAqbMKFFzDl7qN9Apr", suggestions.Items[0].Uri.Text);
        Assert.Equal(Loc.Get(Strings.Search.TypeGenre), suggestions.Items[0].Subtitle);
        Assert.Equal("https://i.scdn.co/image/g", suggestions.Items[0].ImageUrl);

        Assert.Equal(Shell.Omnibar.ItemKind.Playlist, suggestions.Items[1].Kind);
        Assert.Equal("Spotify", suggestions.Items[1].Subtitle);                     // the owner's name
        Assert.Equal("https://i.scdn.co/image/pl", suggestions.Items[1].ImageUrl);  // the WIDEST source (0.2.9 PickImage)

        Assert.Equal(Shell.Omnibar.ItemKind.Episode, suggestions.Items[2].Kind);
        Assert.Equal("Koffie & Code", suggestions.Items[2].Title);
        Assert.Equal("The Morning Show", suggestions.Items[2].Subtitle);

        Assert.Equal(Shell.Omnibar.ItemKind.User, suggestions.Items[3].Kind);
        Assert.Equal("Koffie Liefhebber", suggestions.Items[3].Title);              // displayName, not username
        Assert.Equal(Loc.Get(Strings.Search.TypeUser), suggestions.Items[3].Subtitle);
        Assert.Equal("https://i.scdn.co/image/u", suggestions.Items[3].ImageUrl);
        Assert.Equal("spotify:user:koffieliefhebber", suggestions.Items[3].Uri.Text);
    }

    [Fact]
    public void SuggestionsFromV2_MapsAlbumTypePodcastPublisherAndAudiobookAuthor()
    {
        var suggestions = Spotify.Decode.SearchSuggestions("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "item": { "__typename": "AlbumResponseWrapper", "data": {
            "__typename": "Album", "uri": "spotify:album:2noRn2Aes5aoNVsU6iWThc", "name": "One More Time", "type": "SINGLE",
            "artists": { "items": [ { "profile": { "name": "Daft Punk" } } ] } } } },
          { "item": { "__typename": "AlbumResponseWrapper", "data": {
            "__typename": "Album", "uri": "spotify:album:4m2880jivSbbyEGAKfITCa", "name": "Untyped" } } },
          { "item": { "__typename": "PodcastResponseWrapper", "data": {
            "uri": "spotify:show:5CfCWKI5pZ28U0uOzXkDHe", "name": "Strength and Sthenics Podcast",
            "publisher": { "name": "Denis & Sasa" } } } },
          { "item": { "__typename": "AudiobookResponseWrapper", "data": {
            "uri": "spotify:audiobook:7iHfbu1YPACw6oZPAFJtqe", "name": "Summary of Goodbye, Things",
            "authorsV2": { "items": [ { "name": "Abbey Beathan" } ] } } } },
          { "item": { "__typename": "TrackResponseWrapper", "data": {
            "__typename": "Track", "uri": "spotify:track:7qiZfU4dY1lWllzX7mPBI3", "name": "Crowded",
            "artists": { "items": [
              { "profile": { "name": "A" } }, { "profile": { "name": "B" } }, { "profile": { "name": "C" } }, { "profile": { "name": "D" } }
            ] } } } }
        ] } } } }
        """u8);

        Assert.Empty(suggestions.Queries);
        Assert.Equal(5, suggestions.Items.Count);
        Assert.Equal(Shell.Omnibar.ItemKind.Album, suggestions.Items[0].Kind);
        Assert.Equal("Single - Daft Punk", suggestions.Items[0].Subtitle);          // the wire's release type, title-cased
        Assert.Equal(Loc.Get(Strings.Search.TypeAlbum), suggestions.Items[1].Subtitle);
        Assert.Equal(Shell.Omnibar.ItemKind.Podcast, suggestions.Items[2].Kind);
        Assert.Equal("Denis & Sasa", suggestions.Items[2].Subtitle);
        Assert.Equal(Shell.Omnibar.ItemKind.Audiobook, suggestions.Items[3].Kind);
        Assert.Equal("Abbey Beathan", suggestions.Items[3].Subtitle);
        Assert.Equal(Loc.Get(Strings.Search.SubtitleSong) + " - A, B, C, ...", suggestions.Items[4].Subtitle);   // 0.2.9 JoinNames
    }

    [Fact]
    public void Suggestions_DedupeQueriesCaseInsensitively_RowsByUri_AndRowsByWhatTheyDisplay()
    {
        var suggestions = Spotify.Decode.SearchSuggestions("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "item": { "__typename": "SearchAutoCompleteEntity", "data": { "text": "Koffie" } } },
          { "item": { "__typename": "SearchAutoCompleteEntity", "data": { "text": "koffie" } } },
          { "item": { "__typename": "TrackResponseWrapper", "data": { "__typename": "Track",
            "uri": "spotify:track:0TDLuuLlV54CkRRUOahJb4", "name": "Koffie", "artists": { "items": [ { "profile": { "name": "Band" } } ] } } } },
          { "item": { "__typename": "TrackResponseWrapper", "data": { "__typename": "Track",
            "uri": "spotify:track:0TDLuuLlV54CkRRUOahJb4", "name": "Koffie (same uri)" } } },
          { "item": { "__typename": "TrackResponseWrapper", "data": { "__typename": "Track",
            "uri": "spotify:track:2plbrEY59IikOBgBGLjaoe", "name": "Koffie", "artists": { "items": [ { "profile": { "name": "Band" } } ] } } } },
          { "item": { "__typename": "ArtistResponseWrapper", "data": { "__typename": "Artist",
            "uri": "spotify:artist:1Cs0zKBU1kc0i8ypK3B9ai", "profile": { "name": "Koffie" } } } }
        ] } } } }
        """u8);

        Assert.Equal(["Koffie"], suggestions.Queries);                              // the first spelling wins
        // The relinked duplicate (distinct uri, same kind + title + subtitle) reads as one song; the same uri twice is one
        // row; an artist that shares the title is a different display identity and stays.
        Assert.Equal(2, suggestions.Items.Count);
        Assert.Equal("spotify:track:0TDLuuLlV54CkRRUOahJb4", suggestions.Items[0].Uri.Text);
        Assert.Equal(Shell.Omnibar.ItemKind.Artist, suggestions.Items[1].Kind);
    }

    [Fact]
    public void Suggestions_StopReadingOnceEightQueriesAndSixteenRowsAreIn()
    {
        var json = new StringBuilder("""{ "data": { "searchV2": { "topResultsV2": { "itemsV2": [""");
        for (int i = 0; i < 8; i++)
            json.Append("{ \"item\": { \"__typename\": \"SearchAutoCompleteEntity\", \"data\": { \"text\": \"query ").Append(i).Append("\" } } },");
        for (int i = 0; i < 20; i++)
            json.Append("""{ "item": { "__typename": "ArtistResponseWrapper", "data": { "__typename": "Artist", "uri": "spotify:artist:suggest""")
                .Append(i).Append("\", \"profile\": { \"name\": \"Artist ").Append(i).Append("\" } } } },");
        json.Append("""{ "item": { "__typename": "SearchAutoCompleteEntity", "data": { "text": "too late" } } }""");
        json.Append("] } } } }");

        var suggestions = Spotify.Decode.SearchSuggestions(Encoding.UTF8.GetBytes(json.ToString()));

        Assert.Equal(8, suggestions.Queries.Count);
        Assert.Equal(16, suggestions.Items.Count);
        Assert.DoesNotContain("too late", suggestions.Queries);
        Assert.Equal("Artist 15", suggestions.Items[15].Title);
    }

    [Fact]
    public void A_malformed_or_empty_answer_is_the_empty_suggestions_value()
    {
        Assert.Same(Shell.Omnibar.Suggestions.Empty, Spotify.Decode.SearchSuggestions("not json"u8));
        Assert.Same(Shell.Omnibar.Suggestions.Empty, Spotify.Decode.SearchSuggestions("""{"data":{}}"""u8));
        Assert.Same(Shell.Omnibar.Suggestions.Empty, Spotify.Decode.SearchSuggestions(
            """{"data":{"searchV2":{"topResultsV2":{"itemsV2":[{"item":{"__typename":"NotFound","data":{}}}]}}}}"""u8));
        Assert.Same(Shell.Omnibar.Suggestions.Empty, Spotify.Decode.SearchSuggestions(ReadOnlySpan<byte>.Empty));
    }
}
