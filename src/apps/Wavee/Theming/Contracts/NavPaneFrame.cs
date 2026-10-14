// ── Theming/Contracts/NavPaneFrame.cs — what the pane's frame is drawn from (themes plan 6.1, leftovers P3) ──────────────────
//
// The core pane (`Sidebar.PaneView`, a [CoreHost]) decides everything about its frame: what the layer holds (the mode head and the
// padded list, the empty body, or the Edit outline), whether the docked rail is inert behind a pinned overlay, the drag peek's
// spring-load waypoint while presented compact, the pane's key handler, the drag watcher it mounts and the pane menu (built at open
// time from `NavMenuModel`). The structure draws the boxes around them through `NavRowKit.Frame`: the layer measured at the open
// width, the root, and the context-menu shell with its childless shield (Classic: `ClassicNavFrame`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>One render of the pane's frame: the values and handlers the core decided (see the file header).</summary>
public readonly record struct NavPaneFrameArgs
{
    /// <summary>The pane's OPEN width (the shared width preference), bound for the pane's life: the layer is measured at it even while
    /// presented compact, so text never reflows through the rail width.</summary>
    public required Prop<float> Width { get; init; }

    /// <summary>False while the docked rail is inert behind a pinned overlay; the drawer IS that overlay and stays live.</summary>
    public required bool HitTestVisible { get; init; }

    /// <summary>The drag peek's spring-load waypoint over the whole rail while presented compact (never a destination), else null.</summary>
    public DropTargetSpec? DropTarget { get; init; }

    /// <summary>The layer's children, top to bottom (the mode head and the body, or the Edit outline).</summary>
    public required Element[] Layer { get; init; }

    /// <summary>The zero-size drag watcher the docked pane mounts beside the layer (null in the drawer).</summary>
    public Element? DragWatcher { get; init; }

    /// <summary>The pane's key handler (Ctrl+F, the edit chords...).</summary>
    public required Action<KeyEventArgs> OnKeyDown { get; init; }

    /// <summary>The overlay the pane menu opens in; null when there is none (a headless mount): then there is no menu shell.</summary>
    public IOverlayService? MenuOverlay { get; init; }

    /// <summary>The pane menu, built at open time (one cached delegate).</summary>
    public required Func<ContextMenuModel?> Menu { get; init; }
}
