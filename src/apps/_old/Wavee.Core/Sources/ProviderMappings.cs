namespace Wavee.Core;

// Provider-mappings / dedup / fallback (docs/plans/wavee/architecture.md §5, §7; Music Assistant's two-axis provider model). One
// logical item (a track/album) may exist in several sources; keyed by a stable cross-source identity (ISRC / MusicBrainz
// MBID), a ProviderMapping records WHERE it lives and ProviderPolicy picks the preferred provider with a fallback chain.
// Trivial with one real source today — these types are the groundwork the dedup/merge + availability resolution attach
// to (the Source / Origin / Availability fields on the domain records are the rest of the seam). No behavioural change.

/// <summary>One logical item's presence in a specific source: which source owns it, under what uri, and whether it is
/// currently playable there (geo / tier / delisting resolved at queue time, not re-derived in the UI).</summary>
public sealed record ProviderRef(string SourceId, string Uri, Availability Availability = Availability.Playable);

/// <summary>A logical item mapped across sources, keyed by a stable cross-source identity (ISRC / MBID).</summary>
public sealed record ProviderMapping(string Key, IReadOnlyList<ProviderRef> Providers);

/// <summary>Preferred-source policy + fallback chain. Picks the first PLAYABLE provider in preference order (Spotify →
/// Local → …), falling through on unavailability — so playback resolves availability ONCE, at queue time.</summary>
public sealed class ProviderPolicy
{
    readonly IReadOnlyList<string> _preferred;   // source ids, most-preferred first

    public ProviderPolicy(IReadOnlyList<string> preferredSourceOrder) => _preferred = preferredSourceOrder;

    /// <summary>The first playable provider in preference order, then in registry order; null if none is playable.</summary>
    public ProviderRef? Resolve(IReadOnlyList<ProviderRef> providers)
    {
        foreach (var id in _preferred)
            foreach (var p in providers)
                if (p.SourceId == id && p.Availability == Availability.Playable) return p;
        foreach (var p in providers)
            if (p.Availability == Availability.Playable) return p;
        return null;
    }
}

/// <summary>A track resolved for the queue/playback layer: the domain track + the chosen provider, so geo/tier/region
/// availability is baked in at queue time rather than re-checked in the UI (docs/plans/wavee/architecture.md §5). The future
/// queue/playback federation operates on this instead of a bare <see cref="Track"/>.</summary>
public sealed record PlayableTrack(Track Track, ProviderRef Provider);
