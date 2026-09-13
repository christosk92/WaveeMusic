// ── Wavee.Tests/SpotifyApiTests.cs — the request builders, the metadata batcher, the pathfinder body ──────────────
//
// Wave 2's gate for `Spotify/Spotify.Api.cs` (plan §5 Wave 2: "request builders return the request (pure)"). Owner D's
// `SpotifySessionTests` already pins `Build` — the route a KIND produces. What is pinned here is the layer above it:
// which kind and which arguments each of the ~50 request functions picks, how 700 uris become three POSTs, how a uri's
// several traits stay in ONE entity request, and what the pathfinder body actually looks like on the wire.
//
// NOT ONE OF THESE OPENS A SOCKET. Every fact below is either a pure function or a body the encoder produced, read
// back with the SAME generated parser the service would use — which is the only honest way to assert a protobuf shape
// without a capture, and is the rule `Wavee.Tests.csproj` already writes down for Wave 2's fixtures.

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

public class SpotifyApiBatchingTests
{
    [Fact]
    public void Seven_hundred_uris_are_three_posts_of_three_hundred_three_hundred_and_one_hundred()
    {
        Span<Range> runs = stackalloc Range[8];
        int n = Spotify.Api.Batches(700, runs);

        Assert.Equal(3, n);
        Assert.Equal(new Range(0, 300), runs[0]);
        Assert.Equal(new Range(300, 600), runs[1]);
        Assert.Equal(new Range(600, 700), runs[2]);
    }

    [Fact]
    public void Exactly_three_hundred_uris_are_one_post_and_three_hundred_and_one_are_two()
    {
        Span<Range> runs = stackalloc Range[8];
        Assert.Equal(1, Spotify.Api.Batches(300, runs));
        Assert.Equal(new Range(0, 300), runs[0]);

        Assert.Equal(2, Spotify.Api.Batches(301, runs));
        Assert.Equal(new Range(300, 301), runs[1]);
    }

    [Fact]
    public void No_uris_is_no_posts()
    {
        Span<Range> runs = stackalloc Range[4];
        Assert.Equal(0, Spotify.Api.Batches(0, runs));
    }

    [Theory]
    [InlineData(EntityKind.Track, Xm.ExtensionKind.TrackV4)]
    [InlineData(EntityKind.Episode, Xm.ExtensionKind.EpisodeV4)]
    [InlineData(EntityKind.Album, Xm.ExtensionKind.AlbumV4)]
    [InlineData(EntityKind.Artist, Xm.ExtensionKind.ArtistV4)]
    [InlineData(EntityKind.Show, Xm.ExtensionKind.ShowV4)]
    [InlineData(EntityKind.Playlist, Xm.ExtensionKind.ListMetadataV2)]
    public void Every_catalogue_kind_has_its_one_trait(EntityKind kind, Xm.ExtensionKind expected)
        => Assert.Equal(expected, Spotify.Api.CatalogKindOf(kind));

    /// <summary>A kind the metadata service does not serve is not asked for at all — sending kind 0 makes the service
    /// answer an error for the whole batch, which is how one unrecognised uri used to blank 300 rows.</summary>
    [Theory]
    [InlineData(EntityKind.User)]
    [InlineData(EntityKind.Concert)]
    [InlineData(EntityKind.Unknown)]
    public void A_kind_the_service_does_not_serve_is_never_asked_for(EntityKind kind)
        => Assert.Equal(Xm.ExtensionKind.UnknownExtension, Spotify.Api.CatalogKindOf(kind));

    [Fact]
    public void Several_traits_for_one_uri_are_grouped_into_one_entity_request()
    {
        string[] uris = ["spotify:track:a", "spotify:track:b", "spotify:track:a"];
        Xm.ExtensionKind[] kinds =
        [
            Xm.ExtensionKind.TrackV4,
            Xm.ExtensionKind.TrackV4,
            Xm.ExtensionKind.TrackDescriptor,
        ];

        var request = Xm.BatchedEntityRequest.Parser.ParseFrom(
            Spotify.Api.MetadataBody(uris, kinds, "SE", "premium"));

        // Two entity requests, not three: the repeated uri folded into the one that was already there.
        Assert.Equal(2, request.EntityRequest.Count);
        Assert.Equal("spotify:track:a", request.EntityRequest[0].EntityUri);
        Assert.Equal(2, request.EntityRequest[0].Query.Count);
        Assert.Equal(Xm.ExtensionKind.TrackV4, request.EntityRequest[0].Query[0].ExtensionKind);
        Assert.Equal(Xm.ExtensionKind.TrackDescriptor, request.EntityRequest[0].Query[1].ExtensionKind);
        Assert.Equal("spotify:track:b", request.EntityRequest[1].EntityUri);
        Assert.Single(request.EntityRequest[1].Query);
    }

    [Fact]
    public void The_batch_header_carries_the_market_the_catalogue_and_a_sixteen_byte_task_id()
    {
        string[] uris = ["spotify:album:x"];
        Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.AlbumV4];

        var request = Xm.BatchedEntityRequest.Parser.ParseFrom(
            Spotify.Api.MetadataBody(uris, kinds, "GB", "free"));

        Assert.Equal("GB", request.Header.Country);
        Assert.Equal("free", request.Header.Catalogue);
        Assert.Equal(16, request.Header.TaskId.Length);
    }

    /// <summary>Two bodies for the same uris are NOT byte-identical: the task id is fresh per request, which is what
    /// the service dedupes retries on. A test that asserted equality here would be asserting a bug.</summary>
    [Fact]
    public void Each_body_carries_a_fresh_task_id()
    {
        string[] uris = ["spotify:album:x"];
        Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.AlbumV4];

        var first = Xm.BatchedEntityRequest.Parser.ParseFrom(Spotify.Api.MetadataBody(uris, kinds, "", ""));
        var second = Xm.BatchedEntityRequest.Parser.ParseFrom(Spotify.Api.MetadataBody(uris, kinds, "", ""));

        Assert.NotEqual(first.Header.TaskId, second.Header.TaskId);
    }

    [Fact]
    public void A_body_with_nothing_askable_in_it_is_empty_rather_than_a_request_for_nothing()
    {
        string[] uris = ["spotify:user:bob"];
        Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.UnknownExtension];

        Assert.Empty(Spotify.Api.MetadataBody(uris, kinds, "", ""));
        Assert.Empty(Spotify.Api.MetadataBody("spotify:user:bob", kinds, "", ""));
    }

    [Fact]
    public void One_uri_with_four_traits_is_one_entity_request_with_four_queries()
    {
        Xm.ExtensionKind[] kinds =
        [
            Xm.ExtensionKind.VideoAssociations,
            Xm.ExtensionKind.AudioAssociations,
            Xm.ExtensionKind.AudioFiles,
            Xm.ExtensionKind.ThreebandWaveforms,
        ];

        var request = Xm.BatchedEntityRequest.Parser.ParseFrom(
            Spotify.Api.MetadataBody("spotify:track:z", kinds, "US", "premium"));

        Assert.Single(request.EntityRequest);
        Assert.Equal(4, request.EntityRequest[0].Query.Count);
        for (int i = 0; i < kinds.Length; i++)
            Assert.Equal(kinds[i], request.EntityRequest[0].Query[i].ExtensionKind);
    }
}

public class SpotifyApiPathfinderTests
{
    static JsonDocument Body(byte[] bytes) => JsonDocument.Parse(bytes);

    [Fact]
    public void A_pathfinder_body_is_variables_then_operation_name_then_the_persisted_query()
    {
        byte[] bytes = Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Tracks, "wet leg", 0, 20);
        string json = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("{\"variables\":{", json, StringComparison.Ordinal);
        using JsonDocument document = Body(bytes);
        JsonElement root = document.RootElement;
        Assert.Equal("searchTracks", root.GetProperty("operationName").GetString());
        JsonElement persisted = root.GetProperty("extensions").GetProperty("persistedQuery");
        Assert.Equal(1, persisted.GetProperty("version").GetInt32());
        Assert.Equal(Spotify.Api.Queries.SearchTracks.Hash, persisted.GetProperty("sha256Hash").GetString());
    }

    /// <summary>The home document and the home-SECTION document share one persisted hash and are told apart by the
    /// operation name. It reads like a copy-paste mistake, which is exactly why it is written down as a fact.</summary>
    [Fact]
    public void Home_and_home_section_share_a_hash_and_differ_by_name()
    {
        Assert.Equal(Spotify.Api.Queries.Home.Hash, Spotify.Api.Queries.HomeSection.Hash);
        Assert.NotEqual(Spotify.Api.Queries.Home.Op, Spotify.Api.Queries.HomeSection.Op);
    }

    [Fact]
    public void Every_persisted_hash_is_sixty_four_lowercase_hex_characters()
    {
        foreach (Spotify.Api.Query query in AllQueries())
        {
            Assert.Equal(64, query.Hash.Length);
            foreach (char c in query.Hash)
                Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f', query.Op + " has a bad hash character");
        }
    }

    [Fact]
    public void No_two_operations_share_a_name()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Spotify.Api.Query query in AllQueries())
            Assert.True(seen.Add(query.Op), query.Op + " is declared twice");
    }

    static List<Spotify.Api.Query> AllQueries()
    {
        var all = new List<Spotify.Api.Query>();
        foreach (Spotify.Api.SearchFacet facet in Enum.GetValues<Spotify.Api.SearchFacet>())
            all.Add(Spotify.Api.QueryFor(facet));
        all.Add(Spotify.Api.Queries.Album);
        all.Add(Spotify.Api.Queries.Track);
        all.Add(Spotify.Api.Queries.ArtistOverview);
        all.Add(Spotify.Api.Queries.Home);
        all.Add(Spotify.Api.Queries.BrowseAll);
        all.Add(Spotify.Api.Queries.BrowsePage);
        all.Add(Spotify.Api.Queries.BrowseSection);
        all.Add(Spotify.Api.Queries.NpvArtist);
        all.Add(Spotify.Api.Queries.AlbumMerch);
        all.Add(Spotify.Api.Queries.SimilarAlbums);
        all.Add(Spotify.Api.Queries.WhatsNew);
        all.Add(Spotify.Api.Queries.UserTop);
        all.Add(Spotify.Api.Queries.Discography);
        all.Add(Spotify.Api.Queries.Concert);
        all.Add(Spotify.Api.Queries.ConcertFeed);
        all.Add(Spotify.Api.Queries.ConcertCount);
        all.Add(Spotify.Api.Queries.ConcertConcepts);
        return all;
    }

    /// <summary>Audiobooks is the ONE facet of the shared shape that asks for pre-releases. It is a one-word
    /// difference in a body nine operations share, and it is the kind of thing a refactor eats.</summary>
    [Fact]
    public void Audiobooks_is_the_only_shared_shape_facet_that_asks_for_pre_releases()
    {
        using JsonDocument audiobooks = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Audiobooks, "q", 0, 10));
        using JsonDocument tracks = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Tracks, "q", 0, 10));

        Assert.True(audiobooks.RootElement.GetProperty("variables").GetProperty("includePreReleases").GetBoolean());
        Assert.False(tracks.RootElement.GetProperty("variables").GetProperty("includePreReleases").GetBoolean());
    }

    [Fact]
    public void The_shared_search_shape_carries_the_term_the_offset_and_the_limit()
    {
        using JsonDocument document = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Albums, "boygenius", 40, 25));
        JsonElement variables = document.RootElement.GetProperty("variables");

        Assert.Equal("boygenius", variables.GetProperty("searchTerm").GetString());
        Assert.Equal(40, variables.GetProperty("offset").GetInt32());
        Assert.Equal(25, variables.GetProperty("limit").GetInt32());
        Assert.Equal(25, variables.GetProperty("numberOfTopResults").GetInt32());
    }

    /// <summary>Episodes is the minimal shape: four variables and no `includeAudiobooks`. Sending the shared shape
    /// here is a 400, not a wider answer.</summary>
    [Fact]
    public void The_episode_search_is_the_minimal_shape()
    {
        using JsonDocument document = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Episodes, "q", 5, 10));
        JsonElement variables = document.RootElement.GetProperty("variables");

        Assert.Equal("q", variables.GetProperty("searchTerm").GetString());
        Assert.Equal(5, variables.GetProperty("offset").GetInt32());
        Assert.True(variables.GetProperty("includeEpisodeContentRatingsV2").GetBoolean());
        Assert.False(variables.TryGetProperty("includeAudiobooks", out _));
    }

    /// <summary>The omnibar's suggestions ignore the caller's window entirely — thirty, always, from zero.</summary>
    [Fact]
    public void Suggestions_always_ask_for_thirty_from_zero()
    {
        using JsonDocument document = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Suggestions, "q", 99, 5));
        JsonElement variables = document.RootElement.GetProperty("variables");

        Assert.Equal("q", variables.GetProperty("query").GetString());
        Assert.Equal(30, variables.GetProperty("limit").GetInt32());
        Assert.Equal(30, variables.GetProperty("numberOfTopResults").GetInt32());
        Assert.Equal(0, variables.GetProperty("offset").GetInt32());
    }

    [Fact]
    public void The_top_results_search_carries_the_two_section_filters_and_a_null_prefix()
    {
        using JsonDocument document = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Top, "q", 0, 10));
        JsonElement variables = document.RootElement.GetProperty("variables");

        Assert.Equal(JsonValueKind.Null, variables.GetProperty("isPrefix").ValueKind);
        JsonElement filters = variables.GetProperty("sectionFilters");
        Assert.Equal(2, filters.GetArrayLength());
        Assert.Equal("GENERIC", filters[0].GetString());
        Assert.Equal("VIDEO_CONTENT", filters[1].GetString());
        Assert.Equal(50, variables.GetProperty("numberOfTopResults").GetInt32());
    }

    [Fact]
    public void Genres_asks_for_a_fixed_twenty_top_results()
    {
        using JsonDocument document = Body(Spotify.Api.SearchBody(Spotify.Api.SearchFacet.Genres, "q", 0, 50));
        Assert.Equal(20, document.RootElement.GetProperty("variables").GetProperty("numberOfTopResults").GetInt32());
    }
}

public class SpotifyApiRouteTests
{
    /// <summary>A revision on the wire is the big-endian counter, a comma, and the hash as lowercase hex.</summary>
    [Fact]
    public void A_revision_formats_as_counter_comma_lowercase_hex()
    {
        byte[] revision = new byte[24];
        revision[0] = 0x00; revision[1] = 0x00; revision[2] = 0x30; revision[3] = 0x39;   // 12345
        revision[4] = 0xab; revision[5] = 0xCD;

        Span<char> buffer = stackalloc char[64];
        int n = Spotify.Api.FormatRevision(revision, buffer);

        Assert.Equal("12345,abcd" + new string('0', 36), new string(buffer[..n]));
    }

    [Fact]
    public void A_revision_shorter_than_five_bytes_formats_to_nothing()
    {
        Span<char> buffer = stackalloc char[64];
        Assert.Equal(0, Spotify.Api.FormatRevision([1, 2, 3, 4], buffer));
    }

    /// <summary>The comma MUST leave as `%2C` — an un-encoded one is a 509 from the gateway, and the escape happens
    /// because the diff route puts the revision through `PathWriter.AppendEscaped`.</summary>
    [Fact]
    public void The_revision_is_percent_encoded_into_a_query_string()
    {
        Span<char> buffer = stackalloc char[64];
        var writer = new Spotify.PathWriter(buffer);
        writer.AppendEscaped("12345,abcd");

        Assert.Equal("12345%2Cabcd", new string(writer.Written));
    }

    [Fact]
    public void The_create_base_revision_is_four_zeroes_and_the_word_root()
    {
        ReadOnlySpan<byte> expected = [0, 0, 0, 0, 0x72, 0x6f, 0x6f, 0x74];
        Assert.True(Spotify.Api.CreateBaseRevision.SequenceEqual(expected));
    }

    [Fact]
    public void A_minted_item_id_is_sixteen_lowercase_hex_characters_and_never_the_same_twice()
    {
        string first = Spotify.Api.NewItemId();
        string second = Spotify.Api.NewItemId();

        Assert.Equal(16, first.Length);
        Assert.NotEqual(first, second);
        foreach (char c in first) Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>Four of the five library edges live in the service's "collection" set and are told apart by the item
    /// uri; only artists and shows have a set of their own, and pins have the `ylpin` one.</summary>
    [Theory]
    [InlineData(LibraryEdgeKind.Liked, "collection")]
    [InlineData(LibraryEdgeKind.SavedAlbums, "collection")]
    [InlineData(LibraryEdgeKind.FollowedArtists, "artist")]
    [InlineData(LibraryEdgeKind.SavedShows, "show")]
    [InlineData(LibraryEdgeKind.Pins, "ylpin")]
    public void Each_library_edge_names_its_collection_set(LibraryEdgeKind kind, string expected)
        => Assert.Equal(expected, Spotify.Api.WireSet(kind));

    [Fact]
    public void The_collection_page_size_is_the_services_own_three_hundred()
        => Assert.Equal(300, Spotify.Api.CollectionPageSize);

    /// <summary>The base url for every fixed host is a constant, and only the spclient one comes off the session.</summary>
    [Fact]
    public void The_fixed_hosts_are_constants()
    {
        Assert.Equal("https://api-partner.spotify.com", Spotify.Api.BaseUrl(Spotify.ApiHost.Pathfinder));
        Assert.Equal("https://spclient.wg.spotify.com", Spotify.Api.BaseUrl(Spotify.ApiHost.SpclientWg));
        Assert.Equal("https://login5.spotify.com", Spotify.Api.BaseUrl(Spotify.ApiHost.Login5));
        Assert.Equal("https://clienttoken.spotify.com", Spotify.Api.BaseUrl(Spotify.ApiHost.ClientToken));
        Assert.Equal("https://apresolve.spotify.com", Spotify.Api.BaseUrl(Spotify.ApiHost.ApResolve));
    }
}
