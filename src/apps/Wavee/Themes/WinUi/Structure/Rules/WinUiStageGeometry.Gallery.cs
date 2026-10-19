// ── Themes/WinUi/Structure/Rules/WinUiStageGeometry.Gallery.cs — the numbers of WinUI's (from Classic's) visualizer gallery (themes plan 6.1, T7a1) ──
//
// What `Visualizer.Gallery.UI.cs` held as literals beside the pane and its tiles, moved with the picture: the pane's metrics
// (padding 20 · 14 · 20 · 20, the tile gap, a tile's floor width, the preview strip), the tile's insets and sizes, the settings
// rows and the two slider ranges. Data, not decisions: `Views\Stage\WinUiStageGallery.cs` and `WinUiStageGalleryTiles.cs` read
// them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's (from Classic's) visualizer gallery.</summary>
internal static class WinUiStageGalleryGeometry
{
    // ── the pane ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane's metrics: padding 20 · 14 · 20 · 20, the tile gap, a tile's floor width, the preview strip.</summary>
    public const float PadX = 20f, TileGap = 10f, TileMinW = 180f, PreviewH = 78f;

    /// <summary>The pane's padding and the gap between its title row and the scroller.</summary>
    public static readonly Edges4 PanePadding = new(PadX, 14f, PadX, 20f);

    public const float PaneGap = 10f;

    /// <summary>The scroller's content gap (the grid, then the settings header and rows).</summary>
    public const float ContentGap = 10f;

    /// <summary>How long the pointer must rest on a tile before its preview goes live (a sweep promotes nothing).</summary>
    public const float HoverPromoteMs = 120f;

    /// <summary>The gutter kept between a cursor tile brought into view and the scroller's edge (the grid's tile gap).</summary>
    public const double CursorGutter = 10.0;

    /// <summary>The scroller's key.</summary>
    public const string ScrollKey = "stagegallery";

    // ── the title row ─────────────────────────────────────────────────────────────────────────────────────────────────

    public const float TitleSize = 20f, TitleLine = 28f;

    // ── the group headers ─────────────────────────────────────────────────────────────────────────────────────────────

    public const float GroupHeaderSize = 12f, GroupHeaderLine = 16f;

    /// <summary>A group's header margin: 2 in, and 6 above every header but the first.</summary>
    public static Edges4 GroupHeaderMargin(bool first) => new(2f, first ? 0f : 6f, 0f, 0f);

    // ── a tile ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public const float TileInnerGap = 6f, TileBorderWidth = 2f;

    public static readonly Edges4 TilePadding = new(6f, 6f, 6f, 8f);

    public static readonly CornerRadius4 TileCorners = CornerRadius4.All(6f);

    /// <summary>The preview is narrower than the tile by this (2 × the padding).</summary>
    public const float PreviewInset = 12f;

    /// <summary>The check disc: its size, offset from the preview's top-right corner and its glyph.</summary>
    public const float CheckDisc = 20f, CheckGlyph = 12f;

    public static readonly CornerRadius4 CheckDiscCorners = Radii.Circle(CheckDisc);

    public static readonly Edges4 CheckDiscMargin = new(0f, 8f, 8f, 0f);

    /// <summary>The label block under the preview.</summary>
    public static readonly Edges4 LabelPadding = new(4f, 0f, 4f, 0f);

    public const float LabelGap = 6f, NameSize = 14f, NameLine = 20f, PaceSize = 12f, PaceLine = 16f, BlurbSize = 12f, BlurbLine = 16f;

    // ── the settings rows ─────────────────────────────────────────────────────────────────────────────────────────────

    public const float SettingsHeaderSize = 14f, SettingsHeaderLine = 20f;

    public static readonly Edges4 SettingsHeaderMargin = new(0f, 6f, 0f, 0f);

    public const float RowMinHeight = 48f, RowGlyphSize = 16f, RowLabelSize = 14f, RowLabelLine = 20f;

    public static readonly Edges4 RowPadding = new(16f, 0f, 12f, 0f);

    /// <summary>The toggle switches' floor width.</summary>
    public const float ToggleMinWidth = 40f;

    /// <summary>The sliders' length and thickness.</summary>
    public const float SliderLength = 150f, SliderThickness = 24f;

    /// <summary>The sync offset snaps to a step of 10 ms.</summary>
    public static int SnapOffset(float value) => (int)MathF.Round(value / 10f) * 10;

    public static readonly Slider.SliderOptions SensitivityOptions = new()
    {
        Min = Visualizer.Bands.MinSensitivity, Max = Visualizer.Bands.MaxSensitivity, Step = 0.05f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    public static readonly Slider.SliderOptions OffsetOptions = new()
    {
        Min = -500f, Max = 500f, Step = 10f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms",
    };
}
