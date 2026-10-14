// ── Themes/Common/Rules/CommonModuleRules.cs — the numbers of the module page (themes plan 6.5, T10b3) ──────────────────────────
//
// From `Platform\Modules.UI.cs` (the page's constants and the literals its sections wrote inline), moved VERBATIM: the hero art and the card width, the
// shelf and text budgets, the page gutters, the live badge, the fact tile, the watch shelf's card bounds. WVTH003: no literal look sits in the view.

namespace Wavee.Themes.Common;

/// <summary>The module page's numbers.</summary>
internal static class CommonModuleRules
{
    // ── budgets ──
    public const int ShelfMax = 16, DescriptionLines = 3, TextLines = 6;

    /// <summary>A prose block counts as long (and gets its More / Less toggle) past this many characters per allowed line.</summary>
    public const int CharsPerLine = 90;

    // ── the page ──
    /// <summary>The page's side gutter (the stage slot and the scroller).</summary>
    public const float Gutter = 32f;

    /// <summary>The scroller's top and bottom padding (the bottom one sits over the dock reserve).</summary>
    public const float ScrollPad = 40f;

    // ── the entity reading ──
    public const float HeroArt = 232f;
    public const int HeroArtDecodePx = 256;
    public const float HeroCopyMinWidth = 240f;
    public const int HeroTitleLines = 3, HeroMetaLines = 2;
    public const float CardWidth = 168f;
    public const float FactTileWidth = 180f;
    public const float ListGap = 2f;

    /// <summary>The playables section's row height.</summary>
    public const float PlayableRowHeight = 40f;

    /// <summary>A card's subtitle (and its shelf caption) wraps to this many lines.</summary>
    public const int CardSubtitleLines = 2;

    // ── the live badge ──
    public const float BadgeHeight = 18f, BadgeRadius = 2f, BadgeSize = 10f, BadgeLine = 14f;

    // ── the watch reading ──
    public const float ChannelAvatar = 40f;
    public const float PlayingChipPad = 18f, PlayingChipGlyph = 12f;
    public const float ShelfCardMin = 220f, ShelfCardMax = 300f, VideoCardMinWidth = 120f;
    public const float VideoAspect = 16f / 9f;
    public const int TitleLines = 2;

    // ── small components ──
    public const float TextSize = 14f, TextLine = 20f;
    public const float LinkGlyph = 14f;
}
