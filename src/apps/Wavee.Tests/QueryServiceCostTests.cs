using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>What a whole-model demand is allowed to cost: a 1500-member playlist whose join reads every member
/// and whose demand recipe names every identity. Installing that demand re-PLANS once and never re-JOINS, and a
/// later catalog wave only materializes the keys that actually changed.</summary>
public sealed class QueryServiceCostTests
{
    const int Members = 1500;

    sealed record Roster(IReadOnlyList<string> Titles);
    sealed record RosterSpec(CatalogScope Scope) : QuerySpec<Roster>(Scope);

    sealed class RosterDefinition(CatalogScope scope) : IQueryDefinition<Roster>
    {
        int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public QueryReadResult<Roster> Pending => new(new([]), 0, false);
        public ResourceKey Key(int index) => new(scope, "spotify:track:" + index, FacetKind.TrackIdentity);
        public QueryReadResult<Roster> Read(QueryReadContext read)
        {
            Interlocked.Increment(ref _reads);
            var titles = new string[Members];
            for (int i = 0; i < Members; i++) titles[i] = read.Read<TrackIdentityValue>(Key(i))?.Title ?? "";
            return new(new(titles), 0, titles[0].Length > 0);
        }
        public QueryRequirements Requirements(Roster value, QueryDemand demand)
            => demand.Active
                ? new(Enumerable.Range(0, Members).Select(Key).ToArray(), [])
                : QueryRequirements.Empty;
    }

    sealed class NoReplicas : IQueryReplicaDemand
    {
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<ReplicaRequest>>([]);
    }

    sealed class Fixture : IAsyncDisposable
    {
        public CatalogFixture Catalog { get; } = new();
        public ResourceCoordinator Resources { get; }
        public QueryService Queries { get; }
        public RosterDefinition Definition { get; }
        public QueryTestProvider Provider { get; } = new(request =>
            new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Fetched")))));
        public Fixture()
        {
            Definition = new(Catalog.Scope);
            Resources = new(Catalog.Repository, [Provider], Catalog.Clock);
            Queries = new(Catalog.Repository, Resources, new NoReplicas(), new SimpleEvent<ReplicaChange>());
            Queries.Register<RosterSpec, Roster>(_ => Definition);
        }
        public async Task SeedAsync()
        {
            var keys = Enumerable.Range(0, Members).Select(Definition.Key).ToArray();
            var requests = await Catalog.Repository.CaptureRequestsAsync(keys, ResourcePriority.Visible);
            await Catalog.Repository.AcceptAsync(requests.Select(request => new ResourceResponse(request,
                ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(request.Key.Subject))))).ToArray());
        }
        public async Task AcceptAsync(ResourceKey key, string title)
        {
            var request = await Catalog.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
            await Catalog.Repository.AcceptAsync([new(request,
                ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(title))))]);
        }
        public ValueTask DisposeAsync() => DisposeCore();
        async ValueTask DisposeCore() { Queries.Dispose(); await Resources.DisposeAsync(); await Catalog.DisposeAsync(); }
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task AWholeModelDemandRunsReadOnce()
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        using var handle = f.Queries.Acquire(new RosterSpec(f.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Demanded.Count == Members && !handle.Current.Status.IsRefreshing);
        int reads = f.Definition.Reads;
        var value = handle.Current.Value;
        var resources = handle.Current.Resources;

        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Demanded.Count == Members && !handle.Current.Status.IsRefreshing);

        Assert.Equal(reads, f.Definition.Reads);
        Assert.Same(value, handle.Current.Value);
        Assert.Same(resources, handle.Current.Resources);
        Assert.Equal(f.Definition.Key(0), handle.Current.Demanded[0]);
        Assert.Equal(f.Definition.Key(Members - 1), handle.Current.Demanded[Members - 1]);
        Assert.Empty(f.Provider.Requests);
    }

    [Fact]
    public async Task AFetchWaveMaterializesOnlyChangedKeysOnTheNextJoin()
    {
        await using var f = new Fixture();
        using var handle = f.Queries.Acquire(new RosterSpec(f.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Demanded.Count == Members);
        Assert.Equal(f.Definition.Key(0), handle.Current.Demanded[0]);
        Assert.Equal(f.Definition.Key(Members - 1), handle.Current.Demanded[Members - 1]);
        await Until(() => handle.Current.Value.Titles.Count == Members
            && handle.Current.Value.Titles.All(title => title == "Fetched")
            && !handle.Current.Status.IsRefreshing);
        int reads = f.Definition.Reads;
        var before = handle.Current.Value;
        var beforeResources = handle.Current.Resources;

        await f.AcceptAsync(f.Definition.Key(7), "Renamed");
        await Until(() => handle.Current.Value.Titles.Count == Members && handle.Current.Value.Titles[7] == "Renamed");

        Assert.Equal(reads + 1, f.Definition.Reads);
        Assert.NotSame(before, handle.Current.Value);
        Assert.Equal("Renamed", handle.Current.Value.Titles[7]);
        Assert.Equal("Fetched", handle.Current.Value.Titles[0]);
        Assert.Equal("Fetched", handle.Current.Value.Titles[8]);
        Assert.Same(beforeResources[f.Definition.Key(0)], handle.Current.Resources[f.Definition.Key(0)]);
        Assert.Same(beforeResources[f.Definition.Key(8)], handle.Current.Resources[f.Definition.Key(8)]);
        Assert.NotSame(beforeResources[f.Definition.Key(7)], handle.Current.Resources[f.Definition.Key(7)]);
    }

    [Fact]
    public async Task ActivityChurnThatDoesNotMoveStatusPublishesNothingAtAll()
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        using var handle = f.Queries.Acquire(new RosterSpec(f.Catalog.Scope));
        await Until(() => handle.Current.Value.Titles.Count == Members && !handle.Current.Status.IsRefreshing);
        var key = f.Definition.Key(0);

        var request = await f.Catalog.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        await f.Catalog.Repository.SetActivityAsync(key, request.Stamp, ResourceActivity.Queued);
        await Until(() => handle.Current.Status.IsRefreshing);
        var queued = handle.Current;
        int reads = f.Definition.Reads;

        await f.Catalog.Repository.SetActivityAsync(key, request.Stamp, ResourceActivity.Fetching);
        await f.Catalog.Repository.SetActivityAsync(key, request.Stamp, ResourceActivity.Idle);
        await Until(() => !handle.Current.Status.IsRefreshing);

        Assert.Equal(queued.Revision + 1, handle.Current.Revision);
        Assert.Same(queued.Value, handle.Current.Value);
        Assert.Same(queued.Resources, handle.Current.Resources);
        Assert.Same(queued.Facts, handle.Current.Facts);
        Assert.Equal(queued.FactsRevision, handle.Current.FactsRevision);
        Assert.Equal(reads, f.Definition.Reads);
    }

    [Fact]
    public async Task ADurableChangeToADemandedKeyStillRejoinsAndPublishes()
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        using var handle = f.Queries.Acquire(new RosterSpec(f.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Demanded.Count == Members && !handle.Current.Status.IsRefreshing);
        int reads = f.Definition.Reads;
        var resources = handle.Current.Resources;

        await f.AcceptAsync(f.Definition.Key(3), "Renamed");
        await Until(() => handle.Current.Value.Titles[3] == "Renamed");

        Assert.True(f.Definition.Reads > reads);
        Assert.NotSame(resources, handle.Current.Resources);
        Assert.Equal(Knowledge.Present, handle.Current.Resources[f.Definition.Key(3)].Knowledge);
    }

    [Fact]
    public async Task AMissingKeyPeeksToTheSameUnknownSnapshot()
    {
        await using var catalog = new CatalogFixture();
        var key = catalog.Key("never-fetched");

        var first = catalog.Repository.Peek(key);
        var second = catalog.Repository.Peek(key);
        Assert.Same(first, second);
        Assert.Equal(Knowledge.Unknown, first.Knowledge);

        await catalog.AcceptCountAsync(key, 42);
        var seeded = catalog.Repository.Peek(key);
        Assert.Equal(Knowledge.Present, seeded.Knowledge);
        Assert.NotSame(first, seeded);
    }
}
