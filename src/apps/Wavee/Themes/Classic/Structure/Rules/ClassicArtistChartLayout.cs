// ── Themes/Classic/Structure/Rules/ClassicArtistChartLayout.cs — the numbers of Classic's artist chart (themes plan 6.1, T8c) ────
//
// What `Entities/Artist.UI.Chart.cs` wrote as literals beside the chart (ArtistPopular.cs:88-108), moved verbatim with its picture: the
// shelf's geometry (Modern 56 rows on a 12 gap, Classic 48 on 8; the 264 card minimum is a BREAKPOINT: two columns when the chart
// column is 540 wide), the row's boxes (the 24 rank cell, the gaps of the sub line and the trail, the credit line's gap), the
// selection pill, the pager's chevron and the check lane's motion. They are data, not decisions: `Lists\ClassicArtistChart.cs` reads
// them (WVTH003 allows literals only here, in `Rules\**`). The tiers themselves (the art edge, whether the duration shows, whether the
// sub line stacks, per cell width, with hysteresis) are the core's: `ArtistPopularLayout`.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's artist chart.</summary>
internal static class ClassicArtistChartLayout
{
    // ── the shelf ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Modern's row height and Classic's.</summary>
    public const float RowHeight = 56f, ClassicRowHeight = 48f;

    /// <summary>The gap between cells (Modern) and between the header and the shelf (Modern); Classic uses <c>Spacing.S</c> for both.</summary>
    public const float CellGap = 12f, HeaderGap = 10f;

    /// <summary>Two columns when the chart column is 540 wide: the card minimum is half of it less the gap.</summary>
    public const float ColumnBreak = 540f, MinCardWidth = (ColumnBreak - CellGap) / 2f;

    /// <summary>The shelf's edge fade.</summary>
    public const float EdgeFade = 16f;

    // ── the row ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title rung: 14 (600).</summary>
    public const float TitleSize = 14f;

    /// <summary>The # cell's box (the rank, the equalizer, the transport on hover).</summary>
    public const float RankBox = 24f;

    /// <summary>The gap between the sub line's parts (the explicit mark, the film glyph, the credit, the plays) and between the trail's
    /// heart and duration; the credit line's own gap.</summary>
    public const float SubGap = 5f, TrailGap = 6f, FeatGap = 4f;

    /// <summary>The skeleton's art edge for a Modern row.</summary>
    public const float SkeletonArt = 44f;

    // ── the selection pill, the pager ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The single-selection pill: 3 x 16, a 1.5 radius (Modern only), 2 DIP in from the cell's leading edge.</summary>
    public const float PillWidth = 3f, PillHeight = 16f, PillRadius = 1.5f, PillLead = 2f;

    /// <summary>The pager's own transparent chevron (not the stock 32 filled circle).</summary>
    public const float ChevronBox = 28f;

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cells slide over when the check lane arrives (the track table's own tween).</summary>
    public static readonly LayoutTransition CheckShift = new(
        TransitionChannels.Position, TransitionDynamics.Tween(333f, Easing.FluentDecelerate));
}
