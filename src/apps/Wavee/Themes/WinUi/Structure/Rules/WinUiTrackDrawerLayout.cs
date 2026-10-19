// ── Themes/WinUi/Structure/Rules/WinUiTrackDrawerLayout.cs — the numbers of WinUI's track drawer (themes plan 6.2; P6 W3) ────────────────────
//
// The frame's bottom corners, the versions eyebrow's air, the version rows' small gaps and the tempo swatch's corner, the video thumbnail's play badge and its
// scrim, and the format split button's two halves. They are data, not decisions: `Lists\WinUiTrackDrawer.cs` reads them (WVTH003 allows literals only here, in
// `Rules\**`). The connector's geometry (`DrawerRules.GutterW`, `RailX`, `StubW`, the row pitch and the thumbnails' sizes) and the drawer's indent
// (`RowMetrics.DrawerIndent`) are the core's: the table's extents run on them.

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's track drawer.</summary>
internal static class WinUiTrackDrawerLayout
{
    // ── the frame ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drawer keeps square top corners under its row's plate and rounds the bottom ones (r4: W-Tokens' row radius).</summary>
    public const float BottomRadius = 4f;

    // ── the body ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The air under the "Versions and formats" eyebrow.</summary>
    public const float HeadBottom = 2f;

    /// <summary>The gap inside a pending (reserved video) row's text column; the gap between a version's title and its meta line.</summary>
    public const float PendingGap = 6f, TextGap = 2f;

    /// <summary>The tempo swatch's corner (its box and opacity are the table rows': <c>WinUiListRules.TempoDot</c>).</summary>
    public const float TempoDotRadius = 1.5f;

    /// <summary>The music-video thumbnail's play badge (a 22 disc over a dark scrim) and its inks.</summary>
    public const float PlayBadge = 22f;

    /// <summary>The scrim over a video thumbnail.</summary>
    public static readonly ColorF VideoScrim = new(0f, 0f, 0f, 0.28f);

    /// <summary>The play badge's fill and glyph ink.</summary>
    public static readonly ColorF PlayBadgeFill = new(1f, 1f, 1f, 0.92f), PlayBadgeInk = ColorF.FromRgba(17, 17, 17);

    // ── the format split button ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The two halves: 30 x 28 play (glyph 11) and 20 x 28 caret (glyph 9).</summary>
    public const float SplitHeight = 28f, PlayHalfWidth = 30f, CaretHalfWidth = 20f, PlayGlyph = 11f, CaretGlyph = 9f;
}
