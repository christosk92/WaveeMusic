using System.Collections.Concurrent;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class QueryDemandLifetimeTests
{
    sealed record Spec(CatalogScope Scope, int Count) : QuerySpec<int>(Scope);
    sealed class Definition(Spec spec) : IQueryDefinition<int>
    {
        public int Version;
        public QueryReadResult<int> Pending => new(0, 0, false);
        public QueryReadResult<int> Read(QueryReadContext read)
        {
            read.DependOnReplica("projection-barrier");
            int loaded = 0;
            for (int i = 0; i < spec.Count; i++)
                if (read.Read<ArtistIdentityValue>(Key(i)) is not null) loaded++;
            return new(loaded, Version, true);
        }
        ResourceKey Key(int i) => new(spec.Scope, "spotify:artist:" + i, FacetKind.ArtistIdentity);
        public QueryRequirements Requirements(int value, QueryDemand demand) => demand.Active
            ? new(Enumerable.Range(0, spec.Count).Select(Key).ToArray(), [])
            : QueryRequirements.Empty;
    }
    sealed class Replicas : IQueryReplicaDemand
    {
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReplicaRequest>>(Array.Empty<ReplicaRequest>());
    }
    sealed class BlockedProvider : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public string BatchGroup(ResourceKey key) => key.Subject;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ResourceKey> Requested { get; } = new();
        public async ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            foreach (var request in requests) Requested.Enqueue(request.Key);
            await Release.Task.WaitAsync(ct);
            return requests.Select(request => new ResourceResponse(request,
                ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistIdentityValue("Loaded"))))).ToArray();
        }
    }
    sealed record ReplicaSpec(CatalogScope Scope) : QuerySpec<bool>(Scope);
    sealed class RecoveringReplica : IQueryReplicaDemand, IQueryDefinition<bool>
    {
        public QueryReadResult<bool> Pending => new(false, 0, false);
        public bool Fail = true, Known;
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
        {
            if (Fail) throw new IOException("Rootlist unavailable");
            Known = true;
            return Task.FromResult<IReadOnlyList<ReplicaRequest>>(Array.Empty<ReplicaRequest>());
        }
        public QueryReadResult<bool> Read(QueryReadContext read) => new(Known, 0, Known);
        public QueryRequirements Requirements(bool value, QueryDemand demand) => new([], [new("rootlist", "rootlist")]);
    }

    [Fact]
    public async Task ReplicaFailureIsVisibleWithoutCatalogKeysAndExplicitRefreshRecovers()
    {
        await using var catalog = new CatalogFixture();
        var provider = new QueryTestProvider(request => throw new InvalidOperationException("No metadata expected"));
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        var replica = new RecoveringReplica();
        using var queries = new QueryService(catalog.Repository, resources, replica, new SimpleEvent<ReplicaChange>());
        queries.Register<ReplicaSpec, bool>(_ => replica);
        using var handle = queries.Acquire(new ReplicaSpec(catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Failure is not null);
        Assert.Empty(handle.Current.Resources);
        Assert.Equal("Rootlist unavailable", QueryPresentationRules.InitialFailure(handle.Current)!.Message);
        replica.Fail = false;
        var refreshed = await handle.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(refreshed.Succeeded);
        Assert.True(handle.Current.Value);
        Assert.Null(handle.Current.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParkingOneViewKeepsTheWholeModelWhileAnotherHandleStaysActive(bool parkDuringPublication)
    {
        await using var catalog = new CatalogFixture();
        var provider = new BlockedProvider();
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        using var queries = new QueryService(catalog.Repository, resources, new Replicas(), new SimpleEvent<ReplicaChange>());
        var spec = new Spec(catalog.Scope, 100);
        var definition = new Definition(spec);
        queries.Register<Spec, int>(_ => definition);
        using var first = queries.Acquire(spec);
        using var second = queries.Acquire(spec);
        using var releasePublication = new ManualResetEventSlim();
        var enteredPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = first.Changes.Subscribe(Observers.From<QuerySnapshot<int>>(snapshot =>
        {
            if (!parkDuringPublication || snapshot.OrderRevision != 1) return;
            enteredPublication.TrySetResult();
            releasePublication.Wait(TestContext.Current.CancellationToken);
        }));
        try
        {
            first.SetDemand(QueryDemand.Initial);
            second.SetDemand(QueryDemand.Initial);
            // ResourceCoordinator.MaxInFlight is 4: with one-subject batch groups the 4 lowest-sequence rows
            // run immediately and the rest queue.
            await Until(() => resources.Running == 4 && resources.Pending == 96);
            if (parkDuringPublication)
            {
                // Hold the old projection after its DTO publication, immediately before PlanDemand installs it.
                // Parking here must preserve waiter cleanup for the next, non-superseded demand plan.
                definition.Version = 1;
                queries.NotifyDependencyChanged("projection-barrier");
                await enteredPublication.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            first.SetDemand(QueryDemand.None);
            releasePublication.Set();
            await Until(() => resources.Running == 4 && resources.Pending == 96);
            provider.Release.TrySetResult();
            await Until(() => catalog.Repository.Peek(new(catalog.Scope, "spotify:artist:99", FacetKind.ArtistIdentity)).Knowledge == Knowledge.Present);
            Assert.Equal(100, provider.Requested.Distinct().Count());
            Assert.Contains(provider.Requested, key => key.Subject == "spotify:artist:99");
        }
        finally { releasePublication.Set(); provider.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListsLargerThanCoordinatorCapacityCompleteInBoundedWaves(bool retained)
    {
        const int count = ResourceCoordinator.QueueCapacity + 17;
        await using var catalog = new CatalogFixture();
        var provider = new QueryTestProvider(request => new(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistIdentityValue("Loaded")))));
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        using var queries = new QueryService(catalog.Repository, resources, new Replicas(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, int>(spec => new Definition(spec));
        var spec = new Spec(catalog.Scope, count);
        using var handle = queries.Acquire(spec);
        if (retained)
        {
            handle.SetDemand(QueryDemand.Initial);
            await Until(() => handle.Current.Value == count);
        }
        else
        {
            var result = await queries.ReadOnceAsync(spec, QueryDemand.Initial, TestContext.Current.CancellationToken);
            Assert.Equal(count, result.Value);
        }
        Assert.Equal(count, provider.Requests.Count);
        Assert.Equal(count, provider.Requests.Distinct().Count());
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    // LibraryQueryDemand.EnsureAsync: the go-live window before SetProtocolSessionAsync installs a LibrarySync
    // (session() answers null) must answer every request unserved rather than silently no-op'ing — that silent
    // no-op is exactly what left a playlist page's replica demand dropped for good (findings). Constructing a real
    // LibrarySync needs a full IHttpExchange + ITransport rig (see LibraryVerticalTests); this covers the
    // null-session branch the bug is actually about, per the plan's own fallback. "rootlist"/"collection" kinds
    // only — a "playlist" kind's warm (EnsurePlaylistCachedAsync) reads the catalog for a header, which needs a
    // registered resource provider this bare host does not wire; that warm step is independent of session()
    // anyway (the null-session branch below it is what this test targets).
    [Fact]
    public async Task LibraryQueryDemandAnswersEveryRequestAsUnservedWhenNoProtocolSessionIsInstalled()
    {
        await using var host = new ReplicaTestHost();
        var demand = new LibraryQueryDemand(() => null, host.Replicas);
        var requests = new ReplicaRequest[]
        {
            new("rootlist", "rootlist"),
            new("collection", "albums"),
        };
        var unserved = await demand.EnsureAsync(requests, false, TestContext.Current.CancellationToken);
        Assert.Equal(requests, unserved);
    }
}
