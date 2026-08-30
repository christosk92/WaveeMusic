using System.Collections.Generic;
using System.Linq;

namespace Wavee.Core;

/// <summary>The ordered set of connected sources (docs/plans/wavee/architecture.md §4.3). Order matters: the first source that
/// <see cref="ISource.Owns"/> a URI wins single-item routing, so put richer/real sources before the fallback.</summary>
public sealed class SourceRegistry
{
    readonly IReadOnlyList<ISource> _sources;
    /// <summary>The catalog subset, computed ONCE at construction. The registry is immutable (the composition root
    /// hands it a fixed list), and <see cref="OwnerOf"/> is on the hot path of every hydration route — a per-call
    /// <c>OfType/Where/FirstOrDefault</c> chain allocated three iterators and a closure for a walk over ~3 sources.</summary>
    readonly ICatalogSource[] _catalog;

    public SourceRegistry(IReadOnlyList<ISource> sources)
    {
        _sources = sources;
        var catalog = new List<ICatalogSource>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
            if (sources[i] is ICatalogSource c && (c.Capabilities & SourceCapabilities.Catalog) != 0)
                catalog.Add(c);
        _catalog = catalog.ToArray();
    }

    public IReadOnlyList<ISource> All => _sources;

    /// <summary>Catalog-capable sources, in registry order.</summary>
    public IReadOnlyList<ICatalogSource> CatalogSources => _catalog;

    /// <summary>The first catalog source that owns <paramref name="uri"/> (null if none — the aggregate then falls back).</summary>
    public ICatalogSource? OwnerOf(string uri)
    {
        for (int i = 0; i < _catalog.Length; i++)
            if (_catalog[i].Owns(uri)) return _catalog[i];
        return null;
    }

    /// <summary>Sources that declare a capability, in registry order — the hook the future per-facet federation
    /// (FederatedPlayback / FederatedRemote) routes through.</summary>
    public IEnumerable<ISource> OfCapability(SourceCapabilities cap) => _sources.Where(s => (s.Capabilities & cap) != 0);
}
