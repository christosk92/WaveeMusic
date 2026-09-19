using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

// ── ENGINE ① — Resource (reactive cached revalidating reads) ─────────────────────────────────────────────────────────
// The single abstraction behind entity hydration, library reads, playlists, Home, Pathfinder views, and online search.
// A "config" (a surface) = a fetch + a freshness policy. The engine owns stale-while-revalidate + in-flight dedup. (The
// wire→project split is folded here for the scaffold; the real fetch composes Transport.Request + a projection-to-store.)

public enum LoadState { Loading, Ready, Error }

public readonly record struct Loaded<T>(LoadState State, T? Value, bool IsStale, string? Error)
{
    public bool IsLoading => State == LoadState.Loading;
    public bool IsReady => State == LoadState.Ready;
    public static Loaded<T> Loading => new(LoadState.Loading, default, false, null);
    public static Loaded<T> Ready(T value, bool stale = false) => new(LoadState.Ready, value, stale, null);
    public static Loaded<T> Err(string error) => new(LoadState.Error, default, false, error);
}

// Freshness is a STRATEGY, not bespoke code — one engine dispatches on it.
public abstract record FreshnessPolicy
{
    public sealed record Etag(TimeSpan Ttl) : FreshnessPolicy;                       // extended-metadata
    public sealed record PollWhole(TimeSpan Ttl, bool SuspendInPlayback) : FreshnessPolicy; // home/browse
    public sealed record Immutable : FreshnessPolicy;                                // gids, audio bytes
}

public sealed class Resource<TKey, TValue> where TKey : notnull
{
    readonly Func<TKey, SessionContext, Task<TValue>> _fetch;
    readonly FreshnessPolicy _fresh;
    readonly Func<SessionContext> _ctx;
    readonly Func<TValue, TimeSpan?>? _ttlOf;
    readonly int _maxEntries;
    readonly string? _name;
    readonly WaveeLogger _debugLog;
    readonly object _gate = new();
    readonly Dictionary<TKey, Entry> _cache = new();
    int _fetchCount;
    long _hitCount, _missCount;

    public int FetchCount => _fetchCount;   // dedup / coalesce assertion hook
    public long HitCount => Interlocked.Read(ref _hitCount);
    public long MissCount => Interlocked.Read(ref _missCount);
    /// <summary>Resident entry count (census attribution) — read under <c>_gate</c>, consistent with the class's locking.</summary>
    public int Count { get { lock (_gate) return _cache.Count; } }

    sealed class Entry
    {
        public TValue? Val;
        public bool HasVal;
        public DateTime FetchedAt;
        public DateTime? ExpiresAt;
        public long LastUse;
        public Task? InFlight;
        public bool NeedsRevalidate;   // set by the dealer route (MarkStale); the revision policies gate IsStale on it
        public string? Error;          // last fetch failure (cleared on success) — surfaced via Loaded.Error, not swallowed
    }

    public Resource(Func<TKey, SessionContext, Task<TValue>> fetch, FreshnessPolicy fresh, Func<SessionContext> ctx,
        Func<TValue, TimeSpan?>? ttlOf = null, int maxEntries = 0, string? name = null, WaveeLogger debugLog = default)
    {
        _fetch = fetch;
        _fresh = fresh;
        _ctx = ctx;
        _ttlOf = ttlOf;
        _maxEntries = maxEntries;
        _name = name;
        _debugLog = debugLog;
    }

    /// <summary>Await-able read: serve a resident fresh value without touching the network; otherwise join/start ONE
    /// revalidation and return the outcome.</summary>
    public async Task<Loaded<TValue>> GetAsync(TKey key, CancellationToken ct = default)
    {
        var peek = Peek(key);
        if (peek.IsReady && !peek.IsStale)
        {
            Interlocked.Increment(ref _hitCount);
            LogHitMiss("hit");
            return peek;
        }
        Interlocked.Increment(ref _missCount);
        LogHitMiss("miss");
        await Revalidate(key).WaitAsync(ct).ConfigureAwait(false);
        return Peek(key);
    }

    public Loaded<TValue> Use(TKey key)
    {
        // Snapshot the entry UNDER the lock — Val/HasVal/FetchedAt are written under _gate, so reading them outside it
        // can tear on a weak memory model (Apple Silicon is a target).
        bool hasVal, stale;
        TValue? val;
        string? error;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) { e = new Entry(); _cache[key] = e; }
            hasVal = e.HasVal;
            val = e.Val;
            stale = hasVal && IsStale(e);
            error = hasVal ? null : e.Error;
            if (hasVal && !stale) Touch(e);
        }
        if (hasVal)
        {
            if (!stale) Interlocked.Increment(ref _hitCount);
            if (stale) _ = Revalidate(key);      // SWR: serve stale, refresh in the background
            return Loaded<TValue>.Ready(val!, stale);
        }
        _ = Revalidate(key);                     // (re)try — on an errored entry this is the recovery attempt
        return error != null ? Loaded<TValue>.Err(error) : Loaded<TValue>.Loading;
    }

    public Task Revalidate(TKey key)
    {
        Entry e;
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out e!)) { e = new Entry(); _cache[key] = e; }
            if (e.InFlight != null) return e.InFlight;   // in-flight dedup: coalesce concurrent revalidations
            // Mark in-flight with a TCS BEFORE starting the fetch, so (a) the fetch runs OUTSIDE the lock (no user
            // delegate under our lock) and (b) the slot is keyed to THIS attempt — a synchronously-completing fetch can't
            // leave a stale non-null slot that permanently blocks future revalidations.
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            e.InFlight = tcs.Task;
        }
        _ = RunFetch(key, e, tcs);
        return tcs.Task;
    }

    /// <summary>Non-triggering snapshot of an entry (unlike Use, never starts a fetch) — for batch cache partitioning:
    /// Ready+!IsStale ⇒ fresh (skip), otherwise stale/missing (fetch).</summary>
    public Loaded<TValue> Peek(TKey key)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) return Loaded<TValue>.Loading;
            if (e.HasVal)
            {
                Touch(e);
                return Loaded<TValue>.Ready(e.Val!, IsStale(e));
            }
            if (e.InFlight != null) return Loaded<TValue>.Loading;   // a fetch is in progress — not yet an error
            if (e.Error != null) return Loaded<TValue>.Err(e.Error); // surface the last failure (was silently swallowed)
            return Loaded<TValue>.Loading;
        }
    }

    /// <summary>Per-entry expiry time when <c>ttlOf</c> is configured — diagnostic / test visibility.</summary>
    public DateTime? PeekExpiresAt(TKey key)
    {
        lock (_gate)
            return _cache.TryGetValue(key, out var e) && e.HasVal ? e.ExpiresAt : null;
    }

    /// <summary>Record a value as freshly-fetched, bypassing the per-key fetch — for a batch path that fetched many at once
    /// (so subsequent <see cref="Peek"/>s see them as fresh and skip the network).</summary>
    public void Seed(TKey key, TValue value)
        => Seed(key, value, DateTime.UtcNow, ComputeExpiresAt(value), needsRevalidate: false);

    /// <summary>Restore a persisted value without pretending it was fetched at process startup. Used by durable HTTP
    /// caches so an expired row remains stale (and keeps its ETag) while an unexpired row retains its actual deadline.</summary>
    public void Seed(TKey key, TValue value, DateTime fetchedAtUtc, DateTime? expiresAtUtc, bool needsRevalidate)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) { e = new Entry(); _cache[key] = e; }
            e.Val = value;
            e.HasVal = true;
            e.Error = null;
            e.FetchedAt = fetchedAtUtc;
            e.ExpiresAt = expiresAtUtc;
            e.NeedsRevalidate = needsRevalidate;
            Touch(e);
            EvictIfNeeded();
        }
    }

    /// <summary>Restore a DURABLE persisted row — the cold-tier twin of <see cref="Seed(TKey,TValue,DateTime,DateTime?,bool)"/>.
    /// Two differences, both required because the cold read races the dealer route and the network:
    ///  • never clobber a fresher resident value (a fetch that already landed wins over the row on disk);
    ///  • never clear a <c>NeedsRevalidate</c> that <see cref="MarkStale"/> set while the read was in flight (OR, don't assign).
    /// <see cref="MarkStale"/> creates a valueless entry with NeedsRevalidate=true, so the HasVal guard lets the row through
    /// and the OR is what stops the seed from silently swallowing that push.</summary>
    public void SeedPersisted(TKey key, TValue value, DateTime fetchedAtUtc, DateTime? expiresAtUtc, bool needsRevalidate)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) { e = new Entry(); _cache[key] = e; }
            if (e.HasVal && fetchedAtUtc <= e.FetchedAt) return;   // the network already spoke, and spoke later
            e.Val = value;
            e.HasVal = true;
            e.Error = null;
            e.FetchedAt = fetchedAtUtc;
            e.ExpiresAt = expiresAtUtc;
            e.NeedsRevalidate |= needsRevalidate;
            Touch(e);
            EvictIfNeeded();
        }
    }

    /// <summary>Drop the value so the next Get/Use fetches. Unlike MarkStale, the dead value is never served again.</summary>
    public void Invalidate(TKey key)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) return;
            e.HasVal = false;
            e.Val = default;
            e.Error = null;
            e.ExpiresAt = null;
        }
    }

    /// <summary>Mark a key dirty so the next <see cref="Use"/> serves the resident value stale and revalidates
    /// exactly once; keys nobody marks stay fresh and never eager-refetch (the anti-herd).</summary>
    public void MarkStale(TKey key)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) { e = new Entry(); _cache[key] = e; }
            e.NeedsRevalidate = true;
        }
    }

    async Task RunFetch(TKey key, Entry e, TaskCompletionSource tcs)
    {
        try
        {
            Interlocked.Increment(ref _fetchCount);
            var v = await _fetch(key, _ctx()).ConfigureAwait(false);
            lock (_gate)
            {
                e.Val = v;
                e.HasVal = true;
                e.Error = null;
                e.FetchedAt = DateTime.UtcNow;
                e.ExpiresAt = ComputeExpiresAt(v);
                e.NeedsRevalidate = false;
                Touch(e);
                EvictIfNeeded();
            }
        }
        catch (Exception ex)
        {
            // Fetch failed — record the error ON THE ENTRY so Use/Peek surface it (typed reasons, error UI) and a later
            // Use retries. Still caught here so this fire-and-forget revalidation can't fault unobserved.
            lock (_gate) e.Error = ex.Message;
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(e.InFlight, tcs.Task)) e.InFlight = null; }   // clear by identity
            tcs.SetResult();
        }
    }

    DateTime? ComputeExpiresAt(TValue value) =>
        _ttlOf?.Invoke(value) is { } t ? DateTime.UtcNow + t : null;

    static void Touch(Entry e) => e.LastUse = Stopwatch.GetTimestamp();

    void EvictIfNeeded()
    {
        if (_maxEntries <= 0 || _cache.Count <= _maxEntries) return;
        while (_cache.Count > _maxEntries)
        {
            TKey victim = default!;
            long oldest = long.MaxValue;
            bool found = false;
            foreach (var (k, entry) in _cache)
            {
                if (entry.InFlight != null) continue;
                if (entry.LastUse < oldest) { oldest = entry.LastUse; victim = k; found = true; }
            }
            if (!found) break;
            _cache.Remove(victim);
        }
    }

    void LogHitMiss(string kind)
    {
        if (_name is null) return;
        _debugLog.Debug($"resource {_name}: {kind}");
    }

    bool IsStale(Entry e)
    {
        if (e.ExpiresAt is { } exp && DateTime.UtcNow >= exp) return true;
        return _fresh switch
        {
            FreshnessPolicy.Etag et => e.NeedsRevalidate || DateTime.UtcNow - e.FetchedAt > et.Ttl,
            FreshnessPolicy.PollWhole pw => e.NeedsRevalidate || DateTime.UtcNow - e.FetchedAt > pw.Ttl,
            FreshnessPolicy.Immutable => false,
            _ => true,
        };
    }
}
