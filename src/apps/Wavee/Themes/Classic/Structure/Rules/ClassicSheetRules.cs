// ── Themes/Classic/Structure/Rules/ClassicSheetRules.cs — the numbers of Classic's bottom sheet (themes plan 3.6, TC9) ──────────────────
//
// `C-ContextMenu-Compact` ("Compact song menu, bottom sheet") and `C-Flyouts-Compact` ("Compact filter sheet"): a card laid over the lower edge of
// the window under a 40 % scrim that starts below the 48-DIP chrome row, rounded 8 on top, a 32 x 4 grabber, then the identity header (56), the
// four-column quick-action strip (64 tall cells) and 44-DIP rows. Every target is at least 40. The sheet exists only where the structure's policy says
// context menus are sheets (Compact today); these are the geometry of the one picture, not a per-tier choice. WVTH003 allows literals only here.

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of the bottom sheet (public: the mounted tests read them).</summary>
public static class ClassicSheetRules
{
    /// <summary>The 48-DIP chrome row the scrim starts below (the title bar stays reachable under a sheet).</summary>
    public const float ChromeRow = 48f;

    /// <summary>The scrim over the page: black at 40 %.</summary>
    public static ColorF Scrim => ColorF.FromRgba(0, 0, 0, 102);

    /// <summary>The card's top corners.</summary>
    public const float Radius = 8f;

    /// <summary>The card's padding either side and under the last row.</summary>
    public const float SidePad = 8f, BottomPad = 12f;

    /// <summary>The grabber's zone (its height), the pill and its corner.</summary>
    public const float GrabberZone = 16f, GrabberW = 32f, GrabberH = 4f, GrabberRadius = 2f;

    /// <summary>The grabber's ink: white at 30 %.</summary>
    public static ColorF Grabber => ColorF.FromRgba(255, 255, 255, 77);

    /// <summary>The identity header: its height, side padding and gap, the tile's box, and the title's and subtitle's type.</summary>
    public const float HeaderHeight = 56f, HeaderPadX = 8f, HeaderGap = 12f, HeaderTile = 40f;

    /// <summary>The header's title (14 over 20, semi-bold) and subtitle (12 over 16).</summary>
    public const float TitleSize = 14f, TitleLine = 20f, SubtitleSize = 12f, SubtitleLine = 16f;

    /// <summary>The quick-action strip: the gap between cells, its padding, a cell's height, corner and gap, the icon and the label.</summary>
    public const float StripGap = 4f, StripCell = 64f, StripCellRadius = 4f, StripCellGap = 6f, StripIcon = 18f, StripLabel = 12f;

    /// <summary>The strip's padding (4 above, 8 below).</summary>
    public static readonly Edges4 StripPadding = new(0f, 4f, 0f, 8f);

    /// <summary>A row: its height, side padding, gap, the icon, the label, the trailing chevron and check.</summary>
    public const float RowHeight = 44f, RowPadX = 12f, RowGap = 12f, RowIcon = 16f, RowText = 14f, RowTrail = 12f, RowRadius = 4f;

    /// <summary>The divider: its margin and thickness.</summary>
    public static readonly Edges4 DividerMargin = new(8f, 4f, 8f, 4f);

    /// <summary>The entry slide (up from below).</summary>
    public const float EnterDy = 48f;
}
