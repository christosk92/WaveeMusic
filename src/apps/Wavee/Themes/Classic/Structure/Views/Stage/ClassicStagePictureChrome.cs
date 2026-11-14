// ── Themes/Classic/Structure/Views/Stage/ClassicStagePictureChrome.cs — Classic's `Stage.PictureChrome` (Cinema) ─────────────────
//
// The board "Classic · Cinema": the picture edge to edge (the ONE video presenter following the stage's reservation, `ClassicStageRoot`), a scrim
// under the top bar, the top bar with the Video tab and "Switch to audio", and over a scrim at the foot the title block (the track's title, its
// "Artist · Album" line and the quiet "Playing from" line) above the CONTROL BAR (`ClassicStageVideoBar`, YouTube's player bar: the scrim, the seek
// bar and the row of controls at the foot of the picture's rect). Everything but the picture comes and goes on the stage's idle machine
// (`StageRootView.ChromeVisible`, 3 s without the pointer; paused, it stays), with the stage's Enter/Exit; the hairline shows at the picture's foot
// while it rests. In THEATRE (`StageRootView.ShowTheatre`, the board "Classic · Theatre") the scrims and the title over the picture give way to the
// title block UNDER the picture; the control bar is the picture's (at the foot of its 16:9 box, moving with it) and idles exactly as in Cinema,
// while what covers no picture (the top bar, the title under it, the panel beside it) stays at rest. The core decides that only the picture counts
// for the pointer there (`Stage.UI.cs`, from the reservation's rect); the ground that takes the pointer is the picture's box.
//
// The core mounts this in the VIDEO layer right after the presenter (the hole erases what is painted under it), so it is not inside the
// stage's subtree: the ground carries the stage's pointer wiring (a move is idle activity, a double-click leaves full screen) and the keys
// that bubble here go to the stage's key map. The top bar is the stage root's own builder: one chrome.
//
// UP NEXT (Cinema): the shelf of the next tracks (`ClassicStageShelf`, the core's `StageLayers.UpNextShelf`) rides in and out with the idle chrome,
// a gap above the control bar; the title block stands a gap above the shelf's place, which is reserved on every tier that has a shelf, so neither
// moves when the shelf comes or goes. THE AUDIO FACE (`StageRootView.ShowAudioFace`, an audio-only track while video stays on): the frame stays (top
// bar, shelf, the control bar, at rest: none of it covers a picture), the title over the picture and its scrims give way to the face's own cover and
// title (`ClassicStageAudioFace`, a stage layer under this one: no picture, so no hole), and the ground lets the pointer through to the face's lyrics.
//
// THE END CARD (`StageRootView.ShowEndCard`, a music video ended and the next track waits for the countdown): the picture stays where it is, its last
// frame under a veil (the whole stage in Cinema, the picture's box in Theatre), the card (`ClassicStageEndCard`, the core's `StageLayers.EndCard`)
// over it; the title block over the picture gives way to it (the card names what comes next), and Cinema's shelf lists the tracks after the card's.
// The chrome stays as it is: the paused deck pins the idle chrome.

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
using T = Wavee.Themes.Classic.ClassicStageTheatreGeometry;
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
        var fade = new EnterExit(Opacity: 0f, Active: true);
        // The control bar covers the picture in both layouts, so it idles in both (YouTube's). What covers no picture stays at rest in Theatre:
        // the top bar (the title under the picture and the panel beside it are at rest anyway). The audio face is laid out clear of the whole
        // chrome (its cover, title and lyrics end above the shelf's place and the bar), so the chrome stays at rest there: idling it out would
        // cover nothing and leave the frame's lower block a bare band under the lyrics.
        var theatre = ClassicStageTheatreGeometry.Of(L);
        // THE CHOREOGRAPHY (Stage.Motion.cs): every part comes in on its beat after the picture has mostly landed (1: the title, the scrims, the
        // end card; 2: the control bar, the shelf, the about block; 3: the top bar, the hairline) and leaves first when the stage closes; a part of
        // one arrangement only (the scrims and the shelf in Cinema, the about block in Theatre) also waits out a Cinema ⇄ Theatre move
        var sc = v.Stage;
        Func<bool> cinemaChrome = () => (v.ChromeVisible() || v.ShowAudioFace()) && !v.ShowTheatre();
        // the title over the picture and its scrims: Cinema with a picture (the audio face draws its own title under the cover)
        Func<bool> overPicture = () => v.ChromeVisible() && !v.ShowTheatre() && !v.ShowAudioFace() && !v.ShowEndCard();
        bool shelf = tier.ShelfCardW > 0f && v.Layers.UpNextShelf is not null;
        Func<bool> topShown = () => sc.In(3) && (v.ChromeVisible() || v.ShowTheatre() || v.ShowAudioFace());
        Func<bool> barShown = () => sc.In(2) && (v.ChromeVisible() || v.ShowAudioFace());
        Func<bool> hairline = () => sc.In(Stage.Choreo.Beats) && !v.ChromeVisible() && !v.ShowAudioFace();
        // the title above the shelf's place where the tier has a shelf (reserved: the shelf comes and goes with the chrome, the title never moves for it)
        float shelfH = v.Layers.UpNextShelf is null ? 0f : ClassicStageShelfGeometry.Height(tier.ShelfCardW);
        float cinemaAbove = shelfH > 0f ? ClassicStageShelfGeometry.AboveBar + shelfH + ClassicStageShelfGeometry.TitleAbove : G.TitleAboveBar;
        return Stage.Layer with
        {
            Key = "stage:picturechrome",
            // the stage's pointer wiring and key map, for the moves and keys that land on this layer instead of the stage's root. A move
            // anywhere within it is idle activity: the picture's ground is the hit leaf, the bars and the card are children.
            OnPointerMoveWithin = v.OnHoverMove,
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
                Flow.Show(() => sc.Alone(1) && overPicture(), Stage.Layer with
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
                // the end card's veil over the last frame (Cinema: the stage; Theatre: the picture's box, moving with it)
                Flow.Show(() => sc.In(1) && v.ShowEndCard(), Stage.Layer with
                {
                    Key = "picture:end-dim", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new GroundProps(v), static () => new EndDim())],
                }),
                // the control bar at the picture's foot (Cinema: the stage's; Theatre: the 16:9 box's, moving with it), idling with the chrome; under the
                // title, the shelf and the end card, so its scrim never veils them
                Flow.Show(barShown, Stage.Layer with
                {
                    Key = "picture:bar:motion",
                    Enter = new EnterExit(Dy: ClassicStageVideoBarGeometry.EnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: ClassicStageVideoBarGeometry.EnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ClassicStageVideoBar.Props(v), static () => new ClassicStageVideoBar())],
                }),
                // the title block: ONE block in both arrangements, travelling between Cinema's place (at the gutter, a gap above the shelf's
                // reserved place or the control bar; with the chrome) and Theatre's (under the picture, at rest) in step with the picture
                Flow.Show(() => sc.In(1) && (v.ShowTheatre() || overPicture()), Stage.Layer with
                {
                    Key = "picture:title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: G.TitleEnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new TitleProps(v, cinemaAbove), static () => new TitleBlock())],
                }),
                // Up next: the shelf of the next tracks a gap above the control bar, with the chrome (Cinema, on a tier that has one)
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
                            Margin = new Edges4(ShelfLeft(L), 0f, 0f, ClassicStageVideoBarGeometry.BarH + ClassicStageShelfGeometry.AboveBar),
                            Children = [v.Layers.UpNextShelf is { } upNext ? upNext() : new BoxEl()],
                        },
                    ],
                }),
                // the end card over the picture (centred between the top bar and the shelf's place, or in the picture's box)
                Flow.Show(() => sc.In(1) && v.ShowEndCard() && v.Layers.EndCard is not null, Stage.Layer with
                {
                    Key = "picture:endcard",
                    Enter = new EnterExit(Dy: ClassicStageEndCardGeometry.EnterDy, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Dy: ClassicStageEndCardGeometry.EnterDy, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlNormal,
                    Children = [v.Layers.EndCard is { } endCard ? endCard() : new BoxEl()],
                }),
                // the about block under Theatre's title (YouTube's channel row and description), at rest, where the room under the title holds it whole
                Flow.Show(() => sc.Alone(2) && v.ShowTheatre() && theatre.AboutFits, Stage.Layer with
                {
                    Key = "picture:theatre-about",
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Embed.Comp(new ClassicStageTheatreAbout.Props(v), static () => new ClassicStageTheatreAbout())],
                }),
                ClassicStageRoot.TopBar(v, topShown),
                // the hairline at the picture's foot while the bar rests
                Flow.Show(hairline, Stage.Layer with
                {
                    Key = "picture:hairline:motion", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ClassicStageVideoHairline.Props(v), static () => new ClassicStageVideoHairline())],
                }),
            ],
        };
    }

    /// <param name="CinemaAbove">Cinema's gap from the control bar's top to the block's foot (above the shelf's reserved place, or the bar).</param>
    sealed record TitleProps(StageRootView View, float CinemaAbove);

    /// <summary>THE title block of both arrangements: the title over the "Artist · Album" line and the quiet "Playing from" line. In Cinema it is
    /// Display 36/44 (two lines at most) bottom-left above the shelf's place or the control bar; in Theatre Display 28/36 (one line) under the
    /// picture at its left edge and width. One node: a Cinema ⇄ Theatre switch moves it on the picture's spring and scales its type from the
    /// size it had (<see cref="Stage.Traveller"/>), so it travels with the picture instead of two copies cross-fading.</summary>
    sealed class TitleBlock : Stage.Traveller
    {
        public override Element Render()
        {
            var p = UseProps<TitleProps>();
            var v = p.View;
            var L = v.Stage.Layout.Value;
            bool theatre = v.ShowTheatre();
            var identity = v.Identity;
            BoxEl placed;
            float size;
            if (theatre)
            {
                var r = ClassicStageTheatreGeometry.Of(L).Title;
                size = T.TitleSize;
                placed = new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, Gap = T.MetaGap, HitTestVisible = false,
                    Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H,
                    // the block's gap (MetaGap) is the source line's (SourceGap)
                    Children = Lines(v, identity, T.TitleSize, T.TitleLine, 1, T.MetaSize, T.MetaLine, T.SourceSize, T.SourceLine),
                };
            }
            else
            {
                float left = ShelfLeft(L);
                size = G.TitleSize;
                placed = new BoxEl
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.TitleGap, HitTestVisible = false,
                    MaxWidth = MathF.Max(0f, MathF.Min(G.TitleMaxWFrac * L.W, L.W - left - L.TransportInset)),
                    Margin = new Edges4(left, 0f, 0f, ClassicStageVideoBarGeometry.BarH + p.CinemaAbove),
                    Children = Lines(v, identity, G.TitleSize, G.TitleLine, G.TitleMaxLines, G.MetaSize, G.MetaLine, G.SourceSize, G.SourceLine),
                };
            }
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children = [Travel(placed, size, ClassicStageGeometry.PaneMoveResponse, ClassicStageGeometry.PaneMoveDamping)],
            };
        }

        static Element[] Lines(StageRootView v, StageIdentityProps identity, float titleSize, float titleLine, int titleLines, float metaSize, float metaLine,
            float sourceSize, float sourceLine) =>
        [
            new TextEl("")
            {
                Text = Prop.Of(identity.Title), FontFamily = ClassicStageLayerGeometry.DisplayFace, Size = titleSize, LineHeight = titleLine, Weight = G.TitleWeight,
                Color = Ink.Ink, Wrap = titleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = titleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            new TextEl("")
            {
                Text = Prop.Of(() => identity.Meta(0).Line), Size = metaSize, LineHeight = metaLine, Color = Ink.InkSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            Embed.Comp(new SourceLine.Props(v.Transport, sourceSize, sourceLine), static () => new SourceLine()),
        ];
    }

    /// <summary>The left edge the title block and the shelf share: the stage's gutter (the control bar's).</summary>
    public static float ShelfLeft(Stage.Layout L) => L.TransportInset;

    /// <summary>The shelf's width: the stage between its gutters.</summary>
    public static float ShelfWidth(Stage.Layout L) => MathF.Max(0f, L.W - 2f * L.TransportInset);

    /// <summary>The top of the Cinema chrome's lower block under the audio face (which draws no title block over the picture): the shelf's
    /// reserved place where the tier has a shelf, else the control bar.</summary>
    public static float LowerBlockTop(StageRootView v, Stage.Layout L, ClassicStageLayout tier) => LowerBlockTop(L, tier, v.Layers.UpNextShelf is not null);

    /// <inheritdoc cref="LowerBlockTop(StageRootView, Stage.Layout, ClassicStageLayout)"/> (<paramref name="shelf"/>: the core offers the shelf).
    public static float LowerBlockTop(Stage.Layout L, ClassicStageLayout tier, bool shelf)
    {
        float shelfH = shelf ? ClassicStageShelfGeometry.Height(tier.ShelfCardW) : 0f;
        return L.H - ClassicStageVideoBarGeometry.BarH - (shelfH > 0f ? ClassicStageShelfGeometry.AboveBar + shelfH : 0f);
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
            // the audio face has no picture to guard: the pointer reaches the face (its lyrics scroll and take their clicks) and the stage's root
            bool audio = v.ShowAudioFace();
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:ground", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Fill = ColorF.Transparent,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = ClassicStageTheatre.Move, HitTestVisible = !audio,
                        OnPointerReleased = e => { if (e.ClickCount >= 2) exit(); },
                    },
                ],
            };
        }
    }

    /// <summary>The end card's veil over the last frame: the whole stage in Cinema, the picture's box in Theatre (moving with the reservation's spring).</summary>
    sealed class EndDim : Component
    {
        public override Element Render()
        {
            var v = UseProps<GroundProps>().View;
            var r = ClassicStageTheatre.PictureRect(v.Stage.Layout.Value, v.ShowTheatre());
            return Stage.Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "picture:end-veil", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, Layout = ClassicStageTheatre.Move,
                        Fill = Shade(ClassicStageEndCardGeometry.DimA),
                    },
                ],
            };
        }
    }

    /// <summary>The stage veil at alpha <paramref name="a"/> (the arm's veil: dark on the dark arm, light on the light).</summary>
    static ColorF Shade(float a) => Ink.Veil with { A = a };
}

/// <summary>The quiet "Playing from · source" line of the title blocks (Cinema's over the picture, Theatre's under it): what the transport card's left
/// column said before the control bar took its place. Its own component, reading the transport facts (a context change re-renders this line alone);
/// its height is reserved with no source, so the block never moves.</summary>
internal sealed class SourceLine : Component
{
    public sealed record Props(StageTransportProps T, float Size, float Line);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var f = p.T.Facts();
        return new BoxEl
        {
            Height = p.Line, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, HitTestVisible = false,
            Children =
            [
                new TextEl(f.PlayingFrom) { Size = p.Size, LineHeight = p.Line, Color = Ink.InkTertiary, MaxLines = 1, Shrink = 0f },
                new TextEl(f.Source) { Size = p.Size, LineHeight = p.Line, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        };
    }
}
