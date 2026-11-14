// ── Themes/WinUi/Structure/Views/Stage/WinUiStagePane.cs — WinUI's (from Classic's) pane region of the fullscreen stage (themes plan 6.1, P3) ──────
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
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's stage pane region (<see cref="ViewKeys.StagePane"/>).</summary>
internal static class WinUiStagePane
{
    // Bounds, clip-revealed: the region slides to its new origin and its width follows (the Huge 4K lyrics narrowing beside the Up next column)
    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds,
        TransitionDynamics.Spring(WinUiStageGeometry.PaneMoveResponse, WinUiStageGeometry.PaneMoveDamping), SizeMode.Reveal);

    static readonly Func<Element, Element> s_frame = Frame;
    static readonly Func<Element> s_empty = static () => new BoxEl { HitTestVisible = false };

    /// <summary>The registered template of <see cref="ViewKeys.StagePane"/>.</summary>
    public static Element Template(StagePaneProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var t = WinUiStageTiers.StageOf(ctx.Tier());
        if (props.Theatre) return Theatre(props, L, t);
        var k = sc.Look.Value;
        var pane = WinUiStageGeometry.BoardLargeArt(in L, in k, t) ? WinUiStageGeometry.LargeArtPaneRect(in L, in k, t) : L.PaneRect(in k);
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = pane.W, Height = pane.H, ClipToBounds = true,
            Margin = new Edges4(pane.X, pane.Y, 0f, 0f), Layout = s_move,   // the pane slides to its new rect with the cover (Lyrics ⇄ Large art), its width with it
            Visible = Prop.Of(() => sc.Layout.Value.ShowPane && sc.Pane.Value != Stage.PaneKind.None),
            Children = [props.Panes(s_frame, s_empty)],
        };
    }

    /// <summary>Video mode's Theatre pane beside the picture (the Media Player app's play queue) as an OPEN LIST: no card, the list on the page's Mica,
    /// its header row on the picture's top edge and its foot running to the stage's foot. Shown for as long as the root keeps it mounted (the slide is
    /// the root's); a tier crossing moves it with the pane region's move. Up next draws its own header (the count, Autoplay); the Lyrics pane takes
    /// the same header row here ("Lyrics" in BodyStrong), so switching the pane swaps no frame.</summary>
    static Element Theatre(StagePaneProps props, Stage.Layout L, WinUiStageLayout t)
    {
        var pane = WinUiStageTheatre.Rects(L, t).Pane;
        var st = props.Stage;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = pane.W, Height = pane.H, Margin = new Edges4(pane.X, pane.Y, 0f, 0f),
            Direction = 1, ClipToBounds = true, Layout = s_move,
            Children = [props.Panes(body => st.TheatrePane.Peek() == Stage.PaneKind.Lyrics ? Frame(LyricsHeaded(body)) : Frame(body), s_empty)],
        };
    }

    /// <summary>The Theatre Lyrics pane: the open list's header row ("Lyrics", BodyStrong) over the lyrics.</summary>
    static Element LyricsHeaded(Element body) => new BoxEl
    {
        Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
        Children =
        [
            new BoxEl
            {
                Height = WinUiStageTheatreGeometry.HeaderH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Padding = new Edges4(WinUiStageTheatreGeometry.ListPad, 0f, WinUiStageTheatreGeometry.ListPad, 0f),
                Margin = new Edges4(0f, 0f, 0f, WinUiStageTheatreGeometry.HeaderGap),
                Children = [Ui.BodyStrong(Loc.Get(Strings.Stage.Mode.Lyrics)) with { Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap }],
            },
            new BoxEl { Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, Children = [body] },
        ],
    };

    /// <summary>The frame a pane's body enters in: it rises and fades in, and fades out in place.</summary>
    static Element Frame(Element body) => new BoxEl
    {
        Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
        Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(WinUiStageGeometry.PaneFadeMs, Easing.FluentDecelerate),
            Enter: new EnterExit(Dy: WinUiStageGeometry.PaneEnterDy, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
        Children = [body],
    };
}
