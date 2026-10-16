// ── Theming/Contracts/SearchPageProps.cs — the props of the Search results page (themes plan 6.0, T10a2) ───────────────────
//
// `Search.PageHost` keeps everything that decides: the query row, the facet strip and its selection, the readiness of every facet and section, the
// demand and the Retry verbs, the recheck deadline, the fallback interleave, the playlist rail's items and the best-matches column hysteresis. It hands
// the view the DATA it draws, in two layers: the page's own state (the echo, the tabs, the slide, the verbs, `Pending` / `Failed`) and the BODY, a
// model (`SearchBodyModel`) the host builds each time the skeleton region asks for its content, so a hydrating hit row still re-reads in the region's
// own binding. The hit adapters (`Search.HitData`, `OpenHit`, `PlayHit`, `MenuOf`, `DragOf`, `TrailingOf`, `TitleOf`, `CoverOf`, `SubtitleOf`) are core
// verbs and data over a hit's `EntityRef`; a view calls them and draws the surface. Every member is read-only (WVTH006).


namespace Wavee.Theming.Contracts;

/// <summary>Which way the facet body slides in: never on a mount, only on a real chip change.</summary>
public enum SearchSlide : byte
{
    /// <summary>No slide (the first render, or the chip did not change).</summary>
    None,

    /// <summary>A later tab than before.</summary>
    Forward,

    /// <summary>An earlier tab than before.</summary>
    Back,
}

/// <summary>One facet tab: the facet (its label is <see cref="Search.FacetNameKey"/>) and the count the tab wears (0 draws none).</summary>
public readonly record struct SearchTab(SearchFacet Facet, int Count);

/// <summary>The answer of one section of the All tab (the genre links, the related queries).</summary>
public enum SearchSection : byte
{
    /// <summary>Asked, still in flight.</summary>
    Pending,

    /// <summary>Answered with its list.</summary>
    Ready,

    /// <summary>Un-asked after this page asked (a terminal failure): the compact error with Retry.</summary>
    Failed,

    /// <summary>The answer concluded without it: nothing draws.</summary>
    Concluded,
}

/// <summary>Which body a <see cref="SearchBodyModel"/> describes.</summary>
public enum SearchBodyKind : byte
{
    /// <summary>The facet has not answered: the shimmer in the facet's shape.</summary>
    Pending,

    /// <summary>The facet's own empty sentence (its captioned generic empty, or nothing for Genres).</summary>
    Empty,

    /// <summary>The All tab: the Top result and Best matches (or the fallback list), the playlist rail, the genre links and the related queries.</summary>
    All,

    /// <summary>The dedicated Genres tab: the genre links with no header.</summary>
    Genres,

    /// <summary>The Songs grid.</summary>
    Songs,

    /// <summary>The Albums / Playlists virtualized card grid over the facet row's own edge.</summary>
    FacetGrid,

    /// <summary>A flat hit list (Artists, Podcasts, Episodes, Profiles).</summary>
    Rows,
}

/// <summary>What the facet body draws, built by the host each time the skeleton region asks for its content.</summary>
public sealed record SearchBodyModel
{
    /// <summary>Which body this is.</summary>
    public required SearchBodyKind Kind { get; init; }

    /// <summary>The facet the body belongs to (its empty sentence and the Songs facet's reveal).</summary>
    public required SearchFacet Facet { get; init; }

    /// <summary>What every hit factory needs beyond the hit (the query, the lookup origin, the overlay, whether track art is hidden).</summary>
    public required Search.HitContext Hits { get; init; }

    /// <summary>The All tab's Top result (the ranked list's first hit); null when the answer carried none or was collected.</summary>
    public Search.HitItem? Hero { get; init; }

    /// <summary>The All tab's Best matches (the hits after the Top result); null when the ranked list had no hit past the first, a list (possibly
    /// empty, when every later hit was a hole) otherwise.</summary>
    public IReadOnlyList<Search.HitItem>? Best { get; init; }

    /// <summary>The All tab's no-top-hits list (the pure interleave over the collected hits); null when the Top result is used. An empty list is the empty sentence.</summary>
    public IReadOnlyList<Search.FallbackHit>? Fallback { get; init; }

    /// <summary>The All tab's playlist rail items; empty draws nothing.</summary>
    public IReadOnlyList<Search.HitItem> Rail { get; init; } = Array.Empty<Search.HitItem>();

    /// <summary>The All tab's artist rail items (contract 1.22): the ranked hits' artists, then the Artists facet's own list once it has landed, never the
    /// Top result itself. A structure that draws no artist rail ignores it (Classic draws one at Huge only, <c>C-Search-Huge</c>); empty draws nothing.</summary>
    public IReadOnlyList<Search.HitItem> ArtistRail { get; init; } = Array.Empty<Search.HitItem>();

    /// <summary>The genre section's answer (All).</summary>
    public SearchSection GenreState { get; init; }

    /// <summary>The genre tiles (the All tab's section and the Genres tab), each opening its browse page with the lookup origin.</summary>
    public IReadOnlyList<BrowseTileModel> Genres { get; init; } = Array.Empty<BrowseTileModel>();

    /// <summary>The related section's answer (All).</summary>
    public SearchSection RelatedState { get; init; }

    /// <summary>The related queries, the typed one already dropped.</summary>
    public IReadOnlyList<string> Related { get; init; } = Array.Empty<string>();

    /// <summary>The Songs grid's cells and a flat list's rows, in server order (holes dropped).</summary>
    public IReadOnlyList<Search.HitItem> Rows { get; init; } = Array.Empty<Search.HitItem>();

    /// <summary>The facet row the Albums / Playlists grid virtualizes over (its extent, its hits and its placeholders are read live from it).</summary>
    public Search Row { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SearchPage"/>. The host rebuilds it on every render it makes, so the page view re-runs exactly when the
/// page tree used to be rebuilt.</summary>
public sealed record SearchPageProps
{
    /// <summary>The query has no valid All row (a blank query): the generic empty vacancy.</summary>
    public required bool Vacant { get; init; }

    /// <summary>The committed query lowercased (the echo).</summary>
    public required string Echo { get; init; }

    /// <summary>The tabs stay skeletal (the eleven placeholder pills) until the first body has answered for this query.</summary>
    public required bool ChipsSkeletal { get; init; }

    /// <summary>The facet strip, All first; the selected tab is <see cref="Selected"/>.</summary>
    public required IReadOnlyList<SearchTab> Tabs { get; init; }

    /// <summary>The selected tab's index.</summary>
    public required int Selected { get; init; }

    /// <summary>One cached click per tab index (the host owns them: the chip signal).</summary>
    public required IReadOnlyList<Action> Selectors { get; init; }

    /// <summary>The selected facet (the body's key, its shimmer shape and its scroll shell).</summary>
    public required SearchFacet Facet { get; init; }

    /// <summary>The body's slide: only on a real chip change.</summary>
    public required SearchSlide Slide { get; init; }

    /// <summary>The scroll key of a scrolling facet's offset (the tab's scope, "search:", the facet).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The facet body has not answered: the shimmer shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The facet body failed (a terminal failure un-asked it): the error vacancy with <see cref="Retry"/>.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The body's model; read inside the skeleton region's content binding.</summary>
    public required Func<SearchBodyModel> Body { get; init; }

    /// <summary>The facet body's Retry.</summary>
    public required Action Retry { get; init; }

    /// <summary>A section-level Retry on the All tab (the genre links and the related queries).</summary>
    public required Action RetryAll { get; init; }

    /// <summary>The playlist rail's header: switch to the Playlists tab (not a navigation).</summary>
    public required Action SelectPlaylists { get; init; }

    /// <summary>Best matches' column count for a measured width, with the page's hysteresis behind it.</summary>
    public required Func<float, int> ColsFor { get; init; }

    /// <summary>The artist rail's header: switch to the Artists tab (not a navigation; contract 1.22). Null where the host offers no Artists tab.</summary>
    public Action? SelectArtists { get; init; }
}
