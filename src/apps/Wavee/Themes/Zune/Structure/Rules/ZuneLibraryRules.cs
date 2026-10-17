// ── Themes/Zune/Structure/Rules/ZuneLibraryRules.cs — the numbers of Zune's library (themes plan 6.3; P5 Z7) ─────────────────────────────────
//
// Boards Z-Library-Albums, -Artists, -Podcasts, -Audiobooks (Standard; the -Compact / -Small / -Huge twins feed the tier records, which ZT7 tunes). The
// library is the band's "library" pivot; its kinds (liked · playlists · albums · artists · podcasts · audiobooks) are the band's row 2, so the page draws NO
// title and NO kind chips at Standard and up. Under the band, on the frame's gutter, 8 down:
//
//   head       sort (the kind's words, the lit one primary, a re-tap flips its direction) | view (grid · list, and the "…" menu of the four views and the
//              S/M/L steps) | the find word (a bottom-rule field, 160) | the count, 28 tall, 24 between words, 13 type
//   body       16 under the head: the navigator | the pane, 40 apart. Albums, Podcasts, Audiobooks: the navigator takes the width (its grid or its list), the
//              pane is 380 (the album's songs, 40 rows). Artists: a 220 list (letter · name · count) | the reader taking the width (the band, then a block per
//              release); there is no third pane, the reader is the leaf.
//   pane       the title 22 light, its meta 12 and the verbs under it as words (the 36 rings: play, shuffle, save, open album), the songs as the shared eager
//              rows. The album's "also by" is a line of links under the songs.
//
// WHAT THE CORE FIXES. The navigator's row extents (`LibraryNavMount.RowExtent`: a 56 plate in a 60 pitch, a 40 compact plate in a 44), the letters' arithmetic and
// the artist reader's block extents (`Artist.ReaderShape`) are the host's: the pictures keep those pitches, so the artists' rows stand 44 apart, not the board's 34.
//
// THE COLLAPSE. The columns fold into the breadcrumb drill-in below `SplitFrom` of the page's OWN width, held 24 either way (the core resolves the ladder by id,
// `LibraryLadders.Layout`, and never sees a number).
//
// Public, so `Wavee.Tests` pins the ladder and the records (the assembly has no InternalsVisibleTo). Literal numbers are allowed in `Rules\**` (WVTH003).

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The constants and node keys of Zune's library.</summary>
public static class ZuneLibraryRules
{
    // ── the node keys the views carry (the tests find them) ──────────────────────────────────────────────────────────────

    /// <summary>The page, its two arms, the head, the navigator, the pane, the reader, the Your Episodes body, the search arm.</summary>
    public const string PageKey = "zune-lib-page", WideKey = "lib:wide", SavedKey = "lib:wide:saved", CollapsedKey = "lib:collapsed", HeadKey = "zune-lib-head",
        NavKey = "lib:nav", PaneKey = "lib:detail", ReaderKey = "lib:reader", EmptyKey = "lib:empty", SearchKey = "lib:search";

    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's top under the band, and the gap from the head to the body.</summary>
    public const float PageTop = 8f, HeadBodyGap = 16f;

    /// <summary>The navigator's cap at Huge and 4K (the list or the grid of covers): a 2400 page is never one 1960-wide list.</summary>
    public const float NavCap = 960f;

    /// <summary>The head: its height, the gap between its words and the gap between its groups, the find field's width.</summary>
    public const float HeadH = 28f, WordGap = 24f, GroupGap = 24f, FindW = 160f, FindGlyph = 14f, DividerH = 14f, DividerW = 1f;

    /// <summary>The words' type is the dense meta voice (13); the count follows it.</summary>
    public const float ChevronGlyph = 10f, WordChevronGap = 4f, SaveGlyph = 16f;

    // ── the navigator ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A list row's art, the corner of a square one is the cover role's, the gap between art and words, the row's side padding.</summary>
    public const float RowArt = 40f, RowGap = 12f, RowPadX = 8f;

    /// <summary>A grid card: the name under the cover (13) and the count line (12), the gap between cover and name.</summary>
    public const float CardNameGap = 6f, CardDecodePx = 256f, CardPadSelected = 2f, CardRing = 2f;

    /// <summary>The loading state's shimmer rows and cards.</summary>
    public const int NavSkelRows = 8, NavSkelCards = 24;

    /// <summary>The shimmer bars: a row's title and subtitle, a card's name.</summary>
    public const float NavSkelTitleW = 160f, NavSkelSubtitleW = 96f, NavSkelCardNameW = 64f;

    // ── the collapsed drill ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drill's fade and the arriving leaf's slide in from the right.</summary>
    public const float DrillMs = 160f, DrillDx = 12f;

    // ── the pane (album, show) ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane's header: the cover (the board draws none in the pane; the verbs and the words lead), the title/meta gap, the command row's gap and its top.</summary>
    public const float PaneTitleGap = 2f, PaneMetaBottom = 6f, PaneCommandsGap = 20f, PaneCommandsTop = 8f, PaneCommandsBottom = 12f, PaneTitleSlotH = 28f, PaneMetaSlotH = 16f;

    /// <summary>The pane's command targets: the stock edge, and the Compact tier's (a 40 target).</summary>
    public const float CommandBox = 36f, CompactCommandBox = 40f;

    /// <summary>The selection swap: the entering pane rises 6 over 180 ms while the leaving one fades over 120.</summary>
    public const float SwapInMs = 180f, SwapOutMs = 120f, SwapDy = 6f;

    /// <summary>The counted shimmer's bars.</summary>
    public const float ShimNumW = 14f, ShimClockW = 30f, ShimArtistW = 120f, ShimBarH = 11f, ShimTitleBarH = 22f, ShimTitleW = 220f, ShimMetaW = 140f;

    /// <summary>The title bar's four lengths in rotation, so a column of rows reads as text and not as a fence.</summary>
    public static float ShimRowTitleW(int i) => (i & 3) switch { 0 => 208f, 1 => 264f, 2 => 176f, _ => 232f };

    /// <summary>"Also by": the gap between the line's words, its top, its bottom.</summary>
    public const float AlsoByGap = 12f, AlsoByTop = 8f, AlsoByBottom = 24f;

    // ── the artists' reader ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One spine cover's edge, its ring and its corner; the dot's resting opacities and its hover.</summary>
    public const float DotEdge = 36f, DotRing = 2f, DotArtRadius = 2f;

    /// <summary>The dot's resting opacities: the current block, a block in your library, a catalogue block; and its hover.</summary>
    public const float DotOpacityCurrent = 1f, DotOpacitySaved = 0.5f, DotOpacityCatalogue = 0.3f, DotOpacityHover = 0.9f;

    /// <summary>The spine's item pitch: the cover and the 8 under it.</summary>
    public const float DotPitch = 44f;

    /// <summary>The decode sizes of the band's avatar, a spine cover and a block's cover.</summary>
    public const int AvatarDecodePx = 144, DotDecodePx = 72, BlockDecodePx = 256;

    /// <summary>The reader's sort has three words (newest, oldest, a-z); a counted-skeleton row's number lane and clock lane.</summary>
    public const int ReaderSortWords = 3;

    /// <summary>The counted-skeleton row's lanes: the number and the duration.</summary>
    public const float ReaderNumLane = 28f, ReaderClockLane = 52f;

    /// <summary>The reader's insets: the band, a block and the sub-rail pad on the page's side by this much.</summary>
    public const float ReaderInset = 16f;

    /// <summary>The reader's name (the hero voice, light) size cap for the band, and the head title's size.</summary>
    public const float HeadTitleSize = 20f;

    /// <summary>The head's shimmer bars.</summary>
    public const float ShimmerMetaW = 90f, ShimmerMetaH = 16f, ShimmerTitleW = 180f;

    /// <summary>The block rows: the lanes of the shared eager row (number, title, the trailing heart, duration).</summary>
    public static readonly Track.ColumnSet Cols = new(Album: false, By: false, Date: false, Plays: false,
                                                      Heart: false, Thumb: false, Actions: false, HeartTrailing: true);

    /// <summary>The lane widths of <see cref="Cols"/>.</summary>
    public static readonly TrackSize[] Tracks =
        [TrackSize.Px(28f), TrackSize.Star(1f), TrackSize.Px(Track.Lane.HeartTrailing), TrackSize.Px(52f)];

    /// <summary>The block row's options: the reader never prints a track's artist (the block is the credit).</summary>
    public static readonly Track.EagerRowOptions RowOptions = new(ShowTrackArtist: false);

    /// <summary>The artist band's folded verb row: the whole row's width below which shuffle and "go to" fold into a "…" menu.</summary>
    public static bool FoldVerbs(float laneWidth) => laneWidth < Artist.ReaderShape.ActionsW;

    // ── the search columns ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hit rows' shimmer count, the gap between hit rows, a hit's enter/exit.</summary>
    public const int SkelRows = 6;

    /// <summary>The gap between hit rows and a hit's motion.</summary>
    public const float HitGap = 2f, HitEnterMs = 90f, HitEnterDy = 3f, HitExitMs = 70f, RefiningAlpha = 0.4f;

    /// <summary>The search columns' floors: the albums column (rail open, closed) and the tracks column.</summary>
    public const float SearchAlbumsMinRailOpen = 220f, SearchAlbumsMin = 300f, SearchTracksMin = 220f, SearchMidDefault = 340f;

    /// <summary>An artist / album hit's cover and row floor; a track hit's cover.</summary>
    public const float NameArt = 40f, NameRowFloor = 56f, TrackArt = 36f;

    /// <summary>The artist / album hit's shape.</summary>
    public static readonly SurfaceShape NameShape = Shape.Row(NameArt) with { MinHeight = NameRowFloor };

    /// <summary>The track hit's shape: its cover and the surface's row padding either side.</summary>
    public static readonly SurfaceShape TrackShape = Shape.Row(TrackArt) with { MinHeight = TrackArt + SurfaceGeometry.RowPad + SurfaceGeometry.RowPad };

    /// <summary>An artist is a person: a circular cover.</summary>
    public static bool IsCircular(EntityKind kind) => kind == EntityKind.Artist;

    // ── Your Episodes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cold seed's rows.</summary>
    public const int SavedSeedRows = 6;

    // ── the view menu ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "…" view menu's trigger edge (a 28 target; the Compact tier's is the record's).</summary>
    public const float ViewMoreEdge = 28f, ViewMoreGlyph = 16f;
}

/// <summary>What Zune's library page is in one layout tier.</summary>
public sealed record ZuneLibraryLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The columns are folded into the breadcrumb drill-in (the navigator, then the pane or reader, one at a time): the Compact tier.</summary>
    public bool Single => Tier == WidthTier.Compact;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The height of the head's words and of the controls (40 at Compact, the target; 28 elsewhere).</summary>
    public required float HeadH { get; init; }

    /// <summary>The gap between the head's rows (the kind chips, the sort and view row, the find field) at Compact.</summary>
    public required float HeadGap { get; init; }

    /// <summary>The gap between the navigator and the pane.</summary>
    public required float ColGap { get; init; }

    /// <summary>The pane's width beside the navigator (the album's songs).</summary>
    public required float PaneW { get; init; }

    /// <summary>The artists' list column.</summary>
    public required float ReaderNavW { get; init; }

    /// <summary>The navigator's floor beside a pane.</summary>
    public required float NavMinW { get; init; }

    /// <summary>The sort is ONE button with its menu (Small and Compact) instead of the kind's words.</summary>
    public required bool SortMenu { get; init; }

    /// <summary>The detail pane's floor beside the navigator.</summary>
    public required float RightMin { get; init; }

    /// <summary>The navigator's card-grid gaps (column, row).</summary>
    public required LibraryGridGaps GridGaps { get; init; }

    /// <summary>The full grid's size-S cell floor (NaN: the core's 116).</summary>
    public required float CardCellMin { get; init; }

    /// <summary>The navigator's widest (NaN: it takes what the pane leaves). Huge caps it at 960 so a 2400 page is not one 1960-wide list of tiny rows.</summary>
    public float NavMaxW { get; init; } = float.NaN;

    /// <summary>The pane's "open album" word shows (the pane's commands row has room for it); at Small the pane is 300 and the word would shrink to
    /// "o...", so the verb stays in the "..." menu instead.</summary>
    public bool OpenWord { get; init; } = true;

}

/// <summary>Zune's library ladder and records.</summary>
/// <summary>The library's layout ladder, apart from its records: <see cref="ZuneTiers.Policy"/> reads it while it is being built, and the
/// records read <see cref="ZuneTiers.Frame"/>, so the ladder must not live in a class whose initializer reads ZuneTiers (a type-initializer cycle).</summary>
public static class ZuneLibraryLadders
{
    /// <summary>The split layout begins here, of the page's own width.</summary>
    public const float SplitFrom = 640f;

    /// <summary>The ladder's hold, both ways: entered below 616, left at 664.</summary>
    public const float SplitHold = 24f;

    /// <summary>The layout ladder: index 0 the one-column drill-in, 1 the split.</summary>
    public static TierLadder Layout { get; } = TierLadder.Of(new TierEdge(SplitFrom, NarrowHold: SplitHold, WidenHold: SplitHold));

    /// <summary>The ladders this family adds to <see cref="ZuneTiers.Policy"/>: the core host resolves them by id.</summary>
    public static ImmutableArray<NamedLadder> Ladders { get; } = ImmutableArray.Create(new NamedLadder(LibraryLadders.Layout, Layout));
}

public static class ZuneLibraryTiers
{
    /// <inheritdoc cref="ZuneLibraryLadders.SplitFrom"/>
    public const float SplitFrom = ZuneLibraryLadders.SplitFrom;

    /// <inheritdoc cref="ZuneLibraryLadders.SplitHold"/>
    public const float SplitHold = ZuneLibraryLadders.SplitHold;

    /// <inheritdoc cref="ZuneLibraryLadders.Layout"/>
    public static TierLadder Layout => ZuneLibraryLadders.Layout;

    /// <inheritdoc cref="ZuneLibraryLadders.Ladders"/>
    public static ImmutableArray<NamedLadder> Ladders => ZuneLibraryLadders.Ladders;

    private static ZuneLibraryLayout Make(WidthTier tier, float headH, float headGap, float colGap, float paneW, float readerNavW, float navMin,
        bool sortMenu, float rightMin, LibraryGridGaps gaps, float cardCellMin) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, HeadH = headH, HeadGap = headGap, ColGap = colGap, PaneW = paneW, ReaderNavW = readerNavW,
        NavMinW = navMin, SortMenu = sortMenu, RightMin = rightMin, GridGaps = gaps, CardCellMin = cardCellMin,
    };

    private static readonly ZuneLibraryLayout[] s_records =
    [
        // Compact (Z-Library-*-Compact): the drill-in, every control 40, the kind chips over the head, the grid gaps 16 / 12.
        Make(WidthTier.Compact, headH: 40f, headGap: 12f, colGap: 0f, paneW: 0f, readerNavW: 0f, navMin: 0f, sortMenu: true, rightMin: 0f,
            new LibraryGridGaps(Column: 12f, Row: 16f), cardCellMin: float.NaN),
        // Small (Z-Library-*-Small): split, the pane 300, the artists' list 200, the sort a button, the grid starts at 96.
        Make(WidthTier.Small, headH: 32f, headGap: 8f, colGap: 24f, paneW: 300f, readerNavW: 200f, navMin: 200f, sortMenu: true, rightMin: 300f,
            new LibraryGridGaps(Column: 16f, Row: 12f), cardCellMin: 96f) with { OpenWord = false },
        // Standard (Z-Library-*): the tuned page, 220 / grid / 380, 40 apart.
        Make(WidthTier.Standard, headH: 28f, headGap: 8f, colGap: 40f, paneW: 380f, readerNavW: 220f, navMin: 240f, sortMenu: false, rightMin: 380f,
            new LibraryGridGaps(Column: 16f, Row: 16f), cardCellMin: float.NaN),
        // Huge (Z-Library-*-Huge): wider columns, the pane 440, the list 260, 48 apart.
        Make(WidthTier.Huge, headH: 28f, headGap: 8f, colGap: 48f, paneW: 440f, readerNavW: 260f, navMin: 320f, sortMenu: false, rightMin: 440f,
            new LibraryGridGaps(Column: 16f, Row: 20f), cardCellMin: float.NaN) with { NavMaxW = ZuneLibraryRules.NavCap },
        // Huge 4K: Huge's (pages cap at 2400, decision 2).
        Make(WidthTier.Huge4K, headH: 28f, headGap: 8f, colGap: 48f, paneW: 440f, readerNavW: 260f, navMin: 320f, sortMenu: false, rightMin: 440f,
            new LibraryGridGaps(Column: 16f, Row: 20f), cardCellMin: float.NaN) with { NavMaxW = ZuneLibraryRules.NavCap },
    ];

    /// <summary>The policy the structure publishes for the core host (`ThemePolicies.LibraryTiers`): the grid's gaps per content tier and Small's cell floor. The
    /// navigator's width is the flexible column's (no grip), so no resting width is stated.</summary>
    public static LibraryTierPolicy Policy { get; } = new()
    {
        SmallCardCellMin = 96f,
        CompactGridGaps = s_records[0].GridGaps, SmallGridGaps = s_records[1].GridGaps, StandardGridGaps = s_records[2].GridGaps, HugeGridGaps = s_records[3].GridGaps,
    };

    /// <summary>The record for <paramref name="facts"/>' content tier while the page is <paramref name="single"/> (the layout ladder's answer: the host's tier signal) or
    /// split. A split page in a Compact content tier (the ladders' holds differ by a frame) reads Small's; Standard's where no frame resolved the tiers.</summary>
    public static ZuneLibraryLayout Of(in TierFacts facts, bool single)
        => For(facts.Policy is null ? WidthTier.Standard : facts.Content, single);

    /// <summary>The record for content tier <paramref name="content"/>.</summary>
    public static ZuneLibraryLayout For(WidthTier content, bool single)
    {
        if (single) return s_records[0];
        return s_records[Math.Clamp(Math.Max((int)content, (int)WidthTier.Small), 1, s_records.Length - 1)];
    }
}
