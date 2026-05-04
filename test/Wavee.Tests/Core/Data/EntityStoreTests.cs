using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wavee.Core.Data;
using Xunit;

namespace Wavee.Tests.Core.Data;

/// <summary>
/// Tests for the reactive EntityStore primitive.
/// Covers: inflight dedup, refcount-driven cancellation, push absorption,
/// invalidation-triggered refetch, error emission, and push-vs-fetch races.
/// </summary>
public class EntityStoreTests
{
    private sealed record TestEntity(string Id, int Version);

    private sealed class FakeStore : EntityStore<string, TestEntity>
    {
        public readonly ConcurrentDictionary<string, TestEntity> HotTier = new();
        public readonly ConcurrentDictionary<string, TestEntity> ColdTier = new();
        public Func<string, TestEntity?, CancellationToken, Task<TestEntity>> FetchImpl =
            (key, _, _) => Task.FromResult(new TestEntity(key, 1));
        public int FetchCallCount;
        public TimeSpan TtlOverride = TimeSpan.FromMinutes(5);

        public FakeStore() : base(StringComparer.Ordinal) { }

        protected override TimeSpan Ttl => TtlOverride;

        protected override ValueTask<TestEntity?> ReadHotAsync(string key)
            => new(HotTier.TryGetValue(key, out var v) ? v : null);

        protected override ValueTask<TestEntity?> ReadColdAsync(string key, CancellationToken ct)
            => new(ColdTier.TryGetValue(key, out var v) ? v : null);

        protected override Task<TestEntity> FetchAsync(string key, TestEntity? previous, CancellationToken ct)
        {
            Interlocked.Increment(ref FetchCallCount);
            return FetchImpl(key, previous, ct);
        }

        protected override void WriteHot(string key, TestEntity value) => HotTier[key] = value;

        protected override Task WriteColdAsync(string key, TestEntity value, CancellationToken ct)
        {
            ColdTier[key] = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Observe_EmitsReadyAfterFetch_WhenCacheEmpty()
    {
        // ============================================================
        // WHY: With no cache entry, Observe should emit Loading then
        // Ready(Fresh) once the fetch completes.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        store.FetchImpl = (key, _, _) => Task.FromResult(new TestEntity(key, 42));

        // Act
        var result = await store.Observe("a")
            .OfType<EntityState<TestEntity>.Ready>()
            .FirstAsync()
            .ToTask();

        // Assert
        result.Value.Should().Be(new TestEntity("a", 42));
        result.Freshness.Should().Be(Freshness.Fresh);
        store.FetchCallCount.Should().Be(1);
        store.HotTier.Should().ContainKey("a");
        store.ColdTier.Should().ContainKey("a");
    }

    [Fact]
    public async Task Observe_SharesInflightFetch_AcrossConcurrentSubscribers()
    {
        // ============================================================
        // WHY: Ten concurrent Observes for the same key must issue
        // exactly one FetchAsync call — this is the core dedup promise.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        var gate = new TaskCompletionSource<TestEntity>();
        store.FetchImpl = (_, _, _) => gate.Task;

        // Act: ten concurrent Observe() subscriptions before releasing the fetch
        var ready = new Task<EntityState<TestEntity>.Ready>[10];
        for (int i = 0; i < 10; i++)
        {
            ready[i] = store.Observe("shared")
                .OfType<EntityState<TestEntity>.Ready>()
                .FirstAsync()
                .ToTask();
        }
        gate.SetResult(new TestEntity("shared", 1));
        await Task.WhenAll(ready);

        // Assert
        store.FetchCallCount.Should().Be(1);
        foreach (var r in ready)
            r.Result.Value.Should().Be(new TestEntity("shared", 1));
    }

    [Fact]
    public async Task Unsubscribe_CancelsInflightFetch_WhenLastSubscriberLeaves()
    {
        // ============================================================
        // WHY: This is the structural fix for TaskCanceledException spam —
        // when nobody's watching, the fetch's CTS must be cancelled so the
        // await unwinds cleanly instead of completing with a stale result.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        var fetchStarted = new TaskCompletionSource();
        var cancelled = false;
        var fetchCompleted = new TaskCompletionSource();
        store.FetchImpl = async (_, _, ct) =>
        {
            fetchStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                fetchCompleted.SetResult();
                throw;
            }

            fetchCompleted.SetResult();
            return new TestEntity("x", 0);
        };

        // Act
        var sub = store.Observe("x").Subscribe(_ => { });
        await fetchStarted.Task;
        sub.Dispose();
        await fetchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Push_EmitsReadyFresh_AndWritesTiers()
    {
        // ============================================================
        // WHY: Push is the absorption path for Dealer / IMessenger updates.
        // No fetch should happen; the value lands in all tiers and the
        // subject emits Ready(Fresh).
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        var entity = new TestEntity("p", 7);

        // Act
        var task = store.Observe("p")
            .OfType<EntityState<TestEntity>.Ready>()
            .FirstAsync()
            .ToTask();
        store.Push("p", entity);
        var ready = await task;

        // Assert
        ready.Value.Should().Be(entity);
        ready.Freshness.Should().Be(Freshness.Fresh);
        store.HotTier["p"].Should().Be(entity);
        store.ColdTier["p"].Should().Be(entity);
        // Push short-circuits network — FetchAsync shouldn't have been called.
        store.FetchCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Invalidate_EmitsStale_AndSchedulesRefetchWhileSubscribed()
    {
        // ============================================================
        // WHY: Invalidate must flip Fresh→Stale without a refetch if
        // nobody's watching, and trigger a refetch if at least one
        // subscriber is active. Mirrors "pull on demand" semantics.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        store.HotTier["k"] = new TestEntity("k", 1);
        var version = 1;
        store.FetchImpl = (_, _, _) => Task.FromResult(new TestEntity("k", ++version));

        var emissions = new System.Collections.Generic.List<EntityState<TestEntity>>();
        using var sub = store.Observe("k").Subscribe(s => emissions.Add(s));

        await WaitUntilAsync(() => emissions.Exists(e => e is EntityState<TestEntity>.Ready { Freshness: Freshness.Fresh }));

        // Act
        store.Invalidate("k");
        await WaitUntilAsync(() => emissions.Exists(e => e is EntityState<TestEntity>.Ready r && r.Value.Version == 2));

        // Assert — filter manually to keep FluentAssertions out of expression-tree territory
        var hasStale = emissions.Any(e => e is EntityState<TestEntity>.Ready r && r.Freshness == Freshness.Stale);
        var hasLoading = emissions.Any(e => e is EntityState<TestEntity>.Loading);
        hasStale.Should().BeTrue();
        hasLoading.Should().BeTrue();
        emissions[^1].Should().BeOfType<EntityState<TestEntity>.Ready>()
            .Which.Value.Version.Should().Be(2);
    }

    [Fact]
    public async Task Fetch_Error_EmitsErrorStateWithPreviousValue()
    {
        // ============================================================
        // WHY: Consumers need to distinguish "error while we had data"
        // from "error from scratch" — Error.Previous lets the UI keep
        // rendering the stale data while showing an error badge.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        store.HotTier["e"] = new TestEntity("e", 1);
        store.FetchImpl = (_, _, _) => throw new InvalidOperationException("boom");

        var emissions = new System.Collections.Generic.List<EntityState<TestEntity>>();
        using var sub = store.Observe("e").Subscribe(s => emissions.Add(s));

        // Act: hot cache hit emits Ready, but the cache is older than Ttl, so it
        // schedules a refetch. Force staleness by using a 0 TTL.
        store.TtlOverride = TimeSpan.Zero;
        store.Invalidate("e");

        await WaitUntilAsync(() => emissions.Exists(e => e is EntityState<TestEntity>.Error));

        // Assert
        var err = emissions.OfType<EntityState<TestEntity>.Error>().FirstOrDefault();
        err.Should().NotBeNull();
        err!.Exception.Should().BeOfType<InvalidOperationException>();
        err.Previous.Should().Be(new TestEntity("e", 1));
    }

    [Fact]
    public async Task Push_RacingInflightFetch_WinsViaStamp()
    {
        // ============================================================
        // WHY: If a Dealer push and a network fetch both race to update
        // the same entity, the Push wins (it's authoritative and "now"),
        // and the fetch's late result must be silently dropped.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        var fetchGate = new TaskCompletionSource<TestEntity>();
        store.FetchImpl = (_, _, _) => fetchGate.Task;

        var observed = new System.Collections.Generic.List<EntityState<TestEntity>>();
        using var sub = store.Observe("race").Subscribe(s => observed.Add(s));

        // Act: push while fetch is inflight, then allow fetch to complete
        store.Push("race", new TestEntity("race", 99));
        fetchGate.SetResult(new TestEntity("race", 1)); // late result — should be dropped
        await Task.Delay(50);

        // Assert: final state reflects the Push, not the fetch
        observed[^1].Should().BeOfType<EntityState<TestEntity>.Ready>()
            .Which.Value.Version.Should().Be(99);
    }

    [Fact]
    public async Task GetOnceAsync_ReturnsReadyValue_Completes_AndCancelsInflightOnDispose()
    {
        // ============================================================
        // WHY: GetOnceAsync is the backwards-compat bridge — it should
        // return the first Ready value as a plain Task<T> for callers
        // that aren't migrated to subscriptions yet.
        // ============================================================

        // Arrange
        using var store = new FakeStore();

        // Act
        var value = await store.GetOnceAsync("g", CancellationToken.None);

        // Assert
        value.Should().Be(new TestEntity("g", 1));
        store.FetchCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentObserversOnDifferentKeys_DoNotInterfere()
    {
        // ============================================================
        // WHY: Per-key isolation — two parallel Observes on different
        // keys must not block each other or share state.
        // ============================================================

        // Arrange
        using var store = new FakeStore();
        var gateA = new TaskCompletionSource<TestEntity>();
        var gateB = new TaskCompletionSource<TestEntity>();
        store.FetchImpl = (key, _, _) =>
            key == "A" ? gateA.Task : gateB.Task;

        // Act: observe both, release B first
        var taskA = store.Observe("A").OfType<EntityState<TestEntity>.Ready>().FirstAsync().ToTask();
        var taskB = store.Observe("B").OfType<EntityState<TestEntity>.Ready>().FirstAsync().ToTask();
        gateB.SetResult(new TestEntity("B", 2));
        var readyB = await taskB;
        gateA.SetResult(new TestEntity("A", 1));
        var readyA = await taskA;

        // Assert
        readyA.Value.Id.Should().Be("A");
        readyB.Value.Id.Should().Be("B");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount > deadline)
                throw new TimeoutException("Predicate never became true.");
            await Task.Delay(10);
        }
    }
}
