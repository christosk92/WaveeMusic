// ── Themes/WinUi/Structure/Rules/WinUiListRules.cs — the numbers and pure rules of WinUI's lists (themes plan 6.2; P6 W3) ─────────────────
//
// What WinUI's list styles (`Structure\Lists\WinUi*.cs`) read instead of writing a literal (WVTH003 allows literals only in `Rules\**`). Boards: W-RowStates (and
// -Compact), W-MultiSelect (and -Compact), W-Unavailable (and -Compact), W-Album, W-Playlist, W-LikedSongs and their -Small / -Huge twins; W-Tokens for the
// row heights.
//
//   · THE ROWS (W-Tokens "Row 40 single-line · 48 two-line with 32 art"; W-Playlist, W-MultiSelect draw the art at 40 inside the 48). One row skin per
//     `artworkInRows` value: ON, the MODERN skin, the two-line row (title over its artists, a 40 cover) with an album's numbered rows one line without
//     art (40, the density's row less `AlbumRowTrim`); OFF, the CLASSIC skin, the one-line 40 row with the artist in a lane of its own (the option's own
//     words: "off, one 40 line"). The density (`style.rowDensity`, 40 / 48 -> 32 / 40) is the style's and arrives as `StyleMetrics.TrackRows`; a structure
//     reads the rung the style PUBLISHED, never the option cell (WVTH005).
//   · THE STATES (W-RowStates): rest bare (no fill, no zebra, no border); hover the subtle fill 1 (6 %), pressed fill 2, selected = fill 1 plus the 3 x 16
//     accent pill (selected and hovered, fill 3), playing = the accent text on the title, the heart and the bars, keyboard focus the engine's ring, unavailable
//     the disabled ink (the whole row at 45 %) that says "Unavailable" in the time lane, a drop target the 2-px accent line on the top edge. The plate is a
//     full-width r4 backplate.
//   · THE LANES (the core's, `Track.TracksFor`): # · art · title (+ subline) · album · added · plays · heart · time · "..." · chevron; the heart STANDS in its
//     own lane before the time (WinUI keeps `Plays | heart | time | ...` at rest, a liked row's filled heart at rest, an unliked row's outline on hover).
//   · THE TIERS (`WinUiListTiers`): below 560 the table is two-line, 560-859 drops Plays, Date and Album, from 860 every lane; the window's Compact band
//     (< 641) draws the Compact row form (56 two-line, 40 cover, no header, the time, the heart and a 40 "..." on every row).
//
// Public, so `Wavee.Tests` pins the rules (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The numbers and pure rules of WinUI's lists.</summary>
public static class WinUiListRules
{
    // ── the rows by density ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The densities in rung order (the "Row size" menu's rows).</summary>
    public static ImmutableArray<WinUiDensity> Densities { get; } = [WinUiDensity.Compact, WinUiDensity.Standard];

    /// <summary>The layer-qualified id the "Row size" menu requests a density by (the style's `rowDensity` option: NOT Classic's `density`).</summary>
    public const string DensityQualifiedId = WinUiDensityOption.QualifiedId;

    /// <summary>The word for a density in the "Row size" menu: the style's own option words ("Default", "Compact").</summary>
    public static string DensityLabelKey(WinUiDensity density) => density == WinUiDensity.Compact
        ? Strings.Theme.Winui.Style.DensityCompact : Strings.Theme.Winui.Style.DensityStandard;

    /// <summary>The density a published rung names (<c>TrackRowMetrics.Rung</c>: 0 compact, 1 standard).</summary>
    public static WinUiDensity DensityOfRung(int rung) => WinUiDensityOption.OfRung(rung);

    // ── the row ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid's own left and right inset inside the full-width plate: the "#" stands 14 in from the plate's edge and the art at 46 (the boards' 14 / 48).</summary>
    public const float RowPad = 6f;

    /// <summary>The plate's radius (W-Tokens: rows are controls, r4), the keyboard ring's radius is the engine's.</summary>
    public const float RowRadius = 4f;

    /// <summary>The selection pill: 3 x 16, fully round ends, flush with the plate's leading edge (W-RowStates: left 0).</summary>
    public const float PillWidth = 3f, PillHeight = 16f, PillRadius = 1.5f;

    /// <summary>An album's numbered rows (one line, no art lane) stand this far under the density's two-line row: 48 -> 40 (W-Album: 40-tall rows), 40 -> 32 compact.</summary>
    public const float AlbumRowTrim = 8f;

    /// <summary>The # and the transport glyph, the heart, the film glyph and the versions chevron.</summary>
    public const float TransportGlyph = 12f, HeartGlyph = 16f, FilmGlyph = 12f, ChevronGlyph = 12f;

    /// <summary>The chart mark beside a chart row's number: 8 on a 12 line, 700.</summary>
    public const float ChartMarkSize = 8f, ChartMarkLine = 12f;

    /// <summary>The air between a two-line row's title and its subline (W-Playlist-Huge: 2).</summary>
    public const float TitleGap = 2f;

    /// <summary>The gap between the # cell's number and its chart mark, and between the subline's parts.</summary>
    public const float NumberGap = 2f, MetaGap = 4f;

    /// <summary>The explicit "E": a 16 plate with 2-px corners, the 11-px letter (the boards draw 10; the lint floor is 11) (W-Album).</summary>
    public const float ExplicitBox = 16f, ExplicitRadius = 2f, ExplicitType = 11f;

    /// <summary>The gap between the title and its explicit mark (W-Album: 8).</summary>
    public const float ExplicitGap = 8f;

    /// <summary>The "on this PC" mark that leads a row's subline: a 14 ring, 2 stroke, the 8-type arrow inside.</summary>
    public const float OnPcMark = 14f, OnPcRing = 2f, OnPcGlyph = 8f, OnPcAfter = 2f;

    /// <summary>The arrow the ring carries.</summary>
    public const string OnPcArrow = "↓";

    /// <summary>The tempo lane's key swatch: a 6 DIP dot at 0.85.</summary>
    public const float TempoDot = 6f, TempoDotOpacity = 0.85f;

    /// <summary>What leads the plays a Compact album row states in its subline ("Edward Maya · 598.9M plays", W-Album-Compact).</summary>
    public const string InlineLead = " · ";

    /// <summary>A withheld row's short reason in the time lane (W-RowStates: "Unavailable" at 12).</summary>
    public const float ReasonSize = 12f;

    /// <summary>The drop target's accent line on the row's top edge (W-RowStates "2 px accent insertion line").</summary>
    public const float DropLine = 2f;

    /// <summary>A Compact row's "..." target (the brief's 40 x 40 rule, W-RowStates-Compact's 40 trailing lane).</summary>
    public const float CompactTarget = 40f;

    /// <summary>A liked row's heart rests filled in the primary text; the playing row's in the accent (W-RowStates).</summary>
    public static ColorF HeartInk(bool isNow, ColorF accent, ColorF rest) => isNow ? accent : rest;

    /// <summary>A row lane's ink: the accent text on the playing row (the title: W-RowStates "accent text title"), the lane's own rest ink otherwise.</summary>
    public static ColorF RowInk(bool isNow, ColorF accent, ColorF rest) => isNow ? accent : rest;

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A slot's mount entrance (a re-cut list's crossfade replays it): opacity only, the grid cross-fade's beat.</summary>
    public static readonly LayoutTransition MountEntrance = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate), Enter: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The heart's pop on a like edge: an Enter-only overshoot spring from 0.25 scale and 0 opacity.</summary>
    public static readonly LayoutTransition HeartPop = new(TransitionChannels.Opacity, TransitionDynamics.Spring(0.30f, 0.55f),
        Enter: new EnterExit(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true));

    /// <summary>The check lane arriving: the content slides over by its width.</summary>
    public static readonly LayoutTransition CheckShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));

    // ── the chrome (header, command bar, filter card, group head, unavailable bar) ───────────────────────────────────────

    /// <summary>The header's sort caret and the sort glyph; the caret's pop-in seed scale and the title word's rise on a swap.</summary>
    public const float CaretGlyph = 9f, CaretSeedScale = 0.3f, LabelRise = 4f;

    /// <summary>The 32-DIP command height and plate; the glyph and the gap between a glyph, its word and a trailing mark (W-Album: 16 glyph, 8 gap, 10 padding).</summary>
    public const float CommandHeight = 32f, CommandGlyph = 16f, CommandGap = 8f;

    /// <summary>A labelled command's padding (W-Album: 0 10).</summary>
    public static readonly Edges4 CommandPad = new(10f, 0f, 10f, 0f);

    /// <summary>The disclosure chevron after a flyout command.</summary>
    public const float CommandChevron = 12f;

    /// <summary>The command edge at a window tier: the 32, or the Compact 40 target.</summary>
    public static float CommandEdgeFor(WidthTier window) => window == WidthTier.Compact ? CompactTarget : CommandHeight;

    /// <summary>Does the list bar word Find and carry Select at this window tier? W-Album and W-Album-Huge draw "Find" and "Select"; W-Album-Small keeps the
    /// glyph in the header and Select in "...".</summary>
    public static bool WordedBar(WidthTier window) => window >= WidthTier.Standard;

    /// <summary>The command's own radius (W-Tokens: controls r4).</summary>
    public const float CommandRadius = 4f;

    /// <summary>The count badge (a filter count): 18 round, and its ring.</summary>
    public const float BadgeSize = 18f, BadgeRadius = 9f, BadgeRing = 1.5f;

    /// <summary>The labelled Filter command's badge slot.</summary>
    public const float FilterBadgeSlot = 22f;

    /// <summary>The key prefix the core gives the page bar's lead command.</summary>
    public const string PageLeadKeyPrefix = "cmd:page-lead:";

    /// <summary>The date group head: 40 tall, the words on its floor 6 up, 8 between the name and the count (Body Strong 14/20 over the count's 14/20).</summary>
    public const float GroupHeadSize = 14f, GroupHeadLine = 20f, GroupHeadHeight = 40f, GroupHeadPadBottom = 6f, GroupHeadGap = 8f;

    /// <summary>A command entering and leaving the bar.</summary>
    public static readonly LayoutTransition CommandMotion = new(TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 8f, Opacity: 0f, Active: true), Exit: new EnterExit(Dx: 8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

    /// <summary>The bar's browse / selection swap.</summary>
    public static readonly LayoutTransition ModeMotion = new(TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(210f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 10f, Opacity: 0f, Active: true), Exit: new EnterExit(Dx: -8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

    /// <summary>The header shifting over for the check lane.</summary>
    public static readonly LayoutTransition HeaderShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));

    /// <summary>The check lane's width the header shifts over by (the core's lane).</summary>
    public const float CheckLaneShift = 28f;

    /// <summary>The selection bar (W-MultiSelect): the card (white 5 %, r8) at 48, 12 in on the check side, 8 on the close; the Compact selection mode is
    /// the accent tint (W-MultiSelect-Compact: the accent at 10 %).</summary>
    public const float SelectionBarHeight = 48f, SelectionBarRadius = 8f, SelectionTint = 0.10f;

    /// <summary>The filter card: 360 wide (never under 320), at most 620 tall; the scroll region (at most 500) is the fallback only. One 44 row per facet, a 168
    /// combo, the 12 / 8 group padding, the 48 header.</summary>
    public const float CardWidth = 360f, CardMinWidth = 320f, CardMaxHeight = 620f, ScrollMaxHeight = 500f,
        FacetRowHeight = 44f, FacetRowPadX = 8f, FacetGap = 8f, ComboWidth = 168f,
        GroupPadX = 8f, GroupPadTop = 4f, GroupPadBottom = 8f,
        CardHeaderHeight = 48f, CardHeaderPadLeft = 16f, CardHeaderPadRight = 8f, CardHeaderGap = 8f, CardTitleSize = 16f, FacetLabelSize = 14f;

    /// <summary>The unavailable-songs bar (W-Unavailable): the InfoBar's card, 48 tall (56 at Compact: two lines of 40 targets), r4, its accent info glyph, the gap
    /// and the air under it.</summary>
    public const float UnavailableHeight = 48f, UnavailableCompactHeight = 56f, UnavailableGlyph = 16f, UnavailableGap = 12f, UnavailableBelow = 12f;

    /// <summary>The unavailable-songs bar's padding.</summary>
    public static readonly Edges4 UnavailablePadding = new(16f, 0f, 8f, 0f);

    /// <summary>The band the bar takes in the chrome while shown at a window tier: its height and the air under it.</summary>
    public static float UnavailableExtent(WidthTier window)
        => (window == WidthTier.Compact ? UnavailableCompactHeight : UnavailableHeight) + UnavailableBelow;

    /// <summary>The bar says why (the sentence with its reasons) from Standard; Compact and Small say the short one.</summary>
    public static bool UnavailableExplains(WidthTier window) => window >= WidthTier.Standard;

    // ── the episode row ──────────────────────────────────────────────────────────────────────────────────────────────────

    // (The episode row's numbers are `WinUiEpisodeRowLayout`.)

    // ── the policies ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list policy WinUI publishes: the artwork follows the `artworkInRows` option; ON draws the MODERN skin (the two-line row), OFF the CLASSIC skin
    /// (the one-line 40 row with the artist's own lane).</summary>
    public static ListPolicy ListsFor(bool artworkInRows) => new(TrackArtwork: artworkInRows, RowStyle: artworkInRows ? TrackRowStyle.Modern : TrackRowStyle.Classic);

    /// <summary>The table form at a window tier: the Compact band's Compact row (W-RowStates-Compact, W-Album-Compact, W-MultiSelect-Compact), the Small band's
    /// header table with no chevron lane, Standard's every lane the list column admits; the Huge band's cozy rows (W-Playlist-Huge: 72 with a 64 cover).</summary>
    public static DetailTierLayout Compact { get; } = new(
        new DetailTableTier(CompactRows: true, AlbumColumn: false, PlaysColumn: false, DateColumn: false,
                            ModernRow: CompactModernRow, ClassicRow: CompactClassicRow, Art: CompactArt, ListIndex: true,
                            HeartTrailing: true, KeepDuration: true, VersionsLane: false),
        InsightsDocked: false, RailRest: 0f, HeroActionExtra: 0f, PageInsetX: 16f, BandHeight: WinUiDetailRules.BandHeight);

    /// <summary>The Compact row (W-RowStates-Compact: "track row, 56 px, two-line, ... always shown"; W-MultiSelect-Compact: a 40 cover).</summary>
    public const float CompactModernRow = 56f, CompactClassicRow = 48f, CompactArt = 40f;

    /// <summary>Small (W-Album-Small, W-Playlist-Small): # · title (/ artists) · heart · time · "..." and no chevron lane; the list column's table ladder drops
    /// Album, Date and Plays below 860.</summary>
    public static DetailTierLayout Small { get; } = new(
        new DetailTableTier(CompactRows: false, AlbumColumn: true, PlaysColumn: true, DateColumn: true,
                            HeartTrailing: true, VersionsLane: false, OneLineTrim: AlbumRowTrim, DurationWidth: TimeLane),
        InsightsDocked: false, RailRest: 0f, HeroActionExtra: 0f, PageInsetX: 24f, BandHeight: WinUiDetailRules.BandHeight);

    /// <summary>Standard (W-Album, W-Playlist, W-MultiSelect, W-Unavailable): every lane the list column admits, the density's rows; an album's numbered rows one line.</summary>
    public static DetailTierLayout Standard { get; } = DetailTierLayout.Standard with
    {
        Table = DetailTableTier.Standard with { HeartTrailing = true, OneLineTrim = AlbumRowTrim, DurationWidth = TimeLane },
        PageInsetX = 36f, BandHeight = WinUiDetailRules.BandHeight,
    };

    /// <summary>Huge (W-Playlist-Huge): the cozy rows, 72 with a 64 cover, the same lanes; an album's one-line rows 72 less the trim stay the two-line row's height less 8.</summary>
    public static DetailTierLayout Huge { get; } = Standard with
    {
        Table = Standard.Table with { ModernRow = HugeModernRow, ClassicRow = HugeClassicRow, Art = HugeArt, OneLineTrim = HugeAlbumRowTrim, DurationWidth = HugeTimeLane },
        PageInsetX = 48f, AsideDocked = true, AsideInHero = true, FansInBand = true,
    };

    /// <summary>The time lane (W-Album: 72; W-Playlist-Huge: 80): wide enough for a withheld row's "Unavailable" at 12.</summary>
    public const float TimeLane = 72f, HugeTimeLane = 80f;

    /// <summary>The Huge rows: the board's 72-tall rows with a 64 cover (the one-line Classic skin 56); an album's numbered rows 56.</summary>
    public const float HugeModernRow = 72f, HugeClassicRow = 56f, HugeArt = 64f, HugeAlbumRowTrim = 16f;

    /// <summary>The detail tiers WinUI publishes for the table: its form and lane caps per window tier. W6a extends THIS record (a partial method has one body) with
    /// the hero, the rail and the asides.</summary>
    public static DetailTierPolicy DetailTiers { get; } = new(Compact, Small, Standard, Huge,
        RailTitleShort: 28f, RailTitleTall: 40f, DescriptionShort: 3, DescriptionRegular: 6);

    /// <summary>A row's height in a window tier's table record at a density.</summary>
    public static float TableRowFor(WidthTier window, WinUiDensity density, bool classic)
        => DetailTiers.For(window).Table.RowFor(classic, classic ? WinUiDensityOption.RowSingle(density) : WinUiDensityOption.RowDouble(density));
}
