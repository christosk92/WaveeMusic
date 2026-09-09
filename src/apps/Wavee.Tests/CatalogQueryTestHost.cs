using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Tests;

internal sealed class CatalogQueryTestHost : IAsyncDisposable
{
    public CatalogRuntime Data { get; }
    public AggregateCatalog Library { get; }
    public SourceRegistry Registry { get; }
    public CatalogScope Scope => Data.Catalog.Scope;
    public CatalogQueryTestHost(ICatalogResourceProvider provider) : this(provider, new MemoryDataPersistence()) { }
    public CatalogQueryTestHost(ICatalogResourceProvider provider, MemoryDataPersistence persistence)
    {
        Registry = new([new CatalogSourceRegistration(provider.Provider, _ => true,
            SourceCapabilities.Search | SourceCapabilities.Home | SourceCapabilities.Podcasts)]);
        Data = new(new(provider.Provider, "test", "en", "NL", "premium", 1, false), "test", persistence, persistence,
            new MemoryReplicaProjection(new InMemoryStore()), [provider], _ => provider.Provider);
        Library = new(Registry, Data.Queries, _ => Scope);
    }
    public CatalogQueryTestHost(params ISource[] sources)
    {
        Registry = new(sources);
        string Owner(string uri) => Registry.OwnerOf(uri)?.Id
            ?? Registry.OfCapability(SourceCapabilities.Fallback).FirstOrDefault()?.Id ?? "spotify";
        var persistence = new MemoryDataPersistence();
        Data = new(new("spotify", "test", "en", "NL", "premium", 1, false), "test", persistence, persistence,
            new MemoryReplicaProjection(new InMemoryStore()), sources.Where(s => s is ICatalogSource or IPodcastSource)
                .Select(s => new NativeCatalogResourceProvider(Registry, s)), Owner);
        Library = new(Registry, Data.Queries, uri => Scope with { Provider = Owner(uri) });
    }
    public Task SeedAsync(params CatalogSeed[] seeds) => Data.Catalog.SeedManyAsync(seeds, Data.Catalog.Epoch);
    public Task SeedAsync(Track track)
    { var seeds = new List<CatalogSeed>(); CatalogDomainSeeds.Track(Scope, track, seeds); return SeedAsync(seeds.ToArray()); }
    public Task SeedAsync(Episode episode)
    { var seeds = new List<CatalogSeed>(); CatalogDomainSeeds.Episode(Scope, episode, seeds); return SeedAsync(seeds.ToArray()); }
    public async Task AcceptAsync(ResourceKey key, CatalogValue value)
    {
        var request = await Data.Catalog.CaptureRequestAsync(key, ResourcePriority.Visible);
        await Data.Catalog.AcceptAsync([new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(value)))]);
    }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}

internal sealed class QueryTestProvider(Func<ResourceRequest, ResourceResponse> fetch) : ICatalogResourceProvider
{
    public string Provider => "spotify";
    public readonly List<ResourceKey> Requests = [];
    /// <summary>The full requests (key AND the priority the coordinator actually fetched them at) — what a
    /// background-sweep test needs to tell a Prefetch-priority wave apart from the Visible-priority one.</summary>
    public readonly List<ResourceRequest> Received = [];
    public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
    {
        lock (Requests) { Requests.AddRange(requests.Select(request => request.Key)); Received.AddRange(requests); }
        return ValueTask.FromResult<IReadOnlyList<ResourceResponse>>(requests.Select(fetch).ToArray());
    }
}
