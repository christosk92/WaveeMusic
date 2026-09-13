// ── Wavee.Tests/FetchRoutesTests.cs — the routing input contract (gap batch B1: G-040, G-041, G-042) ──────────────
//
// `FetchRoutes` is the table `Spotify.Api`'s provider codes against: (subject, kind, need) → the extension kinds for ONE
// mixed-kind POST, the pathfinder operations and spclient routes to dispatch per subject, and the groups no route fills
// (sealed). Pure and static, so every fact here is a table read — no provider, no scope.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class FetchRoutesTests
{
    static FetchRoute[] Routes(FetchSubject subject, EntityKind kind, uint need, out uint sealedGroups)
    {
        Span<FetchRoute> into = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        int n = FetchRoutes.For(subject, kind, need, into, out sealedGroups);
        return into[..n].ToArray();
    }

    static int[] Kinds(EntityKind kind, uint need)
    {
        Span<int> into = stackalloc int[FetchRoutes.MaxRoutes];
        return into[..FetchRoutes.ExtensionKinds(FetchSubject.Entity, kind, need, into)].ToArray();
    }

    [Fact]
    public void A_track_row_rides_one_post_with_both_of_its_kinds()
    {
        // TrackFields.Row = Identity | PlayCount | Availability: TrackV4 fills two of the three, kind 185 the third —
        // and both kinds ride ONE BatchedEntityRequest per uri, because the envelope is keyed (kind, uri).
        var routes = Routes(FetchSubject.Entity, EntityKind.Track, (uint)TrackFields.Row, out uint sealedGroups);

        Assert.Equal(2, routes.Length);
        Assert.All(routes, r => Assert.Equal(RouteTransport.Metadata, r.Transport));
        Assert.Equal(0u, sealedGroups);
        Assert.Equal(new[] { FetchRoutes.TrackV4, FetchRoutes.PlayCount }, Kinds(EntityKind.Track, (uint)TrackFields.Row));
    }

    [Fact]
    public void Every_cold_track_group_has_its_own_kind()
    {
        Assert.Equal(new[] { FetchRoutes.AudioAttributes }, Kinds(EntityKind.Track, (uint)TrackFields.Audio));
        Assert.Equal(new[] { FetchRoutes.TrackDescriptor }, Kinds(EntityKind.Track, (uint)TrackFields.Tags));
        Assert.Equal(new[] { FetchRoutes.VideoAssociations }, Kinds(EntityKind.Track, (uint)TrackFields.Video));
        Assert.Equal(new[] { Fetch.AudioFilesKind }, Kinds(EntityKind.Track, (uint)TrackFields.Files));
        // Isrc and Canonical are TrackV4's; asking both with Identity is still one kind.
        Assert.Equal(new[] { FetchRoutes.TrackV4 },
                     Kinds(EntityKind.Track, (uint)(TrackFields.Identity | TrackFields.Isrc | TrackFields.Canonical)));
    }

    [Fact]
    public void A_group_no_transport_fills_is_sealed_and_sends_nothing()
    {
        // Kind 183 is an ALBUM trait; the track payload has nowhere to put it. An answer, not a failure: the provider
        // answers the batch without it and the planner's asked bit keeps the row from asking again.
        var routes = Routes(FetchSubject.Entity, EntityKind.Track, (uint)TrackFields.Publishing, out uint sealedGroups);
        Assert.Empty(routes);
        Assert.Equal((uint)TrackFields.Publishing, sealedGroups);

        Routes(FetchSubject.Entity, EntityKind.Episode, (uint)(EpisodeFields.Identity | EpisodeFields.Progress), out sealedGroups);
        Assert.Equal((uint)EpisodeFields.Progress, sealedGroups);
    }

    [Fact]
    public void An_artist_overview_is_the_pathfinder_alone_and_identity_alone_is_the_cheap_kind()
    {
        // The overview answer carries identity too: a kind-8 POST beside it would be a second round trip for nothing.
        var overview = Routes(FetchSubject.Entity, EntityKind.Artist, (uint)ArtistFields.Overview, out uint sealedGroups);
        var only = Assert.Single(overview);
        Assert.Equal(RouteTransport.Pathfinder, only.Transport);
        Assert.Equal(PathfinderOp.ArtistOverview, only.Op);
        Assert.Equal(0u, sealedGroups);

        var identity = Assert.Single(Routes(FetchSubject.Entity, EntityKind.Artist, (uint)ArtistFields.Identity, out _));
        Assert.Equal(FetchRoutes.ArtistV4, identity.Extension);

        var chart = Routes(FetchSubject.Entity, EntityKind.Artist, (uint)(ArtistFields.Identity | ArtistFields.Chart), out _);
        Assert.Equal(2, chart.Length);
        Assert.Equal(SpclientRoute.ArtistTopTracksExtended, chart[1].Rest);
    }

    [Fact]
    public void A_playlist_row_is_the_batchable_kind_and_the_header_adds_the_reads_only_it_needs()
    {
        // A sidebar of 400 playlists must be one kind-205 POST per 300, never 400 per-subject reads.
        var row = Routes(FetchSubject.Entity, EntityKind.Playlist, (uint)PlaylistFields.Identity, out _);
        Assert.Equal(FetchRoutes.ListMetadataV2, Assert.Single(row).Extension);

        var header = Routes(FetchSubject.Entity, EntityKind.Playlist,
            (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.Visibility), out uint sealedGroups);
        Assert.Equal(2, header.Length);
        Assert.Equal(SpclientRoute.PlaylistRead, header[0].Rest);       // it carries identity: no kind 205 beside it
        Assert.Equal(SpclientRoute.PermissionBase, header[1].Rest);
        Assert.Equal(0u, sealedGroups);

        var saves = Assert.Single(Routes(FetchSubject.Entity, EntityKind.Playlist, (uint)PlaylistFields.Saves, out _));
        Assert.Equal(SpclientRoute.Popcount, saves.Rest);
    }

    [Fact]
    public void The_synthetic_subjects_have_routes_now()
    {
        // G-041: before the subject existed, home, search and browse had no route at all.
        Assert.Equal(PathfinderOp.Home, Assert.Single(Routes(FetchSubject.Home, EntityKind.Unknown, (uint)HomeFields.All, out _)).Op);
        Assert.Equal(PathfinderOp.HomeSection,
                     Assert.Single(Routes(FetchSubject.HomeSection, EntityKind.Unknown, (uint)SectionFields.All, out _)).Op);
        Assert.Equal(PathfinderOp.BrowseSection,
                     Assert.Single(Routes(FetchSubject.BrowseSection, EntityKind.Unknown, (uint)SectionFields.All, out _)).Op);
        Assert.Equal(PathfinderOp.BrowseAll,
                     Assert.Single(Routes(FetchSubject.BrowseDirectory, EntityKind.Unknown, (uint)BrowseFields.All, out _)).Op);
        Assert.Equal(PathfinderOp.BrowsePage,
                     Assert.Single(Routes(FetchSubject.BrowsePage, EntityKind.Unknown, (uint)BrowseFields.Page, out _)).Op);

        var search = Routes(FetchSubject.Search, EntityKind.Unknown,
                            (uint)(SearchFields.Chips | SearchFields.Results | SearchFields.Genres), out _);
        Assert.Equal(new[] { PathfinderOp.Search, PathfinderOp.SearchGenres }, search.Select(r => r.Op).ToArray());
        Assert.Empty(Kinds(EntityKind.Unknown, (uint)HomeFields.All));   // a pathfinder route is never a metadata kind
    }

    [Theory]
    [InlineData(FetchEdge.Rootlist, RouteTransport.Spclient)]
    [InlineData(FetchEdge.Liked, RouteTransport.Spclient)]
    [InlineData(FetchEdge.Pins, RouteTransport.Spclient)]
    [InlineData(FetchEdge.Recents, RouteTransport.Spclient)]
    [InlineData(FetchEdge.PlaylistTracks, RouteTransport.Spclient)]
    [InlineData(FetchEdge.ArtistReleases, RouteTransport.Pathfinder)]
    [InlineData(FetchEdge.TrackCredits, RouteTransport.Metadata)]
    [InlineData(FetchEdge.TrackWaveform, RouteTransport.Metadata)]
    [InlineData(FetchEdge.AlbumRecommendations, RouteTransport.Metadata)]
    [InlineData(FetchEdge.BrowseSections, RouteTransport.Pathfinder)]
    [InlineData(FetchEdge.HomeSectionCards, RouteTransport.Pathfinder)]
    public void Every_relation_the_door_accepts_has_a_route(FetchEdge edge, RouteTransport transport)
        => Assert.Equal(transport, FetchRoutes.ForEdge(edge).Transport);

    [Fact]
    public void An_albums_first_tracklist_is_one_kind_and_a_later_page_is_the_pathfinder()
    {
        Assert.Equal(FetchRoutes.AlbumV4, FetchRoutes.ForEdge(FetchEdge.AlbumTracks, 0).Extension);
        Assert.Equal(PathfinderOp.GetAlbum, FetchRoutes.ForEdge(FetchEdge.AlbumTracks, 50).Op);
        Assert.Equal(RouteTransport.None, FetchRoutes.ForEdge(FetchEdge.None).Transport);
    }

    [Fact]
    public void A_library_relation_names_its_collection_set_and_nothing_else_does()
    {
        Assert.True(FetchRoutes.LibrarySetOf(FetchEdge.FollowedArtists, out var set));
        Assert.Equal(LibraryEdgeKind.FollowedArtists, set);
        Assert.True(FetchRoutes.LibrarySetOf(FetchEdge.Pins, out set));
        Assert.Equal(LibraryEdgeKind.Pins, set);
        Assert.False(FetchRoutes.LibrarySetOf(FetchEdge.Rootlist, out _));
    }

    [Fact]
    public void A_batch_resolves_its_routes_from_its_own_fields()
    {
        var rows = new FetchBatch { Subject = FetchSubject.Entity, Kind = EntityKind.Show, Wanted = (uint)ShowFields.All };
        Span<FetchRoute> into = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        Assert.Equal(1, FetchRoutes.For(rows, into, out _));
        Assert.Equal(FetchRoutes.ShowV4, into[0].Extension);

        var edge = new FetchBatch { Subject = FetchSubject.Edge, Edge = FetchEdge.Rootlist };
        Assert.Equal(1, FetchRoutes.For(edge, into, out uint sealedGroups));
        Assert.Equal(SpclientRoute.Rootlist, into[0].Rest);
        Assert.Equal(0u, sealedGroups);
    }
}
