using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>One application timer for active generated playlists. Pages declare demand and own no refresh clock.</summary>
public sealed class ActiveCatalogDemand : IDisposable
{
    static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan LagRetry = TimeSpan.FromMinutes(5);
    readonly CatalogRepository _catalog;
    readonly IResourceCoordinator _resources;
    readonly TimeProvider _time;
    readonly Action<Exception> _report;
    readonly object _gate = new();
    /// <summary>Per lease, ONLY the PlaylistHeader keys it demands. A query node re-declares its whole
    /// retention set on every projection pass - ~1.5k track keys on a long playlist - so reconciling used to
    /// re-walk every lease's every key, and Peek/CanRequest (two catalog locks apiece) each header, for a
    /// window move that cannot change which generated playlists are active. Headers are the only thing this
    /// timer cares about: the subset is extracted once outside the lock and an unchanged one reconciles nothing.</summary>
    readonly Dictionary<object, HashSet<ResourceKey>> _leaseHeaders = new();
    readonly Dictionary<ResourceKey, ActivePlaylist> _playlists = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly CancellationToken _token;
    readonly ITimer _timer;
    readonly IDisposable _subscription;
    TaskCompletionSource? _run;
    bool _disposed;
    long _observedEpoch;

    // All mutable entry fields are read or changed under _gate. Async work keeps only the entry identity.
    sealed class ActivePlaylist(ResourceKey key, DateTimeOffset now, long epoch)
    {
        public ResourceKey Key { get; } = key;
        public long Epoch { get; } = epoch;
        public DateTimeOffset ProbeAt = now, HeaderAfter = now, ObservedHeaderAt;
        public string? Revision;
        public bool RevisionChanged;
    }

    public ActiveCatalogDemand(CatalogRepository catalog, IResourceCoordinator resources, TimeProvider time,
        Action<Exception> report)
    {
        _catalog = catalog; _resources = resources; _time = time; _report = report;
        _token = _lifetime.Token;
        _timer = time.CreateTimer(_ => { _ = RunDueAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _subscription = catalog.Changes.Subscribe(new Observer(this));
    }

    public void Set(object owner, IReadOnlyList<ResourceKey> keys)
    {
        HashSet<ResourceKey>? headers = null;
        foreach (var key in keys) if (key.Facet == FacetKind.PlaylistHeader) (headers ??= new()).Add(key);
        lock (_gate)
        {
            if (_disposed) return;
            if (headers is null) { if (!_leaseHeaders.Remove(owner)) return; }
            else if (_leaseHeaders.TryGetValue(owner, out var existing) && existing.SetEquals(headers)) return;
            else _leaseHeaders[owner] = headers;
            ReconcileLocked();
        }
    }

    void ReconcileLocked()
    {
        _observedEpoch = _catalog.Epoch;
        var active = new HashSet<ResourceKey>();
        foreach (var lease in _leaseHeaders.Values)
            foreach (var key in lease)
                if (!active.Contains(key) && _catalog.CanRequest(key, requiresNetwork: false)
                    && _catalog.Peek(key).Value is PlaylistHeaderValue header
                    && (header.Format == "daylist" || header.NextUpdateAt is not null))
                    active.Add(key);
        foreach (var old in _playlists.Keys.Where(key => !active.Contains(key)).ToArray()) _playlists.Remove(old);
        var now = _time.GetUtcNow();
        foreach (var key in active)
        {
            if (!_playlists.TryGetValue(key, out var entry) || entry.Epoch != _catalog.Epoch)
                _playlists[key] = entry = new(key, now, _catalog.Epoch);
            ObserveHeaderLocked(entry, now);
        }
        ArmLocked();
    }

    PlaylistHeaderValue? ObserveHeaderLocked(ActivePlaylist entry, DateTimeOffset now)
    {
        var snapshot = _catalog.Peek(entry.Key);
        if (snapshot.Value is not PlaylistHeaderValue header) return null;
        // A lagging authoritative response remains readable, but must not earn another forced request on remount.
        if (snapshot.Provenance == CatalogProvenance.Provider && snapshot.FetchedAt > entry.ObservedHeaderAt)
        {
            entry.ObservedHeaderAt = snapshot.FetchedAt;
            if (header.NextUpdateAt <= now && snapshot.FetchedAt >= header.NextUpdateAt
                && snapshot.FetchedAt + LagRetry > entry.HeaderAfter)
                entry.HeaderAfter = snapshot.FetchedAt + LagRetry;
        }
        if (entry.Revision is not null && string.Equals(entry.Revision, header.Edition, StringComparison.Ordinal))
            entry.RevisionChanged = false;
        return header;
    }

    bool CurrentLocked(ActivePlaylist entry) => !_disposed && _catalog.IsOnline
        && entry.Epoch == _catalog.Epoch && _catalog.CanRequest(entry.Key, requiresNetwork: true)
        && _playlists.TryGetValue(entry.Key, out var current) && ReferenceEquals(entry, current);

    void ArmLocked()
    {
        if (_disposed || _run is not null || !_catalog.IsOnline || _playlists.Count == 0)
        { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        DateTimeOffset due = DateTimeOffset.MaxValue;
        foreach (var entry in _playlists.Values)
        {
            if (entry.Epoch != _catalog.Epoch) continue;
            if (entry.ProbeAt < due) due = entry.ProbeAt;
            var header = ObserveHeaderLocked(entry, _time.GetUtcNow());
            DateTimeOffset? refreshAt = entry.RevisionChanged ? entry.HeaderAfter : header?.NextUpdateAt;
            if (refreshAt is { } refresh)
            {
                if (refresh < entry.HeaderAfter) refresh = entry.HeaderAfter;
                if (refresh < due) due = refresh;
            }
        }
        if (due == DateTimeOffset.MaxValue)
        { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        var delay = due - _time.GetUtcNow();
        _timer.Change(delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay, Timeout.InfiniteTimeSpan);
    }

    // Concurrent timer/manual callers await the same finite pass; no second loop can mutate an entry concurrently.
    public Task RunDueAsync()
    {
        ActivePlaylist[] entries;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposed || !_catalog.IsOnline) return Task.CompletedTask;
            if (_run is not null) return _run.Task;
            _run = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entries = _playlists.Values.ToArray();
        }
        _ = RunCoreAsync(entries, completion);
        return completion.Task;
    }

    async Task RunCoreAsync(ActivePlaylist[] entries, TaskCompletionSource completion)
    {
        try
        {
            foreach (var entry in entries)
            {
                try { await RunEntryAsync(entry).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { break; }
                catch (Exception error) { _report(error); }
            }
        }
        finally
        {
            lock (_gate) { _run = null; if (!_disposed) ArmLocked(); }
            completion.TrySetResult();
        }
    }

    async Task RunEntryAsync(ActivePlaylist entry)
    {
        bool probe;
        lock (_gate)
        {
            if (!CurrentLocked(entry)) return;
            var now = _time.GetUtcNow();
            probe = entry.ProbeAt <= now;
            if (probe) entry.ProbeAt = now + ProbeInterval;
        }
        if (probe)
        {
            var probeKey = entry.Key with { Facet = FacetKind.PlaylistRevision };
            var results = await _resources.EnsureAsync([probeKey], ResourcePriority.Visible, ct: _token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!CurrentLocked(entry)) return; // last lease removed or account changed while HEAD was in flight
                var header = ObserveHeaderLocked(entry, _time.GetUtcNow());
                var result = results.FirstOrDefault(item => item.Key == probeKey);
                if (result is { Status: ResourceEnsureStatus.Ready, Snapshot.Value: PlaylistRevisionValue revision })
                {
                    var prior = entry.Revision ?? header?.Edition;
                    if (prior is not null && !string.Equals(prior, revision.Revision, StringComparison.Ordinal))
                        entry.RevisionChanged = true;
                    entry.Revision = revision.Revision;
                    if (string.Equals(header?.Edition, revision.Revision, StringComparison.Ordinal)) entry.RevisionChanged = false;
                }
            }
        }
        lock (_gate)
        {
            if (!CurrentLocked(entry)) return;
            var now = _time.GetUtcNow();
            var header = ObserveHeaderLocked(entry, now);
            if (header is null || entry.HeaderAfter > now || !(entry.RevisionChanged || header.NextUpdateAt <= now)) return;
            entry.HeaderAfter = now + LagRetry;
        }
        await _resources.EnsureAsync([entry.Key], ResourcePriority.Visible, force: true, ct: _token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _leaseHeaders.Clear(); _playlists.Clear();
        }
        _subscription.Dispose(); _lifetime.Cancel(); _timer.Dispose(); _lifetime.Dispose();
    }

    sealed class Observer(ActiveCatalogDemand owner) : IObserver<CatalogChangeSet>
    {
        public void OnNext(CatalogChangeSet value)
        {
            lock (owner._gate)
                if (!owner._disposed && (owner._observedEpoch != owner._catalog.Epoch
                    || value.Keys.Any(key => key.Facet == FacetKind.PlaylistHeader
                        && owner._leaseHeaders.Values.Any(headers => headers.Contains(key)))))
                    owner.ReconcileLocked();
        }
        public void OnError(Exception error) => owner._report(error);
        public void OnCompleted() { }
    }
}
