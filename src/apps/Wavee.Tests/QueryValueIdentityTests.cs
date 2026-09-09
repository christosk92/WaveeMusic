using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>The read model's IDENTITY contract — what keeps a 1.5k-row page from re-joining and re-mapping itself on
/// every catalog publication. A read that finds nothing changed hands back the PREVIOUS instances; a read where one
/// member moved hands back a record that SHARES every row that did not; and an active demand names every row's
/// identity rather than a viewport window.</summary>
public sealed class QueryValueIdentityTests
{
    const string PlaylistUri = "spotify:playlist:identity";

    static PlaylistMember[] Members(int count)
        => Enumerable.Range(0, count).Select(i => new PlaylistMember("item:" + i, "spotify:track:" + i, "owner", i)).ToArray();

    /// <summary>A member's join reads its own identity plus the album and artist it names — the six-to-eight key
    /// dependency set a real row has, not a bare title.</summary>
    static async Task<CatalogQueryTestHost> PlaylistHostAsync(int count)
    {
        var host = new CatalogQueryTestHost();
        var members = Members(count);
        await host.SeedAsync(members.Select((member, i) => new CatalogSeed(
            new(host.Scope, member.ItemUri, FacetKind.TrackIdentity),
            new ReplaceFacetPatch(new TrackIdentityValue("Song " + i, ["spotify:artist:" + i % 60],
                "spotify:album:" + i % 40, DurationMs: 180_000)))).ToArray());
        await host.SeedAsync(Enumerable.Range(0, 40).Select(i => new CatalogSeed(
            new(host.Scope, "spotify:album:" + i, FacetKind.AlbumIdentity),
            new ReplaceFacetPatch(new AlbumIdentityValue("Album " + i)))).ToArray());
        await host.SeedAsync(Enumerable.Range(0, 60).Select(i => new CatalogSeed(
            new(host.Scope, "spotify:artist:" + i, FacetKind.ArtistIdentity),
            new ReplaceFacetPatch(new ArtistIdentityValue("Artist " + i)))).ToArray());
        await host.Data.Replicas.AdoptPlaylistAsync(new(PlaylistUri, PlaylistReadKind.Snapshot, null, new byte[24],
            [..members], [], null));
        return host;
    }

    static CatalogReadView ViewOf(CatalogQueryTestHost host)
        => new(host.Scope, host.Data.Replicas, static _ => "spotify");

    [Fact]
    public async Task APlaylistWithoutACustomCoverWearsAMosaicOfItsTracks_AndKeepsItsIdentity()
    {
        await using var host = await PlaylistHostAsync(12);
        await host.SeedAsync(Enumerable.Range(0, 12).Select(i => new CatalogSeed(
            new(host.Scope, "spotify:album:" + i, FacetKind.AlbumIdentity),
            new ReplaceFacetPatch(new AlbumIdentityValue("Album " + i, new Image("https://i.scdn.co/image/cover" + i))))).ToArray());
        var view = ViewOf(host);
        var first = view.Playlist(new QueryReadContext(host.Data.Catalog), PlaylistUri);
        var second = view.Playlist(new QueryReadContext(host.Data.Catalog), PlaylistUri);
        Assert.NotNull(first.Cover);
        Assert.Equal(4, first.Cover!.MosaicTiles!.Count);
        Assert.Equal("https://i.scdn.co/image/cover0", first.Cover.MosaicTiles[0]);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task AnUnchangedCatalogReturnsThePlaylistAndItsRowVectorByReference()
    {
        await using var host = await PlaylistHostAsync(1500);
        var view = ViewOf(host);
        var first = view.Playlist(new QueryReadContext(host.Data.Catalog), PlaylistUri, includeRows: true);
        var second = view.Playlist(new QueryReadContext(host.Data.Catalog), PlaylistUri, includeRows: true);
        var (firstRows, secondRows) = (first.Tracks!, second.Tracks!);
        Assert.Equal(1500, firstRows.Count);
        Assert.Equal("Song 7", firstRows[7].Title);
        Assert.Same(first, second);
        Assert.Same(firstRows, secondRows);
        for (int i = 0; i < 1500; i++) Assert.Same(firstRows[i], secondRows[i]);
    }

    [Fact]
    public async Task OneChangedMemberKeepsEveryOtherRowInstance()
    {
        await using var host = await PlaylistHostAsync(1500);
        var view = ViewOf(host);
        var before = view.Playlist(new QueryReadContext(host.Data.Catalog), PlaylistUri, includeRows: true);
        await host.AcceptAsync(new(host.Scope, "spotify:track:1000", FacetKind.TrackIdentity),
            new TrackIdentityValue("Corrected"));
        var after = view.Playlist(new QueryReadContext(host.Data.Catalog), PlaylistUri, includeRows: true);
        var (beforeRows, afterRows) = (before.Tracks!, after.Tracks!);
        Assert.NotSame(before, after);
        Assert.Equal("Corrected", afterRows[1000].Title);
        Assert.Equal("Song 1000", beforeRows[1000].Title);
        int shared = 0;
        for (int i = 0; i < 1500; i++) if (i != 1000 && ReferenceEquals(beforeRows[i], afterRows[i])) shared++;
        Assert.Equal(1499, shared);
        Assert.Equal("item:999", afterRows[999].ContextUid);
    }

    [Fact]
    public async Task TheLibrarySnapshotSurvivesARejoinThatChangedNoReplica()
    {
        await using var host = new CatalogQueryTestHost();
        await host.SeedAsync(Enumerable.Range(0, 40).Select(i => new CatalogSeed(
            new(host.Scope, $"spotify:album:{i:D3}", FacetKind.AlbumIdentity),
            new ReplaceFacetPatch(new AlbumIdentityValue($"Album {i:D3}")))).ToArray());
        await host.Data.Replicas.AdoptCollectionAsync(new(CollectionSets.WireSet("albums"), true, true, 0, null, "r1",
            [..Enumerable.Range(0, 40).Select(i => new CollectionItem($"spotify:album:{i:D3}", false, 1000 - i))]));
        await host.Data.Replicas.AdoptRootlistAsync(new(
            [..Enumerable.Range(0, 10).Select(i => new RootlistEntry(i, 0, "spotify:playlist:" + i, null, 0))],
            new byte[24]));
        using var query = host.Data.Queries.Acquire(new SidebarLibraryQuery(host.Scope));
        await QueryPublication.Initial(query);
        await QueryPublication.Until(() => query.Current.Value.Albums.Count == 40 && query.Current.Value.Entries.Count == 50);
        var before = query.Current;
        Assert.Equal("Album 000", before.Value.Albums[0].Name);
        // A demand naming a facet the join never observed is exactly the case a demand-only replan cannot cover, so
        // this forces a genuine re-read of the whole library join with not one replica or fact moved underneath it.
        query.SetDemand(new(true, QueryPriority.Visible, [FacetKind.PlayCount]));
        await QueryPublication.Until(() => query.Current.Demanded.Any(key => key.Facet == FacetKind.PlayCount));
        Assert.True(query.Current.Revision > before.Revision, "The demand change never published.");
        Assert.Same(before.Value, query.Current.Value);
        Assert.Equal(before.Value, query.Current.Value);
    }

    [Fact]
    public async Task PlaylistWholeModelDemandNamesEveryTrackIdentityAtVisiblePriority()
    {
        const int members = 1494;
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet == FacetKind.TrackIdentity
            ? ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Song " + request.Key.Subject)))
            : ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        await host.Data.Replicas.AdoptPlaylistAsync(new(PlaylistUri, PlaylistReadKind.Snapshot, null, new byte[24],
            [..Members(members)], [], null));
        using var query = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, PlaylistUri));
        await QueryPublication.Initial(query);
        await QueryPublication.Until(() => query.Current.Value.Tracks?.Count == members);
        query.SetDemand(QueryDemand.Initial);
        await QueryPublication.Until(() => query.Current.Demanded.Count(key => key.Facet == FacetKind.TrackIdentity) == members);

        Assert.Equal(Enumerable.Range(0, members).Select(i => "spotify:track:" + i),
            query.Current.Demanded.Where(key => key.Facet == FacetKind.TrackIdentity).Select(key => key.Subject));
        Assert.Contains(query.Current.Demanded, key => key.Subject == "spotify:track:1400");

        await QueryPublication.Until(() => query.Current.Value.Tracks!.All(t => t.Title.Length > 0));
        Assert.Equal(members, provider.Received.Where(r => r.Key.Facet == FacetKind.TrackIdentity)
            .Select(r => r.Key.Subject).Distinct().Count());
        Assert.All(provider.Received.Where(r => r.Key.Facet == FacetKind.TrackIdentity),
            r => Assert.Equal(ResourcePriority.Visible, r.Priority));
    }
}
