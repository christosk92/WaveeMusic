// ── Themes/Zune/Structure/Views/Stage/ZuneStagePane.cs — Zune's pane region of the fullscreen stage (P5 Z4b) ────────────────────────
//
// The region the core pane host fills (the lyrics pane, Up next, the Artist pane: Z4c's views): TOP-LEFT at the allocator's pane rect moved
// onto the tier's gutter (`ZuneStageGeometry.PlatePane`: the lyrics' text starts at the gutter under the identity, beside the art the pane
// keeps its gap to it, and it stops short of the Up next column, at most 900 wide at Huge), or in
// the Large art column under "playing from …" (`ZuneStageGeometry.Arrange`: Large art keeps its lyrics). It clips, slides to a new rect on
// the stage's spring and shows only while the layout has a pane and one is chosen. Each pane's body rises 40 and fades in over 500 ms, and
// fades out in place (the stage's production recipe). WHICH pane shows and the KeepAlive that keeps each one's state are the core's.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

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
        if (props.Theatre) return Theatre(props, in L, ZuneStageTiers.StageOf(in tiers));
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        // the plate's pane sits on the tier's gutter (the lyrics' text starts there), under the tier's taller head (the Compact pivot row, 56, 72),
        // and stops short of the Up next column (`ZuneStageGeometry.PlatePane`)
        var tier = ZuneStageTiers.StageOf(in tiers);
        var pane = a.Form == ZuneStageForm.Column ? a.Pane
            : G.LyricsSide(in L, in k, tier) is { } side && sc.Pane.Value is Stage.PaneKind.Lyrics or Stage.PaneKind.Queue ? side.Pane
            : G.PlatePane(in L, in k, tier, sc.Pane.Value);
        var (x, y, w, h) = (pane.X, pane.Y, pane.W, pane.H);
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = w, Height = h, ClipToBounds = true,
            Margin = new Edges4(x, y, 0f, 0f), Layout = s_move,
            Visible = Prop.Of(() => sc.Layout.Value.ShowPane && sc.Pane.Value != Stage.PaneKind.None),
            Children = [props.Panes(s_frame, s_empty)],
        };
    }

    /// <summary>Video mode's Theatre panel beside the picture as an OPEN LIST on the page: no plate, no card; its top on the picture's top edge,
    /// its foot running to the stage's foot. Up next draws its own section headings (the display face); the lyrics take a "lyrics" heading in the
    /// same voice here, so switching the panel swaps no frame. Shown for as long as the root keeps it mounted (its slide is the root's).</summary>
    static Element Theatre(StagePaneProps props, in Stage.Layout L, ZuneStageLayout t)
    {
        var pane = ZuneStageVideoGeometry.Theatre(in L, t).Pane;
        var st = props.Stage;
        var z = t.Video;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = pane.W, Height = pane.H, Margin = new Edges4(pane.X, pane.Y, 0f, 0f),
            Direction = 1, ClipToBounds = true, Layout = s_move,
            Children = [props.Panes(body => st.TheatrePane.Peek() == Stage.PaneKind.Lyrics ? Frame(LyricsHeaded(body, z)) : Frame(body), s_empty)],
        };
    }

    /// <summary>The Theatre lyrics: "lyrics" in the display face (light, lowercase through the heading case) over the lines.</summary>
    static Element LyricsHeaded(Element body, ZuneStageVideo z) => new BoxEl
    {
        Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
        Children =
        [
            new BoxEl
            {
                Height = z.Px(ZuneStageVideoGeometry.ListHeadH), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Stage.Mode.Lyrics))
                    {
                        Size = z.Dx(ZuneStageVideoGeometry.ListHeadSize), LineHeight = z.Dx(ZuneStageVideoGeometry.ListHeadLine), Weight = ZuneStageVideoGeometry.TitleWeight,
                        FontFamily = R.Type.DisplayFace, Color = StageInk.Ink, Case = R.Case.For(LabelKind.Heading), MaxLines = 1,
                    },
                ],
            },
            new BoxEl { Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, Children = [body] },
        ],
    };

    /// <summary>The frame a pane's body enters in: it rises and fades in, and fades out in place.</summary>
    static Element Frame(Element body) => new BoxEl
    {
        Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
        Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(G.PaneFadeMs, Easing.FluentDecelerate),
            Enter: new EnterExit(Dy: G.PaneEnterDy, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
        Children = [body],
    };
}
