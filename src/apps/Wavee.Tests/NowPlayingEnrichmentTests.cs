using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Queries;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public class NowPlayingEnrichmentTests : PlaybackCatalogTestBase
{
    const string TrackUri = "spotify:track:one";
    static Track Thin(string uri = TrackUri) => ContextResolve.Synthetic(uri);
    static Track Rich() => new("one", TrackUri, "Resolved song", [new("artist", "spotify:artist:artist", "Artist")],
        new("album", "spotify:album:album", "Album"), 210000, false, new("https://i.scdn.co/image/one"));

    [Fact]
    public async Task MetadataArrivalThenPauseOrVolume_CannotRollQueueBack()
    {
        var projection = Catalog.Projection();
        var session = new PlaybackSession(Catalog.Queue);
        var thinSnapshot = session.SetContext("spotify:playlist:list", [new(Thin(), "u1"), new(Thin(), "u2")], 0);
        projection.ApplyLocalSnapshot(thinSnapshot, new PlaybackEvent(EvKind.Paused, Thin(), 0));
        var revision = Catalog.Queue.Current.StructuralRevision;
        var ids = Catalog.Queue.Current.Rows.Select(x => x.ItemId).ToArray();
        await Catalog.SeedAsync(Rich());
        projection.ApplyLocalSnapshot(thinSnapshot, new PlaybackEvent(EvKind.VolumeChanged, Thin(), 0));
        Assert.All(projection.Queue, row => Assert.Equal("Resolved song", row.Track.Title));
        Assert.Equal("Resolved song", session.Snapshot().Current!.Track.Title);
        Assert.Equal(revision, Catalog.Queue.Current.StructuralRevision);
        Assert.Equal(ids, Catalog.Queue.Current.Rows.Select(x => x.ItemId));
    }

    [Fact]
    public async Task MetadataChange_ReplacesOnlyAffectedQueryRows()
    {
        var projection = Catalog.Projection();
        var session = new PlaybackSession(Catalog.Queue);
        projection.ApplyLocalSnapshot(session.SetContext("spotify:playlist:list", [new(Thin(), "u1"), new(Thin("spotify:track:two"), "u2")], 0));
        var definition = new QueueQueryDefinition(new QueueQuery(Catalog.Repository.Scope), Catalog.Queue, Catalog.Queue);
        var first = definition.Read(new QueryReadContext(Catalog.Repository)).Value;
        await Catalog.SeedAsync(Rich());
        var next = definition.Read(new QueryReadContext(Catalog.Repository)).Value;
        Assert.NotSame(first.Rows[0], next.Rows[0]);
        Assert.Same(first.Rows[1], next.Rows[1]);
        Assert.Equal(first.StructuralRevision, next.StructuralRevision);
    }

    [Fact]
    public async Task RuntimeOverride_AppliesOnlyToCurrentOccurrence()
    {
        var projection = Catalog.Projection();
        await Catalog.SeedAsync(Rich());
        var session = new PlaybackSession(Catalog.Queue);
        projection.ApplyLocalSnapshot(session.SetContext("spotify:playlist:list", [new(Rich(), "u1"), new(Rich(), "u2")], 0));
        projection.SetMetadataOverride(TrackUri, "Live title", "Live artist");
        var definition = new QueueQueryDefinition(new QueueQuery(Catalog.Repository.Scope), Catalog.Queue, Catalog.Queue);
        var before = definition.Read(new QueryReadContext(Catalog.Repository)).Value;
        Assert.Equal("Live title", before.Rows[0].Track.Title);
        Assert.Equal("Resolved song", before.Rows[1].Track.Title);
        projection.ApplyLocalSnapshot(session.Next()!);
        Assert.Equal("Resolved song", projection.CurrentTrack!.Title);
    }

    [Fact]
    public void SuccessorOwner_FencesOutgoingQueuePublicationAndDisposal()
    {
        var outgoing = Catalog.Projection();
        var current = Catalog.Projection();
        var session = new PlaybackSession(Catalog.Queue);
        current.ApplyLocalSnapshot(session.SetContext("spotify:playlist:new", [new(Thin(), "u1")], 0));
        var snapshot = Catalog.Queue.Current;
        outgoing.SetLocalQueue([]);
        outgoing.Dispose();
        Assert.Same(snapshot, Catalog.Queue.Current);
    }

    [Fact]
    public async Task EpisodeCatalogJoin_PreservesShowIdentityAcrossHeartbeats()
    {
        const string episodeUri = "spotify:episode:one", showUri = "spotify:show:show";
        var projection = Catalog.Projection();
        var cluster = new ClusterDelta("phone", true, new RemoteTrack(episodeUri, "Episode", "", "", "Show", showUri, null, 1800000),
            showUri, false, true, false, 0, 0, 0, 1800000, false, RepeatMode.Off, [], []);
        Catalog.Cluster(projection, cluster);
        await Catalog.SeedAsync(new Track("one", episodeUri, "Episode", [], new("show", showUri, "Show"), 1800000,
            false, new("https://i.scdn.co/image/episode")));
        Catalog.Cluster(projection, cluster);
        Assert.Equal(showUri, projection.CurrentTrack!.Album.Uri);
        Assert.Equal("Show", projection.CurrentTrack.Album.Name);
        Assert.NotNull(projection.CurrentTrack.Image);
    }
}
