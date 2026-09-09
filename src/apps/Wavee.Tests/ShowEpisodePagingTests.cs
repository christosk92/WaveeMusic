using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ShowEpisodePagingTests
{
    const string Show = "spotify:show:700";
    static QueryTestProvider Provider(int count, int available = int.MaxValue) => new(request =>
    {
        var key = request.Key;
        CatalogValue? value = key.Facet switch
        {
            FacetKind.ShowIdentity => new ShowIdentityValue { Name = "The Show", Publisher = "Publisher", EpisodeCount = count },
            FacetKind.ShowEpisodes => new RelationPageValue(FacetKind.ShowEpisodes, "edition-1", null,
                key.Arguments.Offset, count, key.Arguments.Offset + 50 >= count ? null : (key.Arguments.Offset + 50).ToString(),
                RelationCoverage.Partial, Enumerable.Range(key.Arguments.Offset, Math.Min(50, Math.Max(0, count - key.Arguments.Offset)))
                    .Select(i => new CatalogRelationItem("row:" + i, "spotify:episode:" + i)).ToArray()),
            FacetKind.EpisodeIdentity when int.Parse(EntityUri.IdOf(key.Subject)) < available => new EpisodeIdentityValue
                { Title = "Episode " + EntityUri.IdOf(key.Subject), ShowUri = Show },
            _ => null,
        };
        return new(request, value is null ? ResourceFetchResult.Absent() : ResourceFetchResult.Present(new ReplaceFacetPatch(value)));
    });

    [Fact]
    public async Task OpenRequestsOnlyFirstPage_ThenRemainderOnceTotalIsKnown_AndRepeatedDemandReusesIt()
    {
        var remainder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<ResourceKey>();
        var waves = new List<IReadOnlyList<ResourceKey>>();
        var provider = new GatedShowProvider(700, remainder, requests, waves);
        await using var host = new CatalogQueryTestHost(provider);
        using var handle = host.Data.Queries.Acquire(new ShowDetailQuery(host.Scope, Show));
        handle.SetDemand(QueryDemand.Initial);
        await QueryPublication.Until(() => handle.Current.Value.PagedThrough == 50);
        Assert.Equal(50, handle.Current.Value.Episodes!.Count);
        Assert.Equal(700, handle.Current.Value.TotalEpisodes);
        IReadOnlyList<ResourceKey> firstEpisodeWave;
        lock (requests) firstEpisodeWave = waves.First(wave => wave.Any(key => key.Facet == FacetKind.ShowEpisodes));
        Assert.Equal(0, Assert.Single(firstEpisodeWave, key => key.Facet == FacetKind.ShowEpisodes).Arguments.Offset);
        Assert.DoesNotContain(firstEpisodeWave, key => key.Facet == FacetKind.ShowEpisodes && key.Arguments.Offset > 0);
        await QueryPublication.Until(() =>
            handle.Current.Demanded.Count(key => key.Facet == FacetKind.ShowEpisodes) == 14);
        remainder.SetResult();
        await QueryPublication.Until(() =>
        {
            int episodeIds;
            lock (requests) episodeIds = requests.Count(key => key.Facet == FacetKind.EpisodeIdentity);
            return handle.Current.Value.PagedThrough == 700
                && handle.Current.Demanded.Count(key => key.Facet == FacetKind.EpisodeIdentity) == 700
                && episodeIds == 700
                && !handle.Current.Status.IsRefreshing;
        });
        Assert.Equal(700, handle.Current.Value.Episodes!.Count);
        Assert.Equal(14, requests.Count(key => key.Facet == FacetKind.ShowEpisodes));
        int afterLoad;
        lock (requests) afterLoad = requests.Count;
        var again = await host.Library.GetShowAsync(Show);
        Assert.Equal(700, again!.PagedThrough);
        lock (requests) Assert.Equal(afterLoad, requests.Count);
    }

    sealed class GatedShowProvider(
        int count,
        TaskCompletionSource remainder,
        List<ResourceKey> requests,
        List<IReadOnlyList<ResourceKey>> waves) : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requested,
            CancellationToken ct)
        {
            var wave = requested.Select(request => request.Key).ToArray();
            lock (requests)
            {
                requests.AddRange(wave);
                waves.Add(wave);
            }
            if (requested.Any(request => request.Key.Facet == FacetKind.ShowEpisodes && request.Key.Arguments.Offset > 0))
                return AwaitRemainder(requested, ct);
            return ValueTask.FromResult(Serve(requested));
        }

        async ValueTask<IReadOnlyList<ResourceResponse>> AwaitRemainder(IReadOnlyList<ResourceRequest> requested,
            CancellationToken ct)
        {
            await remainder.Task.WaitAsync(ct);
            return Serve(requested);
        }

        IReadOnlyList<ResourceResponse> Serve(IReadOnlyList<ResourceRequest> requested)
            => requested.Select(request =>
            {
                var key = request.Key;
                CatalogValue? value = key.Facet switch
                {
                    FacetKind.ShowIdentity => new ShowIdentityValue { Name = "The Show", Publisher = "Publisher", EpisodeCount = count },
                    FacetKind.ShowEpisodes => new RelationPageValue(FacetKind.ShowEpisodes, "edition-1", null,
                        key.Arguments.Offset, count, key.Arguments.Offset + 50 >= count ? null : (key.Arguments.Offset + 50).ToString(),
                        RelationCoverage.Partial, Enumerable.Range(key.Arguments.Offset, Math.Min(50, Math.Max(0, count - key.Arguments.Offset)))
                            .Select(i => new CatalogRelationItem("row:" + i, "spotify:episode:" + i)).ToArray()),
                    FacetKind.EpisodeIdentity => new EpisodeIdentityValue
                        { Title = "Episode " + EntityUri.IdOf(key.Subject), ShowUri = Show },
                    _ => null,
                };
                return new ResourceResponse(request, value is null
                    ? ResourceFetchResult.Absent()
                    : ResourceFetchResult.Present(new ReplaceFacetPatch(value)));
            }).ToArray();
    }

    [Fact]
    public async Task ExplicitDemandCanReachEpisode700_WithoutDuplicatingOccurrences()
    {
        var provider = Provider(700);
        await using var host = new CatalogQueryTestHost(provider);
        var result = await host.Data.Queries.ReadOnceAsync(new ShowDetailQuery(host.Scope, Show),
            QueryDemand.Initial);
        Assert.Equal(700, result.Value.PagedThrough);
        Assert.Equal(700, result.Value.Episodes!.Select(episode => episode.Uri).Distinct().Count());
        Assert.Equal(14, provider.Requests.Count(key => key.Facet == FacetKind.ShowEpisodes));
        Assert.Equal("Episode 699", result.Value.Episodes![^1].Title);
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(8, 5)]
    public async Task EndCursorComesFromMembership_IncludingUnavailableEpisodes(int total, int available)
    {
        await using var host = new CatalogQueryTestHost(Provider(total, available));
        var result = await host.Data.Queries.ReadOnceAsync(new ShowDetailQuery(host.Scope, Show));
        Assert.Equal(total, result.Value.TotalEpisodes);
        Assert.Equal(total, result.Value.PagedThrough);
        Assert.Equal(total, result.Value.Episodes!.Count);
        Assert.Equal(available, result.Value.Episodes.Count(episode => episode.Title.Length > 0));
        Assert.Equal(total, await host.Library.LoadMoreEpisodesAsync(Show, total));
    }
    [Fact]
    public async Task RetainedExpandedDemandSurvivesFiniteReadsParkingAndMetadataChanges()
    {
        await using var host = new CatalogQueryTestHost(Provider(120));
        var posts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var spec = new ShowDetailQuery(host.Scope, Show);
        using var watch = host.Data.Queries.Acquire(spec);
        using var binding = new QuerySignalBinding<Wavee.Core.Show>(host.Data.Queries.Acquire(spec), posts.Enqueue);
        var expanded = QueryDemand.Initial;
        binding.SetDemand(expanded); binding.SetActive(true);
        await host.Data.Queries.ReadOnceAsync(spec, expanded);
        await QueryPublication.Until(() =>
        {
            while (posts.TryDequeue(out var post)) post();
            return binding.Snapshot.Peek().Value.PagedThrough == 120;
        });
        Assert.Equal(120, binding.Snapshot.Peek().Value.PagedThrough);
        binding.SetActive(false);
        await host.AcceptAsync(new(host.Scope, "spotify:episode:99", FacetKind.EpisodeIdentity), new EpisodeIdentityValue("Updated", Show));
        Assert.NotEqual("Updated", binding.Snapshot.Peek().Value.Episodes![99].Title);
        await QueryPublication.Until(() => watch.Current.Value.Episodes is { Count: 120 } rows
            && rows[99].Title == "Updated");
        binding.SetActive(true);
        await QueryPublication.Until(() =>
        {
            while (posts.TryDequeue(out var post)) post();
            return binding.Snapshot.Peek().Value.Episodes is { Count: 120 } rows && rows[99].Title == "Updated";
        });
        Assert.Equal(120, binding.Snapshot.Peek().Value.Episodes!.Count);
        Assert.Equal("Updated", binding.Snapshot.Peek().Value.Episodes![99].Title);
    }

}
