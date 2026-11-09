// ── Themes/Common/Rules/CommonMiniShapeIcons.cs — the four window shapes as menu icons (#197) ─────────────────────────────
//
// The mini player's Options menu marks each shape row with the shape itself: a rounded outline at the shape's own ratio (6:1 drawn as 4:1
// so the strip keeps an inside, 12:5, 1:1, 9:16) on the icon font's 16-unit box, registered once as vector icons so they ink and scale
// like the menu's other glyphs. There is no icon-font glyph for these.

using System.Globalization;
using FluentGpu.Controls;

namespace Wavee.Themes.Common;

internal static class MiniShapeIcons
{
    const float Box = 16f, Stroke = 1.25f, Corner = 1.75f, Kappa = 0.5523f;
    static bool s_registered;

    /// <summary>Register the four icons (idempotent; the menu calls it before it builds its rows).</summary>
    public static void Ensure()
    {
        if (s_registered) return;
        s_registered = true;
        Register(CommonMiniPlayerRules.ShapeIconStrip, 0.5f, 6f, 15f, 4f);
        Register(CommonMiniPlayerRules.ShapeIconWide, 0.5f, 4.875f, 15f, 6.25f);
        Register(CommonMiniPlayerRules.ShapeIconSquare, 2f, 2f, 12f, 12f);
        Register(CommonMiniPlayerRules.ShapeIconTall, 3.75f, 0.5f, 8.5f, 15f);
    }

    static void Register(string name, float x, float y, float w, float h)
    {
        string path = Rounded(x, y, w, h, Corner) + Rounded(x + Stroke, y + Stroke, w - 2f * Stroke, h - 2f * Stroke, MathF.Max(0f, Corner - Stroke));
        ThemedIconRegistry.Register(name, new IconDef(null, null, [new IconLayerDef(IconRole.Base, path)], ViewBox: Box, EvenOdd: true));
    }

    /// <summary>A rounded rectangle as one closed contour (cubic corners).</summary>
    static string Rounded(float x, float y, float w, float h, float r)
    {
        float k = r * Kappa, x1 = x + w, y1 = y + h;
        var c = CultureInfo.InvariantCulture;
        return string.Create(c,
            $"M{x + r},{y}L{x1 - r},{y}C{x1 - r + k},{y} {x1},{y + r - k} {x1},{y + r}L{x1},{y1 - r}C{x1},{y1 - r + k} {x1 - r + k},{y1} {x1 - r},{y1}" +
            $"L{x + r},{y1}C{x + r - k},{y1} {x},{y1 - r + k} {x},{y1 - r}L{x},{y + r}C{x},{y + r - k} {x + r - k},{y} {x + r},{y}Z");
    }
}
