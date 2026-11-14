// ── Themes/Zune/Structure/Views/Stage/ZuneStageTheatre.cs — Zune's picture in Video mode: Cinema and Theatre ────────────────────────────
//
// The picture's place on the stage (`ZuneStageRoot` mounts it under `StageRootView.ShowPicture`): the stage's floor and, on it, the hollow
// RESERVATION the one video presenter follows (`StageRootView.PictureRealized`; the presenter lives in the video layer, painted after the stage, so
// nothing here may be its ancestor). Cinema: the reservation is the whole stage on the letterbox ground. Theatre: the picture's 16:9 box on the left
// gutter under the head (`ZuneStageVideoGeometry.Theatre`), the open list beside it. The reservation moves between the two on a Reflow bounds spring:
// its laid-out size and origin interpolate together, so the presenter following it grows and shrinks with it frame by frame.
//
// THE PAGE (Theatre): Zune's flat colour. The floor is ONE still, flat fill, the stage floor tinted toward the playing track's first colour
// (`StageCtx.BasePalette`), painted once per track (keyed: the old page fades out under the new one), never per frame, never over the picture.
// The letterbox black is never on the moving box: Cinema's ground and Theatre's box are still siblings under the hollow reservation.
//
// THE TITLE under the picture (Theatre, at rest): the accent artist line in the display face, the TITLE in the large light display face on one
// line that bleeds out under a fade at the column's edge (Zune never shrinks its display type), and the quiet "playing from …" line.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageVideoGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's picture in the stage's Video mode (Cinema and Theatre).</summary>
internal static class ZuneStageTheatre
{
    /// <summary>The reservation's move between Cinema and Theatre (and every part that rides with it): the stage's pane spring, Reflow.</summary>
    public static readonly LayoutTransition Move = new(TransitionChannels.Bounds, TransitionDynamics.Spring(G.MoveResponse, G.MoveDamping), SizeMode.Reflow);

    public sealed record PictureProps(StageRootView View);

    /// <summary>The floor, the letterbox ground and the hollow reservation; its own component, so a Cinema ⇄ Theatre change re-renders this box alone.</summary>
    internal sealed class Picture : Component
    {
        static readonly EnterExit s_letterboxFade = new(Opacity: 0f, Active: true);

        bool _theatre;   // the layout the last render drew: a return from Theatre brings Cinema's ground back late

        public override Element Render()
        {
            var v = UseProps<PictureProps>().View;
            var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            var L = v.Stage.Layout.Value;
            bool theatre = v.ShowTheatre();
            bool fromTheatre = _theatre && !theatre;
            _theatre = theatre;
            var r = G.Picture(in L, t, theatre);
            var box = G.Theatre(in L, t).Picture;
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    Embed.Comp(new PictureProps(v), static () => new Page()),
                    // Cinema's letterbox ground: at once when the stage opens in Cinema; back from Theatre once the picture has filled the stage
                    theatre
                        ? new BoxEl { Visible = false }
                        : new BoxEl
                        {
                            Key = "stage:picture:ground", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Tok.MediaLetterbox, HitTestVisible = false,
                            Enter = fromTheatre ? new EnterExit(Opacity: 0f, Active: true, DelayMs: G.GroundReturnMs) : default,
                            Transition = MotionTok.ControlNormal,
                        },
                    // Theatre's letterbox box, still at the picture's 16:9 rect (the shrinking picture covers it from the first frame)
                    // (it waits for the content's first beat and leaves with the content: a still dark box at the picture's destination, drawn
                    // before the gliding picture reaches it or after it has flown home, would lie bare on the page under the fading ground)
                    theatre && v.Stage.In(1)
                        ? new BoxEl
                        {
                            Key = "stage:picture:letterbox", Enter = s_letterboxFade, Exit = s_letterboxFade, Transition = MotionTok.ControlNormal, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                            Margin = new Edges4(box.X, box.Y, 0f, 0f), Width = box.W, Height = box.H, Fill = Tok.MediaLetterbox, Layout = Move,
                        }
                        : new BoxEl { Visible = false },
                    // the reservation: hollow, moving
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

    /// <summary>Theatre's page: the floor, flat, tinted toward the track's colour (one opaque fill per pixel, keyed by the palette).</summary>
    internal sealed class Page : Component
    {
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var v = UseProps<PictureProps>().View;
            var palette = v.Stage.BasePalette.Value;
            var floor = Ink.Floor with { A = 1f };
            var fill = ColorF.Lerp(floor, palette.A with { A = 1f }, G.PageTint);
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:page:" + palette.GetHashCode(), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Fill = fill, Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                    },
                ],
            };
        }
    }

    /// <summary>The accent artist line ("Artist · Album" in the display face, 600), one line.</summary>
    public static Element ArtistLine(StageIdentityProps identity, ZuneStageVideo z, ColorF accent) => new TextEl("")
    {
        Text = Prop.Of(() => identity.Meta(0).Line), Size = z.Dx(G.ArtistSize), LineHeight = z.Dx(G.ArtistLine), Weight = G.ArtistWeight, FontFamily = R.Type.DisplayFace,
        Color = accent, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };
}

/// <summary>The quiet "playing from playlist · late night drive" line of the title blocks: the label in the tertiary ink (label case), the source in
/// the ink. Its own component (the transport facts); its height is reserved with no source, so the block never moves.</summary>
internal sealed class ZuneVideoSourceLine : Component
{
    public sealed record Props(StageTransportProps T, float Size, float Line, float Gap);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var f = p.T.Facts();
        return new BoxEl
        {
            Height = p.Line, Margin = new Edges4(0f, p.Gap, 0f, 0f), Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, HitTestVisible = false,
            Children =
            [
                new TextEl(f.PlayingFrom) { Size = p.Size, LineHeight = p.Line, Color = Ink.InkTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f },
                new TextEl(f.Source.Length > 0 && f.PlayingFrom.Length > 0 ? " · " : "") { Size = p.Size, LineHeight = p.Line, Color = Ink.InkTertiary, Shrink = 0f },   // loc-allow: the separator
                new TextEl(f.Source) { Size = p.Size, LineHeight = p.Line, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        };
    }
}
