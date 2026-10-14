// ── Themes/Classic/Structure/Views/Stage/ClassicStageLayers.cs — Classic's backdrop, face, scrim, spectrum and card (themes plan 6.1, T7a2) ──
//
// From `Shell\Stage.UI.cs` (Backdrop, BackdropArt, FaceHost, FaceScrim, NowPlayingCard) and `Stage.Layouts.UI.cs` (SpectrumLayer): the
// element trees those core components built inline. WHICH look is drawn (the layout, the spectrum, the face, the cover's rect) is the core
// allocator's and arrives on the stage context's signals; each template reads them inside its own render, so a layer re-renders on exactly
// what it read, and draws. Elements, keys, motion and bound channels are exactly what the stage built before the move.
//
//   Stage.Backdrop         the baked cover (old layer kept under the new), the base Field, the scrim; ONE quarter-scale repaint boundary
//   Stage.Face             the chosen face, remounted per (kind, 8-DIP width bucket) on a keyed child, inset while the gallery is open
//   Stage.FaceScrim        the stage veil over a BACKDROP face (Large art, Centered)
//   Stage.Spectrum         the strip / ring the layouts draw, in its own small RepaintBoundary
//   Stage.NowPlayingCard   the acrylic card behind the Card layout's thumb

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

/// <summary>What the layer templates share: the stage veil.</summary>
internal static class ClassicStageLayerKit
{
    /// <summary>The stage veil at alpha <paramref name="a"/> (the arm's veil: dark on the dark arm, light on the light).</summary>
    public static ColorF Shade(float a) => Ink.Veil with { A = a };
}

/// <summary>Wavee Classic's stage backdrop: the baked cover (old layer kept under the new), the base Field (the palette's roles, cross-fading
/// with it), the scrim (deeper under Verse — Tone.ScrimFor).</summary>
internal static class ClassicStageBackdrop
{
    /// <summary>The registered template of <see cref="ViewKeys.StageBackdrop"/>.</summary>
    public static Element Template(StageBackdropProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var mode = sc.Mode.Value;
        var kind = sc.Kind.Value;
        var eff = sc.Look.Value.Eff;
        var track = sc.RowValue();
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        return Stage.Layer with   // full height from Grow, not from whichever child happens to declare L.H
        {
            // ONE quarter-scale repaint boundary for the whole backdrop: the floor, the cover under an 80-DIP baked blur, the
            // Field's radial blobs and the flat scrim are all soft or flat, so they raster together into one small surface
            // and composite as ONE upsampled quad. Separately they were three window-sized layers composited every frame
            // (the Field drifts under all of them), and the bars' acrylic re-blurred all three.
            ClipToBounds = true, HitTestVisible = false, HitTestPassThrough = false, Fill = Ink.Floor,
            RepaintBoundary = true, RasterScale = 0.25f,
            // under an opaque header photo the quarter-scale blobs would raster for nothing: hide the whole backdrop (Artist layout, Visualizer mode, photo known)
            Visible = Prop.Of(props.Shown),
            Children =
            [
                Embed.Comp(new ClassicStageBackdropArt.Props(url), static () => new ClassicStageBackdropArt()),
                Visualizer.BackdropField(sc.Slab, L.W, L.H, Prop.Bind(sc.Slab.BaseFieldOp), boundary: false) with { Key = "stage:field" },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Fill = ClassicStageLayerKit.Shade(Stage.LayoutRules.ScrimFor(mode, eff, kind)), BrushTransitionMs = Stage.Tone.CrossFadeMs,   // a static fill cross-fades; a bound one would snap
                },
            ],
        };
    }
}

/// <summary>The blurred cover is baked ONCE per track (BakedBlurSpec) and the PREVIOUS cover stays mounted under
/// the new one, so a track change fades blur-to-blur and never shows a crisp placeholder.</summary>
internal sealed class ClassicStageBackdropArt : Component
{
    public sealed record Props(string Url);

    string _shown = "", _previous = "";

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Url != _shown) { _previous = _shown; _shown = p.Url; }
        var kids = new List<Element>(2);
        if (_previous.Length > 0) kids.Add(Cover(_previous) with { Key = "bd:" + _previous });
        if (_shown.Length > 0) kids.Add(Cover(_shown) with { Key = "bd:" + _shown });
        return Stage.Layer with { HitTestVisible = false, HitTestPassThrough = false, Children = kids.ToArray() };   // full height (Grow), so the stretched art fills the stage
    }

    static ImageEl Cover(string url) => Ui.Image(url, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f, placeholder: Ink.ArtStandIn(url),
                                                  transition: ImageTransition.Fade(Stage.Tone.CrossFadeMs)) with
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, BakedBlur = new BakedBlurSpec(80f, 0.5f),
    };
}

/// <summary>Wavee Classic's face layer: the chosen face, remounted per (kind, 8-DIP width bucket) on a KEYED CHILD (a Key on a component's
/// root is inert in a single-child slot — V-U11; the width is in the key so opening or closing the gallery re-centres the subject under the
/// same 0.55 s FluentDecelerate instead of jumping), inset on the right while the gallery is open. The face is told its SAFE rect
/// (Layout.FaceSafe: clear of the now-playing card, the caption when it shows, the transport). Reads the context's signals only (O8); the
/// live palette re-renders it once per landed fade, never per tick.</summary>
internal static class ClassicStageFace
{
    /// <summary>The registered template of <see cref="ViewKeys.StageFace"/>.</summary>
    public static Element Template(StageLayerProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var look = sc.Look.Value;
        bool backdrop = Stage.LayoutRules.FaceIsBackdrop(look.Eff);       // Large art / Centered: the face runs behind the cover and lyrics
        var kind = Stage.LayoutRules.BackdropKind(look.Eff, sc.Kind.Value);   // atmosphere only there: a now-playing face falls back to Bloom
        var pal = sc.Palette.Value;
        bool galleryOpen = sc.GalleryShown.Value;                  // open AND room AND the Card layout: the inset follows what is on screen
        float w = L.W - L.FaceRight(galleryOpen);
        bool caption = sc.CaptionOn.Value;                         // in Card + Visualizer mode exactly ModeRules.ShowsCaption(...)
        var safe = backdrop ? new Stage.FaceSafeRect(0f, 0f, w, L.H) : L.FaceSafe(galleryOpen, caption);   // a backdrop subject centres on the whole stage
        var track = sc.RowValue();
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        // the stage face is a scope reader while its kind draws the slab's scope series (Scope, Flow)
        Visualizer.UseScopeReader(ctx.Hooks, sc.Slab, Visualizer.Catalog.UsesScope(kind));
        return new BoxEl
        {
            Width = w, Height = L.H, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    // the width in the key is quantised to 8 DIP: the gallery's open/close still remounts (re-centres)
                    // the face, a window drag no longer rebuilds it (and its sims) on every pixel
                    Key = "viz:" + (int)kind + ":" + ((int)w >> 3), Width = w, Height = L.H,
                    Animate = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(550f, Easing.FluentDecelerate),
                        Enter: new EnterExit(Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
                    Children = [Visualizer.Face(kind, sc.Slab, in pal, new Visualizer.FaceSpec(w, L.H, Preview: false, CoverUrl: url.Length > 0 ? url : null,
                        SafeLeft: safe.X, SafeTop: safe.Y, SafeRight: safe.Right, SafeBottom: safe.Bottom, Ambient: Stage.LayoutRules.FaceAmbient(look.Eff)))],
                },
            ],
        };
    }
}

/// <summary>The scrim above the backdrop face (Large art, Centered): the stage veil at the core's <c>LayoutRules.BackdropScrim</c>. Reads the
/// look and the face kind only — it re-renders on a layout or face change, never per tick.</summary>
internal static class ClassicStageFaceScrim
{
    /// <summary>The registered template of <see cref="ViewKeys.StageFaceScrim"/>.</summary>
    public static Element Template(StageFaceScrimProps props, in ViewContext ctx)
        => Stage.Layer with { HitTestVisible = false, HitTestPassThrough = false, Fill = ClassicStageLayerKit.Shade(props.Alpha()), BrushTransitionMs = Stage.Tone.CrossFadeMs };
}

/// <summary>The layouts' spectrum: a component on the context's SIGNALS (Layout, Look) — it re-renders when the layout, the spectrum style or the
/// stage's size moves, never per tick. The strip itself is bound to the slab and lives in its own small RepaintBoundary, so a step re-rasters
/// only that rect. Nothing is mounted for Card (the face is the picture) or Spectrum Off; the box is keyed by (layout, style, 8-DIP width
/// bucket), so a style switch fades the old one out under the new and a drag does not rebuild it on every pixel.</summary>
internal static class ClassicStageSpectrum
{
    /// <summary>The registered template of <see cref="ViewKeys.StageSpectrum"/>.</summary>
    public static Element Template(StageLayerProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        var slab = sc.Slab;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        Element? body = null;
        float x = 0f, y = 0f, w = 0f, h = 0f;
        string key = "";
        if (k.IsLayout)
        {
            switch (k.Spectrum)
            {
                case Stage.SpectrumStyle.Bars:
                case Stage.SpectrumStyle.Line:
                {
                    (x, y, w, h) = L.StripRect(in k);
                    if (h <= 0f || w <= 0f) break;
                    body = k.Spectrum == Stage.SpectrumStyle.Bars ? Visualizer.StripBars(slab, w, h) : Visualizer.StripLine(slab, w, h, k.HeroLyrics);
                    key = "strip:" + (int)k.Eff + ":" + (int)k.Spectrum + ":" + (k.HeroLyrics ? "l" : "a") + ":" + ((int)w >> 3);
                    break;
                }
                case Stage.SpectrumStyle.Ring:
                {
                    var box = L.RingBox(in k);
                    if (box.Size <= 0f) break;
                    float cover = box.Size / Stage.Layout.RingBoxRatio;
                    (x, y, w, h) = (box.X, box.Y, box.Size, box.Size);
                    body = Visualizer.RingAround(slab, cover);
                    key = "ring:" + (int)k.Eff + ":" + ((int)box.Size >> 2);
                    break;
                }
            }
        }
        return Stage.Layer with
        {
            HitTestVisible = false, HitTestPassThrough = false,
            Children = body is null ? [] :
            [
                new BoxEl
                {
                    Key = key, Width = w, Height = h, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Margin = new Edges4(x, y, 0f, 0f), Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [body],
                },
            ],
        };
    }
}

/// <summary>The acrylic now-playing card behind the thumb (Visualizer mode): TOP-LEFT (AlignSelf Start · JustifySelf Start).</summary>
internal static class ClassicStageNowPlayingCard
{
    /// <summary>The registered template of <see cref="ViewKeys.StageNowPlayingCard"/>.</summary>
    public static Element Template(StageNowPlayingCardProps props, in ViewContext ctx) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestVisible = false,   // a ZStack, so the card's Align/Justify mean something
        Children =
        [
            Flow.Show(props.Shown, new BoxEl
            {
                Width = Stage.Layout.NowPlayingCardW, Height = Stage.Layout.NowPlayingCardH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(Stage.Layout.NowPlayingCardX, Stage.Layout.NowPlayingCardY, 0f, 0f), Corners = Radii.CardAll,
                Acrylic = Ink.Card, Shadow = Elevation.Card, HitTestVisible = false,
                Enter = new EnterExit(Dy: ClassicStageLayerGeometry.CardEnterDy, Opacity: 0f, Active: true, DelayMs: ClassicStageLayerGeometry.CardEnterDelayMs),
                Exit = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
            }),
        ],
    };
}
