// ── Themes/Classic/Structure/Rules/Tiers.Home.cs — Classic's Home (and the discovery pages' shared bands) per content tier (themes plan 3.6, TC4) ────
//
// ONE record per CONTENT tier (`ctx.Tier().Content`: the region the page lives in, which is what the page gutter follows), plus the Short variant of each
// (`ctx.Tier().Height`, 330-679 tall: board `C-Shell-Short`). A view reads `ClassicHomeTiers.Of(ctx.Tier())` and never compares a width with a number
// (WVTH013). Compact follows `C-Home-Compact` / `C-Error-Compact`, Small `C-Home-Small`, Huge `C-Home-Huge` and `C-Home-Huge4K`; Standard is the tuned
// design (P3b) and its record is exactly the numbers the page held before tiers (`ClassicHomeRules`, `R.Space`).
//
//   tier      daylist                        recently played                    shelves (covers)                  page column
//   Compact   stacked: 140 art over copy     2 cols of 56 tiles, 40 art         148 art strip, the 3rd peeks      fill, top 12, gap 24
//   Small     216 banner, 364 copy, 32 btns  3 cols of 64 tiles                 fit 148-188 (4 at 800)            fill, top 16, gap 32
//   Standard  320+ card, half-width copy     fit 200, up to 4 cols              fit 148-188                       1600, top 24, gap 40
//   Huge      400 hero BESIDE the recents    2 cols x 4 rows beside the hero    10 columns                        2400, top 24, gap 40
//   Short     page top 16; under 480 tall the daylist is a 126 strip (60 % of the 210 page), eyebrow and title only; from 480 it keeps its form (Huge: Small's banner)
//
// THE FACET ROW'S TRAILING BUTTON and the chapter subtitles: Compact ends the row with the 40 x 40 Friend activity button and drops the chapter headers'
// subtitles (`C-Home-Compact`); Huge stands the Following toggle at the row's end on EVERY word, "All" included (`C-Home-Huge`, `-Huge4K`); Small and
// Standard keep the tuned row (Following only on a word with its own sub-chip) and the subtitles.
//
// HUGE 4K. Classic draws no 4K band ("Huge 4K uses the same rules at 3840", `C-Breakpoints`) and the grids stop at 2400 (`C-Breakpoints`: "Auto-fill to
// 2400"), so 3840 reads the Huge record; `C-Home-Huge4K` draws 12-card shelves and a full-bleed page, which the plan text overrules (flagged in the lane report).
//
// COLUMNS are `(minimum column, maximum columns)` bands the engine's own auto-fill resolves at the region's measured width (a floor-fit INSIDE a tier, never
// a threshold the view compares): `ClassicGridBand`. The first frame of a width-driven box is seeded from `ClassicRegion.EstimateWidth` (the same-frame viewport
// less the chrome in front of the page), so it is already in the right tier and nearly the right column count — there is no fixed 1100-wide first frame.
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>A grid's column rule inside one tier: as many columns as fit at <see cref="MinCol"/>, never more than <see cref="MaxCols"/>.
/// A <see cref="MinCol"/> of 0 is a fixed count (<see cref="MaxCols"/>).</summary>
/// <param name="MinCol">The narrowest column; 0 for a fixed count.</param>
/// <param name="MaxCols">The most columns.</param>
public readonly record struct ClassicGridBand(float MinCol, int MaxCols)
{
    /// <summary>The columns at <paramref name="width"/>, 1..<see cref="MaxCols"/>. A non-positive width (not measured yet) is one column.</summary>
    public int Columns(float width, float gap = 0f)
    {
        if (MinCol <= 0f) return Math.Max(1, MaxCols);
        if (width <= 0f) return 1;
        return Math.Clamp((int)MathF.Floor((width + gap) / (MinCol + gap)), 1, Math.Max(1, MaxCols));
    }

    /// <summary>A fixed count of <paramref name="columns"/>.</summary>
    public static ClassicGridBand Fixed(int columns) => new(0f, columns);
}

/// <summary>A paged shelf's fit inside one tier: <c>PagedShelf</c>'s minimum / maximum card and maximum columns, or one fixed card width (a Compact strip whose
/// next card peeks), and whether the shelf shows its own pager.</summary>
/// <param name="MinCardW">The narrowest card.</param>
/// <param name="MaxCardW">The widest card.</param>
/// <param name="MaxColumns">The most columns; 0 is unlimited.</param>
/// <param name="FixedCardW">A fixed card width (a strip); 0 fits.</param>
/// <param name="Gap">The gap between cards.</param>
/// <param name="Pager">The shelf's chevrons and pips are shown.</param>
public readonly record struct ClassicShelfBand(float MinCardW, float MaxCardW, int MaxColumns, float FixedCardW, float Gap, bool Pager);

/// <summary>How the daylist card is built in a tier.</summary>
public enum ClassicDaylistForm : byte
{
    /// <summary>The tuned card: a 320 floor, half-width copy over a horizontal veil (Standard).</summary>
    Card,
    /// <summary>Compact: the art above (a vertical veil), the copy below it with the actions 40 tall.</summary>
    Stacked,
    /// <summary>Small: a 216 banner, the copy a fixed 364 wide over the veil.</summary>
    Banner,
    /// <summary>Huge: a 400 hero, 760 of copy at the display size, the clock a plate in the corner.</summary>
    Hero,
    /// <summary>Short: a banner capped at 60 % of the page, the eyebrow and the title only.</summary>
    Strip,
}

/// <summary>How much of the daylist clock a tier draws.</summary>
public enum ClassicClockForm : byte
{
    /// <summary>The tuned block: ring, two lines, five bars and their labels (Standard).</summary>
    Full,
    /// <summary>Compact: the ring and the countdown with the current daypart's word, over four-high bars.</summary>
    Bars,
    /// <summary>Small: the ring and one line, the countdown and the next daylist's time.</summary>
    Line,
    /// <summary>Huge: the full block on a dark plate, 440 wide.</summary>
    Boxed,
    /// <summary>Short: no clock.</summary>
    None,
}

/// <summary>The daylist title's type in a tier.</summary>
public enum ClassicDaylistTitle : byte
{
    /// <summary>The 40/52 hero rung while the copy is wide enough, else the 28/36 display rung (<c>DaylistForm</c>).</summary>
    Adaptive,
    /// <summary>The 28/36 display rung at 600; <see cref="ClassicDaylistLayout.TitleLines"/> lines.</summary>
    Display28,
    /// <summary>The 68/92 display rung at 600 (Huge).</summary>
    Display68,
}

/// <summary>What the daylist card is in one tier.</summary>
public sealed record ClassicDaylistLayout
{
    /// <summary>The card's form.</summary>
    public required ClassicDaylistForm Form { get; init; }

    /// <summary>The card's height; NaN lets the copy decide it above the 320 floor (<see cref="ClassicDaylistForm.Card"/>) or the stack (Compact).</summary>
    public required float CardH { get; init; }

    /// <summary>The art band's height over the copy (Compact's stack); 0 when the art runs behind the copy.</summary>
    public required float ArtH { get; init; }

    /// <summary>The copy column's widest: 364 (Small), 760 (Huge), 420 (Short); NaN is the tuned half-width rule (Standard) or the full width (Compact).</summary>
    public required float CopyW { get; init; }

    /// <summary>The copy's insets (left, top, right, bottom).</summary>
    public required Edges4 CopyPad { get; init; }

    /// <summary>The title's type.</summary>
    public required ClassicDaylistTitle Title { get; init; }

    /// <summary>The title's most lines (1 clips with an ellipsis).</summary>
    public required int TitleLines { get; init; }

    /// <summary>The buttons' size: 40 tall at Compact, the control's 32 elsewhere.</summary>
    public required ControlSize Buttons { get; init; }

    /// <summary>The save and more buttons' box: 40 at Compact, 32 elsewhere.</summary>
    public required float IconBox { get; init; }

    /// <summary>The clock's form.</summary>
    public required ClassicClockForm Clock { get; init; }

    /// <summary>The tags, the meta line and the actions are shown (false: the eyebrow and the title only, the Short strip).</summary>
    public required bool Details { get; init; }
}

/// <summary>What Classic's Home is in one content tier (and one height tier).</summary>
public sealed record ClassicHomeLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (330-679 tall): the page top is 16 and the daylist a strip.</summary>
    public required bool Short { get; init; }

    /// <summary>The page column's widest; NaN fills the region (Compact, Small).</summary>
    public required float PageMaxW { get; init; }

    /// <summary>The air over the facet row.</summary>
    public required float PageTop { get; init; }

    /// <summary>The step from the facet row to the first zone.</summary>
    public required float HeadGap { get; init; }

    /// <summary>The step between two zones.</summary>
    public required float ZoneGap { get; init; }

    /// <summary>The facet words take the compact rung (20/28) at every width, instead of the title rung while the row fits it.</summary>
    public required bool CompactFacets { get; init; }

    /// <summary>The facet row ends with the 40 x 40 Friend activity button (Compact: <c>C-Home-Compact</c>).</summary>
    public required bool FriendsButton { get; init; }

    /// <summary>The Following toggle stands at the facet row's end on every word, "All" included, driving the first word's sub-chip there (Huge:
    /// <c>C-Home-Huge</c>); false shows it only on a word that has its own sub-chip (the tuned row).</summary>
    public required bool FollowingOnEveryWord { get; init; }

    /// <summary>The chapter headers carry their subtitles beside the title (false at Compact: <c>C-Home-Compact</c> draws the titles alone).</summary>
    public required bool HeaderSubtitles { get; init; }

    /// <summary>The failed-facet InfoBar's height (72 at Compact: <c>C-Error-Compact</c>); 0 keeps the stock bar's own.</summary>
    public required float FailureBarH { get; init; }

    /// <summary>The failed-facet InfoBar's Retry button: 40 tall (Large) at Compact, the control's 32 elsewhere.</summary>
    public required ControlSize FailureRetry { get; init; }

    /// <summary>The daylist card.</summary>
    public required ClassicDaylistLayout Daylist { get; init; }

    /// <summary>The daylist hero and the Recently played grid share one band (Huge): the daylist 1.35 : 1 beside the tiles.</summary>
    public required bool DaylistBeside { get; init; }

    /// <summary>A cover shelf (the square cards).</summary>
    public required ClassicShelfBand Shelf { get; init; }

    /// <summary>The first cover of a shelf may span two cells (E21) while the shelf has at least three columns; false where the cards are a fixed strip.</summary>
    public required bool ShelfLead { get; init; }

    /// <summary>A wide shelf (the 16:9 tiles).</summary>
    public required ClassicShelfBand WideShelf { get; init; }

    /// <summary>The Recently played tiles' columns.</summary>
    public required ClassicGridBand Recents { get; init; }

    /// <summary>The Recently played tile: the art edge and the row height (56 with 40 art at Compact, 64 with 48 elsewhere).</summary>
    public required float RecentsArt { get; init; }

    /// <summary>The Recently played tile's row height.</summary>
    public required float RecentsRowH { get; init; }

    /// <summary>The release rows' columns.</summary>
    public required ClassicGridBand Releases { get; init; }

    /// <summary>The cluster cards' columns.</summary>
    public required ClassicGridBand Clusters { get; init; }

    /// <summary>The browse tiles' columns.</summary>
    public required ClassicGridBand Tiles { get; init; }

    /// <summary>The episode rows' columns (a lead and a list).</summary>
    public required ClassicGridBand Episodes { get; init; }

    /// <summary>The continue-listening rows' columns.</summary>
    public required ClassicGridBand Continue { get; init; }

    /// <summary>The "See all" drill page's narrowest grid cell.</summary>
    public required float SectionMinCell { get; init; }

    /// <summary>The Recently played grid's played tiles at <paramref name="cols"/> columns: two full rows less the history tile, except where the tier fills a
    /// taller band (Huge: four rows of two beside the hero), never more than the eight tiles the zone has.</summary>
    public int RecentsRows(int cols) => Tier == WidthTier.Huge && !Short ? Math.Max(2, 8 / Math.Max(1, cols)) : 2;

    /// <summary>The played cells before the history tile: <c>rows x cols - 1</c>, never more than <paramref name="available"/>.</summary>
    public int RecentsCells(int cols, int available)
    {
        int want = Math.Max(0, RecentsRows(cols) * Math.Max(1, cols) - 1);
        return Math.Min(want, Math.Max(0, available));
    }
}

/// <summary>Classic's Home records and the first-frame estimate every discovery page seeds its width-driven boxes with.</summary>
public static class ClassicHomeTiers
{
    /// <summary>The record for <paramref name="facts"/>: the content tier's, the Short variant when the window is Short. Standard's where no frame resolved the
    /// tiers (a view mounted alone).</summary>
    public static ClassicHomeLayout Of(in TierFacts facts)
    {
        if (facts.Policy is null) return For(WidthTier.Standard, short_: false);
        bool shortWindow = facts.Height == HeightTier.Short;
        // The strip is only for a window too short for the hero's own height (the `page.hero.full` rung: under 480 tall); a window of 480-679 keeps it.
        bool strip = shortWindow && facts.Policy.IndexOfRung(ClassicTiers.HeroFullRung) >= 0 && !facts.Rung(ClassicTiers.HeroFullRung);
        return For(facts.Content, shortWindow, strip);
    }

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's: Classic draws no 4K band). <paramref name="strip"/>: the window is too
    /// short for a hero at its own height, so the daylist is the 126 strip.</summary>
    public static ClassicHomeLayout For(WidthTier content, bool short_, bool strip = false)
    {
        var tier = (WidthTier)Math.Min((int)content, (int)WidthTier.Huge);
        var layout = tier switch
        {
            WidthTier.Compact => Compact(),
            WidthTier.Small => Small(),
            WidthTier.Standard => Standard(),
            _ => Huge(),
        };
        if (!short_) return layout;
        // A Short window (330-679 tall): the page top is 16 and the hero never sits beside the tiles. From 480 tall the daylist keeps its form (Huge's 400 hero
        // steps down to Small's banner: it would not fit the 60 % cap); under it the daylist is a strip.
        if (!strip)
            return layout with
            {
                Short = true, PageTop = ShortPageTop, DaylistBeside = false,
                Daylist = tier == WidthTier.Huge ? Small().Daylist : layout.Daylist,
            };
        return WithStrip(layout);
    }

    // The Short strip: the hero capped at 60 % of the 210 page (126), only its eyebrow and title.
    private static ClassicHomeLayout WithStrip(ClassicHomeLayout l) => l with
    {
        Short = true,
        PageTop = ShortPageTop,
        DaylistBeside = false,
        Daylist = new ClassicDaylistLayout
        {
            Form = ClassicDaylistForm.Strip, CardH = ShortHeroH, ArtH = 0f, CopyW = StripCopyW,
            CopyPad = new Edges4(24f, 16f, 8f, 12f), Title = ClassicDaylistTitle.Display28, TitleLines = 1,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = ClassicClockForm.None, Details = false,
        },
    };

    /// <summary>The Short page's top air (board <c>C-Shell-Short</c>).</summary>
    public const float ShortPageTop = 16f;

    /// <summary>The hero cap in a Short window: 60 % of the 210-DIP page the Short layout guarantees (board: 126).</summary>
    public const float ShortHeroFrac = 0.6f;

    /// <summary>The Short strip's height.</summary>
    public static float ShortHeroH => ClassicTiers.ShortFloorH * ShortHeroFrac;

    private const float StripCopyW = 420f;

    private static ClassicHomeLayout Compact() => new()
    {
        Tier = WidthTier.Compact, Short = false,
        PageMaxW = float.NaN, PageTop = 12f, HeadGap = 16f, ZoneGap = 24f, CompactFacets = true,
        FriendsButton = true, FollowingOnEveryWord = false, HeaderSubtitles = false,
        FailureBarH = 72f, FailureRetry = ControlSize.Large,
        Daylist = new ClassicDaylistLayout
        {
            Form = ClassicDaylistForm.Stacked, CardH = float.NaN, ArtH = 140f, CopyW = float.NaN,
            CopyPad = new Edges4(16f, 0f, 16f, 16f), Title = ClassicDaylistTitle.Display28, TitleLines = 2,
            Buttons = ControlSize.Large, IconBox = 40f, Clock = ClassicClockForm.Bars, Details = true,
        },
        DaylistBeside = false,
        // 148 of cover plus the card's own 8 either side (the shared card insets its cover): a strip whose next card peeks over the edge.
        Shelf = new ClassicShelfBand(148f, 188f, 0, FixedCardW: 164f, Gap: 0f, Pager: false), ShelfLead = false,
        WideShelf = new ClassicShelfBand(264f, 264f, 0, FixedCardW: 264f, Gap: 12f, Pager: false),
        Recents = new ClassicGridBand(100f, 2), RecentsArt = 40f, RecentsRowH = 56f,
        Releases = new ClassicGridBand(280f, 1), Clusters = new ClassicGridBand(280f, 1), Tiles = new ClassicGridBand(220f, 1),
        Episodes = new ClassicGridBand(280f, 1), Continue = new ClassicGridBand(280f, 1),
        SectionMinCell = 124f,
    };

    private static ClassicHomeLayout Small() => new()
    {
        Tier = WidthTier.Small, Short = false,
        PageMaxW = float.NaN, PageTop = 16f, HeadGap = 16f, ZoneGap = 32f, CompactFacets = false,
        FriendsButton = false, FollowingOnEveryWord = false, HeaderSubtitles = true,
        FailureBarH = 0f, FailureRetry = ControlSize.Medium,
        Daylist = new ClassicDaylistLayout
        {
            Form = ClassicDaylistForm.Banner, CardH = 216f, ArtH = 0f, CopyW = 364f,
            CopyPad = new Edges4(24f, 16f, 8f, 12f), Title = ClassicDaylistTitle.Display28, TitleLines = 1,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = ClassicClockForm.Line, Details = true,
        },
        DaylistBeside = false,
        Shelf = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true), ShelfLead = true,
        WideShelf = new ClassicShelfBand(R.Space.WideTileMin, R.Space.WideTileMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
        Recents = new ClassicGridBand(ClassicHomeRules.RecentsMinCol, 3), RecentsArt = Design.Size.Thumb48, RecentsRowH = ClassicHomeRules.RowH,
        Releases = new ClassicGridBand(ClassicHomeRules.ListMinCol, ClassicHomeRules.ListMaxCols),
        Clusters = new ClassicGridBand(ClassicHomeRules.ClusterMinCol, ClassicHomeRules.ClusterMaxCols),
        Tiles = new ClassicGridBand(ClassicHomeRules.TilesMinCol, ClassicHomeRules.TilesMaxCols),
        Episodes = new ClassicGridBand(ClassicHomeRules.ListMinCol, ClassicHomeRules.ListMaxCols),
        Continue = new ClassicGridBand(ClassicHomeRules.ListMinCol, ClassicHomeRules.ContinueMaxCols),
        SectionMinCell = 156f,
    };

    // Standard is the tuned design: every number is the one the page held before tiers.
    private static ClassicHomeLayout Standard() => new()
    {
        Tier = WidthTier.Standard, Short = false,
        PageMaxW = R.Space.PageMaxW, PageTop = Spacing.XXL, HeadGap = Spacing.XL, ZoneGap = R.Space.SectionGapWide, CompactFacets = false,
        FriendsButton = false, FollowingOnEveryWord = false, HeaderSubtitles = true,
        FailureBarH = 0f, FailureRetry = ControlSize.Medium,
        Daylist = new ClassicDaylistLayout
        {
            Form = ClassicDaylistForm.Card, CardH = float.NaN, ArtH = 0f, CopyW = float.NaN,
            CopyPad = ClassicHomeRules.DaylistCopyPadding, Title = ClassicDaylistTitle.Adaptive, TitleLines = 2,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = ClassicClockForm.Full, Details = true,
        },
        DaylistBeside = false,
        Shelf = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true), ShelfLead = true,
        WideShelf = new ClassicShelfBand(R.Space.WideTileMin, R.Space.WideTileMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
        Recents = new ClassicGridBand(ClassicHomeRules.RecentsMinCol, ClassicHomeRules.RecentsMaxCols),
        RecentsArt = Design.Size.Thumb48, RecentsRowH = ClassicHomeRules.RowH,
        Releases = new ClassicGridBand(ClassicHomeRules.ListMinCol, ClassicHomeRules.ListMaxCols),
        Clusters = new ClassicGridBand(ClassicHomeRules.ClusterMinCol, ClassicHomeRules.ClusterMaxCols),
        Tiles = new ClassicGridBand(ClassicHomeRules.TilesMinCol, ClassicHomeRules.TilesMaxCols),
        Episodes = new ClassicGridBand(ClassicHomeRules.ListMinCol, ClassicHomeRules.ListMaxCols),
        Continue = new ClassicGridBand(ClassicHomeRules.ListMinCol, ClassicHomeRules.ContinueMaxCols),
        SectionMinCell = ClassicHomeRules.SectionCardMinWidth,
    };

    // Huge (C-Home-Huge): the grids widen to 2400 and the counts rise (10 covers, 3 release columns); the daylist is a 400 hero beside the tiles.
    private static ClassicHomeLayout Huge() => new()
    {
        Tier = WidthTier.Huge, Short = false,
        PageMaxW = HugePageMaxW, PageTop = Spacing.XXL, HeadGap = 28f, ZoneGap = R.Space.SectionGapWide, CompactFacets = false,
        FriendsButton = false, FollowingOnEveryWord = true, HeaderSubtitles = true,
        FailureBarH = 0f, FailureRetry = ControlSize.Medium,
        Daylist = new ClassicDaylistLayout
        {
            Form = ClassicDaylistForm.Hero, CardH = 400f, ArtH = 0f, CopyW = 760f,
            CopyPad = new Edges4(32f, 32f, 12f, 28f), Title = ClassicDaylistTitle.Display68, TitleLines = 3,
            Buttons = ControlSize.Medium, IconBox = 32f, Clock = ClassicClockForm.Boxed, Details = true,
        },
        DaylistBeside = true,
        Shelf = new ClassicShelfBand(R.Space.ShelfCardMin, 9999f, 10, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true), ShelfLead = true,
        WideShelf = new ClassicShelfBand(R.Space.WideTileMin, 9999f, 5, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
        // Two columns at every Huge width (C-Home-Huge: repeat(2, 1fr), four rows of 64 inside the 400 hero's band). A 400 floor fitted one column
        // at the tier's narrow end (1920: a 640 band), and its eight stacked rows stood ~270 below the hero.
        Recents = ClassicGridBand.Fixed(2), RecentsArt = Design.Size.Thumb48, RecentsRowH = ClassicHomeRules.RowH,
        Releases = new ClassicGridBand(ClassicHomeRules.ListMinCol, 3),
        Clusters = new ClassicGridBand(ClassicHomeRules.ClusterMinCol, 4),
        Tiles = new ClassicGridBand(ClassicHomeRules.TilesMinCol, 6),
        Episodes = new ClassicGridBand(ClassicHomeRules.ListMinCol, 3),
        Continue = new ClassicGridBand(ClassicHomeRules.ListMinCol, 4),
        SectionMinCell = 216f,
    };

    /// <summary>The Huge page column's widest: the grids stop at 2400 (<c>C-Breakpoints</c>).</summary>
    public const float HugePageMaxW = 2400f;

    /// <summary>The daylist hero's share against the Recently played tiles beside it (<c>C-Home-Huge</c>: 1.35 : 1).</summary>
    public const float HeroShare = 1.35f;

    /// <summary>The gap between the hero and the tiles beside it.</summary>
    public const float HeroGap = 24f;

    /// <summary>Where the facet words yield to the Friend activity button (Compact), their clipped box's trailing fade (<c>C-Home-Compact</c>: the tablist's 24 mask).</summary>
    public const float WordsFadeBand = 24f;
}

/// <summary>The first-frame width of a discovery page's region: what a width-driven box is built at before it has measured itself (plan 3.3 rule 1).</summary>
public static class ClassicRegion
{
    /// <summary>The nav rail Small keeps in front of the page, and the pane Standard and Huge open at: only the chrome a first frame cannot read yet (the
    /// pane's live width is core's).</summary>
    private const float RailW = 48f, PaneW = 320f;

    /// <summary>The page region's width on its first frame: the same-frame <paramref name="viewportWidth"/> less the chrome in front of the page, less both
    /// gutters. Exact when the published facts are current (the facts carry the content width the shell computed for this very viewport), a nearest estimate
    /// otherwise (a window resized inside its tier since the last crossing): always inside the right tier, so no first frame is built at a fixed 1100.</summary>
    public static float EstimateWidth(in TierFacts facts, float viewportWidth)
    {
        float gutter = ClassicTiers.PageOf(facts).Gutter;
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
        return MathF.Max(0f, content - 2f * gutter);
    }

    /// <summary>The width of one band of Home on its first frame: the page region held to the tier's cap, and at a tier whose daylist sits beside the Recently played
    /// tiles (Huge) the hero's or the tiles' share of it.</summary>
    public static float EstimateHomeBand(in TierFacts facts, float viewportWidth, bool hero)
    {
        var layout = ClassicHomeTiers.Of(facts);
        float page = EstimateWidth(facts, viewportWidth);
        if (!float.IsNaN(layout.PageMaxW)) page = MathF.Min(page, MathF.Max(0f, layout.PageMaxW - 2f * ClassicTiers.PageOf(facts).Gutter));
        if (!layout.DaylistBeside) return page;
        float share = hero ? ClassicHomeTiers.HeroShare : 1f;
        return MathF.Max(0f, (page - ClassicHomeTiers.HeroGap) * share / (ClassicHomeTiers.HeroShare + 1f));
    }
}
