// ── Theming/Contracts/FrameBitsProps.cs — the props of the small frame views (themes plan 6.0, T4c) ──────────────────────
//
// The history menu, the file-drop cue, the drag chip layer, the setup wizard's cover scrim and the frame-rate overlay. Each is
// a picture over a decision the core keeps: which entries the back/forward stack lists, whether a drag is over the window,
// what the dragged payload is called, whether the wizard dims the shell, whether developer mode asked for the HUD.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellHistoryMenu"/>: the right-click (or touch-hold) menu of the Back and Forward buttons.
/// The core decides the rows (how many, newest first, the "View all history" tail); the theme draws the menu. Built per open.</summary>
public sealed record HistoryMenuProps
{
    /// <summary>The rows, in order: one per history entry, then (when the stack is longer) a separator and "View all history".</summary>
    public required IReadOnlyList<MenuFlyoutItem> Items { get; init; }

    /// <summary>Close the menu (an invoked row closes it).</summary>
    public required Action Close { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ShellFileDrop"/>: the drop panel the shell shows while an OS drag (files, folders, a
/// playlist file, Spotify links) is over the window's empty space (#240 §3.15, UX S12): two to four large zones that fit what is
/// dragged, each with a glyph and a one-line consequence. The core decides the zones and runs what a drop on one does; the theme
/// draws the panel and hangs <see cref="PanelTarget"/> and <see cref="ZoneTarget"/> on its card and zones.</summary>
public sealed record FileDropProps
{
    /// <summary>Is the panel showing (the pointer is over the window's ground, the card or a zone, with a payload Wavee can use)?
    /// Bound by the template (a drag hover never re-renders the shell).</summary>
    public required IReadSignal<bool> Over { get; init; }

    /// <summary>The live drag's zones, in order (a new array once per drag: the template re-renders on that edge only). Null: a host
    /// with no drop panel (the cue alone).</summary>
    public IReadSignal<DropZoneLook[]>? Zones { get; init; }

    /// <summary>The zone under the pointer (-1: none). Bound: the hovered zone lights without a re-render.</summary>
    public IReadSignal<int>? Hot { get; init; }

    /// <summary>The content region's centre relative to the window's: the panel centres on the content, not the window (bound).</summary>
    public IReadSignal<Point2>? CentreOffset { get; init; }

    /// <summary>The panel card's drop target: a drop between the zones, and the spotlight's cut-out (the scrim never dims the panel).</summary>
    public DropTargetSpec? PanelTarget { get; init; }

    /// <summary>Zone i's drop target: it lights while the pointer is over it and runs that zone's verb on a drop.</summary>
    public Func<int, DropTargetSpec>? ZoneTarget { get; init; }
}

/// <summary>One drop-panel zone: its glyph (in <paramref name="GlyphFont"/>, null for the icon font), its verb ("Play now") and its
/// one-line consequence ("Replaces what's playing", "12 songs"), a thunk the template binds (a count lands when the folder listing
/// does).</summary>
public sealed record DropZoneLook(string Glyph, string? GlyphFont, string Title, Func<string> Detail);

/// <summary>The props of <see cref="ViewKeys.ShellDragChip"/>: the layer that draws the chip that follows the pointer during a drag.</summary>
public sealed record DragChipProps
{
    /// <summary>Resolves the chip for the drag in flight (null: no chip). The core decides what the dragged payload is called and
    /// shows (a track, a playlist, a block of rows); the theme decides how the layer is drawn.</summary>
    public required Func<DragState, FluentGpu.Dsl.Element?> Preview { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ShellCoverScrim"/>: the setup wizard's dim over the shell.</summary>
public sealed record CoverScrimProps
{
    /// <summary>Does the wizard dim the shell right now? Tracked by the template (it re-renders on the edge).</summary>
    public required Func<bool> Dim { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ShellFps"/>: the frame-rate overlay.</summary>
public sealed record FpsProps
{
    /// <summary>Is the overlay on? The core's rule: developer mode AND the diagnostics toggle, never in presentation mode. Bound by the
    /// template; the HUD is mounted only while it holds.</summary>
    public required Func<bool> Shown { get; init; }

    /// <summary>The live driver's drawn pointer lane (Diagnostics.Drive.Cursor.cs), laid over the HUD in the same top overlay slot;
    /// null draws none. Mounted by the core, empty unless a `wavee://diag?cmd=drive` script runs.</summary>
    public Func<FluentGpu.Dsl.Element>? DriveCursor { get; init; }
}
