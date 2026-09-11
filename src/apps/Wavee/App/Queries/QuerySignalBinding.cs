using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>The current view's demand/lifetime seam, shared with its virtualized descendants.</summary>
public interface IQuerySignalBinding : IDisposable
{
    QueryDemand Demand { get; }
    long Revision { get; }
    IReadSignal<IReadOnlyDictionary<ResourceKey, ResourceSnapshot>> Resources { get; }
    IReadSignal<IReadOnlyDictionary<ResourceKey, QueryFact>> Facts { get; }
    /// <summary>The delivered publication's <see cref="QuerySnapshot{T}.FactsRevision"/> — the scalar identity of
    /// <see cref="Facts"/>. A consumer whose work depends on KNOWLEDGE (a whole-membership filter/sort projection)
    /// subscribes to this and reads the dictionaries with <c>Peek()</c>: the join hands out a fresh dictionary
    /// instance per publication, so comparing those by reference re-ran the projection on every one of the ~20
    /// publications a cold playlist open produces, while the revision only moves when a fact actually changed.</summary>
    IReadSignal<long> FactsRevision { get; }
    IReadSignal<Exception?> Failure { get; }
    void SetActive(bool active);
    void SetDemand(QueryDemand demand);
    ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
}

public static class QueryView
{
    public static readonly Context<Signal<IQuerySignalBinding?>?> Slot = new(null);
}

/// <summary>The UI delivery cadence both bindings share.</summary>
public static class QueryDelivery
{
    /// <summary>Milliseconds between two UI deliveries of the same binding. A background query republishes as fast
    /// as its resources land (≈20 publications a second while a playlist opens) and every delivery re-runs the whole
    /// consumer chain, so deliveries coalesce: the FIRST snapshot lands immediately (leading edge), further snapshots
    /// arriving inside the window are collapsed into one latest-wins delivery at its end (trailing edge), and a
    /// failure never waits. 50 ms is the value the pre-catalog <c>DetailLiveRefresh</c> used for the same job.</summary>
    public const int DeliveryCooldownMs = 50;
}

/// <summary>
/// One query subscription and one stable UI signal. Background publications coalesce into one pending UI post.
/// Parking suppresses delivery and releases demand; activation delivers the latest canonical snapshot once.
/// This adapter never loads or recomputes data. Call lifetime and demand methods on the UI thread.
/// </summary>
public sealed class QuerySignalBinding<T> : IQuerySignalBinding, IObserver<QuerySnapshot<T>>
{
    public QueryDemand Demand => _active ? _demand : QueryDemand.None;
    public long Revision => Snapshot.Peek().Revision;
    readonly object _gate = new();
    readonly IQueryHandle<T> _query;
    readonly Action<Action> _post;
    readonly Action<QuerySnapshot<T>>? _published;
    readonly Action<Exception>? _failed;
    readonly TimeProvider _time;
    readonly Signal<Exception?> _failure = new(null);
    Exception? _latestError;
    readonly IDisposable _subscription;
    QuerySnapshot<T> _latest;
    QueryDemand _demand = QueryDemand.Initial;
    bool _active, _posted, _disposed;
    long _generation;
    // The delivery cooldown (QueryDelivery.DeliveryCooldownMs): when the last delivery is younger than the window,
    // the pending one is armed on this timer instead of posted, so a burst costs one leading + one trailing delivery.
    ITimer? _cooldown;
    DateTimeOffset? _lastDelivery;

    public Signal<QuerySnapshot<T>> Snapshot { get; }
    public IReadSignal<Exception?> Failure => _failure;
    readonly Signal<IReadOnlyDictionary<ResourceKey, ResourceSnapshot>> _resources;
    readonly Signal<IReadOnlyDictionary<ResourceKey, QueryFact>> _facts;
    readonly Signal<long> _factsRevision;
    public IReadSignal<IReadOnlyDictionary<ResourceKey, ResourceSnapshot>> Resources => _resources;
    public IReadSignal<IReadOnlyDictionary<ResourceKey, QueryFact>> Facts => _facts;
    public IReadSignal<long> FactsRevision => _factsRevision;

    public QuerySignalBinding(IQueryHandle<T> query, Action<Action> post,
        Action<QuerySnapshot<T>>? published = null, Action<Exception>? failed = null, TimeProvider? time = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _published = published;
        _failed = failed;
        _time = time ?? TimeProvider.System;
        _latest = query.Current;
        Snapshot = new(_latest);
        _resources = new(_latest.Resources);
        _facts = new(_latest.Facts);
        _factsRevision = new(_latest.FactsRevision);
        _subscription = query.Changes.Subscribe(this);
    }

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed || _active == active) return;
            _active = active;
            _generation++;
            _posted = false;
            if (active)
            {
                var current = _query.Current;
                if (current.Revision >= _latest.Revision) _latest = current;
            }
        }
        _query.SetDemand(active ? _demand : QueryDemand.None);
        // An activation is a leading edge of its own generation: it never waits behind the previous view's cooldown.
        if (active) Schedule(immediate: true);
    }

    public void SetDemand(QueryDemand demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        bool active;
        lock (_gate)
        {
            if (_disposed) return;
            if (SameDemand(_demand, demand)) return;
            _demand = demand;
            active = _active;
        }
        _query.SetDemand(active ? demand : QueryDemand.None);
    }

    public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        return _query.RefreshAsync(cancellationToken);
    }

    public void OnNext(QuerySnapshot<T> value)
    {
        lock (_gate)
        {
            if (_disposed || value.Revision < _latest.Revision) return;
            _latest = value;
            _latestError = null;
        }
        Schedule();
    }

    // Query failures are values (Problems); an observer terminal event never fabricates empty primary data.
    public void OnError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate) { if (_disposed) return; _latestError = error; }
        System.Diagnostics.Trace.TraceError("Query subscription terminated: {0}", error);
        Schedule();
    }
    public void OnCompleted() { }

    /// <summary>Coalesced UI delivery. The first snapshot of an activation posts straight away; while snapshots keep
    /// arriving, at most one delivery lands per <see cref="QueryDelivery.DeliveryCooldownMs"/> (latest wins — the
    /// pending post always reads <c>_latest</c> when it runs), and the last one is delivered by the trailing timer
    /// once they stop. A failure — a terminal observer error or a snapshot carrying one — never waits.</summary>
    void Schedule(bool immediate = false)
    {
        long generation;
        TimeSpan wait;
        lock (_gate)
        {
            if (_disposed || !_active || _posted) return;
            wait = immediate || _latestError is not null || _latest.Failure is not null ? TimeSpan.Zero : Cooldown();
            _posted = true;
            generation = _generation;
            if (wait > TimeSpan.Zero)
            {
                _cooldown ??= _time.CreateTimer(static state => ((QuerySignalBinding<T>)state!).PostDeferred(), this,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _cooldown.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        _post(() => Deliver(generation));
    }

    /// <summary>Caller holds <see cref="_gate"/>. How long the pending delivery still owes the cooldown.</summary>
    TimeSpan Cooldown()
    {
        if (_lastDelivery is not { } last) return TimeSpan.Zero;
        var window = TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs);
        var elapsed = _time.GetUtcNow() - last;
        return elapsed < TimeSpan.Zero || elapsed >= window ? TimeSpan.Zero : window - elapsed;
    }

    /// <summary>The trailing edge: the cooldown expired with a delivery still pending. Parking or disposing in the
    /// meantime cleared <c>_posted</c>, so the late timer then posts nothing.</summary>
    void PostDeferred()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || !_active || !_posted) return;
            generation = _generation;
        }
        _post(() => Deliver(generation));
    }

    void Deliver(long generation)
    {
        QuerySnapshot<T> value;
        Exception? failure;
        lock (_gate)
        {
            if (_disposed || !_active || generation != _generation) return;
            _posted = false;
            _lastDelivery = _time.GetUtcNow();
            value = _latest;
            failure = _latestError;
        }
        // UI posts are batched by the host; all channels land before the deferred reactive flush.
        _resources.Value = value.Resources;
        _facts.Value = value.Facts;
        _factsRevision.Value = value.FactsRevision;
        Snapshot.Value = value;
        _failure.Value = failure ?? (value.Failure is { } queryError ? new InvalidOperationException(queryError.Message) : null);
        if (failure is not null) _failed?.Invoke(failure);
        else if (value.Revision > 0) _published?.Invoke(value);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _posted = false;
        }
        _cooldown?.Dispose();
        _subscription.Dispose();
        _query.SetDemand(QueryDemand.None);
        _query.Dispose();
    }

    static bool SameDemand(QueryDemand a, QueryDemand b)
    {
        if (a.Active != b.Active || a.Priority != b.Priority || a.Facets.Count != b.Facets.Count) return false;
        for (int i = 0; i < a.Facets.Count; i++) if (a.Facets[i] != b.Facets[i]) return false;
        return true;
    }

}

/// <summary>
/// A sibling of <see cref="QuerySignalBinding{T}"/> that also owns a pure projection from the query's value to a
/// page model (<typeparamref name="TModel"/>). The projection NEVER runs on the caller's thread: every snapshot —
/// the subscribe replay at construction, a reactivation replay, a background publication — is queued and projected
/// on the thread pool (latest wins; snapshots superseded while one is being projected are skipped outright), so the
/// (potentially expensive, whole-collection) mapping never lands on the UI thread even for a warm query whose handle
/// replays synchronously into the constructor. Only the cheap per-frame merges a caller does in <c>published</c> run
/// on the UI thread. A status-only republication (the query's <c>Value</c> reference unchanged — queued → fetching →
/// present against the same data) is detected by reference equality and reuses the previously computed model.
/// Parking, the delivery cooldown (<see cref="QueryDelivery.DeliveryCooldownMs"/>) and the <see cref="Snapshot"/>
/// signal work exactly as in <see cref="QuerySignalBinding{T}"/>.
/// </summary>
public sealed class QuerySignalBinding<T, TModel> : IQuerySignalBinding, IObserver<QuerySnapshot<T>>
{
    public QueryDemand Demand => _active ? _demand : QueryDemand.None;
    public long Revision => Snapshot.Peek().Revision;
    readonly object _gate = new();
    readonly IQueryHandle<T> _query;
    readonly Action<Action> _post;
    readonly Func<QuerySnapshot<T>, TModel> _project;
    // Does the projection read anything BESIDES the value — the publication's facts? A value-only projection reuses its
    // model whenever the value reference is unchanged; a facts-reading one must also see the same FactsRevision.
    readonly bool _projectionReadsFacts;
    readonly Action<QuerySnapshot<T>, TModel>? _published;
    readonly Action<Exception>? _failed;
    readonly TimeProvider _time;
    readonly Signal<Exception?> _failure = new(null);
    Exception? _latestError;
    // The outcome of the last projection ATTEMPT (whichever value it ran against) — kept apart from _latestError so a
    // terminal OnError (a different failure channel entirely) never gets silently overwritten by a stale success/fail
    // replayed from the reference-equality fast path below.
    Exception? _lastProjectionError;
    readonly IDisposable _subscription;
    QuerySnapshot<T> _latest;
    // The newest snapshot still awaiting projection, and whether a pool worker currently owns the projection loop.
    QuerySnapshot<T>? _pending;
    bool _projecting, _projected;
    T _lastProjectedValue = default!;
    long _lastProjectedFactsRevision;
    TModel _lastModel = default!;
    QueryDemand _demand = QueryDemand.Initial;
    bool _active, _posted, _disposed;
    long _generation;
    // See QuerySignalBinding<T>: the trailing-edge timer of the delivery cooldown.
    ITimer? _cooldown;
    DateTimeOffset? _lastDelivery;

    public Signal<QuerySnapshot<T>> Snapshot { get; }
    public IReadSignal<Exception?> Failure => _failure;
    readonly Signal<IReadOnlyDictionary<ResourceKey, ResourceSnapshot>> _resources;
    readonly Signal<IReadOnlyDictionary<ResourceKey, QueryFact>> _facts;
    readonly Signal<long> _factsRevision;
    public IReadSignal<IReadOnlyDictionary<ResourceKey, ResourceSnapshot>> Resources => _resources;
    public IReadSignal<IReadOnlyDictionary<ResourceKey, QueryFact>> Facts => _facts;
    public IReadSignal<long> FactsRevision => _factsRevision;

    /// <summary>A projection over the query's VALUE alone.</summary>
    public QuerySignalBinding(IQueryHandle<T> query, Action<Action> post, Func<T, TModel> project,
        Action<QuerySnapshot<T>, TModel>? published = null, Action<Exception>? failed = null, TimeProvider? time = null)
        : this(query, post, WithValueOnly(project), readsFacts: false, published, failed, time) { }

    /// <summary>A projection over the whole SNAPSHOT — for a page model that folds the publication's FACTS in (the
    /// detail model's has-video roll-up reads the video-association plane). Such a model is recomputed when the value
    /// reference OR the facts revision moves; the value-only constructor skips the projection whenever the value
    /// reference is unchanged. A factory rather than a second constructor: two constructors taking a one-argument
    /// lambda are ambiguous at every existing call site.</summary>
    public static QuerySignalBinding<T, TModel> OverSnapshot(IQueryHandle<T> query, Action<Action> post,
        Func<QuerySnapshot<T>, TModel> project, Action<QuerySnapshot<T>, TModel>? published = null,
        Action<Exception>? failed = null, TimeProvider? time = null)
        => new(query, post, project, readsFacts: true, published, failed, time);

    QuerySignalBinding(IQueryHandle<T> query, Action<Action> post, Func<QuerySnapshot<T>, TModel> project,
        bool readsFacts, Action<QuerySnapshot<T>, TModel>? published, Action<Exception>? failed, TimeProvider? time)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _projectionReadsFacts = readsFacts;
        _published = published;
        _failed = failed;
        _time = time ?? TimeProvider.System;
        _latest = query.Current;
        Snapshot = new(_latest);
        _resources = new(_latest.Resources);
        _facts = new(_latest.Facts);
        _factsRevision = new(_latest.FactsRevision);
        // A handle replays its current snapshot synchronously into Subscribe; that lands in OnNext → Enqueue, so the
        // first projection is already a pool job, not constructor work on the UI thread.
        _subscription = query.Changes.Subscribe(this);
    }

    static Func<QuerySnapshot<T>, TModel> WithValueOnly(Func<T, TModel> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return snapshot => project(snapshot.Value);
    }

    public void SetActive(bool active)
    {
        QuerySnapshot<T>? replay = null;
        lock (_gate)
        {
            if (_disposed || _active == active) return;
            _active = active;
            _generation++;
            _posted = false;
            if (active)
            {
                // A new generation owes the previous view's cooldown nothing: its first delivery is a leading edge,
                // whether it comes from the replay projection below or straight from the already-projected latest.
                _lastDelivery = null;
                var current = _query.Current;
                if (current.Revision >= _latest.Revision) replay = current;
            }
        }
        _query.SetDemand(active ? _demand : QueryDemand.None);
        // The replay is projected on the pool like any other snapshot and schedules its own delivery; only a
        // reactivation with nothing newer to project posts the already-projected latest straight away. Either way an
        // activation is the leading edge of its own generation and never waits behind the previous view's cooldown.
        if (replay is { } snapshot) Enqueue(snapshot);
        else if (active) Schedule(immediate: true);
    }

    public void SetDemand(QueryDemand demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        bool active;
        lock (_gate)
        {
            if (_disposed) return;
            if (SameDemand(_demand, demand)) return;
            _demand = demand;
            active = _active;
        }
        _query.SetDemand(active ? demand : QueryDemand.None);
    }

    public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        return _query.RefreshAsync(cancellationToken);
    }

    public void OnNext(QuerySnapshot<T> value) => Enqueue(value);

    /// <summary>Hands a snapshot to the pool-owned projection loop. Latest wins: a snapshot still waiting when a newer
    /// one arrives is dropped unprojected. Exactly one loop runs at a time, so projections stay serialized.</summary>
    void Enqueue(QuerySnapshot<T> value)
    {
        // Revision zero is the typed acquisition seed. It is not a result to project or publish.
        if (value.Revision == 0) return;
        lock (_gate)
        {
            if (_disposed || value.Revision < _latest.Revision || (_pending is { } queued && value.Revision < queued.Revision)) return;
            _pending = value;
            if (_projecting) return;
            _projecting = true;
        }
        _ = Task.Run(ProjectPending);
    }

    void ProjectPending()
    {
        while (true)
        {
            QuerySnapshot<T> value;
            lock (_gate)
            {
                if (_disposed || _pending is not { } next) { _projecting = false; return; }
                _pending = null;
                value = next;
            }
            if (Absorb(value)) Schedule();
        }
    }

    // Query failures are values (Problems); an observer terminal event never fabricates empty primary data.
    public void OnError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate) { if (_disposed) return; _latestError = error; }
        System.Diagnostics.Trace.TraceError("Query subscription terminated: {0}", error);
        Schedule();
    }
    public void OnCompleted() { }

    /// <summary>Runs the projection (unless <paramref name="value"/>'s <c>Value</c> is reference-equal to the last
    /// projected value — a status-only republication) and stores the result. Pool thread only, never while holding
    /// <see cref="_gate"/>: the projection is caller-supplied and may walk a whole collection. Returns false when the
    /// snapshot is stale (superseded by a newer revision) and nothing was stored.</summary>
    bool Absorb(QuerySnapshot<T> value)
    {
        bool sameValue;
        lock (_gate)
        {
            if (_disposed || value.Revision < _latest.Revision) return false;
            sameValue = _projected && ReferenceEquals(value.Value, _lastProjectedValue)
                && (!_projectionReadsFacts || value.FactsRevision == _lastProjectedFactsRevision);
        }
        TModel model;
        Exception? error;
        if (sameValue)
        {
            lock (_gate) { model = _lastModel; error = _lastProjectionError; }
        }
        else
        {
            error = null;
            try { model = _project(value); }
            catch (Exception ex) { model = default!; error = ex; }
        }
        lock (_gate)
        {
            if (_disposed || value.Revision < _latest.Revision || (_pending is { } newer && newer.Revision > value.Revision)) return false;
            _latest = value;
            _lastProjectedValue = value.Value;
            _lastProjectedFactsRevision = value.FactsRevision;
            _projected = true;
            _lastProjectionError = error;
            if (error is null) { _lastModel = model; _latestError = null; }
            else _latestError = error;
            return true;
        }
    }

    /// <summary>Coalesced UI delivery — the same leading-edge + trailing-edge cooldown
    /// <see cref="QuerySignalBinding{T}"/> documents. A failure never waits.</summary>
    void Schedule(bool immediate = false)
    {
        long generation;
        TimeSpan wait;
        lock (_gate)
        {
            if (_disposed || !_active || _posted) return;
            wait = immediate || _latestError is not null || _latest.Failure is not null ? TimeSpan.Zero : Cooldown();
            _posted = true;
            generation = _generation;
            if (wait > TimeSpan.Zero)
            {
                _cooldown ??= _time.CreateTimer(static state => ((QuerySignalBinding<T, TModel>)state!).PostDeferred(), this,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _cooldown.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        _post(() => Deliver(generation));
    }

    /// <summary>Caller holds <see cref="_gate"/>. How long the pending delivery still owes the cooldown.</summary>
    TimeSpan Cooldown()
    {
        if (_lastDelivery is not { } last) return TimeSpan.Zero;
        var window = TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs);
        var elapsed = _time.GetUtcNow() - last;
        return elapsed < TimeSpan.Zero || elapsed >= window ? TimeSpan.Zero : window - elapsed;
    }

    /// <summary>The trailing edge: the cooldown expired with a delivery still pending.</summary>
    void PostDeferred()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || !_active || !_posted) return;
            generation = _generation;
        }
        _post(() => Deliver(generation));
    }

    void Deliver(long generation)
    {
        QuerySnapshot<T> value;
        TModel model;
        Exception? failure;
        lock (_gate)
        {
            if (_disposed || !_active || generation != _generation) return;
            _posted = false;
            _lastDelivery = _time.GetUtcNow();
            value = _latest;
            model = _lastModel;
            failure = _latestError;
        }
        _resources.Value = value.Resources;
        _facts.Value = value.Facts;
        _factsRevision.Value = value.FactsRevision;
        Snapshot.Value = value;
        _failure.Value = failure ?? (value.Failure is { } queryError ? new InvalidOperationException(queryError.Message) : null);
        if (failure is null) _published?.Invoke(value, model);
        else _failed?.Invoke(failure);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _posted = false;
        }
        _cooldown?.Dispose();
        _subscription.Dispose();
        _query.SetDemand(QueryDemand.None);
        _query.Dispose();
    }

    static bool SameDemand(QueryDemand a, QueryDemand b)
    {
        if (a.Active != b.Active || a.Priority != b.Priority || a.Facets.Count != b.Facets.Count) return false;
        for (int i = 0; i < a.Facets.Count; i++) if (a.Facets[i] != b.Facets[i]) return false;
        return true;
    }

}

/// <summary>Unknown primary data stays pending; known empty data is ready. Degraded optional facts never erase data.</summary>
public static class QueryPresentationRules
{
    /// <param name="connecting">The shell is still attempting to restore a session (findings 4.2's pre-login deep
    /// link): the runtime starts with an empty offline scope before any resume attempt lands, so an "offline"
    /// verdict during that window is a false "Something went wrong" over the honest "Connecting…" the header already
    /// shows. Callers thread this through from <c>PlaybackBridge.AuthState == ShellAuthState.Connecting</c>.</param>
    public static Exception? InitialFailure<T>(QuerySnapshot<T> snapshot, string unavailable = "This item is not available.",
        bool connecting = false)
    {
        if (snapshot.Status.HasPrimaryData || snapshot.Status.IsRefreshing) return null;
        // A superseded node (its resources were read under a scope the session has since left) is about to be
        // replaced by a re-acquire under the new scope — never a failure, whatever failure/problems it still carries
        // from the dead scope.
        if (snapshot.Status.Superseded) return null;
        if (snapshot.Failure is { } failure) return new InvalidOperationException(failure.Message);
        if (snapshot.Problems.Count > 0)
            return new InvalidOperationException(snapshot.Problems[0].Error?.Message ?? unavailable);
        return snapshot.Status.IsOffline && !connecting ? new InvalidOperationException("No saved copy is available offline.") : null;
    }
}
