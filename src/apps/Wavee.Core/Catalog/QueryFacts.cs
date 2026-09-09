using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Wavee.Core.Catalog;

/// <summary>Data interpretation only. Request activity, freshness and failures belong to ResourceSnapshot.</summary>
public readonly record struct QueryFact(Knowledge Knowledge, CatalogValue? Value);

public static class QueryFacts
{
    public static IReadOnlyDictionary<ResourceKey, QueryFact> Empty { get; } =
        new ReadOnlyDictionary<ResourceKey, QueryFact>(new Dictionary<ResourceKey, QueryFact>());
}

/// <summary>An allocation-free live view over a query's <see cref="ResourceSnapshot"/> map: every
/// <see cref="QueryFact"/> is computed on lookup instead of being copied into a second ~10k-entry dictionary per
/// publication. <see cref="From"/> is the identity-preserving factory PublishJoin calls: it hands back the
/// PREVIOUS view by reference when nothing a consumer can observe through <see cref="QueryFact"/> (Knowledge,
/// Value) moved, so a consumer's reference-identity gate holds across an activity-only publication exactly as it
/// did when facts were a copied dictionary.</summary>
public sealed class FactsView : IReadOnlyDictionary<ResourceKey, QueryFact>
{
    readonly IReadOnlyDictionary<ResourceKey, ResourceSnapshot> _resources;

    public FactsView(IReadOnlyDictionary<ResourceKey, ResourceSnapshot> resources) => _resources = resources;

    public int Count => _resources.Count;
    public IEnumerable<ResourceKey> Keys => _resources.Keys;
    public IEnumerable<QueryFact> Values { get { foreach (var pair in this) yield return pair.Value; } }
    public bool ContainsKey(ResourceKey key) => _resources.ContainsKey(key);

    public QueryFact this[ResourceKey key]
    {
        get { var snapshot = _resources[key]; return new(snapshot.Knowledge, snapshot.Value); }
    }

    public bool TryGetValue(ResourceKey key, out QueryFact value)
    {
        if (_resources.TryGetValue(key, out var snapshot)) { value = new(snapshot.Knowledge, snapshot.Value); return true; }
        value = default; return false;
    }

    public IEnumerator<KeyValuePair<ResourceKey, QueryFact>> GetEnumerator()
    {
        foreach (var pair in _resources) yield return new(pair.Key, new(pair.Value.Knowledge, pair.Value.Value));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Same identity contract the deleted <c>QueryFacts.Update</c> gave: a new view is minted only when
    /// some key's Knowledge or Value actually differs (or the key set differs) from <paramref name="previous"/>;
    /// otherwise <paramref name="previous"/> itself is returned unchanged.</summary>
    public static IReadOnlyDictionary<ResourceKey, QueryFact> From(
        IReadOnlyDictionary<ResourceKey, QueryFact> previous,
        IReadOnlyDictionary<ResourceKey, ResourceSnapshot> resources)
    {
        bool same = previous.Count == resources.Count;
        if (same)
            foreach (var pair in resources)
                if (!previous.TryGetValue(pair.Key, out var old)
                    || old != new QueryFact(pair.Value.Knowledge, pair.Value.Value))
                { same = false; break; }
        return same ? previous : new FactsView(resources);
    }
}
