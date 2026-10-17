// ── Themes/Zune/Structure/Views/Stage/ZuneStageLayers.cs — Zune's backdrop, face host, scrim, spectrum and card (P5 Z4b) ─────────────
//
// WHICH look is drawn is the core's (the layout, the spectrum, the face); each template reads the stage context's signals in its own render
// and draws Zune's picture:
//
//   Stage.Backdrop         the flat stage floor (Z-Lyrics / Z-Queue-Page draw a flat ground, no blurred cover) with the palette's base Field
//                          breathing on it and the core's scrim; ONE quarter-scale repaint boundary
//   Stage.Face             the face host: the chosen face (core, owner Q21; Bloom by default, NQ3), remounted per (kind, 8-DIP width bucket),
//                          told its safe rect for the arrangement (the Card arrangement keeps the subject above the card)
//   Stage.FaceScrim        the stage veil over a backdrop face (Large art, Centered)
//   Stage.Spectrum         the strip / ring the layouts draw: under the Large art column's cover (or ringing it), the allocator's elsewhere
//   Stage.NowPlayingCard   the Card arrangement's well plate (600 x 376, square, flat), or the allocator's 460 card as a flat veil plate

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>What the stage's layer templates share.</summary>
internal static class ZuneStageKit
{
    /// <summary>The stage veil at alpha <paramref name="a"/>.</summary>
    public static ColorF Shade(float a) => Ink.Veil with { A = a };

    /// <summary>The arrangement this render draws in (the same signals every stage view reads).</summary>
    public static ZuneStageArrangement Arrangement(Stage.StageCtx sc, in TierFacts tiers)
    {
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        return G.Arrange(in L, in k, ZuneStageTiers.StageOf(tiers), k.Mode == Stage.Mode.Visualizer && sc.GalleryShown.Value);
    }
}

/// <summary>Zune's stage backdrop: the flat floor, the base Field (the palette's roles, cross-fading with it) and the scrim.</summary>
internal static class ZuneStageBackdrop
{
    /// <summary>The registered template of <see cref="ViewKeys.StageBackdrop"/>.</summary>
    public static Element Template(StageBackdropProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var mode = sc.Mode.Value;
        var kind = sc.Kind.Value;
        var eff = sc.Look.Value.Eff;
        return Stage.Layer with
        {
            // soft or flat, so it rasters as one small surface and composites as one quad
            ClipToBounds = true, HitTestVisible = false, HitTestPassThrough = false, Fill = Ink.Floor,
            RepaintBoundary = true, RasterScale = 0.25f,
            Visible = Prop.Of(props.Shown),
            Children =
            [
                Visualizer.BackdropField(sc.Slab, L.W, L.H, Prop.Bind(sc.Slab.BaseFieldOp), boundary: false) with { Key = "stage:field" },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Fill = ZuneStageKit.Shade(Stage.LayoutRules.ScrimFor(mode, eff, kind)), BrushTransitionMs = Stage.Tone.CrossFadeMs,
                },
            ],
        };
    }
}

/// <summary>Zune's face host: the chosen face (core), remounted per (kind, 8-DIP width bucket) on a keyed child, inset while the gallery is open,
/// told its safe rect for the arrangement.</summary>
internal static class ZuneStageFace
{
    /// <summary>The registered template of <see cref="ViewKeys.StageFace"/>.</summary>
    public static Element Template(StageLayerProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var look = sc.Look.Value;
        bool backdrop = Stage.LayoutRules.FaceIsBackdrop(look.Eff);
        var kind = Stage.LayoutRules.BackdropKind(look.Eff, sc.Kind.Value);
        var pal = sc.Palette.Value;
        bool galleryOpen = sc.GalleryShown.Value;
        float w = L.W - L.FaceRight(galleryOpen);
        bool caption = sc.CaptionOn.Value;
        var tiers = ctx.Tier();
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        var safe = G.FaceSafe(in L, in a, galleryOpen, caption, backdrop, ZuneStageTiers.StageOf(in tiers).HeadH);
        var track = sc.RowValue();
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        Visualizer.UseScopeReader(ctx.Hooks, sc.Slab, Visualizer.Catalog.UsesScope(kind));
        return new BoxEl
        {
            Width = w, Height = L.H, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Key = "viz:" + (int)kind + ":" + ((int)w >> 3), Width = w, Height = L.H,
                    Animate = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(G.GalleryYieldMs, Easing.FluentDecelerate),
                        Enter: new EnterExit(Sx: G.FaceEnterScale, Sy: G.FaceEnterScale, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
                    Children = [Visualizer.Face(kind, sc.Slab, in pal, new Visualizer.FaceSpec(w, L.H, Preview: false, CoverUrl: url.Length > 0 ? url : null,
                        SafeLeft: safe.X, SafeTop: safe.Y, SafeRight: safe.Right, SafeBottom: safe.Bottom, Ambient: Stage.LayoutRules.FaceAmbient(look.Eff)))],
                },
            ],
        };
    }
}

/// <summary>The scrim above a backdrop face (Large art, Centered): the stage veil at the core's per-face depth.</summary>
internal static class ZuneStageFaceScrim
{
    /// <summary>The registered template of <see cref="ViewKeys.StageFaceScrim"/>.</summary>
    public static Element Template(StageFaceScrimProps props, in ViewContext ctx)
        => Stage.Layer with { HitTestVisible = false, HitTestPassThrough = false, Fill = ZuneStageKit.Shade(props.Alpha()), BrushTransitionMs = Stage.Tone.CrossFadeMs };
}

/// <summary>The layouts' spectrum: under (or around) the Large art column's cover, the allocator's strip / ring elsewhere, the bars under a Huge
/// 4K Lyrics stage's cover where the core draws them. Keyed by (layout, style, 8-DIP width bucket): a style switch fades the old one out.</summary>
internal static class ZuneStageSpectrum
{
    /// <summary>The registered template of <see cref="ViewKeys.StageSpectrum"/>.</summary>
    public static Element Template(StageLayerProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        var slab = sc.Slab;
        var a = ZuneStageKit.Arrangement(sc, ctx.Tier());
        var fade = new EnterExit(Opacity: 0f, Active: true);
        Element? body = null;
        float x = 0f, y = 0f, w = 0f, h = 0f;
        string key = "";
        if (a.Form == ZuneStageForm.Centered && k.Spectrum == Stage.SpectrumStyle.Ring && !a.Spectrum.IsEmpty)
        {
            // the Centered arrangement's ring around its own cover (the strips stay the allocator's: they sit above its plate line)
            (x, y, w, h) = (a.Spectrum.X, a.Spectrum.Y, a.Spectrum.W, a.Spectrum.H);
            body = Visualizer.RingAround(slab, a.Cover.W);
            key = "zcring:" + ((int)w >> 2);
        }
        else if (a.Form == ZuneStageForm.Column)
        {
            if (!a.Spectrum.IsEmpty)
            {
                (x, y, w, h) = (a.Spectrum.X, a.Spectrum.Y, a.Spectrum.W, a.Spectrum.H);
                switch (k.Spectrum)
                {
                    case Stage.SpectrumStyle.Ring:
                        body = Visualizer.RingAround(slab, a.Cover.W);
                        key = "zring:" + ((int)w >> 2);
                        break;
                    case Stage.SpectrumStyle.Bars:
                        body = Visualizer.StripBars(slab, w, h);
                        key = "zstrip:b:" + ((int)w >> 3);
                        break;
                    case Stage.SpectrumStyle.Line:
                        body = Visualizer.StripLine(slab, w, h, k.HeroLyrics);
                        key = "zstrip:l:" + ((int)w >> 3);
                        break;
                }
            }
        }
        else if (!k.IsLayout && L.ArtBarsShown(k.Mode))
        {
            (x, y, w, h) = L.ArtBarsRect(k.Mode);
            body = Visualizer.StripBars(slab, w, h);
            key = "artbars:" + ((int)w >> 3);
        }
        else if (k.IsLayout)
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
                    (x, y, w, h) = (box.X, box.Y, box.Size, box.Size);
                    body = Visualizer.RingAround(slab, box.Size / Stage.Layout.RingBoxRatio);
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

/// <summary>The now-playing card: the Card arrangement's well plate (Z-NowPlaying-Card: #111, square, flat), or the allocator's 460 card (and a
/// Compact window's identity card) as a flat veil plate (Z-NowPlaying). ONE keyed node: a change of arrangement moves and resizes it on the
/// cover's spring, so the small card grows into the big one.</summary>
internal static class ZuneStageNowPlayingCard
{
    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds, TransitionDynamics.Spring(G.CoverResponse, G.CoverDamping), SizeMode.Relayout);

    /// <summary>The registered template of <see cref="ViewKeys.StageNowPlayingCard"/>.</summary>
    public static Element Template(StageNowPlayingCardProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var tiers = ctx.Tier();
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        bool card = a.Form == ZuneStageForm.Card;
        // the Stack draws no card (its cover and titles stand on the face); the allocator's card hangs under the head
        bool drawn = a.Form != ZuneStageForm.Stack;
        var L = sc.Layout.Value;
        var look = sc.Look.Value;
        var t = ZuneStageTiers.StageOf(in tiers);
        var (x, y, w, h) = card ? (a.Card.X, a.Card.Y, a.Card.W, a.Card.H) : L.NowPlayingCardRect(sc.Mode.Value);
        if (!card)
        {
            y += t.Drop;
            // the 460 card's edge sits on the tier's gutter (the Compact identity card runs full-bleed: its cover and titles sit on the gutter)
            if (!L.IdentityCardOn(sc.Mode.Value)) x += ZuneStageGeometry.EdgeShift(in L, in look, t);
        }
        var shown = props.Shown;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestVisible = false,
            Children =
            [
                Flow.Show(() => drawn && shown(), new BoxEl
                {
                    Key = "stage:npcard", Width = w, Height = h, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(x, y, 0f, 0f), Corners = R.Shape.All(ShapeRole.Card),
                    Fill = card ? R.Layer.PlayerBar : ZuneStageKit.Shade(G.PlateCardA), BrushTransitionMs = Stage.Tone.CrossFadeMs,
                    HitTestVisible = false, Layout = s_move,
                    Enter = new EnterExit(Dy: G.CardEnterDy, Opacity: 0f, Active: true, DelayMs: G.CardEnterDelayMs),
                    Exit = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                }),
            ],
        };
    }
}
