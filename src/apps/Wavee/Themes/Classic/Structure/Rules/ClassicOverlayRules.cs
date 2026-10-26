// ── Themes/Classic/Structure/Rules/ClassicOverlayRules.cs — the numbers of Classic's overlays (themes plan 6.1, T11a) ─────────────
//
// What `Shell.Overlays.UI.cs` (the account chip and menu, the runtime banner, the update toast's download card), `Controls.cs` (the offline
// strip) and `Actions.UI.cs` (the context menu's identity tile) held as file-level constants and inline literals, moved verbatim with their
// pictures. They are data, not decisions: the views under `Views\Overlays` read them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of the account chip and menu, the runtime banner, the update toast's card, the offline strip and the menu tile.</summary>
public static class ClassicOverlayRules
{
    // ── the account chip ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The avatar's size on the chip.</summary>
    public const float ChipAvatar = 24f;

    /// <summary>The chip's gap and height.</summary>
    public const float ChipGap = 8f, ChipHeight = 32f;

    /// <summary>The chip's padding. The named form carries 6 extra DIP of right padding: part of the budgeted <c>ChromeProfileNameW</c>.</summary>
    public static readonly Edges4 ChipPaddingNamed = new(4f, 0f, 10f, 0f), ChipPaddingBare = new(4f, 0f, 4f, 0f);

    /// <summary>The unknown name's skeleton bar height and radius.</summary>
    public const float ChipNameBarHeight = 12f, SkeletonBarRadius = 2f;

    // ── the account chip as the drawer's account row (TC2, C-Sidebar account="true") ─────────────────────────────────

    /// <summary>The row form: 48 tall, 8 either side, the 32 avatar 10 from the name (14/20 semibold) over "Account and more" (12/16).</summary>
    public const float DrawerChipH = 48f, DrawerChipAvatar = 32f, DrawerChipGap = 10f;

    /// <summary>The row form's padding.</summary>
    public static readonly Edges4 DrawerChipPadding = new(8f, 0f, 8f, 0f);

    /// <summary>The name and the line under it.</summary>
    public const float DrawerNameSize = 14f, DrawerNameLine = 20f, DrawerSubSize = 12f, DrawerSubLine = 16f;

    // ── the account menu ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The menu's width (the rows and the header share it).</summary>
    public const float ProfileMenuWidth = 304f;

    /// <summary>6 + the menu presenter's own (0,2,0,2) = the 8-DIP inset this card has always had.</summary>
    public static readonly Edges4 ProfileMenuPadding = new(0f, 6f, 0f, 6f);

    /// <summary>The divider under the header.</summary>
    public static readonly Edges4 ProfileMenuDividerMargin = new(8f, 4f, 8f, 4f);

    /// <summary>The header: its gap, padding, avatar and its circle.</summary>
    public const float HeaderGap = 12f, HeaderAvatar = 40f, HeaderAvatarRadius = 20f;

    /// <summary>The header's padding.</summary>
    public static readonly Edges4 HeaderPadding = new(14f, 10f, 14f, 10f);

    /// <summary>The header's text column gap.</summary>
    public const float HeaderLinesGap = 2f;

    /// <summary>The name: size and line height.</summary>
    public const float HeaderNameSize = 14f, HeaderNameLine = 20f;

    /// <summary>The unknown name's bar: width, height and margin (keeps the header's height).</summary>
    public const float HeaderNameBarWidth = 140f, HeaderNameBarHeight = 14f;

    /// <summary>The unknown name's bar margin.</summary>
    public static readonly Edges4 HeaderNameBarMargin = new(0f, 3f, 0f, 3f);

    /// <summary>The tier line: the gap, the star (and the Free spacer of its width, so both badges share one baseline), the badge's size and line.</summary>
    public const float TierGap = 5f, TierStar = 10f, TierTextSize = 12f, TierTextLine = 16f;

    /// <summary>The premium tier ink, the ONE hand-authored colour in this surface, applied to the star and the badge. A property (never a
    /// field) so a live theme flip re-resolves it.</summary>
    public static ColorF PremiumInk => Theme.Dark ? ColorF.FromRgba(0xE6, 0xC2, 0x6C) : ColorF.FromRgba(0x8A, 0x63, 0x12);

    // ── the runtime banner ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The banner's inner cap.</summary>
    public const float BannerMaxWidth = 560f;

    // ── the update toast's download card ────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar's width.</summary>
    public const float UpdateCardBarWidth = 240f;

    /// <summary>The card's padding.</summary>
    public static readonly Edges4 UpdateCardPadding = new(16f, 14f, 16f, 14f);

    // ── the offline strip ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip's glyph.</summary>
    public const float OfflineIcon = 16f;

    /// <summary>The cloud-off glyph of the offline voice (the strip and the vacancy; the sidebar's offline pin wears the same one).</summary>
    public const string OfflineGlyph = SidebarGeometry.OfflineGlyph;

    /// <summary>The strip (tuned Classic X1, board C-Offline): 36 tall, 12 either side, 10 between the glyph and the notice, r4.</summary>
    public const float OfflineStripH = 36f, OfflineStripPadX = 12f, OfflineStripGap = 10f;

    // ── the offline vacancy (tuned Classic X1, board C-Offline) ──────────────────────────────────────────────────────

    /// <summary>The vacancy's glyph (40, tertiary) and the step from it to the title (12 on top of the column's 4).</summary>
    public const float OfflineVacancyGlyph = 40f, OfflineVacancyTitleTop = 12f;

    /// <summary>The column's gap, the caption's widest measure, and the step to the actions (16 on top of the gap).</summary>
    public const float OfflineVacancyGap = 4f, OfflineVacancyCaptionMaxW = 360f, OfflineVacancyActionsTop = 16f;

    /// <summary>Between Retry and the library link (a row), and between them stacked (Compact: a column).</summary>
    public const float OfflineVacancyActionsGap = 12f, OfflineVacancyStackGap = 4f;

    /// <summary>Retry's least width where the actions stack (Compact).</summary>
    public const float OfflineVacancyRetryMinW = 120f;

    /// <summary>The column sits above the card's centre: 120 of bottom padding lifts it (the page title stays above it).</summary>
    public const float OfflineVacancyLift = 120f;

    // ── the context menu's identity tile ────────────────────────────────────────────────────────────────────────────

    /// <summary>The tile: its size, the square's radius, the circle's radius, and the decode size (twice the tile).</summary>
    public const float MenuTile = 38f, MenuTileRadius = 6f, MenuTileCircleRadius = 19f;

    /// <summary>The tile's decode size in pixels.</summary>
    public const int MenuTileDecodePx = 76;
}
