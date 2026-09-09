using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.Core.Diagnostics;

namespace Wavee.Backend;

/// <summary>The application data lifetime: one commit queue, catalog, replica coordinator and query graph.</summary>
public sealed class CatalogRuntime : IAsyncDisposable
{
    public DataCommitQueue Commits { get; } = new();
    public CatalogRepository Catalog { get; }
    public LibraryReplicaCoordinator Replicas { get; }
    public ResourceCoordinator Resources { get; }
    public QueryService Queries { get; }
    public ActiveCatalogDemand ActiveDemand { get; }
    public PlaybackQueueProjection PlaybackQueue { get; }
    readonly IDisposable _queueSubscription;
    readonly IDisposable[] _diagnosticRegistrations;
    readonly Func<string, string> _ownerProvider;
    readonly ProviderExecutionPolicy _executionPolicy;
    public ProviderExecutionGate Execution { get; }
    sealed record ProtocolSession(LibrarySync Sync, long Epoch);
    ProtocolSession? _protocol;
    int _disposed;
    // Publishes whenever a live protocol/LibrarySync session installs or clears — QueryService subscribes so a
    // replica request that fired (and was answered unserved by LibraryQueryDemand) before go-live's protocol
    // install lands can be re-asked the moment it does, instead of staying dropped for the query node's lifetime.
    // Initial value false: nothing is installed at construction. A demo/offline runtime that never installs a
    // protocol simply never publishes true — QueryService's subscription is a permanent, harmless no-op for it.
    readonly SimpleSubject<bool> _protocolChanges = new(false);
    public IObservable<bool> ProtocolChanges => _protocolChanges;
    public RootlistCommandRouter Commands { get; }
    public LibrarySync? LiveSync => Commits.ReadConsistent(() => _disposed == 0 && _protocol is { } current
        && current.Epoch == Catalog.Epoch && Catalog.IsOnline ? current.Sync : null);
    public string ProviderForSubject(string uri) => _ownerProvider(uri);
    public CatalogScope ScopeForSubject(string uri) => Catalog.Scope with { Provider = _ownerProvider(uri) };
    /// <summary>The replica owner a catalog scope names. The durable replicas are keyed by (account, storage account)
    /// alone — the generation is only the fencing stamp — so a PROVISIONAL scope (one recalled from the last session,
    /// whose context the AP welcome has not certified yet) owns exactly the rows the certified one will. Deriving the
    /// account from <c>ProviderAccount</c> rather than from <c>ContextKnown</c> is what lets a cold start read the
    /// user's own durable library before there is any session at all.</summary>
    internal static ReplicaScope DeriveReplicaScope(CatalogScope scope, long generation)
        => new(scope.ProviderAccount is { Length: > 0 } account ? account : null, generation, scope.StorageAccount);

    /// <param name="online">Overrides the derived connection state — false for the provisional scope a launch serves
    /// its cached library under, which names a context but has no transport behind it yet.</param>
    public CatalogRuntime(CatalogScope scope, string actualAccount, ICatalogPersistence catalogPersistence,
        IReplicaPersistence replicaPersistence, IReplicaProjectionSink projection,
        IEnumerable<ICatalogResourceProvider> providers, Func<string, string>? ownerProvider = null,
        ProviderExecutionPolicy executionPolicy = ProviderExecutionPolicy.Ready, bool? online = null)
    {
        var registeredProvider = ownerProvider ?? (uri => Wavee.Core.EntityUri.Parse(uri).Provider);
        ownerProvider = uri => CatalogProviderIds.Resolve(uri, registeredProvider(uri));
        _ownerProvider = ownerProvider;
        Catalog = new(Commits, catalogPersistence, TimeProvider.System, scope, actualAccount, online);
        _executionPolicy = executionPolicy;
        Execution = new(Catalog.Epoch, executionPolicy == ProviderExecutionPolicy.Ready
            ? ProviderExecutionState.Ready : ProviderExecutionState.Initializing);
        Commands = new(() => LiveSync);
        Replicas = new(Commits, replicaPersistence, projection,
            () => Catalog.ReadConsistent(() => DeriveReplicaScope(Catalog.Scope, Catalog.Epoch)),
            ReplicaBootstrap.Empty, Catalog);
        Resources = new(Catalog, providers, TimeProvider.System, Execution);
        ActiveDemand = new(Catalog, Resources, TimeProvider.System, error => System.Diagnostics.Trace.TraceError("Catalog active demand failed: {0}", error));
        Queries = new(Catalog, Resources, new LibraryQueryDemand(() => LiveSync, Replicas,
            executionPolicy == ProviderExecutionPolicy.ProtocolSession ? Execution : null, () => Catalog.Epoch),
            Replicas.Changes, ActiveDemand, ProtocolChanges);
        CatalogQueryDefinitions.Register(Queries, Replicas, ownerProvider ?? (uri => Wavee.Core.EntityUri.Parse(uri).Provider));
        Queries.Register<LibrarySearchQuery, Wavee.Core.LibrarySearchResults>(query => new LibrarySearchQueryDefinition(query,
            catalogPersistence as ICatalogSearchPersistence ?? throw new InvalidOperationException("Catalog persistence must provide the local search index."),
            Replicas, ownerProvider ?? (uri => Wavee.Core.EntityUri.Parse(uri).Provider)));
        PlaybackQueue = new(Catalog, Replicas, ownerProvider ?? (uri => Wavee.Core.EntityUri.Parse(uri).Provider));
        Queries.Register<QueueQuery, QueueQuerySnapshot>(query => new QueueQueryDefinition(query, PlaybackQueue, PlaybackQueue));
        _queueSubscription = PlaybackQueue.Changes.Subscribe(Observers.From<Wavee.Core.QueueOccurrenceSnapshot>(
            _ => Queries.NotifyDependencyChanged(QueueQueryDefinition.Dependency)));
        // mem.sample owners: what this runtime keeps resident and how contended its two serialization points are.
        // Registration tokens remove only this runtime's reports during an overlapping session switch.
        _diagnosticRegistrations =
        [
            PerformanceDiagnostics.Owners.Register("catalog", () => "resident=" + Catalog.ResidentCount + " residentMB="
                + (Catalog.EstimatedResidentBytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " lastColdReadMs=" + Catalog.LastColdReadMs),
            PerformanceDiagnostics.Owners.Register("queries", Queries.Diagnostics),
            PerformanceDiagnostics.Owners.Register("fetch", () => { var (jobs, inFlight) = Resources.Load; return "jobs=" + jobs + " inFlight=" + inFlight; }),
            PerformanceDiagnostics.Owners.Register("commits", () =>
            {
                var c = Commits.Contention;
                return "runs=" + c.Commits + " slow=" + c.CommitSlow + " runMsTotal=" + c.CommitRunMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " pending=" + c.Pending + " gateWaitMsTotal=" + c.GateWaitMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " gateSlowWaits=" + c.GateWaitSlow + " publishHoldMsTotal=" + c.PublishHoldMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " publishSlow=" + c.PublishSlow;
            })
        ];
    }
    public Task InitializeAsync(CancellationToken ct = default) => Replicas.ReloadAsync(Replicas.Scope, ct);
    /// <summary>Concludes pre-session startup when login fails/cancels or no saved login exists.</summary>
    public Task SetConnectionIntentAsync(bool connecting, long expectedEpoch)
        => Commits.ExecuteAsync(_ =>
        {
            if (_disposed == 0 && _executionPolicy == ProviderExecutionPolicy.ProtocolSession
                && Catalog.Epoch == expectedEpoch && _protocol is null)
                Execution.Set(expectedEpoch, connecting ? ProviderExecutionState.Initializing : ProviderExecutionState.Offline);
            return ValueTask.FromResult(0);
        });
    /// <summary>Installs a session. When it CONFIRMS the owner already in place — the provisional scope a launch
    /// recalled from its last session, now certified by the AP welcome — the durable replicas are re-stamped with the
    /// new generation inside the very publication that installed it, and nothing is reloaded: the reads keep answering
    /// throughout, which is the whole point of serving the cached library before login. A session that names a DIFFERENT
    /// owner still reloads everything, exactly as before.</summary>
    public async Task<long> SetSessionAsync(CatalogScope scope, string actualAccount, bool online, CancellationToken ct = default)
    {
        var (replicaScope, confirmed) = await Commits.ExecuteAsync(_ =>
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            bool restamped = false;
            Commits.Publish(() =>
            {
                bool owner = Catalog.SetSessionCore(scope, actualAccount, online); _protocol = null;
                restamped = owner && Replicas.RestampScopeCore(DeriveReplicaScope(scope, Catalog.Epoch));
                Execution.Set(Catalog.Epoch, !online ? ProviderExecutionState.Offline
                    : _executionPolicy == ProviderExecutionPolicy.Ready ? ProviderExecutionState.Ready : ProviderExecutionState.Initializing);
                Commits.NotifyAfterPublish(() => _protocolChanges.OnNext(false));
            });
            return ValueTask.FromResult((Replicas.Scope, restamped));
        }, ct).ConfigureAwait(false);
        if (!confirmed) await Replicas.ReloadAsync(replicaScope, ct).ConfigureAwait(false);
        return replicaScope.Generation;
    }
    public Task<bool> SetProtocolSessionAsync(LibrarySync sync, long expectedEpoch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sync);
        return Commits.ExecuteAsync(_ =>
        {
            if (_disposed != 0 || Catalog.Epoch != expectedEpoch || !Catalog.IsOnline) return ValueTask.FromResult(false);
            Commits.Publish(() =>
            {
                _protocol = new(sync, expectedEpoch);
                Execution.Set(expectedEpoch, ProviderExecutionState.Ready);
                Commits.NotifyAfterPublish(() => _protocolChanges.OnNext(true));
            });
            return ValueTask.FromResult(true);
        }, ct);
    }
    public Task<bool> ClearProtocolSessionAsync(LibrarySync expectedSync, long expectedEpoch, CancellationToken ct = default)
        => Commits.ExecuteAsync(_ =>
        {
            if (_protocol is not { } current || !ReferenceEquals(current.Sync, expectedSync) || current.Epoch != expectedEpoch)
                return ValueTask.FromResult(false);
            Commits.Publish(() =>
            {
                _protocol = null;
                Execution.Set(Catalog.Epoch, ProviderExecutionState.Offline);
                Commits.NotifyAfterPublish(() => _protocolChanges.OnNext(false));
            });
            return ValueTask.FromResult(true);
        }, ct);
    public async Task<bool> EndSessionAsync(long expectedEpoch, CancellationToken ct = default)
    {
        var changed = await Commits.ExecuteAsync(_ =>
        {
            bool accepted = false;
            Commits.Publish(() =>
            {
                accepted = Catalog.SetOfflineCore(expectedEpoch);
                if (accepted)
                {
                    _protocol = null;
                    Execution.Set(Catalog.Epoch, ProviderExecutionState.Offline);
                    Commits.NotifyAfterPublish(() => _protocolChanges.OnNext(false));
                }
            });
            return ValueTask.FromResult(accepted ? (ReplicaScope?)Replicas.Scope : null);
        }, ct).ConfigureAwait(false);
        if (changed is not { } scope) return false;
        try { await Replicas.ReloadAsync(scope, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (scope != Replicas.Scope) { return false; }
        return scope == Replicas.Scope;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var registration in _diagnosticRegistrations) registration.Dispose();
        Execution.Set(Catalog.Epoch, ProviderExecutionState.Offline);
        await Commits.ExecuteAsync(_ => { Commits.Publish(() => _protocol = null); return ValueTask.FromResult(0); }).ConfigureAwait(false);
        _queueSubscription.Dispose();
        Queries.Dispose();
        ActiveDemand.Dispose();
        await Resources.DisposeAsync().ConfigureAwait(false);
        await Commits.DisposeAsync().ConfigureAwait(false);
    }
}
