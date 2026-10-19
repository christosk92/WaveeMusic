// ── Themes/WinUi/Structure/Rules/Tiers.Discovery.cs — WinUI's Home per content tier, and the discovery pages' shared bands (themes plan 3.6, 6.2; P6 W5) ──
//
// ONE record per CONTENT tier (`ctx.Tier().Content`: the region the page lives in), read through `WinUiHomeTiers.Of(ctx.Tier())`; a view never compares a width
// with a number (WVTH013). Boards: W-Home-Compact, W-Home-Small, W-Home (Standard), W-Home-Huge, W-Home-Huge4K, W-Home2 (the sticky facet row).
//
//   tier      greeting   daylist                                         Jump back in           shelves (covers)          pairs
//   Compact   28/36      stack: 96 cover, copy, 40 buttons, clock footer  1 column, 4 apart      2 columns, 12 apart       none
//   Small     28/36      120 cover, copy, the clock a line on the eyebrow 2 columns, 8 apart     4 columns, 12 apart       none
//   Standard  40/52      272 cover | copy | 300 clock rows (24 apart)     4 columns, 8 apart     6 columns, 16 apart       none
//   Huge      40/52      288 cover | copy | 320 clock rows                6 columns, 8 apart     10 columns, 20 apart;     daylist 2 : releases 1;
//                                                                                                 14 once the page is wide  clusters 1 : continue 1
//
// NO PAGE CAP at Huge or 4K (owner Q2): the grids fill the region, and the shelves' column counts are floor-fits inside the tier (`WinUiShelfBand`: a
// minimum card and a most-columns cap), so 2560 holds 10 covers and 3840 with a docked pane holds 14. The page's insets are the frame's
// (`WinUiTiers.InsetOf`: 16 / 24 / 36 / 48 by window tier), never a number here.
//
// A COLUMN CHANGE FLIPS: every grid cell and zone carries `WinUiDiscoveryMotion.Shove` / `Reflow` (250 ms), so a width change moves the cards to their new
// tracks instead of snapping them.
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>A grid's column rule inside one tier: as many columns as fit at <see cref="MinCol"/>, never more than <see cref="MaxCols"/>.
/// A <see cref="MinCol"/> of 0 is a fixed count (<see cref="MaxCols"/>).</summary>
/// <param name="MinCol">The narrowest column; 0 for a fixed count.</param>
/// <param name="MaxCols">The most columns.</param>
public readonly record struct WinUiGridBand(float MinCol, int MaxCols)
{
    /// <summary>The columns at <paramref name="width"/>, 1..<see cref="MaxCols"/>. A non-positive width (not measured yet) is one column.</summary>
    public int Columns(float width, float gap = 0f)
    {
        if (MinCol <= 0f) return Math.Max(1, MaxCols);
        if (width <= 0f) return 1;
        return Math.Clamp((int)MathF.Floor((width + gap) / (MinCol + gap)), 1, Math.Max(1, MaxCols));
    }

    /// <summary>The floor to hand a <c>GridEl</c>'s <c>MinColWidth</c>: the band's own, or 1 for a fixed count (the engine's auto-fill must see a positive floor).</summary>
    public float Floor => MathF.Max(1f, MinCol);

    /// <summary>A fixed count of <paramref name="columns"/>.</summary>
    public static WinUiGridBand Fixed(int columns) => new(0f, columns);
}

/// <summary>A paged shelf's fit inside one tier: <c>PagedShelf</c>'s minimum / maximum card and maximum columns, or one fixed card width, the gap between
/// cards, and whether the shelf shows its own pager.</summary>
/// <param name="MinCardW">The narrowest card.</param>
/// <param name="MaxCardW">The widest card.</param>
/// <param name="MaxColumns">The most columns; 0 is unlimited.</param>
/// <param name="FixedCardW">A fixed card width; 0 fits.</param>
/// <param name="Gap">The gap between cards.</param>
/// <param name="Pager">The shelf's chevrons and pips are shown.</param>
public readonly record struct WinUiShelfBand(float MinCardW, float MaxCardW, int MaxColumns, float FixedCardW, float Gap, bool Pager);

/// <summary>How the daylist card is built in a tier.</summary>
public enum WinUiDaylistForm : byte
{
    /// <summary>Standard and Huge (W-Home, W-Home-Huge): the cover | the copy | the clock column behind a hairline.</summary>
    Triple,
    /// <summary>Small (W-Home-Small): a 120 cover beside the copy, the clock one line at the eyebrow's end.</summary>
    Pair,
    /// <summary>Compact (W-Home-Compact): the 96 cover and the title block over the tags, the actions and the clock footer.</summary>
    Stack,
}

/// <summary>How much of the daylist clock a tier draws.</summary>
public enum WinUiClockForm : byte
{
    /// <summary>Standard and Huge: the ring and two lines over the five daypart rows (label, then a 4-px bar).</summary>
    Rows,
    /// <summary>Small: the 16 ring and "Next in 01:35:05" on the eyebrow's line.</summary>
    Line,
    /// <summary>Compact: the 24 ring and the two lines under a hairline, at the card's foot.</summary>
    Footer,
}

/// <summary>Where the Following button stands.</summary>
public enum WinUiFollowingPlace : byte
{
    /// <summary>At the facet row's end (Standard, Huge).</summary>
    FacetRowEnd,
    /// <summary>On the greeting's row, at its end (Small).</summary>
    GreetingRow,
    /// <summary>On the facet row after a hairline, 40 tall (Compact).</summary>
    FacetRowAfterDivider,
}

/// <summary>What the daylist card is in one tier.</summary>
public sealed record WinUiDaylistLayout
{
    /// <summary>The card's form.</summary>
    public required WinUiDaylistForm Form { get; init; }

    /// <summary>The cover's edge: 272 / 288 (Triple), 120 (Pair), 96 (Stack).</summary>
    public required float Cover { get; init; }

    /// <summary>The card's padding: 24 (Triple), 16 (Pair, Stack).</summary>
    public required float Pad { get; init; }

    /// <summary>The gap between the cover, the copy and the clock column.</summary>
    public required float Gap { get; init; }

    /// <summary>The clock column's width (Triple): 300 / 320; 0 where the clock is not a column.</summary>
    public required float ClockW { get; init; }

    /// <summary>The title is Title Large 40/52 (Triple); false is Subtitle 20/28 (Pair, Stack).</summary>
    public required bool TitleLarge { get; init; }

    /// <summary>The title's most lines (Triple wraps to three, Pair and Stack keep one or two).</summary>
    public required int TitleLines { get; init; }

    /// <summary>The buttons' size: 40 tall at Compact, the control's 32 elsewhere.</summary>
    public required ControlSize Buttons { get; init; }

    /// <summary>The save and more buttons' box: 40 at Compact, 32 elsewhere.</summary>
    public required float IconBox { get; init; }

    /// <summary>The clock's form.</summary>
    public required WinUiClockForm Clock { get; init; }

    /// <summary>The ring's edge: 28 (Rows), 24 (Footer), 16 (Line).</summary>
    public required float Ring { get; init; }

    /// <summary>The five dayparts' label column (Rows): 92 at Standard, the board's `width: 92px`.</summary>
    public required float LabelW { get; init; }

    /// <summary>The play button's side padding is the board's 16 (Standard) / 20 (Compact); the stock button's own is kept where 0.</summary>
    public required bool PlayGrows { get; init; }
}

/// <summary>What WinUI's Home is in one content tier.</summary>
public sealed record WinUiHomeLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (below 720 tall).</summary>
    public required bool Short { get; init; }

    /// <summary>The greeting is Title Large 40/52 (Standard, Huge); false is Title 28/36 (Compact, Small).</summary>
    public required bool GreetingLarge { get; init; }

    /// <summary>The step from the greeting to the facet row (8 at Standard and Huge, 4 elsewhere), less the band's own 4.</summary>
    public required float GreetingGap { get; init; }

    /// <summary>The step from the facet band to the first zone (24 less the band's 4 below its words: 20; 12 at Small, 16 at Compact less the same).</summary>
    public required float HeadGap { get; init; }

    /// <summary>The step between two zones (24 / 12 / 16).</summary>
    public required float ZoneGap { get; init; }

    /// <summary>The step from a chapter's header to its body (12; 8 at Small and Compact).</summary>
    public required float HeaderGap { get; init; }

    /// <summary>Where the Following button stands.</summary>
    public required WinUiFollowingPlace Following { get; init; }

    /// <summary>The Following button's height: 32; 40 at Compact (the target floor).</summary>
    public required float FollowingH { get; init; }

    /// <summary>The chapter headers carry their subtitles under the title (false at Compact: <c>W-Home-Compact</c> draws the titles alone).</summary>
    public required bool HeaderSubtitles { get; init; }

    /// <summary>A chapter header's link and tools are 40 tall (Compact: the target floor); 32 elsewhere.</summary>
    public required float HeaderToolsH { get; init; }

    /// <summary>The failed-facet InfoBar's height (72 at Compact: the touch floor); 0 keeps the stock bar's own.</summary>
    public required float FailureBarH { get; init; }

    /// <summary>The failed-facet InfoBar's Retry button: 40 tall (Large) at Compact, the control's 32 elsewhere.</summary>
    public required ControlSize FailureRetry { get; init; }

    /// <summary>The daylist card.</summary>
    public required WinUiDaylistLayout Daylist { get; init; }

    /// <summary>The daylist and the release list share one row (Huge: 2 : 1, <c>W-Home-Huge</c>).</summary>
    public required bool DaylistBeside { get; init; }

    /// <summary>"Because you like" and "Continue listening" share one row, a half each (Huge).</summary>
    public required bool ClustersBeside { get; init; }

    /// <summary>A cover shelf (the square cards).</summary>
    public required WinUiShelfBand Shelf { get; init; }

    /// <summary>The first cover of a shelf may span two cells (E21) while the shelf has at least three columns.</summary>
    public required bool ShelfLead { get; init; }

    /// <summary>A wide shelf (the 16:9 tiles).</summary>
    public required WinUiShelfBand WideShelf { get; init; }

    /// <summary>The Jump back in tiles' columns.</summary>
    public required WinUiGridBand Recents { get; init; }

    /// <summary>The gap between two Jump back in tiles (8; the column gap).</summary>
    public required float RecentsGap { get; init; }

    /// <summary>The Jump back in tile: the art edge and the row height (64 with 48 art).</summary>
    public required float RecentsArt { get; init; }

    /// <summary>The Jump back in tile's row height.</summary>
    public required float RecentsRowH { get; init; }

    /// <summary>The release rows' columns.</summary>
    public required WinUiGridBand Releases { get; init; }

    /// <summary>The cluster cards' columns.</summary>
    public required WinUiGridBand Clusters { get; init; }

    /// <summary>The browse tiles' columns.</summary>
    public required WinUiGridBand Tiles { get; init; }

    /// <summary>The episode rows' columns (a lead and a list).</summary>
    public required WinUiGridBand Episodes { get; init; }

    /// <summary>The continue-listening cards' columns.</summary>
    public required WinUiGridBand Continue { get; init; }

    /// <summary>The "See all" drill page's narrowest grid cell.</summary>
    public required float SectionMinCell { get; init; }

    /// <summary>The rows the Jump back in grid fills, the history tile included: 2 (Compact's single column draws four).</summary>
    public required int RecentsRows { get; init; }

    /// <summary>The Jump back in grid's played tiles at <paramref name="cols"/> columns: two full rows less the history tile, never more than the eight tiles the zone has.</summary>
    public int RecentsCells(int cols, int available)
    {
        int want = Math.Max(0, RecentsRows * Math.Max(1, cols) - 1);
        return Math.Min(want, Math.Max(0, available));
    }
}

/// <summary>WinUI's Home records and the first-frame estimate every discovery page seeds its width-driven boxes with.</summary>
public static class WinUiHomeTiers
{
    /// <summary>The record for <paramref name="facts"/>: the content tier's, the Short variant when the window is Short. Standard's where no frame resolved the
    /// tiers (a view mounted alone).</summary>
    public static WinUiHomeLayout Of(in TierFacts facts)
        => For(facts.Policy is null ? WidthTier.Standard : facts.Content, shortWindow: facts.Policy is not null && facts.Height == HeightTier.Short);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's: WinUI has no 4K band, the grids just fit more columns).</summary>
    public static WinUiHomeLayout For(WidthTier content, bool shortWindow = false)
    {
        var tier = (WidthTier)Math.Min((int)content, (int)WidthTier.Huge);
        var layout = tier switch
        {
            WidthTier.Compact => Compact(),
            WidthTier.Small => Small(),
            WidthTier.Standard => Standard(),
            _ => Huge(),
        };
        // A Short window (below 720 tall): the cover steps down so the card is not a screenful (W-Tiers: "Below 720 window height the short hero is used").
        return shortWindow ? layout with { Short = true, Daylist = layout.Daylist with { Cover = MathF.Min(layout.Daylist.Cover, ShortCover) } } : layout;
    }

    /// <summary>The daylist cover in a Short window.</summary>
    public const float ShortCover = 144f;

    /// <summary>The gap between two cover cards of a shelf at the tier (Compact 12, Small 12, Standard 16, Huge 20).</summary>
    private const float CoverGap = 16f;

    private static WinUiHomeLayout Compact() => new()
    {
        Tier = WidthTier.Compact, Short = false,
        GreetingLarge = false, GreetingGap = 0f, HeadGap = 12f, ZoneGap = 16f, HeaderGap = 8f,
        Following = WinUiFollowingPlace.FacetRowAfterDivider, FollowingH = 40f, HeaderSubtitles = false, HeaderToolsH = 40f,
        FailureBarH = 72f, FailureRetry = ControlSize.Large,
        Daylist = new WinUiDaylistLayout
        {
            Form = WinUiDaylistForm.Stack, Cover = 96f, Pad = 16f, Gap = 16f, ClockW = 0f, TitleLarge = false, TitleLines = 2,
            Buttons = ControlSize.Large, IconBox = 40f, Clock = WinUiClockForm.Footer, Ring = 24f, LabelW = 0f, PlayGrows = false,
        },
        DaylistBeside = false, ClustersBeside = false,
        Shelf = new WinUiShelfBand(120f, 9999f, 2, FixedCardW: 0f, Gap: 12f, Pager: false), ShelfLead = false,
        WideShelf = new WinUiShelfBand(220f, 9999f, 1, FixedCardW: 0f, Gap: 12f, Pager: false),
        Recents = WinUiGridBand.Fixed(1), RecentsRows = 4, RecentsGap = 4f, RecentsArt = 48f, RecentsRowH = 64f,
        Releases = WinUiGridBand.Fixed(1), Clusters = WinUiGridBand.Fixed(1), Tiles = WinUiGridBand.Fixed(2),
        Episodes = WinUiGridBand.Fixed(1), Continue = WinUiGridBand.Fixed(1),
        SectionMinCell = 124f,
    };

    private static WinUiHomeLayout Small() => new()
    {
        Tier = WidthTier.Small, Short = false,
        GreetingLarge = false, GreetingGap = 0f, HeadGap = 8f, ZoneGap = 12f, HeaderGap = 8f,
        Following = WinUiFollowingPlace.GreetingRow, FollowingH = 32f, HeaderSubtitles = true, HeaderToolsH = 32f,
        FailureBarH = 0f, FailureRetry = ControlSize.Medium,
        Daylist = new WinUiDaylistLayout
        {
            Form = WinUiDaylistForm.Pair, Cover = 120f, Pad = 16f, Gap = 16f, ClockW = 0f, TitleLarge = false, TitleLines = 1,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = WinUiClockForm.Line, Ring = 16f, LabelW = 0f, PlayGrows = false,
        },
        DaylistBeside = false, ClustersBeside = false,
        Shelf = new WinUiShelfBand(120f, 9999f, 4, FixedCardW: 0f, Gap: 12f, Pager: true), ShelfLead = true,
        WideShelf = new WinUiShelfBand(180f, 9999f, 3, FixedCardW: 0f, Gap: 12f, Pager: true),
        Recents = WinUiGridBand.Fixed(2), RecentsRows = 2, RecentsGap = 8f, RecentsArt = 48f, RecentsRowH = 64f,
        Releases = new WinUiGridBand(280f, 2), Clusters = new WinUiGridBand(260f, 2), Tiles = new WinUiGridBand(180f, 3),
        Episodes = new WinUiGridBand(280f, 2), Continue = new WinUiGridBand(260f, 2),
        SectionMinCell = 156f,
    };

    private static WinUiHomeLayout Standard() => new()
    {
        Tier = WidthTier.Standard, Short = false,
        GreetingLarge = true, GreetingGap = 4f, HeadGap = 20f, ZoneGap = 24f, HeaderGap = 12f,
        Following = WinUiFollowingPlace.FacetRowEnd, FollowingH = 32f, HeaderSubtitles = true, HeaderToolsH = 32f,
        FailureBarH = 0f, FailureRetry = ControlSize.Medium,
        Daylist = new WinUiDaylistLayout
        {
            Form = WinUiDaylistForm.Triple, Cover = 272f, Pad = 24f, Gap = 24f, ClockW = 300f, TitleLarge = true, TitleLines = 3,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = WinUiClockForm.Rows, Ring = 28f, LabelW = 92f, PlayGrows = false,
        },
        DaylistBeside = false, ClustersBeside = false,
        Shelf = new WinUiShelfBand(150f, 9999f, 6, FixedCardW: 0f, Gap: CoverGap, Pager: true), ShelfLead = true,
        WideShelf = new WinUiShelfBand(220f, 9999f, 4, FixedCardW: 0f, Gap: CoverGap, Pager: true),
        Recents = new WinUiGridBand(240f, 4), RecentsRows = 2, RecentsGap = 8f, RecentsArt = 48f, RecentsRowH = 64f,
        Releases = new WinUiGridBand(320f, 2), Clusters = new WinUiGridBand(300f, 3), Tiles = new WinUiGridBand(220f, 4),
        Episodes = new WinUiGridBand(320f, 2), Continue = new WinUiGridBand(300f, 3),
        SectionMinCell = 176f,
    };

    // Huge (W-Home-Huge, W-Home-Huge4K): no page cap. The covers fit as many columns as 190 allows up to 14 (10 at 2560, 14 at 3840 beside a docked pane).
    private static WinUiHomeLayout Huge() => new()
    {
        Tier = WidthTier.Huge, Short = false,
        GreetingLarge = true, GreetingGap = 4f, HeadGap = 20f, ZoneGap = 24f, HeaderGap = 12f,
        Following = WinUiFollowingPlace.FacetRowEnd, FollowingH = 32f, HeaderSubtitles = true, HeaderToolsH = 32f,
        FailureBarH = 0f, FailureRetry = ControlSize.Medium,
        Daylist = new WinUiDaylistLayout
        {
            Form = WinUiDaylistForm.Triple, Cover = 288f, Pad = 24f, Gap = 24f, ClockW = 320f, TitleLarge = true, TitleLines = 3,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = WinUiClockForm.Rows, Ring = 28f, LabelW = 92f, PlayGrows = false,
        },
        DaylistBeside = true, ClustersBeside = true,
        Shelf = new WinUiShelfBand(190f, 9999f, 14, FixedCardW: 0f, Gap: 20f, Pager: true), ShelfLead = true,
        WideShelf = new WinUiShelfBand(300f, 9999f, 8, FixedCardW: 0f, Gap: 20f, Pager: true),
        Recents = new WinUiGridBand(300f, 6), RecentsRows = 2, RecentsGap = 8f, RecentsArt = 48f, RecentsRowH = 64f,
        Releases = WinUiGridBand.Fixed(1), Clusters = new WinUiGridBand(260f, 3), Tiles = new WinUiGridBand(220f, 7),
        Episodes = new WinUiGridBand(320f, 3), Continue = new WinUiGridBand(260f, 3),
        SectionMinCell = 190f,
    };

    /// <summary>The daylist's share of the Huge row against the release list beside it (2 : 1) and the gap between them.</summary>
    public const float HeroShare = 2f, HeroGap = 24f;
}

/// <summary>The first-frame width of a discovery page's region: what a width-driven box is built at before it has measured itself (plan 3.3 rule 1).</summary>
public static class WinUiRegion
{
    /// <summary>The nav rail Small keeps in front of the page, and the pane Standard and Huge open at: only the chrome a first frame cannot read yet.</summary>
    private const float RailW = WinUiTiers.RailW, PaneW = WinUiTiers.PaneW;

    /// <summary>The page region's width on its first frame: the same-frame <paramref name="viewportWidth"/> less the chrome in front of the page, less both
    /// insets. Exact when the published facts are current, a nearest estimate otherwise: always inside the right tier.</summary>
    public static float EstimateWidth(in TierFacts facts, float viewportWidth)
    {
        float inset = WinUiTiers.InsetOf(in facts);
        if (viewportWidth <= 0f) viewportWidth = facts.ViewportWidth;
        float content;
        if (facts.ContentWidth > 0f && MathF.Abs(facts.ViewportWidth - viewportWidth) < 0.5f)
            content = facts.ContentWidth;
        else
        {
            var window = facts.Policy is null ? WidthTier.Standard : facts.Window;
            float chrome = window switch { WidthTier.Compact => 0f, WidthTier.Small => RailW, _ => PaneW };
            content = MathF.Max(0f, viewportWidth - chrome);
        }
        return MathF.Max(0f, content - 2f * inset);
    }

    /// <summary>The width of one band of Home on its first frame: the page region, and at a tier whose daylist sits beside the release list (Huge) the hero's
    /// or the list's share of it.</summary>
    public static float EstimateHomeBand(in TierFacts facts, float viewportWidth, bool hero)
    {
        var layout = WinUiHomeTiers.Of(facts);
        float page = EstimateWidth(facts, viewportWidth);
        if (!layout.DaylistBeside) return page;
        float share = hero ? WinUiHomeTiers.HeroShare : 1f;
        return MathF.Max(0f, (page - WinUiHomeTiers.HeroGap) * share / (WinUiHomeTiers.HeroShare + 1f));
    }
}

/// <summary>How a discovery part moves when the window crosses a tier or a grid takes another column count (W5: every FLIP is 250 ms).</summary>
public static class WinUiDiscoveryMotion
{
    /// <summary>The FLIP's duration: 250 ms (the board: "grid column changes FLIP over 250 ms").</summary>
    public const float ReflowMs = 250f;

    /// <summary>A surviving part that moves AND resizes (a zone, a band, a grid): position and size tween, the size revealed (a clip), never scaled.</summary>
    public static readonly LayoutTransition Reflow = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>A surviving cell that only moves (a grid cell whose column changed): the position-only FLIP.</summary>
    public static readonly LayoutTransition Shove = new(TransitionChannels.Position, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut));

    /// <summary>A part the new tier draws as a different tree enters and leaves on its opacity alone (the shared tier cross-fade).</summary>
    public static readonly EnterExit SwapFade = new(Opacity: 0f, Active: true);

    /// <summary>The cross-fade's timing.</summary>
    public static MotionTokenDef SwapFadeMotion => MotionTok.ControlFast;
}

/// <summary>The body spacing of a page under the overlay masthead (the frame draws the head: `WinUiMasthead`).</summary>
public static class WinUiPageHeadRules
{
    /// <summary>The gap between the head's foot and the page body (16).</summary>
    public const float HeadGap = 16f;

    /// <summary>A drill-in page's body padding under the masthead (W-BrowseCategory): the page inset either side, the frame's top inset plus the Drill head
    /// (the trail's 28 slot, 4, the 52 title line) plus 16 on top, <paramref name="bottom"/> under.</summary>
    public static Edges4 DrillBodyPad(in TierFacts facts, float bottom)
    {
        var frame = WinUiTiers.FrameOf(in facts);
        return new Edges4(frame.InsetX, frame.InsetTop + WinUiFrameRules.HeadHeight(PageHeadForm.Drill) + HeadGap, frame.InsetX, bottom);
    }

    /// <summary>A title-only page's body top under the masthead (Browse, W-Browse): the frame's top inset plus the 52 title line plus 8 (its subtitle sits in the page).</summary>
    public static float TitleBodyTop(in TierFacts facts) => WinUiTiers.FrameOf(in facts).InsetTop + WinUiFrameRules.HeadHeight(PageHeadForm.Title);
}

public static partial class WinUiTiers
{
    static partial void RungsDiscovery(ImmutableArray<NamedRung>.Builder rungs)
        => rungs.Add(new NamedRung(WinUiSearchTiers.BesideRung, new TierEdge(WinUiSearchTiers.BesideFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Content));

    static partial void LaddersDiscovery(ImmutableArray<NamedLadder>.Builder ladders) { }
}
