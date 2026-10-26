// ── Themes/Classic/Structure/Rules/ClassicRecentsRules.cs — the numbers of Classic's Recents page (themes plan 6.1, T10c2) ──────────────────────────
//
// What `Entities\Recents.UI.cs` and `Entities\Recents.Page.cs` held beside the pictures they size: the group card's art and the Saved stack's tiles, the drawer's
// child lanes, the single-play arm's lanes, the masthead's reserved summary measure, the pivot's baseline, the calendar's legend swatches. They are data, not
// decisions (WVTH003 allows literals only in `Rules\**`). The grouped layout's own numbers (the day header's and the row's height, the calendar's cell and card
// geometry, the sticky push quantum) stay core in `RecentsLayout`: the layout, the rail and the calendar's grid read them.

using FluentGpu.Dsl;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's Recents page.</summary>
public static class ClassicRecentsRules
{
    // ── the masthead and the pivots ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The summary line's reserved measure (in lieu of tabular figures: the line never reflows) and the stagger's entering offset.</summary>
    public const float SummaryMinWidth = 220f, EnterDy = 10f;

    /// <summary>The pivot underline's height (a non-selected tab reserves the same baseline) and the focus ring's margin.</summary>
    public const float UnderlineHeight = 2f, PivotFocusMargin = 2f;

    /// <summary>The Small Overview button's height: the stock Small control (a taller tier asks the Medium control with its own floor).</summary>
    public const float OverviewSmallH = 24f;

    /// <summary>The underline's scaleX 0 → 1 enter, in milliseconds.</summary>
    public const float UnderlineEnterMs = 260f;

    // ── the group card ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card's art square, the Saved stack's member tile and context tile.</summary>
    public const float CardArt = 48f, SavedTile = 36f, SavedContext = 40f;

    /// <summary>The decode sizes of the context art and of a Saved member's cover.</summary>
    public const int ContextDecodePx = 96, MemberDecodePx = 80;

    /// <summary>The quiet name bar of a row whose target has no identity yet (width, height).</summary>
    public const float CardNameBar = 140f, CardNameBarHeight = 10f;

    /// <summary>The Saved check's size and the heat key's swatch edge.</summary>
    public const float CheckSize = 12f, SwatchEdge = 12f;

    // ── the Compact row (C-Recents-Compact) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The chevron glyph inside its 40 lane.</summary>
    public const float ChevronGlyph = 14f;

    /// <summary>The flush day rail's distance from the card's edge (C-Recents-Small: 4).</summary>
    public const float RailMarginRight = 4f;

    /// <summary>The day rail's fade when a tier crossing adds or removes it, in milliseconds.</summary>
    public const float RailFadeMs = 160f;

    // ── the Huge Overview aside (C-Recents-Huge) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A month card's plate in the aside: its padding (16 sides, 14 over, 16 under) and its 1-DIP card stroke.</summary>
    public const float OverviewCardPadX = 16f, OverviewCardPadTop = 14f, OverviewCardPadBottom = 16f, OverviewCardBorder = 1f;

    /// <summary>The air between the aside's "Overview" head and its month cards, and over the head (the board's 4 below the masthead's top).</summary>
    public const float OverviewHeadGap = 16f, OverviewTop = 4f;

    /// <summary>A plated month card's width: the calendar's 290 grid in its plate.</summary>
    public static float OverviewCardW => RecentsLayout.CalGridW + 2f * OverviewCardPadX;

    /// <summary>The aside's body fading in and out as the tier docks or undocks it (or the Overview toggle shows or hides it), in milliseconds.</summary>
    public const float OverviewFadeMs = 160f;

    // ── the single-play arm and the drawer's children ─────────────────────────────────────────────────────────────────────

    /// <summary>An episode member's drawer band: a 40 cover in a 56 row (the shared row cannot be shorter than its text column plus its padding).</summary>
    public const float ChildArt = 40f, ChildEpisodeMinHeight = 56f;

    /// <summary>The duration column, the single-play arm's number lane and its "…" lane, the drawer child's number lane.</summary>
    public const float DurationCol = 52f, SingleNumCol = 36f, SingleActionsCol = 40f, ChildNumCol = 30f;

    /// <summary>The played-at lane of a drawer child (a FIXED caption lane so every "…" aligns).</summary>
    public const float ChildWhenCol = 60f;

    /// <summary>The drawer child's actions column: "…" + gap + the played-at lane.</summary>
    public static float ChildActionsCol => 40f + Spacing.M + ChildWhenCol;
}
