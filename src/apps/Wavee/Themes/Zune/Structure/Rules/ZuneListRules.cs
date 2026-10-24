// ── Themes/Zune/Structure/Rules/ZuneListRules.cs — the numbers and pure rules of Zune's lists (themes plan 6.3; P5 Z3) ───────────
//
// What Zune's list styles (`Structure\Lists\Zune*.cs`) read instead of writing a literal (WVTH003 allows literals only in `Rules\**`):
//
//   · THE ROWS BY DENSITY. The row height is the style's density (`style.density`, Z-Theme-Options "row size · every song list": compact 44 ·
//     standard 52 · comfortable 60), published by the style as `StyleMetrics.TrackRows` and sized by the core's table from it. The rules below
//     restate the ladder the structure draws against (the art edge inside each row, the density a published rung names) so a test pins the
//     two equal and the "Row size" menu names the rung on screen without reading the style's option (WVTH005). P5 decision 3: 4K keeps the
//     user's density (the 4K boards' "rows 60" does not override it).
//   · THE ROW'S STATES (Z-State-Rows, Z-State-Rows-Compact): rest is bare (no zebra, no plate, no border, square); hover the 5 % ink wash
//     (`R.Layer.RowHover`) with the heart and "..." revealed; selected the 12 % accent wash (`R.Layer.SelectedRest`) and the filled check;
//     keyboard focus the engine's ring; PLAYING is accent ink on the title, its subline and every factual lane plus the bars, with no plate;
//     unavailable 45 % ink that says why in the time lane; a drop target a 2-DIP accent line on the row's top edge.
//   · THE NUMERALS (Z-Album, Z-Artist): an album's track number and the chart's rank are the Numeral role (22·300, 28·300 large on the
//     artist page) in the tertiary ink; the playing row trades its number for the accent bars.
//   · THE POLICIES Zune publishes for the lists (`ZunePolicies.Lists.cs`): ONE row skin (Modern: the two-line row, artists in the subline),
//     track artwork on (an album's rows carry none: the core's album lane set has no thumb), and the detail table's records per window
//     tier at the density's rows (ZT5, Z-Sizes-Breakpoints "track rows"):
//
//       tier      lanes (each only where the list column's table ladder, `ZuneDetailRules.Table`, admits it)        insights        rail rest
//       Compact   the Compact row: art or number, title over "artist · time", the heart and a 40 "..." (no header)   sheet           -
//       Small     # · art · title/artist · time · "..."; the Album lane near the top of the tier ("album from 900")  sheet           -
//       Standard  + album, added, plays                                                                              aside, closed   240
//       Huge      the same                                                                                           aside, open     320
//       Huge 4K   the same (the wide twin past the third pane's rung)                                                aside, open     400
//
//     The lane WIDTHS are the core's (`Track.TracksFor`); the boards' lane widths (56 / 110 / 48 / 64 / 48 at Huge) are within a few DIP of them.
//
// Public, so `Wavee.Tests` pins the rules (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The numbers and pure rules of Zune's lists.</summary>
public static class ZuneListRules
{
    // ── the rows by density ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The densities in rung order (the "Row size" menu's rows).</summary>
    public static ImmutableArray<ZuneDensity> Densities { get; } = [ZuneDensity.Compact, ZuneDensity.Standard, ZuneDensity.Comfortable];

    /// <summary>A track row's height at <paramref name="density"/>: 44 / 52 / 60 (Z-Tokens "row 52 (compact 44 · comfortable 60)").</summary>
    public static float RowFor(ZuneDensity density) => density switch
    {
        ZuneDensity.Compact => 44f,
        ZuneDensity.Comfortable => 60f,
        _ => 52f,
    };

    /// <summary>A track row's art edge at <paramref name="density"/>: 32 / 36 / 40 (the 40 of Z-State-Rows at comfortable, the panel's 36 at
    /// standard), always 8 or more inside the row.</summary>
    public static float ArtFor(ZuneDensity density) => density switch
    {
        ZuneDensity.Compact => 32f,
        ZuneDensity.Comfortable => 40f,
        _ => 36f,
    };

    /// <summary>The density a published rung names (<c>TrackRowMetrics.Rung</c>: 0 compact, 1 standard, 2 comfortable); an unknown rung is the
    /// default, standard.</summary>
    public static ZuneDensity DensityOfRung(int rung) => rung switch
    {
        0 => ZuneDensity.Compact,
        2 => ZuneDensity.Comfortable,
        _ => ZuneDensity.Standard,
    };

    /// <summary>The layer-qualified id the "Row size" menu requests a density by (the style's `density` option).</summary>
    public const string DensityQualifiedId = "style.density";

    /// <summary>The word for a density in the "Row size" menu (the style's own chip words, Z-Theme-Options).</summary>
    public static string DensityLabelKey(ZuneDensity density) => density switch
    {
        ZuneDensity.Compact => ZuneStyleKeys.DensityCompact,
        ZuneDensity.Comfortable => ZuneStyleKeys.DensityComfortable,
        _ => ZuneStyleKeys.DensityStandard,
    };

    // ── the row's ink ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row lane's ink: the accent on the playing row (title, subline, plays, time: Z-State-Rows "playing · accent ink and bars, no
    /// plate"), the lane's own rest ink otherwise.</summary>
    public static ColorF RowInk(bool isNow, ColorF accent, ColorF rest) => isNow ? accent : rest;

    /// <summary>The playing row's plate: none (the accent is ink, never a fill).</summary>
    public static bool PlayingRowHasPlate => false;

    // ── the row's small numbers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transport glyph the # cell shows on hover, and the heart glyph.</summary>
    public const float TransportGlyph = 12f, HeartGlyph = 14f;

    /// <summary>The film glyph after a title with a video, the top track's star and the expand chevron.</summary>
    public const float FilmGlyph = 12f, StarGlyph = 11f, ChevronGlyph = 12f;

    /// <summary>The chart mark beside a chart row's number: 8 on a 12 line, 700.</summary>
    public const float ChartMarkSize = 8f, ChartMarkLine = 12f;

    /// <summary>The gap between the # cell's number and its chart mark, and between the subline's parts.</summary>
    public const float NumberGap = 2f, MetaGap = 4f;

    /// <summary>The subline's explicit "E": a 16 square, the letter at the 11-DIP text floor (it was 9.5 in a 14 square: the letter grew to the
    /// floor and the square with it, keeping the 0.68 share, so the "E" stays centred), radius 0 (Zune squares every plate).</summary>
    public const float ExplicitBox = 16f, ExplicitType = 11f;

    /// <summary>The "on this PC" mark that leads a row's subline (Z-Liked: a 14 ring, 2 stroke, the 8 DIP arrow glyph inside, 6 before the artist; the
    /// subline's own gap is <see cref="MetaGap"/>, so the mark adds the rest). A row that is dimmed offline says its reason in the time lane.</summary>
    public const float OnPcMark = 14f, OnPcRing = 2f, OnPcGlyph = 8f, OnPcAfter = 2f;

    /// <summary>The arrow the ring carries (Z-Liked draws the plain "arrow down", not a download tray): the icon font's Down glyph, so the 8-DIP
    /// mark is an icon, not text under the 11-DIP type floor.</summary>
    public const string OnPcArrow = "";

    /// <summary>The tempo lane's key swatch: a 6 DIP square at 0.85.</summary>
    public const float TempoDot = 6f, TempoDotOpacity = 0.85f;

    /// <summary>What leads the duration a Compact row states in its subline ("Kavinsky · 4:18", Z-State-Rows-Compact).</summary>
    public const string InlineDurationLead = " · ";

    /// <summary>A withheld row's short reason in the time lane ("unavailable", the release date).</summary>
    public const float ReasonSize = 11f;

    /// <summary>The drop target's accent line on the row's top edge (Z-State-Rows "2 dip accent line on the edge").</summary>
    public const float DropLine = 2f;

    /// <summary>A Compact row's "..." target (the brief's 40 x 40 rule, Z-State-Rows-Compact's 40 trailing lane).</summary>
    public const float CompactTarget = 40f;

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A slot's mount entrance (a re-cut list's crossfade replays it): opacity only, the grid cross-fade's beat.</summary>
    public static readonly LayoutTransition MountEntrance = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(ZuneMotion.GridCrossFadeMs, Easing.FluentDecelerate), Enter: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The heart's pop on a like edge: an Enter-only overshoot spring (a spring, so reduced motion keeps it) from 0.25 scale and 0
    /// opacity. No Exit leg: a scrolling list never spawns exit orphans for recycled glyphs.</summary>
    public static readonly LayoutTransition HeartPop = new(TransitionChannels.Opacity, TransitionDynamics.Spring(0.30f, 0.55f),
        Enter: new EnterExit(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true));

    /// <summary>The check lane arriving: the content slides over by its width.</summary>
    public static readonly LayoutTransition CheckShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));

    // ── the chrome (header, command bar, filter card, group head, unavailable bar) ───────────────────────────────────────

    /// <summary>The column header's word: 12 on 16 (Z-State-Rows' header row).</summary>
    public const float HeaderType = 12f, HeaderLine = 16f;

    /// <summary>The header's sort caret and the duration lane's clock glyph.</summary>
    public const float CaretGlyph = 8f, ClockGlyph = 12f;

    /// <summary>The caret's pop-in seed scale and the title word's rise on a swap.</summary>
    public const float CaretSeedScale = 0.6f, LabelRise = 4f;

    /// <summary>The 32-DIP command height and plate; the glyph and the gap between a glyph, its word and a trailing mark.</summary>
    public const float CommandHeight = 32f, CommandGlyph = 14f, CommandGap = 6f;

    /// <summary>A labelled command's horizontal padding.</summary>
    public static readonly Edges4 CommandPad = new(8f, 0f, 8f, 0f);

    /// <summary>The key prefix the core gives the page bar's lead command (the round play disc, which has no pad of its own).</summary>
    public const string PageLeadKeyPrefix = "cmd:page-lead:";

    /// <summary>The bar's left group shift (A5) so the FIRST glyph stands at the list's x: a padded command pulls back by its own left pad, the
    /// page bar's lead disc by the inset the core adds to line it up with a card's words (<paramref name="pageBarInset"/>, 2).</summary>
    public static float BarLeadShift(bool pageLead, float pageBarInset) => pageLead ? -pageBarInset : -CommandPad.Left;

    /// <summary>The disclosure chevron after a flyout command.</summary>
    public const float CommandChevron = 8f;

    /// <summary>The command edge at a window tier: the 32, or the Compact 40 target.</summary>
    public static float CommandEdgeFor(WidthTier window) => window == WidthTier.Compact ? CompactTarget : CommandHeight;

    /// <summary>The count badge (a filter count): 18 round, and its ring.</summary>
    public const float BadgeSize = 18f, BadgeRadius = 9f, BadgeRing = 1.5f;

    /// <summary>The labelled Filter command's badge slot.</summary>
    public const float FilterBadgeSlot = 22f;

    /// <summary>The page bar's lead: the play disc's glyph, the ring button's stroke (the board's 2-DIP white ring) and the divider before the
    /// chips (its slot and height).</summary>
    public const float LeadGlyph = 12f, RingStroke = 2f, LeadDividerSlot = 17f, DividerHeight = 16f;

    /// <summary>The date group head: 40 tall, the words on its floor 6 up, 8 between the name and the count.</summary>
    public const float GroupHeadSize = 22f, GroupHeadLine = 28f;

    /// <summary>See <see cref="GroupHeadSize"/>.</summary>
    public const float GroupHeadHeight = 40f, GroupHeadPadBottom = 6f, GroupHeadGap = 8f;

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

    /// <summary>A row cell a crossing moves or resizes (the one-tree page's lanes, audit B9: a list's number lane leaving the Compact row, the time folding into
    /// the subline, the trailing lanes swapping): it glides to its new lane on the pivot clock, laid out at its final size from the commit (a reveal, so the
    /// rows never relayout per frame); a lane only one form draws fades in or out.</summary>
    public static readonly LayoutTransition CellArrange = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut), SizeMode.Reveal, Enter: ZuneMotion.ColumnFade, Exit: ZuneMotion.ColumnFade);

    /// <summary>The header shifting over for the check lane.</summary>
    public static readonly LayoutTransition HeaderShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));

    /// <summary>The check lane's width the header shifts over by (the core's lane).</summary>
    public const float CheckLaneShift = 28f;

    /// <summary>The selection mode's accent tint at Compact (Z-State-Rows-Compact's selection bar).</summary>
    public const float SelectionTint = 0.12f;

    /// <summary>The filter card: 360 wide (never under 320), at most 620 tall; the scroll region (at most 500) is the fallback only. One 44 row
    /// per facet, a 168 combo, the 16 / 12 group padding, the 48 header.</summary>
    public const float CardWidth = 360f, CardMinWidth = 320f, CardMaxHeight = 620f, ScrollMaxHeight = 500f,
        FacetRowHeight = 44f, FacetRowPadX = 4f, FacetGap = 12f, ComboWidth = 168f,
        GroupPadX = 12f, GroupPadTop = 8f, GroupPadBottom = 12f,
        CardHeaderHeight = 48f, CardHeaderPadLeft = 16f, CardHeaderPadRight = 8f, CardHeaderGap = 8f;

    /// <summary>The unavailable-songs bar: 40 tall (44 at Compact), its glyph, the gap and the air under it.</summary>
    public const float UnavailableHeight = 40f, UnavailableCompactHeight = 44f, UnavailableGlyph = 14f, UnavailableGap = 12f, UnavailableBelow = 8f;

    /// <summary>The unavailable-songs bar's padding.</summary>
    public static readonly Edges4 UnavailablePadding = new(12f, 0f, 8f, 0f);

    /// <summary>The band the bar takes in the chrome while shown at a window tier: its height and the air under it.</summary>
    public static float UnavailableExtent(WidthTier window)
        => (window == WidthTier.Compact ? UnavailableCompactHeight : UnavailableHeight) + UnavailableBelow;

    /// <summary>The bar says why (the sentence with its reasons) from Standard; Compact and Small say the short one.</summary>
    public static bool UnavailableExplains(WidthTier window) => window >= WidthTier.Standard;

    // ── the episode row ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The episode row's copy column gap, the meta line's gap, the equalizer and the rule's corner (square).</summary>
    public const float EpisodeCopyGap = 4f, EpisodeMetaGap = 8f, EpisodeEqualizer = 12f, EpisodeToneBarGap = 8f;

    /// <summary>The figure-space runs the episode seed draws its bars with.</summary>
    public const int EpisodeSeedTitleRun = 28, EpisodeSeedDescriptionRun = 64;

    /// <summary>The "new" mark's tracking.</summary>
    public const float EpisodeNewTracking = 60f;

    // ── the lyrics lines (Z-Lyrics) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A state line's type (13 on 18) and the offline caption's measure.</summary>
    public const float LyricsMessageSize = 15f, LyricsMessageLine = 20f, LyricsOfflineCaptionMaxW = 320f;

    /// <summary>The peek gate's meta line: 12 on 16.</summary>
    public const float LyricsPeekMetaSize = 12f, LyricsPeekMetaLine = 16f;

    /// <summary>The skeleton's text ratio.</summary>
    public const float LyricsSkeletonTextRatio = 0.7f;

    /// <summary>The lines' weight: the LyricsLine role's (Zune draws every line at line level, Z-Lyrics); the translation's (the board's 300).</summary>
    public const ushort LyricsTranslationWeight = 300;

    /// <summary>A lyric row's opacity on the ground of the kind. The dark ground keeps the core ladder (the board's 1 / .45 / .24 / .14 / .11, past rows
    /// dimmer). The same alphas read far weaker as dark ink on light (the line next to the active one measured 3.0 : 1 and the quieter ones 1.2 to 1.7 :
    /// 1), so the light ladder lifts them: the line either side of the active one holds 3 : 1 on the stage's pink wash (a 56 DIP run, large text),
    /// the next ring about 2 : 1 and the rest sit at a quiet 1.5 : 1, still visible. Podcast rows that do not own playback stay at 1.</summary>
    public static float LyricRowOpacity(bool podcast, bool ownsPlayback, int emphasis, bool light)
    {
        if (!light || Lyrics.TranscriptPresentation.Neutral(podcast, ownsPlayback)) return Lyrics.TranscriptPresentation.RowOpacity(podcast, ownsPlayback, emphasis);
        int dist = Lyrics.Emphasis.DistOf(emphasis);
        if (dist == 0) return 1f;
        bool past = (emphasis & Lyrics.Emphasis.PastBit) != 0;
        return past
            ? dist switch { 1 => LightNeighbour, 2 => 0.32f, _ => 0.22f }
            : dist switch { 1 => LightNeighbour, 2 => 0.34f, 3 => 0.26f, 4 => 0.22f, _ => 0.20f };
    }

    /// <summary>The light ladder's first ring (the line next to the active one, either side): 56 % of the ink reaches 3 : 1 on the darkest wash the stage paints.</summary>
    public const float LightNeighbour = 0.56f;

    /// <summary>The active line's ink: the accent (Z-Lyrics: the line being sung is the accent, every other line the ink at the opacity ramp).</summary>
    public static ColorF LyricsLineInk(bool active, ColorF accent, ColorF rest) => active ? accent : rest;

    // ── the artist chart (Z-Artist) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A chart row: 60 tall on a 0 gap (the rows touch, Z-Artist), the header 16 over them; the 264 card minimum is the two-column
    /// breakpoint (as Classic's) and the edge fade the shelf's.</summary>
    public const float ChartRowHeight = 60f, ChartCellGap = 0f, ChartHeaderGap = 8f, ChartMinCardWidth = 264f, ChartEdgeFade = 24f;

    /// <summary>The chart heading's gap to its "see all" word, and the word's drop onto the heading's baseline.</summary>
    public const float ChartHeadGap = 20f, ChartBaseline = 4f;

    /// <summary>The rank box (the board's 52 lane), the art edge (48) and the skeleton's art, and the gaps.</summary>
    public const float ChartRankBox = 52f, ChartArt = 48f, ChartSubGap = 6f, ChartTrailGap = 12f, ChartFeatGap = 4f, ChartPagerChevron = 28f;

    /// <summary>The chart's height when its rows per column change: the layout lands once, the presented height tweens and everything below
    /// rides it (the engine's flow reveal), never a jump.</summary>
    public static readonly LayoutTransition ChartResize = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>A rail row (Z-Artist's "latest release" and "artist pick"): a row at <paramref name="art"/> whose title stays the 15/20 card-title rung
    /// (the board draws 15 beside a 96 cover; the core's page-hero rung belongs to Search's top result). The mark is the row shape's MetaLine dial,
    /// which a row never reads (it is a stack's reserved meta line).</summary>
    public static SurfaceShape RailRow(float art) => Shape.Row(art) with { MetaLine = true };

    /// <summary>The shape is a <see cref="RailRow"/>.</summary>
    public static bool IsRailRow(in SurfaceShape s) => s.IsRow && s.MetaLine;

    /// <summary>The chart's decode size for a 48 cover at 2x.</summary>
    public const int ChartArtDecodePx = 96;

    // ── the drawer ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The format split button: 28 tall, a 30 play half and a 20 caret half, their glyphs.</summary>
    public const float SplitHeight = 28f, PlayHalfWidth = 30f, CaretHalfWidth = 20f, PlayGlyph = 11f, CaretGlyph2 = 8f;

    /// <summary>The versions head's air below, the version title's gap to its meta and the pending bars' gap.</summary>
    public const float DrawerHeadBottom = 6f, DrawerTextGap = 2f, DrawerPendingGap = 6f;

    /// <summary>The pending row's bars.</summary>
    public const float PendingTitleBarW = 132f, PendingTitleBarH = 11f, PendingMetaBarW = 84f, PendingMetaBarH = 9f;

    /// <summary>A pending fact's opacity.</summary>
    public const float PendingFactOpacity = 0.6f;

    /// <summary>The video thumb's play badge.</summary>
    public const float PlayBadge = 22f, PlayBadgeGlyph = 11f;

    /// <summary>The now-playing overlay's FAB on a version thumb: 0.62 of the thumb's short edge, 22 to 28.</summary>
    public static float VersionFab(float w, float h) => Math.Clamp(MathF.Min(w, h) * 0.62f, 22f, 28f);

    // ── the surface ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The selected tile's accent border (2 DIP).</summary>
    public const float SurfaceSelectedBorder = 2f;

    /// <summary>The Outline plate's dash.</summary>
    public const float SurfaceDash = 3f;

    /// <summary>A SHELF card (A5): no plate padding (the art stands at the heading's x, at the card's whole width), the art 6 over its caption
    /// lines, and its hover a wash over the art alone.</summary>
    public const float ShelfCaptionGap = 6f;

    /// <summary>The node key the shelf stack body carries, so the shell knows it wears the art wash instead of the whole-card plate.</summary>
    public const string ShelfBodyKey = "zune-surface-shelf";

    /// <summary>The empty air a shelf card keeps UNDER its labels so it is exactly as tall as the core's estimate and seed face
    /// (<c>SurfaceGeometry.ShelfHeight</c>: 4 gutter + 8 pad + cover(w - 16) + 8 gap + labels + 8 pad). The live card has no pad and a 6 gap,
    /// so its cover is taller by 16 / aspect and its chrome shorter by 18; the difference is this slack (2 for a square cover, 9 for a 16:9 one;
    /// never negative). A hydrating card never re-measures its row.</summary>
    public static float ShelfSlack(float width, float aspect)
    {
        float pad = SurfaceGeometry.ShelfPlatePad;
        float estimated = SurfaceGeometry.CoverHeight(width - 2f * pad, aspect);
        float live = SurfaceGeometry.CoverHeight(width, aspect);
        return MathF.Max(0f, 3f * pad - ShelfCaptionGap + estimated - live);
    }

    // ── the policies ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list policy Zune publishes: track artwork on, ONE row skin (Modern: the two-line row with the artists in the subline, Z-State-Rows).
    /// Zune has no row-style or hide-artwork option, so the policy is fixed.</summary>
    public static ListPolicy Lists { get; } = new(TrackArtwork: true, RowStyle: TrackRowStyle.Modern);

    /// <summary>Compact (Z-State-Rows-Compact, Z-Album-Compact): the Compact row form (no column header, the heart trailing, a 40 "...", the time in the
    /// subline), no Album, Plays or Date lane, no list "#"; the density's rows (no floor).</summary>
    public static DetailTierLayout Compact { get; } = new(
        new DetailTableTier(CompactRows: true, AlbumColumn: false, PlaysColumn: false, DateColumn: false, ListIndex: false),
        InsightsDocked: false, RailRest: 0f, HeroActionExtra: 0f);

    /// <summary>Small (Z-Album-Small: # · title · time · "...", Z-Sizes-Breakpoints "album from 900"): the Album lane may show once the list column's
    /// table ladder admits it (a playlist's or Liked Songs' list, a window near the top of the tier; an album's own rows have none), no Plays lane,
    /// the Date lane only on a quiet list; the density's rows. The insights stay a sheet over the list (no aside docks below Standard).</summary>
    public static DetailTierLayout Small { get; } = new(
        new DetailTableTier(CompactRows: false, AlbumColumn: true, PlaysColumn: false, DateColumn: false, HoverActions: true, DurationWidth: HoverTimeLane),
        InsightsDocked: false, RailRest: 0f, HeroActionExtra: 0f);

    /// <summary>Standard and Huge (Z-Album, Z-Album-Huge, Z-Liked, Z-State-Rows): every lane the width admits, the density's rows. Z6a1: a two-column
    /// page docks its ASIDE from Standard (Z-Album's 260 "more by" column; the frame folds it again where the CONTENT tier is narrower, a docked
    /// panel's page), and the identity column rests at the board's 240 (the core's list seed reads it; Zune draws no grip).</summary>
    public static DetailTierLayout Standard { get; } = DetailTierLayout.Standard with
    {
        AsideDocked = true, RailRest = ZuneDetailRules.Standard.RailW,
        Table = DetailTableTier.Standard with { HoverActions = true, OneLineTrim = AlbumRowTrim, DurationWidth = HoverTimeLane },
    };

    /// <summary>Huge (Z-Album-Huge, Z-Liked-Huge, Z-Playlist-*-Huge): Standard's, the identity column resting at 320 (Z6a1), and the insights REST OPEN
    /// as a column of the page (ZT5: Z-Sizes-Breakpoints "liked songs: huge: insights column", Z-Liked-Huge draws the 420 column open). The pane is the
    /// aside's section (Zune docks it there from Standard, `AsideDocked`), so it is never a sheet over the list here; the reader's own choice wins from
    /// the first toggle (the core's `Rest`).</summary>
    public static DetailTierLayout Huge { get; } = Standard with { RailRest = ZuneDetailRules.Huge.RailW, InsightsRestOpen = true };

    /// <summary>Huge 4K (Z-Album-Huge4K), the wide twin the core reads past the third pane's rung (<see cref="ZuneTiers.PaneThirdRung"/>, a window of
    /// 3200): Huge's table and aside, the identity column resting at the board's 400 (the core's list seed reads it for its page-width estimate; Zune
    /// draws the record's width).</summary>
    public static DetailTierLayout Huge4K { get; } = Huge with { RailRest = ZuneDetailRules.Huge4K.RailW };

    /// <summary>The detail tiers Zune publishes: the table's form and lane caps per window tier, the aside and the insights' rest, and the identity
    /// column's resting width (Z3, Z6a1, ZT5). The finer tier numbers (the rail, the aside, the rings) are the frame's own records
    /// (<see cref="ZuneDetailTiers"/>), read by the CONTENT tier.</summary>
    public static DetailTierPolicy DetailTiers { get; } = new(Compact, Small, Standard, Huge,
        RailTitleShort: 28f, RailTitleTall: 40f, DescriptionShort: 3, DescriptionRegular: 6)
    {
        HugeWide = Huge4K, HugeWideRung = ZuneTiers.PaneThirdRung,
    };

    /// <summary>An album's numbered rows (one line, no art) stand this far under the density's row: Z-Album's 42 under the standard 52 (compact 34, comfortable 50).</summary>
    public const float AlbumRowTrim = 10f;

    /// <summary>A liked track's resting heart in the time lane: its glyph and its gap to the time.</summary>
    public const float RestHeartGlyph = 12f, RestHeartGap = 6f;

    /// <summary>The time lane where the heart and the "..." stand on row hover: the core's 64 widened so a withheld row's resting heart, its gap and
    /// "not on PC" (11, about 50) fit at rest without touching or clipping (12 + 6 + 50).</summary>
    public const float HoverTimeLane = 68f;

    /// <summary>An album's number sits this far in from its lane's start (Z-Album: 4), and the hover "..." target in the time lane.</summary>
    public const float NumberInset = 4f, HoverMoreBox = 28f;

    /// <summary>A row's height in a window tier's table record at a density: the density's row at every tier (no floor: "Standard rows").</summary>
    public static float TableRowFor(WidthTier window, ZuneDensity density)
        => DetailTiers.For(window).Table.RowFor(classic: false, RowFor(density));
}
