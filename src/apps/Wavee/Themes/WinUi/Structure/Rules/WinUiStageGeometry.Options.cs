// ── Themes/WinUi/Structure/Rules/WinUiStageGeometry.Options.cs — the numbers of WinUI's stage options popover (P6 W4b) ───────────────────
//
// W-Stage-Huge4K's open popover: 440 wide (the stage less 48 at most), 16 inside; "Face" over a two-column grid of posters (8 apart, the name under
// each), "Layout" over the segmented row of the layouts the window tier allows, the spectrum under a Large art / Centered cover, "Background blur"
// (the lyrics' depth blur, 0 = off, 100) with its value, the Artist layout's photo rows, and the gallery's door at the foot. Literal numbers are
// allowed here (WVTH003 exempts `Rules\**`).

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's stage options popover.</summary>
internal static class WinUiStageOptionsGeometry
{
    /// <summary>The panel's widest and tallest extent and the margin it keeps from the stage's edges.</summary>
    public const float PanelMaxWidth = 440f, PanelMaxHeight = 720f, PanelEdgeInset = 48f;

    /// <summary>The panel's padding and the gap between its sections.</summary>
    public const float Pad = 16f, SectionGap = 16f, HeadGap = 8f;

    /// <summary>A section's body under its head (a segmented row, the slider with its two captions), for the panel's height budget.</summary>
    public const float SectionBodyH = 56f;

    /// <summary>The face grid: two columns, 8 apart, 16:9 posters, the name under each (12 / 16).</summary>
    public const int GridCols = 2;

    public const float GridGap = 8f, PosterAspectW = 16f, PosterAspectH = 9f, TileBorder = 2f, TileGap = 4f;

    public static readonly Edges4 TilePadding = Edges4.All(2f);

    public static readonly CornerRadius4 TileCorners = CornerRadius4.All(4f);

    public static readonly CornerRadius4 PosterCorners = CornerRadius4.All(4f);

    /// <summary>The face grid's tallest extent before it scrolls.</summary>
    public const float GridMaxH = 320f;

    /// <summary>Type: the section heads and the rows (12 / 16 caption, 14 / 20 body), the weights.</summary>
    public const float CaptionSize = 12f, CaptionLine = 16f, BodySize = 14f, BodyLine = 20f;

    public const ushort WeightRegular = 400, WeightSemiBold = 600;

    /// <summary>The blur slider: 0 .. 100 in steps of 5, its thickness.</summary>
    public const float BlurMin = 0f, BlurMax = 100f, BlurStep = 5f, SliderThickness = 24f;

    /// <summary>The Artist layout's photo dimming: 0 .. 100 in steps of 5.</summary>
    public const float DimMin = 0f, DimMax = 100f, DimStep = 5f;

    /// <summary>A toggle row's least height, the toggle's least width.</summary>
    public const float RowMinHeight = 32f, ToggleMinWidth = 40f;

    /// <summary>The foot: the gallery button (36 tall, 12 at the sides) and the G key cap.</summary>
    public const float FooterButtonH = 36f, FooterButtonPadX = 12f, FooterButtonGap = 8f, KeyCapSize = 11f;

    public static readonly Edges4 KeyCapPadding = new(6f, 1f, 6f, 1f);

    public static readonly CornerRadius4 KeyCapCorners = CornerRadius4.All(4f);
}
