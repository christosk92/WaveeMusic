// ── Themes/Zune/Structure/Rules/ZuneHomeRules.cs — the numbers and the plan of Zune's Home (themes plan 6.3; P5 Z5) ──────────────────
//
// Every number Zune's Home views draw with, read off the boards (Z-Home, Z-Home-Podcasts, Z-Light-Home; the Compact / Small / Huge boards for
// the tier records, ZT4's), and the one pure rule that turns a facet's zones into the page's rows (`ZuneHomePlan`). WVTH003 allows
// literals only here, in `Rules\**`; WVTH013 keeps every width decision in the tier record (`ZuneHomeTiers`, by CONTENT tier).
//
//   THE PAGE (Z-Home, Standard): the frame's gutter (48), 8 under the band, the zones 28 apart (22 at Compact), each a heading row (26/32
//   light, its subtitle 13 tertiary 14 after it on the baseline, "see all" 13 at the end) 10 over its body. Pages cap at 2400 (decision 2).
//   QUICKPLAY (All only): pins (132 art) · history (100 art) · new (100 art), the groups 32 apart and wrapping, tiles 12 apart, each a
//   22/28 light head (the QuickplayHead role) and its link word. THE DAYLIST: a 176 strip, the art 320 wide, the copy 32 in (eyebrow 13,
//   title 40/48 light, meta 13, the 36 rings 22 apart under it), the clock column 260 (the count line, five 40-wide bars 4 apart with the
//   current one 20 tall in the accent, the dayparts line). SHELVES: 154 cards 16 apart; the wide tiles a 4-up 16:9 grid. Side by side
//   (1 : 1, 48 apart): the release list (64 rows, 56 art, the "when" 80 wide) beside the clusters (two columns 24 apart, a 28 title over
//   48 rows of 40 art); the new and continue episodes (72 rows, 56 art). The Browse tiles (4-up flat 88 plates) 2 : 1 beside the charts
//   (40 rows, the 22 light rank 32 wide), then "customize home".

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.HomeUi;
using Wavee.Theming;

using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The numbers and node keys of Zune's Home.</summary>
public static class ZuneHomeRules
{
    // ── the node keys the views carry (the tests find them) ──────────────────────────────────────────────────────────────

    /// <summary>The page's column, the quickplay strip, a quickplay group (`zune-home-quick:pins|history|new`), a pin tile, the daylist strip,
    /// a zone's chapter (`zune-home-zone:<zone key>`), a side-by-side pair, the charts list and the customize word.</summary>
    public const string ColumnKey = "zune-home-column", QuickplayKey = "zune-home-quickplay", QuickGroupPrefix = "zune-home-quick:",
        PinTilePrefix = "zune-home-pin:", DaylistKey = "zune-home-daylist", ZonePrefix = "zune-home-zone:", PairPrefix = "zune-home-pair:",
        ChartsKey = "zune-home-charts", CustomizeKey = "zune-home-customize", FailureKey = "zune-home-failure", BusyKey = "zune-home-busy";

    /// <summary>The quickplay groups' ids.</summary>
    public const string PinsGroup = "pins", HistoryGroup = "history", FreshGroup = "new";

    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's top under the band and its foot.</summary>
    public const float PageTop = 8f, PageBottom = 32f;

    /// <summary>A heading row: the gap to its subtitle, and the gap from the row to its body.</summary>
    public const float HeadInlineGap = 14f, HeadGap = 10f;

    /// <summary>The air between a heading's title group and its tools (A6): the "see all" word never glues to the title or its subtitle.</summary>
    public const float HeadToolsGap = 14f;

    /// <summary>A shelf's heading row takes the header's room before the shelf's own spacer does (the engine's header row is title, spacer, pager:
    /// two grow factors would split the room), so "see all" stands at the row's right edge as the board draws it.</summary>
    public const float HeadFillGrow = 1000f;

    /// <summary>The busy bar's height (a switch in flight), over the top of the content.</summary>
    public const float BusyH = 2f;

    /// <summary>The gap between a tile's art and its caption lines.</summary>
    public const float TileCaptionGap = 6f;

    // ── quickplay ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the groups, between a group's tiles, between its head and its tiles.</summary>
    public const float QuickGap = 32f, QuickTileGap = 12f, QuickHeadGap = 10f;

    /// <summary>The play disc that shows on a pin tile while it is hovered.</summary>
    public const float PinPlayDisc = 40f, PinPlayGlyph = 14f, PinPlayInset = 8f;

    // ── the daylist ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The copy's insets, its line gap, the actions' gap and their step down from the meta.</summary>
    public const float DaylistCopyPad = 32f, DaylistCopyGap = 6f, DaylistActionsGap = 22f, DaylistActionsTop = 8f, DaylistActionLabelGap = 10f;

    /// <summary>The action rings: the 36 disc, its 2 ring, the glyph.</summary>
    public const float RingSize = 36f, RingStroke = 2f, RingGlyph = 13f;

    /// <summary>The daylist's save heart button (the target inside the ring; the button freezes its box at mount, so one size at every tier) and the
    /// shelf pager's chevron: the 32 circle in a 40 hit box (the box overhangs the circle by 4 each side with a negative margin, so the header keeps
    /// its layout).</summary>
    public const float SaveHitBox = 40f, ChevronCircle = 32f, ChevronTarget = 40f;

    /// <summary>The clock: its right inset, its line gap, the bars (40 wide, 6 tall, the current one 20, 4 apart).</summary>
    public const float ClockPadRight = 32f, ClockGap = 8f, BarW = 40f, BarH = 6f, BarLitH = 20f, BarGap = 4f;

    /// <summary>The idle bars' ink: the primary text at 14 %.</summary>
    public const float BarIdleAlpha = 0.14f;

    /// <summary>The gap between two daypart words of the clock's last line (a space at 12; the line wraps between words, as the board's does).</summary>
    public const float DaypartWordGap = 4f;

    /// <summary>The daylist art's focus (a little above centre) and its decode size.</summary>
    public const float DaylistArtFocusY = 0.4f;

    /// <summary>The decode size of the daylist art.</summary>
    public const int DaylistArtDecodePx = 640;

    // ── the zones ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two zones side by side: the gap between them.</summary>
    public const float PairGap = 48f;

    /// <summary>A shelf: its card and the gap between cards.</summary>
    public const float ShelfCardW = 154f, ShelfGap = 16f;

    /// <summary>The wide tiles' gap, their narrowest tile and the cap that lets the tier's columns share the width.</summary>
    public const float WideGap = 16f, WideMinW = 200f, WideMaxW = 9999f;

    /// <summary>The release list: the row, its art, the "when" column.</summary>
    public const float ReleaseRowH = 64f, ReleaseArt = 56f, ReleaseWhenW = 80f, RowGap = 12f;

    /// <summary>At most this many release rows.</summary>
    public const int ReleaseRowsMax = 6;

    /// <summary>The clusters: the gap between their columns, a cluster's title row, its rows and their art, the narrowest column.</summary>
    public const float ClusterGap = 24f, ClusterTitleH = 28f, ClusterRowH = 48f, ClusterArt = 40f, ClusterRowGap = 4f, ClusterMinCol = 200f;

    /// <summary>At most this many clusters, and rows per cluster.</summary>
    public const int ClusterMax = 4, ClusterRows = 3;

    /// <summary>The episode rows: the floor, the art, the gap between art and copy.</summary>
    public const float EpisodeRowH = 72f, EpisodeArt = 56f, EpisodeGap = 14f;

    /// <summary>The Browse tiles: their plate, its insets, the gap between plates, the narrowest plate.</summary>
    public const float TileH = 88f, TilePadX = 12f, TilePadY = 10f, TileGap = 12f, TileMinCol = 120f;

    /// <summary>A plate with art keeps its label readable on any art in either theme: the raised tone under the art, and a scrim over its lower
    /// <see cref="PlateScrimShare"/> that goes from nothing to <see cref="PlateScrimAlpha"/> (45 %) black.</summary>
    public const float PlateScrimShare = 0.5f;

    /// <inheritdoc cref="PlateScrimShare"/>
    public const byte PlateScrimAlpha = 115;

    /// <summary>The plate's bottom scrim: black from 0 at the top of its band to 45 % at the foot.</summary>
    public static readonly GradientSpec PlateScrim = FluentGpu.Dsl.Ui.GradientDown(
        new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 0)),
        new GradientStop(1f, ColorF.FromRgba(0, 0, 0, PlateScrimAlpha)));

    /// <summary>The Browse tiles beside the charts, 2 : 1.</summary>
    public const float TilesShare = 2f, ChartsShare = 1f;

    /// <summary>The charts: a row and its rank's width; the customize word's step down.</summary>
    public const float ChartRowH = 40f, ChartRankW = 32f, ChartGap = 12f, CustomizeTop = 16f;

    /// <summary>The section drill page's narrowest cell.</summary>
    public const float SectionMinCell = 154f, SectionGap = 16f;

    /// <summary>The window-height rung the daylist strip needs (Z-Shell-Short: "330 tall: daylist to one row"): crossed from this height, held 24 on
    /// shrinking, so the Small board (600 tall) keeps its 112 row and a 330 window folds it to one 56 row. A lane assumption between the two boards.</summary>
    public const string DaylistRung = "home.daylist.full";

    /// <inheritdoc cref="DaylistRung"/>
    public const float DaylistFullFrom = 480f;

    /// <summary>A discovery page's CHAPTER (a zone, a band, a section, the daylist strip) whose height changes on a crossing or in place: the layout
    /// lands once at the final height, the presented extent tweens from the old one and everything below rides the difference (the engine's flow
    /// reveal, vertical only; `Design.Reveal.Resize`'s cut). A tier crossing runs frame reflows (the band's inset, the title bar's pill) beside the
    /// page, and the engine's reflow-shove rule snaps a POSITION slide that commits beside a live reflow (ZT1's lesson): a chapter that only
    /// slid would pop under the reveal that is still easing above it. A size channel has no such snap. Shared by Home, Browse and Search.</summary>
    public static readonly LayoutTransition ChapterResize = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>The daylist strip's own height across a crossing (176 / 168 / 112 / 56): a reflow on the height axis. A FlowReveal would snap here, since the
    /// engine snaps flow reveals on the frame an interactive resize ends, and a height-rung crossing is exactly that frame.</summary>
    public static readonly LayoutTransition DaylistResize = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);
}

/// <summary>How the daylist strip is arranged in a tier (one tree in every form: only its direction and sizes are live props).</summary>
public enum ZuneDaylistForm : byte
{
    /// <summary>Art | copy | clock in one tall strip (Standard and up: 176, 220 at Huge).</summary>
    Strip,

    /// <summary>Art | copy in a 112 strip, the clock gone (Compact and Small: Z-Frame-Compact, Z-Frame-Small).</summary>
    Row,

    /// <summary>Art | copy | play in ONE 56 row (a window too short for the strip: Z-Shell-Short).</summary>
    OneRow,
}

/// <summary>Which role the daylist's title is set in.</summary>
public enum ZuneDaylistTitle : byte
{
    /// <summary>The quickplay head, 22/28 (the one-row form: one line).</summary>
    Group,

    /// <summary>The heading, 26/32 (Small).</summary>
    Heading,

    /// <summary>The daylist title, 40/48 (Standard, and Compact: the title keeps its step, wrapping over up to three lines).</summary>
    Title,

    /// <summary>The hero, 64/72 (Huge).</summary>
    Hero,
}

/// <summary>What Zune's Home lays out in one CONTENT tier (and, under a window too short for the strip, in the one-row form of it).
/// Every property defaults to Standard's (Z-Home); a tier's record states its differences.</summary>
public sealed record ZuneHomeLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's, so the page title and the band share an x).</summary>
    public required float Gutter { get; init; }

    /// <summary>The gap between zones.</summary>
    public float ZoneGap { get; init; } = 28f;

    // ── quickplay ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The quickplay groups stand in one column (Compact) instead of a row that wraps; the gap between them.</summary>
    public bool QuickStacked { get; init; }

    /// <summary>The groups wrap onto a second row where the page is narrower than the row of them (every tier but Huge). Huge keeps ONE row, as
    /// Z-Frame-Huge draws it (the pins, then the history running on past the page's right edge, clipped there): the owner's window never stacks the
    /// groups into full rows of big tiles.</summary>
    public bool QuickWrap { get; init; } = true;

    /// <inheritdoc cref="QuickStacked"/>
    public float QuickGroupGap { get; init; } = ZuneHomeRules.QuickGap;

    /// <summary>The gap between a group's tiles.</summary>
    public float QuickTileGap { get; init; } = ZuneHomeRules.QuickTileGap;

    /// <summary>The pins quickplay shows, and their art.</summary>
    public int QuickPins { get; init; } = 4;

    /// <inheritdoc cref="QuickPins"/>
    public float PinTileW { get; init; } = 132f;

    /// <summary>The history's plays and their art (a row's, where <see cref="HistoryRows"/>).</summary>
    public int QuickTiles { get; init; } = 3;

    /// <inheritdoc cref="QuickTiles"/>
    public float QuickTileW { get; init; } = 100f;

    /// <summary>The new group's releases (0 = no group: the zone below says it); their art is <see cref="QuickTileW"/>.</summary>
    public int QuickFresh { get; init; } = 3;

    /// <summary>A history or new tile carries the second line under its name.</summary>
    public bool QuickSubs { get; init; } = true;

    /// <summary>The history is a list of rows (Compact), not tiles.</summary>
    public bool HistoryRows { get; init; }

    // ── the daylist ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The daylist's form, its height, its art width and its clock column (0 = no clock).</summary>
    public ZuneDaylistForm DaylistForm { get; init; } = ZuneDaylistForm.Strip;

    /// <inheritdoc cref="DaylistForm"/>
    public float DaylistH { get; init; } = 176f;

    /// <inheritdoc cref="DaylistForm"/>
    public float DaylistArtW { get; init; } = 320f;

    /// <inheritdoc cref="DaylistForm"/>
    public float ClockW { get; init; } = 260f;

    /// <summary>The title's role and its lines, the copy's side insets. The title keeps its role's size at every tier (Zune is a large typeface): a long
    /// name WRAPS over up to <see cref="DaylistTitleLines"/> lines and the strip grows to hug it (<see cref="DaylistGrows"/>), never an ellipsis on one line.
    /// Only the one-row form (a window too short for the strip) keeps one line.</summary>
    public ZuneDaylistTitle DaylistTitle { get; init; } = ZuneDaylistTitle.Title;

    /// <inheritdoc cref="DaylistTitle"/>
    public int DaylistTitleLines { get; init; } = 2;

    /// <inheritdoc cref="DaylistTitle"/>
    public float DaylistPad { get; init; } = ZuneHomeRules.DaylistCopyPad;

    /// <summary>The eyebrow above the title and the meta line under it show.</summary>
    public bool DaylistMeta { get; init; } = true;

    /// <summary>How many of the four rings (play, shuffle, save, more) the daylist draws, whether they carry their words, their disc and the
    /// gap between them.</summary>
    public int DaylistRings { get; init; } = 4;

    /// <inheritdoc cref="DaylistRings"/>
    public bool RingLabels { get; init; } = true;

    /// <inheritdoc cref="DaylistRings"/>
    public float RingSize { get; init; } = ZuneHomeRules.RingSize;

    /// <summary>The ring's hit box where it is larger than the disc (Compact: a 36 disc in a 40 target, the brief's Compact rule); 0 = the disc itself.</summary>
    public float RingTarget { get; init; }

    /// <summary>The strip's height is a floor (<see cref="DaylistH"/>) and grows with its copy (a wrapped title or wrapped rings never clip); its height
    /// glides on the size channel (<see cref="ZuneHomeRules.ChapterResize"/>). False only in the one-row form.</summary>
    public bool DaylistGrows { get; init; } = true;

    /// <inheritdoc cref="DaylistRings"/>
    public float RingsGap { get; init; } = ZuneHomeRules.DaylistActionsGap;

    /// <summary>The play ring stands at the strip's end, outside the copy (Compact and the one-row form).</summary>
    public bool PlayAtEnd { get; init; }

    /// <summary>The clock's bars.</summary>
    public float ClockBarW { get; init; } = ZuneHomeRules.BarW;

    // ── the zones ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two zones that pair (releases | clusters, new | continue episodes) stand side by side, and the gap between them.</summary>
    public bool PairBeside { get; init; } = true;

    /// <inheritdoc cref="PairBeside"/>
    public float PairGap { get; init; } = ZuneHomeRules.PairGap;

    /// <summary>A cover shelf: 0 = the fixed 154 card (Standard), else it fits up to this many cards across the page, none narrower than
    /// <see cref="ShelfMinW"/>.</summary>
    public int ShelfCols { get; init; }

    /// <inheritdoc cref="ShelfCols"/>
    public float ShelfMinW { get; init; } = ZuneHomeRules.ShelfCardW;

    /// <summary>The cover shelf's fixed card width where <see cref="ShelfCols"/> is 0: 154 (Standard), 220 at 4K (Z-Home-Huge4K "min 220").</summary>
    public float ShelfCardW { get; init; } = ZuneHomeRules.ShelfCardW;

    /// <summary>The wide tiles' columns and the narrowest tile, the clusters' columns, the Browse tiles' columns, the Browse tiles' height, and their name
    /// in the big voice.</summary>
    public int WideCols { get; init; } = 4;

    /// <inheritdoc cref="WideCols"/>
    public float WideMinW { get; init; } = ZuneHomeRules.WideMinW;

    /// <inheritdoc cref="WideCols"/>
    public int ClusterCols { get; init; } = 2;

    /// <inheritdoc cref="WideCols"/>
    public int TileCols { get; init; } = 4;

    /// <inheritdoc cref="WideCols"/>
    public float TileH { get; init; } = ZuneHomeRules.TileH;

    /// <inheritdoc cref="WideCols"/>
    public bool TileBig { get; init; }

    /// <summary>The release rows: the floor, the art and the "when" column (0 = none; Compact has the row's own "…" instead).</summary>
    public float ReleaseRowH { get; init; } = ZuneHomeRules.ReleaseRowH;

    /// <inheritdoc cref="ReleaseRowH"/>
    public float ReleaseArt { get; init; } = ZuneHomeRules.ReleaseArt;

    /// <inheritdoc cref="ReleaseRowH"/>
    public float ReleaseWhenW { get; init; } = ZuneHomeRules.ReleaseWhenW;

    /// <summary>The cluster rows: the title row, the row floor and the art.</summary>
    public float ClusterTitleH { get; init; } = ZuneHomeRules.ClusterTitleH;

    /// <inheritdoc cref="ClusterTitleH"/>
    public float ClusterRowH { get; init; } = ZuneHomeRules.ClusterRowH;

    /// <inheritdoc cref="ClusterTitleH"/>
    public float ClusterArt { get; init; } = ZuneHomeRules.ClusterArt;

    /// <summary>The charts stand beside the Browse tiles (2 : 1) instead of under them; a chart row and its rank's width.</summary>
    public bool ChartsBeside { get; init; } = true;

    /// <inheritdoc cref="ChartsBeside"/>
    public float ChartRowH { get; init; } = ZuneHomeRules.ChartRowH;

    /// <inheritdoc cref="ChartsBeside"/>
    public float ChartRankW { get; init; } = ZuneHomeRules.ChartRankW;

    /// <summary>A heading row carries its subtitle.</summary>
    public bool Subtitles { get; init; } = true;

    /// <summary>The minimum hit target of a link word and a cluster's title (40 at Compact, 0 = the word's own size elsewhere).</summary>
    public float LinkTarget { get; init; }

    /// <summary>The daylist is not the wide strip (a 112 row or the one-row form): kept for the readers that only ask "is it stacked down".</summary>
    public bool DaylistStacked => DaylistForm != ZuneDaylistForm.Strip;
}

/// <summary>Zune's Home tier records (content tier; ZT4 tuned Compact, Small and Huge against Z-Frame-* and Z-Home-*; the one-row daylist is the
/// height rung's, `Tiers.Home.cs`).</summary>
public static class ZuneHomeTiers
{
    private static ZuneHomeLayout Std(WidthTier tier) => new() { Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter };

    // Huge's numbers (Z-Frame-Huge, Z-Home-Huge); Huge 4K is the same (the page caps at 2400, decision 2; plan "Home at 4K is built from the Huge rules").
    private static ZuneHomeLayout Huge(WidthTier tier) => Std(tier) with
    {
        ZoneGap = 36f, QuickWrap = false, QuickGroupGap = 48f, QuickTileGap = 16f, QuickPins = 6, PinTileW = 180f, QuickTiles = 4, QuickTileW = 140f, QuickFresh = 3,
        DaylistH = 220f, DaylistArtW = 440f, ClockW = 320f, DaylistTitle = ZuneDaylistTitle.Hero, DaylistPad = 40f, DaylistRings = 3, ClockBarW = 52f,
        ShelfCols = 8, ShelfMinW = 140f, WideCols = 6, ClusterCols = 3, TileCols = 8, TileH = 120f, TileBig = true,
        ReleaseWhenW = 96f, ClusterTitleH = 32f, ClusterRowH = 64f, ChartRowH = 64f, ChartRankW = 36f,
    };

    private static readonly ZuneHomeLayout[] s_records =
    [
        // Compact (Z-Frame-Compact, Z-Home-Compact): 22 between zones; the groups in a column; four 80 pins, the history as three 56 rows (44 art),
        // no new group; the daylist a 168 floor (112 art, the title at the daylist step 40/48 over up to three lines, then four unlabelled 36 rings, each in
        // a 40 target 12 apart; the strip grows with the title and where the rings wrap). Z-Frame-Compact draws a 112 row with a 22/28 title and one play ring
        // at the end, but the owner's large-type rule keeps the 40/48 title, and that step does not fit beside a ring at the end without cutting the name,
        // so the rings stay under it (Z-Home-Compact draws no daylist, so the two boards do not disagree); one column of everything,
        // two across for the shelf, the wide tiles and the Browse tiles; the release rows have no "when" (the row's own "…" instead).
        Std(WidthTier.Compact) with
        {
            ZoneGap = 22f, QuickStacked = true, QuickGroupGap = 22f, QuickPins = 4, PinTileW = 80f, QuickTiles = 3, QuickFresh = 0,
            HistoryRows = true,
            DaylistForm = ZuneDaylistForm.Row, DaylistH = 168f, DaylistArtW = 112f, ClockW = 0f, DaylistTitle = ZuneDaylistTitle.Title, DaylistTitleLines = 3,
            DaylistPad = 16f, DaylistMeta = false, DaylistRings = 4, RingLabels = false, RingSize = 36f, RingTarget = 40f, RingsGap = 12f, PlayAtEnd = false,
            PairBeside = false, ShelfCols = 2, ShelfMinW = 120f, WideCols = 2, WideMinW = 120f, ClusterCols = 1, TileCols = 2,
            ReleaseRowH = 56f, ReleaseArt = 44f, ReleaseWhenW = 0f, ClusterTitleH = 40f, ClusterRowH = 56f, ClusterArt = 44f, ChartsBeside = false,
            Subtitles = false, LinkTarget = 40f,
        },
        // Small (Z-Frame-Small, Z-Home-Small): 18 between zones; pins and history side by side, three 88 tiles each (the name alone), no new group;
        // the daylist a 112 row (140 art, 26/32, play and shuffle with their words); the shelf five across, the wide tiles four; the release list
        // beside "because you like" (32 apart; 52 rows, 40 art, the "when" 72), the Browse tiles four across.
        Std(WidthTier.Small) with
        {
            ZoneGap = 18f, QuickGroupGap = 32f, QuickPins = 3, PinTileW = 88f, QuickTiles = 3, QuickTileW = 88f, QuickFresh = 0, QuickSubs = false,
            DaylistForm = ZuneDaylistForm.Row, DaylistH = 112f, DaylistArtW = 140f, ClockW = 0f, DaylistTitle = ZuneDaylistTitle.Heading,
            DaylistPad = 24f, DaylistMeta = false, DaylistRings = 2, RingsGap = 18f,
            PairBeside = true, PairGap = 32f, ShelfCols = 5, ShelfMinW = 100f, WideCols = 4, WideMinW = 120f, ClusterCols = 1, TileCols = 4,
            ReleaseRowH = 52f, ReleaseArt = 40f, ReleaseWhenW = 72f, ClusterRowH = 52f, ClusterArt = 40f, ChartsBeside = false,
        },
        // Standard (Z-Home): the tuned page (the defaults).
        Std(WidthTier.Standard),
        // Huge (Z-Frame-Huge, Z-Home-Huge): 36 between zones; pins six 180, history four 140 and new three 140 with their lines, the groups 48 apart;
        // the daylist a 220 strip (440 art, the title 64/72, play shuffle and save, a 320 clock of 52 bars); the shelf eight across, the wide tiles
        // six, three cluster columns, the Browse tiles eight across at 120 with the big name; 64 rows (the "when" 96).
        Huge(WidthTier.Huge),
        // Huge 4K (Z-Home-Huge4K "min 220"): the same page, its cover shelf a row of fixed 220 cards (it was eight flexing to ~290 at the 2400 cap).
        Huge(WidthTier.Huge4K) with { ShelfCols = 0, ShelfCardW = 220f },
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneHomeLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    // The one-row daylist (Z-Shell-Short): the same page, its daylist one 56 row, the gaps tight, no heading subtitles.
    private static readonly ZuneHomeLayout[] s_oneRow = Array.ConvertAll(s_records, static r => r with
    {
        ZoneGap = Math.Min(r.ZoneGap, 14f), QuickGroupGap = Math.Min(r.QuickGroupGap, 14f), Subtitles = false,
        DaylistForm = ZuneDaylistForm.OneRow, DaylistH = 56f, DaylistArtW = 56f, ClockW = 0f, DaylistTitle = ZuneDaylistTitle.Group, DaylistTitleLines = 1,
        DaylistPad = 16f, DaylistMeta = false, DaylistRings = 1, RingLabels = false, RingSize = ZuneHomeRules.RingSize, PlayAtEnd = true,
        DaylistGrows = false,
    });

    /// <summary>The record for <paramref name="facts"/>: its content tier's, in its one-row daylist form where the window is too short for the
    /// strip (the height rung <see cref="ZuneHomeRules.DaylistRung"/> is not crossed); Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneHomeLayout Of(in TierFacts facts)
    {
        if (facts.Policy is null) return For(WidthTier.Standard);
        int i = Math.Clamp((int)facts.Content, 0, s_records.Length - 1);
        return DaylistFits(in facts) ? s_records[i] : s_oneRow[i];
    }

    /// <summary>The window is tall enough for the daylist strip: its height rung is crossed (or the policy declares none).</summary>
    public static bool DaylistFits(in TierFacts facts)
        => facts.Policy is null || facts.Policy.IndexOfRung(ZuneHomeRules.DaylistRung) < 0 || facts.Rung(ZuneHomeRules.DaylistRung);
}

/// <summary>What one row of Zune's Home column is.</summary>
public enum ZuneHomeRowKind : byte
{
    /// <summary>The quickplay strip (All only): <see cref="ZuneHomeRow.First"/> is the Recents zone or -1, <see cref="ZuneHomeRow.Second"/>
    /// the release-list zone or -1.</summary>
    Quickplay,

    /// <summary>One zone (<see cref="ZuneHomeRow.First"/>).</summary>
    Single,

    /// <summary>Two zones side by side where the tier has room (<see cref="ZuneHomeRow.First"/>, <see cref="ZuneHomeRow.Second"/>).</summary>
    Pair,
}

/// <summary>One row of Zune's Home column: indices into the facet's zones.</summary>
public readonly record struct ZuneHomeRow(ZuneHomeRowKind Kind, int First, int Second = -1);

/// <summary>Zune's Home plan: which zones stand where (pure, pinned by the rules tests).</summary>
public static class ZuneHomePlan
{
    /// <summary>The rows of a facet's zones. On All (<paramref name="quickplay"/>) the strip leads (the daylist, the planner's first zone, follows
    /// it) and takes the Recents zone as its history, so the Recents zone stands nowhere else; the release list stays in the column too (the board
    /// draws it twice: three covers in the strip, the list below). Two neighbours that pair (a release list and the clusters, new episodes and
    /// continue listening) share one row; every other zone is a row of its own, in the planner's order.</summary>
    public static ImmutableArray<ZuneHomeRow> Rows(IReadOnlyList<ZoneKind> kinds, bool quickplay)
    {
        var rows = ImmutableArray.CreateBuilder<ZuneHomeRow>(kinds.Count + 1);
        int recents = -1, releases = -1;
        if (quickplay)
        {
            for (int i = 0; i < kinds.Count; i++)
            {
                if (kinds[i] == ZoneKind.RecentGrid && recents < 0) recents = i;
                if (kinds[i] == ZoneKind.ReleaseList && releases < 0) releases = i;
            }
            rows.Add(new ZuneHomeRow(ZuneHomeRowKind.Quickplay, recents, releases));
        }
        for (int i = 0; i < kinds.Count; i++)
        {
            if (i == recents) continue;
            if (i + 1 < kinds.Count && i + 1 != recents && Pairs(kinds[i], kinds[i + 1]))
            {
                rows.Add(new ZuneHomeRow(ZuneHomeRowKind.Pair, i, i + 1));
                i++;
                continue;
            }
            rows.Add(new ZuneHomeRow(ZuneHomeRowKind.Single, i));
        }
        return rows.ToImmutable();
    }

    /// <summary>Whether <paramref name="a"/> followed by <paramref name="b"/> share a row.</summary>
    public static bool Pairs(ZoneKind a, ZoneKind b) => (a, b) switch
    {
        (ZoneKind.ReleaseList, ZoneKind.ClusterCards) or (ZoneKind.ClusterCards, ZoneKind.ReleaseList) => true,
        (ZoneKind.EpisodeLead, ZoneKind.ContinueEpisodes) or (ZoneKind.EpisodeRows, ZoneKind.ContinueEpisodes) => true,
        _ => false,
    };

    /// <summary>Whether a facet opens on the quickplay strip: All (the empty facet id).</summary>
    public static bool HasQuickplay(string facet) => facet.Length == 0;
}
