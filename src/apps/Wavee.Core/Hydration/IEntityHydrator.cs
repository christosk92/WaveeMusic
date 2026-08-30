using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

// ── THE hydration façade (docs/plans/wavee/hydration-facade-design.md §1.3) ──────────────────────────────────────────
// ONE entry point for every metadata fetch/enrich in the app. The UI asks the catalog, playback asks this directly, and
// both land in the same router → provider hydrator → ladder → store. Nothing else fetches catalog metadata.

/// <summary>Whether the caller waits. <see cref="Blocking"/> returns when the level is reached OR the ladder is
/// exhausted (it never hangs on a background continuation); <see cref="Background"/> enqueues on the pump and returns
/// the CURRENT level immediately — the caller re-reads the store through <c>IStore.Changes</c>.</summary>
public enum HydrationMode : byte { Blocking, Background }

/// <summary>How one hydration request should behave. <c>default</c> is a blocking, non-revalidating, unattributed,
/// normal-priority request — the shape every page open wants.</summary>
/// <param name="Revalidate">Ignore a fresh ledger seal and re-ask the transport (the "known-better" path).</param>
/// <param name="Surface">Which screen asked — picks the trait bundle AND the <c>client-feature-id</c> attribution.</param>
/// <param name="Priority">Pump ordering; negative = prefetch (yield to anything a user is looking at).</param>
/// <param name="SubAsk">Diagnostics only — never read by any ladder or the trait pipeline. True marks a ladder's OWN
/// internal identity/ref repair (a thin-member scan, a stub batch, an album's disc-row fix-up) that deliberately never
/// carries its caller's <see cref="TraitSurface"/>: the repair is identity work, not that surface's trait ask. Left
/// false, a <see cref="TraitSurface.None"/> ask is a REAL caller's own surface, and the census logs it distinctly —
/// see <c>TrackHydrationCensus.TraitNotAsked</c> vs <c>TrackHydrationCensus.TraitSurfaceEmpty</c>.</param>
public readonly record struct HydrationOptions(
    HydrationMode Mode = HydrationMode.Blocking,
    bool Revalidate = false,
    TraitSurface Surface = TraitSurface.None,
    int Priority = 0,
    bool SubAsk = false)
{
    public static readonly HydrationOptions Default = new();

    /// <summary>Speculative warm-up: background, lowest priority, attributed as a prefetch.</summary>
    public static readonly HydrationOptions Prefetch =
        new(HydrationMode.Background, Priority: -1, Surface: TraitSurface.Prefetch);
}

/// <summary>How a hydration attempt ended.</summary>
public enum HydrationStatus : byte
{
    /// <summary>The requested level is resident.</summary>
    Reached,
    /// <summary>The ladder RAN and could not get there (the level is sealed Exhausted for its TTL) — or a background
    /// request that has been enqueued but not yet observed.</summary>
    Partial,
    /// <summary>A transport failure. Nothing is sealed; the next ask retries.</summary>
    Failed,
    /// <summary>The caller's token cancelled.</summary>
    Cancelled,
    /// <summary>Structurally impossible here: offline, no owning source, or a kind with no ladder.</summary>
    Unsupported,
}

/// <summary>What one <c>EnsureAsync</c> produced: the level actually resident when it returned, and why it stopped.</summary>
public readonly record struct HydrationOutcome(HydrationLevel Reached, HydrationStatus Status, string? Error = null)
{
    public bool Ok => Status == HydrationStatus.Reached;
}

/// <summary>What one <c>EnsureManyAsync</c> produced. <see cref="Missing"/> is the uris that did NOT make the level —
/// a caller that must render something (a queue, a context) uses it to pick its placeholder.</summary>
public readonly record struct HydrationBatchOutcome(
    IReadOnlyCollection<string> Reached, IReadOnlyCollection<string> Missing, HydrationStatus Status);

/// <summary>The one metadata façade. Implementations never throw for transport reasons — a failure is a
/// <see cref="HydrationStatus"/>, and <c>EnsureTraitsAsync</c> swallows them entirely (traits are always optional
/// polish). There is NO nullable hydrator anywhere: an offline/unowned uri gets a real implementation that answers
/// <see cref="HydrationStatus.Unsupported"/>.</summary>
public interface IEntityHydrator
{
    /// <summary>What is resident RIGHT NOW — presence-only, synchronous, store-backed. Cheap enough for a render pass.</summary>
    HydrationLevel LevelOf(string uri);

    Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default);

    Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default);

    /// <summary>Ensure the per-playable extension traits a surface wants (TraitPolicy picks the set).</summary>
    Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default);

    /// <summary>Ensure an EXPLICIT trait set (the toggle paths: the Plays column, a video warm).</summary>
    Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default);

    /// <summary>A known-better outcome arrived out of band (dealer push, video canonical recovery): unseal every level
    /// for this uri so the next ask really re-fetches. The escape hatch for an Exhausted seal.</summary>
    void Invalidate(string uri);
}
