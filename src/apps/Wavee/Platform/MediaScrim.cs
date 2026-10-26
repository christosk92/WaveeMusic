// ── Platform/MediaScrim.cs — how dark the scrim under text on a photograph must be, from the photograph itself ─────────────────────
//
// Role: CORE (pure: no engine, no entity, no theme read; every input is an argument)
//
// TEXT ON A PHOTOGRAPH is light on-media ink (`R.OnMedia.Ink`) over a darkening veil, in every theme and kind. A fixed veil is tuned
// for an average photograph: on a bright one (a white wall, a sky) the ink does not read. So the veil is SOLVED per photograph, the
// WCAG G18 way: measure the photograph where the copy sits (a high percentile of its luminance over each line's band, so a few bright
// pixels count but one specular highlight does not), and find the least extra darkening that brings the ink to the ratio each line
// needs against that worst-case ground AFTER the veil is composited (4.5 : 1 for body text, 3 : 1 for large text).
//
// THE TWO LAYERS. A surface keeps its own static veil (the FLOOR: its board's gradient, untouched on a photograph that already reads)
// and lays a BOOST over it: black, clear at the top of the photograph and full from the top of the copy down, at an opacity this file
// solves and clamps to the surface's CEILING (the photograph must stay a photograph). Composited transmittance is the product
// (1 - floor) x (1 - boost). The blend is the compositor's: sRGB-encoded channels scaled by (1 - alpha), so the solve works on the
// sRGB-encoded grey that has each pixel's relative luminance (`LumaMap`).
//
// THE MAP is measured ONCE per decoded image, small (`MapEdge` on its long side), by `Palette.Tone` (Entities/Palette.Tone.cs). The
// fit is the engine's `ImageFit.Cover` of the whole photograph into the box (the decode keeps the source's aspect), so a box region
// maps to the centred crop (`CoverUv`).

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The scrim solve for text on a photograph. See the file header.</summary>
public static class MediaScrim
{
    /// <summary>The long side of a measured map, in pixels: fine enough that a bright band the size of a glyph counts.</summary>
    public const int MapEdge = 160;

    /// <summary>The percentile of a band's luminance the ink is judged against (0.9: the brightest tenth of the photograph under the line).</summary>
    public const float Percentile = 0.9f;

    /// <summary>The boost's fade when a measurement lands or the box changes (it never snaps).</summary>
    public const float FadeMs = 250f;

    /// <summary>Where a line's glyphs start below its line box's top, as a share of the font size (the cap height's lead under the ascent).</summary>
    public const float CapLead = 0.2f;

    /// <summary>The contrast body text needs, and large text (24 DIP and up, or bold 18.66) needs (WCAG AA).</summary>
    public const float NeedBody = 4.5f, NeedLarge = 3f;

    /// <summary>The headroom the solve keeps over each line's ratio: the map is a downsampled estimate and the compositor's blend is not exactly
    /// the sRGB scale the solve assumes, so it aims a little past the line it must clear.</summary>
    public const float Headroom = 1.15f;

    /// <summary>The ratio a line of <paramref name="size"/> DIP at <paramref name="weight"/> needs.</summary>
    public static float NeedFor(float size, int weight = 400) => size >= 24f || (size >= 18.66f && weight >= 700) ? NeedLarge : NeedBody;

    /// <summary>A measured photograph: its size and, per pixel, the sRGB-encoded grey of the pixel's relative luminance (0..1), row-major.</summary>
    public sealed record LumaMap(int Width, int Height, float[] Grey)
    {
        /// <summary>Width over height.</summary>
        public float Aspect => Width / (float)Math.Max(1, Height);
    }

    /// <summary>The static veil a surface paints: clear at <paramref name="Top"/> (box DIP), black at <paramref name="EdgeAlpha"/> at
    /// <paramref name="Top"/> + <paramref name="Height"/>, linear between.</summary>
    public readonly record struct Floor(float Top, float Height, float EdgeAlpha)
    {
        /// <summary>The floor's alpha at <paramref name="y"/> (box DIP).</summary>
        public float AlphaAt(float y) => EdgeAlpha * Math.Clamp((y - Top) / MathF.Max(1f, Height), 0f, 1f);
    }

    /// <summary>One line of copy: the box region its glyphs cover (DIP) and the ratio it needs.</summary>
    public readonly record struct Band(float Left, float Top, float Right, float Bottom, float Need)
    {
        /// <summary>The glyph zone of a line box at <paramref name="top"/>, <paramref name="lineHeight"/> tall, set at <paramref name="size"/>: from the cap
        /// top to the baseline's descent (the half-leading split above and below the em), between <paramref name="left"/> and <paramref name="right"/>.</summary>
        public static Band Line(float left, float right, float top, float lineHeight, float size, int weight = 400)
        {
            float lead = MathF.Max(0f, (lineHeight - size) / 2f);
            return new Band(left, top + lead + size * CapLead, right, top + lead + size, NeedFor(size, weight));
        }
    }

    // ── the map ──

    /// <summary>The map of a premultiplied BGRA8 image (opaque pixels: premultiplied equals straight).</summary>
    public static LumaMap Measure(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var grey = new float[width * height];
        for (int i = 0, p = 0; i < grey.Length; i++, p += 4)
        {
            float l = 0.2126f * Lin(bgra[p + 2] / 255f) + 0.7152f * Lin(bgra[p + 1] / 255f) + 0.0722f * Lin(bgra[p] / 255f);
            grey[i] = Enc(l);
        }
        return new LumaMap(width, height, grey);
    }

    /// <summary>Where a cover crop puts its focal point (`ImageEl.FocusX/Y` at `FocusFrameX/Y`); <c>default</c> is the centred crop.</summary>
    public readonly record struct Crop(float FocusX, float FocusY, float FrameX, float FrameY)
    {
        /// <summary>The centred crop.</summary>
        public static Crop Centred => new(0.5f, 0.5f, 0.5f, 0.5f);

        /// <summary>This crop, or the centred one for <c>default</c>.</summary>
        public Crop OrCentred => this == default ? Centred : this;
    }

    /// <summary>The image-space rectangle (0..1) a box region shows when a <paramref name="imageAspect"/> image is cover-fitted into a
    /// <paramref name="boxW"/> x <paramref name="boxH"/> box with its focal point (<paramref name="focusX"/>, <paramref name="focusY"/>) at the box point
    /// (<paramref name="frameX"/>, <paramref name="frameY"/>), clamped to cover: the engine's `ImageEl` crop (`FocusX/Y`, `FocusFrameX/Y`; the defaults centre it).</summary>
    public static (float U0, float V0, float U1, float V1) CoverUv(float imageAspect, float boxW, float boxH, float left, float top, float right, float bottom,
        float focusX = 0.5f, float focusY = 0.5f, float frameX = 0.5f, float frameY = 0.5f)
    {
        float boxAspect = boxW / MathF.Max(1f, boxH);
        float su = 1f, sv = 1f;
        if (imageAspect > boxAspect) su = boxAspect / imageAspect;   // wider photograph: its sides are cut
        else sv = imageAspect / boxAspect;                           // taller photograph: its top and foot are cut
        float u0 = Math.Clamp(focusX - su * frameX, 0f, 1f - su), v0 = Math.Clamp(focusY - sv * frameY, 0f, 1f - sv);
        float u(float x) => u0 + su * Math.Clamp(x / MathF.Max(1f, boxW), 0f, 1f);
        float v(float y) => v0 + sv * Math.Clamp(y / MathF.Max(1f, boxH), 0f, 1f);
        return (u(left), v(top), u(right), v(bottom));
    }

    /// <summary>The grey at <paramref name="percentile"/> of the map's pixels inside the image-space rectangle (at least one pixel).</summary>
    public static float GreyAt(LumaMap map, (float U0, float V0, float U1, float V1) uv, float percentile)
    {
        int x0 = Math.Clamp((int)MathF.Floor(uv.U0 * map.Width), 0, map.Width - 1), x1 = Math.Clamp((int)MathF.Ceiling(uv.U1 * map.Width), x0 + 1, map.Width);
        int y0 = Math.Clamp((int)MathF.Floor(uv.V0 * map.Height), 0, map.Height - 1), y1 = Math.Clamp((int)MathF.Ceiling(uv.V1 * map.Height), y0 + 1, map.Height);
        var values = new float[(x1 - x0) * (y1 - y0)];
        int k = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) values[k++] = map.Grey[y * map.Width + x];
        Array.Sort(values);
        return values[Math.Clamp((int)MathF.Round(percentile * (values.Length - 1)), 0, values.Length - 1)];
    }

    // ── the solve ──

    /// <summary>The boost opacity (0..<paramref name="ceiling"/>) that brings <paramref name="ink"/> to every band's ratio over the photograph,
    /// under the surface's <paramref name="floor"/> veil, judged at each band's top (where the veil is lightest) against its
    /// <see cref="Percentile"/> grey. 0: the floor is enough. The ceiling wins over a band no darkening below it can satisfy.</summary>
    public static float SolveBoost(LumaMap map, float boxW, float boxH, ReadOnlySpan<Band> bands, Floor floor, ColorF ink, float ceiling, Crop crop = default)
    {
        float boost = 0f;
        crop = crop.OrCentred;
        foreach (Band b in bands)
        {
            float grey = GreyAt(map, CoverUv(map.Aspect, boxW, boxH, b.Left, b.Top, b.Right, b.Bottom, crop.FocusX, crop.FocusY, crop.FrameX, crop.FrameY), Percentile);
            float under = grey * (1f - floor.AlphaAt(b.Top));
            float need = b.Need * Headroom;
            if (Contrast(ink, under) >= need) continue;
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) / 2f;
                if (Contrast(ink, under * (1f - mid)) >= need) hi = mid; else lo = mid;
            }
            boost = MathF.Max(boost, hi);
        }
        return MathF.Min(boost, ceiling);
    }

    /// <summary>The WCAG ratio of <paramref name="ink"/> (its alpha composited) over an opaque grey ground (sRGB-encoded, 0..1).</summary>
    public static float Contrast(ColorF ink, float grey)
    {
        float a = Math.Clamp(ink.A, 0f, 1f);
        float r = a * ink.R + (1f - a) * grey, g = a * ink.G + (1f - a) * grey, b = a * ink.B + (1f - a) * grey;
        float lt = 0.2126f * Lin(r) + 0.7152f * Lin(g) + 0.0722f * Lin(b), lg = Lin(grey);
        return (MathF.Max(lt, lg) + 0.05f) / (MathF.Min(lt, lg) + 0.05f);
    }

    static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    static float Enc(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
}
