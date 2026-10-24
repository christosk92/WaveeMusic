// ── Themes/Zune/Structure/Rules/ZunePanelWashRules.cs — the numbers of the side panel's cover glow (Views/Panel/ZunePanelWash.cs) ─────────────────

using FluentGpu.Foundation;

namespace Wavee.Themes.Zune;

/// <summary>The panel glow's numbers: its colour at the peak per kind (the cover's hue at this saturation, value and alpha), how far down the panel it reaches,
/// the shape of its fall, the edge.</summary>
public static class ZunePanelWashRules
{
    public const string WashKey = "zune-panel-wash", EdgeKey = "zune-panel-edge", ScrimKey = "zune-panel-scrim", CardWashKey = "zune-panel-card-wash",
        SurfaceKey = "zune-panel-surface";

    public const float EdgeW = 1f;


    /// <summary>The glow is gone by this fraction of the panel's height (the head, the lyrics' or the queue's option row, the playing row lie inside it).</summary>
    public const float GlowReach = 0.4f;

    /// <summary>The glow's centre (node-relative: over the pivots, on the panel's top edge) and its half-width as a fraction of the panel's width (an ellipse
    /// wider than the panel, so the glow spans the head and only thins toward the far corner).</summary>
    public static readonly Point2 GlowCentre = new(0.35f, 0f);
    public const float GlowWidth = 1.1f;

    /// <summary>The fall's two middle stops: (where, as a fraction of the reach; the strength there, as a fraction of the peak's alpha). An ease-out: it holds
    /// over the head, then thins softly to nothing at the reach.</summary>
    public static readonly (float At, float Strength)[] GlowStops = [(0.3f, 0.78f), (0.62f, 0.3f)];

    /// <summary>Dark: a saturated, deep tint at a low alpha over the near-black plate (reads as the cover's colour, not a grey-plum).</summary>
    public const float DarkGlowS = 0.82f, DarkGlowV = 0.62f, DarkGlowA = 0.4f;

    /// <summary>Light: a clear pastel of the hue over the pale plate.</summary>
    public const float LightGlowS = 0.45f, LightGlowV = 1f, LightGlowA = 0.5f;

    /// <summary>The panel zone's scrim (design "B, one surface"): the page's own wash runs on under the panel and this dims it, so the page and the panel read
    /// as one sheet with a slightly darker panel zone. Dark: near-black at 42 % (the owner's mock, `rgba(10,10,12,.42)`). Light: a faint dark veil, so the
    /// zone stays a shade under the pale page and the inks keep their contrast.</summary>
    public static ColorF ScrimOf(bool light) => light ? ColorF.FromRgba(20, 20, 24, 12) : ColorF.FromRgba(10, 10, 12, 107);

    /// <summary>The glow's peak colour for the cover's <paramref name="hue"/> on the ground of the kind.</summary>
    public static ColorF GlowOf(float hue, bool light) => light
        ? ColorF.FromHsv(hue, LightGlowS, LightGlowV, LightGlowA)
        : ColorF.FromHsv(hue, DarkGlowS, DarkGlowV, DarkGlowA);
}
