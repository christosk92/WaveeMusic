// ── Themes/Zune/Structure/Rules/ZuneRecentsRules.cs — the numbers of Zune's Recents page (themes plan 6.3; P5 Z7) ───────────────────────────
//
// Boards Z-Recents (Standard), -Compact, -Small, -Huge. The page has no masthead and no pivot strip: the band's row 2 carries the four pivots
// (all · music · podcasts · artists) and the "overview" word, published by the core host. What the page draws:
//
//   the list    day headings (22 light, a hairline, the count at the end; the core's 48 band, the sticky copy pinned over the rows), then the core's
//               64 rows: the kind word (56), the 48 art, the title over its caption, the time (64) and the chevron (32), the drawer under an open
//               row (40 track rows, 72 in). Small drops the kind word into the caption and sizes to the 52-ish board row; Compact stacks it and
//               makes the chevron a 40 target. The row height itself is the core's (`RecentsLayout.RowHeight`, 64): the measured layout, the
//               sticky band and the drawer's cached extent all read it, so no tier changes it.
//   overview    beside the list from Small: 220 (Small), 300 (Standard), 420 (Huge), 28 / 48 / 56 from the list. The month grids (7 equal columns,
//               cells 24 / 28 / 48 tall), the heat key, "jump to today" and the busiest day. Compact has none beside the list: the "overview" word
//               zooms the list out to the calendar (the host's semantic zoom), which every tier keeps.
//
// WVTH003 allows literals only here; WVTH013 keeps every width decision in the tier record (`ZuneRecentsTiers`, by CONTENT tier).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Which of the display roles a heading wears.</summary>
public enum ZuneRecentsHead : byte
{
    /// <summary>The 22/28 light voice.</summary>
    Small,
    /// <summary>The 26/32 light voice.</summary>
    Standard,
    /// <summary>The 40/48 light voice (the page hero).</summary>
    Large,
}

/// <summary>The numbers and node keys of Zune's Recents page.</summary>
public static class ZuneRecentsRules
{
    // ── the node keys (the tests find them) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The page, its column, the list column, the overview aside and its body, the month grid prefix, the kind lane.</summary>
    public const string PageKey = "zune-recents-page", ColumnKey = "zune-recents-column", ListKey = "zune-recents-list", AsideKey = "zune-recents-aside",
        OverviewKey = "zune-recents-overview", MonthPrefix = "zune-recents-month:", KindLaneKey = "zune-recents-kind";

    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list's foot (the shell clips above the player bar; this is breathing room).</summary>
    public const float PageBottom = 16f;

    /// <summary>A day heading: the gap between its label, its hairline and its count; the hairline's height; the hairline's floor (it never collapses
    /// beside a long label or count, so every heading wears the one form: label, rule, count).</summary>
    public const float DayGap = 16f, DayRule = 1f, DayRuleMin = 24f;

    /// <summary>The distance from a row's trailing edge to its chevron glyph's right edge, which a day heading's count aligns to: the row's pad, then
    /// (where the played-at stamp is not stacked) the "..." lane and the gap before it, then the chevron lane's air around its glyph.</summary>
    public static float CountInset(bool stacked, float chevronLane, float moreLane, float gap)
        => SurfaceGeometry.RowPad + (stacked ? 0f : moreLane + gap) + (chevronLane - ChevronGlyph) / 2f;

    // ── the row ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The art square (the surface's own floor), the Saved stack's member tile and context tile.</summary>
    public const float CardArt = 48f, SavedTile = 36f, SavedContext = 40f;

    /// <summary>The decode sizes of the context art and of a Saved member's cover.</summary>
    public const int ContextDecodePx = 96, MemberDecodePx = 80;

    /// <summary>The quiet name bar of a row whose target has no identity yet (width, height).</summary>
    public const float NameBar = 140f, NameBarHeight = 10f;

    /// <summary>The time lane's width and the chevron's glyph.</summary>
    public const float TimeLane = 64f, ChevronGlyph = 12f;

    /// <summary>The Saved check, the Saved line's weight, and the Saved stack's steps (the two member covers' x, and the context tile's drop).</summary>
    public const float CheckSize = 12f, SavedStepFirst = 6f, SavedStepSecond = 12f, SavedDrop = 8f;

    /// <summary>The Saved line's weight.</summary>
    public const ushort SavedWeight = 600;

    // ── the drawer ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drawer's left inset (under the art), its air under the last child, a child's art and its episode floor.</summary>
    public const float DrawerInset = 72f, DrawerPadBottom = 8f, ChildArt = 40f, ChildEpisodeMinHeight = 56f;

    /// <summary>A child's lanes: the number, the duration, the played-at stamp and the actions.</summary>
    public const float ChildNumCol = 36f, DurationCol = 52f, ChildWhenCol = 60f, ChildActionsCol = 40f;

    // ── the overview ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The aside's fade when a tier crossing adds or removes it, in milliseconds.</summary>
    public const float AsideFadeMs = 160f;

    /// <summary>The aside's top (under the band, level with the first day heading), the gap between its blocks, the gap in a month grid, the heat key's swatch.</summary>
    public const float AsideTop = 4f, AsideGap = 12f, GridGap = 4f, SwatchEdge = 12f, SwatchGap = 2f;

    /// <summary>The heat key's levels (the board's four swatches: quiet, light, medium, busy).</summary>
    public static readonly int[] KeyLevels = [0, 1, 3, 5];

    /// <summary>The heat's alpha floor (a level-1 day is this faint; level 5 is the accent itself).</summary>
    public const float HeatFloor = 0.3f;

    /// <summary>Today's ring.</summary>
    public const float TodayRing = 2f;

    /// <summary>A week's days, the heat's levels (0..5), and the level from which a numeral takes the accent's contrast ink.</summary>
    public const int Week = 7, HeatLevels = 6, NumeralInkFrom = 4;

    /// <summary>A month card in the zoomed-out calendar: its narrowest column and the gap under its title.</summary>
    public const float MonthMinW = 290f, MonthTitleGap = 8f;
}

/// <summary>What Zune's Recents page lays out in one CONTENT tier.</summary>
public sealed record ZuneRecentsLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public System.Collections.Immutable.ImmutableArray<TierRegion> Regions { get; init; } = System.Collections.Immutable.ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The kind word stands in its own lane before the art (56, Huge 72); 0 folds it into the caption (Small, Compact).</summary>
    public required float KindLane { get; init; }

    /// <summary>The row folds the played-at stamp under the caption and the trailing cluster is the chevron's lane alone (Compact).</summary>
    public required bool Stacked { get; init; }

    /// <summary>The chevron lane: 40 at Compact (a target), 32 elsewhere.</summary>
    public required float ChevronLane { get; init; }

    /// <summary>A drawer child's height: 44 at Compact, 36 at Small, 40 elsewhere.</summary>
    public required float ChildRowH { get; init; }

    /// <summary>The drawer's inset under the art.</summary>
    public required float DrawerInset { get; init; }

    /// <summary>The overview beside the list: its width (0: none, the zoom only) and its gap from the list.</summary>
    public required float AsideW { get; init; }

    /// <summary>See <see cref="AsideW"/>.</summary>
    public required float AsideGap { get; init; }

    /// <summary>How many months the aside shows.</summary>
    public required int AsideMonths { get; init; }

    /// <summary>A month grid's cell height in the aside.</summary>
    public required float CellH { get; init; }

    /// <summary>The aside's heading voice.</summary>
    public required ZuneRecentsHead AsideHead { get; init; }

    /// <summary>The day heading's voice.</summary>
    public required ZuneRecentsHead DayHead { get; init; }

    /// <summary>The month grids' gap inside the aside.</summary>
    public required float GridGap { get; init; }

    /// <summary>The zoomed-out calendar's cell height (40 at Compact: a target; 32 elsewhere) and the gap between its month cards.</summary>
    public required float CalendarCellH { get; init; }

    /// <inheritdoc cref="CalendarCellH"/>
    public required float CalendarGap { get; init; }

    /// <summary>The aside docks beside the list.</summary>
    public bool HasAside => AsideW > 0f;
}

/// <summary>Zune's Recents tier records (content tier; ZT7 tunes Compact, Small and Huge against their boards).</summary>
public static class ZuneRecentsTiers
{
    private static ZuneRecentsLayout Make(WidthTier tier, float kindLane, bool stacked, float chevron, float child, float drawer, float asideW, float asideGap,
        int months, float cellH, ZuneRecentsHead asideHead, ZuneRecentsHead dayHead, float gridGap, float calCellH, float calGap) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, KindLane = kindLane, Stacked = stacked, ChevronLane = chevron, ChildRowH = child, DrawerInset = drawer,
        AsideW = asideW, AsideGap = asideGap, AsideMonths = months, CellH = cellH, AsideHead = asideHead, DayHead = dayHead, GridGap = gridGap,
        CalendarCellH = calCellH, CalendarGap = calGap,
    };

    private static readonly ZuneRecentsLayout[] s_records =
    [
        // Compact (Z-Recents-Compact): the kind folds into the caption stack, a 40 chevron, 44 drawer rows, no aside (the overview word zooms).
        Make(WidthTier.Compact, kindLane: 0f, stacked: true, chevron: 40f, child: 44f, drawer: 56f, asideW: 0f, asideGap: 0f, months: 0, cellH: 0f,
            ZuneRecentsHead.Small, ZuneRecentsHead.Small, gridGap: 4f, calCellH: 40f, calGap: 16f),
        // Small (Z-Recents-Small): the kind in the caption, the time and a 28 chevron, 36 drawer rows, a 220 overview 28 from the list.
        Make(WidthTier.Small, kindLane: 0f, stacked: false, chevron: 32f, child: 36f, drawer: 60f, asideW: 220f, asideGap: 28f, months: 1, cellH: 24f,
            ZuneRecentsHead.Small, ZuneRecentsHead.Small, gridGap: 3f, calCellH: 32f, calGap: 24f),
        // Standard (Z-Recents): the tuned page, a 300 overview 48 from the list.
        Make(WidthTier.Standard, kindLane: 56f, stacked: false, chevron: 32f, child: 40f, drawer: 72f, asideW: 300f, asideGap: 48f, months: 2, cellH: 28f,
            ZuneRecentsHead.Standard, ZuneRecentsHead.Small, gridGap: 4f, calCellH: 32f, calGap: 40f),
        // Huge (Z-Recents-Huge): a 72 kind lane, 44 drawer rows, a 420 overview 56 from the list, 48 cells.
        Make(WidthTier.Huge, kindLane: 72f, stacked: false, chevron: 32f, child: 44f, drawer: 136f, asideW: 420f, asideGap: 56f, months: 2, cellH: 48f,
            ZuneRecentsHead.Large, ZuneRecentsHead.Standard, gridGap: 5f, calCellH: 32f, calGap: 40f),
    ];

    /// <summary>The record of content tier <paramref name="content"/> (Huge 4K reads Huge's: pages cap at 2400, decision 2).</summary>
    public static ZuneRecentsLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    /// <summary>The record for <paramref name="facts"/>: its content tier's, on the FRAME's gutter (the window tier's: the band, the title bar's wordmark and every page share
    /// one left edge, so a docked panel that narrows the content never moves it).</summary>
    public static ZuneRecentsLayout Of(in TierFacts facts)
    {
        var record = OfContent(in facts);
        float gutter = ZuneTiers.GutterOf(in facts);
        return facts.Policy is null || record.Gutter == gutter ? record : record with { Gutter = gutter };
    }

    private static ZuneRecentsLayout OfContent(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
