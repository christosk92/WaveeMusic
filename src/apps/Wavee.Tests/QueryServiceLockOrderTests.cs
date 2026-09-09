using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class QueryServiceLockOrderTests
{
    sealed record Spec(CatalogScope Scope) : QuerySpec<string>(Scope);
    sealed class Definition(CatalogScope scope) : IQueryDefinition<string>
    {
        public readonly ResourceKey Root = new(scope, "spotify:track:lock-root", FacetKind.TrackIdentity);
        public readonly ResourceKey Extra = new(scope, "spotify:track:lock-extra", FacetKind.TrackIdentity);
        public bool IncludeExtra;
        public QueryReadResult<string> Pending => new("", 0, false);
        public QueryReadResult<string> Read(QueryReadContext read)
        {
            var root = read.Read<TrackIdentityValue>(Root);
            return new(root?.Title ?? "", 0, root is not null);
        }
        public QueryRequirements Requirements(string value, QueryDemand demand)
            => new(IncludeExtra ? [Root, Extra] : [Root], []);
    }

    sealed class ReplicaDemand : IQueryReplicaDemand
    {
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReplicaRequest>>([]);
    }

    sealed class Fixture : IAsyncDisposable
    {
        public readonly CatalogFixture Catalog = new();
        public readonly SimpleSubject<bool> Protocol = new(false);
        public readonly Definition Definition;
        public readonly ResourceCoordinator Resources;
        public readonly QueryService Queries;
        public IQueryHandle<string> Handle = null!;
        public object Node = null!, NodeGate = null!;

        public Fixture()
        {
            Definition = new(Catalog.Scope);
            Resources = new(Catalog.Repository, [new QueryTestProvider(request => new(request,
                ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Loaded")))))], Catalog.Clock);
            Queries = new(Catalog.Repository, Resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>(),
                protocolChanges: Protocol);
            Queries.Register<Spec, string>(_ => Definition);
        }

        public async Task Initialize()
        {
            var capture = await Catalog.Repository.CaptureRequestAsync(Definition.Root, ResourcePriority.Visible);
            await Catalog.Repository.AcceptAsync([new(capture,
                ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Loaded"))))]);
            Handle = Queries.Acquire(new Spec(Catalog.Scope));
            Handle.SetDemand(QueryDemand.Initial);
            await Until(() => Handle.Current.Value == "Loaded" && Queries.GetActiveResourceKeys().Contains(Definition.Root));
            // Inspect synchronization objects only: the tests invoke real production retry/replan operations,
            // never production source text, and add no test-only locking hooks to the shipping implementation.
            Node = Handle.GetType().GetField("_node", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Handle)!;
            NodeGate = Node.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Node)!;
        }

        public async ValueTask DisposeAsync()
        {
            Handle?.Dispose();
            Queries.Dispose();
            await Resources.DisposeAsync();
            await Catalog.DisposeAsync();
        }
    }

    static async Task Until(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    static async Task AssertCatalogWaitDoesNotOwnNode(Fixture fixture, Action operation)
    {
        using var catalogHeld = new ManualResetEventSlim();
        using var releaseCatalog = new ManualResetEventSlim();
        using var operationStarted = new ManualResetEventSlim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var holder = Task.Factory.StartNew(() => fixture.Catalog.Repository.ReadConsistent(() =>
        {
            catalogHeld.Set();
            releaseCatalog.Wait(deadline.Token);
            return 0;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            operationStarted.Set();
            try { operation(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true, Name = "query-lock-order-probe" };
        bool workerStarted = false;
        try
        {
            await Until(() => catalogHeld.IsSet);
            worker.Start(); workerStarted = true;
            // The worker has no artificial waits: WaitSleepJoin here is the real production monitor wait.
            // Keep the catalog held until that wait exists, so the assertion is not a sleep-based race.
            await Until(() => operationStarted.IsSet &&
                ((worker.ThreadState & ThreadState.WaitSleepJoin) != 0 || completed.Task.IsCompleted));
            Assert.False(completed.Task.IsCompleted);
            bool enteredNode = Monitor.TryEnter(fixture.NodeGate);
            if (enteredNode) Monitor.Exit(fixture.NodeGate);
            Assert.True(enteredNode,
                "Waiting for the catalog must not retain the node gate: Project holds catalog while acquiring node.");
        }
        finally
        {
            // Never create the actual cyclic wait in a test. Releasing catalog lets even the regressed code
            // finish and dispose, so a failed lock-order assertion cannot strand the rest of the test suite.
            releaseCatalog.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            if (workerStarted) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ProtocolRetryWaitsForCatalogWithoutHoldingTheProjectionGate()
    {
        await using var fixture = new Fixture();
        await fixture.Initialize();
        await AssertCatalogWaitDoesNotOwnNode(fixture, () => fixture.Protocol.OnNext(true));
        Assert.Equal("Loaded", fixture.Handle.Current.Value);
    }

    [Fact]
    public async Task ReplanOfPreviouslyUnobservedKeyWaitsForCatalogBeforeOwningNode()
    {
        await using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Definition.IncludeExtra = true;
        var replan = fixture.Node.GetType().GetMethod("TryReplan", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await AssertCatalogWaitDoesNotOwnNode(fixture, () => Assert.True((bool)replan.Invoke(fixture.Node, null)!));
        await Until(() => fixture.Handle.Current.Demanded.Contains(fixture.Definition.Extra));
    }

    sealed class Value(string title) : IEquatable<Value>
    {
        public string Title { get; } = title;
        public Action? BeforeEquals;
        public bool Equals(Value? other)
        {
            Interlocked.Exchange(ref BeforeEquals, null)?.Invoke();
            return other?.Title == Title;
        }
        public override bool Equals(object? obj) => obj is Value other && Equals(other);
        public override int GetHashCode() => Title.GetHashCode(StringComparison.Ordinal);
    }
    sealed record ValueSpec(CatalogScope Scope) : QuerySpec<Value>(Scope);
    sealed class ValueDefinition(ResourceKey root) : IQueryDefinition<Value>
    {
        public QueryReadResult<Value> Pending => new(new(""), 0, false);
        public QueryReadResult<Value> Read(QueryReadContext read)
        {
            var identity = read.Read<TrackIdentityValue>(root);
            return new(new(identity?.Title ?? ""), 0, identity is not null);
        }
        public QueryRequirements Requirements(Value value, QueryDemand demand) => new([root], []);
    }
    sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public async Task ScopeChangeDuringSnapshotBuildRejectsCandidateWithOldScopeStatus()
    {
        await using var catalog = new CatalogFixture();
        var root = new ResourceKey(catalog.Scope, "spotify:track:scope-race", FacetKind.TrackIdentity);
        async Task Accept(string title)
        {
            var request = await catalog.Repository.CaptureRequestAsync(root, ResourcePriority.Visible);
            await catalog.Repository.AcceptAsync([new(request,
                ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(title))))]);
        }
        await Accept("Original");
        await using var resources = new ResourceCoordinator(catalog.Repository,
            [new QueryTestProvider(_ => throw new InvalidOperationException("Resident data must not fetch."))], catalog.Clock);
        using var queries = new QueryService(catalog.Repository, resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>());
        queries.Register<ValueSpec, Value>(_ => new ValueDefinition(root));
        using var handle = queries.Acquire(new ValueSpec(catalog.Scope));
        await Until(() => handle.Current.Value.Title == "Original");
        var seen = new ConcurrentQueue<QuerySnapshot<Value>>();
        using var subscription = handle.Changes.Subscribe(new Observer<QuerySnapshot<Value>>(seen.Enqueue));
        using var comparing = new ManualResetEventSlim();
        using var releaseComparison = new ManualResetEventSlim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        handle.Current.Value.BeforeEquals = () => { comparing.Set(); releaseComparison.Wait(deadline.Token); };
        try
        {
            await Accept("Changed");
            await Until(() => comparing.IsSet);
            // Value equality is part of the real DTO build, after its scope-status capture and before install.
            // The catalog must remain writable while that build owns only the node gate.
            var scopeB = catalog.Scope with { ProviderAccount = "account-b" };
            await catalog.Repository.SetSessionAsync(scopeB, "account-b", true).WaitAsync(deadline.Token);
        }
        finally { releaseComparison.Set(); }
        await Until(() => handle.Current.Status.Superseded);
        Assert.DoesNotContain(seen, snapshot => snapshot.Value.Title == "Changed" && !snapshot.Status.Superseded);
    }
}
