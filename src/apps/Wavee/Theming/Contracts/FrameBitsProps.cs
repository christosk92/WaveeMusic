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

/// <summary>The props of <see cref="ViewKeys.ShellFileDrop"/>: the cue the shell shows while a file is dragged over the window.</summary>
public sealed record FileDropProps
{
    /// <summary>Is a file drag over the window? Bound by the template (a drag hover never re-renders the shell).</summary>
    public required IReadSignal<bool> Over { get; init; }
}

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
}
