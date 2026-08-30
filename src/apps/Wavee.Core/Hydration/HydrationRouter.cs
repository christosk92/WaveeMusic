using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

// ── THE multi-source hydration router (docs/plans/wavee/hydration-facade-design.md §2.1, architecture.md §4.3) ───────
// `Services.Hydrator` is this. It answers the SAME ownership question single-item catalog reads already ask —
// `SourceRegistry.OwnerOf(uri)`, i.e. the first Catalog-capable source whose `Owns` claims the uri — and forwards to
// that source's `ICatalogSource.Hydrator`. There is therefore no second notion of "who owns this uri": a source that
// owns a namespace for reads owns it for hydration too. A uri NO source owns gets `NotOwnedEntityHydrator`
// (`Unsupported`) rather than an exception, which is exactly why a MIXED batch — a Spotify playlist holding a local
// import, a queue mixing `spotify:` and `wavee:playlist:` rows — completes instead of poisoning the whole call.
//
// Zero-dep: this lives in Wavee.Core because it needs nothing but the registry and the port.

/// <summary>The `IEntityHydrator` that fans a request out across the connected sources. Stateless and immutable — the
/// registry it routes through is fixed at construction, and every per-source seam behind it (the Spotify source's
/// <see cref="SwitchableEntityHydrator"/>) does its own go-live/offline flipping, so ONE router instance is correct for
/// the whole process lifetime.</summary>
public sealed class HydrationRouter : IEntityHydrator
{
    readonly SourceRegistry _registry;

    public HydrationRouter(SourceRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Who hydrates this uri. Never null — an unowned uri answers <see cref="NotOwnedEntityHydrator"/>.</summary>
    public IEntityHydrator HydratorFor(string uri)
        => _registry.OwnerOf(uri)?.Hydrator ?? NotOwnedEntityHydrator.Instance;

    public HydrationLevel LevelOf(string uri) => HydratorFor(uri).LevelOf(uri);

    public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => HydratorFor(uri).EnsureAsync(uri, level, opts, ct);

    public async Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        if (uris is not { Count: > 0 })
            return new HydrationBatchOutcome(Array.Empty<string>(), Array.Empty<string>(), HydrationStatus.Reached);

        var groups = GroupByOwner(uris);
        // Single owner (the overwhelmingly common case: one page's worth of one provider's uris) → forward the
        // caller's own list, so nothing is copied and the hydrator sees exactly what it was handed.
        if (groups.Count == 1 && groups[0].Uris.Count == uris.Count)
            return await groups[0].Hydrator.EnsureManyAsync(uris, level, opts, ct).ConfigureAwait(false);

        var reached = new List<string>(uris.Count);
        var missing = new List<string>();
        var status = HydrationStatus.Reached;
        foreach (var g in groups)
        {
            var outcome = await g.Hydrator.EnsureManyAsync(g.Uris, level, opts, ct).ConfigureAwait(false);
            reached.AddRange(outcome.Reached);
            missing.AddRange(outcome.Missing);
            status = Worst(status, outcome.Status);
        }
        return new HydrationBatchOutcome(reached, missing, status);
    }

    public async Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
    {
        if (uris is not { Count: > 0 }) return;
        var groups = GroupByOwner(uris);
        if (groups.Count == 1 && groups[0].Uris.Count == uris.Count)
        {
            await groups[0].Hydrator.EnsureTraitsAsync(uris, surface, ct).ConfigureAwait(false);
            return;
        }
        foreach (var g in groups)
            await g.Hydrator.EnsureTraitsAsync(g.Uris, surface, ct).ConfigureAwait(false);
    }

    public async Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface,
        CancellationToken ct = default)
    {
        if (uris is not { Count: > 0 }) return;
        var groups = GroupByOwner(uris);
        if (groups.Count == 1 && groups[0].Uris.Count == uris.Count)
        {
            await groups[0].Hydrator.EnsureTraitsAsync(uris, traits, surface, ct).ConfigureAwait(false);
            return;
        }
        foreach (var g in groups)
            await g.Hydrator.EnsureTraitsAsync(g.Uris, traits, surface, ct).ConfigureAwait(false);
    }

    public void Invalidate(string uri) => HydratorFor(uri).Invalidate(uri);

    // ── grouping ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Groups come back in REGISTRY order (the unowned group last), and within a group the uris keep the caller's
    // first-seen order — so a batch is deterministic regardless of how the caller interleaved providers, and the
    // richest/first-registered source is always asked first.

    readonly struct OwnerGroup(IEntityHydrator hydrator, List<string> uris)
    {
        public IEntityHydrator Hydrator { get; } = hydrator;
        public List<string> Uris { get; } = uris;
    }

    List<OwnerGroup> GroupByOwner(IReadOnlyList<string> uris)
    {
        Dictionary<ICatalogSource, List<string>>? byOwner = null;
        List<string>? unowned = null;
        for (int i = 0; i < uris.Count; i++)
        {
            string uri = uris[i];
            if (_registry.OwnerOf(uri) is not { } owner) { (unowned ??= new List<string>()).Add(uri); continue; }
            byOwner ??= new Dictionary<ICatalogSource, List<string>>();
            if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<string>();
            list.Add(uri);
        }

        var groups = new List<OwnerGroup>((byOwner?.Count ?? 0) + 1);
        if (byOwner is not null)
            foreach (var source in _registry.CatalogSources)
                if (byOwner.TryGetValue(source, out var list)) groups.Add(new OwnerGroup(source.Hydrator, list));
        if (unowned is not null) groups.Add(new OwnerGroup(NotOwnedEntityHydrator.Instance, unowned));
        return groups;
    }

    /// <summary>The merged status of a fan-out: the WORST any group reported. Severity is
    /// Reached &lt; Partial &lt; Unsupported &lt; Cancelled &lt; Failed — `Failed` last because it is the only one that
    /// means "ask again", and `Unsupported` above `Partial` because a group nobody owns can never improve.</summary>
    internal static HydrationStatus Worst(HydrationStatus a, HydrationStatus b)
        => Severity(b) > Severity(a) ? b : a;

    static int Severity(HydrationStatus s) => s switch
    {
        HydrationStatus.Reached => 0,
        HydrationStatus.Partial => 1,
        HydrationStatus.Unsupported => 2,
        HydrationStatus.Cancelled => 3,
        _ => 4,   // Failed
    };
}
