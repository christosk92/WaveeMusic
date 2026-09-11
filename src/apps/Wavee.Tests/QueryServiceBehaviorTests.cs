using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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

public sealed class QueryServiceBehaviorTests
{
    sealed record Graph(string Title, string? AlbumUri, string AlbumName, string? ArtistUri, string ArtistName);
    sealed record Spec(CatalogScope Scope) : QuerySpec<Graph>(Scope);
    sealed class Definition(CatalogScope scope) : IQueryDefinition<Graph>
    {
        public QueryReadResult<Graph> Pending => new(new("", null, "", null, ""), 0, false);
        public int Reads;
        public ResourceKey Root => new(scope, "spotify:track:root", FacetKind.TrackIdentity);
        public QueryReadResult<Graph> Read(QueryReadContext read)
        {
            Reads++;
            var track = read.Read<TrackIdentityValue>(Root);
            var album = track?.AlbumUri is { } albumUri ? read.Read<AlbumIdentityValue>(new(scope, albumUri, FacetKind.AlbumIdentity)) : null;
            string? artistUri = album?.ArtistUris?.FirstOrDefault();
            var artist = artistUri is not null ? read.Read<ArtistIdentityValue>(new(scope, artistUri, FacetKind.ArtistIdentity)) : null;
            return new(new(track?.Title ?? "", track?.AlbumUri, album?.Name ?? "", artistUri, artist?.Name ?? ""), 0, track is not null);
        }
        public QueryRequirements Requirements(Graph value, QueryDemand demand)
        {
            var keys = new List<ResourceKey> { Root };
            if (value.AlbumUri is { } album) keys.Add(new(scope, album, FacetKind.AlbumIdentity));
            if (value.ArtistUri is { } artist) keys.Add(new(scope, artist, FacetKind.ArtistIdentity));
            return new(keys, []);
        }
    }
    sealed class ReplicaDemand : IQueryReplicaDemand
    {
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReplicaRequest>>(Array.Empty<ReplicaRequest>());
    }
    sealed class Fixture : IAsyncDisposable
    {
        public CatalogFixture Catalog { get; } = new();
        public ResourceCoordinator Resources { get; }
        public QueryService Queries { get; }
        public Definition Definition { get; }
        public QueryTestProvider Provider { get; } = new(request => new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(
            request.Key.Facet switch
            {
                FacetKind.TrackIdentity => new TrackIdentityValue("Song", AlbumUri: "spotify:album:one"),
                FacetKind.AlbumIdentity => new AlbumIdentityValue("Album", ArtistUris: ["spotify:artist:one"]),
                _ => new ArtistIdentityValue("Artist"),
            }))));
        public Fixture()
        {
            Definition = new(Catalog.Scope);
            Resources = new(Catalog.Repository, [Provider], Catalog.Clock);
            Queries = new(Catalog.Repository, Resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>());
            Queries.Register<Spec, Graph>(_ => Definition);
        }
        public async Task Accept(string title)
        {
            var request = await Catalog.Repository.CaptureRequestAsync(Definition.Root, ResourcePriority.Visible);
            await Catalog.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(title))))]);
        }
        public ValueTask DisposeAsync() => DisposeCore();
        async ValueTask DisposeCore() { Queries.Dispose(); await Resources.DisposeAsync(); await Catalog.DisposeAsync(); }
    }

    // ── CatalogRuntime.ProtocolChanges → QueryService replica re-ask (findings: a page's replica demand firing
    // before go-live installs the protocol used to be answered `unserved` once by LibraryQueryDemand and then never
    // asked again — PlaylistDetailQuery/rootlist-only pages stuck on shimmer). These fixtures are pure-replica (no
    // catalog keys) so a wave's outcome is driven entirely by the fake demand port. ────────────────────────────────

    sealed record ProtocolReplicaSpec(CatalogScope Scope) : QuerySpec<bool>(Scope);
    /// <summary>A replica demand whose answer flips on <see cref="Serve"/> — the shape of the real
    /// LibraryQueryDemand's null-vs-installed-session behavior, without a real LibrarySync.</summary>
    sealed class ProtocolAwareReplica : IQueryReplicaDemand, IQueryDefinition<bool>
    {
        public QueryReadResult<bool> Pending => new(false, 0, false);
        public bool Serve, Known;
        public int Calls;
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (!Serve) return Task.FromResult<IReadOnlyList<ReplicaRequest>>(requests.ToArray());
            Known = true;
            return Task.FromResult<IReadOnlyList<ReplicaRequest>>(Array.Empty<ReplicaRequest>());
        }
        public QueryReadResult<bool> Read(QueryReadContext read) => new(Known, 0, Known);
        public QueryRequirements Requirements(bool value, QueryDemand demand) => new([], Known ? [] : [new("rootlist", "rootlist")]);
    }

    sealed record MixedSpec(CatalogScope Scope) : QuerySpec<string>(Scope);
    /// <summary>One catalog key plus one replica request, every wave — proves an unserved replica request cannot
    /// stall the catalog side of the SAME wave.</summary>
    sealed class MixedDefinition(CatalogScope scope) : IQueryDefinition<string>
    {
        public QueryReadResult<string> Pending => new("", 0, false);
        public readonly ResourceKey Root = new(scope, "spotify:track:mixed", FacetKind.TrackIdentity);
        public QueryReadResult<string> Read(QueryReadContext read)
        {
            var track = read.Read<TrackIdentityValue>(Root);
            return new(track?.Title ?? "", 0, track is not null);
        }
        public QueryRequirements Requirements(string value, QueryDemand demand) => new([Root], [new("rootlist", "rootlist")]);
    }
    sealed class AlwaysDeferReplica : IQueryReplicaDemand
    {
        public int Calls;
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult<IReadOnlyList<ReplicaRequest>>(requests.ToArray()); }
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task DeferredReplicaDemandIsReissuedExactlyOnceWhenTheProtocolSubjectPublishesTrue()
    {
        await using var catalog = new CatalogFixture();
        var provider = new QueryTestProvider(request => throw new InvalidOperationException("No metadata expected"));
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        var replica = new ProtocolAwareReplica();
        var protocolChanges = new SimpleSubject<bool>(false);
        using var queries = new QueryService(catalog.Repository, resources, replica, new SimpleEvent<ReplicaChange>(),
            active: null, protocolChanges: protocolChanges);
        queries.Register<ProtocolReplicaSpec, bool>(_ => replica);
        using var handle = queries.Acquire(new ProtocolReplicaSpec(catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);

        // Settles unserved while no protocol is installed (RunDemandAsync's own bounded in-run retry may have tried
        // it more than once — that is an implementation detail, not what this test is about).
        await Until(() => replica.Calls > 0 && !handle.Current.Value);
        int callsBeforePublish = replica.Calls;
        Assert.False(handle.Current.Value);

        replica.Serve = true;
        protocolChanges.OnNext(true);

        await Until(() => handle.Current.Value);
        Assert.Equal(callsBeforePublish + 1, replica.Calls);
    }

    [Fact]
    public async Task AnUnservedReplicaRequestDoesNotStallTheCatalogSideOfTheSameWaveAndTheRunSettles()
    {
        await using var catalog = new CatalogFixture();
        var provider = new QueryTestProvider(request => new(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Loaded")))));
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        var replica = new AlwaysDeferReplica();
        using var queries = new QueryService(catalog.Repository, resources, replica, new SimpleEvent<ReplicaChange>());
        var definition = new MixedDefinition(catalog.Scope);
        queries.Register<MixedSpec, string>(_ => definition);
        using var handle = queries.Acquire(new MixedSpec(catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);

        // Catalog side resolves even though the replica is never served.
        await Until(() => handle.Current.Value == "Loaded");
        int callsAtSettle = replica.Calls;

        // No spin: the run settles rather than retrying the unservable replica forever.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(callsAtSettle, replica.Calls);
    }

    [Fact]
    public async Task PublishingProtocolAvailableWithNoPendingReplicaRequirementReissuesNothing()
    {
        await using var catalog = new CatalogFixture();
        var provider = new QueryTestProvider(request => throw new InvalidOperationException("No metadata expected"));
        await using var resources = new ResourceCoordinator(catalog.Repository, [provider], catalog.Clock);
        var replica = new ProtocolAwareReplica { Serve = true, Known = true };   // already served — nothing pending
        var protocolChanges = new SimpleSubject<bool>(false);
        using var queries = new QueryService(catalog.Repository, resources, replica, new SimpleEvent<ReplicaChange>(),
            active: null, protocolChanges: protocolChanges);
        queries.Register<ProtocolReplicaSpec, bool>(_ => replica);
        using var handle = queries.Acquire(new ProtocolReplicaSpec(catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Value);
        Assert.Equal(0, replica.Calls);

        protocolChanges.OnNext(true);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, replica.Calls);
    }

    sealed record EmptySpec(CatalogScope Scope) : QuerySpec<int>(Scope);
    sealed class EmptyDefinition : IQueryDefinition<int>
    {
        public QueryReadResult<int> Pending => new(0, 0, false);
        public QueryReadResult<int> Read(QueryReadContext read) => new(0, 0, false);
        public QueryRequirements Requirements(int value, QueryDemand demand) => QueryRequirements.Empty;
    }

    // Findings 4.2: unlike Graph/Spec/Definition above (whose Definition closes over ONE fixed scope for every
    // acquire, by design, so other tests can track a single re-read counter), this pair reads the scope named by
    // EACH spec instance — required to prove a re-acquire under the new scope actually fetches.
    sealed record ScopeSpec(CatalogScope Scope) : QuerySpec<string>(Scope);
    sealed class ScopeDefinition(CatalogScope scope) : IQueryDefinition<string>
    {
        public QueryReadResult<string> Pending => new("", 0, false);
        public readonly ResourceKey Root = new(scope, "spotify:track:scoped", FacetKind.TrackIdentity);
        public QueryReadResult<string> Read(QueryReadContext read)
        {
            var track = read.Read<TrackIdentityValue>(Root);
            return new(track?.Title ?? "", 0, track is not null);
        }
        public QueryRequirements Requirements(string value, QueryDemand demand) => new([Root], []);
    }

    [Fact]
    public async Task SessionMoveToAnotherAccountSupersedesTheOldScopeNodeWithoutMarkingItOffline()
    {
        await using var fixture = new Fixture();
        fixture.Queries.Register<ScopeSpec, string>(spec => new ScopeDefinition(spec.Scope));
        using var handleA = fixture.Queries.Acquire(new ScopeSpec(fixture.Catalog.Scope));
        var refreshA = await handleA.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(refreshA.Succeeded);
        Assert.Equal("Song", handleA.Current.Value);
        Assert.False(handleA.Current.Status.Superseded);
        Assert.False(handleA.Current.Status.IsOffline);

        handleA.SetDemand(QueryDemand.Initial);

        var scopeB = fixture.Catalog.Scope with { ProviderAccount = "account-b" };
        await fixture.Catalog.Repository.SetSessionAsync(scopeB, "account-b", true);
        await Until(() => handleA.Current.Status.Superseded);

        // Superseded — the old scope's resources are now stamped Offline by SetSessionCore — but never "offline":
        // this node is about to be replaced by a re-acquire under the new scope, not failing to read a real cache.
        Assert.True(handleA.Current.Status.Superseded);
        Assert.False(handleA.Current.Status.IsOffline);

        fixture.Provider.Requests.Clear();
        using var handleB = fixture.Queries.Acquire(new ScopeSpec(scopeB));
        var refreshB = await handleB.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(refreshB.Succeeded);
        Assert.False(handleB.Current.Status.Superseded);
        Assert.Contains(fixture.Provider.Requests, key => key.Scope == scopeB);
    }

    [Fact]
    public async Task OfflineSessionChangeReachesUnknownQueriesEvenWhenTheCatalogHasNoResidentKeys()
    {
        await using var fixture = new Fixture();
        fixture.Queries.Register<EmptySpec, int>(_ => new EmptyDefinition());
        using var query = fixture.Queries.Acquire(new EmptySpec(fixture.Catalog.Scope));
        Assert.Empty(query.Current.Resources);
        Assert.False(query.Current.Status.IsOffline);
        await fixture.Catalog.Repository.SetSessionAsync(fixture.Catalog.Scope, fixture.Catalog.Scope.ProviderAccount, false);
        await Until(() => query.Current.Status.IsOffline);
        Assert.True(query.Current.Status.IsOffline);
        Assert.NotNull(QueryPresentationRules.InitialFailure(query.Current));
    }

    [Fact]
    public async Task SteadyResidentTrimPreservesActiveDemand_AndLeavesEvictedFactsDurable()
    {
        await using var fixture = new Fixture();
        await fixture.Accept("Pinned");
        using var handle = fixture.Queries.Acquire(new Spec(fixture.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => fixture.Queries.GetActiveResourceKeys().Contains(fixture.Definition.Root));
        var seeds = Enumerable.Range(0, QueryService.ResidentHighWater + 1).Select(index =>
            new CatalogSeed(new(fixture.Catalog.Scope, "spotify:track:inactive" + index, FacetKind.PlayCount),
                new ReplaceFacetPatch(new PlayCountValue(index)))).ToArray();
        await fixture.Catalog.Repository.SeedManyAsync(seeds, fixture.Catalog.Repository.Epoch);
        await fixture.Queries.TrimInactiveAsync();
        Assert.True(fixture.Catalog.Repository.ResidentCount <= QueryService.ResidentTarget);
        Assert.Equal("Pinned", handle.Current.Value.Title);
        Assert.Equal("Pinned", Assert.IsType<TrackIdentityValue>(fixture.Catalog.Repository.Peek(fixture.Definition.Root).Value).Title);
        Assert.Contains(fixture.Definition.Root, fixture.Queries.GetActiveResourceKeys());
        Assert.Equal(seeds.Length + 1, fixture.Catalog.Persistence.Records.Count);
        handle.SetDemand(QueryDemand.None);
        await Until(() => !fixture.Queries.GetActiveResourceKeys().Contains(fixture.Definition.Root));
        Assert.DoesNotContain(fixture.Definition.Root, fixture.Queries.GetActiveResourceKeys());
    }

    [Fact]
    public async Task OfflineColdObservationReportsLoadingUntilItsFiniteDiskReadCompletes()
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Catalog.Persistence.BeforeRead = _ => release.Task;
        await fixture.Catalog.Repository.SetSessionAsync(fixture.Catalog.Scope, fixture.Catalog.Scope.ProviderAccount, false);
        using var handle = fixture.Queries.Acquire(new Spec(fixture.Catalog.Scope));
        await Until(() => handle.Current.Revision > 0);
        Assert.True(handle.Current.Status.IsOffline);
        Assert.True(handle.Current.Status.IsRefreshing);
        Assert.False(handle.Current.Status.HasPrimaryData);
        var read = fixture.Queries.ReadOnceAsync(new Spec(fixture.Catalog.Scope), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(read.IsCompleted);
        release.SetResult();
        var result = await read;
        Assert.True(result.Status.IsOffline);
        Assert.False(result.Status.IsRefreshing);
        Assert.False(result.Status.HasPrimaryData);
        Assert.Empty(fixture.Provider.Requests);
        Assert.NotNull(QueryPresentationRules.InitialFailure(result));
    }

    [Fact]
    public async Task ExplicitRefreshFollowsNewIdentityLinksUntilItsDemandClosureIsComplete()
    {
        await using var f = new Fixture();
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        Assert.Empty(f.Provider.Requests);
        var result = await handle.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal("Song", handle.Current.Value.Title);
        Assert.Equal("Album", handle.Current.Value.AlbumName);
        Assert.Equal("Artist", handle.Current.Value.ArtistName);
        Assert.Equal(new[] { FacetKind.TrackIdentity, FacetKind.AlbumIdentity, FacetKind.ArtistIdentity },
            f.Provider.Requests.Select(key => key.Facet));
    }

    [Fact]
    public async Task NewSubscriberReceivesExactlyOneCurrentReplay_AndAFaultyObserverCannotBlockOthers()
    {
        await using var f = new Fixture();
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        await f.Accept("One"); await f.Accept("Two");
        await Until(() => handle.Current.Value.Title == "Two" && !handle.Current.Status.IsRefreshing);
        var seen = new ConcurrentQueue<QuerySnapshot<Graph>>();
        using var broken = handle.Changes.Subscribe(Observers.From<QuerySnapshot<Graph>>(_ => throw new InvalidOperationException("observer")));
        using var healthy = handle.Changes.Subscribe(Observers.From<QuerySnapshot<Graph>>(seen.Enqueue));
        Assert.Equal("Two", Assert.Single(seen).Value.Title);
        await f.Accept("Three");
        await Until(() => seen.LastOrDefault()?.Value.Title == "Three");
        Assert.Equal(new[] { "Two", "Three" }, seen.Select(snapshot => snapshot.Value.Title));
        var delivered = seen.ToArray();
        Assert.True(delivered[1].Revision > delivered[0].Revision);
    }

    [Fact]
    public async Task ActivityOnlyChangesReuseTheJoinedGraph_WhileStatusStillPublishes()
    {
        await using var f = new Fixture();
        await f.Accept("Song");
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        await Until(() => handle.Current.Value.Title == "Song" && !handle.Current.Status.IsRefreshing);
        var value = handle.Current.Value;
        var facts = handle.Current.Facts;
        long factsRevision = handle.Current.FactsRevision;
        int reads = f.Definition.Reads;
        var request = await f.Catalog.Repository.CaptureRequestAsync(f.Definition.Root, ResourcePriority.Visible);
        await f.Catalog.Repository.SetActivityAsync(f.Definition.Root, request.Stamp, ResourceActivity.Fetching);
        await Until(() => handle.Current.Status.IsRefreshing);
        Assert.True(handle.Current.Status.IsRefreshing);
        Assert.Same(value, handle.Current.Value);
        Assert.Same(facts, handle.Current.Facts);
        Assert.Equal(factsRevision, handle.Current.FactsRevision);
        Assert.Equal(reads, f.Definition.Reads);
        await f.Catalog.Repository.SetActivityAsync(f.Definition.Root, request.Stamp, ResourceActivity.Idle);
        await Until(() => !handle.Current.Status.IsRefreshing);
        Assert.False(handle.Current.Status.IsRefreshing);
        Assert.Same(value, handle.Current.Value);
        Assert.Same(facts, handle.Current.Facts);
        Assert.Equal(factsRevision, handle.Current.FactsRevision);
        Assert.Equal(reads, f.Definition.Reads);
    }

    // Passive leases observe local changes without admitting remote work. UI parking is a binding delivery concern.

    [Fact]
    public async Task APassiveLeaseObservesLocalChangesWithoutRemoteDemand()
    {
        await using var f = new Fixture();
        await f.Accept("Song");
        // A freshly acquired handle's Demand defaults to QueryDemand.None (Active=false) until SetDemand is called.
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        await Until(() => handle.Current.Value.Title == "Song");
        Assert.Equal("Song", handle.Current.Value.Title);
        int reads = f.Definition.Reads;

        await f.Accept("Changed while parked");
        await Until(() => handle.Current.Value.Title == "Changed while parked");
        Assert.True(f.Definition.Reads > reads);
        Assert.Empty(f.Provider.Requests);
    }

    [Fact]
    public async Task APassiveLeaseRetainsItsLatestLocalValueWhenItActivatesDemand()
    {
        await using var f = new Fixture();
        await f.Accept("Song");
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        await Until(() => handle.Current.Value.Title == "Song");
        await f.Accept("Changed while parked");
        await Until(() => handle.Current.Value.Title == "Changed while parked");
        int reads = f.Definition.Reads;

        handle.SetDemand(QueryDemand.Initial); // Active=true: Handle.SetDemand always calls Node.Recompute(project:true)
        await Until(() => f.Definition.Reads > reads);

        Assert.True(f.Definition.Reads > reads);
        Assert.Equal("Changed while parked", handle.Current.Value.Title);
    }

    [Fact]
    public async Task AnActiveLeaseStillRecomputesOnACatalogChangeThatTouchesItsKeys()
    {
        await using var f = new Fixture();
        await f.Accept("Song");
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        await Until(() => handle.Current.Value.Title == "Song");
        int reads = f.Definition.Reads;

        await f.Accept("Changed while active");
        await Until(() => handle.Current.Value.Title == "Changed while active");

        Assert.True(f.Definition.Reads > reads);
        Assert.Equal("Changed while active", handle.Current.Value.Title);
    }

    [Fact]
    public async Task AColdReadFailureClearsWhenThatResourceIsAccepted()
    {
        await using var f = new Fixture();
        f.Catalog.Persistence.BeforeRead = _ => Task.FromException(new IOException("unreadable"));
        using var handle = f.Queries.Acquire(new Spec(f.Catalog.Scope));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = handle.Changes.Subscribe(Observers.From<QuerySnapshot<Graph>>(snapshot =>
        { if (snapshot.Problems.Any(problem => problem.Error?.Kind == ResourceErrorKind.Persistence)) failed.TrySetResult(); }));
        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        f.Catalog.Persistence.BeforeRead = null;
        await f.Accept("Recovered");
        await Until(() => handle.Current.Value.Title == "Recovered");
        Assert.Equal("Recovered", handle.Current.Value.Title);
        Assert.Empty(handle.Current.Problems);
    }

    [Fact]
    public async Task SameAccountConfirm_DoesNotRedemandOrRepublishResidentQuery_AndRetriesOfflineKeyOnceAfterProtocol()
    {
        await using var fixture = new CatalogFixture();
        var resident = new ResourceKey(fixture.Scope, "spotify:track:root", FacetKind.TrackIdentity);
        var album = new ResourceKey(fixture.Scope, "spotify:album:one", FacetKind.AlbumIdentity);
        var artist = new ResourceKey(fixture.Scope, "spotify:artist:one", FacetKind.ArtistIdentity);
        foreach (var (key, value) in new (ResourceKey, CatalogValue)[]
        {
            (resident, new TrackIdentityValue("Song", AlbumUri: "spotify:album:one")),
            (album, new AlbumIdentityValue("Album", ArtistUris: ["spotify:artist:one"])),
            (artist, new ArtistIdentityValue("Artist")),
        })
        {
            var capture = await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
            await fixture.Repository.AcceptAsync([new(capture, ResourceFetchResult.Present(new ReplaceFacetPatch(value)))]);
        }

        var fetches = new List<ResourceKey>();
        var provider = new QueryTestProvider(request =>
        {
            lock (fetches) fetches.Add(request.Key);
            return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(request.Key.Facet switch
            {
                FacetKind.TrackIdentity => new TrackIdentityValue(
                    request.Key.Subject == "spotify:track:scoped" ? "Late" : "Song", AlbumUri: "spotify:album:one"),
                FacetKind.AlbumIdentity => new AlbumIdentityValue("Album", ArtistUris: ["spotify:artist:one"]),
                _ => new ArtistIdentityValue("Artist"),
            })));
        });
        var gate = new ProviderExecutionGate(fixture.Repository.Epoch, ProviderExecutionState.Initializing);
        await using var resources = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock, gate);
        var protocol = new SimpleSubject<bool>(false);
        using var queries = new QueryService(fixture.Repository, resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>(),
            active: null, protocolChanges: protocol);
        queries.Register<Spec, Graph>(_ => new Definition(fixture.Scope));
        queries.Register<ScopeSpec, string>(spec => new ScopeDefinition(spec.Scope));

        using var residentHandle = queries.Acquire(new Spec(fixture.Scope));
        residentHandle.SetDemand(QueryDemand.Initial);
        await Until(() => residentHandle.Current.Status.HasPrimaryData && residentHandle.Current.Value.Title == "Song");
        long revision = residentHandle.Current.Revision;
        lock (fetches) Assert.Empty(fetches);

        using var missingHandle = queries.Acquire(new ScopeSpec(fixture.Scope));
        missingHandle.SetDemand(QueryDemand.Initial);
        await Until(() => missingHandle.Current.Revision > 0);
        lock (fetches) Assert.Empty(fetches);

        await fixture.Repository.SetSessionAsync(fixture.Scope, fixture.Scope.ProviderAccount, true);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(revision, residentHandle.Current.Revision);
        lock (fetches) Assert.Empty(fetches);

        gate.Set(fixture.Repository.Epoch, ProviderExecutionState.Ready);
        protocol.OnNext(true);
        await Until(() => missingHandle.Current.Value == "Late");
        lock (fetches)
        {
            Assert.Equal(1, fetches.Count(key => key.Subject == "spotify:track:scoped"));
            Assert.DoesNotContain(fetches, key => key.Subject == "spotify:track:root");
        }
        Assert.Equal(revision, residentHandle.Current.Revision);
    }
}
