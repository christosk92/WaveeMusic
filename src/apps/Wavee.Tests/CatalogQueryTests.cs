using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogQueryTests
{
    const string Playlist = "spotify:playlist:daylist";
    const string Track = "spotify:track:one";
    static async Task PlaylistSeed(CatalogQueryTestHost host, params PlaylistMember[] rows)
        => await host.Data.Replicas.AdoptPlaylistAsync(new(Playlist, PlaylistReadKind.Snapshot, null, new byte[24],
            [..rows], [], new("daylist", Playlist, "Old edition", null, "owner", null, rows.Length)));

    [Fact]
    public async Task AcquireAndReadArePassive_EvenWhenMetadataIsMissing()
    {
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        using var first = host.Data.Queries.Acquire(new AlbumDetailQuery(host.Scope, "spotify:album:unknown"));
        using var second = host.Data.Queries.Acquire(new AlbumDetailQuery(host.Scope, "spotify:album:unknown"));
        await QueryPublication.Initial(first);
        await host.Data.Commits.FlushAsync();
        Assert.False(first.Current.Status.HasPrimaryData);
        Assert.Same(first.Current, second.Current);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LikedRowsRetainMissingMetadataAndSortByMembershipTimestamp()
    {
        await using var host = new CatalogQueryTestHost();
        await host.SeedAsync(new Wavee.Core.Track("one", Track, "Song", [], new("", "", ""), 1000, false, null));
        await host.Data.Replicas.AdoptCollectionAsync(new("collection", true, true, 0, null, "r1",
            [new("spotify:track:cold", false, 2000), new(Track, false, 3000), new("spotify:track:colder", false, 0)]));
        using var handle = host.Data.Queries.Acquire(new LikedSongsQuery(host.Scope));
        await QueryPublication.Initial(handle);
        var tracks = handle.Current.Value;
        Assert.Equal(new[] { Track, "spotify:track:cold", "spotify:track:colder" }, tracks.Select(track => track.Uri));
        Assert.Equal("Song", tracks[0].Title); Assert.Equal("", tracks[1].Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2000), tracks[1].AddedAt); Assert.Null(tracks[2].AddedAt);
        using var library = host.Data.Queries.Acquire(new SidebarLibraryQuery(host.Scope));
        await QueryPublication.Initial(library);
        Assert.Equal(tracks.Count, library.Current.Value.Stats.LikedSongs);
    }

    [Fact]
    public async Task KnownEmptyPlaylistAndMissingMembershipAreDifferentStates()
    {
        await using var host = new CatalogQueryTestHost();
        using var handle = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, Playlist));
        Assert.False(handle.Current.Value.MembershipLoaded); Assert.False(handle.Current.Status.HasPrimaryData);
        await PlaylistSeed(host);
        await QueryPublication.Until(() => handle.Current.Value.MembershipLoaded);
        Assert.True(handle.Current.Value.MembershipLoaded); Assert.True(handle.Current.Status.HasPrimaryData);
        Assert.Empty(handle.Current.Value.Tracks!);
    }

    [Fact]
    public async Task PlaylistMembershipPublishesReadyWhileUnresolvedIdentitiesStayLoadingRows()
    {
        await using var host = new CatalogQueryTestHost();
        await host.SeedAsync(new Wavee.Core.Track("one", Track, "Song", [], new("", "", ""), 1000, false, null));
        await PlaylistSeed(host, new("first", Track, "alice", 1000), new("second", "spotify:track:missing", "bob", 2000));
        using var handle = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, Playlist));
        handle.SetDemand(QueryDemand.Initial);
        await QueryPublication.Until(() => handle.Current.Value.MembershipLoaded && handle.Current.Demanded.Count > 0);
        Assert.True(handle.Current.Status.HasPrimaryData);
        Assert.Equal(2, handle.Current.Value.Tracks!.Count);
        Assert.Equal("Song", handle.Current.Value.Tracks[0].Title);
        Assert.Equal("", handle.Current.Value.Tracks[1].Title);
        Assert.True(DetailPageReadiness.InitialLoadComplete(handle.Current));
    }

    [Fact]
    public async Task DuplicateTrackOccurrencesRetainIndependentContextWithoutContaminatingIdentity()
    {
        await using var host = new CatalogQueryTestHost();
        await host.SeedAsync(new Wavee.Core.Track("one", Track, "Song", [], new("", "", ""), 1000, false, null));
        await PlaylistSeed(host, new("first", Track, "alice", 1000), new("second", Track, "bob", 2000));
        using var detail = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, Playlist));
        await QueryPublication.Initial(detail);
        Assert.Equal(new[] { "first", "second" }, detail.Current.Value.Tracks!.Select(row => row.ContextUid));
        Assert.Equal(new[] { "alice", "bob" }, detail.Current.Value.Tracks!.Select(row => row.AddedBy));
        using var identity = host.Data.Queries.Acquire(new EntityCardQuery(host.Scope, Track));
        await QueryPublication.Initial(identity);
        Assert.Null(identity.Current.Value.Playable!.AddedBy); Assert.Null(identity.Current.Value.Playable.ContextUid);
    }

    [Fact]
    public async Task OneDaylistHeaderUpdateReachesHomeSidebarAndDetailWithoutChangingOrder()
    {
        await using var host = new CatalogQueryTestHost();
        await PlaylistSeed(host, new PlaylistMember("row", Track, null, 0));
        await host.Data.Replicas.AdoptRootlistAsync(new([new(0, 0, Playlist, null, 0)], new byte[24]));
        var document = new CatalogDocumentValue(FacetKind.Home, [])
        { HomeGroups = [new("group", HomeGroupKind.Hero, "Made for you", [new("card", Playlist) { HomeKind = HomeCardKind.Playlist }])] };
        await host.AcceptAsync(new(host.Scope, CatalogSubjects.Home, FacetKind.Home, new(Filter: "")), document);
        using var home = host.Data.Queries.Acquire(new HomeQuery(host.Scope));
        using var sidebar = host.Data.Queries.Acquire(new SidebarLibraryQuery(host.Scope));
        using var detail = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, Playlist));
        await QueryPublication.Initial(home);
        await QueryPublication.Initial(sidebar);
        await QueryPublication.Initial(detail);
        long homeOrder = home.Current.OrderRevision, detailOrder = detail.Current.OrderRevision;
        await host.AcceptAsync(new(host.Scope, Playlist, FacetKind.PlaylistHeader),
            new PlaylistHeaderValue(Name: "Sunday evening", Cover: new("https://img/new")) { Format = "daylist" });
        await QueryPublication.Until(() => home.Current.Value.Groups[0].Cards[0].Title == "Sunday evening"
            && sidebar.Current.Value.Playlists[0].Name == "Sunday evening" && detail.Current.Value.Name == "Sunday evening");
        Assert.Equal("Sunday evening", home.Current.Value.Groups[0].Cards[0].Title);
        Assert.Equal("https://img/new", home.Current.Value.Groups[0].Cards[0].Image!.Url);
        Assert.Equal("Sunday evening", Assert.Single(sidebar.Current.Value.Playlists).Name);
        Assert.Equal("Sunday evening", detail.Current.Value.Name);
        Assert.Equal(homeOrder, home.Current.OrderRevision); Assert.Equal(detailOrder, detail.Current.OrderRevision);
        Assert.Equal("row", Assert.Single(detail.Current.Value.Tracks!).ContextUid);
    }

    [Fact]
    public async Task OwnerIdentityUpdateRejoinsBothPlaylistAndSidebar()
    {
        await using var host = new CatalogQueryTestHost();
        await PlaylistSeed(host);
        await host.AcceptAsync(new(host.Scope, Playlist, FacetKind.PlaylistHeader),
            new PlaylistHeaderValue(Name: "Mix", OwnerUri: "spotify:user:owner") { OwnerName = "owner" });
        await host.Data.Replicas.AdoptRootlistAsync(new([new(0, 0, Playlist, null, 0)], new byte[24]));
        using var detail = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, Playlist));
        using var sidebar = host.Data.Queries.Acquire(new SidebarLibraryQuery(host.Scope));
        await QueryPublication.Initial(detail);
        await QueryPublication.Initial(sidebar);
        await host.AcceptAsync(new(host.Scope, "spotify:user:owner", FacetKind.UserIdentity), new UserIdentityValue("Owner Display", new("https://img/avatar")));
        await QueryPublication.Until(() => detail.Current.Value.OwnerName == "Owner Display"
            && sidebar.Current.Value.Playlists.Count > 0 && sidebar.Current.Value.Playlists[0].OwnerName == "Owner Display");
        Assert.Equal("Owner Display", detail.Current.Value.OwnerName);
        Assert.Equal("https://img/avatar", detail.Current.Value.Owner!.Avatar!.Url);
        Assert.Equal("Owner Display", Assert.Single(sidebar.Current.Value.Playlists).OwnerName);
    }

    [Fact]
    public async Task AlbumAndArtistRowsJoinTheSameChangedTrackFacet()
    {
        await using var host = new CatalogQueryTestHost();
        const string album = "spotify:album:a", artist = "spotify:artist:a";
        await host.AcceptAsync(new(host.Scope, album, FacetKind.AlbumIdentity), new AlbumIdentityValue("Album"));
        await host.AcceptAsync(new(host.Scope, artist, FacetKind.ArtistIdentity), new ArtistIdentityValue("Artist"));
        foreach (var key in new[] { new ResourceKey(host.Scope, album, FacetKind.AlbumTracks, new(0,50)),
            new ResourceKey(host.Scope, artist, FacetKind.ArtistPopular, new(0,50)) })
            await host.AcceptAsync(key, new RelationPageValue(key.Facet, "one", null, 0, 1, null, RelationCoverage.Complete, [new("row", Track)]));
        using var albumQuery = host.Data.Queries.Acquire(new AlbumDetailQuery(host.Scope, album));
        using var artistQuery = host.Data.Queries.Acquire(new ArtistDetailQuery(host.Scope, artist));
        await QueryPublication.Initial(albumQuery);
        await QueryPublication.Initial(artistQuery);
        await host.AcceptAsync(new(host.Scope, Track, FacetKind.TrackIdentity), new TrackIdentityValue("Corrected", DurationMs: 321));
        await QueryPublication.Until(() => albumQuery.Current.Value.Tracks is { Count: 1 } a && a[0].Title == "Corrected"
            && artistQuery.Current.Value.TopTracks is { Count: 1 } b && b[0].Title == "Corrected");
        Assert.Equal("Corrected", Assert.Single(albumQuery.Current.Value.Tracks!).Title);
        Assert.Equal("Corrected", Assert.Single(artistQuery.Current.Value.TopTracks!).Title);
        Assert.Equal(321, Assert.Single(albumQuery.Current.Value.Tracks!).DurationMs);
    }

    [Fact]
    public async Task PopularWindowYieldsATrackIdentityKeyForEveryVisibleChartRow()
    {
        // ArtistPopular.cs and SidebarArtistTopTracksSource.cs both lease the "popular" window collection — a
        // "rows"/"tracks"-only ArtistDefinition would contribute zero row keys for it (Part 5 item 3f).
        await using var host = new CatalogQueryTestHost();
        const string artist = "spotify:artist:popular-window";
        var items = Enumerable.Range(0, 20).Select(i => new CatalogRelationItem("popular:" + i, "spotify:track:pop" + i)).ToArray();
        await host.AcceptAsync(new(host.Scope, artist, FacetKind.ArtistPopular, new(0, 50)),
            new RelationPageValue(FacetKind.ArtistPopular, "snap", null, 0, 20, null, RelationCoverage.Complete, items));
        using var handle = host.Data.Queries.Acquire(new ArtistDetailQuery(host.Scope, artist));
        handle.SetDemand(QueryDemand.Initial);
        await QueryPublication.Until(() => handle.Current.Value.TopTracks is { Count: 20 }
            && host.Data.Queries.GetActiveResourceKeys().Count(key => key.Facet == FacetKind.TrackIdentity) == 20);
        Assert.Equal(20, handle.Current.Value.TopTracks!.Count);
        var trackKeys = host.Data.Queries.GetActiveResourceKeys()
            .Where(key => key.Facet == FacetKind.TrackIdentity && key.Subject.StartsWith("spotify:track:pop", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(20, trackKeys.Length);
    }
    [Theory]
    [InlineData(FacetKind.TrackIdentity)]
    [InlineData(FacetKind.EpisodeIdentity)]
    [InlineData(FacetKind.AlbumIdentity)]
    [InlineData(FacetKind.ArtistIdentity)]
    [InlineData(FacetKind.PlaylistHeader)]
    [InlineData(FacetKind.ShowIdentity)]
    [InlineData(FacetKind.UserIdentity)]
    public void QueryDemandRejectsIdentityFacets(FacetKind facet)
    {
        var error = Assert.Throws<ArgumentException>("facets", () => new QueryDemand(true, QueryPriority.Visible, [facet]));
        Assert.Contains("identity facet", error.Message, StringComparison.Ordinal);
        Assert.True(QueryDemand.IsIdentityFacet(facet));
    }

    [Fact]
    public void QueryDemandAcceptsDisplayFacets_AndIsIdentityFacetIsFalseForThem()
    {
        Assert.False(QueryDemand.IsIdentityFacet(FacetKind.PlayCount));
        Assert.False(QueryDemand.IsIdentityFacet(FacetKind.AudioAttributes));
        Assert.False(QueryDemand.IsIdentityFacet(FacetKind.VideoAssociation));
        var demand = new QueryDemand(true, QueryPriority.Visible,
            [FacetKind.PlayCount, FacetKind.AudioAttributes, FacetKind.VideoAssociation]);
        Assert.Equal(new[] { FacetKind.PlayCount, FacetKind.AudioAttributes, FacetKind.VideoAssociation }, demand.Facets);
    }

    [Theory]
    [InlineData(FacetKind.PlayCount)]
    [InlineData(FacetKind.AudioAttributes)]
    [InlineData(FacetKind.VideoAssociation)]
    public async Task PlaylistFacetDemandNamesEveryRowIdentityAlbumArtistAndTheNamedFacet(FacetKind field)
    {
        const string album = "spotify:album:offscreen", artist = "spotify:artist:offscreen", episode = "spotify:episode:offscreen", show = "spotify:show:offscreen";
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet switch
        {
            FacetKind.TrackIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Song", [artist], album))),
            FacetKind.EpisodeIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new EpisodeIdentityValue("Episode", show))),
            FacetKind.ArtistIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistIdentityValue("Artist name"))),
            FacetKind.AlbumIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new AlbumIdentityValue("Album name"))),
            FacetKind.ShowIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new ShowIdentityValue("Show name"))),
            _ => ResourceFetchResult.Absent(),
        }));
        await using var host = new CatalogQueryTestHost(provider);
        await PlaylistSeed(host, new("song", Track, null, 0), new("episode", episode, null, 0));
        var result = await host.Data.Queries.ReadOnceAsync(new PlaylistDetailQuery(host.Scope, Playlist),
            new QueryDemand(true, QueryPriority.Visible, [field]));
        Assert.Equal(new[] { "Song", "Episode" }, result.Value.Tracks!.Select(row => row.Title));
        Assert.Contains(provider.Requests, key => key.Subject == Track && key.Facet == FacetKind.TrackIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == episode && key.Facet == FacetKind.EpisodeIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == album && key.Facet == FacetKind.AlbumIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == artist && key.Facet == FacetKind.ArtistIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == show && key.Facet == FacetKind.ShowIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == Track && key.Facet == field);
        Assert.Contains(provider.Requests, key => key.Subject == episode && key.Facet == field);
        Assert.Equal("Artist name", result.Value.Tracks![0].Artists[0].Name);
        Assert.Equal("Album name", result.Value.Tracks![0].Album.Name);
        Assert.Equal("Show name", result.Value.Tracks![1].Album.Name);
    }

    [Fact]
    public async Task AlbumOpensItsCompleteMembership_AndDemandsEveryRowIdentity()
    {
        const string album = "spotify:album:large";
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet switch
        {
            FacetKind.AlbumIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new AlbumIdentityValue("Long album", TrackCount: 75))),
            FacetKind.AlbumTracks => ResourceFetchResult.Present(new ReplaceFacetPatch(new RelationPageValue(FacetKind.AlbumTracks,
                "edition", null, request.Key.Arguments.Offset, 75, request.Key.Arguments.Offset == 0 ? "50" : null,
                request.Key.Arguments.Offset == 0 ? RelationCoverage.Partial : RelationCoverage.Complete,
                Enumerable.Range(request.Key.Arguments.Offset, request.Key.Arguments.Offset == 0 ? 50 : 25)
                    .Select(i => new CatalogRelationItem("row:" + i, "spotify:track:" + i)).ToArray()))),
            FacetKind.TrackIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Song " + request.Key.Subject))),
            _ => ResourceFetchResult.Absent(),
        }));
        await using var host = new CatalogQueryTestHost(provider);
        var result = await host.Data.Queries.ReadOnceAsync(new AlbumDetailQuery(host.Scope, album), QueryDemand.Initial);
        Assert.Equal(75, result.Value.Tracks!.Count);
        Assert.Equal("spotify:track:74", result.Value.Tracks[74].Uri);
        Assert.Equal(2, provider.Requests.Count(key => key.Facet == FacetKind.AlbumTracks));
        Assert.Equal(75, provider.Requests.Count(key => key.Facet == FacetKind.TrackIdentity));
        Assert.Equal("Song spotify:track:74", result.Value.Tracks[74].Title);
    }

    [Fact]
    public async Task PlaylistRequirementsNameEveryRowIdentityAlbumArtistAndFacetInMembershipOrder()
    {
        const int n = 5;
        var facet = FacetKind.PlayCount;
        await using var host = new CatalogQueryTestHost();
        var members = Enumerable.Range(0, n)
            .Select(i => new PlaylistMember("item:" + i, "spotify:track:" + i, "owner", i)).ToArray();
        await host.SeedAsync(members.Select((member, i) => new CatalogSeed(
            new(host.Scope, member.ItemUri, FacetKind.TrackIdentity),
            new ReplaceFacetPatch(new TrackIdentityValue("Song " + i, ["spotify:artist:" + i],
                "spotify:album:" + i, DurationMs: 180_000)))).ToArray());
        await host.SeedAsync(Enumerable.Range(0, n).Select(i => new CatalogSeed(
            new(host.Scope, "spotify:album:" + i, FacetKind.AlbumIdentity),
            new ReplaceFacetPatch(new AlbumIdentityValue("Album " + i)))).ToArray());
        await host.SeedAsync(Enumerable.Range(0, n).Select(i => new CatalogSeed(
            new(host.Scope, "spotify:artist:" + i, FacetKind.ArtistIdentity),
            new ReplaceFacetPatch(new ArtistIdentityValue("Artist " + i)))).ToArray());
        await host.Data.Replicas.AdoptPlaylistAsync(new(Playlist, PlaylistReadKind.Snapshot, null, new byte[24],
            [..members], [], null));
        using var handle = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, Playlist));
        await QueryPublication.Initial(handle);
        await QueryPublication.Until(() => handle.Current.Value.Tracks?.Count == n);
        handle.SetDemand(new QueryDemand(true, QueryPriority.Visible, [facet]));
        await QueryPublication.Until(() =>
            handle.Current.Demanded.Count(key => key.Facet == FacetKind.TrackIdentity) == n
            && handle.Current.Demanded.Any(key => key.Facet == facet));

        var demanded = handle.Current.Demanded;
        Assert.Equal(Enumerable.Range(0, n).Select(i => "spotify:track:" + i),
            demanded.Where(key => key.Facet == FacetKind.TrackIdentity).Select(key => key.Subject));
        for (int i = 0; i < n; i++)
        {
            Assert.Contains(demanded, key => key.Subject == "spotify:track:" + i && key.Facet == FacetKind.TrackIdentity);
            Assert.Contains(demanded, key => key.Subject == "spotify:album:" + i && key.Facet == FacetKind.AlbumIdentity);
            Assert.Contains(demanded, key => key.Subject == "spotify:artist:" + i && key.Facet == FacetKind.ArtistIdentity);
            Assert.Contains(demanded, key => key.Subject == "spotify:track:" + i && key.Facet == facet);
        }
    }

}
