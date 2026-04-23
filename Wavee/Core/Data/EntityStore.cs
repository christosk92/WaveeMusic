using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Data;

// Reactive entity-store primitive. Per-key cache with:
//   • 3-tier read flow (hot → cold → network) subclasses customize via the
//     abstract Read/Fetch/Write hooks.
//   • Inflight dedup: concurrent subscribers for the same key share one fetch.
//   • Refcounted subscription: fetch is cancelled when the last subscriber
//     disposes. The state subject stays warm so re-subscription is instant.
//   • Push absorption: external events (Dealer, IMessenger) call Push() to
//     seed the stream without a network round-trip.
//
// Consumers MUST apply their own ObserveOn() if they need a specific thread;
// the store emits on whichever thread completed a fetch or invoked Push/Invalidate.
public abstract class EntityStore<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : class
{
    private sealed class Slot
    {
        public readonly object Gate = new();
        public readonly BehaviorSubject<EntityState<TValue>> Subject =
            new(new EntityState<TValue>.Initial());
        public int RefCount;
        public CancellationTokenSource? Cts;
        public Task? InFlight;
        public long StampTicks; // monotonic counter of state updates; guards against out-of-order fetches
        public bool ColdProbed;
    }

    private readonly ConcurrentDictionary<TKey, Slot> _slots;
    private readonly ILogger? _logger;
    private long _stampSeq;
    private volatile bool _disposed;

    protected EntityStore(IEqualityComparer<TKey>? comparer = null, ILogger? logger = null)
    {
        _slots = comparer is null
            ? new ConcurrentDictionary<TKey, Slot>()
            : new ConcurrentDictionary<TKey, Slot>(comparer);
        _logger = logger;
    }

    // ── Subclass hooks ──────────────────────────────────────────────────

    // How long a Ready(Fresh) value remains fresh before a background refetch
    // is scheduled on the next Observe() or on Invalidate().
    protected abstract TimeSpan Ttl { get; }

    // In-memory tier (HotCache<T> typically). Synchronous-friendly; no cancellation.
    protected abstract ValueTask<TValue?> ReadHotAsync(TKey key);

    // Cold tier (SQLite typically). Should be cancellation-aware.
    protected abstract ValueTask<TValue?> ReadColdAsync(TKey key, CancellationToken ct);

    // Network / authoritative tier. `previous` is whatever we already have
    // cached (null if none) so the implementation can do conditional / diff fetches.
    protected abstract Task<TValue> FetchAsync(TKey key, TValue? previous, CancellationToken ct);

    // Writes after a successful fetch or Push.
    protected abstract void WriteHot(TKey key, TValue value);
    protected abstract Task WriteColdAsync(TKey key, TValue value, CancellationToken ct);

    // ── Public API ──────────────────────────────────────────────────────

    public IObservable<EntityState<TValue>> Observe(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Observable.Create<EntityState<TValue>>(observer =>
        {
            var slot = _slots.GetOrAdd(key, _ => new Slot());
            Interlocked.Increment(ref slot.RefCount);

            // BehaviorSubject replays last state to the new subscriber synchronously.
            var inner = slot.Subject.Subscribe(observer);

            // Kick off materialization (idempotent — only fetches if needed).
            _ = MaterializeAsync(key, slot);

            return Disposable.Create(() =>
            {
                inner.Dispose();
                // Intentionally do NOT cancel slot.Cts on refcount==0. A naive
                // cancel-on-unsubscribe throws OperationCanceledException through
                // every CT linked to slot.Cts (HTTP client pipelines register
                // several), and on Debug + VS that adds real CPU per cancellation.
                // Letting the fetch complete populates the cache; if the slot has
                // no observers by then, the OnNext is a no-op. Dispose() still
                // cancels everything on genuine store teardown.
                Interlocked.Decrement(ref slot.RefCount);
            });
        });
    }

    public Task<TValue> GetOnceAsync(TKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Intentionally ignoring ct at the Rx-to-Task boundary. Passing ct to
        // ToTask(ct) caused one OperationCanceledException throw per cancelled
        // caller — adds first-chance exception noise without benefit, since
        // the underlying fetch always completes to the cache anyway. Callers
        // that need cancellation semantics check ct themselves after awaiting.
        return Observe(key)
            .Where(s => s is EntityState<TValue>.Ready or EntityState<TValue>.Error)
            .Select(s => s switch
            {
                EntityState<TValue>.Ready r => r.Value,
                EntityState<TValue>.Error e => throw e.Exception,
                _ => throw new InvalidOperationException()
            })
            .FirstAsync()
            .ToTask();
    }

    public void Push(TKey key, TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = _slots.GetOrAdd(key, _ => new Slot());
        var stamp = Interlocked.Increment(ref _stampSeq);

        lock (slot.Gate)
        {
            slot.StampTicks = stamp;
            CancelInflightUnderLock(slot); // outrun any in-progress fetch
            slot.ColdProbed = true;
        }

        try { WriteHot(key, value); }
        catch (Exception ex) { _logger?.LogDebug(ex, "WriteHot failed on push for key {Key}", key); }

        _ = WriteColdSafeAsync(key, value);

        slot.Subject.OnNext(new EntityState<TValue>.Ready(value, DateTimeOffset.UtcNow, Freshness.Fresh));
    }

    public void Invalidate(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_slots.TryGetValue(key, out var slot))
            return;

        EntityState<TValue>? stale = null;
        bool shouldRefetch;

        lock (slot.Gate)
        {
            var current = slot.Subject.Value;
            if (current is EntityState<TValue>.Ready r)
                stale = new EntityState<TValue>.Ready(r.Value, r.Stamp, Freshness.Stale);

            shouldRefetch = slot.RefCount > 0 && slot.InFlight is null;
        }

        if (stale is not null)
            slot.Subject.OnNext(stale);

        if (shouldRefetch)
            _ = StartFetchAsync(key, slot, previous: stale is EntityState<TValue>.Ready r2 ? r2.Value : null);
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, slot) in _slots)
        {
            lock (slot.Gate)
                CancelInflightUnderLock(slot);
            try { slot.Subject.OnCompleted(); } catch { /* already completed */ }
            slot.Subject.Dispose();
        }

        _slots.Clear();
    }

    // ── Materialization ─────────────────────────────────────────────────

    private async Task MaterializeAsync(TKey key, Slot slot)
    {
        // Cheap short-circuit: if the subject already has a Ready value that's
        // still fresh, we have nothing to do. If it's stale we fall through to
        // fire a background refetch.
        EntityState<TValue> current;
        bool shouldHotProbe;
        bool shouldColdProbe;

        lock (slot.Gate)
        {
            current = slot.Subject.Value;
            shouldHotProbe = current is EntityState<TValue>.Initial;
            shouldColdProbe = !slot.ColdProbed;
        }

        try
        {
            if (shouldHotProbe)
            {
                var hot = await ReadHotAsync(key).ConfigureAwait(false);
                if (hot is not null)
                {
                    EmitFromCache(slot, hot);
                    current = slot.Subject.Value;
                }
            }

            if (shouldColdProbe && current is EntityState<TValue>.Initial)
            {
                // Only probe cold if we still have no data. Use a temporary CTS
                // tied to the slot's lifetime so dispose cancels the cold read.
                var cts = new CancellationTokenSource();
                try
                {
                    var cold = await ReadColdAsync(key, cts.Token).ConfigureAwait(false);
                    if (cold is not null)
                    {
                        try { WriteHot(key, cold); } catch { /* best-effort */ }
                        EmitFromCache(slot, cold);
                        current = slot.Subject.Value;
                    }
                }
                finally
                {
                    cts.Dispose();
                    lock (slot.Gate) slot.ColdProbed = true;
                }
            }

            // Schedule a fetch if: no data at all, OR data is Stale and we have subscribers.
            bool needsNetworkFetch;
            TValue? previous;
            lock (slot.Gate)
            {
                var state = slot.Subject.Value;
                previous = state switch
                {
                    EntityState<TValue>.Ready r => r.Value,
                    EntityState<TValue>.Error e => e.Previous,
                    EntityState<TValue>.Loading l => l.Previous,
                    _ => null
                };
                needsNetworkFetch = state switch
                {
                    EntityState<TValue>.Initial => true,
                    EntityState<TValue>.Ready r => r.Freshness == Freshness.Stale && slot.InFlight is null,
                    _ => false
                };
            }

            if (needsNetworkFetch)
                await StartFetchAsync(key, slot, previous).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MaterializeAsync failed for key {Key}", key);
            var prev = slot.Subject.Value switch
            {
                EntityState<TValue>.Ready r => r.Value,
                _ => null
            };
            slot.Subject.OnNext(new EntityState<TValue>.Error(ex, prev));
        }
    }

    private void EmitFromCache(Slot slot, TValue value)
    {
        var stamp = DateTimeOffset.UtcNow; // cache tier doesn't track own stamp today; good enough
        var freshness = IsStale(stamp) ? Freshness.Stale : Freshness.Fresh;
        slot.Subject.OnNext(new EntityState<TValue>.Ready(value, stamp, freshness));
    }

    private bool IsStale(DateTimeOffset stamp) => DateTimeOffset.UtcNow - stamp > Ttl;

    private Task StartFetchAsync(TKey key, Slot slot, TValue? previous)
    {
        var stamp = Interlocked.Increment(ref _stampSeq);
        CancellationTokenSource cts;
        Task fetchTask;

        lock (slot.Gate)
        {
            if (slot.InFlight is not null) return slot.InFlight;

            cts = new CancellationTokenSource();
            slot.Cts = cts;
            slot.StampTicks = stamp;
            slot.Subject.OnNext(new EntityState<TValue>.Loading(previous));
            fetchTask = RunFetchAsync(key, slot, previous, stamp, cts);
            slot.InFlight = fetchTask;
        }

        return fetchTask;
    }

    private async Task RunFetchAsync(TKey key, Slot slot, TValue? previous, long stamp, CancellationTokenSource cts)
    {
        try
        {
            var value = await FetchAsync(key, previous, cts.Token).ConfigureAwait(false);

            // If a Push beat us to the punch, drop our result.
            lock (slot.Gate)
            {
                if (slot.StampTicks != stamp)
                    return;
            }

            try { WriteHot(key, value); }
            catch (Exception ex) { _logger?.LogDebug(ex, "WriteHot failed post-fetch for key {Key}", key); }

            await WriteColdSafeAsync(key, value).ConfigureAwait(false);

            slot.Subject.OnNext(new EntityState<TValue>.Ready(value, DateTimeOffset.UtcNow, Freshness.Fresh));
        }
        catch (OperationCanceledException)
        {
            // Refcount hit zero or a Push raced us. Silent — not an error.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FetchAsync failed for key {Key}", key);
            slot.Subject.OnNext(new EntityState<TValue>.Error(ex, previous));
        }
        finally
        {
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Cts, cts))
                {
                    slot.Cts = null;
                    slot.InFlight = null;
                }
            }
            cts.Dispose();
        }
    }

    private async Task WriteColdSafeAsync(TKey key, TValue value)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WriteColdAsync(key, value, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WriteColdAsync failed for key {Key}", key);
        }
    }

    private static void CancelInflight(Slot slot)
    {
        lock (slot.Gate) CancelInflightUnderLock(slot);
    }

    private static void CancelInflightUnderLock(Slot slot)
    {
        if (slot.Cts is null) return;
        try { slot.Cts.Cancel(); } catch { /* already disposed */ }
    }

    // ── Testing hooks ────────────────────────────────────────────────────

    internal int SlotCountForTests => _slots.Count;
}
