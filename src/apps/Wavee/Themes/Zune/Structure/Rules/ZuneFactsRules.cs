// ── Themes/Zune/Structure/Rules/ZuneFactsRules.cs — the numbers of Zune's facts bento, the insights panel's body (themes line p6) ─────
//
// No Zune board draws the insights (Q18). The numbers come from the boards' own vocabulary for a side panel (Z-Frame-Panel, Queue): a section
// is a small lowercase label in the tertiary ink over its content on the panel's wash, no plate, no border, no corner; sections stand apart by
// space. A hero figure is the theme's StatHero (40/48·200, the track drawer's figures) over a meta caption; the charts are flat and square, the
// accent on the bar that matters and a quiet neutral on the rest, the tempo a thin line over a hairline baseline with the median as a hairline;
// axis captions in the micro type (11/14). Every row the reader can lens or open (an artist, a legend entry, a pill, a disclosure, the one
// command) is 40 tall at every tier: the Compact target floor, which the sheet reaches when it covers a narrow page.

namespace Wavee.Themes.Zune;

/// <summary>The facts bento's numbers (<c>ZuneFacts</c>).</summary>
internal static class ZuneFactsRules
{
    /// <summary>A section's label-to-content gap, and the figure-to-chart gap.</summary>
    public const float SectionInnerGap = 8f, FigureChartGap = 12f;

    /// <summary>The head row: the gap between the label and its trailing fact (and between the two lines when the fact wraps under it).</summary>
    public const float HeadGap = 4f;

    /// <summary>The caption's gap under the figure.</summary>
    public const float FigureCaptionGap = 0f;

    /// <summary>The strips: the stage height, the gap between columns, a silent bucket's stub, the rest bars' neutral alpha.</summary>
    public const float SparkHeight = 56f, SparkGap = 4f, SparkMinBar = 2f, SparkRestAlpha = 0.16f;

    /// <summary>The tempo plot: the stage height, the ridge's (very light) fill, the line, the floor under which the line is not drawn, the
    /// axis's micro type, its row and its gap under the stage.</summary>
    public const float DensityHeight = 56f, DensityAreaAlpha = 0.06f, DensityLine = 1.25f, DensityLineFloor = 0.015f;
    public const float AxisSize = 11f, AxisHeight = 14f, AxisGap = 4f, Hairline = 1f;

    /// <summary>A lensable row (an artist, the "N more" disclosure, a pill, a legend entry): its height and the gap inside it.</summary>
    public const float RowH = 40f, RowInnerGap = 8f, LegendInnerGap = 6f;

    /// <summary>The face pile to the names, the legend's and the pills' gaps.</summary>
    public const float ArtistGap = 12f, LegendGap = 12f, PillGap = 16f;

    /// <summary>The blend bar: its height and the gap between segments; the legend swatch.</summary>
    public const float BlendBarH = 6f, BlendSegGap = 2f, Swatch = 10f;

    /// <summary>A pill's glyph, its stroke and the 24-unit view box the facts' stroked glyphs are drawn in.</summary>
    public const float PillGlyph = 14f, PillGlyphStroke = 1.5f, GlyphViewBox = 24f;

    /// <summary>The list bar's insights mark (three square bars on a baseline, the theme's thin line).</summary>
    public const float ToggleStroke = 1.5f;
}
