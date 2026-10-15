// ── Theming/Contracts/TrackChromeStyle.cs — what a structure supplies for the track table's chrome (themes plan 6.1, T8b) ─────
//
// T8a gave `StyleKeys.TrackTable` its ROW half (`TrackRowTemplates`). T8b is the CHROME half: the column header, the command bar's
// buttons and surface, the filter card, the selection bar's surface and the placeholder row. They ride the same `TrackListStyle`
// (`TrackListStyle.Chrome`); no new key.
//
// WHAT THE CORE KEEPS. `Track.TableHost` and its chrome components decide everything that is not a picture: the sort / filter /
// selection STATE and the decisions on it (`TableRules.NextSort`, `HeaderActive`, the command bar's fit and eviction, the hold on the
// bar while the filter card is open, which facets the list can earn, the counts, the selection lane's variants), every overlay's
// open / close and focus, the measured widths, the "…" menu's items. A template receives the facts and the verbs as args and draws:
// it never names `Playback`, `Prefs`, `Store` or `Shell` (WVTH002). The chrome components keep their hooks; the templates are plain
// functions they call during their own render, so subscriptions, keys and effect timing are exactly what they were.
//
// NOT HERE. The search disclosure's field (it is a focus and caret machine: `TableSearchHost`, `TableSearchField`) stays in
// `Entities\Track.Table.Chrome.cs`; it calls the kit below for its glyph button. `Controls.SelectionBar` (the shared bar several pages mount) and the shared selection lane's commands are the
// shared control's. The host-free header shim the library's album pane shimmers under is that pane's own picture (`ClassicLibraryAlbumPane`), and the placeholder row it and the
// artists' reader draw is `ClassicTrackChrome.Placeholder` (T10c1 moved `Track.TableHeaderShim` and `Track.ShimmerRow` with their callers).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

// ── the column header ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Draws the column header over the SAME <see cref="Track.ColumnSet"/> and tracks the rows use: keyed cells, the sort
/// carets, the check lane's shift and the band's hairline. Called from the core header component's render.</summary>
internal delegate Element TrackHeaderTemplate(TrackHeaderArgs args);

/// <summary>What one render of the core header decided.</summary>
internal sealed class TrackHeaderArgs
{
    /// <summary>The lanes (and so which header cells exist); <c>Set.Classic</c> is the row style.</summary>
    public required Track.ColumnSet Set { get; init; }

    /// <summary>The width tracks the rows share.</summary>
    public required TrackSize[] Tracks { get; init; }

    /// <summary>The sort this render saw.</summary>
    public required Track.SortSpec Sort { get; init; }

    /// <summary>The live sort, for the pieces that are components of their own (the caret's spring, the title label's word swap).</summary>
    public required IReadSignal<Track.SortSpec> SortSignal { get; init; }

    /// <summary>The select check lane is in (the header shifts over by its width).</summary>
    public required bool Checks { get; init; }

    /// <summary>The hero arm (the Title header reads "Song", the duration lane wears the word).</summary>
    public required bool Vertical { get; init; }

    /// <summary>A header was clicked: the core applies <c>TableRules.NextSort</c> to the live sort. The flag is "the artist column's own cell".</summary>
    public required Action<Track.SortColumn, bool> OnSort { get; init; }
}

// ── the command bar ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The command bar's pictures. The core decides which commands exist and hands each one's behaviour; the kit draws them.</summary>
internal interface ITrackCommandKit
{
    /// <summary>The chromeless lane both modes (browse and selection) share; the keyed mode box is what animates between them.</summary>
    Element Surface(string mode, Element content);

    /// <summary>One measured command slot: the keyed box that enters and exits on the bar's motion and reports its width.</summary>
    Element Command(string key, Element command, Action<float> onWidth);

    /// <summary>The list bar (tuned Classic D3): the <paramref name="left"/> group (Play next, Shuffle, Tune), a flexible gap, then the
    /// right group: the <paramref name="view"/> commands (Filter, Sort), the search host (Find, led by
    /// <c>CommandBarLayout.SearchGap</c>) and the <paramref name="tail"/> (Insights, "…"). A <paramref name="middle"/> (the page bar's genre
    /// chips, tuned Classic D8) takes the flexible gap's place: it grows, never under <c>CommandBarLayout.InlineChipsMin</c>, and ends
    /// <c>CommandBarLayout.InlineChipsTrail</c> before the right group.</summary>
    Element Bar(Element[] left, Element[] view, Element search, Element[] tail, Element? middle = null);

    /// <summary>The page bar's lead (a page with no rail, tuned Classic D16): the page's Play split for <paramref name="subject"/> in the
    /// <paramref name="accent"/> ("More ways to play" on its chevron), the labelled <paramref name="shuffleLabel"/> button and the divider
    /// before the chips.</summary>
    Element PageLead(EntityUri subject, Func<ColorF> accent, Action play, string shuffleLabel, Action shuffle);

    /// <summary>The split "Play next" command (primary action + flyout of <paramref name="items"/>) wearing CommandBar visuals.</summary>
    Element PlayNext(string label, Action onPlay, IReadOnlyList<MenuFlyoutItem> items);

    /// <summary>The labelled (icon + text) command, optionally with a trailing element (a chevron, a caret).</summary>
    Element Labeled(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized, Element? trailing = null);

    /// <summary>The 32 x 32 glyph button: active takes the accent plate, idle is ghost.</summary>
    Element IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized);

    /// <summary>The disclosure chevron after a command that opens a flyout (the Sort button's one chevron, tuned Classic D6).</summary>
    Element Chevron();

    /// <summary>The Filter command: the funnel + <paramref name="label"/> or the funnel alone, with the count badge while filters are on.
    /// The button's slot never changes size with the count, so a filter turning on moves nothing.</summary>
    Element FilterCommand(int count, bool labelled, string label, Action onClick, Action<NodeHandle> onRealized);

    /// <summary>The funnel button of the search host (32 x 32, accent plate and corner badge while any facet is on).</summary>
    Element FilterFunnel(int count, Action onClick, Action<NodeHandle> onRealized);

    /// <summary>The "…" button's fixed 32-DIP slot: <paramref name="more"/> with the filter count as a corner badge when one rides here.</summary>
    Element MoreSlot(Element more, int filterCount);

    // The row-size control (M3): the structure owns the density option (the style's), so the "…" menu's "Row size" rows come from here (tuned
    // Classic D3 keeps Row size in "…" only); the core owns where the submenu sits.

    /// <summary>The "Row size" submenu's radio rows (the overflow menu): one per rung, the current one checked. Read when the menu opens.</summary>
    IReadOnlyList<MenuFlyoutItem> DensityItems();
}

// ── the filter card ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One facet row of the filter card: its word and a combo on the core's signal. <see cref="On"/> is the core's decision that the
/// row is off its default (it tints the combo and turns the word primary).</summary>
internal readonly record struct TrackFilterFacet(string Key, string Label, Signal<int> Value, string[] Options, Action<int> Changed,
                                                 string Placeholder, bool On);

/// <summary>One status switch row of the filter card.</summary>
internal readonly record struct TrackFilterToggle(string Key, string Label, Signal<bool> Value, Action<bool> Changed, bool On);

/// <summary>What the core decided for one render of the filter card.</summary>
internal sealed class TrackFilterCardArgs
{
    /// <summary>The card's title ("Filter").</summary>
    public required string Title { get; init; }

    /// <summary>What the list shows against what it has (the filtered counts), already worded.</summary>
    public required string Status { get; init; }

    /// <summary>The "Reset" word.</summary>
    public required string ResetLabel { get; init; }

    /// <summary>Is there anything to reset?</summary>
    public required bool CanReset { get; init; }

    /// <summary>Clear every facet.</summary>
    public required Action Reset { get; init; }

    /// <summary>The facet rows this list can earn, in order.</summary>
    public required IReadOnlyList<TrackFilterFacet> Facets { get; init; }

    /// <summary>The status switches this list can earn, in order (may be empty).</summary>
    public required IReadOnlyList<TrackFilterToggle> Toggles { get; init; }
}

/// <summary>Draws the immediate-apply filter card from what the core decided.</summary>
internal delegate Element TrackFilterCardTemplate(TrackFilterCardArgs args);

// ── the placeholder ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Draws the blank row a slot shows before its reveal edge: the SAME tracks, gap, height and padding as the live row
/// (the core passes them), so the crossing is a cross-fade and never a reflow.</summary>
internal delegate Element TrackPlaceholderTemplate(in Track.ColumnSet set, TrackSize[] tracks, float rowH, float padX);

// ── the date group head ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Draws the head of a date group above the row that starts it (tuned Classic D16): the group's name and its count, at the rows'
/// own gutter <paramref name="padX"/>. BOUND: <paramref name="head"/> is the slot's live head (null on a row inside a group: it draws nothing
/// and takes no room); read it only inside bound props, so a recycle never re-renders the slot.</summary>
internal delegate Element TrackGroupHeaderTemplate(Func<Track.DateGroupHead?> head, float padX);

// ── the record ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The structure's chrome for the track table (<see cref="TrackListStyle.Chrome"/>).</summary>
public sealed class TrackChromeTemplates
{
    internal TrackChromeTemplates(TrackHeaderTemplate header, ITrackCommandKit commands, TrackFilterCardTemplate filterCard,
                                  TrackPlaceholderTemplate placeholder, TrackGroupHeaderTemplate groupHeader)
    {
        Header = header;
        Commands = commands;
        FilterCard = filterCard;
        Placeholder = placeholder;
        GroupHeader = groupHeader;
    }

    /// <summary>The column header.</summary>
    internal TrackHeaderTemplate Header { get; }

    /// <summary>The command bar's buttons and surface (the selection bar rides the same surface).</summary>
    internal ITrackCommandKit Commands { get; }

    /// <summary>The filter card.</summary>
    internal TrackFilterCardTemplate FilterCard { get; }

    /// <summary>The placeholder row.</summary>
    internal TrackPlaceholderTemplate Placeholder { get; }

    /// <summary>The head of a date group (tuned Classic D16).</summary>
    internal TrackGroupHeaderTemplate GroupHeader { get; }
}
