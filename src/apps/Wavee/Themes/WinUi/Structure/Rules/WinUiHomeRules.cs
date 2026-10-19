// ── Themes/WinUi/Structure/Rules/WinUiHomeRules.cs — the numbers of WinUI's Home (themes plan 6.2; P6 W5) ─────────────────────────────
//
// What the Home boards (W-Home, W-Home2, W-Home-Compact / -Small / -Huge / -Huge4K) draw beside the pictures they size: the pinned facet band (a 48 bar:
// the 40 words and the 4 the board pads each side), the chapter header (a 20/28 title over a 12/16 subtitle), the grids' tile geometry, the daylist's
// pieces. They are data, not decisions (WVTH003 allows literals only in `Rules\**`); the DECISIONS stay core and pure (`FacetForm`, `DaylistForm`,
// `DaypartTimeline`, `ShelfLead`, `RecentsCells`, `FacetDimPlan`...), and the views call them. The tier's records are `Tiers.Discovery.cs`.
//
// Public, so `Wavee.Tests` pins the numbers (the assembly has no InternalsVisibleTo).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's Home views.</summary>
public static class WinUiHomeRules
{
    // ── the facet band (W-Home2: a 48 bar, the words 40 tall inside it) ───────────────────────────────────────────────

    /// <summary>The selector row's height: the SelectorBar's 40 items (W-Tokens: 14/20 words padded 12, the pill under the word).</summary>
    public const float SelectorH = 40f;

    /// <summary>The air the board pads above and below the words once the band pins (W-Home2: the 48 bar).</summary>
    public const float BandPad = 4f;

    /// <summary>The pinned band's height: 48.</summary>
    public const float BandH = SelectorH + 2f * BandPad;

    /// <summary>The busy bar's slot: WinUI's 3-px ProgressBar, along the band's lower edge.</summary>
    public const float BarH = 3f;

    /// <summary>The hairline under a pinned band (W-Home2: white 8.4 %).</summary>
    public const float StuckLineH = 1f;

    /// <summary>The page column's <c>ScrollScope</c> name: the band pins against it (so the pin holds for the whole page).</summary>
    public const string PageScope = "home:page";

    /// <summary>The failure bar's slide-in distance (it enters 8 DIP above its rest).</summary>
    public const float FailureEnterDy = -8f;

    /// <summary>The Following button's glyph gap is the stock button's; the Compact divider before it is 1 x 20.</summary>
    public const float DividerW = 1f, DividerH = 20f;

    // ── the chapter geometry ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The chapter header: the title's 28 line over the subtitle's 16 line (W-Home: `h2` 20/28 and a 12-px span).</summary>
    public const float TitleLine = 28f, SubtitleLine = 16f;

    /// <summary>The header row's tools gap: the link, a 16-px divider and the pager.</summary>
    public const float ToolsDividerH = 16f;

    /// <summary>The header row's gap between its text and its tools.</summary>
    public const float HeaderGap = 12f;

    // ── the grid geometry per body ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A release row's height (W-Home2: 56, no plate at rest), a Jump back in tile's art, a cluster row's height and art, a browse tile's plate.</summary>
    public const float ReleaseRowH = 56f, ReleaseArt = 40f;
    public const float ClusterRowH = 48f, ClusterArt = 40f, ClusterAvatar = 24f;
    public const float TileArt = 48f, TileH = 64f;

    /// <summary>A browse tile (the coloured plate): its height and the title's inset.</summary>
    public const float BrowseTileH = 80f;

    /// <summary>The gaps of the grids: rows 4, columns 24 (releases), clusters and continue 16, tiles 16, the Jump back in tiles the tier's.</summary>
    public const float ReleaseColGap = 24f, ReleaseRowGap = 4f, CardGap = 16f;

    /// <summary>How many release rows, cluster cards and cluster rows a body shows.</summary>
    public const int ReleaseRowsMax = 6, ClusterMax = 6, ClusterRows = 3;

    /// <summary>The Recents cell's equalizer height, the history tile's 7-day strip box.</summary>
    public const float EqualizerH = 12f, HistoryStripW = 56f, HistoryStripH = 14f;

    /// <summary>The history strip's bar gap and floor.</summary>
    public const float SparkGap = 2f, SparkMinBar = 2f;

    /// <summary>The 2-span lead cell of a cover shelf (E21/E22): item 0 spans two columns once the shelf has three.</summary>
    public const int LeadSpan = 2, LeadMinColumns = 3;

    // ── the daylist card ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The decode target for the cover (the largest, 288, at 2x).</summary>
    public const int DaylistArtDecodePx = 576;

    /// <summary>The copy column's gap (W-Home: 6; Compact and Small 2) and the 12-px label rows.</summary>
    public const float CopyGapLarge = 6f, CopyGapSmall = 2f;

    /// <summary>At most this many tag links on the description line.</summary>
    public const int DaylistMaxTags = 6;

    /// <summary>The clock column's rows: the ring row, the gap to the five daypart rows (12), the rows' gap (10) and top air (4), the bar (4 high, 2 corner), the
    /// label / bar gap (12), the card's divider (the clock column's left hairline) and its inset.</summary>
    public const float ClockGap = 12f, DaypartGap = 10f, DaypartTop = 4f, DaypartBarH = 4f, DaypartLabelGap = 12f;

    /// <summary>The Compact footer's gap above the clock (12 and a hairline).</summary>
    public const float FooterPad = 12f;

    /// <summary>The ring's stroke is the stock ring's; its edge is the tier's.</summary>
    public const float RingSize = FluentGpu.Controls.ProgressRing.MinSize;

    // ── the "See all" drill page ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the "N items" line and the grid.</summary>
    public const float SectionBodyGap = 4f;

    /// <summary>The drill head's own line under the breadcrumb: the title (Title Large 40/52) and its sub line (the count).</summary>
    public const float DrillTitleLine = 52f;
}
