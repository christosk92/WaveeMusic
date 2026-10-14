// ── Theming/Contracts/NavDropLook.cs — how the navigation pane presents a drag (themes plan 6.1, T5e) ─────────────────────
//
// THE DROP FACTS ARE CORE'S, THE PICTURES ARE THE STRUCTURE'S. What may drop where, the insert index, payload acceptance, the
// refusal sentences, the auto-expand and drag-peek dwell timers and the published slot (`Shell\NavDropRules*`, `Sidebar.UI.Drop.cs`)
// stay in the core pane. What the structure draws is the cues: the insertion caret, the "into" plate, the Pinned band's dashed
// ring and the folder flyout row's plate (`ClassicNavRow`, `ClassicNavFlyouts`; their numbers are `SidebarGeometry`'s), and the
// three presentation choices of the engine's drag machinery below, which the pane's drop specs and its reorder bands read.

using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>The presentation choices a structure makes for a drag over the navigation pane (read through
/// <see cref="NavRowKit.DropLook"/>). One instance per structure, stateless.</summary>
public sealed record NavDropLook
{
    /// <summary>How every drop target of the pane (rows, the header and folder "+" plates, the rail's folder tiles) joins the
    /// engine's drag-dim: the pane's own targets stay lit while the rest of the app dims.</summary>
    public required DropTargetVisualPolicy TargetPolicy { get; init; }

    /// <summary>The lifted source's look while a row of an in-place band is reordered.</summary>
    public required DragVisualStyle ReorderDragStyle { get; init; }

    /// <summary>Does the engine's own insertion line draw over a reorderable band? (The pane draws its own caret per row.)</summary>
    public required bool ReorderInsertionLine { get; init; }
}
