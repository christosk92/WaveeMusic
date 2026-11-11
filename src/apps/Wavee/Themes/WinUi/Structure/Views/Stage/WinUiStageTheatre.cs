// ── Themes/WinUi/Structure/Views/Stage/WinUiStageTheatre.cs — WinUI's picture in Video mode: Cinema and Theatre ─────────────────────────
//
// The picture's place on the stage (`WinUiStageRoot` mounts it under `StageRootView.ShowPicture`): the page and, on it, the hollow RESERVATION the one
// video presenter follows (`StageRootView.PictureRealized`; the presenter lives in the video layer, painted after the stage). In Cinema the reservation
// is the whole stage; in Theatre it is the picture's 16:9 box in the left region (`WinUiStageTheatreGeometry`), the play-queue pane beside it. The
// reservation moves between the two on the pane region's bounds spring, Reflow: its laid-out size and origin interpolate together, so the presenter
// following it grows and shrinks with it frame by frame and nothing covers the picture.
//
// THE PAGE (Theatre): Mica. The window's floor tinted by what plays (`StageCtx.BasePalette`), a still diagonal wash painted once per track (a keyed layer
// that cross-fades), never per frame, never over the picture (a sibling under the reservation). In Cinema the letterbox ground covers it.
//
// THE RESERVATION IS HOLLOW: it is the one box that moves, and a box that paints re-rasters every tile it crosses on every frame of that move. So the
// letterbox is a STILL box at each layout's rect (the stage in Cinema, the 16:9 box in Theatre), a sibling under the reservation, never its ancestor.
// Cinema's ground returns only once the growing picture has filled the stage (`GroundReturnMs`).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.WinUi.WinUiStageTheatreGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's picture in the stage's Video mode (Cinema and Theatre).</summary>
internal static class WinUiStageTheatre
{
    /// <summary>The reservation's move between Cinema and Theatre (and everything that follows the picture's rect): the pane region's spring, Reflow.</summary>
    public static readonly LayoutTransition Move = new(TransitionChannels.Bounds,
        TransitionDynamics.Spring(WinUiStageGeometry.PaneMoveResponse, WinUiStageGeometry.PaneMoveDamping), SizeMode.Reflow);

    /// <summary>The window tier's stage record as a component sees it (a crossing re-renders the reader).</summary>
    public static WinUiStageLayout Tier(IReadSignal<ThemeFacts>? facts) => WinUiStageTiers.StageOf(TierContext.DrawnIn(facts).Value.Tiers);

    /// <summary>Theatre's rects for the stage layout <paramref name="L"/> under the tier's bar, at the bar's own inset (the wordmark's left edge: the
    /// picture, its title and the about block line up under it, as Cinema's title block does).</summary>
    public static G.Rects Rects(Stage.Layout L, WinUiStageLayout t) => G.Of(L, t.BarH, t.BarInset);

    /// <summary>The picture's rect: the whole stage in Cinema, the left region's 16:9 box in Theatre.</summary>
    public static RectF PictureRect(Stage.Layout L, WinUiStageLayout t, bool theatre) => theatre ? Rects(L, t).Picture : new RectF(0f, 0f, L.W, L.H);

    public sealed record PictureProps(StageRootView View);

    /// <summary>The page, the letterbox and the reservation. Its own component: a Cinema ⇄ Theatre change re-renders this box alone.</summary>
    internal sealed class Picture : Component
    {
        bool _theatre;   // the layout the last render drew: a return from Theatre is not the stage opening in Cinema

        public override Element Render()
        {
            var v = UseProps<PictureProps>().View;
            var t = Tier(UseContext(ThemeFactsContext.Signal));
            var L = v.Stage.Layout.Value;
            bool theatre = v.ShowTheatre();
            bool fromTheatre = _theatre && !theatre;
            _theatre = theatre;
            var r = PictureRect(L, t, theatre);
            var box = Rects(L, t).Picture;
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    Embed.Comp(new PictureProps(v), static () => new Wash()),
                    theatre
                        ? new BoxEl { Visible = false }
                        : new BoxEl
                        {
                            Key = "stage:picture:ground", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Tok.MediaLetterbox, HitTestVisible = false,
                            Enter = fromTheatre ? new EnterExit(Opacity: 0f, Active: true, DelayMs: G.GroundReturnMs) : default,
                            Transition = MotionTok.ControlNormal,
                        },
                    theatre
                        ? new BoxEl
                        {
                            Key = "stage:picture:letterbox", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                            Margin = new Edges4(box.X, box.Y, 0f, 0f), Width = box.W, Height = box.H, Fill = Tok.MediaLetterbox, Layout = Move,
                        }
                        : new BoxEl { Visible = false },
                    new BoxEl
                    {
                        Key = "stage:picture:box", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = Move,
                        OnRealized = v.PictureRealized,
                    },
                ],
            };
        }
    }

    /// <summary>The page's Mica: the floor tinted toward the playing track's colour, a still diagonal (see the file header). One opaque fill per pixel;
    /// its own component, keyed by the palette, so a track change cross-fades it and re-renders this layer alone.</summary>
    internal sealed class Wash : Component
    {
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var st = UseProps<PictureProps>().View.Stage;
            var palette = st.BasePalette.Value;
            var floor = Ink.Floor with { A = 1f };
            var c = palette.A with { A = 1f };
            var mica = new GradientSpec(GradientShape.Linear, G.WashAngle,
                [new GradientStop(0f, ColorF.Lerp(floor, c, G.WashTopA)), new GradientStop(1f, ColorF.Lerp(floor, c, G.WashBottomA))]);
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:wash:" + palette.GetHashCode(), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = mica, Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                    },
                ],
            };
        }
    }

    /// <summary>Theatre's title block under the picture: the title (Title 28/36 semibold, one line), "Artist · Album" (Body) and the quiet "Playing from"
    /// line (Caption), at the picture's left edge and width, moving with it.</summary>
    public static Element TheatreTitle(StageRootView v, in RectF title)
    {
        var identity = v.Identity;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.MetaGap, HitTestVisible = false,
            Margin = new Edges4(title.X, title.Y, 0f, 0f), Width = title.W, Height = title.H, Layout = Move,
            Children =
            [
                new TextEl("")
                {
                    Text = Prop.Of(identity.Title), FontFamily = WinUiStageLayerGeometry.DisplayFace, Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.TitleWeight,
                    Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
                new TextEl("")
                {
                    Text = Prop.Of(() => identity.Meta(0).Line), Size = G.MetaSize, LineHeight = G.MetaLine, Color = Ink.InkSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
                Embed.Comp(new WinUiStageSourceLine.Props(v.Transport, G.SourceSize, G.SourceLine, G.SourceGap - G.MetaGap), static () => new WinUiStageSourceLine()),
            ],
        };
    }
}

/// <summary>The quiet "Playing from · source" line of the title blocks (Cinema's over the picture, Theatre's under it): the source's name in the accent
/// text as the transport card's lead column draws it (W-Stage). Its own component; its height is reserved with no source, so the block never moves.</summary>
internal sealed class WinUiStageSourceLine : Component
{
    /// <param name="Nudge">Added to the block's gap above this line (Theatre's 2 under a 4 gap: -2).</param>
    public sealed record Props(StageTransportProps T, float Size, float Line, float Nudge = 0f);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var f = p.T.Facts();
        return new BoxEl
        {
            Height = p.Line, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, HitTestVisible = false,
            Margin = new Edges4(0f, p.Nudge, 0f, 0f),
            Children =
            [
                new TextEl(f.PlayingFrom) { Size = p.Size, LineHeight = p.Line, Color = Ink.InkTertiary, MaxLines = 1, Shrink = 0f },
                new TextEl(f.Source) { Size = p.Size, LineHeight = p.Line, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        };
    }
}
