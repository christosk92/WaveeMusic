// ── Themes/WinUi/Structure/Rules/WinUiNotificationRules.cs — the numbers of WinUI's notification panel (themes plan 6.1, T11a) ───
//
// What `Shell.UI.cs` (section 6, the notification panel's pictures) and `Notify.cs` (the panel's fixed geometry) held as inline literals and
// constants, moved verbatim with their pictures. They are data, not decisions: `Views\Overlays\WinUiNotifications.cs` reads them (WVTH003
// allows literals only here, in `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of the notification panel.</summary>
internal static class WinUiNotificationRules
{
    // ── the panel ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panel's fixed geometry: 380 wide, the whole panel caps at 520, and the feed scrolls at most 420 so the header (48: 12 + 28
    /// + 8) and the chip strip (50: 2 + <c>Controls.ChipRailExtent</c>) above it still fit under the cap (48 + 50 + 420 = 518).</summary>
    public const float PanelWidth = 380f, FeedMaxHeight = 420f, PanelMaxHeight = 520f;

    /// <summary>What sits above the feed: the header (48) and the chip rail (50). A window too short for the whole panel bounds the feed by the panel's room less this.</summary>
    public const float FixedHeight = 98f;

    /// <summary>The feed's padding and the gap between its rows.</summary>
    public static readonly Edges4 FeedPadding = new(6f, 4f, 6f, 8f);

    /// <summary>The gap between the feed's rows, and between an expanded activity row and its detail.</summary>
    public const float FeedGap = 2f;

    // ── the header and the pills ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The header row.</summary>
    public static readonly Edges4 HeaderPadding = new(14f, 12f, 8f, 8f);

    /// <summary>The header's gap.</summary>
    public const float HeaderGap = 8f;

    /// <summary>The header's title size.</summary>
    public const float HeaderTitleSize = 15f;

    /// <summary>The filter chip rail's padding.</summary>
    public static readonly Edges4 FilterPadding = new(12f, 2f, 12f, 0f);

    // ── the pending-sync line ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The line's gap, padding and glyph and text size.</summary>
    public const float PendingGap = 8f, PendingIcon = 12f, PendingTextSize = 12f;

    /// <summary>The line's padding.</summary>
    public static readonly Edges4 PendingPadding = new(14f, 4f, 14f, 8f);

    // ── a row ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row card: the gap, the minimum height and the corner radius.</summary>
    public const float CardGap = 10f, CardMinHeight = 56f, CardRadius = 8f;

    /// <summary>A row card's padding.</summary>
    public static readonly Edges4 CardPadding = new(10f, 8f, 10f, 8f);

    /// <summary>The enter slide (up from below) and the exit slide (up and away).</summary>
    public const float CardEnterDy = 6f, CardExitDy = -4f;

    /// <summary>The unread dot.</summary>
    public const float UnreadDot = 8f, UnreadDotRadius = 4f;

    /// <summary>The content column's gap (the update row's 3, the social and release rows' 2).</summary>
    public const float UpdateColumnGap = 3f, ColumnGap = 2f;

    /// <summary>The update row's body line size and line cap.</summary>
    public const float UpdateBodySize = 12f;

    /// <summary>The update row's progress strip: the bar's width, its gap, its top margin.</summary>
    public const float UpdateBarWidth = 200f, UpdateStripGap = 8f;

    /// <summary>The update row's progress strip margin.</summary>
    public static readonly Edges4 UpdateStripMargin = new(0f, 6f, 0f, 0f);

    /// <summary>The update row's button row: its gap and margin.</summary>
    public const float UpdateButtonsGap = 6f;

    /// <summary>The update row's button row margin.</summary>
    public static readonly Edges4 UpdateButtonsMargin = new(0f, 4f, 0f, 0f);

    /// <summary>A social row's avatar and its circle.</summary>
    public const float SocialAvatar = 40f, SocialAvatarRadius = 20f;

    /// <summary>A release row's cover and its corner.</summary>
    public const float ReleaseCover = 44f, ReleaseCoverRadius = 5f;

    /// <summary>A release row's creator line size.</summary>
    public const float ReleaseCreatorSize = 12f;

    /// <summary>The activity row's right column: its gap.</summary>
    public const float ActivityRightGap = 4f;

    /// <summary>The activity detail: its gap, the target line's gap, its size, and its margin (the glyph chip's width + the row gap, aligned under the title).</summary>
    public const float DetailGap = 3f, DetailTargetGap = 8f, DetailTextSize = 12f;

    /// <summary>The activity detail's margin.</summary>
    public static readonly Edges4 DetailMargin = new(46f, 0f, 12f, 6f);

    // ── the empty body ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The empty body's minimum height, padding and message cap.</summary>
    public const float EmptyMinHeight = 120f, EmptyPadding = 24f, EmptyMessageMaxWidth = 300f;

    // ── glyph chip, pills, link ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The round glyph chip and its icon.</summary>
    public const float GlyphChip = 36f, GlyphChipRadius = 18f, GlyphIcon = 16f;

    /// <summary>The release type pill's padding and radius.</summary>
    public static readonly Edges4 TypePillPadding = new(9f, 2f, 9f, 2f);

    /// <summary>The release type pill's radius.</summary>
    public const float TypePillRadius = 10f;

    /// <summary>The action pill: the height, the padding, the radius and the text size.</summary>
    public const float PillMinHeight = 28f, PillRadius = 14f, PillTextSize = 12f;

    /// <summary>The action pill's padding.</summary>
    public static readonly Edges4 PillPadding = new(12f, 4f, 12f, 4f);

    /// <summary>The header link: the padding, the radius and the text size.</summary>
    public const float LinkRadius = 6f, LinkTextSize = 12f;

    /// <summary>The header link's padding.</summary>
    public static readonly Edges4 LinkPadding = new(8f, 3f, 8f, 3f);
}
