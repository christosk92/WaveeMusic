// ── Themes/Classic/Structure/Views/Stage/ClassicStageTheatre.cs — Classic's picture in Video mode: Cinema and Theatre ──────────────
//
// The picture's place on the stage (`ClassicStageRoot` mounts it under `StageRootView.ShowPicture`): the stage's floor and, on it, the hollow
// RESERVATION the one video presenter follows (`StageRootView.PictureRealized`; the presenter lives in the video layer, painted after the stage).
// In Cinema (the board "Classic · Cinema") the reservation is the whole stage; in Theatre (the board "Classic · Theatre") it is the picture's
// 16:9 box in the left region (`ClassicStageTheatreGeometry`), the panel beside it on the right. The reservation moves between the two on a
// Reflow bounds spring (the pane region's move): its laid-out size and its origin interpolate together, so the presenter that follows it grows
// and shrinks with it frame by frame and nothing ever covers the picture. The title block under the picture in Theatre is the picture
// chrome's (`ClassicStagePictureChrome`, `TheatreTitle`).
//
// THE PAGE'S COLOUR (Theatre: "why is the page just full black?"): on the floor, under everything, a STILL wash in the playing track's palette
// (`StageCtx.BasePalette`, what the audio stage's backdrop derives), a glow around the picture's box fading to a faint tint of the whole page
// (YouTube's ambient mode). It is painted once per track (a keyed layer: the old one fades out as the new one fades in), never per frame, never
// over the picture (a sibling under the reservation), and it does not move with the Cinema ⇄ Theatre glide (it sits at Theatre's box; in Cinema
// the letterbox ground covers it). The panel beside the picture is an open list on this page, so the colour shows through it with no blur.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.Classic.ClassicStageTheatreGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's picture in the stage's Video mode (Cinema and Theatre).</summary>
internal static class ClassicStageTheatre
{
    /// <summary>The reservation's move between Cinema and Theatre: the pane region's position spring, Reflow (the size runs through layout, so
    /// the presenter following it is sized with it).</summary>
    public static readonly LayoutTransition Move = new(TransitionChannels.Bounds,
        TransitionDynamics.Spring(ClassicStageGeometry.PaneMoveResponse, ClassicStageGeometry.PaneMoveDamping), SizeMode.Reflow);

    /// <summary>The picture's rect for the stage layout <paramref name="L"/>: the whole stage in Cinema, the left region's 16:9 box in Theatre.</summary>
    public static RectF PictureRect(Stage.Layout L, bool theatre) => theatre ? G.Of(L).Picture : new RectF(0f, 0f, L.W, L.H);

    public sealed record PictureProps(StageRootView View);

    /// <summary>The floor, the letterbox ground and the reservation. A component of its own so a Cinema ⇄ Theatre change re-renders this box alone
    /// (the reservation's rect), never the stage root.
    /// <para>THE RESERVATION IS HOLLOW: it is the one box that moves (its bounds spring between the two rects, the presenter following it), and a
    /// box that paints re-rasters every tile it crosses on every frame of that move. So the letterbox black is never on the moving box: it is a
    /// STILL box at each layout's rect (the whole stage in Cinema, the 16:9 box in Theatre), a sibling under the reservation (never an ancestor of
    /// the picture). What shows is what the moving black box showed: the presenter (its own letterbox included) covers its rect on every frame,
    /// Theatre's box lies inside the shrinking picture from the first frame and Cinema's ground comes back only once the growing picture has
    /// filled the stage (<see cref="ClassicStageTheatreGeometry.GroundReturnMs"/>), so the floor shows around the moving picture either way.</para></summary>
    internal sealed class Picture : Component
    {
        bool _theatre;   // the layout the last render drew: what tells a return from Theatre from the stage opening in Cinema

        public override Element Render()
        {
            var v = UseProps<PictureProps>().View;
            var L = v.Stage.Layout.Value;
            bool theatre = v.ShowTheatre();
            bool fromTheatre = _theatre && !theatre;
            _theatre = theatre;
            var r = PictureRect(L, theatre);
            var box = G.Of(L).Picture;
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    // the floor around the picture in Theatre (under it in Cinema the letterbox covers it), and the track's colour on it
                    new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Ink.Floor, HitTestVisible = false },
                    Embed.Comp(new PictureProps(v), static () => new Wash()),
                    // Cinema's letterbox ground, edge to edge: there at once when the stage opens in Cinema; back from Theatre once the picture has
                    // filled the stage (until then the floor shows around the growing picture, as it did around the growing box); gone at once as
                    // Theatre opens (the floor shows around the shrinking picture)
                    theatre
                        ? new BoxEl { Visible = false }
                        : new BoxEl
                        {
                            Key = "stage:picture:ground", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Tok.MediaLetterbox, HitTestVisible = false,
                            Enter = fromTheatre ? new EnterExit(Opacity: 0f, Active: true, DelayMs: G.GroundReturnMs) : default,
                            Transition = MotionTok.ControlNormal,
                        },
                    // Theatre's letterbox box, still at the picture's 16:9 rect (the shrinking picture covers it from the first frame)
                    theatre
                        ? new BoxEl
                        {
                            Key = "stage:picture:letterbox", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                            Margin = new Edges4(box.X, box.Y, 0f, 0f), Width = box.W, Height = box.H, Fill = Tok.MediaLetterbox,
                            Layout = Move,   // still while Theatre holds; a tier crossing or a resize glides it with the reservation instead of snapping
                        }
                        : new BoxEl { Visible = false },
                    // the reservation: hollow, moving
                    new BoxEl
                    {
                        Key = "stage:picture:box",
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H,
                        Layout = Move,
                        OnRealized = v.PictureRealized,
                    },
                ],
            };
        }
    }

    /// <summary>The page's colour: the still wash of the playing track's palette, centred on Theatre's picture box (see the file header). Its own
    /// component: a track change (the palette) or a resize re-renders this layer alone, and the keyed glow cross-fades.</summary>
    internal sealed class Wash : Component
    {
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var v = UseProps<PictureProps>().View;
            var st = v.Stage;
            var L = st.Layout.Value;
            var palette = st.BasePalette.Value;
            var box = G.Of(L).Picture;
            float w = MathF.Max(1f, L.W), h = MathF.Max(1f, L.H);
            var c = palette.A;
            var glow = new GradientSpec(GradientShape.Radial, 0f,
                [new GradientStop(0f, c with { A = G.WashCentreA }), new GradientStop(G.WashMidAt, c with { A = G.WashMidA }), new GradientStop(1f, c with { A = G.WashEdgeA })])
            {
                RadialCenter = new Point2((box.X + box.W * 0.5f) / w, (box.Y + box.H * 0.5f) / h),
                RadialRadius = new Point2(box.W * G.WashReachX / w, box.H * G.WashReachY / h),
            };
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:wash:" + palette.GetHashCode(), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = glow, Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                    },
                ],
            };
        }
    }

    /// <summary>Theatre's title block under the picture: the title (Display 28/36 semibold, one line) over the "Artist · Album" line and the quiet
    /// "Playing from" line, at the picture's left edge and width.</summary>
    public static Element TheatreTitle(StageRootView v, in RectF title)
    {
        var identity = v.Identity;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.MetaGap, HitTestVisible = false,
            Margin = new Edges4(title.X, title.Y, 0f, 0f), Width = title.W, Height = title.H,
            Layout = Move,
            Children =
            [
                new TextEl("")
                {
                    Text = Prop.Of(identity.Title), FontFamily = ClassicStageLayerGeometry.DisplayFace, Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.TitleWeight,
                    Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
                new TextEl("")
                {
                    Text = Prop.Of(() => identity.Meta(0).Line), Size = G.MetaSize, LineHeight = G.MetaLine, Color = Ink.InkSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
                Embed.Comp(new SourceLine.Props(v.Transport, G.SourceSize, G.SourceLine), static () => new SourceLine()),   // the block's gap (MetaGap) is the source line's (SourceGap)
            ],
        };
    }
}
