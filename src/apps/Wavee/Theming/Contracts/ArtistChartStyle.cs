// ── Theming/Contracts/ArtistChartStyle.cs — what a structure supplies for the artist's Top-tracks chart (themes plan 6.1, T8c) ──────
//
// The chart is ONE PagedShelf collection (a five-row virtual strip with page-mandatory snapping, at most two columns, a "< .. >" pip pager
// in its header) whose realized cells are chart ROW components keyed by track. The structure registers an `ArtistChartStyle` under
// `StyleKeys.ArtistChart`; the core chart host (`Artist.ChartHost`, Entities\Artist.UI.Chart.cs) keeps everything that is not a picture
// and calls the templates while it renders, so its subscriptions, keys and effect timing are exactly what they were.
//
// WHAT THE CORE KEEPS. The readiness gate (identity, artwork; the failure latch and its recheck deadline), the asks (`Demand`,
// `DemandTargets`, the Retry), the (index, slot) items and the row keys (the remount keys: the cell key "row:" + uri + art + style,
// the shelf key columns x art x style: `maxColumns` and `cardHeight` are PagedShelf ctor configuration), the selection model (a chart
// row selection survives the page flips that slide the realized window), the drag payload, the menu, the play-from-here queue, the
// per-row memo over (slot, version, row state) that is the row's render gate, the tier decision (`ArtistPopularLayout.Decide`: rank and
// play-count columns at every width), the standard selection bar (`Controls.SelectionBar`) and the pager's controlled-pips state. A
// template receives the facts and the verbs as args and draws: it never names `Playback`, `Entities`, `Prefs` or `Shell` (WVTH002).
//
// A host mounted where no registry provides the key draws nothing (logged once, `ListStyleLookup`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The shelf configuration the structure decides: Modern rows are 56 tall on a 12 gap, Classic's 48 on 8; the card minimum is a
/// BREAKPOINT (two columns when the chart column is 540 wide) and the maximum is uncapped on purpose.</summary>
internal readonly record struct ArtistChartMetrics(float RowHeight, float CellGap, float HeaderGap, float MinCardWidth, float EdgeFade);

/// <summary>The strings a row paints: the live row's resolved values, or the skeleton's representative shape.</summary>
internal readonly record struct ArtistChartCells(string Title, string? ArtUrl, string Duration, string PlaysCompact, string PlaysExact,
                                                 bool Explicit, bool Video);

/// <summary>What the core decided for one render of a chart row (and what the skeleton passes with nothing live).</summary>
internal sealed class ArtistChartRowArgs
{
    /// <summary>The strings the row paints.</summary>
    public required ArtistChartCells Cells { get; init; }

    /// <summary>The row's ordinal in the chart (the # cell prints <c>Index + 1</c>).</summary>
    public required int Index { get; init; }

    /// <summary>The playback / library state of the row's track.</summary>
    public required Track.RowState State { get; init; }

    /// <summary>The tier the host decided for the cell's width (the art edge, whether the duration shows, whether the sub line stacks).</summary>
    public required ArtistPopularLayout.Tier Tier { get; init; }

    /// <summary>The row's track, for the featured credit; default for the skeleton.</summary>
    public Track Track { get; init; }

    /// <summary>The page artist's slot (never repeated under a row) and the first featured artist and the count the core found
    /// (<c>FeatCount</c> 0: no credit line).</summary>
    public int PageArtist { get; init; }

    /// <summary>The first featured artist's slot.</summary>
    public int FeatSlot { get; init; }

    /// <summary>How many featured artists the row has.</summary>
    public int FeatCount { get; init; }

    /// <summary>The # cell's play / pause (a null one is a skeleton or a withheld row).</summary>
    public Action? OnPlay { get; init; }

    /// <summary>The heart's like (null: a static, non-interactive heart).</summary>
    public Action? OnLike { get; init; }

    /// <summary>The like just flipped unsaved to saved (the heart's pop).</summary>
    public bool Pop { get; init; }

    /// <summary>The row's hover signal: the reveal source and the equalizer's pause gate (null for the skeleton).</summary>
    public Signal<bool>? Hovered { get; init; }

    /// <summary>The shelf slot root's scope: the slot owns click, focus and the keyboard, and the check lane rides in front of the
    /// cells (null for the skeleton, which is inert).</summary>
    public RowScope? Slot { get; init; }

    /// <summary>Are the check lanes shown (a multi-selection)?</summary>
    public Func<bool>? ChecksVisible { get; init; }

    /// <summary>The track-artwork preference: false builds no art cell at all and the mid column takes the width.</summary>
    public required bool ShowArtwork { get; init; }

    /// <summary>The row style (the Classic rows or the Modern ones).</summary>
    public required bool Classic { get; init; }
}

/// <summary>What the core decided for one realized cell: the row content and the wired behaviour around it.</summary>
internal sealed class ArtistChartCellArgs
{
    /// <summary>The keyed chart-row component.</summary>
    public required Element Content { get; init; }

    /// <summary>The cell's drag (a vertical lift wins the drag; a horizontal sweep yields to the shelf's pan).</summary>
    public required DragSource Draggable { get; init; }

    /// <summary>The single-selection pill's fill (the page's WATCHED accent: a grading landing re-tints it in place).</summary>
    public required Prop<ColorF> PillFill { get; init; }

    /// <summary>The pill's opacity: 1 while this row is the selection and the check lane is down, else 0 (an always-mounted BOUND opacity:
    /// selecting is compositor-only).</summary>
    public required Prop<float> PillOpacity { get; init; }

    /// <summary>The row style.</summary>
    public required bool Classic { get; init; }
}

/// <summary>What the core decided for the chart's skeleton: the real layout's counts.</summary>
internal sealed class ArtistChartSkeletonArgs
{
    /// <summary>Charted rows shown (the known count, else the overview's seed cap).</summary>
    public required int Total { get; init; }

    /// <summary>Columns (1 or 2).</summary>
    public required int Columns { get; init; }

    /// <summary>Rows per column.</summary>
    public required int PerColumn { get; init; }

    /// <summary>The row style.</summary>
    public required bool Classic { get; init; }

    /// <summary>The track-artwork preference.</summary>
    public required bool ShowArtwork { get; init; }
}

/// <summary>What the core decided for the pager: its page, the controlled pips' selection and the verbs.</summary>
internal sealed class ArtistChartPagerArgs
{
    /// <summary>The shelf's page.</summary>
    public required int Page { get; init; }

    /// <summary>How many pages.</summary>
    public required int PageCount { get; init; }

    /// <summary>Is there a previous page?</summary>
    public required bool CanPrev { get; init; }

    /// <summary>Is there a next page?</summary>
    public required bool CanNext { get; init; }

    /// <summary>Previous page.</summary>
    public required Action Prev { get; init; }

    /// <summary>Next page.</summary>
    public required Action Next { get; init; }

    /// <summary>Go to a page (the pips' change AND reselect: after a partial pan the strip rests between pages and the re-click puts it back).</summary>
    public required Action<int> GoTo { get; init; }

    /// <summary>The charted count (the bare count at one page).</summary>
    public required int Total { get; init; }

    /// <summary>The pips' controlled selection, mirrored from the shelf's page by the core's effect.</summary>
    public required Signal<int> Selected { get; init; }
}

/// <summary>The chart's shelf configuration for a row style.</summary>
internal delegate ArtistChartMetrics ArtistChartMetricsTemplate(bool classic);

/// <summary>Draws the chart's header (the accent rule and the "Top tracks" title).</summary>
internal delegate Element ArtistChartHeaderTemplate(string title, ColorF accent);

/// <summary>Draws one chart row, shared by live rows and the skeleton so the two cannot drift.</summary>
internal delegate Element ArtistChartRowTemplate(ArtistChartRowArgs args);

/// <summary>Draws one realized cell around a row: the accent pill, the drag source. The core attaches the context menu to what it returns.</summary>
internal delegate BoxEl ArtistChartCellTemplate(ArtistChartCellArgs args);

/// <summary>Draws the derived-skeleton source: the SAME row template over a representative shape.</summary>
internal delegate Element ArtistChartSkeletonTemplate(ArtistChartSkeletonArgs args);

/// <summary>Draws the failed chart's panel: the header over the Error vacancy and its Retry.</summary>
internal delegate Element ArtistChartFailedTemplate(string title, ColorF accent, Action retry);

/// <summary>Draws the pager (previous, pips and next at two or more pages, the bare count at one).</summary>
internal delegate Element ArtistChartPagerTemplate(ArtistChartPagerArgs args);

/// <summary>The structure's style for the artist chart (<c>StyleKeys.ArtistChart</c>).</summary>
public sealed class ArtistChartStyle
{
    internal ArtistChartStyle(ArtistChartMetricsTemplate metrics, ArtistChartHeaderTemplate header, ArtistChartRowTemplate row,
                              ArtistChartCellTemplate cell, ArtistChartSkeletonTemplate skeleton, ArtistChartFailedTemplate failed,
                              ArtistChartPagerTemplate pager)
    {
        Metrics = metrics;
        Header = header;
        Row = row;
        Cell = cell;
        Skeleton = skeleton;
        Failed = failed;
        Pager = pager;
    }

    /// <summary>The shelf configuration.</summary>
    internal ArtistChartMetricsTemplate Metrics { get; }

    /// <summary>The header.</summary>
    internal ArtistChartHeaderTemplate Header { get; }

    /// <summary>One row.</summary>
    internal ArtistChartRowTemplate Row { get; }

    /// <summary>One realized cell.</summary>
    internal ArtistChartCellTemplate Cell { get; }

    /// <summary>The skeleton source.</summary>
    internal ArtistChartSkeletonTemplate Skeleton { get; }

    /// <summary>The failed panel.</summary>
    internal ArtistChartFailedTemplate Failed { get; }

    /// <summary>The pager.</summary>
    internal ArtistChartPagerTemplate Pager { get; }
}
