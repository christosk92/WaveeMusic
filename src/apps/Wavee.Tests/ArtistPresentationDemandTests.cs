using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Persistence;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ArtistPresentationDemandTests
{
    sealed class LocalProvider : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public bool RequiresNetwork(ResourceKey key) => false;
        public ConcurrentQueue<ResourceKey> Requests { get; } = new();
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            foreach (var request in requests) Requests.Enqueue(request.Key);
            return ValueTask.FromResult<IReadOnlyList<ResourceResponse>>(requests.Select(request =>
                new ResourceResponse(request, ResourceFetchResult.Absent(TimeSpan.FromHours(12)))).ToArray());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(456)]
    public async Task ColdRestartWithFreshOverviewLoadsPersistedCountsThroughPageDemand(long count)
    {
        const string artistUri = "spotify:artist:a", trackUri = "spotify:track:t";
        var persistence = new MemoryDataPersistence();
        var provider = new LocalProvider();
        CatalogScope scope;
        await using (var first = new CatalogQueryTestHost(provider, persistence))
        {
            scope = first.Scope;
            await first.AcceptAsync(new(scope, artistUri, FacetKind.ArtistIdentity), new ArtistIdentityValue("Artist"));
            await first.AcceptAsync(new(scope, artistUri, FacetKind.ArtistOverview), new ArtistOverviewValue(MonthlyListeners: 100));
            await first.AcceptAsync(new(scope, artistUri, FacetKind.ArtistPopular, new(Limit: 50)),
                new RelationPageValue(FacetKind.ArtistPopular, "a", null, 0, 1, null, RelationCoverage.Complete,
                    [new("occurrence", trackUri)]));
            await first.AcceptAsync(new(scope, trackUri, FacetKind.TrackIdentity), new TrackIdentityValue("Track"));
            await first.AcceptAsync(new(scope, trackUri, FacetKind.PlayCount), new PlayCountValue(count));
        }

        await using var restarted = new CatalogQueryTestHost(provider, persistence);
        var countKey = new ResourceKey(scope, trackUri, FacetKind.PlayCount);
        Assert.Equal(Knowledge.Unknown, restarted.Data.Catalog.Peek(countKey).Knowledge);
        var demand = new QueryDemand(true, QueryPriority.Visible,
            TrackPresentationRequirements.RequiredFacets(TrackPresentationRequirements.ArtistPopular));
        var snapshot = await restarted.Data.Queries.ReadOnceAsync(new ArtistDetailQuery(scope, artistUri), demand,
            TestContext.Current.CancellationToken);
        Assert.Equal(count, Assert.Single(snapshot.Value.TopTracks!).PlayCount);
        Assert.Equal(Knowledge.Present, snapshot.Resources[countKey].Knowledge);
        Assert.Equal(TrackFactState.Present, TrackFactPresentation.State(snapshot.Resources[countKey]));
        Assert.DoesNotContain(provider.Requests, key => key.Subject == artistUri && key.Facet == FacetKind.ArtistOverview);
        Assert.DoesNotContain(provider.Requests, key => key == countKey);
    }
}
