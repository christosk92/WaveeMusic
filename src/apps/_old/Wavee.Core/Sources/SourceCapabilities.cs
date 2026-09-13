namespace Wavee.Core;

/// <summary>What a source can do — the <c>supported_features</c> analog (see docs/plans/wavee/architecture.md §4.2). A source
/// implements only the facets it supports and ORs the matching flags here; the aggregate routes only to capable
/// sources and the UI gates affordances on declared capability (no dead buttons). Catalog is the only facet wired in
/// the first pass; the rest are declared seams that real sources (a live Spotify account, a local-files source) fill.</summary>
[System.Flags]
public enum SourceCapabilities
{
    None = 0,
    Catalog = 1 << 0,   // playlists / albums / artists / library / stats reads
    Home = 1 << 1,   // contributes home-feed groups
    Search = 1 << 2,   // search
    Podcasts = 1 << 3,   // shows / episodes
    Playback = 1 << 4,   // owns a player for its contexts
    Remote = 1 << 5,   // Connect devices / transfer
    Session = 1 << 6,   // auth / account / market / tier
    Lyrics = 1 << 7,   // lyrics
    Mutations = 1 << 8,   // save / follow / playlist edits / folders
    LocalDecode = 1 << 9,   // decodes local files (vs streamed)
    /// <summary>The LAST-RESORT catalog source: it answers a single-item read no source OWNS (and one whose owner
    /// returned null), so an unrecognized uri still opens instead of painting an empty shell. Exactly one source
    /// declares it (the synthetic <c>FakeSource</c> in the demo backend); the real backend has none, which is the
    /// point — a real account must not invent an entity. Routing is unaffected: hydration and ownership still go
    /// through <see cref="ISource.Owns"/>, so the fallback is a READ affordance only (design §2.1).</summary>
    Fallback = 1 << 10,
}
