using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Tests;

/// <summary>Each xUnit case owns its catalog worker and every playback projection it creates.</summary>
public abstract class PlaybackCatalogTestBase : IDisposable
{
    protected PlaybackCatalogTestHost Catalog { get; } = new();
    public virtual void Dispose() => Catalog.Dispose();
}

public sealed class PlaybackCatalogTestHost : IDisposable
{
    readonly List<NowPlayingProjection> _projections = [];
    readonly List<PlaybackCatalogTestHost> _children = [];
    readonly MemoryDataPersistence _persistence = new();
    public CatalogRuntime Data { get; }
    public PlaybackQueueProjection Queue => Data.PlaybackQueue;
    public CatalogRepository Repository => Data.Catalog;
    public PlaybackCatalogTestHost()
    {
        Data = new CatalogRuntime(new CatalogScope("spotify", "test", "en", "US", "premium", 1, false), "test",
            _persistence, _persistence, new NoReplicaProjection(), [], uri => EntityUri.Parse(uri).Provider);
    }
    public NowPlayingProjection Projection(string deviceId = "us", Func<long>? clock = null,
        Func<long>? serverNowUnixMs = null, double initialVolume01 = .7)
    {
        var projection = new NowPlayingProjection(deviceId, Queue, clock, serverNowUnixMs, initialVolume01);
        _projections.Add(projection);
        return projection;
    }
    public PlaybackCatalogTestHost Fork()
    { var child = new PlaybackCatalogTestHost(); _children.Add(child); return child; }
    public void Seed(params Track[] tracks) => SeedAsync(tracks).GetAwaiter().GetResult();
    public Task SeedAsync(params Track[] tracks) => Queue.SeedTracksAsync(tracks, Repository.Epoch);
    public void Cluster(NowPlayingProjection projection, ClusterDelta cluster)
    { projection.OnCluster(cluster); Flush(); }
    public void Event(NowPlayingProjection projection, PlaybackEvent value)
    { projection.OnEvent(value); Flush(); }
    public Task ObserveAsync(params CatalogObservation[] observations) => Data.Commits.CommitAsync(async token =>
    {
        var prepared = await Repository.PrepareObservationsAsync(observations, token);
        return new DataCommit<int>(ct => _persistence.CommitAsync(prepared.Commit, ct), prepared.Publish, 0);
    });
    public ResourceKey Key(string uri, FacetKind facet) => new(Repository.Scope with { Provider = EntityUri.Parse(uri).Provider }, uri, facet);
    public void Flush() => Data.Commits.FlushAsync().GetAwaiter().GetResult();
    public void Dispose()
    {
        foreach (var child in _children) child.Dispose();
        foreach (var projection in _projections) projection.Dispose();
        Data.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    sealed class NoReplicaProjection : IReplicaProjectionSink { public void Publish(ReplicaProjection projection) { } }
}
