using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

// ── The three portable IEntityHydrator implementations (design §1.3) ─────────────────────────────────────────────────
// Wiring discipline: no seam is ever null. A source that has nothing to fetch gets CompleteEntityHydrator, an unowned
// uri gets NotOwnedEntityHydrator, and a seam that flips at go-live/offline gets SwitchableEntityHydrator — so every
// caller can `await hydrator.EnsureAsync(...)` unconditionally.

/// <summary>For COMPLETE-AT-CONSTRUCTION sources (the Spotify export, local files, the synthetic catalog, session
/// user playlists, test fakes): every entity they own is fully materialized the moment they answer, so every rung is
/// already reached and there is nothing to fetch.</summary>
public sealed class CompleteEntityHydrator : IEntityHydrator
{
    public static readonly CompleteEntityHydrator Instance = new();
    CompleteEntityHydrator() { }

    public HydrationLevel LevelOf(string uri) => HydrationLevel.Full;

    public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => Task.FromResult(new HydrationOutcome(HydrationLevel.Full, HydrationStatus.Reached));

    public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => Task.FromResult(new HydrationBatchOutcome(uris, Array.Empty<string>(), HydrationStatus.Reached));

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Invalidate(string uri) { }
}

/// <summary>The answer for a uri NO registered source owns. Not an error and not an exception — the router hands this
/// back so a mixed batch (a Spotify playlist holding a local file, say) still completes.</summary>
public sealed class NotOwnedEntityHydrator : IEntityHydrator
{
    public static readonly NotOwnedEntityHydrator Instance = new();
    NotOwnedEntityHydrator() { }

    public HydrationLevel LevelOf(string uri) => HydrationLevel.None;

    public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => Task.FromResult(new HydrationOutcome(HydrationLevel.None, HydrationStatus.Unsupported));

    public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => Task.FromResult(new HydrationBatchOutcome(Array.Empty<string>(), uris, HydrationStatus.Unsupported));

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Invalidate(string uri) { }
}

/// <summary>The go-live/offline seam: one stable reference every consumer holds forever, whose INNER flips between the
/// provider hydrator (live) and the offline one (logged out). <see cref="SetInner"/> is the only mutation and the field
/// is volatile, so a call already in flight keeps running against the implementation it started on.</summary>
public sealed class SwitchableEntityHydrator : IEntityHydrator
{
    volatile IEntityHydrator _inner;

    /// <param name="inner">REQUIRED — the offline/complete implementation this starts on. There is no null state.</param>
    public SwitchableEntityHydrator(IEntityHydrator inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IEntityHydrator Inner => _inner;

    public void SetInner(IEntityHydrator inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public HydrationLevel LevelOf(string uri) => _inner.LevelOf(uri);

    public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => _inner.EnsureAsync(uri, level, opts, ct);

    public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
        => _inner.EnsureManyAsync(uris, level, opts, ct);

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
        => _inner.EnsureTraitsAsync(uris, surface, ct);

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
        => _inner.EnsureTraitsAsync(uris, traits, surface, ct);

    public void Invalidate(string uri) => _inner.Invalidate(uri);
}
