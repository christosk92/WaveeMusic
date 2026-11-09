// ── Themes/WinUi/Structure/Rules/WinUiMiniRules.cs — the mini player's numbers in WinUi (#197) ─────────────────────────
//
// From the approved prototype (scratchpad miniplayer/index.html, .tk[data-theme="winui"]): transport and save at 40 DIP or more,
// the hover window buttons at 32 like caption buttons, the art corner and plates of the theme.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static class WinUiMiniRules
{
    public const float ArtCorner = 4f;
    public const float ButtonCorner = 5f;
    public const float ButtonBox = 40f;
    public const float ButtonGlyph = 16f;
    public const float PlayBox = 44f;
    public const float PlayGlyph = 18f;
    public const float ChromeBox = 32f;
    public const float ChromeGlyph = 16f;
    public const float Pad = 14f;
    public const float Gap = 14f;
    public const float StripPadStart = 10f;
    public const float StripThumb = 40f;
    public const float StripGap = 11f;
    public const float Hairline = 2f;
    public const float HairlineHot = 5f;
    public const float Badge = 18f;
    public const float GroundAlpha = 0.8f;
    public const float VeilAlpha = 0.52f;
    public const float ShadeAlpha = 0.72f;
    public const float HoverInMs = 120f;
    public const float HoverOutDelayMs = 600f;
}
