using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class QueryProjectionWorkerTests
{
    sealed record Spec(CatalogScope Scope) : QuerySpec<int>(Scope);
    sealed class BlockedDefinition : IQueryDefinition<int>, IDisposable
    {
        public QueryReadResult<int> Pending => new(-1, 0, false);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public int Reads, Overlaps;
        int _reading, _wanted;
        public QueryReadResult<int> Read(QueryReadContext read)
        {
            if (Interlocked.Increment(ref _reading) != 1) Interlocked.Increment(ref Overlaps);
            try
            {
                Interlocked.Increment(ref Reads);
                Entered.TrySetResult();
                Release.Wait(TestContext.Current.CancellationToken);
                return new(_wanted, 0, true);
            }
            finally { Interlocked.Decrement(ref _reading); }
        }
        public QueryRequirements Requirements(int value, QueryDemand demand)
        {
            _wanted = demand.Active ? 1 : 0;
            return QueryRequirements.Empty;
        }
        public void Dispose() => Release.Dispose();
    }
    sealed class LocalReplicas : IQueryReplicaDemand
    {
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests,
            bool force, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReplicaRequest>>([]);
    }

    sealed class FaultingDefinition : IQueryDefinition<int>
    {
        public QueryReadResult<int> Pending => new(-1, 0, false);
        public QueryReadResult<int> Read(QueryReadContext read) => throw new InvalidOperationException("Projection failed");
        public QueryRequirements Requirements(int value, QueryDemand demand) => QueryRequirements.Empty;
    }

    [Fact]
    public async Task FiniteOperationsReturnProjectionFailureWithoutTreatingThePendingSeedAsData()
    {
        await using var catalog = new CatalogFixture();
        await using var resources = new ResourceCoordinator(catalog.Repository, [], catalog.Clock);
        using var queries = new QueryService(catalog.Repository, resources, new LocalReplicas(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, int>(_ => new FaultingDefinition());
        var spec = new Spec(catalog.Scope);
        using var handle = queries.Acquire(spec);
        var result = await queries.ReadOnceAsync(spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ResourceErrorKind.InvalidResponse, result.Failure?.Kind);
        Assert.False(result.Status.HasPrimaryData);
        Assert.Equal(-1, result.Value);
        var refresh = await handle.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(refresh.Succeeded);
        Assert.Equal(ResourceErrorKind.InvalidResponse, refresh.Failure?.Kind);
    }

    sealed class GatedDemandDefinition(ResourceKey key) : IQueryDefinition<int>, IDisposable
    {
        public QueryReadResult<int> Pending => new(0, 0, false);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        int _reads;
        public QueryReadResult<int> Read(QueryReadContext read)
        {
            int serial = Interlocked.Increment(ref _reads);
            if (serial == 2)
            {
                Entered.TrySetResult();
                Release.Wait(TestContext.Current.CancellationToken);
            }
            return new(serial, 0, true);
        }
        public QueryRequirements Requirements(int value, QueryDemand demand)
            => demand.Active ? new([key], []) : QueryRequirements.Empty;
        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public async Task ParkingDuringProjectionDiscardsItsPublicationAndDoesNotAdmitItsRemoteDemand()
    {
        await using var catalog = new CatalogFixture();
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Absent()));
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        using var definition = new GatedDemandDefinition(new(catalog.Scope, "spotify:track:parked", FacetKind.PlayCount));
        using var queries = new QueryService(catalog.Repository, resources, new LocalReplicas(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, int>(_ => definition);
        using var handle = queries.Acquire(new Spec(catalog.Scope));
        await Until(() => handle.Current.Value == 1);
        var seen = new ConcurrentQueue<int>();
        using var subscription = handle.Changes.Subscribe(Observers.From<QuerySnapshot<int>>(snapshot => seen.Enqueue(snapshot.Value)));
        handle.SetDemand(QueryDemand.Initial);
        try
        {
            await definition.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            handle.SetDemand(QueryDemand.None);
            definition.Release.Set();
            await Until(() => handle.Current.Value >= 3);
            Assert.DoesNotContain(2, seen);
            Assert.Empty(provider.Requests);
            Assert.Empty(queries.GetActiveResourceKeys());
        }
        finally { definition.Release.Set(); }
    }

    [Fact]
    public async Task BlockedProjectionCannotBlockAcquireCurrentDemandSubscribeOrDispose()
    {
        await using var catalog = new CatalogFixture();
        await using var resources = new ResourceCoordinator(catalog.Repository,
            [new QueryTestProvider(_ => throw new InvalidOperationException("No remote demand"))], catalog.Clock);
        using var definition = new BlockedDefinition();
        using var queries = new QueryService(catalog.Repository, resources, new LocalReplicas(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, int>(_ => definition);
        var spec = new Spec(catalog.Scope);
        var acquire = Task.Run(() => queries.Acquire(spec));
        try
        {
            await definition.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var handle = await acquire.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Task.Run(() =>
            {
                Assert.Equal(-1, handle.Current.Value);
                using var second = queries.Acquire(spec);
                using var sub = second.Changes.Subscribe(Observers.From<QuerySnapshot<int>>(_ => { }));
                for (int i = 1; i <= 100; i++) handle.SetDemand(QueryDemand.Initial);
                second.SetDemand(QueryDemand.None);
            }).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            definition.Release.Set();
            await Until(() => handle.Current.Value == 1);
            Assert.Equal(0, definition.Overlaps);
            Assert.InRange(definition.Reads, 1, 2);
        }
        finally { definition.Release.Set(); }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task FiniteReadWaitsForProjectionAndReturnsItsOwnDemandResult(bool active, int expected)
    {
        await using var catalog = new CatalogFixture();
        await using var resources = new ResourceCoordinator(catalog.Repository,
            [new QueryTestProvider(_ => throw new InvalidOperationException("No remote demand"))], catalog.Clock);
        using var definition = new BlockedDefinition();
        using var queries = new QueryService(catalog.Repository, resources, new LocalReplicas(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, int>(_ => definition);
        var read = queries.ReadOnceAsync(new Spec(catalog.Scope), active ? QueryDemand.Initial : QueryDemand.None,
            TestContext.Current.CancellationToken);
        try
        {
            await definition.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(read.IsCompleted);
            definition.Release.Set();
            var result = await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(expected, result.Value);
            Assert.True(result.Revision > 0);
        }
        finally { definition.Release.Set(); }
    }

    static async Task Until(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
