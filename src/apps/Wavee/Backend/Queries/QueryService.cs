using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.Core.Diagnostics;

namespace Wavee.Backend.Queries;

/// <summary>Shared dependency projections. Leases own demand; projected snapshots own no network operations.</summary>
public sealed class QueryService : IQueryService, IDisposable
{
    readonly object _gate = new();
    readonly CatalogRepository _catalog;
    readonly IResourceCoordinator _resources;
    readonly IQueryReplicaDemand _replicaDemand;
    readonly ActiveCatalogDemand? _active;
    readonly Dictionary<Type, Func<object, object>> _definitions = new();
    readonly Dictionary<object, INode> _nodes = new();
    // Copy-on-write: OnCatalogChange runs once per publication (many times a second under a busy sync) and must not
    // pay a fresh `_nodes.Values.ToArray()` every time. Rebuilt only where `_nodes` itself is mutated (Acquire/Release,
    // both already under `_gate`); a reader takes the lock just to grab the current reference, never to iterate.
    INode[] _nodeSnapshot = [];
    readonly IDisposable _catalogSubscription;
    readonly IDisposable _replicaSubscription;
    readonly IDisposable? _protocolSubscription;
    readonly CancellationTokenSource _lifetime = new();
    bool _disposed;
    Task _trimTask = Task.CompletedTask;
    long _trimQuietUntil;
    internal const int ResidentHighWater = 12000, ResidentTarget = 8000;
    /// <summary>Change-driven trims are coalesced into one look per quiet period. A busy sync publishes many times
    /// a second and every look took the catalog gate for <see cref="CatalogRepository.ResidentCount"/>; above the
    /// high-water mark it then sorted the whole entries table under that gate. Explicit callers are never throttled.</summary>
    internal const int TrimQuietMs = 2000;

    public QueryService(CatalogRepository catalog, IResourceCoordinator resources,
        IQueryReplicaDemand replicaDemand, IObservable<ReplicaChange> replicaChanges, ActiveCatalogDemand? active = null,
        IObservable<bool>? protocolChanges = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _replicaDemand = replicaDemand ?? throw new ArgumentNullException(nameof(replicaDemand));
        _active = active;
        _catalogSubscription = catalog.Changes.Subscribe(new ActionObserver<CatalogChangeSet>(OnCatalogChange));
        _replicaSubscription = replicaChanges.Subscribe(new ActionObserver<ReplicaChange>(OnReplicaChange));
        // CatalogRuntime.ProtocolChanges (true = a live protocol/LibrarySync session just installed): nudge every
        // active-leased node whose current requirements include a replica request, so one that fired — and was
        // answered unserved — before go-live installed the protocol gets asked for real instead of staying dropped
        // for the node's whole lifetime (findings: LibraryQueryDemand.EnsureAsync used to silently no-op on a null
        // session with nobody ever re-asking). No-op for a demo/offline runtime that never publishes a protocol.
        _protocolSubscription = protocolChanges?.Subscribe(new ActionObserver<bool>(OnProtocolChange));
    }

    public void Register<TSpec, T>(Func<TSpec, IQueryDefinition<T>> create) where TSpec : QuerySpec<T>
    {
        ArgumentNullException.ThrowIfNull(create);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _definitions.Add(typeof(TSpec), spec => create((TSpec)spec));
        }
    }

    public IQueryHandle<T> Acquire<T>(QuerySpec<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Node<T> node;
        Node<T>.Handle handle;
        bool initialize = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_nodes.TryGetValue(query, out var existing))
            {
                if (!_definitions.TryGetValue(query.GetType(), out var create))
                    throw new NotSupportedException("No query definition registered for " + query.GetType().Name);
                existing = new Node<T>(this, query, (IQueryDefinition<T>)create(query));
                initialize = true;
                _nodes.Add(query, existing);
                _nodeSnapshot = _nodes.Values.ToArray();
            }
            node = (Node<T>)existing;
            handle = node.Lease();
        }
        try { if (initialize) node.Recompute(); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public async Task<QuerySnapshot<T>> ReadOnceAsync<T>(QuerySpec<T> query, QueryDemand? demand = null,
        CancellationToken cancellationToken = default)
    {
        using var handle = (Node<T>.Handle)Acquire(query);
        return await handle.ReadOnceAsync(demand ?? QueryDemand.Initial, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A detached snapshot of current active lease demand, used by cache retention. This performs no I/O.</summary>
    public IReadOnlyCollection<ResourceKey> GetActiveResourceKeys()
    {
        var into = new HashSet<ResourceKey>();
        CollectActiveResourceKeys(into);
        return into;
    }

    /// <summary>Same answer as <see cref="GetActiveResourceKeys"/>, written into a caller-owned, reused set instead
    /// of allocating a fresh one — <see cref="CatalogRepository.TrimUnpinned"/> calls this once per (quiet-coalesced)
    /// trim look, and a busy sync used to pay a ~1.7k-entry <c>SelectMany().Distinct().ToArray()</c> for that every
    /// time.</summary>
    internal void CollectActiveResourceKeys(HashSet<ResourceKey> into)
    {
        INode[] nodes;
        lock (_gate) nodes = _nodes.Values.ToArray();
        foreach (var node in nodes)
            foreach (var key in node.DemandedResources()) into.Add(key);
    }

    internal Task TrimInactiveAsync()
    {
        int resident = _catalog.ResidentCount;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_trimTask.IsCompleted) return _trimTask;
            if (resident <= ResidentHighWater) return Task.CompletedTask;
            return _trimTask = Task.Run(() =>
            {
                try { _catalog.TrimUnpinned(CollectActiveResourceKeys, ResidentTarget); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("Catalog resident trim failed: {0}", error); }
            });
        }
    }

    void OnCatalogChange(CatalogChangeSet change)
    {
        INode[] nodes;
        lock (_gate) nodes = _nodeSnapshot;
        // A session that REPLACED the scope invalidates answers a node cannot know it was holding, so it re-joins
        // everything. A session that merely CONFIRMED the scope the app was already serving (the AP welcome certifying
        // the provisional scope this launch recalled) invalidates nothing: it names only the keys that actually moved,
        // so the ordinary dependency test is both sufficient and — on a warm launch, where that set is empty — free.
        bool everyNode = SessionFanOutRules.RecomputesEveryNode(change.Kind, change.Confirmed);
        foreach (var node in nodes)
            // Remote demand and observation have separate lifetimes. A passive lease still observes committed
            // local state; parked UI bindings independently suppress delivery. Rejoins remain worker-coalesced.
            if (node.LeaseCount > 0 && (everyNode || node.DependsOn(change.Keys)))
                node.RecomputeCatalog(change.Keys, change.Kind, change.Confirmed);
        TrimAfterChange();
    }

    /// <summary>At most one resident-trim look per <see cref="TrimQuietMs"/>, whatever the publication rate.</summary>
    void TrimAfterChange()
    {
        long now = Environment.TickCount64, quiet = Interlocked.Read(ref _trimQuietUntil);
        if (now < quiet || Interlocked.CompareExchange(ref _trimQuietUntil, now + TrimQuietMs, quiet) != quiet) return;
        _ = TrimInactiveAsync();
    }

    void OnReplicaChange(ReplicaChange change)
        => NotifyDependencyChanged(change.AggregateId);

    void OnProtocolChange(bool available)
    {
        if (!available) return;
        INode[] nodes;
        lock (_gate) nodes = _nodeSnapshot;
        foreach (var node in nodes) if (node.HasActiveLease) node.RetryUnservedDemand();
    }

    public void NotifyDependencyChanged(string dependency)
    {
        INode[] nodes;
        lock (_gate) nodes = _nodes.Values.ToArray();
        foreach (var node in nodes) if (node.DependsOnReplica(dependency)) node.RecomputeReplica(dependency);
    }

    void Release(object key, INode node)
    {
        bool removed = false;
        lock (_gate)
            if (_nodes.TryGetValue(key, out var current) && ReferenceEquals(node, current) && node.LeaseCount == 0)
            { _nodes.Remove(key); _nodeSnapshot = _nodes.Values.ToArray(); removed = true; }
        if (removed) node.Dispose();
        _ = Task.Run(TrimInactiveAsync);
    }

    public void Dispose()
    {
        INode[] nodes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            nodes = _nodes.Values.ToArray();
            _nodes.Clear();
        }
        _lifetime.Cancel();
        _catalogSubscription.Dispose();
        _replicaSubscription.Dispose();
        _protocolSubscription?.Dispose();
        foreach (var node in nodes) node.Dispose();
        _lifetime.Dispose();
    }

    interface INode : IDisposable
    {
        int LeaseCount { get; }
        bool HasActiveLease { get; }
        bool DependsOn(IReadOnlyList<ResourceKey> keys);
        bool DependsOnReplica(string id);
        void Recompute();
        void RecomputeCatalog(IReadOnlyList<ResourceKey> keys, CatalogChangeKind kind, bool confirmed = false);
        IReadOnlyList<ResourceKey> DemandedResources();
        void RecomputeReplica(string id);
        /// <summary>CatalogRuntime.ProtocolChanges published true. Retries replica requests that fired before
        /// go-live, and catalog keys that are still Unknown/Offline — never a resident key that already has a
        /// terminal answer. A same-account login used to reset <c>_demandSettled</c> on every node and re-Ensure
        /// the world.</summary>
        void RetryUnservedDemand();
        /// <summary>Resident cost, lock-free: the last pass's resource map size and its type name (for the sampler).</summary>
        int DependencyCount { get; }
        string SpecName { get; }
    }

    /// <summary>The query graph's footprint for <c>mem.sample</c>: nodes (leased / idle), the sum of every node's
    /// last-pass resource map (each entry is a <see cref="ResourceSnapshot"/> reference the node keeps alive — the
    /// count that grows when pages leave nodes behind), and the three largest nodes by that count. Volatile reads
    /// only; no gate beyond the node-array grab.</summary>
    public string Diagnostics()
    {
        INode[] nodes;
        lock (_gate) nodes = _nodeSnapshot;
        int leased = 0; long deps = 0;
        INode? a = null, b = null, c = null;
        foreach (var node in nodes)
        {
            if (node.HasActiveLease) leased++;
            int d = node.DependencyCount;
            deps += d;
            if (a is null || d > a.DependencyCount) { c = b; b = a; a = node; }
            else if (b is null || d > b.DependencyCount) { c = b; b = node; }
            else if (c is null || d > c.DependencyCount) c = node;
        }
        var sb = new System.Text.StringBuilder(160);
        sb.Append("nodes=").Append(nodes.Length).Append(" leased=").Append(leased).Append(" deps=").Append(deps).Append(" top=");
        if (a is not null) sb.Append(a.SpecName).Append(':').Append(a.DependencyCount);
        if (b is not null) sb.Append(',').Append(b.SpecName).Append(':').Append(b.DependencyCount);
        if (c is not null) sb.Append(',').Append(c.SpecName).Append(':').Append(c.DependencyCount);
        return sb.ToString();
    }

    sealed class ActionObserver<TEvent>(Action<TEvent> action) : IObserver<TEvent>
    {
        public void OnNext(TEvent value) => action(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    sealed class Node<T> : INode
    {
        // UI lifetime and scheduling never acquire the projection gate. The worker may hold _gate during
        // a complete catalog join, while Acquire/Current/SetDemand/Subscribe/Dispose keep returning promptly.
        readonly object _leaseGate = new();
        readonly object _gate = new();
        // Guards ONLY _pendingFull/_pendingChangeKeys. Scheduling entry points (RecomputeReplica, RecomputeCatalog,
        // Recompute, QueueRejoin, ProjectLoop's replan fallback) must stay non-blocking per the invariant above —
        // they used to take _gate for this, which a parked Project pass can hold indefinitely, deadlocking any
        // caller that schedules from a UI/demand thread while a definition's Read is parked. This gate is never
        // held while acquiring another lock, so it can never be the far side of an inversion.
        readonly object _pendingGate = new();
        readonly QueryService _owner;
        readonly QuerySpec<T> _spec;
        IQueryDefinition<T> _definition;
        readonly IAsyncQueryDefinition<T>? _asyncDefinition;
        long _prepareGeneration, _preparedGeneration = -1;
        Task? _prepareTask;
        CancellationTokenSource? _prepareCancellation;
        ResourceError? _preparationFailure;
        ResourceError? _queryFailure;
        readonly List<Handle> _leases = new();
        Handle[] _leaseHandles = [];
        QueryDemand[] _leaseDemands = [];
        QueryDemand[] _plannedLeaseDemands = [];
        int _finiteActiveCount;
        bool _projectionRunning, _projectionRequested, _projectRequested, _releaseObsoleteRequested, _invalidateRequested;
        bool _replanRequested;
        Task _projectionTask = Task.CompletedTask;
        QueryRequirements _activeRequirements = QueryRequirements.Empty;
        ResourcePriority _activePriority;
        readonly Dictionary<QueryDemand, QueryRequirements> _finiteRequirements = new();
        IReadOnlyList<ResourceKey> _retentionKeys = [];
        readonly HashSet<ResourceKey> _coldAsked = new();
        readonly List<QueryDemand> _finiteDemands = [];
        readonly Dictionary<ResourceKey, Task> _coldTasks = new();
        readonly HashSet<ResourceKey> _coldPending = new();
        readonly CancellationTokenSource _lifetime;
        readonly CancellationToken _token;
        readonly Dictionary<ResourceKey, (ResourceError Error, long Revision)> _readErrors = new();
        readonly Dictionary<ResourceKey, long> _revalidationAttempts = new();
        Dictionary<ResourceKey, long> _revalidations = new();
        // The dependency set IS the last pass's resource map (its key set is exactly what the join observed), so no
        // separate 10k-entry HashSet is rebuilt per pass — that rebuild was a large-object-heap allocation per arrival
        // wave, and LOH churn is what schedules gen2 collections in the middle of a scroll.
        ResourceMap _dependencies = ResourceMap.Empty;
        // Reused across every pass this node ever runs: SetIfDifferent copies a touched 256-wide chunk once per
        // pass, Reset(_dependencies) rebases it onto the new committed map for the next one.
        readonly ResourceMap.Builder _resourceBuilder = ResourceMap.Empty.ToBuilder();
        // A reused per-node set every REJOIN pass's Read() calls land in (never a dependency scope's — those are
        // sized for a handful of keys and outlive nothing). Feeds QueryPassStats.KeysObserved for tests.
        readonly HashSet<ResourceKey> _observedBuffer = new();
        // Keys a Durable/ColdRead catalog publication actually named since this node's last pass — accumulated by
        // RecomputeCatalog (reused set, cleared once a pass captures it), so the next rejoin only re-Peeks the
        // catalog for a key that moved, one not yet in the map, or everything when `_pendingFull`.
        HashSet<ResourceKey> _pendingChangeKeys = new();
        // Set by a Session/scope-replace publication (or a change set that named no keys at all — trust the fan-out,
        // not an empty key list) and by the node's own first pass. A precise per-key change list either doesn't
        // exist for these or isn't safe to trust, so the next rejoin re-Peeks everything, exactly like before this
        // change-set-driven pass existed.
        bool _pendingFull = true;
        // Keys a plan-only pass (TryReplan) required but the last join never observed AND the repository does not
        // yet know — collected instead of forcing a full pass, since a plan can still ask the coordinator for them.
        // Published like _dependencies: DependsOn also answers true for a key in here, so the catalog change that
        // lands the fetched value schedules the rejoin that finally presents it. A full pass clears this — its own
        // join either observes the key (moving it into _dependencies) or the repository still doesn't know it,
        // in which case the very next plan-only pass collects it again.
        HashSet<ResourceKey> _awaited = new();
        HashSet<string> _replicas = new(StringComparer.Ordinal);
        QuerySnapshot<T>? _current;
        // A join whose result a newer viewport superseded left `_current` behind that join. Only a full pass
        // may recover it: a plan-only pass would publish the stale value with a bumped revision.
        bool _joinSuperseded;
        long _revision;
        long _demandVersion;
        long _replicaDemandGeneration;
        CancellationTokenSource? _demandRunCancellation;
        readonly Dictionary<ResourceKey, CancellationTokenSource> _demandWaiters = new();
        HashSet<ResourceKey> _desiredResources = [];
        // Last catalog key set a demand run actually settled against. A catalog publication that only
        // fills keys this node already asked for must not start a fresh RunDemandAsync (empty `asked`
        // → re-Ensure every key → another all-ready catalog.demand.wave). Facet/activation growth still
        // starts a run because the new set is not equal to this one.
        HashSet<ResourceKey> _settledResources = [];
        bool _demandSettled;
        bool _seedResidentOnNextRun;
        bool _planning;
        volatile bool _disposed;

        public Node(QueryService owner, QuerySpec<T> spec, IQueryDefinition<T> definition)
        {
            _owner = owner; _spec = spec; _definition = definition; _asyncDefinition = definition as IAsyncQueryDefinition<T>;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner._lifetime.Token);
            _token = _lifetime.Token;
            var pending = definition.Pending;
            _current = new(0, pending.OrderRevision, pending.Value, new(false, true, false), []);
        }

        public int LeaseCount { get { lock (_leaseGate) return _leases.Count; } }
        // A finite demand (ReadOnceAsync/RefreshAsync's own resolution loop) counts as active too: it calls
        // Recompute() itself once its own reads land, but a catalog change mid-flight must still be able to move it
        // via the normal path rather than being silently skipped for the window the finite demand is outstanding.
        public bool HasActiveLease
        { get => Volatile.Read(ref _finiteActiveCount) > 0 || Volatile.Read(ref _leaseDemands).Any(static demand => demand.Active); }
        public QuerySnapshot<T> Current => Volatile.Read(ref _current)!;
        public int DependencyCount => Volatile.Read(ref _dependencies).Count;
        public string SpecName => _spec.GetType().Name;
        public bool DependsOn(IReadOnlyList<ResourceKey> keys)
        {
            var dependencies = Volatile.Read(ref _dependencies);
            var awaited = Volatile.Read(ref _awaited);
            foreach (var key in keys) if (dependencies.ContainsKey(key) || awaited.Contains(key)) return true;
            return _asyncDefinition?.IsInvalidatedBy(keys) == true;
        }
        public bool DependsOnReplica(string id) => Volatile.Read(ref _replicas).Contains(id) || _asyncDefinition?.IsInvalidatedByReplica(id) == true;

        public Handle Lease()
        {
            lock (_leaseGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var handle = new Handle(this);
                _leases.Add(handle);
                CaptureLeases();
                return handle;
            }
        }

        public void RecomputeReplica(string id)
        {
            // A replica-driven rejoin has no precise catalog key list of its own (it exists to re-read Replicas.*,
            // not a tracked Durable/ColdRead change) — force a full refresh rather than risking a stale catalog Peek.
            lock (_pendingGate) _pendingFull = true;
            _ = QueueProjection(true, _asyncDefinition?.IsInvalidatedByReplica(id) == true);
        }

        public void RetryUnservedDemand()
        {
            // Project owns publication -> catalog -> node while joining. Protocol installation calls us on
            // another worker: owning node while waiting for catalog would deadlock that join and every UI reader.
            bool retry = _owner._catalog.ReadConsistent(() =>
            {
                lock (_gate)
                {
                    if (_disposed) return false;
                    bool unservedCatalog = false;
                    foreach (var key in _desiredResources)
                    {
                        var snapshot = _owner._catalog.Peek(key);
                        if (snapshot.Knowledge == Knowledge.Unknown || snapshot.Activity == ResourceActivity.Offline)
                        { unservedCatalog = true; break; }
                    }
                    bool unservedReplica = _activeRequirements.Replicas.Count > 0;
                    if (!unservedCatalog && !unservedReplica) return false;
                    if (unservedReplica) Interlocked.Increment(ref _replicaDemandGeneration);
                    _demandSettled = false;
                    _seedResidentOnNextRun = true;
                    return true;
                }
            });
            if (retry) Recompute(false);
        }

        public void RecomputeCatalog(IReadOnlyList<ResourceKey> keys, CatalogChangeKind kind, bool confirmed = false)
        {
            // The change set itself says whether a joined value can have moved. An Activity publication changes no
            // knowledge and no value, so it never re-joins; a Durable/ColdRead/replacing-Session one always does.
            // A CONFIRMING session (same account+scope the launch already served) invalidates nothing — treating it
            // like a replace set `_pendingFull` and rebuilt every snapshot after silent login.
            bool joined = kind is CatalogChangeKind.Durable or CatalogChangeKind.ColdRead
                || (kind == CatalogChangeKind.Session && !confirmed);
            if (joined)
                lock (_pendingGate)
                {
                    // Session/scope replace (or a fan-out whose keys list is empty) invalidates by construction that
                    // a per-key list cannot express precisely — trust the fan-out, refresh everything on the next pass.
                    if ((kind == CatalogChangeKind.Session && !confirmed) || keys.Count == 0) _pendingFull = true;
                    else foreach (var key in keys) _pendingChangeKeys.Add(key);
                }
            _ = QueueProjection(joined, kind is CatalogChangeKind.Durable or CatalogChangeKind.Session
                && !confirmed && _asyncDefinition?.IsInvalidatedBy(keys) == true);
        }

        public void Recompute() => Recompute(true);
        void Recompute(bool project)
        {
            // Every OTHER trigger for a rejoin (initial acquire, a swapped async definition, an error retry) has
            // no precise catalog key list — only RecomputeCatalog's own Durable/ColdRead path does. Force a full
            // refresh here so the change-set fast path in QueryReadContext never answers from a stale cached value.
            if (project) lock (_pendingGate) _pendingFull = true;
            _ = QueueProjection(project);
        }
        /// <summary>Demand moved, nothing else. Queues a plan-only pass on the same serialized worker.</summary>
        public void Replan() => _ = QueueProjection(false, replanOnly: true);

        /// <summary>An explicit rejoin request (RefreshAsync/ReadOnceAsync's own demand loop, a landed replica
        /// wave) with no precise catalog key list of its own — force a full refresh for whichever pass services
        /// this request rather than risk the change-set fast path answering from a stale cached value.</summary>
        Task QueueRejoin(bool invalidatePreparation = false)
        {
            lock (_pendingGate) _pendingFull = true;
            return QueueProjection(true, invalidatePreparation);
        }

        Task QueueProjection(bool project, bool invalidatePreparation = false, bool replanOnly = false)
        {
            lock (_leaseGate)
            {
                if (_disposed) return Task.CompletedTask;
                if (replanOnly) _replanRequested = true;
                else { _projectionRequested = true; _projectRequested |= project; }
                _invalidateRequested |= invalidatePreparation;
                if (_projectionRunning) return _projectionTask;
                _projectionRunning = true;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _projectionTask = done.Task;
                _ = Task.Run(() => ProjectLoop(done));
                return done.Task;
            }
        }

        void ProjectLoop(TaskCompletionSource done)
        {
            try
            {
                while (true)
                {
                    bool project, invalidate, replan = false;
                    lock (_leaseGate)
                    {
                        if (_disposed || !(_projectionRequested || _replanRequested)) { _projectionRunning = false; return; }
                        if (_projectionRequested)
                        {
                            project = _projectRequested;
                            invalidate = _invalidateRequested;
                            _invalidateRequested = false;
                            // A join re-plans as part of its own pass; a pending plan-only request is subsumed.
                            _projectRequested = _projectionRequested = _replanRequested = false;
                        }
                        else { project = invalidate = false; replan = true; _replanRequested = false; }
                    }
                    try
                    {
                        if (invalidate) InvalidatePreparation();
                        if (replan)
                        {
                            // TryReplan's own fallback: no precise catalog key list drove this rejoin either.
                            if (!TryReplan()) { lock (_pendingGate) _pendingFull = true; Project(true); }
                        }
                        else Project(project);
                    }
                    catch (Exception error)
                    {
                        QuerySnapshot<T> failed;
                        lock (_gate)
                        {
                            if (_disposed) continue;
                            _queryFailure = new(ResourceErrorKind.InvalidResponse, error.Message);
                            failed = Current with { Revision = ++_revision, Failure = _queryFailure,
                                Status = Current.Status with { IsRefreshing = false } };
                            Volatile.Write(ref _current, failed);
                        }
                        foreach (var handle in Volatile.Read(ref _leaseHandles)) handle.Publish(failed);
                    }
                }
            }
            finally { done.TrySetResult(); }
        }

        // Called only while the short lease gate is held. Published arrays never mutate afterwards.
        void CaptureLeases()
        {
            Volatile.Write(ref _leaseHandles, _leases.ToArray());
            Volatile.Write(ref _leaseDemands, _leases.Select(static lease => lease.Demand).ToArray());
        }

        /// <summary>A full pass: the value join (inside the catalog's consistent read) followed by the snapshot
        /// build (outside it). Only the join needs the publication gate; the ~10k-entry dictionary work, the error
        /// overlay, the problem list and the equality tests read nothing from the catalog, so holding the gate for
        /// them serialized every other node and every commit behind one page's projection.</summary>
        void Project(bool project)
        {
            var cold = new List<(ResourceKey[] Keys, TaskCompletionSource Done)>();
            QueryReadContext read = null!;
            QueryReadResult<T> projected = null!;
            QueryDemand[] leaseDemands = [];
            bool rejoined = false;
            // Always-on pass timeline (query.pass): where a page's first publication spent its time — waiting for
            // the publication gate, joining, sizing requirements, building/publishing the snapshot, planning demand.
            long t0 = Stopwatch.GetTimestamp(), tGate = 0, tJoin = 0, tRequire = 0;
            bool firstPass = false;
            // Always publication gate -> node gate. A commit can publish to this node while owning the same
            // reentrant publication gate; taking these locks in the reverse order would deadlock a UI read.
            bool joined = _owner._catalog.ReadConsistent(() =>
            {
              lock (_gate)
              {
                tGate = Stopwatch.GetTimestamp();
                firstPass = _revision == 0;
                if (_disposed) return false;
#if DEBUG || FLUENTGPU_DIAG
                // Validation runs here, never by blocking an acquiring UI thread on the catalog's publication gate.
                if (_revision == 0 && QueryScopeRules.IsStale(_spec.Scope, _owner._catalog.IsActiveScope(_spec.Scope)))
                    throw new InvalidOperationException($"{_spec.GetType().Name} was acquired under a stale catalog scope; re-acquire the current scope.");
#endif
                // Captured once: a preparation can swap the definition between the two halves of this pass, and the
                // recipe that sized the join must be the one that names its keys.
                var definition = _definition;
                // Captured and cleared here (not in RecomputeCatalog) so the exact set this pass acts on is the
                // one every concurrent RecomputeCatalog call up to THIS instant contributed to; anything that lands
                // after schedules its own pass via the normal DependsOn/RecomputeCatalog path.
                bool fullRefresh;
                HashSet<ResourceKey> changedKeys;
                lock (_pendingGate)
                {
                    fullRefresh = _pendingFull; _pendingFull = false;
                    changedKeys = _pendingChangeKeys;
                    if (changedKeys.Count > 0) _pendingChangeKeys = new();
                }
                _resourceBuilder.Reset(_dependencies);
                read = new QueryReadContext(_owner._catalog, _resourceBuilder, fullRefresh, changedKeys, _observedBuffer);
                leaseDemands = Volatile.Read(ref _leaseDemands);
                var activeDemand = MergeDemand(leaseDemands);
                // Identical by construction when nothing finite is outstanding — the same reference then serves both
                // the read recipe and the active one, so the recipe is built once instead of three times.
                var readDemand = _finiteDemands.Count == 0 ? activeDemand
                    : MergeDemand(leaseDemands.Concat(_finiteDemands));
                bool demandUnchanged = _finiteDemands.Count == 0 && ReferenceEquals(leaseDemands, _plannedLeaseDemands);
                // A paged relation sizes its own read from the CURRENT demand (RelationProjection.Require moves
                // _requestedEnd), so the recipe must run against the previous value BEFORE the join as well.
                // Same demand + no finite work: last pass already sized that relation; walking a 1.5k-row
                // playlist's RowKeys again here is pure O(n) waste on every catalog batch.
                if (_current is not null && readDemand is not null && !demandUnchanged)
                    definition.Requirements(_current.Value, readDemand);
                // `_joinSuperseded`: the last join's DTO was dropped because the viewport moved under it, so
                // `_current` is behind the catalog. Whatever queued this pass, it re-joins.
                var previous = _current;
                rejoined = project || _joinSuperseded || _revision == 0 || previous is null;
                if (rejoined || previous is null)
                {
                    projected = definition.Read(read);
                    if (_queryFailure?.Kind == ResourceErrorKind.InvalidResponse) _queryFailure = null;
                    _revalidations = new(read.RevalidationCandidates);
                    foreach (var key in _revalidationAttempts.Keys.Where(key => !_revalidations.ContainsKey(key)).ToArray())
                        _revalidationAttempts.Remove(key);
                }
                else
                {
                    // Activity/error-only transitions publish status while preserving the exact joined DTO graph.
                    // forceFresh: this pass exists precisely to notice an activity transition (Queued/Fetching/
                    // Offline) that a Durable/ColdRead change set never names, so it always re-Peeks — the
                    // change-set fast path is for the REJOIN branch above only.
                    foreach (var key in _dependencies.Keys) read.Read(key, allowColdRead: false, forceFresh: true);
                    foreach (var id in _replicas) read.DependOnReplica(id);
                    projected = new(previous.Value, previous.OrderRevision, previous.Status.HasPrimaryData);
                }
                tJoin = Stopwatch.GetTimestamp();
                bool sameValue = previous is not null && ReferenceEquals(projected.Value, previous.Value);
                var readRequirements = readDemand is null ? QueryRequirements.Empty
                    : sameValue && demandUnchanged ? _activeRequirements
                    : definition.Requirements(projected.Value, readDemand);
                foreach (var key in readRequirements.Catalog) read.Read(key);
                _activeRequirements = activeDemand is null ? QueryRequirements.Empty
                    : ReferenceEquals(activeDemand, readDemand) ? readRequirements
                    : sameValue && demandUnchanged ? _activeRequirements
                    : definition.Requirements(projected.Value, activeDemand);
                _plannedLeaseDemands = leaseDemands;
                _activePriority = activeDemand is null ? ResourcePriority.Prefetch : (ResourcePriority)activeDemand.Priority;
                _finiteRequirements.Clear();
                foreach (var demand in _finiteDemands)
                    _finiteRequirements[demand] = demand.Active ? definition.Requirements(projected.Value, demand) : QueryRequirements.Empty;
                tRequire = Stopwatch.GetTimestamp();
                return true;
              }
            });
            if (!joined) return;
            PublishJoin(read, projected, leaseDemands, cold, rejoined);
            long tPublish = Stopwatch.GetTimestamp();
            foreach (var work in cold) _ = Task.Run(() => LoadColdAsync(work.Keys, work.Done));
            PlanDemand();
            long tPlan = Stopwatch.GetTimestamp();
            ReleaseObsoleteWhenPlanned();
            if (_asyncDefinition is not null) _ = PrepareForObservationAsync();
            // The first pass of every node is logged (it is the page's time-to-data); any later pass only when it
            // crossed a frame (8 ms) — a rejoin that long inside a scroll is the jank.
            double totalMs = (tPlan - t0) * TicksToMs;
            if (firstPass || totalMs >= 8.0)
            {
                int coldKeys = 0;
                foreach (var work in cold) coldKeys += work.Keys.Length;
                WaveeLog.Instance.Event(totalMs >= 33.0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "query", firstPass ? "query.first-pass" : "query.pass",
                    "query " + _spec.GetType().Name + " totalMs=" + Ms(totalMs)
                    + " gateWaitMs=" + Ms((tGate - t0) * TicksToMs) + " joinMs=" + Ms((tJoin - tGate) * TicksToMs)
                    + " requireMs=" + Ms((tRequire - tJoin) * TicksToMs) + " publishMs=" + Ms((tPublish - tRequire) * TicksToMs)
                    + " planMs=" + Ms((tPlan - tPublish) * TicksToMs)
                    + " keys=" + read.Resources.Count + " coldKeys=" + coldKeys + " rejoined=" + (rejoined ? 1 : 0)
                    + " hasData=" + (projected.HasPrimaryData ? 1 : 0) + " sinceAcquireMs=" + Ms((tPlan - _acquiredTicks) * TicksToMs)
                    + " sinceNavMs=" + Ms(PerformanceDiagnostics.Navigation.SinceNavigationMs));
            }
        }

        static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        readonly long _acquiredTicks = Stopwatch.GetTimestamp();
        static string Ms(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The second half of a full pass. One walk of the read's resources answers every question the
        /// snapshot needs — the error overlay, the problem list, the refresh/offline/superseded aggregates and
        /// whether anything a consumer can observe actually moved.
        ///
        /// <para><b>Identity contract.</b> When the key set is unchanged and no key's Knowledge, Value, Error,
        /// Revision or offline-ness moved, the PREVIOUS <see cref="QuerySnapshot{T}.Resources"/> dictionary is
        /// published again by reference (as <see cref="QueryFacts.Update"/> already does for Facts), so a
        /// consumer's reference-identity gate holds across a scroll burst instead of being defeated by a fresh
        /// ~10k-entry dictionary per publication. The cost is that Queued/Fetching/Backoff/Idle churn is NOT
        /// mirrored into those snapshot values; it is carried by <see cref="QueryStatus.IsRefreshing"/> instead.
        /// That is safe for every consumer: <c>TrackFactPresentation.State</c> maps Unknown+Idle and
        /// Unknown+Queued/Fetching/Backoff to the same <c>Pending</c>, and the one activity that DOES change what a
        /// row renders — <see cref="ResourceActivity.Offline"/> — is part of the identity test above, so it always
        /// forces a fresh map. Activity histograms are sampled at publications rather than at every activity
        /// transition, which is also the only thing that kept ~18 publications per scroll burst alive.</para></summary>
        void PublishJoin(QueryReadContext read, QueryReadResult<T> projected, QueryDemand[] leaseDemands,
            List<(ResourceKey[] Keys, TaskCompletionSource Done)> cold, bool rejoined)
        {
            // Capture scope status BEFORE taking node._gate. The projection's publication -> catalog -> node
            // order also applies to these status reads; an on-demand ActiveScope lookup inside the node lock
            // would reverse it. Keep the large snapshot-building pass outside the catalog publication gate.
            var scopeCache = new Dictionary<CatalogScope, bool>();
            foreach (var key in read.Resources.Keys) scopeCache.TryAdd(key.Scope, false);
            var scopes = scopeCache.Keys.ToArray();
            var scopeStatus = _owner._catalog.ReadConsistent(() =>
            {
                foreach (var scope in scopes) scopeCache[scope] = _owner._catalog.IsActiveScope(scope);
                return (Online: _owner._catalog.IsOnline, Epoch: _owner._catalog.Epoch);
            });
            QuerySnapshot<T> next;
            bool changed;
            Handle[] handles = [];
            lock (_gate)
            {
                if (_disposed) return;
                var previous = _current;
                var observed = read.Resources;
                foreach (var pair in observed)
                    if (pair.Value.Knowledge != Knowledge.Unknown && _readErrors.TryGetValue(pair.Key, out var cleared)
                        && pair.Value.Revision > cleared.Revision) _readErrors.Remove(pair.Key);
                var previousResources = (ResourceMap?)previous?.Resources;
                bool sameKeys = previousResources is not null && previousResources.Count == observed.Count;
                bool sameFacts = sameKeys;
                bool anyOverlay = false;
                bool refreshing = false, offline = false, superseded = false;
                List<FacetProblem>? found = null;
                foreach (var pair in observed)
                {
                    var value = pair.Value;
                    var error = Overlay(pair.Key, value);
                    if (!ReferenceEquals(error, value.Error)) anyOverlay = true;
                    if (sameKeys)
                    {
                        if (!previousResources!.TryGetValue(pair.Key, out var old)) sameKeys = sameFacts = false;
                        else if (sameFacts && (old.Knowledge != value.Knowledge || !Equals(old.Value, value.Value)
                            || old.Revision != value.Revision || old.Error != error
                            || (old.Activity == ResourceActivity.Offline) != (value.Activity == ResourceActivity.Offline)))
                            sameFacts = false;
                    }
                    if (error is not null || value.Knowledge is Knowledge.Absent or Knowledge.Unsupported)
                        (found ??= []).Add(new(pair.Key, value.Knowledge, error));
                    if (value.Activity is ResourceActivity.Fetching or ResourceActivity.Queued) refreshing = true;
                    // A key whose scope the repository has moved away from (a session switched account/scope while
                    // this node was still attached) is superseded, not offline — its Offline activity is exactly the
                    // stale-scope marker SetSessionCore stamps on every other-scope entry.
                    if (!scopeCache[pair.Key.Scope]) superseded = true;
                    else if (value.Activity == ResourceActivity.Offline) offline = true;
                }
                ResourceMap resources;
                if (sameFacts) resources = previousResources!;
                // No key's overlay differs from its observed Error, so the pass's own builder already IS the
                // published shape — commit it directly instead of paying for a second full-size copy.
                else if (!anyOverlay) resources = read.Commit();
                else
                {
                    // Overlay writes land in the SAME builder (a chunk this touches was either already going to be
                    // copied by this pass's own Read calls, or gets copied here for the first time) instead of a
                    // second full-size dictionary.
                    foreach (var pair in observed)
                    {
                        var error = Overlay(pair.Key, pair.Value);
                        if (!ReferenceEquals(error, pair.Value.Error)) read.Overlay(pair.Key, pair.Value with { Error = error });
                    }
                    resources = read.Commit();
                }
                IReadOnlyList<FacetProblem> problems = found is null ? [] : found.ToArray();
                if (previous is not null && previous.Problems.Count == problems.Count
                    && (problems.Count == 0 || previous.Problems.SequenceEqual(problems))) problems = previous.Problems;
                List<ResourceKey>? coldKeys = null;
                foreach (var key in read.ColdCandidates)
                    if (observed[key].Knowledge == Knowledge.Unknown && _coldAsked.Add(key))
                    {
                        _coldPending.Add(key); (coldKeys ??= []).Add(key);
                    }
                if (coldKeys is not null)
                {
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    foreach (var key in coldKeys) _coldTasks[key] = done.Task;
                    cold.Add((coldKeys.ToArray(), done));
                }
                bool coldPending = false;
                foreach (var key in _coldPending) if (observed.ContainsKey(key)) { coldPending = true; break; }
                // _dependencies is BOTH the observed key set for DependsOn AND the next pass's change-set-driven
                // builder base — so unlike the key-set-only identity contract above, its VALUES must always be this
                // pass's freshly committed map, even when the key set itself (sameKeys) is unchanged. Skipping this
                // write when sameKeys (as a pure "the reference already matches" micro-optimization) would leave the
                // next pass's builder rebased on a map that still holds a value THIS pass just Peeked past.
                Volatile.Write(ref _dependencies, resources);
                // A full pass's own join is now the authority on what this node awaits: whatever it didn't observe
                // either isn't required any more, or TryReplan's next plan-only pass will re-collect it. A
                // status-only pass (rejoined false) re-observed the same dependency set, not a fresh join, so it
                // leaves _awaited exactly as the plan-only pass that built it left it.
                if (rejoined && _awaited.Count > 0) Volatile.Write(ref _awaited, []);
                Volatile.Write(ref _replicas, new(read.Replicas, StringComparer.Ordinal));
                WriteRetentionKeys();
                var status = new QueryStatus(projected.HasPrimaryData,
                    (_asyncDefinition is not null && _preparedGeneration != _prepareGeneration) || coldPending || refreshing,
                    !scopeStatus.Online || offline, superseded);
                var previousFacts = previous?.Facts ?? QueryFacts.Empty;
                var facts = ReferenceEquals(resources, previousResources) ? previousFacts
                    : FactsView.From(previousFacts, resources);
                long factsRevision = (previous?.FactsRevision ?? 0) + (ReferenceEquals(previousFacts, facts) ? 0 : 1);
                changed = _revision == 0 || previous is null || previous.OrderRevision != projected.OrderRevision
                    || previous.Failure != (_queryFailure ?? _preparationFailure)
                    || !EqualityComparer<T>.Default.Equals(previous.Value, projected.Value)
                    || previous.Status != status || !ReferenceEquals(previous.Problems, problems)
                    || !ReferenceEquals(previous.Resources, resources)
                    || !previous.Demanded.SequenceEqual(_activeRequirements.Catalog);
                next = changed ? new QuerySnapshot<T>(++_revision, projected.OrderRevision, projected.Value, status, problems)
                    { Resources = resources, Facts = facts, FactsRevision = factsRevision,
                        Failure = _queryFailure ?? _preparationFailure, Demanded = _activeRequirements.Catalog } : previous!;
            }
            // A scope/account/offline change can land during the expensive DTO build. Recheck the captured epoch
            // under publication -> catalog -> node before installing it, rather than publishing stale scope status.
            // No callback or replanning runs under these locks; a rejected join schedules a fresh pass below.
            bool scopeChanged = false;
            _owner._catalog.ReadConsistent(() =>
            {
              lock (_gate)
              {
                if (_owner._catalog.Epoch != scopeStatus.Epoch)
                {
                    changed = false;
                    _joinSuperseded = true;
                    scopeChanged = true;
                    return 0;
                }
                // A newer viewport/activation owns the next publication. Cold work above may safely complete
                // into the cache, including its completion sources, but this superseded DTO is never delivered.
                lock (_leaseGate)
                {
                    // Only a dropped RE-JOIN leaves `_current` behind the catalog. A status-only pass projects the
                    // value it already published, so losing its publication costs nothing but the status.
                    if (_disposed || !ReferenceEquals(leaseDemands, _leaseDemands)) { changed = false; _joinSuperseded |= rejoined; }
                    else { _joinSuperseded = false; if (changed) Volatile.Write(ref _current, next); }
                }
                handles = Volatile.Read(ref _leaseHandles);
                Interlocked.Increment(ref _demandVersion);
                return 0;
              }
            });
            if (changed) foreach (var handle in handles) handle.Publish(next);
            if (scopeChanged) Recompute();

            ResourceError? Overlay(ResourceKey key, ResourceSnapshot value)
                => _readErrors.TryGetValue(key, out var failure) ? failure.Error : _preparationFailure ?? value.Error;
        }

        /// <summary>A demand-only pass: re-plans the recipe from the CURRENT value and publishes at most a new
        /// <see cref="QuerySnapshot{T}.Demanded"/>. <see cref="IQueryDefinition{T}.Read"/> takes no demand, so a
        /// viewport move can never change the joined value; re-joining every member on every row crossed while
        /// scrolling (one <c>SetDemand</c> per row, ~10-12k catalog Peeks each, all under the publication gate) was
        /// pure waste. Answers false — the caller then runs a full pass — for the cases a plan cannot cover:
        /// nothing joined yet, a join whose DTO a newer viewport superseded (`_current` is behind the catalog), and
        /// a newly demanded key the last join never observed AND the repository already knows (a plan cannot
        /// present it, and nothing would ever ask the repository again — waiting for a catalog change that already
        /// happened never arrives). A newly demanded key the repository does NOT yet know is instead collected into
        /// <see cref="_awaited"/> and planning proceeds: <see cref="PlanDemand"/> asks the coordinator for it below,
        /// and the catalog change that lands the answer schedules the full pass that finally presents it (via
        /// <see cref="DependsOn"/> testing <see cref="_awaited"/> too). Finite demands (Refresh/ReadOnce) always
        /// re-join.</summary>
        bool TryReplan()
        {
            QuerySnapshot<T>? next = null;
            Handle[]? handles = null;
            // The plan owns only the node gate while every required key is one the last join observed — the common
            // scroll case (one SetDemand per row crossed) never queues behind a commit's publication. A previously
            // unobserved key needs the repository's knowledge, and reading it must acquire publication/catalog
            // BEFORE node, exactly like Project and protocol retries — so that case re-runs the plan under
            // catalog -> node instead of ever peeking the catalog from inside the node gate.
            bool? planned = Plan(catalogHeld: false);
            planned ??= _owner._catalog.ReadConsistent(() => Plan(catalogHeld: true));
            if (planned != true || handles is null) return planned == true;
            if (next is not null) foreach (var handle in handles) handle.Publish(next);
            PlanDemand();
            ReleaseObsoleteWhenPlanned();
            return true;

            // true: planned; false: only a full pass can serve this demand; null: a key the last join never observed
            // needs the catalog gate first (nothing was written).
            bool? Plan(bool catalogHeld)
            {
              lock (_gate)
              {
                if (_disposed) return true;
                var current = _current;
                if (current is null || _revision == 0 || _joinSuperseded || _finiteDemands.Count > 0) return false;
                var definition = _definition;
                var leaseDemands = Volatile.Read(ref _leaseDemands);
                var activeDemand = MergeDemand(leaseDemands);
                var required = activeDemand is null ? QueryRequirements.Empty
                    : definition.Requirements(current.Value, activeDemand);
                HashSet<ResourceKey>? awaited = null;
                foreach (var key in required.Catalog)
                    if (!current.Resources.ContainsKey(key))
                    {
                        if (!catalogHeld) return null;
                        if (_owner._catalog.TryPeek(key, out var snapshot) && snapshot.Knowledge != Knowledge.Unknown)
                            return false;
                        (awaited ??= []).Add(key);
                    }
                Volatile.Write(ref _awaited, awaited ?? []);
                _activeRequirements = required;
                _plannedLeaseDemands = leaseDemands;
                _activePriority = activeDemand is null ? ResourcePriority.Prefetch : (ResourcePriority)activeDemand.Priority;
                _finiteRequirements.Clear();
                WriteRetentionKeys();
                if (!current.Demanded.SequenceEqual(required.Catalog))
                {
                    // Value, Resources, Facts and Status all carry over by reference: only what is being asked for moved.
                    var candidate = current with { Revision = _revision + 1, Demanded = required.Catalog };
                    lock (_leaseGate)
                        if (!_disposed && ReferenceEquals(leaseDemands, _leaseDemands))
                        { _revision++; Volatile.Write(ref _current, candidate); next = candidate; }
                }
                handles = Volatile.Read(ref _leaseHandles);
                Interlocked.Increment(ref _demandVersion);
                return true;
              }
            }
        }

        // Called under _gate. `Required` already de-duplicates a definition's own recipe; GetActiveResourceKeys
        // distincts across nodes, so the single-recipe case needs no second pass over ~1.7k keys.
        void WriteRetentionKeys()
            => Volatile.Write(ref _retentionKeys, _finiteRequirements.Count == 0 ? _activeRequirements.Catalog
                : _activeRequirements.Catalog.Concat(_finiteRequirements.Values.SelectMany(required => required.Catalog))
                    .Distinct().ToArray());

        void ReleaseObsoleteWhenPlanned()
        {
            bool release;
            lock (_leaseGate)
            {
                // A superseded planner did not install the new desired set. Preserve parking/disposal cleanup
                // until a matching pass can release obsolete waiters against that new set, not the old viewport.
                release = ReferenceEquals(_plannedLeaseDemands, _leaseDemands) && _releaseObsoleteRequested;
                if (release) _releaseObsoleteRequested = false;
            }
            if (release) ReleaseObsoleteWaiters();
        }

        void InvalidatePreparation()
        {
            CancellationTokenSource? cancellation;
            lock (_gate)
            {
                if (_disposed || _asyncDefinition is null) return;
                _prepareGeneration++; _prepareTask = null; _preparationFailure = null;
                cancellation = _prepareCancellation; _prepareCancellation = null;
            }
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        async Task PrepareForObservationAsync()
        {
            try { await EnsurePreparedAsync(_token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Query preparation failed: {0}", error); }
        }

        async Task EnsurePreparedAsync(CancellationToken ct)
        {
            if (_asyncDefinition is null) return;
            while (true)
            {
                Task prepare;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_preparedGeneration == _prepareGeneration) return;
                    if (_prepareTask is null)
                    {
                        var generation = _prepareGeneration;
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
                        _prepareCancellation = cancellation;
                        _prepareTask = Task.Run(() => PrepareAsync(generation, cancellation));
                    }
                    prepare = _prepareTask;
                }
                try { await prepare.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_token.IsCancellationRequested) { }
            }
        }

        async Task PrepareAsync(long generation, CancellationTokenSource cancellation)
        {
            try
            {
                var prepared = await _asyncDefinition!.PrepareAsync(cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || generation != _prepareGeneration || cancellation.IsCancellationRequested) return;
                    _definition = prepared; _preparedGeneration = generation; _preparationFailure = null;
                }
                Recompute();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (_disposed || generation != _prepareGeneration) return;
                    _preparedGeneration = generation; _preparationFailure = new(ResourceErrorKind.Persistence, error.Message);
                }
                Recompute(false);
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_prepareCancellation, cancellation)) _prepareCancellation = null;
                cancellation.Dispose();
            }
        }

        async Task AwaitColdReadsAsync(CancellationToken ct)
        {
            while (true)
            {
                await QueueProjection(false).WaitAsync(ct).ConfigureAwait(false);
                Task[] pending;
                lock (_gate) pending = _coldTasks.Values.Where(task => !task.IsCompleted).Distinct().ToArray();
                if (pending.Length == 0) return;
                await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>Caller holds <c>_gate</c>. Mirrors <see cref="ClaimRevalidations"/> without claiming.</summary>
        bool HasUnclaimedRevalidation(IReadOnlyList<ResourceKey> keys)
        {
            if (_revalidations.Count == 0) return false;
            foreach (var key in keys)
                if (_revalidations.TryGetValue(key, out var basis)
                    && (!_revalidationAttempts.TryGetValue(key, out var attempted) || attempted != basis))
                    return true;
            return false;
        }

        static bool SameResourceSet(HashSet<ResourceKey> settled, IReadOnlyList<ResourceKey> required)
        {
            if (settled.Count != required.Count) return false;
            foreach (var key in required) if (!settled.Contains(key)) return false;
            return true;
        }

        static QueryDemand? MergeDemand(IEnumerable<QueryDemand> demands)
        {
            var active = demands.Where(demand => demand.Active).ToArray();
            if (active.Length == 0) return null;
            return new(true, active.Max(demand => demand.Priority),
                active.SelectMany(demand => demand.Facets).Distinct().ToArray());
        }

        public IReadOnlyList<ResourceKey> DemandedResources()
            => Volatile.Read(ref _retentionKeys);

        async Task LoadColdAsync(ResourceKey[] keys, TaskCompletionSource done)
        {
            try { await _owner._catalog.ReadManyAsync(keys, _token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception error)
            {
                int unknown = 0;
                foreach (var key in keys)
                {
                    var snapshot = _owner._catalog.Peek(key);
                    if (snapshot.Knowledge == Knowledge.Unknown)
                    {
                        unknown++;
                        lock (_gate) _readErrors[key] = (new(ResourceErrorKind.Persistence, error.Message), snapshot.Revision);
                    }
                }
                // Always-on: a cold read that throws used to vanish, leaving only "unavailable" rows as evidence.
                WaveeLog.Instance.Event(WaveeLogLevel.Warning, "catalog", "catalog.cold-read.failed",
                    "cold read failed spec=" + _definition.GetType().Name + " keys=" + keys.Length + " unknown=" + unknown
                    + " first=" + (keys.Length > 0 ? keys[0].Subject + "/" + keys[0].Facet : "-")
                    + " error=" + error.GetType().Name + ": " + error.Message);
            }
            finally
            {
                lock (_gate) foreach (var key in keys) _coldPending.Remove(key);
                try { Recompute(false); }
                finally { done.TrySetResult(); }
            }
        }

        // A lease that parks or goes away releases the queued requests only it wanted (another lease's shared row is
        // kept); a mere window move inside an active lease never does — see PlanDemand.
        void ReleaseObsoleteWaiters()
        {
            List<CancellationTokenSource> obsolete = [];
            lock (_gate)
                foreach (var pair in _demandWaiters)
                    if (!_desiredResources.Contains(pair.Key)) obsolete.Add(pair.Value);
            foreach (var cancellation in obsolete)
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        void PlanDemand()
        {
            List<CancellationTokenSource> obsolete = [];
            bool start = false;
            IReadOnlyList<ResourceKey> requirements;
            lock (_gate)
            {
                if (!_disposed && !ReferenceEquals(_plannedLeaseDemands, Volatile.Read(ref _leaseDemands)))
                {
                    // The UI replaced its intent during a projection. Only the next worker pass may install
                    // requirements from that new intent; staged cold reads are still drained normally. A plan is
                    // enough - the join cannot have moved with it - and TryReplan itself falls back to a full pass
                    // for the two cases a plan cannot cover (a superseded join, a never-observed key).
                    Replan();
                    return;
                }
                // Plan and install from the same lease version. Otherwise a delayed older planner can replace a
                // new viewport with obsolete resources and repeatedly defer the actual visible rows.
                bool active = !_disposed && Volatile.Read(ref _leaseDemands).Any(static demand => demand.Active);
                requirements = !active ? [] : Gather().Requirements.Catalog;
                _desiredResources = new(requirements);
                // A window move never cancels a queued request: measured 2026-09-07, the reveal ramp's shrinking
                // window cancelled 90 queued identity keys per page open and the rows behind them were never re-asked.
                // A fetch for a row that scrolled away completes into the cache (one batched POST); only parking or
                // disposal (below) releases the waiters.
                if (!active)
                {
                    _settledResources = [];
                    _demandSettled = false;
                    if (_demandRunCancellation is { } run) obsolete.Add(run);
                }
                // "Settled" holds only while the set is the same AND nothing in it is owed another Ensure: a
                // revalidation basis a later join raised (a stale fact) is a fresh ask on an unchanged key set.
                else if (!_planning && !(_demandSettled && SameResourceSet(_settledResources, requirements)
                                          && !HasUnclaimedRevalidation(requirements)))
                { _planning = true; start = true; }
            }
            _owner._active?.Set(this, requirements);
            // Cancel only removed resources. Another handle on this node retains its waiter; other nodes have
            // independent coordinator waiters. Cancellation never runs while the query publication lock is held.
            foreach (var cancellation in obsolete)
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            if (start) _ = Task.Run(RunDemandAsync);
        }

        (QueryRequirements Requirements, ResourcePriority Priority, long Version) Gather()
        {
            lock (_gate)
            {
                return (_activeRequirements, _activePriority, Interlocked.Read(ref _demandVersion));
            }
        }

        async Task RunDemandAsync()
        {
            bool released = false;
            long settledVersion = -1;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(_token);
            lock (_gate) _demandRunCancellation = run;
            var runToken = run.Token;
            var asked = new HashSet<ResourceKey>();
            var replicaAsked = new HashSet<ReplicaRequest>();
            long replicaGeneration = Interlocked.Read(ref _replicaDemandGeneration);
            // A request the demand port could not serve (LibraryQueryDemand.EnsureAsync answers unserved when no
            // protocol session is installed yet — findings: a page's replica demand firing before go-live installs
            // the protocol used to just vanish, asked exactly once and never again) gets exactly ONE more try within
            // THIS run, tracked here so it cannot be retried a second time. Bounding it is what keeps this safe: a
            // null session has nothing to await, so unconditionally re-adding an unserved request to `replicaAsked`
            // every pass (the way a catalog Deferred is retried below) would spin a query whose only outstanding
            // work is that replica instead of letting the run settle. Anything still unserved after its one retry
            // stays marked asked for the rest of this run — the real retry is a FRESH run, which
            // CatalogRuntime.ProtocolChanges publishing true drives QueryService to start via PlanDemand() once this
            // run has settled (_planning back to false); that new run's own empty replicaAsked re-asks it for real.
            var replicaRetried = new HashSet<ReplicaRequest>();
            // A key that came back Failed is owed a retry once canonical backoff allows one; a run that saw one
            // therefore never marks the set settled — the next intersecting publication starts a fresh run.
            bool anyFailed = false;
            try
            {
                while (true)
                {
                    await QueueProjection(false).WaitAsync(runToken).ConfigureAwait(false);
                    long latestReplicaGeneration = Interlocked.Read(ref _replicaDemandGeneration);
                    if (latestReplicaGeneration != replicaGeneration)
                    {
                        replicaAsked.Clear(); replicaRetried.Clear(); replicaGeneration = latestReplicaGeneration;
                    }
                    var plan = Gather();
                    bool seedResident;
                    lock (_gate) { seedResident = _seedResidentOnNextRun; _seedResidentOnNextRun = false; }
                    if (seedResident) SeedResidentAsked(plan.Requirements.Catalog, asked);
                    var forced = ClaimRevalidations(plan.Requirements.Catalog);
                    var keys = plan.Requirements.Catalog.Where(key => !forced.Contains(key) && !asked.Contains(key))
                        .ToArray();
                    foreach (var key in keys) asked.Add(key);
                    foreach (var key in forced) asked.Add(key);
                    var replicas = plan.Requirements.Replicas.Where(replicaAsked.Add).ToArray();
                    if (keys.Length == 0 && forced.Length == 0 && replicas.Length == 0)
                    {
                        lock (_gate)
                            if (_disposed || plan.Version == _demandVersion)
                            {
                                _settledResources = _desiredResources;
                                _demandSettled = !anyFailed;
                                settledVersion = plan.Version; released = true; return;
                            }
                        continue;
                    }
                    var normalRead = _owner._resources.EnsureAsync(keys, plan.Priority, ct: runToken,
                        waiterCancellation: DemandToken);
                    var forcedRead = _owner._resources.EnsureAsync(forced, plan.Priority, force: true, ct: runToken,
                        waiterCancellation: DemandToken);
                    var replicaRead = _owner._replicaDemand.EnsureAsync(replicas, false, runToken);
                    await Task.WhenAll(normalRead, forcedRead, replicaRead).ConfigureAwait(false);
                    // Replica implementations need not publish a catalog notification. Rejoin their completed
                    // local state before discovering the next closure wave.
                    if (replicas.Length > 0) await QueueRejoin().WaitAsync(runToken).ConfigureAwait(false);
                    DemandWaveDiagnostics.Log(_spec, normalRead.Result, forcedRead.Result, replicas.Length,
                        _owner._catalog.LastColdReadMs);
                    foreach (var unserved in replicaRead.Result)
                        if (replicaRetried.Add(unserved)) replicaAsked.Remove(unserved);
                    bool recovered;
                    lock (_gate) { recovered = _queryFailure is not null; _queryFailure = null; }
                    if (recovered) Recompute(false);
                    bool deferred = false;
                    foreach (var result in normalRead.Result.Concat(forcedRead.Result))
                    {
                        if (result.Status == ResourceEnsureStatus.Failed) anyFailed = true;
                        if (result.Status != ResourceEnsureStatus.Deferred || result.Snapshot.Activity == ResourceActivity.Offline) continue;
                        asked.Remove(result.Key);
                        lock (_gate) _revalidationAttempts.Remove(result.Key);
                        deferred = true;
                    }
                    if (deferred) await _owner._resources.WaitForCapacityAsync(runToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (_gate) _queryFailure = new(ResourceErrorKind.Transport, error.Message);
                Recompute();
            }
            finally
            {
                bool restart;
                lock (_gate)
                {
                    _planning = false;
                    restart = runToken.IsCancellationRequested || released && settledVersion != _demandVersion
                        || replicaGeneration != Interlocked.Read(ref _replicaDemandGeneration);
                    if (ReferenceEquals(_demandRunCancellation, run)) _demandRunCancellation = null;
                    foreach (var cancellation in _demandWaiters.Values) cancellation.Dispose();
                    _demandWaiters.Clear();
                }
                // A view can reactivate before the canceled run has released its waiters.
                if (restart && !_token.IsCancellationRequested) Recompute(false);
            }

            void SeedResidentAsked(IReadOnlyList<ResourceKey> required, HashSet<ResourceKey> already)
            {
                foreach (var key in required)
                {
                    if (already.Contains(key)) continue;
                    if (OwedRevalidation(key)) continue;
                    var snapshot = _owner._catalog.Peek(key);
                    if (snapshot.Knowledge != Knowledge.Unknown && snapshot.Activity != ResourceActivity.Offline
                        && snapshot.Error is null)
                        already.Add(key);
                }
            }

            bool OwedRevalidation(ResourceKey key)
            {
                lock (_gate)
                    return _revalidations.TryGetValue(key, out var basis)
                        && (!_revalidationAttempts.TryGetValue(key, out var attempted) || attempted != basis);
            }

            CancellationToken DemandToken(ResourceKey key)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_plannedLeaseDemands, Volatile.Read(ref _leaseDemands)))
                        return new CancellationToken(true);
                    if (!_desiredResources.Contains(key)) return new CancellationToken(true);
                    if (_demandWaiters.TryGetValue(key, out var existing))
                    {
                        if (!existing.IsCancellationRequested) return existing.Token;
                        existing.Dispose();
                    }
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    _demandWaiters[key] = cancellation;
                    return cancellation.Token;
                }
            }
        }

        ResourceKey[] ClaimRevalidations(IReadOnlyList<ResourceKey> keys)
        {
            lock (_gate)
            {
                var forced = new List<ResourceKey>();
                foreach (var key in keys)
                    if (_revalidations.TryGetValue(key, out var basis)
                        && (!_revalidationAttempts.TryGetValue(key, out var attempted) || attempted != basis))
                    {
                        _revalidationAttempts[key] = basis; forced.Add(key);
                    }
                return forced.ToArray();
            }
        }

        public async ValueTask<RefreshResult> RefreshAsync(CancellationToken ct)
        {
            var demand = MergeDemand(Volatile.Read(ref _leaseDemands)) ?? QueryDemand.Initial;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _readErrors.Clear(); _revalidationAttempts.Clear(); _queryFailure = null;
                _finiteDemands.Add(demand); Volatile.Write(ref _finiteActiveCount, _finiteDemands.Count);
            }
            if (_asyncDefinition is not null) InvalidatePreparation();
            var asked = new HashSet<ResourceKey>();
            var replicas = new HashSet<ReplicaRequest>();
            var barrierAsked = new HashSet<ResourceKey>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
            try
            {
            await QueueRejoin().WaitAsync(linked.Token).ConfigureAwait(false);
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                await EnsurePreparedAsync(linked.Token).ConfigureAwait(false);
                await AwaitColdReadsAsync(linked.Token).ConfigureAwait(false);
                QueryRequirements required;
                lock (_gate)
                {
                    if (!_finiteRequirements.TryGetValue(demand, out required!))
                        return new(Current.Problems) { Failure = Current.Failure };
                }
                var plan = (Requirements: required, Priority: (ResourcePriority)demand.Priority);
                var barriers = ClaimRevalidations(plan.Requirements.Catalog).Where(barrierAsked.Add).ToArray();
                var keys = plan.Requirements.Catalog.Where(key => !asked.Contains(key))
                    .Concat(barriers).Distinct().ToArray();
                foreach (var key in keys) asked.Add(key);
                var replicaKeys = plan.Requirements.Replicas.Where(replicas.Add).ToArray();
                if (keys.Length == 0 && replicaKeys.Length == 0) return new(Current.Problems) { Failure = Current.Failure };
                var read = _owner._resources.EnsureAsync(keys, plan.Priority, force: true, ct: linked.Token);
                await Task.WhenAll(read,
                    _owner._replicaDemand.EnsureAsync(replicaKeys, true, linked.Token)).ConfigureAwait(false);
                if (RetryDeferred(read.Result, asked))
                    await _owner._resources.WaitForCapacityAsync(linked.Token).ConfigureAwait(false);
                await QueueRejoin().WaitAsync(linked.Token).ConfigureAwait(false);
            }
            }
            finally
            {
                lock (_gate) { _finiteDemands.Remove(demand); Volatile.Write(ref _finiteActiveCount, _finiteDemands.Count); }
                Recompute(false);
            }
        }

        async Task<QuerySnapshot<T>> ReadOnceAsync(QueryDemand demand, CancellationToken ct)
        {
            var asked = new HashSet<ResourceKey>();
            var replicas = new HashSet<ReplicaRequest>();
            var forcedAsked = new HashSet<ResourceKey>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
            lock (_gate) { _finiteDemands.Add(demand); Volatile.Write(ref _finiteActiveCount, _finiteDemands.Count); }
            try
            {
              await QueueRejoin().WaitAsync(linked.Token).ConfigureAwait(false);
              while (true)
              {
                linked.Token.ThrowIfCancellationRequested();
                await EnsurePreparedAsync(linked.Token).ConfigureAwait(false);
                await AwaitColdReadsAsync(linked.Token).ConfigureAwait(false);
                if (!demand.Active) return Current;
                QueryRequirements required;
                lock (_gate)
                {
                    if (!_finiteRequirements.TryGetValue(demand, out required!)) return Current;
                }
                var forced = ClaimRevalidations(required.Catalog).Where(forcedAsked.Add).ToArray();
                var keys = required.Catalog.Where(key => !forced.Contains(key) && !asked.Contains(key))
                    .ToArray();
                foreach (var key in keys) asked.Add(key);
                foreach (var key in forced) asked.Add(key);
                var replicaKeys = required.Replicas.Where(replicas.Add).ToArray();
                if (keys.Length == 0 && forced.Length == 0 && replicaKeys.Length == 0) return Current;
                var normalRead = _owner._resources.EnsureAsync(keys, (ResourcePriority)demand.Priority, ct: linked.Token);
                var forcedRead = _owner._resources.EnsureAsync(forced, (ResourcePriority)demand.Priority, force: true, ct: linked.Token);
                await Task.WhenAll(normalRead, forcedRead,
                    _owner._replicaDemand.EnsureAsync(replicaKeys, false, linked.Token)).ConfigureAwait(false);
                if (RetryDeferred(normalRead.Result.Concat(forcedRead.Result), asked))
                    await _owner._resources.WaitForCapacityAsync(linked.Token).ConfigureAwait(false);
                await QueueRejoin().WaitAsync(linked.Token).ConfigureAwait(false);
              }
            }
            finally
            {
                lock (_gate) { _finiteDemands.Remove(demand); Volatile.Write(ref _finiteActiveCount, _finiteDemands.Count); }
                Recompute(false);
                _ = _owner.TrimInactiveAsync();
            }
        }

        static bool RetryDeferred(IEnumerable<ResourceEnsureResult> results, HashSet<ResourceKey> asked)
        {
            bool retry = false;
            foreach (var result in results)
                if (result.Status == ResourceEnsureStatus.Deferred && result.Snapshot.Activity != ResourceActivity.Offline)
                { asked.Remove(result.Key); retry = true; }
            return retry;
        }

        public void Dispose()
        {
            lock (_leaseGate) { if (_disposed) return; _disposed = true; }
            // Coordinator cancellation can invoke callbacks. Keep it off the UI disposal path as well.
            _ = Task.Run(() =>
            {
                _owner._active?.Set(this, []);
                _lifetime.Cancel();
                _lifetime.Dispose();
            });
        }

        public sealed class Handle : IQueryHandle<T>, IObservable<QuerySnapshot<T>>
        {
            readonly Node<T> _node;
            readonly List<Subscription> _subscriptions = new();
            bool _disposed;
            public QueryDemand Demand { get; private set; } = QueryDemand.None;
            public Handle(Node<T> node) => _node = node;
            public QuerySnapshot<T> Current => _node.Current;
            public IObservable<QuerySnapshot<T>> Changes => this;
            public IDisposable Subscribe(IObserver<QuerySnapshot<T>> observer)
            {
                ArgumentNullException.ThrowIfNull(observer);
                Subscription subscription;
                QuerySnapshot<T> current;
                lock (_node._leaseGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    subscription = new(this, observer);
                    _subscriptions.Add(subscription);
                    current = _node.Current;
                }
                subscription.Publish(current);
                return subscription;
            }
            public void Publish(QuerySnapshot<T> snapshot)
            {
                Subscription[] subscriptions;
                lock (_node._leaseGate) { if (_disposed) return; subscriptions = _subscriptions.ToArray(); }
                foreach (var subscription in subscriptions) subscription.Publish(snapshot);
            }

            // Each observer receives serialized increasing revisions, including exactly one replay. A subscriber's
            // exception cannot escape through a committed catalog transaction or prevent another subscriber's update.
            sealed class Subscription(Handle owner, IObserver<QuerySnapshot<T>> observer) : IDisposable
            {
                readonly object _gate = new();
                readonly Queue<QuerySnapshot<T>> _pending = new();
                long _latest = -1;
                bool _disposed, _draining;
                public void Publish(QuerySnapshot<T> snapshot)
                {
                    lock (_gate)
                    {
                        if (_disposed || snapshot.Revision <= _latest) return;
                        _latest = snapshot.Revision; _pending.Enqueue(snapshot);
                        if (_draining) return;
                        _draining = true;
                    }
                    while (true)
                    {
                        QuerySnapshot<T> next;
                        lock (_gate)
                        {
                            if (_disposed || _pending.Count == 0) { _draining = false; return; }
                            next = _pending.Dequeue();
                        }
                        try { observer.OnNext(next); }
                        catch (Exception error) { System.Diagnostics.Trace.TraceError("Query observer failed: {0}", error); }
                    }
                }
                public void Dispose()
                {
                    lock (_gate) { _disposed = true; _pending.Clear(); }
                    lock (owner._node._leaseGate) owner._subscriptions.Remove(this);
                }
            }
            public void SetDemand(QueryDemand demand)
            {
                ArgumentNullException.ThrowIfNull(demand);
                bool activation;
                lock (_node._leaseGate)
                {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        if (Demand.Active == demand.Active && Demand.Priority == demand.Priority
                            && Demand.Facets.SequenceEqual(demand.Facets)) return;
                        activation = Demand.Active != demand.Active;
                        _node._releaseObsoleteRequested |= activation;
                        Demand = demand;
                        _node.CaptureLeases();
                        Interlocked.Increment(ref _node._demandVersion);
                }
                // A facet/priority change only changes what is ASKED FOR. IQueryDefinition<T>.Read takes no demand,
                // so demand cannot move the joined value: this re-plans (Requirements against the current
                // value) instead of re-joining every member. An activation/parking transition still re-joins - a lease
                // coming back may demand a key the last join never observed, which a plan cannot present.
                if (activation) _node.Recompute(); else _node.Replan();
            }
            public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
                => new(Task.Run(async () => await _node.RefreshAsync(cancellationToken).ConfigureAwait(false), cancellationToken));
            public Task<QuerySnapshot<T>> ReadOnceAsync(QueryDemand demand, CancellationToken ct)
                => Task.Run(() => _node.ReadOnceAsync(demand, ct), ct);
            public void Dispose()
            {
                Subscription[] subscriptions;
                lock (_node._leaseGate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _node._leases.Remove(this);
                    _node.CaptureLeases();
                    _node._releaseObsoleteRequested = true;
                    Interlocked.Increment(ref _node._demandVersion);
                    subscriptions = _subscriptions.ToArray();
                }
                foreach (var subscription in subscriptions) subscription.Dispose();
                _node._owner.Release(_node._spec, _node);
                _node.Recompute(false);
            }
        }
    }
}

/// <summary>Engine-free acquire-scope predicate, checked by the initial projection worker under DEBUG/FLUENTGPU_DIAG
/// (findings 4.2) — pulled out so the table of cases (online/local, scope active/inactive, context known/pre-login) is
/// unit-testable without constructing a repository.</summary>
public static class QueryScopeRules
{
    /// <summary>A spec is stale when it names an authenticated Spotify scope that is not the repository's active one.
    /// A local (non-Spotify) provider is never stale here — those facts are current before any Spotify session
    /// exists. A pre-login Spotify scope (<c>ContextKnown == false</c>) is also never stale: nothing has attempted to
    /// authenticate it yet, so "not active" is the honest, expected answer rather than a late/dead acquire.</summary>
    public static bool IsStale(CatalogScope scope, bool isActiveScope)
        => !isActiveScope && scope.Provider == "spotify" && scope.ContextKnown;
}

/// <summary>Always-on demand-side log: one <c>catalog.demand.wave</c> line per wave a query node asks the
/// coordinator for, with the facet histogram of what was asked and how each key came back. Pairs with the
/// coordinator's <c>catalog.fetch.batch</c> line: the batch line says what was fetched, this one says what a page
/// wanted — a row that never shows up here was never demanded at all.</summary>
static class DemandWaveDiagnostics
{
    /// <summary>Test-only: an async-local sink so a fixture can count waves without racing parallel tests.</summary>
    internal static readonly AsyncLocal<List<string>?> Waves = new();

    public static void Log(object spec, IReadOnlyList<ResourceEnsureResult> normal, IReadOnlyList<ResourceEnsureResult> forced, int replicas,
        long coldMs = 0)
    {
        int total = normal.Count + forced.Count;
        if (total == 0 && replicas == 0) return;
        var facets = new Dictionary<FacetKind, int>();
        int ready = 0, absent = 0, deferred = 0, failed = 0, superseded = 0, unsupported = 0, offline = 0;
        foreach (var list in new[] { normal, forced })
            foreach (var result in list)
            {
                facets[result.Key.Facet] = facets.GetValueOrDefault(result.Key.Facet) + 1;
                switch (result.Status)
                {
                    case ResourceEnsureStatus.Ready: ready++; break;
                    case ResourceEnsureStatus.Absent: absent++; break;
                    case ResourceEnsureStatus.Failed: failed++; break;
                    case ResourceEnsureStatus.Superseded: superseded++; break;
                    case ResourceEnsureStatus.Unsupported: unsupported++; break;
                    default:
                        deferred++;
                        if (result.Snapshot.Activity == ResourceActivity.Offline) offline++;
                        break;
                }
            }
        var histogram = string.Join(",", facets.OrderByDescending(pair => pair.Value).Select(pair => pair.Key + "=" + pair.Value));
        string specName = spec.GetType().Name;
        Waves.Value?.Add(specName + " keys=" + total + " replicas=" + replicas);
        WaveeLog.Instance.Event(failed + offline > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "catalog", "catalog.demand.wave",
            "demand wave spec=" + specName + " keys=" + total + " forced=" + forced.Count + " replicas=" + replicas
            + " ready=" + ready + " absent=" + absent + " deferred=" + deferred + " offline=" + offline + " failed=" + failed
            + " superseded=" + superseded + " unsupported=" + unsupported + " facets=[" + histogram + "] coldMs=" + coldMs);
    }
}
