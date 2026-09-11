using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Queries;

/// <summary>One pure projector plus its explicit demand recipe. Neither Read nor Requirements performs I/O.</summary>
public interface IQueryDefinition<T>
{
    /// <summary>Cheap immutable loading seed; never enumerate membership or read the catalog here.</summary>
    QueryReadResult<T> Pending { get; }
    QueryReadResult<T> Read(QueryReadContext context);
    QueryRequirements Requirements(T value, QueryDemand demand);
}

/// <summary>Finite local preparation returns a new pure definition. The query node owns cancellation and stale-result rejection.</summary>
public interface IAsyncQueryDefinition<T> : IQueryDefinition<T>
{
    ValueTask<IQueryDefinition<T>> PrepareAsync(CancellationToken ct);
    bool IsInvalidatedBy(IReadOnlyList<ResourceKey> keys);
    bool IsInvalidatedByReplica(string id);
}

public sealed record QueryReadResult<T>(T Value, long OrderRevision, bool HasPrimaryData);
public sealed record ReplicaRequest(string Kind, string Id);
public sealed record QueryRequirements(IReadOnlyList<ResourceKey> Catalog,
    IReadOnlyList<ReplicaRequest> Replicas)
{
    public static QueryRequirements Empty { get; } = new([], []);
}

public interface IQueryReplicaDemand
{
    /// <summary>Ensures each request's replica data is fresh (or, for <c>force:true</c>, forces a refetch), and answers
    /// the subset that could NOT be served this call — e.g. no protocol session is installed yet — so the caller can
    /// re-ask later instead of the request silently vanishing. Empty when everything was served.</summary>
    Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct);
}

/// <summary>Dependency collection happens only while building an immutable snapshot, never in an item indexer.
///
/// <para>Backed by a <see cref="ResourceMap.Builder"/> instead of a plain dictionary: a full membership pass whose
/// change set the caller already knows (<paramref name="changedKeys"/> — the keys a Durable/ColdRead catalog
/// publication actually named since this node's last pass) re-Peeks the catalog only for those keys plus any key
/// not yet in the map; every other key is answered from the previous pass's snapshot with no catalog lookup and no
/// chunk copy. <paramref name="fullRefresh"/> forces the old behaviour (Peek every key) for a session/scope replace
/// or the node's first pass, where a precise change list either doesn't exist or isn't trustworthy.</para></summary>
public sealed class QueryReadContext
{
    readonly CatalogRepository _catalog;
    readonly ResourceMap.Builder _builder;
    readonly bool _fullRefresh;
    readonly IReadOnlyCollection<ResourceKey> _changedKeys;
    readonly HashSet<ResourceKey>? _observed;
    readonly HashSet<string> _replicas = new(StringComparer.Ordinal);
    readonly HashSet<ResourceKey> _coldCandidates = new();
    readonly Dictionary<ResourceKey, long> _revalidationCandidates = new();
    int _materializedFromCatalog;

    /// <param name="capacity">Ignored; retained so every existing <c>new QueryReadContext(catalog, N)</c> call site
    /// (dependency scopes, tests, a first pass with no previous map) keeps compiling unchanged. A dependency scope
    /// or a fresh node always starts from <see cref="ResourceMap.Empty"/>, which makes every key a first observation
    /// regardless of this parameter.</param>
    public QueryReadContext(CatalogRepository catalog, int capacity = 0)
        : this(catalog, ResourceMap.Empty.ToBuilder(), fullRefresh: true, changedKeys: [], observed: null)
    {
    }

    /// <param name="builder">A per-node, reused <see cref="ResourceMap.Builder"/> already <see cref="ResourceMap.Builder.Reset"/>
    /// onto the previous pass's committed map.</param>
    /// <param name="fullRefresh">True forces a catalog Peek for every key this pass reads (a session/scope replace,
    /// or the node's first pass — where <paramref name="builder"/>'s base map is empty, so this is also what a
    /// never-observed key already gets for free).</param>
    /// <param name="changedKeys">Keys a catalog publication named since the last pass. Always re-Peeked even when
    /// not <paramref name="fullRefresh"/>.</param>
    /// <param name="observed">A reused per-node set every <see cref="Read"/>ed key is added to (a rejoin pass only);
    /// null for a dependency scope, where nothing outlives the call.</param>
    public QueryReadContext(CatalogRepository catalog, ResourceMap.Builder builder, bool fullRefresh,
        IReadOnlyCollection<ResourceKey> changedKeys, HashSet<ResourceKey>? observed)
    {
        _catalog = catalog;
        _builder = builder;
        _fullRefresh = fullRefresh;
        _changedKeys = changedKeys;
        _observed = observed;
        observed?.Clear();
    }

    public QueryReadContext CreateDependencyScope() => new(_catalog);
    public IReadOnlyDictionary<ResourceKey, ResourceSnapshot> Resources => _builder;
    public IReadOnlyCollection<string> Replicas => _replicas;
    public IReadOnlyCollection<ResourceKey> ColdCandidates => _coldCandidates;
    /// <summary>A fresh page can belong to a different relation snapshot. The first-page revision bounds one retry.</summary>
    public IReadOnlyDictionary<ResourceKey, long> RevalidationCandidates => _revalidationCandidates;
    public void RequireRevalidation(ResourceKey key, long basisRevision) => _revalidationCandidates[key] = basisRevision;
    /// <summary>Commits the builder into an immutable <see cref="ResourceMap"/> — the SAME base instance when this
    /// pass touched nothing, otherwise a new map sharing every chunk it didn't touch.</summary>
    public ResourceMap Commit() => _builder.Commit();
    public QueryPassStats Stats => _builder.Stats with { KeysObserved = _observed?.Count ?? _materializedFromCatalog };
    public bool IsChanged(ResourceKey key) => _fullRefresh || _changedKeys.Contains(key);

    /// <param name="forceFresh">Bypasses the change-set fast path: always re-Peeks the catalog. Used by a
    /// status-only pass re-observing its existing dependency set, whose whole purpose is to notice an activity
    /// transition (Queued/Fetching/Offline) that a Durable/ColdRead change set never names.</param>
    public ResourceSnapshot Read(ResourceKey key, bool allowColdRead = true, bool forceFresh = false)
    {
        _observed?.Add(key);
        ResourceSnapshot value;
        if (!forceFresh && !IsChanged(key) && _builder.TryGetValue(key, out var kept)) value = kept;
        else
        {
            value = _catalog.Peek(key);
            _builder.SetIfDifferent(key, value);
            _materializedFromCatalog++;
        }
        if (allowColdRead && value.Knowledge == Knowledge.Unknown) _coldCandidates.Add(key);
        return value;
    }

    public T? Read<T>(ResourceKey key, bool allowColdRead = true) where T : CatalogValue => Read(key, allowColdRead).Value as T;
    public void DependOnReplica(string id) => _replicas.Add(id);
    /// <summary>PublishJoin's error-overlay pass writes the overlaid snapshot straight into the same builder
    /// instead of copying a second full-size dictionary; a chunk this touches was already going to be copied by
    /// this pass's own <see cref="Read"/> calls, or gets copied here for the first time.</summary>
    internal void Overlay(ResourceKey key, ResourceSnapshot value) => _builder.SetIfDifferent(key, value);
}
