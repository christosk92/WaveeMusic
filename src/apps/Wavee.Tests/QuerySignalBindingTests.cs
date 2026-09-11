using System.Collections.Concurrent;
using System.Diagnostics;
using FluentGpu.Signals;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class QuerySignalBindingTests
{
    [Fact]
    public void PendingSeedDoesNotPublishButDeveloperReceivesDataWithoutAReadinessPredicate()
    {
        var clock = new CatalogClock();
        var posts = new Queue<Action>();
        var delivered = new List<int>();
        using var binding = new QuerySignalBinding<int>(new Handle(), posts.Enqueue,
            published: snapshot => delivered.Add(snapshot.Value), time: clock);
        binding.SetActive(true);
        posts.Dequeue()();
        Assert.Empty(delivered);
        // Past the delivery cooldown the next snapshot is a leading edge again (the cadence itself is pinned by
        // ABurstOfPublicationsCostsOneLeadingAndOneTrailingDelivery below).
        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));
        // Query data availability is not permission to call the page author's publication callback.
        binding.OnNext(new(1, 0, 42, new(false, false, false), []));
        posts.Dequeue()();
        Assert.Equal([42], delivered);
    }

    [Fact]
    public void ABurstOfPublicationsCostsOneLeadingAndOneTrailingDelivery()
    {
        // A cold playlist open republishes ~20 times a second and EVERY delivery re-runs the whole consumer chain
        // (the whole-membership projection, every realized row, the viewport demand). Deliveries therefore coalesce:
        // leading edge immediately, then at most one per DeliveryCooldownMs, latest wins.
        var clock = new CatalogClock();
        var posts = new Queue<Action>();
        var handle = new Handle();
        var delivered = new List<int>();
        using var binding = new QuerySignalBinding<int>(handle, posts.Enqueue, s => delivered.Add(s.Value), time: clock);
        binding.SetActive(true);
        posts.Dequeue()();                                   // the activation's own delivery (the revision-0 seed)
        Assert.Empty(delivered);
        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));

        handle.Emit(1);                                      // a quiet window: this one does not wait
        Assert.Single(posts);
        posts.Dequeue()();
        Assert.Equal([1], delivered);

        for (int revision = 2; revision <= 21; revision++)   // 20 more snapshots inside 10 ms
        {
            handle.Emit(revision);
            clock.Advance(TimeSpan.FromMilliseconds(0.5));
        }
        Assert.Empty(posts);                                 // not one extra UI delivery while they keep arriving
        Assert.Equal([1], delivered);

        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));   // the trailing edge
        Assert.Single(posts);
        posts.Dequeue()();
        Assert.Equal([1, 21], delivered);                    // two deliveries for 21 snapshots; the last is the latest
        Assert.Equal(21, binding.Snapshot.Peek().Value);
    }

    [Fact]
    public void AFailureNeverWaitsForTheDeliveryCooldown()
    {
        var clock = new CatalogClock();
        var posts = new Queue<Action>();
        var handle = new Handle();
        var failures = new List<Exception>();
        using var binding = new QuerySignalBinding<int>(handle, posts.Enqueue, failed: failures.Add, time: clock);
        binding.SetActive(true);
        posts.Dequeue()();
        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));
        handle.Emit(1);
        posts.Dequeue()();                                   // a delivery just landed: the window is open
        var error = new InvalidOperationException("subscription ended");
        handle.Fail(error);
        Assert.Single(posts);                                // ...and the failure still posts straight away
        posts.Dequeue()();
        Assert.Same(error, Assert.Single(failures));
    }

    [Fact]
    public void ActivityPublicationUpdatesResourcesWithoutInvalidatingFacts()
    {
        var clock = new CatalogClock();
        var posts = new Queue<Action>();
        using var binding = new QuerySignalBinding<int>(new Handle(), posts.Enqueue, time: clock);
        var runtime = new ReactiveRuntime();
        int factReads = 0, statusReads = 0;
        using var factsEffect = new Effect(runtime, () => { _ = binding.Facts.Value; factReads++; });
        using var statusEffect = new Effect(runtime, () => { _ = binding.Resources.Value; statusReads++; });
        binding.SetActive(true);
        var scope = new CatalogScope("spotify", "a", "en", "NL", "premium", 1, false);
        var key = new ResourceKey(scope, "spotify:track:a", FacetKind.PlayCount);
        var present = ResourceSnapshot.Unknown(key) with { Knowledge = Knowledge.Present, Value = new PlayCountValue(0) };
        IReadOnlyDictionary<ResourceKey, ResourceSnapshot> resources = new Dictionary<ResourceKey, ResourceSnapshot> { [key] = present };
        var facts = FactsView.From(QueryFacts.Empty, resources);
        binding.OnNext(new(1, 0, 1, new(true, false, false), []) { Resources = resources, Facts = facts, FactsRevision = 1 });
        posts.Dequeue()(); runtime.Flush();
        Assert.Equal(2, factReads); Assert.Equal(2, statusReads);
        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));   // past the delivery cooldown
        resources = new Dictionary<ResourceKey, ResourceSnapshot> { [key] = present with { Activity = ResourceActivity.Fetching } };
        binding.OnNext(new(2, 0, 1, new(true, true, false), [])
            { Resources = resources, Facts = FactsView.From(facts, resources), FactsRevision = 1 });
        posts.Dequeue()(); runtime.Flush();
        Assert.Equal(2, factReads); Assert.Equal(3, statusReads);
        Assert.Same(facts, binding.Facts.Peek());
        Assert.Equal(ResourceActivity.Fetching, binding.Resources.Peek()[key].Activity);
    }

    [Fact]
    public void Bursts_publish_only_the_latest_snapshot_on_the_ui_post()
    {
        var posts = new Queue<Action>();
        var handle = new Handle();
        var delivered = new List<int>();
        using var binding = new QuerySignalBinding<int>(handle, posts.Enqueue, s => delivered.Add(s.Value));
        Assert.Empty(posts); // passive acquisition and replay do not start UI work or demand
        binding.SetActive(true);
        handle.Emit(1);
        handle.Emit(2);
        Assert.Single(posts);
        Assert.Empty(delivered);
        posts.Dequeue()();
        Assert.Equal([2], delivered);
        Assert.Equal(2, binding.Snapshot.Peek().Value);
    }

    [Fact]
    public void Parking_drops_queued_delivery_and_replays_latest_with_saved_window_on_activation()
    {
        var posts = new Queue<Action>();
        var handle = new Handle();
        var delivered = new List<int>();
        using var binding = new QuerySignalBinding<int>(handle, posts.Enqueue, s => delivered.Add(s.Value));
        var demand = new QueryDemand(true, QueryPriority.Visible, []);
        binding.SetDemand(demand);
        binding.SetActive(true);
        binding.SetActive(false);
        handle.Emit(1);
        handle.Emit(2);
        Assert.Equal(QueryDemand.None, handle.Demand);
        posts.Dequeue()();
        Assert.Empty(posts);
        Assert.Empty(delivered);
        binding.SetActive(true);
        Assert.Equal(demand, handle.Demand);
        Assert.Single(posts);
        posts.Dequeue()();
        Assert.Equal([2], delivered);
    }

    [Fact]
    public void Disposing_invalidates_old_route_posts_and_detaches_the_handle()
    {
        var posts = new Queue<Action>();
        var old = new Handle();
        var delivered = new List<int>();
        var binding = new QuerySignalBinding<int>(old, posts.Enqueue, s => delivered.Add(s.Value));
        binding.SetActive(true);
        old.Emit(1);
        binding.Dispose();
        posts.Dequeue()();
        Assert.Empty(delivered);
        Assert.True(old.Disposed);
        Assert.False(old.Subscribed);
        Assert.Equal(QueryDemand.None, old.Demand);
        old.Emit(2);
        Assert.Empty(posts);
    }

    [Fact]
    public void TerminalObserverErrorIsDeliveredOnUiWithoutInventingEmptyData_AndObeysParkLifetime()
    {
        var posts = new Queue<Action>();
        var handle = new Handle();
        var failures = new List<Exception>();
        using var binding = new QuerySignalBinding<int>(handle, posts.Enqueue, failed: failures.Add);
        binding.SetActive(true); handle.Emit(7); posts.Dequeue()();
        binding.SetActive(false);
        var error = new InvalidOperationException("subscription ended");
        handle.Fail(error);
        Assert.Empty(posts); Assert.Null(binding.Failure.Peek());
        binding.SetActive(true);
        Assert.Empty(failures);
        posts.Dequeue()();
        Assert.Equal(7, binding.Snapshot.Peek().Value);
        Assert.Same(error, binding.Failure.Peek());
        Assert.Same(error, Assert.Single(failures));
    }

    [Fact]
    public void InitialPresentationDistinguishesUnknownEmptyOfflineAndOptionalFailure()
    {
        var unknown = new QuerySnapshot<int[]>(1, 0, [], new(false, false, false), []);
        Assert.Null(QueryPresentationRules.InitialFailure(unknown));
        var offline = unknown with { Status = new(false, false, true) };
        Assert.NotNull(QueryPresentationRules.InitialFailure(offline));
        Assert.Null(QueryPresentationRules.InitialFailure(offline with { Status = new(false, true, true) }));
        var scope = new CatalogScope("spotify", "a", "en", "NL", "premium", 1, false);
        var readyEmpty = unknown with { Status = new(true, false, false), Problems =
            [new(new(scope, "spotify:track:x", FacetKind.PlayCount), Knowledge.Unknown,
                new(ResourceErrorKind.Transport, "optional count failed"))] };
        Assert.Null(QueryPresentationRules.InitialFailure(readyEmpty));
        var missing = unknown with { Problems = [new(new(scope, "home", FacetKind.Home), Knowledge.Absent, null)] };
        Assert.NotNull(QueryPresentationRules.InitialFailure(missing));
    }

    [Fact]
    public void SupersededIsNeverAFailure_AndConnectingSuppressesTheOfflineVerdict()
    {
        // Findings 4.2: a node whose resources were read under a scope the session has since left is about to be
        // replaced by a re-acquire under the new scope — never a failure, even though SetSessionCore also stamps
        // its resources Offline (Superseded must win over that).
        var unknown = new QuerySnapshot<int[]>(1, 0, [], new(false, false, false), []);
        var superseded = unknown with { Status = new(false, false, true, Superseded: true) };
        Assert.Null(QueryPresentationRules.InitialFailure(superseded));

        // Findings 4.2: the pre-login restored deep link starts offline before any resume attempt lands — render
        // the shell's own "Connecting…" state, not a hard failure, while that race is still open.
        var offline = unknown with { Status = new(false, false, true) };
        Assert.Null(QueryPresentationRules.InitialFailure(offline, connecting: true));
        Assert.NotNull(QueryPresentationRules.InitialFailure(offline, connecting: false));
    }

    sealed class Handle : IQueryHandle<int>, IObservable<QuerySnapshot<int>>
    {
        IObserver<QuerySnapshot<int>>? _observer;
        public QuerySnapshot<int> Current { get; private set; } = Snapshot(0);
        public IObservable<QuerySnapshot<int>> Changes => this;
        public QueryDemand Demand = QueryDemand.None;
        public bool Disposed;
        public bool Subscribed => _observer is not null;
        public void SetDemand(QueryDemand demand) => Demand = demand;
        public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default) => new(new RefreshResult([]));
        public IDisposable Subscribe(IObserver<QuerySnapshot<int>> observer)
        {
            _observer = observer;
            observer.OnNext(Current);
            return new Subscription(() => _observer = null);
        }
        public void Emit(int value) { Current = Snapshot(value); _observer?.OnNext(Current); }
        public void Fail(Exception error) => _observer?.OnError(error);
        public void Dispose() => Disposed = true;
        static QuerySnapshot<int> Snapshot(int value) => new(value, 0, value, new(true, false, false), []);
    }

    sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

/// <summary>
/// <see cref="QuerySignalBinding{T, TModel}"/>: the projecting sibling. Its whole point is moving the (expensive,
/// pure) mapping off the UI thread — these tests pin that projection runs on a serialized pool worker
/// (never inside the posted delivery), that a status-only republication (same Value reference) reuses the previous
/// model instead of re-running the mapping, that coalescing still delivers only the latest model, and that a
/// throwing projection is routed to `failed` rather than escaping into the publisher.
/// </summary>
public sealed class ProjectingQuerySignalBindingTests
{
    // A reference type so ReferenceEquals(Value, ...) is a meaningful identity check (an int would box every read).
    sealed class Payload(int n) { public int N = n; }

    // Posts arrive from the pool worker that owns the projection loop; a test drains them on its own thread once
    // they land, running each posted delivery in order until the condition it is waiting for holds.
    sealed class Posts
    {
        readonly ConcurrentQueue<Action> _queue = new();
        public void Enqueue(Action action) => _queue.Enqueue(action);
        public void PumpUntil(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the awaited delivery never arrived");
                if (_queue.TryDequeue(out var post)) post(); else Thread.Sleep(1);
            }
        }
    }

    [Fact]
    public void ProjectionRunsOffTheCallersThread_AndNeverInsideThePostedDelivery()
    {
        var posts = new Posts();
        var handle = new Handle();
        var projectedOn = new ConcurrentBag<int>();
        var deliveredOn = new List<int>();
        using var binding = new QuerySignalBinding<Payload, int>(handle, posts.Enqueue,
            project: p => { projectedOn.Add(Environment.CurrentManagedThreadId); return p.N; },
            published: (_, _) => deliveredOn.Add(Environment.CurrentManagedThreadId));
        int caller = Environment.CurrentManagedThreadId;
        binding.SetActive(true);
        handle.Emit(new Payload(1));
        posts.PumpUntil(() => binding.Snapshot.Peek().Value.N == 1);
        Assert.NotEmpty(projectedOn);
        Assert.DoesNotContain(caller, projectedOn);          // the seed replay and the emit both projected on the pool
        Assert.All(deliveredOn, thread => Assert.Equal(caller, thread)); // delivery is the posted (UI) side
    }

    [Fact]
    public void TwoPublicationsWithTheSameValueReferenceProjectOnce()
    {
        var posts = new Posts();
        var handle = new Handle();
        int projections = 0;
        var models = new List<int>();
        using var binding = new QuerySignalBinding<Payload, int>(handle, posts.Enqueue,
            project: p => { Interlocked.Increment(ref projections); return p.N; },
            published: (_, model) => models.Add(model));
        binding.SetActive(true);
        var payload = new Payload(5);
        handle.Emit(payload);
        posts.PumpUntil(() => models.Contains(5));
        int projectedSoFar = Volatile.Read(ref projections);
        handle.Emit(payload); // a status-only republication: the same Value reference again
        posts.PumpUntil(() => models.Count(model => model == 5) == 2);
        Assert.Equal(projectedSoFar, Volatile.Read(ref projections)); // delivered again, projected zero more times
    }

    [Fact]
    public void OverSnapshot_ReprojectsWhenTheFactsRevisionMoves_ButNotForAStatusOnlyRepublication()
    {
        // The detail model folds the publication's video-association facts in, so its projection is identified by
        // (value reference, facts revision) — a same-value republication that carries NEW knowledge must re-map,
        // and one that carries the same knowledge must not.
        var posts = new Posts();
        var handle = new Handle();
        int projections = 0;
        var models = new List<int>();
        using var binding = QuerySignalBinding<Payload, int>.OverSnapshot(handle, posts.Enqueue,
            project: snapshot => { Interlocked.Increment(ref projections); return snapshot.Value.N; },
            published: (_, model) => models.Add(model));
        binding.SetActive(true);
        var payload = new Payload(5);
        handle.Emit(payload, factsRevision: 1);
        posts.PumpUntil(() => models.Count == 1);
        Assert.Equal(1, Volatile.Read(ref projections));
        Assert.Equal(1, binding.FactsRevision.Peek());

        handle.Emit(payload, factsRevision: 1);              // status only: same value, same knowledge
        posts.PumpUntil(() => models.Count == 2);
        Assert.Equal(1, Volatile.Read(ref projections));

        handle.Emit(payload, factsRevision: 2);              // same value, a fact landed
        posts.PumpUntil(() => models.Count == 3);
        Assert.Equal(2, Volatile.Read(ref projections));
        Assert.Equal(2, binding.FactsRevision.Peek());
    }

    [Fact]
    public void ANewValueReferenceReprojects()
    {
        var posts = new Posts();
        var handle = new Handle();
        int projections = 0;
        using var binding = new QuerySignalBinding<Payload, int>(handle, posts.Enqueue,
            project: p => { Interlocked.Increment(ref projections); return p.N; });
        binding.SetActive(true);
        handle.Emit(new Payload(1));
        posts.PumpUntil(() => binding.Snapshot.Peek().Value.N == 1);
        int afterFirst = Volatile.Read(ref projections);
        handle.Emit(new Payload(2)); // a distinct instance
        posts.PumpUntil(() => binding.Snapshot.Peek().Value.N == 2);
        Assert.Equal(afterFirst + 1, Volatile.Read(ref projections));
    }

    [Fact]
    public void SnapshotsSupersededWhileOneIsProjectingAreSkippedAndOnlyTheLatestIsDelivered()
    {
        var posts = new Posts();
        var handle = new Handle();
        var delivered = new List<int>();
        var projected = new ConcurrentQueue<int>();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var binding = new QuerySignalBinding<Payload, int>(handle, posts.Enqueue,
            project: p =>
            {
                if (p.N == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
                projected.Enqueue(p.N);
                return p.N;
            },
            published: (_, model) => delivered.Add(model));
        binding.SetActive(true);
        handle.Emit(new Payload(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));  // the pool is inside the projection of 1
        handle.Emit(new Payload(2));
        handle.Emit(new Payload(3));                          // supersedes 2 before any worker sees it
        release.Set();
        posts.PumpUntil(() => delivered.Contains(3));
        Assert.DoesNotContain(2, projected);                  // 2 was never projected, let alone delivered
        Assert.DoesNotContain(2, delivered);
        Assert.Equal(3, delivered[^1]);
    }

    [Fact]
    public void AThrowingProjectionReachesFailed_NotThePublisher()
    {
        var posts = new Posts();
        var handle = new Handle();
        var published = new List<int>();
        var failures = new List<Exception>();
        var thrown = new InvalidOperationException("bad model");
        using var binding = new QuerySignalBinding<Payload, int>(handle, posts.Enqueue,
            project: p => p.N == 0 ? 0 : throw thrown,
            published: (_, model) => published.Add(model), failed: failures.Add);
        binding.SetActive(true);
        handle.Emit(new Payload(1));
        posts.PumpUntil(() => failures.Count > 0);
        Assert.DoesNotContain(1, published);                  // the publisher never sees a model from a throwing projection
        Assert.Same(thrown, Assert.Single(failures));
        Assert.Same(thrown, binding.Failure.Peek());
    }

    // A reference-type IQueryHandle<Payload> mirroring QuerySignalBindingTests.Handle<int>, but over a real
    // reference-typed Value so the projecting binding's reference-equality dedup is actually exercised.
    sealed class Handle : IQueryHandle<Payload>, IObservable<QuerySnapshot<Payload>>
    {
        IObserver<QuerySnapshot<Payload>>? _observer;
        public QuerySnapshot<Payload> Current { get; private set; } = Snapshot(new Payload(0));
        public IObservable<QuerySnapshot<Payload>> Changes => this;
        public QueryDemand Demand = QueryDemand.None;
        public void SetDemand(QueryDemand demand) => Demand = demand;
        public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default) => new(new RefreshResult([]));
        public IDisposable Subscribe(IObserver<QuerySnapshot<Payload>> observer)
        {
            _observer = observer;
            observer.OnNext(Current);
            return new Subscription(() => _observer = null);
        }
        long _revision;
        public void Emit(Payload value) { Current = Snapshot(value); _observer?.OnNext(Current); }
        /// <summary>A republication of the SAME payload carrying its own facts revision (the projecting binding's
        /// second identity axis). The revision always advances so nothing is dropped as stale.</summary>
        public void Emit(Payload value, long factsRevision)
        {
            Current = Snapshot(value) with { Revision = ++_revision + value.N, FactsRevision = factsRevision };
            _observer?.OnNext(Current);
        }
        public void Dispose() { }
        static QuerySnapshot<Payload> Snapshot(Payload value) => new(value.N, 0, value, new(true, false, false), []);
    }

    sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
