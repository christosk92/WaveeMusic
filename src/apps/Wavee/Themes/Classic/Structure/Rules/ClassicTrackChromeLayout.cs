// ── Themes/Classic/Structure/Rules/ClassicTrackChromeLayout.cs — the numbers of Classic's track table chrome (themes plan 6.1, T8b) ──
//
// What `Entities/Track.Table.Chrome.cs` wrote as literals beside the header, the command bar, the filter card and the placeholder,
// moved verbatim with their pictures: boxes, glyph sizes, gaps, the filter card's rows and the motions of the bar and the header.
// They are data, not decisions: `Lists\ClassicTrackChrome.cs` reads them (WVTH003 allows literals only here, in `Rules\**`).
//
// WHAT IS NOT HERE. The header's height per row style (`TableRules.HeaderHeightFor`), the lane widths and the toolbar row height
// (`Detail.VerticalLayout`) are the core's: the sticky clip inset, the relief ladder and the hero's band run on them.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's track table chrome.</summary>
public static class ClassicTrackChromeLayout
{
    // ── the column header ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The header shifts right by the check lane's width while the lane is in.</summary>
    public const float CheckLaneShift = 28f;

    /// <summary>The duration lane's clock glyph.</summary>
    public const float ClockGlyph = 14f;

    /// <summary>The sort caret's glyph, its pop-in scale seed, and the word swap's rise.</summary>
    public const float CaretGlyph = 9f, CaretSeedScale = 0.3f, LabelRise = 4f;

    /// <summary>Where the header's rule starts (tuned Classic D9 / V10): at the row PLATES' left edge — a Modern row's
    /// <see cref="Track.RowMetrics.RowInset"/> skin margin, a full-width plate's 0 (a Classic row's, and a quiet list's: Liked Songs, D16 / D-9).
    /// The hero arm keeps the rows' gutter (its stacked rows are plain).</summary>
    public static float HeaderRuleInset(bool fullWidthPlates, bool vertical, float padX)
        => vertical ? padX : fullWidthPlates ? 0f : Track.RowMetrics.RowInset;

    /// <summary>The header shifting over for the check lane: the slow beat.</summary>
    public static readonly LayoutTransition HeaderShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));

    // ── the date group head (tuned Classic D16, board C-TrackList) ────────────────────────────────────────────────────

    /// <summary>The band: 40 tall, the words on its floor 6 up, 8 between the name and the count, both 13/18.</summary>
    public const float GroupHeadHeight = 40f, GroupHeadPadBottom = 6f, GroupHeadGap = 8f, GroupHeadSize = 13f, GroupHeadLine = 18f;

    // ── the command bar ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 32-DIP command height (a glyph button, a labelled command, the split button's halves).</summary>
    public const float CommandHeight = 32f;

    /// <summary>The command glyph and the gap between a glyph, its word and its trailing mark.</summary>
    public const float CommandGlyph = 14f, CommandGap = 6f;

    /// <summary>The split button's secondary (chevron) half and the divider between the halves.</summary>
    public const float SplitSecondaryWidth = 24f, SplitDividerHeight = 20f;

    /// <summary>The disclosure chevron after a flyout command.</summary>
    public const float ChevronGlyph = 8f;

    /// <summary>The page bar's lead (board C-Liked, tuned Classic D16): Shuffle's own lead after the split (with the split's 4 and the bar's 2,
    /// the board's 8), and the slot the divider before the chips is centred in.</summary>
    public const float PageShuffleLead = 2f, PageDividerSlot = 17f;

    /// <summary>The Filter funnel inside its icon-only button, and the count badge.</summary>
    public const float FunnelGlyph = 14f, BadgeSize = 18f, BadgeRadius = 9f;

    /// <summary>The count badge's ring and its nudges: on the funnel (4), on the funnel plate's corner (0), on "…" (3 out, 3 up).</summary>
    public const float BadgeRing = 1.5f, BadgeNudgeMore = 3f, BadgeNudgeCommand = 4f;

    /// <summary>The funnel's optical offsets (rest +1 down; with a badge -4 / +4 so glyph and pill never share ink).</summary>
    public const float FunnelRestY = 1f, FunnelBadgedX = -4f, FunnelBadgedY = 4f;

    /// <summary>The ONE command-box width the glyph-only plate and the "…" slot share.</summary>
    public const float PlateSize = 32f;

    /// <summary>The play-next command's glyph and word spacing.</summary>
    public const float PlayNextGlyph = 14f;

    // ── the bar at every tier (Tiers.Detail.cs, TC5) ─────────────────────────────────────────────────────────────────

    /// <summary>A command's height for the tier's <paramref name="target"/> edge (the tuned 32, the Compact 40).</summary>
    public static float CommandHeightFor(float target) => MathF.Max(CommandHeight, target);

    /// <summary>The glyph plate's edge for the tier's <paramref name="target"/> edge.</summary>
    public static float PlateFor(float target) => MathF.Max(PlateSize, target);

    /// <summary>The lane's vertical padding: the tuned 5, or what is left of the 44 lane around the tier's taller commands (Compact: 2).</summary>
    public static float SurfacePadYFor(float target)
        => MathF.Max(0f, MathF.Min(Detail.VerticalLayout.ToolbarSurfacePadY, (Detail.VerticalLayout.ToolbarRowHeight - CommandHeightFor(target)) / 2f));

    /// <summary>The selection bar's accent tint at Compact (board C-MultiSelect-Compact: the accent at 10 %).</summary>
    public const float SelectionTint = 0.10f;

    /// <summary>A command entering and leaving the bar (a rung evicts or promotes it).</summary>
    public static readonly LayoutTransition CommandMotion = new(TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 8f, Opacity: 0f, Active: true), Exit: new EnterExit(Dx: 8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

    /// <summary>The bar's browse / selection swap (the selection bar's enter and exit).</summary>
    public static readonly LayoutTransition ModeMotion = new(TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(210f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 10f, Opacity: 0f, Active: true), Exit: new EnterExit(Dx: -8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

    // ── the filter card ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card: 360 wide (never under 320), at most 620 tall; the scroll region (at most 500) is the fallback only.</summary>
    public const float CardWidth = 360f, CardMinWidth = 320f, CardMaxHeight = 620f, ScrollMaxHeight = 500f;

    /// <summary>A facet row: 44 tall, 8 in from either side, a 168-wide combo on the right, an 8 gap.</summary>
    public const float FacetRowHeight = 44f, FacetRowPadX = 8f, ComboWidth = 168f, FacetGap = 8f;

    /// <summary>The card's header row: 48 tall, "Filter" at 16, an 8 gap.</summary>
    public const float CardHeaderHeight = 48f, CardTitleSize = 16f, CardHeaderGap = 8f, FacetLabelSize = 14f;

    /// <summary>The card's header padding (16 left, 8 right) and the body groups' padding.</summary>
    public const float CardHeaderPadLeft = 16f, CardHeaderPadRight = 8f, GroupPadX = 8f, GroupPadTop = 4f, GroupPadBottom = 8f;
}
