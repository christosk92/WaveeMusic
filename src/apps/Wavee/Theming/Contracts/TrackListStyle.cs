// ── Theming/Contracts/TrackListStyle.cs — what a structure supplies for the track table's rows (themes plan 6.1, T8a) ─────────
//
// A theme cannot hand back a finished `Element` for a virtualized list: recycling, selection, drag, keyboard and scroll anchoring
// are core behaviour (`Track.TableHost`). So the list is a STYLE: the structure registers a `TrackListStyle` under
// `StyleKeys.TrackTable` and the core host builds the list from it. T8a is the ROW half of that record (the bound grid of lanes and
// the row's skin, in both row styles); T8b adds the header, the command / list bar, the filter card, the selection bar and the
// placeholder, T8c the eager and episode rows, the surface, the artist chart and the drawer.
//
// WHAT THE CORE KEEPS. Which rows exist, their kinds, the presentation memo (`RowPresentation`), the like edge, the reveal ramp
// and the shimmer, the drawer, the selection and drag wiring, the keyboard and the context menu are all core: a template receives
// them as `BoundRow` (the slot's bound inputs) and `TrackRowSkin` (the wired behaviour) and only draws. A template is called once
// per slot per shape, never per bind, exactly as the bound grid always was: a recycle rebinds, it allocates nothing.
//
// THE ROW STYLE TODAY. Modern or Classic is still the core's `ColumnSet.Classic` (the `detail.rowstyle` setting, read into the
// table's snapshot); the core picks `Modern` or `Classic` below by it. M2 deletes that flag and turns the pick into the
// structure's own option (the stamp then re-keys the list). Both templates are the structure's, so a second theme supplies
// its own pair and draws another row over the same table.
//
// NOT YET `BoundRowTemplate<RowPresentation>`. The engine's typed bound-row channel (EP6: `ItemsView.CreateBound` over
// `BoundListProps`, one stamp-driven slot rebuild) wants the WHOLE slot as a plain tree with no component in it. The table's
// slot today is two components (`TableSlot`: the track / episode switch and the open drawer; `TableRowContent`: the reveal gate,
// the presentation memo, the like-edge latch), and replacing them would add binds and `Flow` boundaries to every row, which this
// move must not do. The templates below are the pieces those components call; `Stamp` is carried for the PR that folds the slot.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>Draws the bound grid of one row: the lanes (# · heart · art · title · artist · album · added by · date · plays · tempo ·
/// duration · video · more · expand) with every per-item value bound to <paramref name="row"/>. Called once per slot per shape.</summary>
internal delegate Element TrackRowGridTemplate(Track.BoundRow row, in Track.ColumnSet set, TrackSize[] tracks, float rowH, float art);

/// <summary>Draws the row's container (fills, border, corners, pill, divider and their motion) around the wired behaviour of
/// <see cref="TrackRowSkin"/>. Called once per slot render; returns the box the core attaches the context menu to.</summary>
internal delegate BoxEl TrackRowSkinTemplate(TrackRowSkin skin);

/// <summary>One row style's templates: the bound grid and the skin. The delegates speak in the table's own bound-row types, so they
/// are internal; the object is public so a test can hold the registered style.</summary>
public sealed class TrackRowTemplates
{
    internal TrackRowTemplates(TrackRowGridTemplate grid, TrackRowSkinTemplate skin)
    {
        Grid = grid;
        Skin = skin;
    }

    /// <summary>Draws the bound grid of one row.</summary>
    internal TrackRowGridTemplate Grid { get; }

    /// <summary>Draws the row's container around the wired behaviour.</summary>
    internal TrackRowSkinTemplate Skin { get; }
}

/// <summary>The structure's style for the track table's rows (<c>StyleKeys.TrackTable</c>).</summary>
public sealed class TrackListStyle
{
    internal TrackListStyle(int stamp, TrackRowTemplates modern, TrackRowTemplates classic, TrackChromeTemplates chrome)
    {
        Stamp = stamp;
        Modern = modern;
        Classic = classic;
        Chrome = chrome;
    }

    /// <summary>The table's chrome (T8b): the column header, the command bar's buttons and surface, the filter card, the selection
    /// bar's surface and the placeholder row. One set for both row styles; the header reads the row style from the lanes it draws.</summary>
    public TrackChromeTemplates Chrome { get; }

    /// <summary>A structure-computed value that changes whenever either template would build a different row (a theme option it
    /// read). Carried for the stamp-driven slot rebuild of the typed bound-row channel; the table re-keys its list on the
    /// row-style setting itself until then.</summary>
    public int Stamp { get; }

    /// <summary>The Modern row style (the default).</summary>
    public TrackRowTemplates Modern { get; }

    /// <summary>The Classic row style (the owner's 0.2.9 look).</summary>
    public TrackRowTemplates Classic { get; }
}

/// <summary>What the core hands a skin template for ONE slot render: the content to wrap, the lanes the core wired, and the
/// live facts the fills read. A template applies ALL of the wiring below to the box it builds (a template that drops one loses a
/// behaviour: drag, tap, keys, focus, the equalizer's hover pause) and keeps the box <c>Focusable = false</c> (the list owns the
/// one roving tab stop) with the Button role. The delegates are built once per skin render; the template never creates a closure
/// per bind beyond the <c>Prop.Of</c> thunks its fills need.</summary>
internal sealed class TrackRowSkin
{
    // ── what to wrap ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The row's content (the grid, or the shimmer before the reveal), placed after the check lane.</summary>
    public required Element Content { get; init; }

    /// <summary>The selection check lane (<c>SelectorVisualsBound.BoundCheckLane</c>), wired to the selection model.</summary>
    public required Element CheckLane { get; init; }

    // ── the geometry the core decided ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The row's height (density already applied).</summary>
    public required float RowHeight { get; init; }

    /// <summary>The table's horizontal padding at the current tier (<c>RowMetrics.PadXFor</c>).</summary>
    public required float PadX { get; init; }

    /// <summary>The hero system's STACKED flow: plain full-bleed rows (Modern only; never true for Classic).</summary>
    public required bool Plain { get; init; }

    /// <summary>The bottom corners are square: THIS row's drawer is open, or still closing under it.</summary>
    public required bool Squared { get; init; }

    /// <summary>The slot's first render fades in (a mount entrance, not a recycle).</summary>
    public required bool Entrance { get; init; }

    // ── the facts the fills read (live: call them inside a bound thunk) ──────────────────────────────────────────────

    /// <summary>Is this slot's row selected?</summary>
    public required Func<bool> IsSelected { get; init; }

    /// <summary>Is an .mp4 being dragged over this row (the drop cue rides the fill)?</summary>
    public required Func<bool> Cue { get; init; }

    /// <summary>Is the row at an odd DISPLAY position (zebra parity)?</summary>
    public required Func<bool> Odd { get; init; }

    /// <summary>Are the check lanes shown (a multi-selection or the select mode)?</summary>
    public required Func<bool> ChecksVisible { get; init; }

    /// <summary>The page accent (the pill's fill).</summary>
    public required Func<ColorF> Accent { get; init; }

    // ── the behaviour the core wired (apply every one) ───────────────────────────────────────────────────────────────

    /// <summary>The .mp4 drop target, or null while no video override exists.</summary>
    public DropTargetSpec? DropTarget { get; init; }

    /// <summary>The row's drag source (the payload resolves the slot's current index at drag start).</summary>
    public required DragSource Draggable { get; init; }

    /// <summary>Tap and double-tap.</summary>
    public required Action<PointerEventArgs> OnPointerReleased { get; init; }

    /// <summary>Enter, Space and the Alt+arrow block move.</summary>
    public required Action<KeyEventArgs> OnKeyDown { get; init; }

    /// <summary>The slot's focus edge (keyboard-current tracking).</summary>
    public required Action<bool> OnFocusChanged { get; init; }

    /// <summary>Row hover, which pauses the equalizer under its hover reveal.</summary>
    public required Action<Point2> OnHoverMove { get; init; }

    /// <summary>The pointer left the row.</summary>
    public required Action OnPointerExit { get; init; }
}
