// ── Themes/Classic/Structure/Views/Stage/ClassicStageTheatre.cs — Classic's picture in Video mode: Cinema and Theatre ──────────────
//
// The picture's place on the stage (`ClassicStageRoot` mounts it under `StageRootView.ShowPicture`): the stage's floor and, on it, the hollow
// RESERVATION the one video presenter follows (`StageRootView.PictureRealized`; the presenter lives in the video layer, painted after the stage).
// In Cinema (the board "Classic · Cinema") the reservation is the whole stage; in Theatre (the board "Classic · Theatre") it is the picture's
// 16:9 box in the left region (`ClassicStageTheatreGeometry`), the panel beside it on the right. The reservation moves between the two on a
// Reflow bounds spring (the pane region's move): its laid-out size and its origin interpolate together, so the presenter that follows it grows
// and shrinks with it frame by frame and nothing ever covers the picture. The title block under the picture in Theatre is the picture
// chrome's (`ClassicStagePictureChrome`, `TheatreTitle`).

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

    /// <summary>The floor and the reservation. A component of its own so a Cinema ⇄ Theatre change re-renders this box alone (the reservation's
    /// rect), never the stage root.</summary>
    internal sealed class Picture : Component
    {
        public override Element Render()
        {
            var v = UseProps<PictureProps>().View;
            var r = PictureRect(v.Stage.Layout.Value, v.ShowTheatre());
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    // the floor around the picture in Theatre (under it in Cinema the letterbox covers it)
                    new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Ink.Floor, HitTestVisible = false },
                    new BoxEl
                    {
                        Key = "stage:picture:box",
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H,
                        Fill = Tok.MediaLetterbox,
                        Layout = Move,
                        OnRealized = v.PictureRealized,
                    },
                ],
            };
        }
    }

    /// <summary>Theatre's title block under the picture: the title (Display 28/36 semibold, one line) over the "Artist · Album" line, at the
    /// picture's left edge and width.</summary>
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
            ],
        };
    }
}
