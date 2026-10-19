// ── Themes/Classic/Structure/Views/Nav/WinUiNavFrame.cs — the boxes Classic builds the navigation pane in (themes plan 6.1, P3) ──
//
// Moved from the core pane (`Sidebar.PaneHost` and `Sidebar.PaneView`, now [CoreHost]) with `NavRowKit.ModeHost`, `Frame` and `ListBox`:
// the layout host's box and its mode-switch fade, the ONE layer (the rail is the SAME list planned compact, clipped to the frame's
// presented column), the root, the context-menu shell with its childless shield, and the list's inset box. What each box holds and
// every flag on it (the open width, the inert-behind-a-pinned-overlay rule, the drag peek's waypoint, the key handler, the menu) is
// the core's decision and arrives on `NavPaneFrameArgs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Classic's pane boxes (see the file header); <see cref="WinUiNavRows"/> hands them to the core.</summary>
public static class WinUiNavFrame
{
    /// <summary>The context-menu SHIELD's key: the node MUST stay childless (see <see cref="Frame"/>).</summary>
    public const string ContextShieldKey = "sidebar:context-shield";

    /// <summary>The layout host's box. A quick fade only on a layout switch: the shell's width transition owns spatial motion.</summary>
    public static Element ModeHost(Element mode) => new BoxEl
    {
        Grow = 1f, Direction = 1,
        Children = [mode with { Enter = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.ControlFast }],
    };

    /// <summary>The pane's frame.</summary>
    public static Element Frame(in NavPaneFrameArgs args)
    {
        // ONE layer: the rail is the SAME list planned compact (design V.9), clipped to the frame's presented column. The
        // layer is measured at the OPEN width even while presented compact, so text never reflows through the rail width.
        var layer = new BoxEl
        {
            Key = "pane-layer", Direction = 1, Grow = 1f, Shrink = 0f, ClipToBounds = true,
            Width = args.Width,
            // The docked rail is inert behind a pinned overlay (design C.3); the drawer IS that overlay, so it stays live.
            HitTestVisible = args.HitTestVisible,
            // DRAG PEEK: a spring-load waypoint over the whole rail (never a destination).
            DropTarget = args.DropTarget,
            Children = args.Layer,
        };

        var root = new BoxEl
        {
            // No fill, no corners: the sidebar is flush frame chrome over Mica (ch 25 §4).
            Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
            OnKeyDown = args.OnKeyDown,
            Children = args.DragWatcher is { } watcher ? [layer, watcher] : [layer],
        };

        // THE CONTEXT-MENU SHIELD. A context flyout makes an element a hit target (ContextBit), so hanging the menu
        // off the root made every dead spot resolve the WHOLE sidebar as the press/hover owner. The menu goes on a
        // ZStack SHELL plus a CHILDLESS full-bleed shield beneath the content: content wins wherever it hits, the
        // shield takes the rest, and a cascade from a childless node reaches nothing.
        if (args.MenuOverlay is not { } svc) return root;
        var menu = args.Menu;
        return new BoxEl
        {
            Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
            Children =
            [
                new BoxEl { Key = ContextShieldKey }.WithContextMenu(svc, menu),
                root with { Grow = 0f, Shrink = 0f },
            ],
        }.WithContextMenu(svc, menu);
    }

    /// <summary>THE PANE'S ONE INSET (<see cref="WinUiNavGeometry.ListInset"/>, 4,3,4,0) around the virtualized list, and nowhere else.</summary>
    public static Element ListBox(Element list) => new BoxEl
    {
        Key = "plan-pad", Direction = 1, Grow = 1f, Padding = WinUiNavGeometry.ListInset,
        Children = [list],
    };
}
