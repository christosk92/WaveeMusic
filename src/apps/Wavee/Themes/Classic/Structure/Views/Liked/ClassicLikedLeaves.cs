// ── Themes/Classic/Structure/Views/Liked/ClassicLikedLeaves.cs — the cover's palette leaves (themes plan 6.1, T9c) ─────────────────────
//
// From `Entities\User.Cover.cs`: THE LEAF DISCIPLINE. A grading landing for one of sixteen covers repaints ONE node: only these four
// leaves (the Marquee / Stack ground, Lens's veil, Tone's face and Rainbow's grid) read `Palette.Watch`; the cover view and the
// treatments never do. Every leaf paints its neutral fallback immediately and upgrades in place (E6): nothing waits on a colour.
// Also the bundled stock cover.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Which two-wash ground a Marquee or Stack cover paints.</summary>
internal enum ClassicLikedGroundKind : byte { Marquee, Stack }

internal static class ClassicLikedLeaves
{
    /// <summary>The bundled Spotify stock cover: correct offline, never a render-time request. No reveal transition: a
    /// fade on the DEGRADE target is what reads as a flash (G8, M-23).</summary>
    public static Element StockCover(float size, float radius, string? morphKey)
        => Image(s_stockCoverPath, size, size, radius, R.Layer.ArtPlaceholder, transition: ImageTransition.None)
            with { MorphId = morphKey };

    static readonly string s_stockCoverPath = Path.Combine(AppContext.BaseDirectory, "assets", "covers", "liked-songs-300.png");


    internal static Element PaletteGround(string? urlA, string? urlB, string? urlBase, ClassicLikedGroundKind kind)
        => Embed.Comp(new GroundProps(urlA, urlB, urlBase, kind), static () => new LikedPaletteGround()) with { Key = "liked-ground:" + kind };

    internal static Element LensVeil(string? urlA, string? urlB)
        => Embed.Comp(new VeilProps(urlA, urlB), static () => new LikedLensVeil()) with { Key = "liked-lens-veil" };

    /// <summary>Tone's five spots CYCLE the tiles: one liked cover is a legal monochrome tone, four empty spots a hole.</summary>
    internal static Element ToneGround(string[] tiles)
        => Embed.Comp(new ToneProps(Cycle(tiles, 0), Cycle(tiles, 1), Cycle(tiles, 2), Cycle(tiles, 3), Cycle(tiles, 4)),
                      static () => new LikedToneGround()) with { Key = "liked-tone" };

    internal static Element RainbowGrid(string tileKey, string[] cells, float edge, float gap, int decodePx)
        => Embed.Comp(new RainbowProps(tileKey, cells, edge, gap, decodePx), static () => new LikedRainbowGrid()) with { Key = "liked-rainbow" };

    static string? Cycle(string[] tiles, int i) => tiles.Length == 0 ? null : tiles[i % tiles.Length];

    /// <summary>A cover's graded scheme — the DARK half in BOTH themes (a cover treatment is imagery carrying on-media
    /// ink over a dark plate in light theme too), falling back to whichever half exists.</summary>
    static bool TryDarkScheme(string? url, out Scheme scheme)
    {
        scheme = default;
        if (string.IsNullOrEmpty(url)) return false;
        return (Wavee.Palette.TryScheme(url, lightTheme: false, out scheme) || Wavee.Palette.TryScheme(url, lightTheme: true, out scheme))
               && !scheme.IsEmpty;
    }

    /// <summary>The chroma a cover contributes to a ground: <c>Vivid(Accent(scheme))</c>.</summary>
    static ColorF? LikedChroma(string? url)
        => TryDarkScheme(url, out var s) ? ColorScience.Vivid(ColorScience.Accent(in s)) : null;

    /// <summary>The ground plate's tint — the tinted-dark background role, so the plate stays a plate.</summary>
    static ColorF? LikedBaseTint(string? url)
        => TryDarkScheme(url, out var s) ? ColorScience.TintedDark(in s) : null;

    /// <summary>Subscribe the calling leaf to a cover's grading.</summary>
    static void WatchCover(string? url)
    {
        if (url is { Length: > 0 }) _ = Wavee.Palette.Watch(url).Value;
    }

    sealed record GroundProps(string? UrlA, string? UrlB, string? UrlBase, ClassicLikedGroundKind Kind);
    sealed record VeilProps(string? UrlA, string? UrlB);
    sealed record ToneProps(string? U0, string? U1, string? U2, string? U3, string? U4);

    /// <summary>The cells are an ARRAY (reference equality), so equality is declared over the caller's content key and
    /// the scalar geometry — a fresh-but-equal re-push must not re-render sixteen Watch reads (ch 07 §9).</summary>
    sealed record RainbowProps(string TileKey, string[] Cells, float Edge, float Gap, int DecodePx)
    {
        public bool Equals(RainbowProps? other)
            => other is not null && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal)
               && Edge.Equals(other.Edge) && Gap.Equals(other.Gap) && DecodePx == other.DecodePx;

        public override int GetHashCode() => HashCode.Combine(TileKey, Edge, Gap, DecodePx);
    }

    /// <summary>Marquee's / Stack's ground: two radial washes over a tinted near-black plate — three nodes, because the
    /// engine has one gradient per node (and four stops per gradient).</summary>
    sealed class LikedPaletteGround : Component
    {
        static ColorF MarqueeBase => ClassicLikedRules.MarqueeBase;
        static ColorF StackBase => ClassicLikedRules.StackBase;

        public override Element Render()
        {
            var p = UseProps<GroundProps>();
            WatchCover(p.UrlA); WatchCover(p.UrlB); WatchCover(p.UrlBase);

            bool marquee = p.Kind == ClassicLikedGroundKind.Marquee;
            ColorF plate = marquee ? MarqueeBase : StackBase;
            // Ungraded ⇒ the plate alone: a dark plate immediately, gaining chroma when the palette answers (E6).
            if (LikedBaseTint(p.UrlBase) is { } tint) plate = ColorF.Lerp(plate, tint, marquee ? 0.40f : 0.45f);

            Element a = LikedChroma(p.UrlA) is { } ca
                ? Wash(ca, marquee ? 0.88f : 0.85f, marquee ? new Point2(0.15f, 0.12f) : new Point2(0.20f, 0.10f),
                       marquee ? new Point2(1.184f, 0.855f) : new Point2(1.250f, 0.855f), marquee ? 0.72f : 0.70f)
                : new BoxEl { HitTestVisible = false };
            Element b = LikedChroma(p.UrlB) is { } cb
                ? Wash(cb, marquee ? 0.82f : 0.80f, marquee ? new Point2(0.88f, 0.88f) : new Point2(0.90f, 0.90f),
                       marquee ? new Point2(1.250f, 0.987f) : new Point2(1.184f, 0.921f), marquee ? 0.72f : 0.70f)
                : new BoxEl { HitTestVisible = false };
            return new BoxEl
            {
                ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = plate,
                Children = [a, b],
            };
        }

        static Element Wash(ColorF c, float alpha, Point2 centre, Point2 radius, float falloff) => new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
            Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = alpha }), new GradientStop(falloff, c with { A = 0f })])
            { RadialCenter = centre, RadialRadius = radius },
        };
    }

    /// <summary>Lens's veil: <c>linear-gradient(160deg, t1 45%, t2 38%)</c> at opacity .55, folded into two alphas.
    /// ENGINE ADAPTATION: no multiply blend — source-over at the folded alphas over a ground already at 62 % (§4.4 #1).
    /// ONE grading is enough: stop 1 reuses stop 0's colour (item 72); only with neither graded is it the neutral.</summary>
    sealed class LikedLensVeil : Component
    {
        const float Angle = 70f;   // CSS 160° − 90°
        const float AlphaA = 0.45f * 0.55f, AlphaB = 0.38f * 0.55f;

        public override Element Render()
        {
            var p = UseProps<VeilProps>();
            WatchCover(p.UrlA); WatchCover(p.UrlB);
            ColorF a = LikedChroma(p.UrlA) ?? ColorScience.PageToneNeutralDark;
            ColorF b = LikedChroma(p.UrlB) ?? a;
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = LinearGradient(Angle, new GradientStop(0f, a with { A = AlphaA }), new GradientStop(1f, b with { A = AlphaB })),
            };
        }
    }

    /// <summary>Tone's face: a plate graded from the newest cover, five radial spots, the closing vignette. ENGINE
    /// ADAPTATION: no additive blend (spots are source-over at α .55) and no film grain (§4.4 #2, #3). A spot exists only
    /// for a GRADED tile, so an ungraded cover is the plate + vignette alone.</summary>
    sealed class LikedToneGround : Component
    {
        static readonly Point2[] Centres = [new(0.18f, 0.20f), new(0.82f, 0.25f), new(0.25f, 0.85f), new(0.80f, 0.80f), new(0.50f, 0.50f)];
        static readonly float[] RadiusFactors = [0.9f, 0.8f, 0.85f, 0.75f, 0.6f];
        const float RadiusScale = 0.62f;
        const float SpotAlpha = 0.55f;
        static readonly Element Empty = new BoxEl { HitTestVisible = false };

        public override Element Render()
        {
            var p = UseProps<ToneProps>();
            Span<string?> urls = [p.U0, p.U1, p.U2, p.U3, p.U4];
            for (int i = 0; i < urls.Length; i++) WatchCover(urls[i]);

            // The base is the app's neutral page-tone dark — Tone's plate and the page ground under it are the same token.
            ColorF plate = LikedChroma(p.U0) is { } lead
                ? ColorF.Lerp(ColorScience.PageToneNeutralDark, lead, 0.55f)
                : ColorScience.PageToneNeutralDark;

            var kids = new Element[urls.Length + 1];
            for (int i = 0; i < urls.Length; i++)
            {
                float r = RadiusFactors[i] * RadiusScale;
                kids[i] = LikedChroma(urls[i]) is { } c
                    ? new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = SpotAlpha }), new GradientStop(1f, c with { A = 0f })])
                        { RadialCenter = Centres[i], RadialRadius = new Point2(r, r) },
                    }
                    : Empty;
            }
            kids[urls.Length] = new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = GradientDown(
                    new GradientStop(0f, R.OnMedia.Ink with { A = 0.10f }),
                    new GradientStop(0.6f, R.OnMedia.ScrimRest with { A = 0f }),
                    new GradientStop(1f, R.OnMedia.ScrimRest with { A = 0.28f })),
            };
            return new BoxEl { ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = plate, Children = kids };
        }
    }

    /// <summary>Rainbow's grid: a late grading does not just change a colour, it MOVES a tile into the hue ramp. Cells are
    /// positional, so a reorder swaps already-decoded textures rather than re-requesting them. The hue is read from
    /// <c>BackgroundTintedBase</c> — the chroma role, never <c>TextBrightAccent</c> (white on every dark half).</summary>
    sealed class LikedRainbowGrid : Component
    {
        float?[] _hues = [];

        public override Element Render()
        {
            var p = UseProps<RainbowProps>();
            var cells = p.Cells;
            for (int i = 0; i < cells.Length; i++) WatchCover(cells[i]);
            if (_hues.Length != cells.Length) _hues = new float?[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                _hues[i] = TryDarkScheme(cells[i], out var s) ? LikedCoverRules.HueOf(s.BackgroundTintedBase) : null;

            int[] order = LikedCoverRules.RainbowOrder(_hues);
            var kids = new Element[order.Length];
            for (int i = 0; i < order.Length; i++) kids[i] = ClassicLikedTreatments.ArtCell(cells[order[i]], p.Edge, 0f, p.DecodePx);
            return ClassicLikedTreatments.TileGridOf(LikedCoverRules.RainbowColumns, kids, p.Edge, p.Gap);
        }
    }
}
