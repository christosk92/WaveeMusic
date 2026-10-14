// ── Theming/Contracts/EagerRowStyle.cs — what a structure supplies for the EAGER track row (themes plan 6.1, T8c) ───────────────
//
// The eager row is the short preview list's row (Home's top tracks, the library reader's blocks, Recents, the album drawer's rows):
// ONE one-shot grid of the same lanes the virtualized table draws, inside a hover container that is the interactive ancestor. The
// structure registers an `EagerRowStyle` under `StyleKeys.EagerTrackRow` and the core host (`Track.EagerRow`'s component) builds the
// row from it while it renders, so its subscriptions, keys and effect timing are exactly what they were.
//
// WHAT THE CORE KEEPS. Which rows exist, the state a cell reflects (`Track.StateOf`, the like edge), the hover signal, the drag
// payload, the click (`Track.Invoke`), the context menu and the menu's items, the stamp memo that re-renders a row only when ITS
// track moved. The templates receive the facts and the wired behaviour and only draw: they never name `Playback`, `Entities`,
// `Store` or `Prefs` (WVTH002).
//
// A host mounted where no registry provides the key draws nothing (logged once, `ListStyleLookup`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>Draws the eager grid of one row: the lanes of <paramref name="set"/> over the shared <paramref name="tracks"/>, every
/// cell keyed. Called from the host's render and from the table's counted shimmer, which draws the SAME row (a structure's discography
/// style may draw its drawer rows with it too: Classic's does).</summary>
internal delegate Element EagerRowGridTemplate(Track t, int displayIndex, in Track.RowState st, in Track.ColumnSet set,
                                               TrackSize[] tracks, float rowH, Element title, in Track.GridOptions o);

/// <summary>Draws the title run of an eager row (the plain ellipsis run with the full-text tooltip while it is cut).</summary>
internal delegate Element EagerRowTitleTemplate(Track t, bool isNow);

/// <summary>Draws the eager row's container (fills, border, corners, the hover overlay "…") around the grid and applies every
/// wired member of <see cref="EagerRowSkin"/>.</summary>
internal delegate BoxEl EagerRowSkinTemplate(EagerRowSkin skin);

/// <summary>The structure's style for the eager track row (<c>StyleKeys.EagerTrackRow</c>).</summary>
public sealed class EagerRowStyle
{
    internal EagerRowStyle(EagerRowGridTemplate grid, EagerRowTitleTemplate title, EagerRowSkinTemplate skin)
    {
        Grid = grid;
        Title = title;
        Skin = skin;
    }

    /// <summary>The eager grid.</summary>
    internal EagerRowGridTemplate Grid { get; }

    /// <summary>The title run.</summary>
    internal EagerRowTitleTemplate Title { get; }

    /// <summary>The row's container.</summary>
    internal EagerRowSkinTemplate Skin { get; }
}

/// <summary>What the core hands the skin template for ONE render of an eager row. A template applies ALL the wired members to the
/// box it builds (dropping one loses a behaviour: the drag, the click, the hover edges that drive the reveal and pause the
/// equalizer) and keeps the Button role; the core attaches the context menu around what it returns.</summary>
internal sealed class EagerRowSkin
{
    /// <summary>The grid (<see cref="EagerRowStyle.Grid"/>'s result).</summary>
    public required Element Grid { get; init; }

    /// <summary>The row style: the Classic row (square, no inset) or the Modern one.</summary>
    public required bool Classic { get; init; }

    /// <summary>The row sits at an odd display position of a zebra list.</summary>
    public required bool OddZebra { get; init; }

    /// <summary>The row's height.</summary>
    public required float RowHeight { get; init; }

    /// <summary>The column set has no "…" lane (no Actions, no Video lane, no caller cell), so the standard "…" rides the row as a hover
    /// overlay at its end over an opaque chip and reserves no width.</summary>
    public required bool OverlayMore { get; init; }

    /// <summary>The row's drag (selection-less: the row's own track).</summary>
    public required DragSource Draggable { get; init; }

    /// <summary>The row's click: the single-click play (the now-playing row toggles pause).</summary>
    public required Action OnClick { get; init; }

    /// <summary>Row hover: the PointerBit every <c>HoverOpacity</c> descendant inherits AND the equalizer's pause gate.</summary>
    public required Action<Point2> OnHoverMove { get; init; }

    /// <summary>The pointer left the row.</summary>
    public required Action OnPointerExit { get; init; }
}
