// ── Themes/Zune/Structure/Views/Stage/ZuneStageRoot.cs — Zune's `Stage.Root` (P5 Z4b) ──────────────────────────────────────────────
//
// The core surface keeps every hook, effect and state machine of the fullscreen stage (the window, the focus scope, the idle machine, the
// layout signal and its classes, the key map); it hands this template a `StageRootView` each render. Zune keeps the stack Classic draws
// (the stage's layering and every production motion recipe are deliberate) and changes the picture inside it:
//
//   backdrop · hero (Z4c's slot) · face · smoke (Card) · face scrim · spectrum · now-playing card · identity · artist name (Z4c's slot)
//   · pane · Up next column · caption · chrome (top bar · transport · face chip · gallery) · hairline · lyric facts · hero facts · clock
//
//   · the TRANSPORT is one view on one full-bleed chrome layer (`chrome:transport`): it draws the flat plate at the foot, or places its
//     controls inside the Card arrangement's card or the Large art column (`ZuneStageGeometry.Arrange`), so a layout switch never
//     remounts the stage or the transport's slot: the plates cross-fade and the one cover node springs to its new rect.
//   · the SMOKE under a Card face is the Card arrangement's diagonal veil (Z-NowPlaying-Card), or the core's bottom smoke under the plate.
//   · the HAIRLINE while the chrome rests is Zune's 2-px line.
//
// VIDEO MODE (`StageRootView.ShowPicture`: Cinema and Theatre, and the Overview tab): the audio stack above gives way to the picture's hollow
// reservation (`ZuneStageTheatre`: the presenter follows it, no ancestor here carries an opacity, a scale or a layout transition over it), the
// audio face in the picture's place over an audio-only track (`ZuneStageAudioFace`), and Theatre's open list sliding in from the right. The chrome
// over the picture is `ZuneStagePictureChrome`, which the core mounts in the video layer.
//
// ZStack placement: vertical = AlignSelf, horizontal = JustifySelf. A Flow.Show boundary and a component are transparent anchors (flex
// columns), so every Show's child is a full-bleed `Stage.Layer` and the placed box sits one level down (see `Stage.Layer`).

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

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's fullscreen stage root.</summary>
internal static class ZuneStageRoot
{
    /// <summary>The registered template of <see cref="ViewKeys.StageRoot"/>: the core surface, drawn by <see cref="Draw"/>.</summary>
    public static Element Template(StageRootProps props, in ViewContext ctx) => props.Mount(Draw);

    /// <summary>The root box and the layer stack for one render of the surface. Static: its delegate is cached, so a re-run of the template
    /// passes an equal argument and re-renders nothing.</summary>
    static Element Draw(StageRootView v)
    {
        var layers = v.Layers;
        return new BoxEl
        {
            Width = v.Width, Height = v.Height, ZStack = true, ClipToBounds = true, Focusable = true,
            OnRealized = v.OnRealized,
            // a move ANYWHERE within the stage is idle activity (over a lyric line, the open list, the chrome), as the contract asks
            OnPointerMoveWithin = v.OnHoverMove,
            OnPointerExit = v.OnPointerExit,
            OnPointerWheel = v.OnPointerWheel,
            OnKeyDown = v.OnKeyDown,
            OnFocusChanged = v.OnFocusChanged,
            Children =
            [
                // Video mode: the picture's hollow reservation (the presenter follows its painted rect) on the stage's floor: the whole stage in
                // Cinema, the left column's 16:9 box in Theatre (ZuneStageTheatre); nothing of the audio stage is drawn under the picture
                Flow.Show(v.ShowPicture, Stage.Layer with
                {
                    Key = "stage:picture", HitTestVisible = false, HitTestPassThrough = false,
                    Children = [Embed.Comp(new ZuneStageTheatre.PictureProps(v), static () => new ZuneStageTheatre.Picture())],
                }),
                // the AUDIO FACE (Video mode over an audio-only track: no presenter, no hole): the flat field, the cover and the lyrics in the
                // picture's place, fading in over the reservation and out under the picture the next video brings back
                Flow.Show(() => v.Stage.In(1) && v.ShowPicture() && v.ShowAudioFace(), Stage.Layer with
                {
                    Key = "stage:audioface",
                    Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                    Children = [Embed.Comp(new ZuneStageAudioFace.Props(v), static () => new ZuneStageAudioFace())],
                }),
                // Theatre's open list beside the picture: it slides in from the right as Theatre opens and back out as it closes
                Flow.Show(() => v.Stage.Alone(2) && v.ShowPicture() && v.ShowTheatre() && layers.TheatrePane is not null, Stage.Layer with
                {
                    Key = "stage:theatre",
                    Enter = s_theatreMotion, Exit = s_theatreMotion, Transition = MotionTok.StandardEnter,
                    Children = [layers.TheatrePane is { } theatrePane ? theatrePane() with { Key = "stage:theatre:pane" } : new BoxEl()],
                }),
                Flow.Show(() => !v.ShowPicture(), Stage.Layer with { Key = "stage:audio", Children = AudioStack(v) }),
                layers.LyricFacts() with { Key = "stage:facts" },
                Flow.Show(v.ShowHeroFacts, layers.HeroFacts() with { Key = "stage:herofacts" }),
                layers.Clock() with { Key = "stage:clock" },
            ],
        };
    }

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    /// <summary>Theatre's list slide: in from the right beside the shrinking picture, back out as the picture fills the stage.</summary>
    static readonly EnterExit s_theatreMotion = new(Dx: ZuneStageVideoGeometry.PaneSlide, Opacity: 0f, Active: true);

    /// <summary>The stage's layers for the audio modes (Lyrics, Visualizer, Up next, Artist), bottom-up, from the backdrop to the hairline.</summary>
    static Element[] AudioStack(StageRootView v)
    {
        var layers = v.Layers;
        var fade = s_fade;
        return
            [
                Slots.View(ViewKeys.StageBackdrop, v.Backdrop) with { Key = "stage:backdrop" },
                // the Artist layout's photo (Z4c's view; the slot keeps its place in the stack)
                Flow.Show(v.ShowHeroLayer, Stage.Layer with
                {
                    Key = "stage:hero", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageHero, v.Hero)],
                }),
                Flow.Show(v.ShowFace, new BoxEl
                {
                    Key = "stage:face", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Enter = new EnterExit(Sx: G.FaceEnterScale, Sy: G.FaceEnterScale, Opacity: 0f, Active: true),
                    Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageFace, v.Face)],
                }),
                Embed.Comp(new ZuneStageSmoke.Props(v.Stage, v.ShowSmoke), static () => new ZuneStageSmoke()) with { Key = "stage:smoke" },
                Slots.View(ViewKeys.StageFaceScrim, v.FaceScrim) with { Key = "stage:facescrim" },
                Slots.View(ViewKeys.StageSpectrum, v.Spectrum) with { Key = "stage:spectrum" },
                Slots.View(ViewKeys.StageNowPlayingCard, v.NowPlayingCard) with { Key = "stage:npc" },
                Slots.View(ViewKeys.StageIdentity, v.Identity) with { Key = "stage:identity" },
                // the Artist layout's big name (Z4c's view; the slot keeps its place in the stack)
                Flow.Show(v.ShowArtistName, Stage.Layer with
                {
                    Key = "stage:artistname", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageArtistName, v.ArtistName)],
                }),
                layers.Pane() with { Key = "stage:pane" },
                Flow.Show(() => layers.UpNext is not null && v.Stage.Layout.Value.UpNextShown(v.Stage.Mode.Value), UpNext(v)),
                Flow.Show(v.ShowCaption, Stage.Layer with
                {
                    // per-frame motion over a still picture: its own repaint boundary
                    Key = "stage:caption", HitTestVisible = false, HitTestPassThrough = false, RepaintBoundary = true,
                    Enter = new EnterExit(Dy: G.CaptionEnterDy, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [layers.Caption()],
                }),
                Chrome(v),
                // the 2-px progress line while the chrome rests: the foot, full width
                Flow.Show(() => !v.ChromeVisible(), Stage.Layer with
                {
                    Key = "stage:hairline", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = G.HairlineH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0, HitTestVisible = false,
                            Children = [new BoxEl { Grow = 1f, Height = G.HairlineH, Fill = Prop.Bind(v.Stage.Slab.Accent), TransformOriginX = 0f, TransformOriginY = G.HairlineOriginY,
                                                    Transform = Prop.Of(() => Affine2D.Scale(v.HairlineFraction(), 1f)) }],
                        },
                    ],
                }),
            ];
    }

    /// <summary>The chrome: the top bar, the transport, the face chip and the gallery, each under its own Show whose child is a full-bleed
    /// layer carrying the Enter/Exit. The wrapper is pass-through: empty chrome yields to the stage root's pointer handlers.</summary>
    static Element Chrome(StageRootView v)
    {
        var L = v.Stage.Layout.Value;
        bool sheet = L.Aspect == Stage.Aspect.Portrait;
        var top = new EnterExit(Dy: -G.BarEnterDy, Opacity: 0f, Active: true);
        var foot = new EnterExit(Dy: G.BarEnterDy, Opacity: 0f, Active: true);
        return Stage.Layer with
        {
            Key = "stage:chrome",
            Children =
            [
                Flow.Show(v.ChromeVisible, Stage.Layer with
                {
                    Key = "chrome:top", Enter = top, Exit = top, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        // the head, placed at the tier's height (48, the Compact bar and its pivot row, 56, 72): a component, it reads the tier
                        Embed.Comp(new ZuneStageHead.Props(v.TopBar), static () => new ZuneStageHead()),
                    ],
                }),
                // the transport: ONE slot on a full-bleed layer; the view places the plate (or the Card / Large art controls) itself
                Flow.Show(v.ChromeVisible, Stage.Layer with
                {
                    Key = "chrome:transport", Enter = foot, Exit = foot, Transition = MotionTok.ControlNormal,
                    Children = [Slots.View(ViewKeys.StageTransport, v.Transport)],
                }),
                // the face chip under a Compact window's identity card (Visualizer mode): the door to the gallery sheet
                Flow.Show(() => v.ChromeVisible() && v.Stage.Layout.Value.FaceChipShown(v.Stage.Mode.Value) && Stage.LayoutRules.ShowsNowPlayingCard(v.Stage.Look.Value.Eff), Stage.Layer with
                {
                    Key = "chrome:facechip", Enter = top, Exit = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ZuneStageFaceChip.Props(v.Stage), static () => new ZuneStageFaceChip())],
                }),
                // the gallery: a right-docked column, or Portrait's bottom sheet; not gated on the idle chrome (the face is inset for it)
                Flow.Show(v.ShowGallery, Stage.Layer with
                {
                    Key = "chrome:gallery",
                    Enter = GalleryMotion(sheet), Exit = GalleryMotion(sheet), Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0,
                            AlignSelf = sheet ? FlexAlign.End : FlexAlign.Start, JustifySelf = sheet ? FlexAlign.Stretch : FlexAlign.End,
                            Margin = sheet ? new Edges4(0f, 0f, 0f, L.TransportH + G.SheetGutters * Stage.Layout.Pad) : new Edges4(0f, Stage.Layout.GalleryTop, Stage.Layout.GalleryRight, L.GalleryBottomInset),
                            Layout = s_galleryFoot,
                            Children = [Slots.View(ViewKeys.StageGallery, v.Gallery)],
                        },
                    ],
                }),
            ],
        };
    }

    static EnterExit GalleryMotion(bool sheet) => new(Dx: sheet ? 0f : G.GallerySlide, Dy: sheet ? G.GallerySlide : 0f, Opacity: 0f, Active: true);

    static readonly LayoutTransition s_galleryFoot = new(TransitionChannels.Bounds, TransitionDynamics.Tween(G.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Reveal);

    static readonly EnterExit s_upNextMotion = new(Dx: G.UpNextSlide, Opacity: 0f, Active: true);

    /// <summary>The Up next column a wide Huge stage docks beside the lyrics (the core's rect, at the tier's gutter and under its head).</summary>
    static Element UpNext(StageRootView v)
        => Stage.Layer with
        {
            Key = "stage:upnext",
            Enter = s_upNextMotion, Exit = s_upNextMotion, Transition = MotionTok.StandardEnter,
            Children = [Embed.Comp(new ZuneStageUpNextFrame.Props(v.Stage, v.Layers.UpNext), static () => new ZuneStageUpNextFrame())],
        };
}

/// <summary>The stage's head: the top bar's slot placed at the top, the window tier's head tall (never stretched to the stage). Keyed
/// <see cref="HeadKey"/> so the tests find the placed bar.</summary>
internal sealed class ZuneStageHead : Component
{
    public const string HeadKey = "zune-stage-head";

    public sealed record Props(StageTopBarProps TopBar);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var t = ZuneStageTiers.StageOf(in tiers);
        return new BoxEl
        {
            // a crossing that changes the head's height (W640: the pivots' own row, W1920: 48 -> 56) eases it through layout instead of
            // snapping (audit B3bc); the bar inside fills it
            Key = HeadKey, Height = t.HeadH, Shrink = 0f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 0,
            Layout = ZuneMotion.HeightResize,
            Children = [Slots.View(ViewKeys.StageTopBar, p.TopBar)],
        };
    }
}

/// <summary>The Up next column's flat floating pane: the core's rect moved in by the tier's gutter (the core budgets it at the plate's 0
/// inset) and down by the tier's head. A component: the rect needs the window tier.</summary>
internal sealed class ZuneStageUpNextFrame : Component
{
    public sealed record Props(Stage.StageCtx Stage, Func<Element>? Build);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var t = ZuneStageTiers.StageOf(in tiers);
        var r = p.Stage.Layout.Value.UpNextRect;
        float x = G.UpNextPaneX(p.Stage.Layout.Value, t), y = r.Y + t.Drop, h = MathF.Max(0f, r.H - t.Drop);
        var pane = new BoxEl
        {
            Key = "stage:upnext:pane", Width = r.W, Height = h,
            Direction = 1, Padding = Edges4.All(G.UpNextPad), Corners = R.Shape.All(ShapeRole.Overlay), ClipToBounds = true,
            Fill = R.Layer.FloatingPane,
            Children = p.Build is null ? [] : [p.Build()],
        };
        // the core's drawer: its tab (the pane's flat fill) collapses the pane off the right edge and back (Stage.UpNextDrawer)
        return Stage.UpNextDrawer(p.Stage, x, y, r.W, h, pane, new BoxEl { Fill = R.Layer.FloatingPane }, G.GalleryYieldMs);
    }
}

/// <summary>The smoke under a Card face: the Card arrangement's diagonal veil over the whole stage (Z-NowPlaying-Card), or the core's
/// bottom smoke under the plate so it reads over any face. A component: the arrangement needs the window tier.</summary>
internal sealed class ZuneStageSmoke : Component
{
    public sealed record Props(Stage.StageCtx Stage, Func<bool> Shown);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var sc = p.Stage;
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        var a = ZuneStageGeometry.Arrange(in L, in k, ZuneStageTiers.StageOf(tiers), k.Mode == Stage.Mode.Visualizer && sc.GalleryShown.Value);
        var fade = ZuneMotion.SwapFade;
        Element veil = a.Form == ZuneStageForm.Card
            ? new BoxEl
            {
                Key = "smoke:card", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                Gradient = GradientDown(
                    new GradientStop(0f, Shade(0f)),
                    new GradientStop(ZuneStageGeometry.CardVeilClearStop, Shade(0f)),
                    new GradientStop(ZuneStageGeometry.CardVeilMidStop, Shade(ZuneStageGeometry.CardVeilMidA)),
                    new GradientStop(1f, Shade(ZuneStageGeometry.CardVeilFootA))),
            }
            : new BoxEl
            {
                Key = "smoke:plate", Height = ZuneStageGeometry.SmokeH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(ZuneStageGeometry.SmokeA))),
            };
        return Stage.Layer with { HitTestVisible = false, HitTestPassThrough = false, Visible = Prop.Of(p.Shown), Children = [veil] };
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };
}
