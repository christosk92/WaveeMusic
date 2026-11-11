// ── Themes/WinUi/Structure/Views/Stage/WinUiStagePictureChrome.cs — WinUI's `Stage.PictureChrome` (Video mode: Cinema and Theatre) ──────────
//
// The Media Player app's full-screen video, in WinUI's stage language. CINEMA: the picture edge to edge (the one video presenter following the stage's
// reservation, `WinUiStageTheatre`), a short veil under the top bar (the bar is the stage's own, its selector on a smoke plate: `WinUiStageTopBar`),
// and at the foot the title block, the Up next shelf (`WinUiStageShelf`) and the COMMAND BAR (`WinUiStageVideoBar`: one r8 smoke plate, the seek rail
// across its top, the commands under it). Everything but the picture comes and goes on the stage's idle machine (`StageRootView.ChromeVisible`: 3 s
// without the pointer; paused, it stays; the pointer leaving the window hides it), with the stage's Enter/Exit; the accent hairline shows at the
// picture's foot while it rests. THEATRE (`StageRootView.ShowTheatre`): the veils and the title over the picture give way to the title block and the
// about card UNDER the picture; the command bar is the picture's (at the foot of its 16:9 box, moving with it) and idles exactly as in Cinema, while
// what covers no picture (the top bar, the title, the about card, the pane beside it) stays at rest. The core counts only the picture for the pointer
// there; the ground that takes the pointer is the picture's box.
//
// The core mounts this in the VIDEO layer right after the presenter (the hole erases what is painted under it), so it is not inside the stage's subtree:
// the ground carries the stage's pointer wiring (a move is idle activity, a double-click leaves full screen) and the keys that bubble here go to the
// stage's key map.
//
// THE AUDIO FACE (`StageRootView.ShowAudioFace`, an audio-only track while video stays on): the frame stays at rest (top bar, shelf, command bar: none
// covers a picture), the title over the picture gives way to the face's own (`WinUiStageAudioFace`, a stage layer under this one: no hole), and the
// ground lets the pointer through to the face's lyrics. THE END CARD (`StageRootView.ShowEndCard`): the last frame stays under a veil (the stage in
// Cinema, the box in Theatre), the card over it (`WinUiStageEndCard`); the title over the picture gives way to it and Cinema's shelf lists the tracks
// after the card's.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.WinUi.WinUiStageCinemaGeometry;
using B = Wavee.Themes.WinUi.WinUiStageVideoBarGeometry;
using S = Wavee.Themes.WinUi.WinUiStageShelfGeometry;
using T = Wavee.Themes.WinUi.WinUiStageTheatreGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's stage chrome over the picture (Video mode).</summary>
internal static class WinUiStagePictureChrome
{
    /// <summary>The registered template of <see cref="ViewKeys.StagePictureChrome"/>.</summary>
    public static Element Template(StagePictureChromeProps props, in ViewContext ctx)
    {
        var v = props.Root;
        var L = v.Stage.Layout.Value;
        var t = WinUiStageTiers.StageOf(ctx.Tier());
        var fade = new EnterExit(Opacity: 0f, Active: true);
        var theatre = WinUiStageTheatre.Rects(L, t);
        // the command bar covers the picture in both layouts, so it idles in both; what covers no picture stays at rest in Theatre (the top bar).
        // Over the audio face the frame stays at rest: idling it out would cover nothing.
        // THE CHOREOGRAPHY (Stage.Motion.cs): every part comes in on its beat after the picture has mostly landed (1: the title, the veils, the end
        // card; 2: the command bar, the shelf, the about block; 3: the top bar, the hairline) and leaves first when the stage closes; a part of one
        // arrangement only (the veils and the shelf in Cinema, the about block in Theatre) also waits out a Cinema ⇄ Theatre move
        var sc = v.Stage;
        Func<bool> cinemaChrome = () => (v.ChromeVisible() || v.ShowAudioFace()) && !v.ShowTheatre();
        Func<bool> overPicture = () => v.ChromeVisible() && !v.ShowTheatre() && !v.ShowAudioFace() && !v.ShowEndCard();
        bool shelf = t.ShelfCardW > 0f && v.Layers.UpNextShelf is not null;
        Func<bool> topShown = () => sc.In(3) && (v.ChromeVisible() || v.ShowTheatre() || v.ShowAudioFace());
        Func<bool> barShown = () => sc.In(2) && (v.ChromeVisible() || v.ShowAudioFace());
        Func<bool> hairline = () => sc.In(Stage.Choreo.Beats) && !v.ChromeVisible() && !v.ShowAudioFace();
        float shelfH = v.Layers.UpNextShelf is null ? 0f : S.Height(t.ShelfCardW);
        float cinemaAbove = shelfH > 0f ? S.AboveBar + shelfH + S.TitleAbove : G.TitleAboveBar;
        var topBar = v.TopBar;
        return Stage.Layer with
        {
            Key = "stage:picturechrome",
            OnPointerMoveWithin = v.OnHoverMove,
            OnPointerExit = v.OnPointerExit,
            OnPointerWheel = v.OnPointerWheel,
            OnKeyDown = v.OnKeyDown,
            Children =
            [
                Embed.Comp(new ChromeProps(v), static () => new Ground()),
                // the veils: under the top bar, and under the title block and the shelf (Cinema with the chrome)
                Flow.Show(() => sc.Alone(1) && overPicture(), Stage.Layer with
                {
                    Key = "picture:scrims", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = G.TopScrimH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Shade(G.TopScrimA)), new GradientStop(G.TopScrimSolidAt, Shade(G.TopScrimA)), new GradientStop(1f, Shade(0f))),
                        },
                        new BoxEl
                        {
                            Height = MathF.Round(G.BottomScrimFrac * L.H), AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(G.BottomScrimSolidAt, Shade(G.BottomScrimA)), new GradientStop(1f, Shade(G.BottomScrimA))),
                        },
                    ],
                }),
                // the end card's veil over the last frame (Cinema: the stage; Theatre: the picture's box, moving with it)
                Flow.Show(() => sc.In(1) && v.ShowEndCard(), Stage.Layer with
                {
                    Key = "picture:end-dim", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ChromeProps(v), static () => new EndDim())],
                }),
                // the command bar at the picture's foot, idling with the chrome; under the title, the shelf and the end card
                Flow.Show(barShown, Stage.Layer with
                {
                    Key = "picture:bar:motion",
                    Enter = new EnterExit(Dy: B.EnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: B.EnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new WinUiStageVideoBar.Props(v), static () => new WinUiStageVideoBar())],
                }),
                // the title block: ONE block in both arrangements, travelling between Cinema's place (a gap above the shelf's reserved place or
                // the command bar; with the chrome) and Theatre's (under the picture, at rest) in step with the picture
                Flow.Show(() => sc.In(1) && (v.ShowTheatre() || overPicture()), Stage.Layer with
                {
                    Key = "picture:title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new TitleProps(v, cinemaAbove), static () => new TitleBlock())],
                }),
                // Up next: the shelf a gap above the command bar, with the chrome (Cinema, on a tier that has one)
                Flow.Show(() => shelf && sc.Alone(2) && cinemaChrome(), Stage.Layer with
                {
                    Key = "picture:shelf",
                    Enter = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 1,
                            Margin = new Edges4(Left(t), 0f, 0f, B.BarH(t.BarInset) + S.AboveBar),
                            Children = [v.Layers.UpNextShelf is { } upNext ? upNext() : new BoxEl()],
                        },
                    ],
                }),
                // the end card (centred between the top bar and the shelf's place, or in the picture's box)
                Flow.Show(() => sc.In(1) && v.ShowEndCard() && v.Layers.EndCard is not null, Stage.Layer with
                {
                    Key = "picture:endcard",
                    Enter = new EnterExit(Dy: WinUiStageEndCardGeometry.EnterDy, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Dy: WinUiStageEndCardGeometry.EnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [v.Layers.EndCard is { } endCard ? endCard() : new BoxEl()],
                }),
                // the about block under Theatre's title (the channel row and the description card), at rest, where the room under the title holds it whole
                Flow.Show(() => sc.Alone(2) && v.ShowTheatre() && theatre.AboutFits, Stage.Layer with
                {
                    Key = "picture:theatre-about",
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Embed.Comp(new WinUiStageTheatreAbout.Props(v), static () => new WinUiStageTheatreAbout())],
                }),
                // the stage's top bar (its own head: the bar at the tier's height, no sheet grabber over the picture)
                Flow.Show(topShown, Stage.Layer with
                {
                    Key = "chrome:top",
                    Enter = new EnterExit(Dy: -WinUiStageGeometry.BarEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: -WinUiStageGeometry.BarEnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new WinUiStageHead.Props(topBar, Picture: true), static () => new WinUiStageHead())],
                }),
                // the hairline at the picture's foot while the bar rests
                Flow.Show(hairline, Stage.Layer with
                {
                    Key = "picture:hairline:motion", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ChromeProps(v), static () => new Hairline())],
                }),
            ],
        };
    }

    /// <param name="CinemaAbove">Cinema's gap from the command bar's top to the block's foot (above the shelf's reserved place, or the bar).</param>
    sealed record TitleProps(StageRootView View, float CinemaAbove);

    /// <summary>THE title block of both arrangements: the title over "Artist · Album" and the "Playing from" line. In Cinema TitleLarge (two lines
    /// at most), BodyLarge and Body, bottom-left above the shelf's reserved place or the command bar; in Theatre Title 28/36 (one line), Body and
    /// Caption under the picture at its left edge and width. One node: a Cinema ⇄ Theatre switch moves it on the picture's spring and scales its
    /// type from the size it had (<see cref="Stage.Traveller"/>), so it travels with the picture instead of two copies cross-fading.</summary>
    sealed class TitleBlock : Stage.Traveller
    {
        public override Element Render()
        {
            var p = UseProps<TitleProps>();
            var v = p.View;
            var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
            var L = v.Stage.Layout.Value;
            bool theatre = v.ShowTheatre();
            var identity = v.Identity;
            BoxEl placed;
            float size;
            if (theatre)
            {
                var r = WinUiStageTheatre.Rects(L, t).Title;
                size = T.TitleSize;
                placed = new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, Gap = T.MetaGap, HitTestVisible = false,
                    Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H,
                    Children = Lines(v, identity, T.TitleSize, T.TitleLine, 1, T.MetaSize, T.MetaLine, T.SourceSize, T.SourceLine, T.SourceGap - T.MetaGap),
                };
            }
            else
            {
                float left = Left(t);
                size = G.TitleSize;
                placed = new BoxEl
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.TitleGap, HitTestVisible = false,
                    MaxWidth = MathF.Max(0f, MathF.Min(G.TitleMaxWFrac * L.W, L.W - 2f * left)),
                    Margin = new Edges4(left, 0f, 0f, B.BarH(t.BarInset) + p.CinemaAbove),
                    Children = Lines(v, identity, G.TitleSize, G.TitleLine, G.TitleMaxLines, G.MetaSize, G.MetaLine, G.SourceSize, G.SourceLine, 0f),
                };
            }
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children = [Travel(placed, size, WinUiStageGeometry.PaneMoveResponse, WinUiStageGeometry.PaneMoveDamping)],
            };
        }

        static Element[] Lines(StageRootView v, StageIdentityProps identity, float titleSize, float titleLine, int titleLines, float metaSize, float metaLine,
            float sourceSize, float sourceLine, float sourceNudge) =>
        [
            new TextEl("")
            {
                Text = Prop.Of(identity.Title), FontFamily = WinUiStageLayerGeometry.DisplayFace, Size = titleSize, LineHeight = titleLine, Weight = G.TitleWeight,
                Color = Ink.Ink, Wrap = titleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = titleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            new TextEl("")
            {
                Text = Prop.Of(() => identity.Meta(0).Line), Size = metaSize, LineHeight = metaLine, Color = Ink.InkSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            Embed.Comp(new WinUiStageSourceLine.Props(v.Transport, sourceSize, sourceLine, sourceNudge), static () => new WinUiStageSourceLine()),
        ];
    }

    /// <summary>The left edge the title block, the shelf and the command bar share in Cinema: the bar's inset (the wordmark's).</summary>
    public static float Left(WinUiStageLayout t) => t.BarInset;

    /// <summary>The shelf's width: the stage between the bar's insets.</summary>
    public static float ShelfWidth(Stage.Layout L, WinUiStageLayout t) => MathF.Max(0f, L.W - 2f * t.BarInset);

    /// <summary>The top of Cinema's lower block (the shelf's reserved place where the tier has one, else the command bar).</summary>
    public static float LowerBlockTop(Stage.Layout L, WinUiStageLayout t, bool shelf)
    {
        float shelfH = shelf ? S.Height(t.ShelfCardW) : 0f;
        return L.H - B.BarH(t.BarInset) - (shelfH > 0f ? S.AboveBar + shelfH : 0f);
    }

    sealed record ChromeProps(StageRootView View);

    /// <summary>The ground: the whole stage in Cinema, the picture's box in Theatre (moving with the reservation). It takes the pointer (the picture under
    /// it never does) and a double-click on it leaves full screen; over the audio face it lets the pointer through to the face.</summary>
    sealed class Ground : Component
    {
        public override Element Render()
        {
            var v = UseProps<ChromeProps>().View;
            var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
            var r = WinUiStageTheatre.PictureRect(v.Stage.Layout.Value, t, v.ShowTheatre());
            var exit = v.Stage.Exit;
            bool audio = v.ShowAudioFace();
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:ground", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Fill = ColorF.Transparent,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = WinUiStageTheatre.Move, HitTestVisible = !audio,
                        OnPointerReleased = e => { if (e.ClickCount >= 2) exit(); },
                    },
                ],
            };
        }
    }

    /// <summary>The end card's veil over the last frame (the stage in Cinema, the picture's box in Theatre).</summary>
    sealed class EndDim : Component
    {
        public override Element Render()
        {
            var v = UseProps<ChromeProps>().View;
            var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
            var r = WinUiStageTheatre.PictureRect(v.Stage.Layout.Value, t, v.ShowTheatre());
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:end-veil", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = WinUiStageTheatre.Move,
                        Fill = Shade(WinUiStageEndCardGeometry.DimA),
                    },
                ],
            };
        }
    }

    /// <summary>The progress hairline while the bar rests, in the accent, at the picture's foot (moving with it).</summary>
    sealed class Hairline : Component
    {
        public override Element Render()
        {
            var v = UseProps<ChromeProps>().View;
            var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
            var r = WinUiStageTheatre.PictureRect(v.Stage.Layout.Value, t, v.ShowTheatre());
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:hairline", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 0, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y + r.H - Stage.Layout.HairlineH, 0f, 0f), Width = r.W, Height = Stage.Layout.HairlineH, Layout = WinUiStageTheatre.Move,
                        Children = [new BoxEl { Grow = 1f, Height = Stage.Layout.HairlineH, Fill = Prop.Bind(v.Stage.Slab.Accent), TransformOriginX = 0f, TransformOriginY = WinUiStageGeometry.HairlineOriginY,
                                                Transform = Prop.Of(() => Affine2D.Scale(v.HairlineFraction(), 1f)) }],
                    },
                ],
            };
        }
    }

    /// <summary>The stage veil at alpha <paramref name="a"/> (dark on the dark arm, light on the light).</summary>
    static ColorF Shade(float a) => Ink.Veil with { A = a };
}
