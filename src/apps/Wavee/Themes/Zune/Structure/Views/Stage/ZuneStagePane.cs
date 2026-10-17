// ── Themes/Zune/Structure/Views/Stage/ZuneStagePane.cs — Zune's pane region of the fullscreen stage (P5 Z4b) ────────────────────────
//
// The region the core pane host fills (the lyrics pane, Up next, the Artist pane: Z4c's views): TOP-LEFT at the allocator's pane rect, or in
// the Large art column under "playing from …" (`ZuneStageGeometry.Arrange`: Large art keeps its lyrics). It clips, slides to a new rect on
// the stage's spring and shows only while the layout has a pane and one is chosen. Each pane's body rises 40 and fades in over 500 ms, and
// fades out in place (the stage's production recipe). WHICH pane shows and the KeepAlive that keeps each one's state are the core's.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.Zune.ZuneStageGeometry;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage pane region (<see cref="ViewKeys.StagePane"/>).</summary>
internal static class ZuneStagePane
{
    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds, TransitionDynamics.Spring(G.PaneMoveResponse, G.PaneMoveDamping), SizeMode.Reveal);

    static readonly Func<Element, Element> s_frame = Frame;

    static readonly Func<Element> s_empty = static () => new BoxEl { HitTestVisible = false };

    /// <summary>The registered template of <see cref="ViewKeys.StagePane"/>.</summary>
    public static Element Template(StagePaneProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        var tiers = ctx.Tier();   // hooks first, unconditionally
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        var (x, y, w, h) = a.Form == ZuneStageForm.Column ? (a.Pane.X, a.Pane.Y, a.Pane.W, a.Pane.H) : L.PaneRect(in k);
        if (a.Form != ZuneStageForm.Column)
        {
            // the allocator hangs the pane under its 48 bar: the tier's taller head (the Compact pivot row, 56, 72) takes its top
            float drop = ZuneStageTiers.StageOf(in tiers).Drop;
            (y, h) = (y + drop, MathF.Max(0f, h - drop));
        }
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = w, Height = h, ClipToBounds = true,
            Margin = new Edges4(x, y, 0f, 0f), Layout = s_move,
            Visible = Prop.Of(() => sc.Layout.Value.ShowPane && sc.Pane.Value != Stage.PaneKind.None),
            Children = [props.Panes(s_frame, s_empty)],
        };
    }

    /// <summary>The frame a pane's body enters in: it rises and fades in, and fades out in place.</summary>
    static Element Frame(Element body) => new BoxEl
    {
        Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
        Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(G.PaneFadeMs, Easing.FluentDecelerate),
            Enter: new EnterExit(Dy: G.PaneEnterDy, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
        Children = [body],
    };
}
