// ── Themes/Classic/Structure/Rules/ClassicDiscographyRules.cs — the numbers of Classic's discography (themes plan 6.1, T10b2) ─────
//
// What `Entities\Artist.Discography.cs` wrote as literals beside the facet section, the grid, the cards, the album drawer and the standalone page, moved
// verbatim with their pictures (`Lists\ClassicDiscography.cs`, `Views\Pages\ClassicDiscographyPage.cs`). They are data, not decisions: the drawer's
// verdict (`Album.DrawerVerdict`: the gaps, the per-column cap, the heights) stays core and sizes the tier's geometry the artist records name
// (`ClassicArtistLayout.Drawer`: the columns, the pitch, the head), and the views read them.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>Classic's discography numbers.</summary>
internal static class ClassicDiscographyRules
{
    // ── the facet section ──
    /// <summary>The facet header row's own height; with the band above it, the grid's sticky clip inset.</summary>
    public const float FacetHeaderRowH = 40f;

    /// <summary>The header's accent spine: its width and its minimum height.</summary>
    public const float SpineW = 3f, SpineMinH = 22f;

    /// <summary>The expander's chevron box.</summary>
    public const float ChevronBox = 28f;

    // ── the grid ──
    /// <summary>The grid's column minimum.</summary>
    public const float MinColumn = 180f;

    /// <summary>The grid's gap (the artist page's skeleton row of seeds uses it too).</summary>
    public static float Gap => Spacing.L;

    /// <summary>The grid surface's height is the cell width + 50 (the square cover is the cell less its 2 × 8 padding, over the 8 gap, the title +
    /// caption lines and the 12 bottom pad): one uniform card height, so the drawer's hug spacing is exact; the seed face pinned to it is exactly as tall.
    /// RowGap is the vertical gutter, folded into the LazyGrid row extra.</summary>
    public const float CardChrome = 50f, RowGap = 20f;

    /// <summary>The card subtitle's chevron cue and its gap after the meta.</summary>
    public const float SubtitleGap = 4f, SubtitleGlyph = 9f;

    // ── the drawer ──
    /// <summary>The caret under the opened card: its width, its height, the 1 DIP it sinks to hide the panel's top stroke, and its stroke.</summary>
    public const float CaretW = 16f, CaretH = 8f, CaretOverlap = 1f, CaretStroke = 1f;

    /// <summary>The panel's padding (horizontal, vertical) and its border.</summary>
    public const float PanelPadX = 12f, PanelPadY = 6f, PanelBorder = 1f;

    /// <summary>The head row's play button, the cover and its decode, the open button and its roundness (a circle), and its glyph (the row's height and
    /// the Compact 40 buttons are the artist records': `ClassicArtistLayout.DrawerHead`, `.Target`).</summary>
    public const float HeadPlay = 32f, HeadCover = 28f, HeadOpen = 28f, HeadOpenRound = 0.5f, HeadOpenGlyph = 11.76f, HeadBorder = 1f;
    public const int HeadCoverDecode = 56;

    /// <summary>The rows' columns: # 26 · heart · title ★ · time 44 · … 32 at the tuned pitch; C-Discography-Compact's # 24 · time 40 · … 40; the air the pitch
    /// keeps over a row's content (28 in 32).</summary>
    public const float RankCol = 26f, TimeCol = 44f, MoreCol = 32f, RankColCompact = 24f, TimeColCompact = 40f, MoreColCompact = 40f, RowAir = 4f;

    /// <summary>The shimmer row's bars: the rank, the title (and its cap), the time, the trailing air; their height and corner.</summary>
    public const float ShimmerRank = 16f, ShimmerTitleMax = 240f, ShimmerTime = 30f, ShimmerTrail = 32f, ShimmerBarH = 11f, ShimmerRadius = 4f;

    /// <summary>The check lane's leading margin in a row.</summary>
    public const float CheckLead = 4f;

    /// <summary>The single-selection pill: 3 x 16, a 1.5 radius, 2 DIP in from the slot's leading edge.</summary>
    public const float PillW = 3f, PillH = 16f, PillRadius = 1.5f, PillLead = 2f;

    /// <summary>The cells slide over when the check lane arrives (the track table's own tween).</summary>
    public static readonly LayoutTransition CheckShift = new(
        TransitionChannels.Position, TransitionDynamics.Tween(333f, Easing.FluentDecelerate));

    // ── the page ──
    /// <summary>The standalone page's grid reveal inset: there is no sticky facet band on that page.</summary>
    public const float PageTopInset = 28f;
}
