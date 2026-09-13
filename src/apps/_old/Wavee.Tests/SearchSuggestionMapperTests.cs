using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class SearchSuggestionMapperTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SuggestionsFromV2_SplitsAutocompleteQueriesAndRichHits()
    {
        var suggestions = SpotifyExportMapper.SuggestionsFromV2(Root("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "item": { "__typename": "SearchAutoCompleteEntity",
            "data": { "text": "david guetta", "uri": "spotify:search:david+guetta" } } },
          { "item": { "__typename": "TrackResponseWrapper", "data": {
            "__typename": "Track",
            "uri": "spotify:track:0TDLuuLlV54CkRRUOahJb4",
            "name": "Titanium (feat. Sia)",
            "contentRating": { "label": "NONE" },
            "albumOfTrack": { "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/cover", "width": 300, "height": 300 }
            ] } },
            "artists": { "items": [
              { "profile": { "name": "David Guetta" }, "uri": "spotify:artist:1Cs0zKBU1kc0i8ypK3B9ai" },
              { "profile": { "name": "Sia" }, "uri": "spotify:artist:5WUlDfRSoLAfcVSX1WnrxN" }
            ] }
          } } },
          { "item": { "__typename": "ArtistResponseWrapper", "data": {
            "__typename": "Artist",
            "uri": "spotify:artist:1Cs0zKBU1kc0i8ypK3B9ai",
            "profile": { "name": "David Guetta" },
            "visuals": { "avatarImage": { "sources": [
              { "url": "https://i.scdn.co/image/avatar", "width": 640, "height": 640 }
            ] } }
          } } }
        ] } } } }
        """));

        Assert.Equal("david guetta", Assert.Single(suggestions.Queries));
        Assert.Equal(2, suggestions.Items.Count);
        Assert.Equal(SearchSuggestionKind.Track, suggestions.Items[0].Kind);
        Assert.Equal("Titanium (feat. Sia)", suggestions.Items[0].Title);
        Assert.Contains("David Guetta", suggestions.Items[0].Subtitle);
        Assert.Equal("https://i.scdn.co/image/cover", suggestions.Items[0].Image!.Url);
        Assert.Equal(SearchSuggestionKind.Artist, suggestions.Items[1].Kind);
        Assert.Equal("David Guetta", suggestions.Items[1].Title);
        Assert.Equal("https://i.scdn.co/image/avatar", suggestions.Items[1].Image!.Url);
    }

    [Fact]
    public void TopHitsFromV2_PreservesWrapperOrderAndMatchedFields()
    {
        var hits = SpotifyExportMapper.TopHitsFromV2(Root("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "matchedFields": [ "NAME" ], "item": { "__typename": "TrackResponseWrapper", "data": {
            "__typename": "TrackResponseWrapper",
            "uri": "spotify:track:top",
            "name": "I don't want to hurt you",
            "trackMediaType": "AUDIO",
            "albumOfTrack": { "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/top", "width": 300, "height": 300 }
            ] } },
            "artists": { "items": [
              { "profile": { "name": "Natsu" }, "uri": "spotify:artist:top" }
            ] }
          } } },
          { "matchedFields": [ "LYRICS" ], "item": { "__typename": "TrackResponseWrapper", "data": {
            "uri": "spotify:track:lyrics",
            "name": "BIRDS OF A FEATHER",
            "trackMediaType": "VIDEO",
            "albumOfTrack": { "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/lyrics", "width": 300, "height": 300 }
            ] } },
            "artists": { "items": [
              { "profile": { "name": "Billie Eilish" }, "uri": "spotify:artist:billie" }
            ] }
          } } },
          { "item": { "__typename": "PodcastResponseWrapper", "data": {
            "uri": "spotify:show:pod",
            "name": "Strength and Sthenics Podcast",
            "publisher": { "name": "Denis & Sasa" },
            "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/pod", "width": 300, "height": 300 }
            ] }
          } } },
          { "item": { "__typename": "AudiobookResponseWrapper", "data": {
            "uri": "spotify:audiobook:book",
            "name": "Summary of Goodbye, Things",
            "accessInfo": { "signifier": { "text": "Included in Premium" } },
            "authorsV2": { "items": [ { "name": "Abbey Beathan" } ] },
            "audiobookDuration": { "totalMilliseconds": 3840000 },
            "publishDate": { "isoString": "2020-01-13T00:00:00Z", "precision": "MINUTE" },
            "description": "Author(s): Abbey Beathan\nNarrator(s): Peter Prova\n\nGoodbye, Things summary.",
            "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/book", "width": 300, "height": 300 }
            ] }
          } } },
          { "matchedFields": [ "LYRICS" ], "item": {
            "__typename": "Playlist",
            "uri": "spotify:playlist:direct",
            "name": "Direct Playlist",
            "ownerV2": { "data": { "name": "Spotify" } },
            "images": { "items": [ { "sources": [
              { "url": "https://i.scdn.co/image/pl", "width": 300, "height": 300 }
            ] } ] }
          } }
        ] } } } }
        """));

        Assert.Equal(5, hits.Count);
        Assert.Equal(SearchHitKind.Track, hits[0].Kind);
        Assert.Equal("I don't want to hurt you", hits[0].Name);
        Assert.True(hits[0].MatchedTitle);
        Assert.False(hits[0].MatchedLyrics);
        Assert.False(hits[1].MatchedTitle);
        Assert.Equal(SearchHitKind.Track, hits[1].Kind);
        Assert.True(hits[1].MatchedLyrics);
        Assert.Equal("Music video", hits[1].TypeLabel);
        Assert.Equal(SearchHitKind.Podcast, hits[2].Kind);
        Assert.Equal(SearchHitKind.Audiobook, hits[3].Kind);
        Assert.Equal("Included in Premium", hits[3].AccessLabel);
        Assert.Equal("Jan 13, 2020 • 1 hr 4 min", hits[3].Meta);
        Assert.Contains("Goodbye, Things summary.", hits[3].Detail);
        Assert.Equal(SearchHitKind.Playlist, hits[4].Kind);
        Assert.Equal("Playlist • Spotify", hits[4].Subtitle);
        Assert.True(hits[4].MatchedLyrics);
    }

    // Search results used to project `associationsV3.videoAssociations.totalCount` onto a Track.HasVideo field. Both
    // halves are deliberately gone: has-video is a property of the catalogue entry, answered by the VideoAssociation
    // plane (kind 99) through VideoPresence — a second, weaker copy on the row is what let a list and its own expand
    // drawer disagree. This pins that the mapper still swallows payloads WITH the node (present, zero, absent) without
    // tripping over it.
    [Fact]
    public void SearchFromV2_IgnoresVideoAssociationNodes()
    {
        var results = SpotifyExportMapper.SearchFromV2(Root("""
        { "data": { "searchV2": { "tracksV2": { "items": [
          { "item": { "data": {
            "uri": "spotify:track:withvideo",
            "name": "Has A Video",
            "duration": { "totalMilliseconds": 180000 },
            "associationsV3": { "videoAssociations": { "totalCount": 1 } },
            "albumOfTrack": { "uri": "spotify:album:A", "name": "Album A" },
            "artists": { "items": [ { "profile": { "name": "Art" }, "uri": "spotify:artist:A" } ] }
          } } },
          { "item": { "data": {
            "uri": "spotify:track:zerocount",
            "name": "Zero Count",
            "associationsV3": { "videoAssociations": { "totalCount": 0 } },
            "albumOfTrack": { "uri": "spotify:album:A", "name": "Album A" }
          } } },
          { "item": { "data": {
            "uri": "spotify:track:nofield",
            "name": "Field Omitted",
            "albumOfTrack": { "uri": "spotify:album:A", "name": "Album A" }
          } } }
        ] } } } }
        """));

        Assert.Equal(3, results.Tracks.Count);
        Assert.Equal("spotify:track:withvideo", results.Tracks[0].Uri);
        Assert.Equal("Has A Video", results.Tracks[0].Title);
        Assert.Equal("spotify:track:zerocount", results.Tracks[1].Uri);
        Assert.Equal("spotify:track:nofield", results.Tracks[2].Uri);
    }

    [Fact]
    public void GhostFor_PicksPrefixMatch_NotTheFirstQuery()
    {
        var queries = new[] { "koffie", "loffler", "lofi sleep", "lofi hip hop" };
        Assert.Equal("loffler", SearchSuggestions.GhostFor("lo", queries));
        Assert.Equal("loffler", SearchSuggestions.GhostFor("loff", queries));
        Assert.Equal("lofi sleep", SearchSuggestions.GhostFor("lofi", queries));
        Assert.Null(SearchSuggestions.GhostFor("loffi", queries));
        Assert.Null(SearchSuggestions.GhostFor("", queries));
    }

    [Fact]
    public void SuggestionsFromV2_MapsGenreEpisodeUser()
    {
        var suggestions = SpotifyExportMapper.SuggestionsFromV2(Root("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "item": { "__typename": "SearchAutoCompleteEntity",
            "data": { "text": "koffie", "uri": "spotify:search:koffie" } } },
          { "item": { "__typename": "SearchAutoCompleteEntity",
            "data": { "text": "loffler", "uri": "spotify:search:loffler" } } },
          { "item": { "__typename": "SearchAutoCompleteEntity",
            "data": { "text": "lofi sleep", "uri": "spotify:search:lofi+sleep" } } },
          { "item": { "__typename": "GenreResponseWrapper", "data": {
            "__typename": "Genre",
            "uri": "spotify:genre:0JQ5DAqbMKFFzDl7qN9Apr",
            "name": "Sleep",
            "image": { "sources": [ { "url": "https://i.scdn.co/image/g", "width": 300, "height": 300 } ] }
          } } },
          { "item": { "__typename": "PlaylistResponseWrapper", "data": {
            "__typename": "Playlist",
            "uri": "spotify:playlist:pl",
            "name": "lofi hip hop",
            "ownerV2": { "data": { "name": "Spotify" } },
            "images": { "items": [ { "sources": [ { "url": "https://i.scdn.co/image/pl", "width": 300, "height": 300 } ] } ] }
          } } }
        ] } } } }
        """));

        Assert.Equal(new[] { "koffie", "loffler", "lofi sleep" }, suggestions.Queries);
        Assert.Equal("loffler", SearchSuggestions.GhostFor("loff", suggestions.Queries));
        Assert.Equal(SearchSuggestionKind.Genre, suggestions.Items[0].Kind);
        Assert.Equal("spotify:genre:0JQ5DAqbMKFFzDl7qN9Apr", suggestions.Items[0].Uri);
        Assert.Equal(SearchSuggestionKind.Playlist, suggestions.Items[1].Kind);
    }

    [Fact]
    public void SearchFromV2_ReadsChipOrderPlaylistsFirst()
    {
        var results = SpotifyExportMapper.SearchFromV2(Root("""
        { "data": { "searchV2": {
          "chipOrder": { "items": [
            { "typeName": "PLAYLISTS" },
            { "typeName": "TRACKS" },
            { "typeName": "EPISODES" },
            { "typeName": "GENRES" },
            { "typeName": "PODCASTS" },
            { "typeName": "ALBUMS" },
            { "typeName": "AUDIOBOOKS" },
            { "typeName": "ARTISTS" },
            { "typeName": "AUTHORS" },
            { "typeName": "USERS" }
          ] },
          "playlists": { "totalCount": 128, "items": [] },
          "tracksV2": { "totalCount": 40, "items": [] },
          "genres": { "totalCount": 8, "items": [
            { "data": { "uri": "spotify:genre:sleep", "name": "Sleep",
              "image": { "extractedColors": { "colorDark": { "hex": "#1A237E", "isFallback": false } } } } }
          ] }
        } } }
        """));

        Assert.NotNull(results.ChipOrder);
        Assert.Equal(SearchFacet.Playlists, results.ChipOrder![0].Facet);
        Assert.Equal(SearchFacet.Tracks, results.ChipOrder[1].Facet);
        Assert.Equal(SearchFacet.Genres, results.ChipOrder[3].Facet);
        Assert.Equal(128, results.PlaylistsTotal);
        Assert.Equal(8, results.GenresTotal);
        var genre = Assert.Single(results.Genres!);
        Assert.Equal("Sleep", genre.Name);
        Assert.Equal(0xFF1A237Eu, genre.Accent);
    }

    [Fact]
    public void SearchFromV2_MapsGenreAndPodcastChipAliases()
    {
        var results = SpotifyExportMapper.SearchFromV2(Root("""
        { "data": { "searchV2": {
          "chipOrder": { "items": [
            { "typeName": "GENRES_AND_MOODS" },
            { "typeName": "PODCASTS_AND_SHOWS" }
          ] },
          "genres": { "totalCount": 3, "items": [] },
          "podcasts": { "totalCount": 5, "items": [] }
        } } }
        """));

        Assert.NotNull(results.ChipOrder);
        Assert.Equal(SearchFacet.Genres, results.ChipOrder![0].Facet);
        Assert.Equal(SearchFacet.Podcasts, results.ChipOrder[1].Facet);
    }

}
