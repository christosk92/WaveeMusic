// ── Themes/WinUi/Structure/Rules/WinUiArtistChartLayout.cs — the numbers of WinUI's artist chart (themes plan 6.2; P6 W3) ──────────────────
//
// The shelf's geometry (W-Artist: 60-tall rows touching, the one-line skin 48; the 264 card minimum is a BREAKPOINT: two columns when the chart column is 540
// wide), the row's boxes (the 24 rank cell, the gaps of the sub line and the trail, the credit line's gap), the selection pill, the pager's chevron and the check
// lane's motion. They are data, not decisions: `Lists\WinUiArtistChart.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). The tiers themselves (the
// art edge, whether the duration shows, whether the sub line stacks, per cell width, with hysteresis) are the core's: `ArtistPopularLayout`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's artist chart.</summary>
internal static class WinUiArtistChartLayout
{
    // ── the shelf ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row's height, and the one-line skin's.</summary>
    public const float RowHeight = 60f, OneLineRowHeight = 48f;

    /// <summary>The gap between cells (the rows touch) and between the header and the shelf.</summary>
    public const float CellGap = 0f, HeaderGap = 8f;

    /// <summary>The row's inset inside its plate and the gap between the title and its sub line.</summary>
    public const float RowInset = 6f, CopyGap = 1f;

    /// <summary>Two columns when the chart column is 540 wide: the card minimum is half of it less the gap.</summary>
    public const float ColumnBreak = 540f, MinCardWidth = (ColumnBreak - CellGap) / 2f;

    /// <summary>The shelf's edge fade.</summary>
    public const float EdgeFade = 16f;

    // ── the row ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The # cell's box (the rank, the equalizer, the transport on hover).</summary>
    public const float RankBox = 24f;

    /// <summary>The gap between the sub line's parts (the explicit mark, the film glyph, the credit, the plays) and between the trail's
    /// heart and duration; the credit line's own gap.</summary>
    public const float SubGap = 5f, TrailGap = 6f, FeatGap = 4f;

    /// <summary>The skeleton's art edge.</summary>
    public const float SkeletonArt = 40f;

    // ── the selection pill, the pager ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The single-selection pill: 3 x 16, a 1.5 radius, flush with the cell's leading edge.</summary>
    public const float PillWidth = 3f, PillHeight = 16f, PillRadius = 1.5f;

    /// <summary>The pager's own transparent chevron (not the stock 32 filled circle).</summary>
    public const float ChevronBox = 28f;

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A surviving part whose HEIGHT alone changes (the chart taking four rows past the Huge 4K rung, five below it): the layout lands once at the final
    /// height, the presented extent tweens from the old one and everything below rides the difference (the engine's flow reveal, vertical only).</summary>
    public static readonly LayoutTransition Resize = new(
        TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>The cells slide over when the check lane arrives (the track table's own tween).</summary>
    public static readonly LayoutTransition CheckShift = new(
        TransitionChannels.Position, TransitionDynamics.Tween(333f, Easing.FluentDecelerate));
}
