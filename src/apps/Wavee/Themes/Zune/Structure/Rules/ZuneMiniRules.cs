// ── Themes/Zune/Structure/Rules/ZuneMiniRules.cs — the mini player's numbers in Zune (#197) ─────────────────────────
//
// From the approved prototype (scratchpad miniplayer/index.html, .tk[data-theme="zune"]): transport and save at 40 DIP or more,
// the hover window buttons at 32 like caption buttons, the art corner and plates of the theme.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

internal static class ZuneMiniRules
{
    public const float ArtCorner = 0f;
    public const float ButtonCorner = 20f;
    public const float ButtonBox = 40f;
    public const float ButtonGlyph = 16f;
    public const float PlayBox = 44f;
    public const float PlayGlyph = 18f;
    public const float ChromeBox = 32f;
    public const float ChromeGlyph = 16f;
    public const float Pad = 10f;   // the large title needs the room: a tighter card edge, never a smaller title
    public const float Gap = 14f;
    public const float StripPadStart = 10f;
    public const float StripThumb = 40f;
    public const float StripGap = 11f;
    public const float Hairline = 2f;
    public const float HairlineHot = 5f;
    public const float Badge = 18f;
    public const float GroundAlpha = 1f;
    public const float VeilAlpha = 0.52f;
    public const float ShadeAlpha = 0.72f;
    public const float HoverInMs = 120f;
    public const float HoverOutDelayMs = 600f;

    /// <summary>The title's display size per shape (strip 23, wide 27, square 28, tall 30): large and light, never shrunk to fit; it bleeds
    /// past the column under a clip instead (owner rule).</summary>
    public const float TitleLeading = 1.15f;
    public static float TitleSize(MiniShape shape) => shape switch { MiniShape.Strip => 23f, MiniShape.Square => 28f, MiniShape.Tall => 30f, _ => 27f };
}
