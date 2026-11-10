// ── Themes/Classic/Structure/Views/Stage/ClassicStagePictureChrome.cs — Classic's `Stage.PictureChrome` (Cinema) ─────────────────
//
// The stage's chrome over the music video in Video mode, as the board "Classic · Cinema" draws it: the picture edge to edge (the ONE video
// presenter following the stage's reservation, `ClassicStageRoot`), a scrim under the top bar, the top bar with the Video tab, "Switch to
// audio" and Exit, and over a scrim at the foot the title block (the track's title, its "Artist · Album" line) above the stage's own
// transport card. Everything but the picture comes and goes on the stage's idle machine (`StageRootView.ChromeVisible`, 3 s without the
// pointer; paused, it stays), with the stage's Enter/Exit; the hairline shows while it rests. In THEATRE (`StageRootView.ShowTheatre`, the
// board "Classic · Theatre") the scrims and the title over the picture give way to the title block UNDER the picture, the chrome stays at rest
// (nothing of it covers the picture) and the ground that takes the pointer shrinks to the picture's box (the panel beside it is the stage's).
//
// The core mounts this in the VIDEO layer right after the presenter (the hole erases what is painted under it), so it is not inside the
// stage's subtree: the ground carries the stage's pointer wiring (a move is idle activity, a double-click leaves full screen) and the keys
// that bubble here go to the stage's key map. The top bar, the transport and the hairline are the stage root's own builders: one chrome.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Classic.ClassicStageCinemaGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's stage chrome over the picture (Video mode, Cinema).</summary>
internal static class ClassicStagePictureChrome
{
    /// <summary>The registered template of <see cref="ViewKeys.StagePictureChrome"/>.</summary>
    public static Element Template(StagePictureChromeProps props, in ViewContext ctx)
    {
        var v = props.Root;
        var L = v.Stage.Layout.Value;
        var tier = ClassicStageTiers.StageOf(ctx.Tier());
        var exit = v.Stage.Exit;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        // Theatre: the picture in the left region with its title under it and the panel beside it (the stage draws the panel); the chrome stays
        // at rest there (none of it covers the picture) and the hairline gives way to the transport. Cinema keeps the idle chrome.
        var theatre = ClassicStageTheatreGeometry.Of(L);
        Func<bool> cinemaChrome = () => v.ChromeVisible() && !v.ShowTheatre();
        Func<bool> chromeShown = () => v.ChromeVisible() || v.ShowTheatre();
        Func<bool> hairline = () => !v.ChromeVisible() && !v.ShowTheatre();
        return Stage.Layer with
        {
            Key = "stage:picturechrome",
            // the stage's pointer wiring and key map, for the moves and keys that land on this layer instead of the stage's root
            OnHoverMove = v.OnHoverMove,
            OnPointerExit = v.OnPointerExit,
            OnPointerWheel = v.OnPointerWheel,
            OnKeyDown = v.OnKeyDown,
            Children =
            [
                // the ground over the picture: it takes the pointer (the picture under it never does), and a double-click on it leaves full
                // screen. Cinema: the whole stage. Theatre: the picture's box alone, so the panel beside it (the stage's, under this layer) and
                // the floor around it take their own pointer.
                Embed.Comp(new GroundProps(v), static () => new Ground()),
                // the scrims: under the top bar, and under the title block and the transport (Cinema: the chrome over the picture)
                Flow.Show(cinemaChrome, Stage.Layer with
                {
                    Key = "picture:scrims", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = G.TopScrimH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Shade(G.TopScrimA)), new GradientStop(1f, Shade(0f))),
                        },
                        new BoxEl
                        {
                            Height = MathF.Round(G.BottomScrimFrac * L.H), AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(G.BottomScrimSolidAt, Shade(G.BottomScrimA)), new GradientStop(1f, Shade(G.BottomScrimA))),
                        },
                    ],
                }),
                // the title block: the transport card's content edge, a gap above the card (Cinema)
                Flow.Show(cinemaChrome, Stage.Layer with
                {
                    Key = "picture:title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Title(v, L, tier)],
                }),
                // Theatre's title block under the picture, at rest
                Flow.Show(v.ShowTheatre, Stage.Layer with
                {
                    Key = "picture:theatre-title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [ClassicStageTheatre.TheatreTitle(v, theatre.Title)],
                }),
                ClassicStageRoot.TopBar(v, chromeShown),
                ClassicStageRoot.Transport(v, chromeShown),
                ClassicStageRoot.Hairline(v, hairline),
            ],
        };
    }

    /// <summary>The title (Display, two lines at most) over the "Artist · Album" line, bottom-left above the transport card.</summary>
    static Element Title(StageRootView v, Stage.Layout L, ClassicStageLayout tier)
    {
        var identity = v.Identity;
        float left = L.TransportLeft + tier.TransportPadding.Left;
        return new BoxEl
        {
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.TitleGap, HitTestVisible = false,
            MaxWidth = MathF.Max(0f, MathF.Min(G.TitleMaxWFrac * L.W, L.W - left - L.TransportInset)),
            Margin = new Edges4(left, 0f, 0f, L.TransportH + L.TransportInset + G.TitleAboveTransport),
            Children =
            [
                new TextEl("")
                {
                    Text = Prop.Of(identity.Title), FontFamily = ClassicStageLayerGeometry.DisplayFace, Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.TitleWeight,
                    Color = Ink.Ink, Wrap = TextWrap.Wrap, MaxLines = G.TitleMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
                new TextEl("")
                {
                    Text = Prop.Of(() => identity.Meta(0).Line), Size = G.MetaSize, LineHeight = G.MetaLine, Color = Ink.InkSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
    }

    sealed record GroundProps(StageRootView View);

    /// <summary>The ground: the whole stage in Cinema, the picture's box in Theatre (moving with the reservation's spring). A component of its
    /// own, so a Cinema ⇄ Theatre change re-renders this box alone.</summary>
    sealed class Ground : Component
    {
        public override Element Render()
        {
            var v = UseProps<GroundProps>().View;
            var r = ClassicStageTheatre.PictureRect(v.Stage.Layout.Value, v.ShowTheatre());
            var exit = v.Stage.Exit;
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:ground", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Fill = ColorF.Transparent,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = ClassicStageTheatre.Move,
                        OnPointerReleased = e => { if (e.ClickCount >= 2) exit(); },
                    },
                ],
            };
        }
    }

    /// <summary>The stage veil at alpha <paramref name="a"/> (the arm's veil: dark on the dark arm, light on the light).</summary>
    static ColorF Shade(float a) => Ink.Veil with { A = a };
}
