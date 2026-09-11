using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ResourceCoordinatorBatchingTests
{
    [Fact]
    public async Task FifteenHundredTrackIdentities_SplitIntoAtMostSixBatchesOfThreeHundredInEnqueueOrder()
    {
        await using var fixture = new CatalogFixture();
        var batches = new List<string[]>();
        var provider = new RecordingProvider((requests, _) =>
        {
            lock (batches) batches.Add(requests.Select(request => request.Key.Subject).ToArray());
            return Task.FromResult(Present(requests));
        });
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var keys = Enumerable.Range(0, 1500)
            .Select(i => new ResourceKey(fixture.Scope, "spotify:track:" + i, FacetKind.TrackIdentity))
            .ToArray();

        var results = await coordinator.EnsureAsync(keys).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(results, result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
        lock (batches)
        {
            Assert.InRange(batches.Count, 1, 6);
            Assert.All(batches, batch => Assert.InRange(batch.Distinct(StringComparer.Ordinal).Count(), 1, 300));
            // Batches are FORMED in enqueue order (TakeBatchLocked walks Sequence). FetchAsync is entered
            // after an await, so completion/start-of-fetch may interleave under MaxInFlight=4.
            var subjects = keys.Select(key => key.Subject).ToArray();
            foreach (var batch in batches)
            {
                int start = Array.IndexOf(subjects, batch[0]);
                Assert.True(start >= 0);
                Assert.Equal(subjects.Skip(start).Take(batch.Length), batch);
            }
            Assert.Equal(subjects.ToHashSet(StringComparer.Ordinal),
                batches.SelectMany(batch => batch).ToHashSet(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task OneBatchGroup_RunsAtMostFourBatchesInFlight()
    {
        await using var fixture = new CatalogFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingProvider(async (requests, _) =>
        {
            await release.Task;
            return Present(requests);
        });
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var keys = Enumerable.Range(0, 1500)
            .Select(i => new ResourceKey(fixture.Scope, "spotify:track:" + i, FacetKind.TrackIdentity))
            .ToArray();

        var ensure = coordinator.EnsureAsync(keys);
        await UntilAsync(() => provider.Active == ResourceCoordinator.MaxInFlight);
        Assert.Equal(ResourceCoordinator.MaxInFlight, provider.MaximumActive);
        Assert.True(provider.Calls >= ResourceCoordinator.MaxInFlight);
        release.SetResult();
        var results = await ensure.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(results, result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
        Assert.Equal(ResourceCoordinator.MaxInFlight, provider.MaximumActive);
        Assert.InRange(provider.Calls, 5, 6);
    }

    static IReadOnlyList<ResourceResponse> Present(IReadOnlyList<ResourceRequest> requests)
        => requests.Select(request => new ResourceResponse(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(request.Key.Subject))))).ToArray();

    static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }

    sealed class RecordingProvider(Func<IReadOnlyList<ResourceRequest>, CancellationToken, Task<IReadOnlyList<ResourceResponse>>> fetch)
        : ICatalogResourceProvider
    {
        int _calls, _active, _maximum;
        public string Provider => "spotify";
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximum);
        public string BatchGroup(ResourceKey key) => "metadata";
        public async ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (previous < active && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try { return await fetch(requests, ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
