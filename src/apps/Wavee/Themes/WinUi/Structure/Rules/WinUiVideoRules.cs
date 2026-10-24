// ── Themes/WinUi/Structure/Rules/WinUiVideoRules.cs — the numbers of WinUI's in-window video overlay (audit C0; W-Video, -Small, -Compact) ──────
//
// The mini player's chrome per window tier: the button box and its glyph, the strip's height, whether the chrome shows at rest, and how the resize
// zones are laid out. Data, not decisions: `Views\Video\WinUiVideoPip.cs` reads them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The mini player's chrome at one window tier.</summary>
/// <param name="Tier">The window tier this record is for.</param>
/// <param name="GlyphBox">A chrome button's square target (W-Video 32; W-Video-Compact 40, the touch floor).</param>
/// <param name="Glyph">Its icon's size (W-Video 14; 16 in the 40 box).</param>
/// <param name="ChromeAtRest">The chrome shows at rest (a Compact window has no hover to reveal it).</param>
/// <param name="EdgeZones">The corner cells go inert and a press on an edge band near either end grips that corner too: no corner cell under the
/// 40 touch floor (Compact). Otherwise each of the eight zones grips its own edges (12 x 14 corners, 6 bands). Same cells either way.</param>
public sealed record WinUiVideoPipLayout(WidthTier Tier, float GlyphBox, float Glyph, bool ChromeAtRest, bool EdgeZones);

/// <summary>The constants of WinUI's mini player and its per-window-tier records.</summary>
public static class WinUiVideoRules
{
    /// <summary>"Dock in rail" (W-Video: the window with its right pane, Segoe Fluent's DockRight; the placement menu's row keeps its own glyph).</summary>
    public static readonly string DockGlyph = ((char)0xE90D).ToString();

    /// <summary>The gap between the chrome's buttons (W-Video: a 34 pitch over 32 boxes).</summary>
    public const float GlyphGap = 2f;

    /// <summary>The glyph plate's hover and pressed alpha over the on-media ink.</summary>
    public const float GlyphHoverAlpha = 0.14f, GlyphPressedAlpha = 0.22f;

    /// <summary>The card's ring (W-Video: a one-DIP surface stroke round the r8 card).</summary>
    public const float RingWidth = 1f;

    /// <summary>The resize grip at the south-east corner (W-Video: an L of two 10-DIP strokes, 4 in from the corner).</summary>
    public const float GripLength = 10f, GripStroke = 1.5f, GripInset = 4f;

    /// <summary>How far from an edge band's end a press also grips the corner (the Compact edge zones).</summary>
    public const float CornerReach = 40f;

    private static readonly WinUiVideoPipLayout[] s_tiers =
    [
        // W-Video-Compact: 40 targets at rest, the edge bands as the resize zones
        new(WidthTier.Compact, GlyphBox: 40f, Glyph: 16f, ChromeAtRest: true, EdgeZones: true),
        // W-Video-Small: the hover chrome, 32 targets
        new(WidthTier.Small, GlyphBox: 32f, Glyph: 14f, ChromeAtRest: false, EdgeZones: false),
        // W-Video
        new(WidthTier.Standard, GlyphBox: 32f, Glyph: 14f, ChromeAtRest: false, EdgeZones: false),
        // Huge (W-Video-Huge docks the video: the floating card keeps Standard's chrome)
        new(WidthTier.Huge, GlyphBox: 32f, Glyph: 14f, ChromeAtRest: false, EdgeZones: false),
        // Huge 4K
        new(WidthTier.Huge4K, GlyphBox: 40f, Glyph: 16f, ChromeAtRest: false, EdgeZones: false),
    ];

    /// <summary>The record for window tier <paramref name="window"/>.</summary>
    public static WinUiVideoPipLayout Of(WidthTier window)
    {
        var tier = s_tiers[Math.Clamp((int)window, 0, s_tiers.Length - 1)];
        return Wavee.Theming.InspectSwitches.HoverChromeAtRest && !tier.ChromeAtRest ? tier with { ChromeAtRest = true } : tier;   // `mount chrome=shown`: an agent has no pointer
    }

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiVideoPipLayout OfFacts(in TierFacts facts) => Of(facts.Policy is null ? WidthTier.Standard : facts.Window);
}
