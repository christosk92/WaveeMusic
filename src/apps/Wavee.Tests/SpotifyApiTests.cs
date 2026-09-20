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
//
// Gap batch B1b adds the provider's pure half: the route walk minus what the build cannot decode (G-040/G-041), the
// edge gate and the list routes with their held-revision /diff (G-042), the outcome fold, the collection bodies
// (G-043), zstd (G-054), the conditional content-filter read and the permission route (G-053), the profile route
// (G-033), the cover grader's body (G-055) and the bearer url gate (G-008).

using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Wavee;
using Xunit;
using Col = Wavee.Protocol.Collection;
using Pl = Wavee.Protocol.Playlist;
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
        all.Add(Spotify.Api.Queries.DynamicColors);
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

// ── gap batch B1b: the provider's route walk (G-040, G-041, G-042) ─────────────────────────────────────────────────────

public class SpotifyApiProviderRouteTests
{
    static FetchRoute[] Routes(FetchSubject subject, EntityKind kind, uint need, out uint sealedGroups)
    {
        Span<FetchRoute> into = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        int n = Spotify.Api.RoutesFor(subject, kind, need, into, out sealedGroups);
        return into[..n].ToArray();
    }

    static int[] Kinds(FetchRoute[] routes, int derived = 0)
    {
        Span<int> into = stackalloc int[FetchRoutes.MaxRoutes];
        return into[..Spotify.Api.MetadataKinds(routes, derived, into)].ToArray();
    }

    /// <summary>The fix the register names first: a sidebar asking a track's Row gets ONE POST carrying both of its
    /// kinds, not one kind per uri and a second request for the play count.</summary>
    [Fact]
    public void A_track_row_is_one_post_carrying_both_of_its_kinds()
    {
        var routes = Routes(FetchSubject.Entity, EntityKind.Track, (uint)TrackFields.Row, out uint sealedGroups);

        Assert.Equal(new[] { FetchRoutes.TrackV4, FetchRoutes.PlayCount }, Kinds(routes));
        Assert.Equal(0u, sealedGroups);
    }

    [Fact]
    public void Every_cold_track_group_rides_the_same_post()
    {
        uint need = (uint)(TrackFields.Identity | TrackFields.Audio | TrackFields.Tags | TrackFields.Video);
        var routes = Routes(FetchSubject.Entity, EntityKind.Track, need, out _);

        Assert.Equal(new[] { FetchRoutes.TrackV4, FetchRoutes.AudioAttributes, FetchRoutes.TrackDescriptor,
                             FetchRoutes.VideoAssociations }, Kinds(routes));
    }

    /// <summary>Kind 5 rides the derived <c>spotify:audio:</c> entity, so a Files batch asks it and nothing else.</summary>
    [Fact]
    public void A_derived_audio_batch_asks_kind_five_alone()
    {
        var routes = Routes(FetchSubject.Entity, EntityKind.Track, (uint)TrackFields.Files, out _);
        Assert.Equal(new[] { Fetch.AudioFilesKind }, Kinds(routes, Fetch.AudioFilesKind));
    }

    /// <summary>`fetchPlaylist` has no persisted hash, so it is never sent — and skipping it BEFORE it counts as served
    /// is what lets the playlist4 read claim the daylist window with the identity (0.2.9 read both off that one GET,
    /// WP-5.O), rather than sealing the daylist.</summary>
    [Fact]
    public void A_route_that_cannot_be_decoded_does_not_swallow_the_groups_another_route_can_fill()
    {
        uint need = (uint)(PlaylistFields.Identity | PlaylistFields.Daylist);
        var routes = Routes(FetchSubject.Entity, EntityKind.Playlist, need, out uint sealedGroups);

        Assert.Contains(routes, r => r.Transport == RouteTransport.Spclient && r.Rest == SpclientRoute.PlaylistRead);
        Assert.DoesNotContain(routes, r => r.Transport == RouteTransport.Pathfinder && r.Op == PathfinderOp.FetchPlaylist);
        Assert.Equal(0u, sealedGroups);
    }

    [Fact]
    public void A_playlist_header_sends_the_v2_read_the_permission_and_the_popcount_per_subject()
    {
        uint need = (uint)(PlaylistFields.Header | PlaylistFields.Saves);
        var routes = Routes(FetchSubject.Entity, EntityKind.Playlist, need, out uint sealedGroups);

        Assert.Equal(new[] { SpclientRoute.PlaylistRead, SpclientRoute.PermissionBase, SpclientRoute.Popcount },
                     routes.Select(r => r.Rest).ToArray());
        Assert.All(routes, r => Assert.Equal(RouteTransport.Spclient, r.Transport));
        Assert.Empty(Kinds(routes));
        // The accent is the pathfinder read's alone, and that read cannot be sent in this build.
        Assert.Equal((uint)PlaylistFields.Accent, sealedGroups);
    }

    /// <summary>The overview answer carries Identity, so an Overview ask is the pathfinder query ALONE.</summary>
    [Fact]
    public void An_artist_overview_is_the_pathfinder_query_alone()
    {
        var routes = Routes(FetchSubject.Entity, EntityKind.Artist, (uint)ArtistFields.Overview, out uint sealedGroups);

        var only = Assert.Single(routes);
        Assert.Equal(PathfinderOp.ArtistOverview, only.Op);
        Assert.Equal(0u, sealedGroups);
    }

    [Fact]
    public void An_artist_chart_is_the_extended_top_track_list()
    {
        var routes = Routes(FetchSubject.Entity, EntityKind.Artist, (uint)ArtistFields.Chart, out uint sealedGroups);

        Assert.Equal(SpclientRoute.ArtistTopTracksExtended, Assert.Single(routes).Rest);
        Assert.Equal(0u, sealedGroups);
    }

    /// <summary>G-033: a user's Identity is kind 15 (the REST arm is the provider's fallback inside that route); the
    /// content filters go out on their own spclient read now that they have a staged column (WP-5.O).</summary>
    [Fact]
    public void A_user_identity_is_kind_fifteen_and_content_filters_have_their_own_read()
    {
        uint need = (uint)(UserFields.Identity | UserFields.ContentFilters);
        var routes = Routes(FetchSubject.Entity, EntityKind.User, need, out uint sealedGroups);

        Assert.Equal(new[] { FetchRoutes.UserProfile }, Kinds(routes));
        Assert.Contains(routes, r => r.Transport == RouteTransport.Spclient && r.Rest == SpclientRoute.LikedContentFilters);
        Assert.Equal(0u, sealedGroups);
    }

    [Theory]
    [InlineData(FetchSubject.Home, (uint)HomeFields.All, PathfinderOp.Home)]
    [InlineData(FetchSubject.HomeSection, (uint)SectionFields.All, PathfinderOp.HomeSection)]
    [InlineData(FetchSubject.BrowseSection, (uint)SectionFields.All, PathfinderOp.BrowseSection)]
    [InlineData(FetchSubject.BrowseDirectory, (uint)BrowseFields.All, PathfinderOp.BrowseAll)]
    [InlineData(FetchSubject.BrowsePage, (uint)BrowseFields.All, PathfinderOp.BrowsePage)]
    public void Every_synthetic_subject_routes_to_its_pathfinder_operation(FetchSubject subject, uint need, PathfinderOp op)
    {
        var routes = Routes(subject, EntityKind.Unknown, need, out uint sealedGroups);

        Assert.Equal(op, Assert.Single(routes).Op);
        Assert.Equal(0u, sealedGroups);
    }

    [Fact]
    public void A_search_sends_its_facet_query_and_its_genre_and_related_strips()
    {
        var routes = Routes(FetchSubject.Search, EntityKind.Unknown, (uint)SearchFields.All, out uint sealedGroups);

        Assert.Equal([PathfinderOp.Search, PathfinderOp.SearchGenres, PathfinderOp.SearchSuggestions], routes.Select(r => r.Op).ToArray());
        Assert.Equal(0u, sealedGroups);
    }

    [Fact]
    public void A_concert_routes_whole_to_its_pathfinder_query()
    {
        var routes = Routes(FetchSubject.Entity, EntityKind.Concert, (uint)ConcertFields.All, out uint sealedGroups);

        Assert.Equal(PathfinderOp.Concert, Assert.Single(routes).Op);
        Assert.Equal(0u, sealedGroups);
    }

    [Theory]
    [InlineData(FetchEdge.Rootlist, 0, true)]
    [InlineData(FetchEdge.Liked, 0, true)]
    [InlineData(FetchEdge.SavedAlbums, 0, true)]
    [InlineData(FetchEdge.FollowedArtists, 0, true)]
    [InlineData(FetchEdge.SavedShows, 0, true)]
    [InlineData(FetchEdge.Recents, 0, true)]
    [InlineData(FetchEdge.PlaylistTracks, 0, true)]
    [InlineData(FetchEdge.AlbumTracks, 0, true)]
    [InlineData(FetchEdge.ArtistPopular, 0, true)]
    [InlineData(FetchEdge.ArtistReleases, 40, true)]
    [InlineData(FetchEdge.TrackCredits, 0, true)]
    [InlineData(FetchEdge.HomeSections, 0, true)]
    [InlineData(FetchEdge.HomeSectionCards, 20, true)]
    [InlineData(FetchEdge.BrowseSections, 10, true)]
    [InlineData(FetchEdge.SearchResults, 0, true)]
    // pins (G-062, B2b) route through the same collection-v2 paging as every other library set
    [InlineData(FetchEdge.Pins, 0, true)]
    // the artist page's facets page at any offset; the artist schedule (WP-5.N)
    [InlineData(FetchEdge.ArtistAlbums, 0, true)]
    [InlineData(FetchEdge.ArtistSingles, 40, true)]
    [InlineData(FetchEdge.ArtistCompilations, 0, true)]
    [InlineData(FetchEdge.ArtistConcerts, 0, true)]
    // no staging relation / no fold / a fold that lands at the wrong offset: answered without a request
    [InlineData(FetchEdge.Friends, 0, false)]
    [InlineData(FetchEdge.PlaylistTracks, 100, false)]
    // the album page's relations (WP-5.M): merch and similar albums have folds; getAlbum lands more-by and a later page
    [InlineData(FetchEdge.AlbumMerch, 0, true)]
    [InlineData(FetchEdge.AlbumMoreBy, 0, true)]
    [InlineData(FetchEdge.AlbumTracks, 50, true)]
    [InlineData(FetchEdge.AlbumSimilar, 0, true)]
    [InlineData(FetchEdge.SearchResults, 30, true)]
    [InlineData(FetchEdge.None, 0, false)]
    public void The_edge_gate_answers_only_what_a_fold_can_land(FetchEdge edge, int offset, bool served)
        => Assert.Equal(served, Spotify.Api.ServesEdge(edge, offset));
}

// ── the outcome fold ───────────────────────────────────────────────────────────────────────────────────────────────────

public class SpotifyApiOutcomeTests
{
    static bool Fails(Spotify.Api.FetchOutcome outcome, int attempt, out int status, out int retryAfter)
        => outcome.Fails(attempt, out status, out retryAfter);

    [Fact]
    public void A_batch_that_sent_nothing_is_answered_empty()
        => Assert.False(Fails(new Spotify.Api.FetchOutcome(), 0, out _, out _));

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(404)]
    public void An_answer_is_never_a_failure(int status)
    {
        var outcome = new Spotify.Api.FetchOutcome();
        outcome.Note(status);
        Assert.False(Fails(outcome, 0, out _, out _));
        Assert.Equal(1, outcome.Answered);
    }

    [Fact]
    public void A_retryable_failure_fails_the_batch_and_carries_the_retry_after()
    {
        var outcome = new Spotify.Api.FetchOutcome();
        outcome.Note(200);
        outcome.Note(429, 12);

        Assert.True(Fails(outcome, 0, out int status, out int retryAfter));
        Assert.Equal(429, status);
        Assert.Equal(12, retryAfter);
    }

    /// <summary>On the LAST attempt a secondary endpoint that keeps failing must not cost the rows the primary one
    /// already delivered.</summary>
    [Fact]
    public void On_the_last_attempt_what_answered_is_committed()
    {
        var outcome = new Spotify.Api.FetchOutcome();
        outcome.Note(200);
        outcome.Note(503);

        Assert.False(Fails(outcome, Fetch.MaxAttempts - 1, out _, out _));
    }

    [Fact]
    public void A_last_attempt_with_no_answer_still_fails()
    {
        var outcome = new Spotify.Api.FetchOutcome();
        outcome.Note(0);

        Assert.True(Fails(outcome, Fetch.MaxAttempts - 1, out int status, out _));
        Assert.Equal(0, status);
    }

    [Fact]
    public void A_terminal_failure_fails_only_when_nothing_answered()
    {
        var alone = new Spotify.Api.FetchOutcome();
        alone.Note(400);
        Assert.True(Fails(alone, 0, out int status, out _));
        Assert.Equal(400, status);

        var beside = new Spotify.Api.FetchOutcome();
        beside.Note(400);
        beside.Note(200);
        Assert.False(Fails(beside, 0, out _, out _));
    }

    /// <summary>The 2026-09-16 chart defect: Pathfinder ArtistOverview 200 beside Spclient ArtistTopTracksExtended 401.
    /// The batch is delivered as an ANSWER (what landed is committed), and the outcome names the groups the refused
    /// route would have filled so the planner un-asks exactly those — not the overview's, not the 404'd, not the
    /// answered.</summary>
    [Fact]
    public void A_route_that_failed_beside_one_that_answered_reports_its_groups_unfilled()
    {
        var outcome = new Spotify.Api.FetchOutcome();
        outcome.Note(200, 0, (uint)ArtistFields.Overview);         // the overview landed
        outcome.Note(401, 0, (uint)ArtistFields.Chart);            // the top-tracks REST was refused

        Assert.False(Fails(outcome, 0, out _, out _));             // an answer, not a failure…
        Assert.Equal((uint)ArtistFields.Chart, outcome.Unfilled);  // …that names what it could not fill

        var answered = new Spotify.Api.FetchOutcome();
        answered.Note(200, 0, (uint)ArtistFields.Chart);
        answered.Note(404, 0, (uint)ArtistFields.Identity);        // "nothing there" IS an answer: sealed, never unfilled
        Assert.Equal(0u, answered.Unfilled);

        var retryable = new Spotify.Api.FetchOutcome();
        retryable.Note(200, 0, (uint)ArtistFields.Overview);
        retryable.Note(503, 0, (uint)ArtistFields.Chart);
        Assert.False(Fails(retryable, Fetch.MaxAttempts - 1, out _, out _));   // the last attempt commits what answered…
        Assert.Equal((uint)ArtistFields.Chart, retryable.Unfilled);           // …and still un-seals what did not

        var ungrouped = new Spotify.Api.FetchOutcome();
        ungrouped.Note(401);                                       // a caller without groups adds nothing
        Assert.Equal(0u, ungrouped.Unfilled);
    }

    [Fact]
    public void A_fault_fails_whatever_answered()
    {
        var outcome = new Spotify.Api.FetchOutcome();
        outcome.Note(200);
        outcome.Fault();
        Assert.True(Fails(outcome, Fetch.MaxAttempts - 1, out _, out _));
    }
}

// ── the bodies (G-040, G-043, G-033, G-055) ────────────────────────────────────────────────────────────────────────────

public class SpotifyApiBodyTests
{
    [Fact]
    public void The_mixed_kind_post_gives_every_uri_every_kind_under_one_entity_request()
    {
        string[] uris = ["spotify:track:a", "", "spotify:track:b", "spotify:track:a"];
        int[] kinds = [FetchRoutes.TrackV4, FetchRoutes.PlayCount, 0];

        var request = Xm.BatchedEntityRequest.Parser.ParseFrom(Spotify.Api.BatchBody(uris, kinds, "SE", "premium"));

        Assert.Equal(2, request.EntityRequest.Count);                    // the empty uri skipped, the repeat folded
        foreach (var entity in request.EntityRequest)
        {
            Assert.Equal(2, entity.Query.Count);                          // kind 0 is never asked
            Assert.Equal(Xm.ExtensionKind.TrackV4, entity.Query[0].ExtensionKind);
            Assert.Equal(Xm.ExtensionKind.OnPlatformReputationTrait, entity.Query[1].ExtensionKind);
        }
        Assert.Equal("SE", request.Header.Country);
    }

    [Fact]
    public void A_mixed_kind_post_with_nothing_askable_is_empty()
    {
        string[] uris = ["", ""];
        int[] kinds = [FetchRoutes.TrackV4];
        Assert.Empty(Spotify.Api.BatchBody(uris, kinds, "", ""));
        Assert.Empty(Spotify.Api.BatchBody(["spotify:track:a"], ReadOnlySpan<int>.Empty, "", ""));
    }

    /// <summary>G-033: only a 2xx entity WITH extension data is answered by kind 15; the REST arm takes the rest.</summary>
    [Fact]
    public void Answered_uris_are_the_two_hundreds_that_carried_data_for_that_kind()
    {
        var response = new Xm.BatchedExtensionResponse();
        var profiles = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.UserProfile };
        profiles.ExtensionData.Add(Entity("spotify:user:ann", 200, data: true));
        profiles.ExtensionData.Add(Entity("spotify:user:bob", 404, data: true));
        profiles.ExtensionData.Add(Entity("spotify:user:cat", 200, data: false));
        var tracks = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
        tracks.ExtensionData.Add(Entity("spotify:user:dan", 200, data: true));
        response.ExtendedMetadata.Add(profiles);
        response.ExtendedMetadata.Add(tracks);

        var answered = Spotify.Api.AnsweredUris(response.ToByteArray(), FetchRoutes.UserProfile);

        Assert.Equal(new[] { "spotify:user:ann" }, answered.ToArray());

        static Xm.EntityExtensionData Entity(string uri, int status, bool data)
        {
            var entity = new Xm.EntityExtensionData
            {
                EntityUri = uri,
                Header = new Xm.EntityExtensionDataHeader { StatusCode = status },
            };
            if (data) entity.ExtensionData = new Google.Protobuf.WellKnownTypes.Any { Value = ByteString.CopyFromUtf8("x") };
            return entity;
        }
    }

    /// <summary>The captured discography variables — `order: DATE_DESC`, no locale — and a floored offset.</summary>
    [Fact]
    public void The_discography_body_is_the_captured_paged_shape()
    {
        using JsonDocument document = JsonDocument.Parse(Spotify.Api.DiscographyBody("spotify:artist:x", -5, 20));
        JsonElement variables = document.RootElement.GetProperty("variables");

        Assert.Equal("spotify:artist:x", variables.GetProperty("uri").GetString());
        Assert.Equal(0, variables.GetProperty("offset").GetInt32());
        Assert.Equal(20, variables.GetProperty("limit").GetInt32());
        Assert.Equal("DATE_DESC", variables.GetProperty("order").GetString());
        Assert.False(variables.TryGetProperty("locale", out _));
        Assert.Equal("queryArtistDiscographyAll", document.RootElement.GetProperty("operationName").GetString());
    }

    /// <summary>G-055: the grader answers POSITIONALLY, so the body keeps the uris in the order they were given.</summary>
    [Fact]
    public void The_cover_grader_body_keeps_the_image_uris_in_order()
    {
        string[] uris = ["spotify:image:bb", "spotify:image:aa"];
        using JsonDocument document = JsonDocument.Parse(Spotify.Api.DynamicColorsBody(uris));

        JsonElement images = document.RootElement.GetProperty("variables").GetProperty("imageUris");
        Assert.Equal(2, images.GetArrayLength());
        Assert.Equal("spotify:image:bb", images[0].GetString());
        Assert.Equal("spotify:image:aa", images[1].GetString());
        Assert.Equal(Spotify.Api.Queries.DynamicColors.Op, document.RootElement.GetProperty("operationName").GetString());
    }

    /// <summary>G-043: a save is `is_removed = false` with the time in SECONDS; a remove flips the flag; the pins set is
    /// `ylpin`; an empty uri is never written.</summary>
    [Fact]
    public void A_collection_write_names_the_set_the_items_the_seconds_and_the_update_id()
    {
        string[] uris = ["spotify:track:a", "", "spotify:album:b"];
        var saved = Col.WriteRequest.Parser.ParseFrom(
            Spotify.Api.CollectionWriteBody("bob", LibraryEdgeKind.Liked, uris, saved: true, 1_700_000_000, "cid"));

        Assert.Equal("bob", saved.Username);
        Assert.Equal("collection", saved.Set);
        Assert.Equal("cid", saved.ClientUpdateId);
        Assert.Equal(2, saved.Items.Count);
        Assert.All(saved.Items, i => Assert.False(i.IsRemoved));
        Assert.Equal(1_700_000_000, saved.Items[0].AddedAt);

        var removed = Col.WriteRequest.Parser.ParseFrom(
            Spotify.Api.CollectionWriteBody("bob", LibraryEdgeKind.Pins, ["spotify:playlist:p"], saved: false, 1, "c2"));
        Assert.Equal("ylpin", removed.Set);
        Assert.True(Assert.Single(removed.Items).IsRemoved);

        Assert.Empty(Spotify.Api.CollectionWriteBody("bob", LibraryEdgeKind.Liked, [""], saved: true, 1, "c3"));
    }

    [Fact]
    public void A_collection_page_request_carries_the_cursor_and_the_three_hundred_limit()
    {
        var request = Col.PageRequest.Parser.ParseFrom(
            Spotify.Api.CollectionPageBody("bob", "artist", "tok", Spotify.Api.CollectionPageSize));

        Assert.Equal("bob", request.Username);
        Assert.Equal("artist", request.Set);
        Assert.Equal("tok", request.PaginationToken);
        Assert.Equal(300, request.Limit);
    }

    [Fact]
    public void The_next_page_token_is_read_off_the_page_and_empty_on_the_last()
    {
        var page = new Col.PageResponse { NextPageToken = "next-7" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:track:a", AddedAt = 5 });
        Assert.Equal("next-7", Spotify.Api.NextPageToken(page.ToByteArray()));

        var last = new Col.PageResponse();
        last.Items.Add(new Col.CollectionItem { Uri = "spotify:track:a" });
        Assert.Equal("", Spotify.Api.NextPageToken(last.ToByteArray()));
    }
}

// ── the list routes, the diff verdict, zstd (G-042, G-053, G-054, G-033) ──────────────────────────────────────────────

public class SpotifyApiListRouteTests
{
    const Spotify.HeaderSet Common = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken
                                   | Spotify.HeaderSet.Identity | Spotify.HeaderSet.AcceptLanguage;

    [Fact]
    public void A_playlist_read_is_the_decorated_v2_get_and_may_answer_zstd()
    {
        var route = Spotify.Api.ListRoute(Spotify.Api.ListKind.Playlist, "37i9dQZF1DX");

        Assert.Equal(Spotify.Verb.Get, route.Verb);
        Assert.Equal("/playlist/v2/playlist/37i9dQZF1DX?decorate=revision,attributes,length,owner,capabilities,picture", route.Path);
        Assert.Equal(Common | Spotify.HeaderSet.AcceptProtobuf | Spotify.HeaderSet.ApplyLenses
            | Spotify.HeaderSet.AcceptListItems | Spotify.HeaderSet.AcceptGeoblock | Spotify.HeaderSet.DsaMode, route.Headers);
        Assert.Equal("CAwQAQ==", route.SyncReason);
        Assert.True(route.Zstd);
    }

    [Fact]
    public void The_rootlist_read_escapes_the_username()
    {
        var route = Spotify.Api.ListRoute(Spotify.Api.ListKind.Rootlist, "a b");
        Assert.StartsWith("/playlist/v2/user/a%20b/rootlist?decorate=revision", route.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recents_page_carries_its_sync_reason_and_lenses_but_not_the_applied_one()
    {
        var route = Spotify.Api.ListRoute(Spotify.Api.ListKind.Recents, "");

        Assert.Equal("/playlist/v2/list/recents/page", route.Path);
        Assert.Equal("CAwQAQ==", route.SyncReason);
        Assert.NotEqual(0u, (uint)(route.Headers & Spotify.HeaderSet.ApplyLenses));
        Assert.NotEqual(0u, (uint)(route.Headers & Spotify.HeaderSet.AcceptListItems));
        Assert.Equal(0u, (uint)(route.Headers & Spotify.HeaderSet.AppliedLenses));
    }

    /// <summary>The held revision rides the query TWICE, escaped — an unescaped comma is a 509.</summary>
    [Fact]
    public void A_diff_read_sends_the_held_revision_twice_escaped_with_handles_content()
    {
        var route = Spotify.Api.ListDiffRoute(Spotify.Api.ListKind.Rootlist, "bob", "45,236c58");

        Assert.Equal("/playlist/v2/user/bob/rootlist/diff?revision=45%2C236c58&handlesContent=&hint_revision=45%2C236c58",
                     route.Path);
        Assert.True(route.Zstd);
    }

    [Fact]
    public void The_recents_diff_adds_the_applied_lenses_and_its_own_sync_reason()
    {
        var route = Spotify.Api.ListDiffRoute(Spotify.Api.ListKind.Recents, "", "7,52");

        Assert.StartsWith("/playlist/v2/list/recents/page/diff?revision=7%2C52", route.Path, StringComparison.Ordinal);
        Assert.Equal("CAEQAQ==", route.SyncReason);
        Assert.NotEqual(0u, (uint)(route.Headers & Spotify.HeaderSet.AppliedLenses));
    }

    static byte[] Revision24(int counter)
    {
        var revision = new byte[24];
        revision[3] = (byte)counter;
        for (int i = 4; i < 24; i++) revision[i] = (byte)i;
        return revision;
    }

    [Fact]
    public void A_list_answers_its_own_revision_in_the_wire_spelling()
    {
        var answer = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Revision24(45)) };
        Span<char> into = stackalloc char[128];
        int n = Spotify.Api.RevisionOf(answer.ToByteArray(), into);

        Assert.StartsWith("45,0405060708", new string(into[..n]), StringComparison.Ordinal);
        Assert.Equal(0, Spotify.Api.RevisionOf([], into));
    }

    [Fact]
    public void A_zstd_frame_unwraps_one_shot_and_streamed_and_anything_else_is_itself()
    {
        byte[] raw = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("selected list content ", 64)));

        using (var compressor = new ZstdSharp.Compressor(3))
        {
            byte[] framed = compressor.Wrap(raw).ToArray();
            Assert.True(Spotify.Api.IsZstd(framed));
            Assert.Equal(raw, Spotify.Api.Unzstd(framed));
        }

        using var sink = new MemoryStream();
        using (var stream = new ZstdSharp.CompressionStream(sink)) stream.Write(raw);
        byte[] streamed = sink.ToArray();
        Assert.True(Spotify.Api.IsZstd(streamed));
        Assert.Equal(raw, Spotify.Api.Unzstd(streamed));

        Assert.False(Spotify.Api.IsZstd(raw));
        Assert.Same(raw, Spotify.Api.Unzstd(raw));
    }

    [Fact]
    public void A_zstd_magic_over_garbage_is_unreadable_rather_than_empty()
        => Assert.Null(Spotify.Api.Unzstd([0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x02, 0x03, 0x04, 0x05]));

    [Fact]
    public void The_popcount_and_permission_routes_want_protobuf()
    {
        var popcount = Spotify.Api.PopcountRoute("abc");
        Assert.Equal("/popcount/v2/playlist/abc/count", popcount.Path);
        Assert.Equal(Common | Spotify.HeaderSet.AcceptProtobuf, popcount.Headers);

        var permission = Spotify.Api.PermissionBaseRoute("abc");
        Assert.Equal("/playlist-permission/v1/playlist/abc/permission/base", permission.Path);
        Assert.Equal(Spotify.Verb.Get, permission.Verb);
        Assert.Equal(Common | Spotify.HeaderSet.AcceptProtobuf, permission.Headers);
    }

    [Fact]
    public void The_profile_route_carries_the_captured_market_and_escapes_the_username()
    {
        var route = Spotify.Api.ProfileRoute("a@b");
        Assert.Equal("/user-profile-view/v3/profile/a%40b?market=from_token", route.Path);
        Assert.Equal(Common | Spotify.HeaderSet.AcceptJson, route.Headers);
    }

    [Fact]
    public void The_content_filter_read_is_a_json_get()
    {
        var route = Spotify.Api.LikedContentFiltersRoute;
        Assert.Equal("/content-filter/v1/liked-songs?subjective=true&market=from_token", route.Path);
        Assert.Equal(Common | Spotify.HeaderSet.AcceptJson, route.Headers);
    }
}

// ── the subjects, the zone, the bearer gate (G-041, G-008) ─────────────────────────────────────────────────────────────

public class SpotifyApiSubjectTests
{
    [Theory]
    [InlineData("wavee:search:00:daft punk", Spotify.Api.SearchFacet.Top, "daft punk")]
    [InlineData("wavee:search:01:x", Spotify.Api.SearchFacet.Tracks, "x")]
    [InlineData("wavee:search:06:x", Spotify.Api.SearchFacet.Artists, "x")]
    [InlineData("wavee:search:08:x", Spotify.Api.SearchFacet.Users, "x")]
    [InlineData("wavee:search:10:x", Spotify.Api.SearchFacet.Authors, "x")]
    [InlineData("wavee:search:03:a:b", Spotify.Api.SearchFacet.Playlists, "a:b")]
    public void A_search_subject_names_its_operation_facet_and_its_query(string uri, Spotify.Api.SearchFacet facet, string query)
    {
        Assert.True(Spotify.Api.TryParseSearchSubject(uri, out var parsedFacet, out string parsedQuery));
        Assert.Equal(facet, parsedFacet);
        Assert.Equal(query, parsedQuery);
    }

    [Theory]
    [InlineData("wavee:search:99:x")]
    [InlineData("wavee:search:0x:x")]
    [InlineData("wavee:search:01")]
    [InlineData("spotify:track:0RZyUsKfiC7MtiGKatCtGc")]
    public void Anything_else_is_not_a_search_subject(string uri)
        => Assert.False(Spotify.Api.TryParseSearchSubject(uri, out _, out _));

    /// <summary>The page's facets and the operation families order differently; every page facet maps to its own
    /// operation, and none of them to the suggestions strip.</summary>
    [Fact]
    public void Every_page_facet_maps_to_a_distinct_operation()
    {
        var mapped = Enum.GetValues<Wavee.SearchFacet>().Select(Spotify.Api.FacetOf).ToArray();
        Assert.Equal(mapped.Length, mapped.Distinct().Count());
        Assert.DoesNotContain(Spotify.Api.SearchFacet.Suggestions, mapped);
    }

    [Theory]
    [InlineData("wavee:home", "")]
    [InlineData("wavee:home:podcasts", "podcasts")]
    [InlineData("wavee:browse", null)]
    public void A_home_subject_names_its_facet(string uri, string? facet)
        => Assert.Equal(facet, Spotify.Api.HomeFacetOf(uri));

    [Theory]
    [InlineData("spotify:user:bob", "bob")]
    [InlineData("spotify:user:a%40b", "a@b")]
    [InlineData("spotify:user:bob:playlist:x", "bob")]
    public void A_user_uri_names_its_username(string uri, string username)
        => Assert.Equal(username, Spotify.Api.UsernameOf(uri));

    [Theory]
    [InlineData("Europe/Amsterdam", "Europe/Amsterdam")]
    [InlineData("", Spotify.Api.FallbackTimeZone)]
    [InlineData("Not A Zone", Spotify.Api.FallbackTimeZone)]
    public void The_home_zone_is_an_iana_id_or_the_explicit_fallback(string localId, string expected)
        => Assert.Equal(expected, Spotify.Api.IanaZone(localId));

    [Fact]
    public void The_local_zone_is_never_a_windows_id()
        => Assert.Contains("/", Spotify.Api.LocalTimeZone, StringComparison.Ordinal);

    [Theory]
    [InlineData("https://gew4-spclient.spotify.com/color-lyrics/v2/track/x", true)]
    [InlineData("https://spotify.com/x", true)]
    [InlineData("http://gew4-spclient.spotify.com/x", false)]
    [InlineData("https://spotify.com.example.net/x", false)]
    [InlineData("https://evilspotify.com/x", false)]
    [InlineData("not a url", false)]
    public void A_bearer_goes_only_to_https_spotify_hosts(string url, bool allowed)
        => Assert.Equal(allowed, Spotify.Api.IsSpotifyUrl(url));
}
