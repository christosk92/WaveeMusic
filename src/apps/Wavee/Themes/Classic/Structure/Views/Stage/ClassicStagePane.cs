// ── Themes/Classic/Structure/Views/Stage/ClassicStagePane.cs — Classic's pane region of the fullscreen stage (themes plan 6.1, P3) ──────
//
// From `Shell\Stage.UI.cs` (`PaneHost` and its `PaneFrame`): the element tree the core pane host built inline. The region sits TOP-LEFT at the
// allocator's pane rect (`Stage.Layout.PaneRect` for the look the core decided), clips, slides to a new rect with the cover (Lyrics ⇄ Large art)
// and shows only while the layout has a pane and one is chosen. Each pane's body enters in the frame below: it rises 40 DIP and fades in over
// 500 ms (FluentDecelerate) and fades out in place. WHICH pane is shown and the KeepAlive that keeps each one's state (`pane:<kind>`: Lyrics mode
// and Large art share "pane:1") are the core's: the template hands `props.Panes` its frame and the empty arm and places what comes back.
// Elements, keys and motion are exactly what the host built before the move.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's stage pane region (<see cref="ViewKeys.StagePane"/>).</summary>
internal static class ClassicStagePane
{
    // Bounds, clip-revealed: the region slides to its new origin and its width follows (the Huge 4K lyrics narrowing beside the Up next column)
    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds,
        TransitionDynamics.Spring(ClassicStageGeometry.PaneMoveResponse, ClassicStageGeometry.PaneMoveDamping), SizeMode.Reveal);

    static readonly Func<Element, Element> s_frame = Frame;
    static readonly Func<Element> s_empty = static () => new BoxEl { HitTestVisible = false };

    /// <summary>The registered template of <see cref="ViewKeys.StagePane"/>.</summary>
    public static Element Template(StagePaneProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        var pane = L.PaneRect(in k);
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = pane.W, Height = pane.H, ClipToBounds = true,
            Margin = new Edges4(pane.X, pane.Y, 0f, 0f), Layout = s_move,   // the pane slides to its new rect with the cover (Lyrics ⇄ Large art), its width with it
            Visible = Prop.Of(() => sc.Layout.Value.ShowPane && sc.Pane.Value != Stage.PaneKind.None),
            Children = [props.Panes(s_frame, s_empty)],
        };
    }

    /// <summary>The frame a pane's body enters in: it rises and fades in, and fades out in place.</summary>
    static Element Frame(Element body) => new BoxEl
    {
        Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
        Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(ClassicStageGeometry.PaneFadeMs, Easing.FluentDecelerate),
            Enter: new EnterExit(Dy: ClassicStageGeometry.PaneEnterDy, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
        Children = [body],
    };
}
