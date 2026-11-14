// ── Themes/Zune/Structure/Views/Stage/ZuneStagePictureChrome.cs — Zune's `Stage.PictureChrome` (Video mode) ──────────────────────────────
//
// The chrome over the music video, Zune's way (`ZuneStageVideoGeometry` has the picture): the picture edge to edge in CINEMA and, with the
// chrome, the stage's head (the pivots with "video" leading them, "switch to audio" at the right) over a veil fading into the picture; at the foot
// the plate (`ZuneStageVideoBar`: the audio stage's flat plate, the seek line across the picture, one row of plateless glyphs and the accent
// disc); above it the "up next" strip (`ZuneStageShelf`) and, above that, the accent artist line over the TITLE in the light display face. All of
// it comes and goes on the stage's idle machine (`StageRootView.ChromeVisible`: 3 s without the pointer, gone when the pointer leaves the
// window; paused, it stays). The 2-px progress line stands at the picture's foot while the plate rests.
//
// THEATRE (`StageRootView.ShowTheatre`): the plate is the picture's (at the foot of its 16:9 box, moving with it) and idles as in Cinema; what
// covers no picture stays at rest: the head, the title block under the picture (`ZuneStageTheatre.TheatreTitle`), "about the artist"
// (`ZuneStageTheatreAbout`) and the open list beside it (the stage root's). The core counts only the picture for the pointer there.
//
// THE AUDIO FACE (an audio-only track while video stays on): the frame stays at rest (head, strip, plate: none of it covers a picture), the
// title over the picture and its veils give way to the face's own (`ZuneStageAudioFace`, a stage layer under this one), and the ground lets the
// pointer through to the face's lyrics. THE END CARD: the picture's last frame under a veil, the card over it (`ZuneStageEndCard`), the strip
// lists the tracks after the card's.
//
// The core mounts this in the VIDEO layer right after the presenter (the hole erases what is painted under it): no ancestor here carries an
// opacity, a scale or a layout transition over the picture; the layer carries the stage's pointer wiring and key map.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Zune.ZuneStageVideoGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage chrome over the picture (Video mode).</summary>
internal static class ZuneStagePictureChrome
{
    /// <summary>The registered template of <see cref="ViewKeys.StagePictureChrome"/>.</summary>
    public static Element Template(StagePictureChromeProps props, in ViewContext ctx)
    {
        var v = props.Root;
        var L = v.Stage.Layout.Value;
        var t = ZuneStageTiers.StageOf(ctx.Tier());
        var z = t.Video;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        var rise = new EnterExit(Dy: z.Px(G.RiseDy), Opacity: 0f, Active: true);
        var top = new EnterExit(Dy: -ZuneStageGeometry.BarEnterDy, Opacity: 0f, Active: true);
        var theatre = G.Theatre(in L, t);
        bool shelf = z.ShelfCardW > 0f && v.Layers.UpNextShelf is not null;
        // the plate covers the picture in both layouts, so it idles in both; over the audio face the frame stays at rest (it covers no picture).
        // THE CHOREOGRAPHY (Stage.Motion.cs): every part comes in on its beat after the picture has mostly landed (1: the title, the veils, the end
        // card; 2: the plate, the strip, the about block; 3: the head, the line) and leaves first when the stage closes; a part of one arrangement
        // only (the veils and the strip in Cinema, the about block in Theatre) also waits out a Cinema ⇄ Theatre move
        var sc = v.Stage;
        Func<bool> cinemaChrome = () => (v.ChromeVisible() || v.ShowAudioFace()) && !v.ShowTheatre();
        Func<bool> overPicture = () => v.ChromeVisible() && !v.ShowTheatre() && !v.ShowAudioFace() && !v.ShowEndCard();
        Func<bool> topShown = () => sc.In(3) && (v.ChromeVisible() || v.ShowTheatre() || v.ShowAudioFace());
        Func<bool> barShown = () => sc.In(2) && (v.ChromeVisible() || v.ShowAudioFace());
        Func<bool> hairline = () => sc.In(Stage.Choreo.Beats) && !v.ChromeVisible() && !v.ShowAudioFace();
        return Stage.Layer with
        {
            Key = "stage:picturechrome",
            OnPointerMoveWithin = v.OnHoverMove,
            OnPointerExit = v.OnPointerExit,
            OnPointerWheel = v.OnPointerWheel,
            OnKeyDown = v.OnKeyDown,
            Children =
            [
                // the ground over the picture: it takes the pointer (the picture under it never does), a double-click leaves full screen
                Embed.Comp(new GroundProps(v), static () => new Ground()),
                // the veils of Cinema's chrome over the picture: under the head, and under the title above the plate
                Flow.Show(() => sc.Alone(1) && overPicture(), Stage.Layer with
                {
                    Key = "picture:scrims", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = z.Px(G.TopVeilH), AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Shade(G.TopVeilA)), new GradientStop(1f, Shade(0f))),
                        },
                        new BoxEl
                        {
                            Height = MathF.Round(G.TitleVeilFrac * L.H), AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(G.TitleVeilA))),
                        },
                    ],
                }),
                // the end card's veil over the last frame (Cinema: the stage; Theatre: the picture's box, moving with it)
                Flow.Show(() => sc.In(1) && v.ShowEndCard(), Stage.Layer with
                {
                    Key = "picture:end-dim", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new GroundProps(v), static () => new EndDim())],
                }),
                // the plate at the picture's foot, idling with the chrome
                Flow.Show(barShown, Stage.Layer with
                {
                    Key = "picture:bar:motion",
                    Enter = new EnterExit(Dy: G.BarEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: G.BarEnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ZuneStageVideoBar.Props(v), static () => new ZuneStageVideoBar())],
                }),
                // the accent artist line, the title and "playing from": ONE block in both arrangements, travelling between Cinema's place (at the
                // gutter, above the strip's reserved place or the plate; with the chrome) and Theatre's (under the picture, at rest) with the picture
                Flow.Show(() => sc.In(1) && (v.ShowTheatre() || overPicture()), Stage.Layer with
                {
                    Key = "picture:title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = rise, Exit = rise, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new TitleProps(v, shelf), static () => new TitleBlock())],
                }),
                // the "up next" strip above the plate, with the chrome (Cinema, on a tier that has one)
                Flow.Show(() => shelf && sc.Alone(2) && cinemaChrome(), Stage.Layer with
                {
                    Key = "picture:shelf",
                    Enter = rise, Exit = rise, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 1,
                            Margin = new Edges4(t.Gutter, 0f, 0f, G.BarH(t) + z.Px(G.ShelfAbovePlate)),
                            Children = [v.Layers.UpNextShelf is { } upNext ? upNext() : new BoxEl()],
                        },
                    ],
                }),
                // the end card over the picture
                Flow.Show(() => sc.In(1) && v.ShowEndCard() && v.Layers.EndCard is not null, Stage.Layer with
                {
                    Key = "picture:endcard",
                    Enter = rise, Exit = rise, Transition = MotionTok.ControlNormal,
                    Children = [v.Layers.EndCard is { } endCard ? endCard() : new BoxEl()],
                }),
                // Theatre: "about the artist" under the title block, at rest
                Flow.Show(() => sc.Alone(2) && v.ShowTheatre() && theatre.AboutFits, Stage.Layer with
                {
                    Key = "picture:theatre-about",
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Embed.Comp(new ZuneStageTheatreAbout.Props(v), static () => new ZuneStageTheatreAbout())],
                }),
                // the stage's head (Zune's bar and pivots)
                Flow.Show(topShown, Stage.Layer with
                {
                    Key = "chrome:top", Enter = top, Exit = top, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ZuneStageHead.Props(v.TopBar), static () => new ZuneStageHead())],
                }),
                // the 2-px line at the picture's foot while the plate rests
                Flow.Show(hairline, Stage.Layer with
                {
                    Key = "picture:hairline:motion", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ZuneStageVideoHairline.Props(v), static () => new ZuneStageVideoHairline())],
                }),
            ],
        };
    }

    /// <param name="Shelf">The tier has Cinema's "up next" strip (its place is reserved under the title).</param>
    sealed record TitleProps(StageRootView View, bool Shelf);

    /// <summary>THE title block of both arrangements: the accent artist line, the title in the light display face and "playing from". In Cinema
    /// bottom-left above the strip's place or the plate, the title two lines at most; in Theatre under the picture, the large title one line
    /// bleeding out under a fade. One node: a Cinema ⇄ Theatre switch moves it on the picture's spring and scales its type from the size it had
    /// (<see cref="Stage.Traveller"/>), so it travels with the picture instead of two copies cross-fading. The sizes at rest are the theme's.</summary>
    sealed class TitleBlock : Stage.Traveller
    {
        public override Element Render()
        {
            var p = UseProps<TitleProps>();
            var v = p.View;
            var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            var z = t.Video;
            var L = v.Stage.Layout.Value;
            var accent = v.Stage.Accent.Value.Text;
            bool theatre = v.ShowTheatre();
            var identity = v.Identity;
            BoxEl placed;
            float size;
            if (theatre)
            {
                var r = G.Theatre(in L, t).Title;
                size = z.Dx(G.TheatreTitleSize);
                placed = new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, HitTestVisible = false,
                    Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H,
                    Children =
                    [
                        ZuneStageTheatre.ArtistLine(identity, z, accent),
                        new BoxEl
                        {
                            Height = z.Dx(G.TheatreTitleLine), Margin = new Edges4(0f, z.Px(G.TitleGap), 0f, 0f), Direction = 0,
                            ClipToBounds = true, HitTestPassThrough = true, EdgeFade = new EdgeFadeSpec(EdgeMask.Right, z.Px(G.TitleFadeBand)),
                            Children =
                            [
                                new TextEl("")
                                {
                                    Text = Prop.Of(identity.Title), Size = size, LineHeight = z.Dx(G.TheatreTitleLine), Weight = G.TitleWeight,
                                    FontFamily = R.Type.DisplayFace, CharSpacing = G.TitleTracking, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.None, Shrink = 0f,
                                },
                            ],
                        },
                        Embed.Comp(new ZuneVideoSourceLine.Props(v.Transport, z.Px(G.SourceSize), z.Px(G.SourceLine), z.Px(G.SourceGap)), static () => new ZuneVideoSourceLine()),
                    ],
                };
            }
            else
            {
                float lower = G.LowerBlockTop(in L, t, p.Shelf);
                size = z.Dx(G.CinemaTitleSize);
                placed = new BoxEl
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 1, HitTestVisible = false,
                    MaxWidth = MathF.Max(0f, MathF.Min(G.CinemaTitleMaxWFrac * L.W, L.W - 2f * t.Gutter)),
                    Margin = new Edges4(t.Gutter, 0f, 0f, L.H - lower + z.Px(G.CinemaTitleAbove)),
                    Children =
                    [
                        ZuneStageTheatre.ArtistLine(identity, z, accent),
                        // the title's line box: a plain wrapping row here, Theatre's clipped one-line bleed there (one node either way)
                        new BoxEl
                        {
                            Direction = 0, MinWidth = 0f, HitTestPassThrough = true,
                            Children =
                            [
                                new TextEl("")
                                {
                                    Text = Prop.Of(identity.Title), Size = size, LineHeight = z.Dx(G.CinemaTitleLine), Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace,
                                    CharSpacing = G.TitleTracking, Color = Ink.Ink, Wrap = TextWrap.Wrap, MaxLines = G.CinemaTitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                                },
                            ],
                        },
                        Embed.Comp(new ZuneVideoSourceLine.Props(v.Transport, z.Px(G.SourceSize), z.Px(G.SourceLine), z.Px(G.SourceGap)), static () => new ZuneVideoSourceLine()),
                    ],
                };
            }
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children = [Travel(placed, size, G.MoveResponse, G.MoveDamping)],
            };
        }
    }

    sealed record GroundProps(StageRootView View);

    /// <summary>The ground: the whole stage in Cinema, the picture's box in Theatre (moving with the reservation's spring).</summary>
    sealed class Ground : Component
    {
        public override Element Render()
        {
            var v = UseProps<GroundProps>().View;
            var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            var r = G.Picture(v.Stage.Layout.Value, t, v.ShowTheatre());
            var exit = v.Stage.Exit;
            bool audio = v.ShowAudioFace();
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:ground", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Fill = ColorF.Transparent,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = ZuneStageTheatre.Move, HitTestVisible = !audio,
                        OnPointerReleased = e => { if (e.ClickCount >= 2) exit(); },
                    },
                ],
            };
        }
    }

    /// <summary>The end card's veil over the last frame (Cinema: the stage; Theatre: the picture's box).</summary>
    sealed class EndDim : Component
    {
        public override Element Render()
        {
            var v = UseProps<GroundProps>().View;
            var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            var r = G.Picture(v.Stage.Layout.Value, t, v.ShowTheatre());
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:end-veil", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = ZuneStageTheatre.Move, Fill = Shade(G.EndDimA),
                    },
                ],
            };
        }
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };
}
