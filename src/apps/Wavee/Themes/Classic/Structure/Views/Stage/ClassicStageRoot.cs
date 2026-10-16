// ── Themes/Classic/Structure/Views/Stage/ClassicStageRoot.cs — Classic's `Stage.Root` (themes plan 6.1, T7a1) ───────────────
//
// From `Shell\Stage.UI.cs`: the element tree `SurfaceCore` built inline (the root box, the layer stack) and `ChromeHost` (the chrome's
// placement and its Enter/Exit). The core surface keeps every hook, effect and state machine of the fullscreen stage (the window and the
// focus scope, the idle machine and the cursor, the ONE layout signal and its aspect classes, the preferences, the accent, the key
// map); it hands this template a `StageRootView` each render and mounts what comes back under the stage context. Elements, keys,
// transitions and bound channels are exactly what the stage built before the move, so Classic renders and animates as it always did.
//
//   THE STACK, BOTTOM-UP.
//
//     backdrop · hero layer (Artist) · face · smoke (Card) · face scrim · spectrum · now-playing card · identity (the morphing cover
//     and titles) · artist name · pane · caption · chrome (top bar · transport · gallery) · hairline · lyric facts · hero facts · clock
//
//   ZStack placement: vertical = AlignSelf, horizontal = JustifySelf (FlexLayout.cs:1279-1286). A Flow.Show boundary and a component are
//   transparent ANCHORS (default flex COLUMNS), so every Show's child is a full-bleed `Stage.Layer` and the placed box sits one level
//   down; see `Stage.Layer` for why.
//
// The layout layers (T7a2: backdrop, hero photo, face, face scrim, spectrum, now-playing card, identity, artist name) are views of their own
// (`ViewKeys.Stage*`, mounted here at the position the stack has always had, each under the same keys). The pane host (with the lyrics pane
// T7b and the queue pane T7d, both views, inside it), the caption host and the zero-size machinery (lyric facts, hero facts, the visualizer clock) arrive
// as builders on `StageRootView.Layers`. Nothing here decides: every predicate is the core surface's.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's fullscreen stage root.</summary>
internal static class ClassicStageRoot
{
    /// <summary>The registered template of <see cref="ViewKeys.StageRoot"/>: the core surface, drawn by <see cref="Draw"/>.</summary>
    public static Element Template(StageRootProps props, in ViewContext ctx) => props.Mount(Draw);

    /// <summary>The root box and the layer stack for one render of the surface. A static method: its delegate is cached, so re-running the
    /// template passes an equal argument and re-renders nothing.</summary>
    static Element Draw(StageRootView v)
    {
        var layers = v.Layers;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        return new BoxEl
        {
            Width = v.Width, Height = v.Height, ZStack = true, ClipToBounds = true, Focusable = true,   // explicit size (V-U49); the floor is the Backdrop's
            OnRealized = v.OnRealized,
            OnHoverMove = v.OnHoverMove,
            OnPointerExit = v.OnPointerExit,
            OnPointerWheel = v.OnPointerWheel,
            OnKeyDown = v.OnKeyDown,
            OnFocusChanged = v.OnFocusChanged,
            Children =
            [
                Slots.View(ViewKeys.StageBackdrop, v.Backdrop) with { Key = "stage:backdrop" },
                // the Artist layout's photo + scrims (the backdrop is hidden under an opaque header, see Backdrop)
                Flow.Show(v.ShowHeroLayer, Stage.Layer with
                {
                    Key = "stage:hero", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageHero, v.Hero)],
                }),
                // the face: the Show's child carries its own Enter/Exit (the bare-ComponentEl rule, §1.8 — V-U31); FaceHost keys a CHILD per kind
                Flow.Show(v.ShowFace, new BoxEl
                {
                    Key = "stage:face", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Enter = new EnterExit(Sx: ClassicStageGeometry.FaceEnterScale, Sy: ClassicStageGeometry.FaceEnterScale, Opacity: 0f, Active: true),
                    Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageFace, v.Face)],
                }),
                // bottom smoke under the face: BOTTOM (AlignSelf) across the width (JustifySelf) — V-U9
                new BoxEl
                {
                    Key = "stage:smoke", Height = Stage.Tone.SmokeH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(Stage.Tone.SmokeA))),
                    Visible = Prop.Of(v.ShowSmoke),
                },
                // the legibility scrim between a BACKDROP face (Large art, Centered) and the cover / lyrics: the stage veil, deeper over
                // the cover-carrying faces (LayoutRules.BackdropScrim); a static fill, so a face change cross-fades it
                Slots.View(ViewKeys.StageFaceScrim, v.FaceScrim) with { Key = "stage:facescrim" },
                // the layouts' spectrum: a strip / ring in its own small RepaintBoundary (Card draws none — the face is the picture)
                Slots.View(ViewKeys.StageSpectrum, v.Spectrum) with { Key = "stage:spectrum" },
                Slots.View(ViewKeys.StageNowPlayingCard, v.NowPlayingCard) with { Key = "stage:npc" },
                Slots.View(ViewKeys.StageIdentity, v.Identity) with { Key = "stage:identity" },
                Flow.Show(v.ShowArtistName, Stage.Layer with
                {
                    Key = "stage:artistname", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageArtistName, v.ArtistName)],
                }),
                layers.Pane() with { Key = "stage:pane" },
                // the Huge 4K stage's Up next column beside the lyrics (C-Stage-Huge4K): its own acrylic frame at the allocator's rect, sliding
                // in from the right as the tier or the mode brings it
                Flow.Show(() => layers.UpNext is not null && v.Stage.Layout.Value.UpNextShown(v.Stage.Mode.Value), UpNext(v)),
                // the lyric caption: a full-bleed LAYER (the Show's child carries the fade); the caption host places its block from the
                // allocator's caption geometry (Layout.CaptionX/W/Bottom) — centred in the face's region, above the transport
                Flow.Show(v.ShowCaption, Stage.Layer with
                {
                    // The caption animates every frame (its clock is per-frame) over the still scrim and art: a
                    // RepaintBoundary keeps that motion in its own slice (tiles over the text only) instead of re-
                    // rastering the full-window root tiles under it on every frame.
                    Key = "stage:caption", HitTestVisible = false, HitTestPassThrough = false, RepaintBoundary = true,
                    Enter = new EnterExit(Dy: ClassicStageGeometry.CaptionEnterDy, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [layers.Caption()],
                }),
                Chrome(v),
                // the hairline while the chrome is hidden: BOTTOM edge (AlignSelf), full width (JustifySelf) as a direct child of
                // the Show's layer — directly under the Show anchor its AlignSelf End read horizontally and left it 0 wide (V-U49)
                Flow.Show(() => !v.ChromeVisible(), Stage.Layer with
                {
                    Key = "stage:hairline", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = Stage.Layout.HairlineH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0, HitTestVisible = false,
                            Children = [new BoxEl { Grow = 1f, Height = Stage.Layout.HairlineH, Fill = Prop.Bind(v.Stage.Slab.Accent), TransformOriginX = 0f, TransformOriginY = ClassicStageGeometry.HairlineOriginY,
                                                    Transform = Prop.Of(() => Affine2D.Scale(v.HairlineFraction(), 1f)) }],
                        },
                    ],
                }),
                layers.LyricFacts() with { Key = "stage:facts" },
                Flow.Show(v.ShowHeroFacts, layers.HeroFacts() with { Key = "stage:herofacts" }),
                layers.Clock() with { Key = "stage:clock" },
            ],
        };
    }

    /// <summary>The chrome: the top bar, the transport card and the gallery pane, each under its OWN Flow.Show whose child is a
    /// full-bleed layer carrying the Enter/Exit (a bare ComponentEl root never plays its exit, Reconciler.cs:4106-4137 — V-U31) and the
    /// ZStack placement (vertical = AlignSelf, horizontal = JustifySelf — V-U9). The wrapper is PASS-THROUGH so empty chrome area
    /// yields to the stage root's pointer handlers while the bars stay clickable (V-U8).</summary>
    static Element Chrome(StageRootView v)
    {
        var L = v.Stage.Layout.Value;
        bool sheet = L.Aspect == Stage.Aspect.Portrait;
        // Every piece: Flow.Show → a full-bleed LAYER carrying the Enter/Exit → the PLACED box as the layer's direct ZStack
        // child. Placed directly under the Show anchor (a flex column, see Stage.Layer), the bar's AlignSelf Start / the card's End
        // read as HORIZONTAL alignment: the bar shrank to its content (brand over "Lyrics", exit cut to "Ex…") and the card
        // sat at the top-right overflowing the window; and this host, content-height, put "bottom" near the top.
        return Stage.Layer with
        {
            Key = "stage:chrome",
            Children =
            [
                // TOP bar: top edge (AlignSelf Start), full width (JustifySelf Stretch)
                Flow.Show(v.ChromeVisible, Stage.Layer with
                {
                    Key = "chrome:top",
                    Enter = new EnterExit(Dy: -ClassicStageGeometry.BarEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: -ClassicStageGeometry.BarEnterDy, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = Stage.Layout.TopBarH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Direction = 0,
                            Children = [Slots.View(ViewKeys.StageTopBar, v.TopBar)],
                        },
                    ],
                }),
                // TRANSPORT card: bottom edge (AlignSelf End — auto height ⇒ the card's own height, the window tier's), full width at the
                // tier's gutter, or right of the art on Compact (Layout.TransportLeft — V-U23), or beside a gallery that runs to the foot
                // (C-Stage-Huge); its own component places it, so a gallery toggle re-renders the placement and not this surface.
                Flow.Show(v.ChromeVisible, Stage.Layer with
                {
                    Key = "chrome:transport",
                    Enter = new EnterExit(Dy: ClassicStageGeometry.BarEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: ClassicStageGeometry.BarEnterDy, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ClassicStageTransportPlace.Props(v.Stage, v.Transport), static () => new ClassicStageTransportPlace())],
                }),
                // the FACE CHIP under the identity card (C-Stage-Compact, Visualizer mode in the Card layout): the face's name, and the door to the gallery sheet
                Flow.Show(() => v.ChromeVisible() && v.Stage.Layout.Value.FaceChipShown(v.Stage.Mode.Value) && Stage.LayoutRules.ShowsNowPlayingCard(v.Stage.Look.Value.Eff), Stage.Layer with
                {
                    Key = "chrome:facechip",
                    Enter = new EnterExit(Dy: -ClassicStageGeometry.BarEnterDy, Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new ClassicStageFaceChip.Props(v.Stage), static () => new ClassicStageFaceChip())],
                }),
                // GALLERY pane: a right-docked column (top-aligned, JustifySelf End) or Portrait's bottom sheet (AlignSelf End, full width)
                // NOT gated on the idle chrome: the face beside it is inset for it while it is open (GalleryShown)
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
                            Margin = sheet ? new Edges4(0f, 0f, 0f, L.TransportH + ClassicStageGeometry.SheetGutters * Stage.Layout.Pad) : new Edges4(0f, Stage.Layout.GalleryTop, Stage.Layout.GalleryRight, L.GalleryBottomInset),
                            Layout = s_galleryFoot,   // a tier crossing moves its foot (to the stage's foot at Huge, C-Stage-Huge) on the yield's tween
                            Children = [Slots.View(ViewKeys.StageGallery, v.Gallery)],
                        },
                    ],
                }),
            ],
        };
    }

    /// <summary>The gallery's slide: in from the right (or up from the foot on Portrait's sheet) and back out the same way.</summary>
    static EnterExit GalleryMotion(bool sheet) => new(Dx: sheet ? 0f : ClassicStageGeometry.GallerySlide, Dy: sheet ? ClassicStageGeometry.GallerySlide : 0f, Opacity: 0f, Active: true);

    /// <summary>The docked gallery's foot follows a tier crossing (clip-revealed, on the transport card's yield tween).</summary>
    static readonly LayoutTransition s_galleryFoot = new(TransitionChannels.Bounds, TransitionDynamics.Tween(ClassicStageGeometry.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Reveal);

    /// <summary>The Up next column's slide: in from the right and back out the same way.</summary>
    static readonly EnterExit s_upNextMotion = new(Dx: ClassicStageGeometry.UpNextSlide, Opacity: 0f, Active: true);

    /// <summary>The Huge 4K stage's Up next column (C-Stage-Huge4K): a full-bleed layer carrying the slide, the acrylic frame placed at the
    /// allocator's rect (<c>Layout.UpNextRect</c>) as its direct ZStack child, the core's stage-queue host inside it.</summary>
    static Element UpNext(StageRootView v)
    {
        var L = v.Stage.Layout.Value;
        var r = L.UpNextRect;
        var build = v.Layers.UpNext;
        return Stage.Layer with
        {
            Key = "stage:upnext",
            Enter = s_upNextMotion, Exit = s_upNextMotion, Transition = MotionTok.StandardEnter,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = r.W, Height = r.H, Margin = new Edges4(r.X, r.Y, 0f, 0f),
                    Direction = 1, Padding = Edges4.All(ClassicStageGeometry.UpNextPad), Corners = Radii.CardAll, ClipToBounds = true,
                    Acrylic = Ink.Card, BorderWidth = 1f, BorderColor = Ink.Stroke,
                    Children = build is null ? [] : [build()],
                },
            ],
        };
    }

    /// <summary>The stage veil at alpha <paramref name="a"/> (the arm's veil: dark on the dark arm, light on the light).</summary>
    static ColorF Shade(float a) => Ink.Veil with { A = a };
}
