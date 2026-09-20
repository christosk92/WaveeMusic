using System;
using System.Collections.Generic;
using System.Threading;

namespace Wavee.Core;

/// <summary>The durable backing store for activity entries. SQLite on the real backend; in-memory on the fake. Ids are
/// assigned by the <see cref="ActivityLog"/> (monotonic), so <see cref="Append"/> takes a fully-formed entry.</summary>
public interface IActivityStore
{
    void Append(ActivityEntry entry);
    void SetStatus(long id, ActivityStatus status);
    void MarkAllRead();
    void Clear();
    /// <summary>The newest <paramref name="limit"/> entries, newest-first.</summary>
    IReadOnlyList<ActivityEntry> LoadRecent(int limit);
    /// <summary>Keep the newest <paramref name="maxCount"/> entries and drop anything older than <paramref name="maxAgeMs"/>.</summary>
    void Prune(int maxCount, long maxAgeMs);
}

/// <summary>The app-local activity log: every invertible/loggable library mutation appends an entry here; the UI reads
/// the cached snapshot (newest-first, capped) and drives Undo off it. Records inline from <c>LibraryBridge</c> — no bus.
/// A cache + a durable store: writes update the cache synchronously (so the UI converges this frame) and persist through
/// the store. <see cref="SuppressRecording"/> ref-counts so an Undo's inverse mutation does NOT re-record.</summary>
public sealed class ActivityLog
{
    const int Cap = 200;
    const long PruneMaxAgeMs = 30L * 24 * 3600 * 1000;   // 30 days
    const int PruneEvery = 20;

    readonly IActivityStore _store;
    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    readonly List<ActivityEntry> _cache;   // newest-first; guarded by _gate
    ActivityEntry[] _snapshot = Array.Empty<ActivityEntry>();
    long _nextId;
    int _appendsSincePrune;
    int _suppress;
    int _rev;

    public ActivityLog(IActivityStore store)
    {
        _store = store;
        store.Prune(Cap, PruneMaxAgeMs);
        var recent = store.LoadRecent(Cap);
        _cache = new List<ActivityEntry>(recent);
        long max = 0;
        foreach (var e in _cache) if (e.Id > max) max = e.Id;
        _nextId = max + 1;
        PublishLocked();
    }

    /// <summary>The cached entries, newest-first — the panel keys its rows on these.</summary>
    public IReadOnlyList<ActivityEntry> Snapshot => Volatile.Read(ref _snapshot);
    public IObservable<int> Changed => _changed;
    public bool IsSuppressed => Volatile.Read(ref _suppress) > 0;

    /// <summary>Ref-counted recording suppression (UI thread) — Undo wraps its inverse mutation so it never re-records.</summary>
    public IDisposable SuppressRecording()
    {
        Interlocked.Increment(ref _suppress);
        return new Suppressor(this);
    }

    /// <summary>Append a Done entry. No-op (returns -1) while suppressed. Returns the new entry id.</summary>
    public long Record(ActivityKind kind, string targetUri, string? targetName = null, ActivityPayload? payload = null)
    {
        if (IsSuppressed) return -1;
        long id;
        lock (_gate)
        {
            id = _nextId++;
            var entry = new ActivityEntry(id, kind, targetUri, targetName, payload,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ActivityStatus.Done, Read: false);
            _cache.Insert(0, entry);
            _store.Append(entry);
            if (_cache.Count > Cap) _cache.RemoveRange(Cap, _cache.Count - Cap);
            if (++_appendsSincePrune >= PruneEvery) { _appendsSincePrune = 0; _store.Prune(Cap, PruneMaxAgeMs); }
            PublishLocked();
        }
        Fire();
        return id;
    }

    public void MarkFailed(long id) => SetStatus(id, ActivityStatus.Failed);
    public void MarkUndone(long id) => SetStatus(id, ActivityStatus.Undone);

    void SetStatus(long id, ActivityStatus status)
    {
        if (id < 0) return;
        bool changed = false;
        lock (_gate)
        {
            for (int i = 0; i < _cache.Count; i++)
                if (_cache[i].Id == id) { _cache[i] = _cache[i] with { Status = status }; changed = true; break; }
            if (changed) { _store.SetStatus(id, status); PublishLocked(); }
        }
        if (changed) Fire();
    }

    public void MarkAllRead()
    {
        bool changed = false;
        lock (_gate)
        {
            for (int i = 0; i < _cache.Count; i++)
                if (!_cache[i].Read) { _cache[i] = _cache[i] with { Read = true }; changed = true; }
            if (changed) { _store.MarkAllRead(); PublishLocked(); }
        }
        if (changed) Fire();
    }

    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _cache.Count > 0;
            _cache.Clear();
            _store.Clear();
            PublishLocked();
        }
        if (changed) Fire();
    }

    void PublishLocked() => _snapshot = _cache.ToArray();
    void Fire() => _changed.OnNext(Interlocked.Increment(ref _rev));

    sealed class Suppressor(ActivityLog log) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Decrement(ref log._suppress);
        }
    }
}
