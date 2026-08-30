using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Which ground a palette treatment paints. The two recipes differ only in their radial anchors, radii and
/// how far the base is pulled toward the tinted swatch — the prototype authored them separately
/// (<c>.cv-marq .ground</c> / <c>.cv-stack .ground</c>) and they read differently, so both numbers ship.</summary>
enum LikedGroundKind : byte { Marquee, Stack }

/// <summary>The Liked cover's PALETTE LEAVES: the nodes that subscribe to <c>CoverColorPlane.Watch</c>.
///
/// <para>The discipline is <c>CoverPaletteLeaves</c>'s, and it is load-bearing rather than stylistic: a grading
/// arriving for one of sixteen covers must repaint ONE node, not rebuild sixteen <c>ImageEl</c>s and their decode
/// requests. So <see cref="LikedCoverArt"/> and <see cref="LikedCoverTreatments"/> never touch <c>Watch</c> — they
/// mount these, and these own the subscription. <c>CoverPageTonePlane</c> is the template.</para>
///
/// <para>All three paint IMMEDIATELY from whatever the plane already has and upgrade in place when it learns more
/// (E6): a miss inside <c>TryGetScheme</c> self-enqueues the cover for grading, and the <c>Watch</c> read above is
/// what turns that arrival into a repaint. Nothing here ever waits on a colour to draw.</para></summary>
static class LikedCoverLeaves
{
    /// <summary>Marquee's / Stack's ground: two radial washes from the two newest gradings over a tinted near-black
    /// base. Keyed so a style switch between the two recipes remounts rather than cross-fading two different
    /// geometries through one node.</summary>
    internal static Element PaletteGround(string? urlA, string? urlB, string? urlBase, LikedGroundKind kind)
        => Embed.Comp(new LikedPaletteGround.Props(urlA, urlB, urlBase, kind), static () => new LikedPaletteGround())
            with { Key = "liked-ground:" + kind };

    /// <summary>Lens's veil — the one palette layer in a treatment whose ground is ART, so it tints rather than
    /// paints: a single translucent diagonal wash between the two newest gradings, over the blurred mosaic.</summary>
    internal static Element LensVeil(string? urlA, string? urlB)
        => Embed.Comp(new LikedLensVeil.Props(urlA, urlB), static () => new LikedLensVeil()) with { Key = "liked-lens-veil" };

    /// <summary>Tone's whole face — no art tiles at all, five radial spots graded from the five newest covers.</summary>
    internal static Element ToneGround(IReadOnlyList<string> tiles)
        => Embed.Comp(new LikedToneGround.Props(At(tiles, 0), At(tiles, 1), At(tiles, 2), At(tiles, 3), At(tiles, 4)),
                      static () => new LikedToneGround()) with { Key = "liked-tone" };

    /// <summary>Rainbow's 4x4, whose ORDER is a function of the gradings — so the grid itself has to be the leaf.</summary>
    internal static Element RainbowGrid(string tileKey, string[] cells, float edge, float gap, int decodePx)
        => Embed.Comp(new LikedRainbowGrid.Props(tileKey, cells, edge, gap, decodePx),
                      static () => new LikedRainbowGrid()) with { Key = "liked-rainbow" };

    /// <summary>Cycle rather than run out: Tone's minimum is ONE liked cover, and five spots all graded from that one
    /// cover is a legitimate (monochrome) tone, where four empty spots would be a hole.</summary>
    static string? At(IReadOnlyList<string> tiles, int i)
        => tiles.Count == 0 ? null : tiles[i % tiles.Count];

    /// <summary>The chroma a cover contributes to a treatment ground.
    ///
    /// <para>Deliberately the DARK grading in BOTH themes, unlike <c>Surfaces.SchemeFor</c> (which follows the theme).
    /// A cover treatment is IMAGERY: it carries on-media ink over a dark plate in light theme exactly as it does in
    /// dark, so it wants the provider's dark-half chroma (median HSV S ~0.73) in both — the light half would wash the
    /// wash out on a light page while the ink over it stayed white. Falls back to whichever half exists.</para></summary>
    internal static ColorF? Chroma(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var plane = SpotifyLive.CoverColorPlane.Current;
        var s = plane.TryGetScheme(url, lightTheme: false) ?? plane.TryGetScheme(url, lightTheme: true);
        return s is { } scheme ? WaveePalette.Vivid(WaveePalette.Accent(scheme)) : null;
    }

    /// <summary>The base plate's tint — the tinted-dark background role, not the accent, so the plate stays a plate.</summary>
    internal static ColorF? BaseTint(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var plane = SpotifyLive.CoverColorPlane.Current;
        var s = plane.TryGetScheme(url, lightTheme: false) ?? plane.TryGetScheme(url, lightTheme: true);
        return s is { } scheme ? WaveePalette.TintedDark(scheme) : null;
    }

    /// <summary>Subscribe this leaf to a cover's grading. Hoisted out of every paint closure for
    /// <c>CoverPageTonePlane</c>'s reason: <c>Watch</c> takes the plane's lock.</summary>
    internal static void Subscribe(string? url)
    {
        if (url is { Length: > 0 }) _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
    }
}

/// <summary>Marquee's and Stack's ground: <c>radial(A) + radial(B) + tinted near-black base</c>, the
/// <c>HomeFoldTile</c> ZStack-of-radial-boxes idiom (the engine has one gradient per node, and a <c>GradientSpec</c>
/// caps at four stops — so "three backgrounds" is three nodes, exactly as the prototype's three CSS layers are).</summary>
sealed class LikedPaletteGround : Component
{
    internal sealed record Props(string? UrlA, string? UrlB, string? UrlBase, LikedGroundKind Kind);

    // The prototype's own near-blacks (`color-mix(... var(--t5) 40%, #17171c)` / `45%, #1b1b20`).
    static readonly ColorF MarqueeBase = ColorF.FromRgba(0x17, 0x17, 0x1C);
    static readonly ColorF StackBase = ColorF.FromRgba(0x1B, 0x1B, 0x20);

    public override Element Render()
    {
        var p = UseProps<Props>();
        LikedCoverLeaves.Subscribe(p.UrlA);
        LikedCoverLeaves.Subscribe(p.UrlB);
        LikedCoverLeaves.Subscribe(p.UrlBase);

        bool marquee = p.Kind == LikedGroundKind.Marquee;
        ColorF plate = marquee ? MarqueeBase : StackBase;
        // Ungraded ⇒ the plate alone. That IS the neutral fallback (E6): a dark plate, immediately, that gains its
        // chroma the moment the plane answers — never a white flash and never a wait.
        if (LikedCoverLeaves.BaseTint(p.UrlBase) is { } tint)
            plate = ColorF.Lerp(plate, tint, marquee ? 0.40f : 0.45f);

        var kids = new List<Element>(2);
        if (LikedCoverLeaves.Chroma(p.UrlA) is { } a)
            kids.Add(Wash(a, marquee ? 0.88f : 0.85f,
                          marquee ? new Point2(0.15f, 0.12f) : new Point2(0.20f, 0.10f),
                          marquee ? new Point2(1.184f, 0.855f) : new Point2(1.250f, 0.855f),
                          marquee ? 0.72f : 0.70f));
        if (LikedCoverLeaves.Chroma(p.UrlB) is { } b)
            kids.Add(Wash(b, marquee ? 0.82f : 0.80f,
                          marquee ? new Point2(0.88f, 0.88f) : new Point2(0.90f, 0.90f),
                          marquee ? new Point2(1.250f, 0.987f) : new Point2(1.184f, 0.921f),
                          marquee ? 0.72f : 0.70f));

        return new BoxEl
        {
            ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
            HitTestVisible = false, Fill = plate,
            // BOUND-free on purpose: this leaf re-renders on the Watch edge anyway, and a bound brush cannot carry the
            // two radial CHILDREN appearing, which is what a first grading actually changes here.
            Children = kids.ToArray(),
        };
    }

    static Element Wash(ColorF c, float alpha, Point2 centre, Point2 radius, float falloff) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
        [
            new GradientStop(0f, c with { A = alpha }),
            new GradientStop(falloff, c with { A = 0f }),
        ])
        { RadialCenter = centre, RadialRadius = radius },
    };
}

/// <summary>Lens's veil: <c>linear-gradient(160deg, t1 45%, t2 38%)</c> at <c>opacity:.55</c>, one node, over the
/// blurred mosaic and under the crisp window.
///
/// <para>ENGINE ADAPTATION, stated rather than hidden: the prototype composites this layer with
/// <c>mix-blend-mode: multiply</c>, which the renderer does not have (it blends premultiplied source-over). A
/// source-over tint at the same effective alphas lifts the ground slightly where multiply would deepen it — but the
/// ground it lands on has already been taken to 62 % brightness by the treatment's own dim, so the layer still reads
/// as a colour cast over a dark plate rather than a wash on top of one.</para>
///
/// <para>E6: ungraded, the veil is the app's neutral page-tone dark at the same alphas — a calm, immediate dimming
/// that GAINS its chroma when the plane answers. Nothing here ever waits on a colour to paint.</para></summary>
sealed class LikedLensVeil : Component
{
    internal sealed record Props(string? UrlA, string? UrlB);

    // CSS angles run clockwise from "to top"; the engine's run 0 = left-to-right, 90 = top-to-bottom. 160 - 90 = 70.
    const float Angle = 70f;
    // The CSS colour-mix percentages folded with the layer's own opacity, once, here — rather than an Opacity on the
    // node, which would cost a group for two stops.
    const float AlphaA = 0.45f * 0.55f, AlphaB = 0.38f * 0.55f;

    public override Element Render()
    {
        var p = UseProps<Props>();
        LikedCoverLeaves.Subscribe(p.UrlA);
        LikedCoverLeaves.Subscribe(p.UrlB);

        ColorF a = LikedCoverLeaves.Chroma(p.UrlA) ?? WaveePalette.PageToneNeutralDark;
        // One grading is enough for a veil: the second stop reuses the first rather than mixing a graded colour into a
        // neutral, which would read as a gradient that stops half way.
        ColorF b = LikedCoverLeaves.Chroma(p.UrlB) ?? a;

        return new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
            Gradient = LinearGradient(Angle,
                new GradientStop(0f, a with { A = AlphaA }),
                new GradientStop(1f, b with { A = AlphaB })),
        };
    }
}

/// <summary>Tone's face: a base graded from the newest cover, five radial spots at the prototype's anchors, and the
/// top-to-bottom vignette.
///
/// <para>ENGINE ADAPTATION, stated rather than hidden: the prototype composites its five spots with canvas
/// <c>globalCompositeOperation = 'lighter'</c> (additive). The renderer blends premultiplied source-over and has no
/// additive mode, so each spot is drawn at a reduced alpha (~0.55 rather than the prototype's 0.75–0.9) — the stack
/// then reads as the same soft multi-source glow instead of five flat overlapping discs. The prototype's film grain
/// has no engine equivalent at all (no noise shader hook, no from-pixels image API) and is simply absent.</para></summary>
sealed class LikedToneGround : Component
{
    internal sealed record Props(string? U0, string? U1, string? U2, string? U3, string? U4);

    // `spots` in the prototype's paintTone(): x, y, and the radius factor that its `rad*W*.62` scales.
    static readonly Point2[] Centres =
    [
        new(0.18f, 0.20f), new(0.82f, 0.25f), new(0.25f, 0.85f), new(0.80f, 0.80f), new(0.50f, 0.50f),
    ];
    static readonly float[] Radii5 = [0.9f, 0.8f, 0.85f, 0.75f, 0.6f];
    const float RadiusScale = 0.62f;
    const float SpotAlpha = 0.55f;   // see the 'lighter' note above

    public override Element Render()
    {
        var p = UseProps<Props>();
        Span<string?> urls = [p.U0, p.U1, p.U2, p.U3, p.U4];
        for (int i = 0; i < urls.Length; i++) LikedCoverLeaves.Subscribe(urls[i]);

        // The base is the newest like's own colour; with nothing graded yet it is the app's neutral page-tone dark, so
        // Tone is a calm dark plate on its first frame rather than a white square (E6).
        ColorF plate = LikedCoverLeaves.Chroma(p.U0) is { } lead
            ? ColorF.Lerp(WaveePalette.PageToneNeutralDark, lead, 0.55f)
            : WaveePalette.PageToneNeutralDark;

        var kids = new List<Element>(6);
        for (int i = 0; i < urls.Length; i++)
        {
            if (LikedCoverLeaves.Chroma(urls[i]) is not { } c) continue;
            kids.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [
                    new GradientStop(0f, c with { A = SpotAlpha }),
                    new GradientStop(1f, c with { A = 0f }),
                ])
                {
                    RadialCenter = Centres[i],
                    RadialRadius = new Point2(Radii5[i] * RadiusScale, Radii5[i] * RadiusScale),
                },
            });
        }

        // The prototype's closing linear pass: a white lift at the top, black at the foot, nothing in the middle.
        kids.Add(new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, ColorF.FromRgba(255, 255, 255) with { A = 0.10f }),
                new GradientStop(0.6f, ColorF.FromRgba(0, 0, 0) with { A = 0f }),
                new GradientStop(1f, ColorF.FromRgba(0, 0, 0) with { A = 0.28f })),
        });

        return new BoxEl
        {
            ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
            HitTestVisible = false, Fill = plate, Children = kids.ToArray(),
        };
    }
}

/// <summary>Rainbow's grid. It is a leaf and not a static builder because its ORDER — not merely its paint — depends
/// on the gradings: a cover that grades late does not just change colour, it MOVES into the hue ramp. Watching the
/// sixteen urls here means that reshuffle repaints one node while the rest of the cover, and the page around it, are
/// untouched. Cells are positional (as every mosaic in the app is) so a reorder swaps two already-decoded textures
/// rather than re-requesting them.</summary>
sealed class LikedRainbowGrid : Component
{
    /// <summary>The cells are an ARRAY, which records compare by reference — so a re-push of the identical content in
    /// a fresh array would re-render this leaf (and its sixteen Watch reads) every time the parent renders. Equality is
    /// therefore declared over <paramref name="TileKey"/>, the caller's content key, and the scalar geometry.</summary>
    internal sealed record Props(string TileKey, string[] Cells, float Edge, float Gap, int DecodePx)
    {
        public bool Equals(Props? other)
            => other is not null
               && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal)
               && Edge.Equals(other.Edge) && Gap.Equals(other.Gap) && DecodePx == other.DecodePx;

        public override int GetHashCode() => HashCode.Combine(TileKey, Edge, Gap, DecodePx);
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        var cells = p.Cells;
        for (int i = 0; i < cells.Length; i++) LikedCoverLeaves.Subscribe(cells[i]);

        // The chroma-carrying swatch, not the contrast ink: BackgroundTintedBase is the role that actually says what
        // colour a sleeve IS (TextBrightAccent is white on most covers and would put the whole grid in one bucket).
        var hues = new float?[cells.Length];
        var plane = SpotifyLive.CoverColorPlane.Current;
        for (int i = 0; i < cells.Length; i++)
        {
            var s = plane.TryGetScheme(cells[i], lightTheme: false) ?? plane.TryGetScheme(cells[i], lightTheme: true);
            hues[i] = s is { } scheme ? LikedCoverRules.HueOf(scheme.BackgroundTintedBase) : null;
        }

        int[] order = LikedCoverRules.RainbowOrder(hues);
        var kids = new Element[order.Length];
        for (int i = 0; i < order.Length; i++)
            kids[i] = LikedCoverTreatments.Cell(cells[order[i]], p.Edge, 0f, p.DecodePx);

        return LikedCoverTreatments.Grid(LikedCoverRules.RainbowColumns, kids, p.Edge, p.Gap);
    }
}
