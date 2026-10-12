// ── Themes/Classic/Structure/Rules/StageGeometry.Options.cs — the numbers of Classic's stage options popover (themes plan 6.1, T7a1) ──
//
// What `Stage.Options.UI.cs` spelled inline (440 wide, 20 padding, tabs with a 3 DIP accent underline, a three-column poster grid,
// the layout rows' 84x48 schematics), moved verbatim with the popover's picture. They are data, not decisions:
// `Views\Stage\ClassicStageOptions.cs` reads them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>A schematic dot of a layout row's thumbnail: its offset and size in the 84x48 plate.</summary>
internal readonly record struct ClassicStageThumbDot(float X, float Y, float W, float H);

/// <summary>The constants of Classic's stage options popover.</summary>
internal static class ClassicStageOptionsGeometry
{
    // ── the panel ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panel's widest and tallest extent, and the margin it keeps from the stage's edges (440 wide, 720 tall, 24 each side).</summary>
    public const float PanelMaxWidth = 440f, PanelMaxHeight = 720f, PanelEdgeInset = 48f;

    /// <summary>The two tabs.</summary>
    public const int TabCount = 2;

    /// <summary>The visualizer grid's column count.</summary>
    public const int GridCols = 3;

    // ── type ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    public const ushort WeightRegular = 400, WeightSemiBold = 600;

    /// <summary>The panel title.</summary>
    public const float TitleSize = 18f, TitleLine = 24f;

    /// <summary>Body (tab labels, the footer button, rows, section titles).</summary>
    public const float BodySize = 14f, BodyLine = 20f;

    /// <summary>Caption (counts, tile names, notes, secondary lines).</summary>
    public const float CaptionSize = 12f, CaptionLine = 16f;

    /// <summary>The group chips' label.</summary>
    public const float ChipSize = 13f, ChipLine = 18f;

    /// <summary>The layout tab's help text.</summary>
    public const float HelpSize = 13f, HelpLine = 19f;

    /// <summary>The hero note under the dimming slider.</summary>
    public const float NoteLine = 17f;

    /// <summary>The "G" key cap in the footer button.</summary>
    public const float KeyCapSize = 11f;

    /// <summary>The selected layout row's check glyph.</summary>
    public const float CheckSize = 12f;

    // ── the header and the tablist ───────────────────────────────────────────────────────────────────────────────────

    public const float HeaderGap = 12f;
    public static readonly Edges4 HeaderPadding = new(20f, 18f, 20f, 0f);

    public const float TabsGap = 4f, TabGap = 6f, TabHeadGap = 8f, TabUnderlineHeight = 3f;
    public static readonly Edges4 TabPadding = new(14f, 8f, 14f, 0f);
    public static readonly CornerRadius4 TabCorners = CornerRadius4.All(6f);
    public static readonly CornerRadius4 TabUnderlineCorners = CornerRadius4.All(2f);

    /// <summary>The count badge on the Visualizer tab.</summary>
    public static readonly Edges4 CountBadgePadding = new(7f, 1f, 7f, 1f);
    public static readonly CornerRadius4 CountBadgeCorners = Radii.Circle(20f);

    // ── the Visualizer tab ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tab body's vertical rhythm.</summary>
    public const float VizBodyGap = 14f;
    public static readonly Edges4 VizBodyPadding = new(0f, 16f, 0f, 0f);

    /// <summary>"Now showing": the poster's size, the row's gap and padding, the text column's gap.</summary>
    public const float NowPosterWidth = 136f, NowPosterHeight = 76f, NowGap = 14f, NowTextGap = 2f;
    public static readonly Edges4 NowPadding = new(20f, 0f, 20f, 0f);

    /// <summary>A poster's corner radius.</summary>
    public static readonly CornerRadius4 PosterCorners = CornerRadius4.All(6f);

    /// <summary>The group chips.</summary>
    public static readonly Edges4 ChipsPadding = new(20f, 0f, 20f, 0f);
    public const float ChipGridGap = 6f, ChipMinHeight = 32f;
    public static readonly Edges4 ChipPadding = new(12f, 0f, 12f, 0f);
    public static readonly CornerRadius4 ChipCorners = Radii.Circle(32f);

    /// <summary>The poster grid: horizontal padding both sides, the gap between tiles, the 16:9 poster, what a tile's frame takes off its poster.</summary>
    public const float GridPadX = 40f, GridGap = 10f, PosterAspectW = 16f, PosterAspectH = 9f, TilePosterInsetW = 8f, TilePosterInsetH = 4f;
    public static readonly Edges4 GridPadding = new(20f, 2f, 20f, 8f);

    /// <summary>One tile: its gap, frame, corners, border and name margin.</summary>
    public const float TileGap = 4f, TileBorder = 2f;
    public static readonly Edges4 TileFramePadding = Edges4.All(2f);
    public static readonly CornerRadius4 TileCorners = CornerRadius4.All(8f);
    public static readonly Edges4 TileNameMargin = new(2f, 0f, 2f, 2f);

    // ── the footer ───────────────────────────────────────────────────────────────────────────────────────────────────

    public const float FooterGap = 12f, FooterButtonGap = 8f, FooterButtonMinHeight = 36f;
    public static readonly Edges4 FooterPadding = new(20f, 12f, 20f, 16f);
    public static readonly Edges4 FooterButtonPadding = new(12f, 0f, 12f, 0f);
    public static readonly Edges4 KeyCapPadding = new(6f, 1f, 6f, 1f);
    public static readonly CornerRadius4 KeyCapCorners = CornerRadius4.All(4f);

    // ── the Layout tab ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The scroller's content: gap and padding.</summary>
    public const float LayoutGap = 12f;
    public static readonly Edges4 LayoutPadding = new(20f, 16f, 20f, 16f);

    /// <summary>The artist-photo group: gap, top padding, the dimming block's gap, the slider's length inset and thickness.</summary>
    public const float HeroGap = 12f, HeroDimGap = 6f, HeroSliderInset = 40f, HeroSliderThickness = 24f;
    public static readonly Edges4 HeroPadding = new(0f, 10f, 0f, 0f);

    /// <summary>The toggle's minimum width.</summary>
    public const float ToggleMinWidth = 40f;

    /// <summary>The dimming slider: 0-100 in steps of five.</summary>
    public const float DimMin = 0f, DimMax = 100f, DimStep = 5f;

    /// <summary>A section (title over a control) and a labelled row.</summary>
    public const float SectionGap = 8f, RowGap = 2f, RowMinHeight = 32f;
    public static readonly Edges4 SectionPadding = new(0f, 10f, 0f, 0f);

    /// <summary>The radio rows' plain style: the focus ring's outset.</summary>
    public static readonly Edges4 RadioFocusMargin = Edges4.All(-2f);

    // ── a layout row ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 84x48 schematic and its dots.</summary>
    public const float ThumbWidth = 84f, ThumbHeight = 48f;
    public static readonly CornerRadius4 ThumbCorners = CornerRadius4.All(6f);
    public static readonly CornerRadius4 ThumbDotCorners = CornerRadius4.All(3f);

    /// <summary>How much of the palette's first colour the schematic's plate takes, and the Artist plate's ink alpha / its dot's.</summary>
    public const float ThumbPlateMix = 0.55f, ThumbArtistPlateAlpha = 0.45f, ThumbArtistDotAlpha = 0.85f;

    public static readonly ClassicStageThumbDot CardDot = new(6f, 6f, 14f, 9f);
    public static readonly ClassicStageThumbDot LargeDot = new(8f, 9f, 22f, 22f);
    public static readonly ClassicStageThumbDot CenteredDot = new(31f, 9f, 22f, 22f);
    public static readonly ClassicStageThumbDot ArtistDot = new(6f, 37f, 30f, 5f);

    /// <summary>The row: gap, padding, the selected fill's alpha, the border widths, the text column's gap and its title row's gap.</summary>
    public const float LayoutRowGap = 12f, LayoutRowSelectedAlpha = 0.16f, LayoutRowBorderOn = 2f, LayoutRowBorderOff = 1f, LayoutRowTextGap = 2f, LayoutRowTitleGap = 6f;
    public static readonly Edges4 LayoutRowPadding = Edges4.All(10f);
}
