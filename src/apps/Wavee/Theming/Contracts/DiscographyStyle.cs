// ── Theming/Contracts/DiscographyStyle.cs — what a structure supplies for the discography (themes plan 6.1, T10b2) ─────────────────
//
// The discography is the artist page's three inline facet sections (Albums, Singles & EPs, Compilations) and the standalone `disco:` page's one facet: an
// accent-spined, pinned facet header over a virtualized grid of album cards whose clicked card opens an inline album drawer (the tracklist, its selection
// bar, the "Show all" row) under its row, with a caret apexed on the card. The structure registers a `DiscographyStyle` under `StyleKeys.Discography`; the
// core components (`Entities\Artist.Discography.cs`: `FacetHost`, `DiscoGridHost`, `DiscoCell`, `DrawerPanel`, `DrawerSlot`, `DrawerRowHost`) keep every
// hook, memo, effect and decision and call the templates while they render, so their subscriptions, keys and effect timing are exactly what they were.
//
// WHAT THE CORE KEEPS. The facet's demand and its next-page pacing, its presence and failure verdicts, the era plan and its year-fold memo, the visible
// range, the grid's count memo (the facet's server total), the expanded ALBUM SLOT (identity, never an ordinal) and its re-derived index, the drawer's ONE
// verdict (`Album.DrawerVerdict` through `Artist.DrawerVerdictFor`), the tracklist demand and its Retry, each cell's row stamp and the cached card data it
// gates on, the card's verbs (toggle, play, drag payload, menu), the drawer's selection model, its list mount (the geometry-keyed `ItemsView`), the rows'
// invoke / menu / drag and the play-from-here. A template receives the facts and the verbs and draws: it never names `Playback`, `Entities` or `Shell`.
//
// A host mounted where no registry provides the key draws nothing (logged once, `ListStyleLookup`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The grid configuration the structure decides: the column minimum, the gap, the card's chrome under its square cover (the card is the cell
/// width plus it) and the vertical gutter folded into the grid's row extra.</summary>
internal readonly record struct DiscographyMetrics(float MinColumn, float Gap, float CardChrome, float RowGap);

/// <summary>What the core decided for one render of a facet section.</summary>
internal sealed class DiscoFacetArgs
{
    /// <summary>The facet's title ("Albums", "Singles &amp; EPs", "Compilations").</summary>
    public required string Title { get; init; }

    /// <summary>The facet's server total (the header's "N releases").</summary>
    public required int Total { get; init; }

    /// <summary>The era plan of a COMPLETE facet (null while Partial).</summary>
    public DiscographyEraBand[]? Eras { get; init; }

    /// <summary>The grid's realized window: the header's label follows it (an era's label while the grid shows that era).</summary>
    public required IReadSignal<LazyGridVisibleRange> Visible { get; init; }

    /// <summary>The page accent, read in the host's render (the header's spine).</summary>
    public required ColorF Accent { get; init; }

    /// <summary>The facet's grid (the core's grid component).</summary>
    public required Element Grid { get; init; }

    /// <summary>Where the header pins (the band's height above it); with the header's own row, the grid's sticky clip inset.</summary>
    public required float PinTop { get; init; }
}

/// <summary>What one card paints off its row (built once per row, version and height; the host caches the data it returns).</summary>
internal sealed class DiscoCardArgs
{
    /// <summary>The album's uri text.</summary>
    public required string Uri { get; init; }

    /// <summary>The album's title.</summary>
    public required string Title { get; init; }

    /// <summary>The meta line ("2019 · Album · 12 songs").</summary>
    public required string Meta { get; init; }

    /// <summary>The cover url.</summary>
    public string? Cover { get; init; }

    /// <summary>The card's click: open / close its drawer.</summary>
    public required Action Click { get; init; }

    /// <summary>The cover's play.</summary>
    public required Action Play { get; init; }

    /// <summary>The card's drag source.</summary>
    public required DragSource Drag { get; init; }

    /// <summary>The card's menu, built at open.</summary>
    public required Func<ContextMenuModel?> Menu { get; init; }

    /// <summary>The card's pinned height (the cell width plus <see cref="DiscographyMetrics.CardChrome"/>).</summary>
    public required float Height { get; init; }
}

/// <summary>What the core decided for the drawer's frame under the opened card's row.</summary>
internal sealed class DiscoDrawerFrameArgs
{
    /// <summary>The per-album panel (keyed "drawer:" + uri).</summary>
    public required Element Panel { get; init; }

    /// <summary>The reserved slot height (the verdict's).</summary>
    public required float SlotHeight { get; init; }

    /// <summary>The grid's columns, cell width, gap and the opened card's left edge (the caret's apex).</summary>
    public required int Columns { get; init; }

    /// <inheritdoc cref="Columns"/>
    public required float CellWidth { get; init; }

    /// <inheritdoc cref="Columns"/>
    public required float Gap { get; init; }

    /// <inheritdoc cref="Columns"/>
    public required float Left { get; init; }
}

/// <summary>What the core decided for one render of the album drawer's panel.</summary>
internal sealed class DiscoDrawerPanelArgs
{
    /// <summary>The album the drawer previews.</summary>
    public required Album Album { get; init; }

    /// <summary>THE verdict: loading (the shimmer at the shown count), ready-but-empty (the Retry note) or the rows.</summary>
    public required Album.DrawerVerdict Verdict { get; init; }

    /// <summary>The head's Play (the album, or a toggle while it plays).</summary>
    public required Action PlayAlbum { get; init; }

    /// <summary>Open the album's page (the head's title and its open button).</summary>
    public required Action GoAlbum { get; init; }

    /// <summary>The ready-but-empty note's Retry (re-asks the tracklist while the drawer stays open).</summary>
    public required Action Retry { get; init; }

    /// <summary>The rows' bound list (the core mounts it once per geometry); null while there is no cell to list.</summary>
    public Element? List { get; init; }

    /// <summary>The rows the list spans (each <see cref="Album.DrawerVerdict.RowPitch"/> tall).</summary>
    public required int Rows { get; init; }

    /// <summary>The floating selection bar (the core's component).</summary>
    public required Element Bar { get; init; }
}

/// <summary>What the core decided for one bound slot of the drawer's list (a track row).</summary>
internal sealed class DiscoDrawerSlotArgs
{
    /// <summary>The row's keyed cell component.</summary>
    public required Element Content { get; init; }

    /// <summary>The slot's index and the verdict (the two-column gutter: the left column insets its inner edge, the right one its own).</summary>
    public required int Index { get; init; }

    /// <inheritdoc cref="Index"/>
    public required Album.DrawerVerdict Verdict { get; init; }

    /// <summary>The slot's row scope: the selection, the interaction funnel and the focus report.</summary>
    public required RowScope Scope { get; init; }

    /// <summary>The release that routes a tap / double tap into the funnel, and the Enter / Space keys.</summary>
    public required Action<PointerEventArgs> Release { get; init; }

    /// <inheritdoc cref="Release"/>
    public required Action<KeyEventArgs> Key { get; init; }

    /// <summary>The row's drag (the selection when the press is on it, else the row).</summary>
    public required DragSource Drag { get; init; }

    /// <summary>Are the check lanes shown (a multi-selection)?</summary>
    public required Func<bool> ChecksVisible { get; init; }

    /// <summary>The single-selection pill's fill and opacity (bound: selecting is compositor-only).</summary>
    public required Prop<ColorF> PillFill { get; init; }

    /// <inheritdoc cref="PillFill"/>
    public required Prop<float> PillOpacity { get; init; }
}

/// <summary>What the core decided for the drawer's "Show all N tracks" cell.</summary>
internal sealed class DiscoShowAllArgs
{
    /// <summary>The album's track total.</summary>
    public required int Total { get; init; }

    /// <summary>The release and the keys that open the album.</summary>
    public required Action<PointerEventArgs> Release { get; init; }

    /// <inheritdoc cref="Release"/>
    public required Action<KeyEventArgs> Key { get; init; }

    /// <summary>The slot's focus report.</summary>
    public required Action<bool> FocusChanged { get; init; }
}

/// <summary>What the core decided for one drawer track cell.</summary>
internal sealed class DiscoDrawerTrackArgs
{
    /// <summary>The row's track and index.</summary>
    public required Track Track { get; init; }

    /// <inheritdoc cref="Track"/>
    public required int Index { get; init; }

    /// <summary>The row's playback / library state.</summary>
    public required Track.RowState State { get; init; }

    /// <summary>The # cell's play (the album from this track) and the heart's like.</summary>
    public required Action Play { get; init; }

    /// <inheritdoc cref="Play"/>
    public required Action Like { get; init; }
}

/// <summary>The grid's configuration in <paramref name="tiers"/>: the window tiers of the facts the calling grid is drawn in (its root's, never the
/// process's last window).</summary>
internal delegate DiscographyMetrics DiscographyMetricsTemplate(in TierFacts tiers);

/// <summary>Draws a facet section: the spined header (an expander that pins), the grid clipped under it, the trailing air.</summary>
internal delegate Element DiscoFacetTemplate(DiscoFacetArgs args);

/// <summary>Draws a failed facet: the compact Error vacancy and its Retry.</summary>
internal delegate Element DiscoFacetFailedTemplate(Action retry);

/// <summary>Builds a card's data (the shared surface draws it).</summary>
internal delegate Controls.CardData DiscoCardTemplate(DiscoCardArgs args);

/// <summary>Builds the OPENED variant of a card's data (the accent skin and the up chevron).</summary>
internal delegate Controls.CardData DiscoOpenedTemplate(Controls.CardData plain, string meta, Func<ColorF> accent);

/// <summary>Builds the seed's data (no row yet) at the card's pinned height.</summary>
internal delegate Controls.CardData DiscoSeedTemplate(float height);

/// <summary>Draws one cell from its data (the seed and the card share one key, so a row landing is a props re-push).</summary>
internal delegate Element DiscoCellTemplate(Controls.CardData data);

/// <summary>Draws the drawer's frame: the clipped, revealed slot, the panel under its top gap and the caret.</summary>
internal delegate Element DiscoDrawerFrameTemplate(DiscoDrawerFrameArgs args);

/// <summary>Draws the drawer's panel: the head over the body (shimmer, empty note or rows) and the floating selection bar.</summary>
internal delegate Element DiscoDrawerPanelTemplate(DiscoDrawerPanelArgs args);

/// <summary>Draws the drawer's floating selection bar at a count (0: hidden).</summary>
internal delegate Element DiscoDrawerBarTemplate(int count, Func<int, Element> lane);

/// <summary>Draws one track slot's skin. The core attaches the context menu to what it returns.</summary>
internal delegate BoxEl DiscoDrawerSlotTemplate(DiscoDrawerSlotArgs args);

/// <summary>Draws the "Show all N tracks" cell.</summary>
internal delegate Element DiscoShowAllTemplate(DiscoShowAllArgs args);

/// <summary>Draws one drawer track cell.</summary>
internal delegate Element DiscoDrawerTrackTemplate(DiscoDrawerTrackArgs args);

/// <summary>The structure's style for the discography (<c>StyleKeys.Discography</c>).</summary>
public sealed class DiscographyStyle
{
    internal DiscographyStyle(DiscographyMetricsTemplate metrics, DiscoFacetTemplate facet, DiscoFacetFailedTemplate facetFailed,
                              DiscoCardTemplate card, DiscoOpenedTemplate opened, DiscoSeedTemplate seed, DiscoCellTemplate cell,
                              DiscoDrawerFrameTemplate drawerFrame, DiscoDrawerPanelTemplate drawerPanel, DiscoDrawerBarTemplate drawerBar,
                              DiscoDrawerSlotTemplate drawerSlot, DiscoShowAllTemplate showAll, DiscoDrawerTrackTemplate drawerTrack)
    {
        Metrics = metrics;
        Facet = facet;
        FacetFailed = facetFailed;
        Card = card;
        Opened = opened;
        Seed = seed;
        Cell = cell;
        DrawerFrame = drawerFrame;
        DrawerPanel = drawerPanel;
        DrawerBar = drawerBar;
        DrawerSlot = drawerSlot;
        ShowAll = showAll;
        DrawerTrack = drawerTrack;
    }

    /// <summary>The grid's configuration.</summary>
    internal DiscographyMetricsTemplate Metrics { get; }

    /// <summary>A facet section.</summary>
    internal DiscoFacetTemplate Facet { get; }

    /// <summary>A failed facet.</summary>
    internal DiscoFacetFailedTemplate FacetFailed { get; }

    /// <summary>A card's data.</summary>
    internal DiscoCardTemplate Card { get; }

    /// <summary>The opened card's data.</summary>
    internal DiscoOpenedTemplate Opened { get; }

    /// <summary>The seed's data.</summary>
    internal DiscoSeedTemplate Seed { get; }

    /// <summary>One cell.</summary>
    internal DiscoCellTemplate Cell { get; }

    /// <summary>The drawer's frame.</summary>
    internal DiscoDrawerFrameTemplate DrawerFrame { get; }

    /// <summary>The drawer's panel.</summary>
    internal DiscoDrawerPanelTemplate DrawerPanel { get; }

    /// <summary>The drawer's selection bar.</summary>
    internal DiscoDrawerBarTemplate DrawerBar { get; }

    /// <summary>One track slot.</summary>
    internal DiscoDrawerSlotTemplate DrawerSlot { get; }

    /// <summary>The "Show all" cell.</summary>
    internal DiscoShowAllTemplate ShowAll { get; }

    /// <summary>One drawer track cell.</summary>
    internal DiscoDrawerTrackTemplate DrawerTrack { get; }
}
