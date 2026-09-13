using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee;

/// <summary>
/// A keyed single-flight cache with a success-only TTL: engine-free, BCL-only, so it is headlessly unit-testable
/// (<c>SingleFlightMemoTests</c>) without any of the video plumbing it fronts.
///
/// <para><b>Single flight.</b> Two callers racing <see cref="GetOrStartAsync"/> for the SAME key while no cached
/// answer is live share exactly ONE call to <paramref name="start"/> — the second caller attaches to the first's
/// in-flight task instead of starting a second network round trip. This is the fix for the duplicate-resolve defect
/// this type was built to close (a toggle firing two full video resolves for the same track).</para>
///
/// <para><b>The flight outlives its callers.</b> <paramref name="start"/> is invoked ONCE per flight and runs to
/// completion regardless of what any individual caller does afterwards — it is never linked to a caller's own
/// <see cref="CancellationToken"/>. A caller that cancels merely DETACHES from the flight (its own
/// <c>GetOrStartAsync</c> call throws <see cref="OperationCanceledException"/>); the flight keeps running in the
/// background and still populates the cache for the next caller. This is deliberate: a video toggle flipped twice in
/// a row must not abort the resolve the first flip already paid for.</para>
///
/// <para><b>TTL applies to success only.</b> A flight that completes with a result is cached until
/// <paramref name="ttl"/> elapses (measured against the injectable <c>clock</c>, which defaults to
/// <see cref="Environment.TickCount64"/> semantics — a monotonic millisecond counter, not wall-clock time, so it is
/// immune to a system clock step). A flight that FAULTS is evicted immediately: the next call starts a fresh flight
/// rather than replaying the same exception for up to <paramref name="ttl"/>.</para>
///
/// <para>Thread-safe: every mutation of the internal table happens under one lock; the (potentially slow)
/// <paramref name="start"/> callback itself never runs while holding it.</para>
/// </summary>
public sealed class SingleFlightMemo<T>
{
    sealed class Entry
    {
        // Assigned once, right after the flight is registered — see GetOrStartAsync. Never null after construction.
        public Task<T?> Flight = null!;
        // long.MaxValue while the flight has not (yet) completed successfully; set to clock() + ttl the instant it
        // does. A faulted flight is removed from the table outright rather than given an expiry.
        public long ExpiresAtTick = long.MaxValue;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly TimeSpan _ttl;
    readonly Func<long> _clock;

    /// <param name="ttl">How long a SUCCESSFUL flight's result stays cached and shareable. Must be non-negative;
    /// <see cref="TimeSpan.Zero"/> means "never cache a completed result" (every call after the flight finishes
    /// starts a fresh one, but concurrent callers while it is still in flight still share it).</param>
    /// <param name="clock">Monotonic millisecond clock, for tests. Defaults to <see cref="Environment.TickCount64"/>.</param>
    public SingleFlightMemo(TimeSpan ttl, Func<long>? clock = null)
    {
        if (ttl < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        _ttl = ttl;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    /// <summary>Get the cached result for <paramref name="key"/>, or join/start the one flight resolving it. The
    /// returned task completes with the flight's result, or throws <see cref="OperationCanceledException"/> the
    /// instant <paramref name="callerCt"/> is cancelled — WITHOUT cancelling the flight itself (see the type docs).
    /// <paramref name="start"/> is called with <paramref name="key"/> and <see cref="CancellationToken.None"/>: it
    /// is the flight's own token, deliberately never <paramref name="callerCt"/>.</summary>
    public Task<T?> GetOrStartAsync(string key, Func<string, Task<T?>> start, CancellationToken callerCt)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(start);

        Task<T?> flight;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && !IsExpired(existing))
            {
                flight = existing.Flight;
            }
            else
            {
                var entry = new Entry();
                // Table membership FIRST: a flight that completes synchronously (cached upstream, sync throw) runs
                // its stamp/evict block before RunFlightAsync returns — it must find its own entry in the table, or
                // a success never gets a TTL (cached forever) and a fault never gets evicted (fault cached).
                _entries[key] = entry;
                entry.Flight = RunFlightAsync(key, start, entry);
                flight = entry.Flight;
            }
        }
        return DetachAsync(flight, callerCt);
    }

    // Runs start(key) UNLINKED to any caller. On success, stamps the entry's TTL (iff it is still the table's
    // current entry for the key — a concurrent Invalidate may have already dropped it, which is fine, that call
    // simply leaves no trace). On failure, evicts the entry so the NEXT call starts over instead of replaying the
    // fault for the rest of the TTL window.
    async Task<T?> RunFlightAsync(string key, Func<string, Task<T?>> start, Entry entry)
    {
        try
        {
            var result = await start(key).ConfigureAwait(false);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    entry.ExpiresAtTick = _clock() + (long)_ttl.TotalMilliseconds;
            }
            return result;
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(key);
            }
            throw;
        }
    }

    bool IsExpired(Entry e) => e.ExpiresAtTick != long.MaxValue && _clock() >= e.ExpiresAtTick;

    // Wrap `flight` so a cancelled caller sees its OWN cancellation immediately, without the flight itself being
    // touched. A caller with a non-cancelable token (the overwhelmingly common case — CancellationToken.None) pays
    // nothing extra: it awaits the flight directly.
    static async Task<T?> DetachAsync(Task<T?> flight, CancellationToken callerCt)
    {
        if (flight.IsCompleted || !callerCt.CanBeCanceled)
            return await flight.ConfigureAwait(false);

        var cancelSignal = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (callerCt.Register(() => cancelSignal.TrySetCanceled(callerCt)))
        {
            var winner = await Task.WhenAny(flight, cancelSignal.Task).ConfigureAwait(false);
            if (winner == cancelSignal.Task) return await cancelSignal.Task.ConfigureAwait(false);
        }
        return await flight.ConfigureAwait(false);
    }

    /// <summary>Drop the cached/in-flight entry for <paramref name="key"/>. A flight already running for it is left
    /// to finish (its result is simply not stored anywhere) — the NEXT <see cref="GetOrStartAsync"/> call for the
    /// same key starts a fresh flight rather than joining the one being invalidated.</summary>
    public void Invalidate(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate) { _entries.Remove(key); }
    }

    /// <summary>Drop every cached/in-flight entry.</summary>
    public void Clear()
    {
        lock (_gate) { _entries.Clear(); }
    }
}
