// ── Themes/Zune/Structure/Rules/ZuneHomeRules.cs — the numbers and the plan of Zune's Home (themes plan 6.3; P5 Z5) ──────────────────
//
// Every number Zune's Home views draw with, read off the boards (Z-Home, Z-Home-Podcasts, Z-Light-Home; the Compact / Small / Huge boards for
// the tier records, which ZT4 tunes), and the one pure rule that turns a facet's zones into the page's rows (`ZuneHomePlan`). WVTH003 allows
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

    /// <summary>The clock: its right inset, its line gap, the bars (40 wide, 6 tall, the current one 20, 4 apart).</summary>
    public const float ClockPadRight = 32f, ClockGap = 8f, BarW = 40f, BarH = 6f, BarLitH = 20f, BarGap = 4f;

    /// <summary>The idle bars' ink: the primary text at 14 %.</summary>
    public const float BarIdleAlpha = 0.14f;

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

    /// <summary>The Browse tiles beside the charts, 2 : 1.</summary>
    public const float TilesShare = 2f, ChartsShare = 1f;

    /// <summary>The charts: a row and its rank's width; the customize word's step down.</summary>
    public const float ChartRowH = 40f, ChartRankW = 32f, ChartGap = 12f, CustomizeTop = 16f;

    /// <summary>The section drill page's narrowest cell.</summary>
    public const float SectionMinCell = 154f, SectionGap = 16f;
}

/// <summary>How the daylist strip is arranged in a tier (one tree in every form: only its direction and sizes are live props).</summary>
public enum ZuneDaylistForm : byte
{
    /// <summary>Art | copy | clock in one 176 strip (Standard and up).</summary>
    Strip,

    /// <summary>The art over the copy, the clock under it (Compact and Small).</summary>
    Stacked,
}

/// <summary>What Zune's Home lays out in one CONTENT tier.</summary>
public sealed record ZuneHomeLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's, so the page title and the band share an x).</summary>
    public required float Gutter { get; init; }

    /// <summary>The gap between zones.</summary>
    public required float ZoneGap { get; init; }

    /// <summary>The pins quickplay shows, and their art; the history and new tiles, and their art.</summary>
    public required int QuickPins { get; init; }

    /// <inheritdoc cref="QuickPins"/>
    public required float PinTileW { get; init; }

    /// <inheritdoc cref="QuickPins"/>
    public required int QuickTiles { get; init; }

    /// <inheritdoc cref="QuickPins"/>
    public required float QuickTileW { get; init; }

    /// <summary>The daylist's form, its height (the strip's; the stacked form's art), its art width and its clock column.</summary>
    public required ZuneDaylistForm DaylistForm { get; init; }

    /// <inheritdoc cref="DaylistForm"/>
    public required float DaylistH { get; init; }

    /// <inheritdoc cref="DaylistForm"/>
    public required float DaylistArtW { get; init; }

    /// <inheritdoc cref="DaylistForm"/>
    public required float ClockW { get; init; }

    /// <summary>Two zones that pair (releases | clusters, new | continue episodes) stand side by side.</summary>
    public required bool PairBeside { get; init; }

    /// <summary>The wide tiles' columns, the clusters' columns, the Browse tiles' columns.</summary>
    public required int WideCols { get; init; }

    /// <inheritdoc cref="WideCols"/>
    public required int ClusterCols { get; init; }

    /// <inheritdoc cref="WideCols"/>
    public required int TileCols { get; init; }

    /// <summary>The charts stand beside the Browse tiles (2 : 1) instead of under them.</summary>
    public required bool ChartsBeside { get; init; }

    /// <summary>A heading row carries its subtitle.</summary>
    public required bool Subtitles { get; init; }

    /// <summary>The daylist strip stacks (art over copy, the clock under it).</summary>
    public bool DaylistStacked => DaylistForm == ZuneDaylistForm.Stacked;
}

/// <summary>Zune's Home tier records (content tier; ZT4 tunes Compact, Small and Huge against their boards).</summary>
public static class ZuneHomeTiers
{
    private static ZuneHomeLayout Make(WidthTier tier, float zoneGap, int pins, float pinW, int tiles, float tileW, ZuneDaylistForm daylist,
        float daylistH, float daylistArtW, float clockW, bool pair, int wide, int clusters, int tileCols, bool chartsBeside, bool subtitles) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, ZoneGap = zoneGap, QuickPins = pins, PinTileW = pinW, QuickTiles = tiles, QuickTileW = tileW,
        DaylistForm = daylist, DaylistH = daylistH, DaylistArtW = daylistArtW, ClockW = clockW, PairBeside = pair, WideCols = wide,
        ClusterCols = clusters, TileCols = tileCols, ChartsBeside = chartsBeside, Subtitles = subtitles,
    };

    private static readonly ZuneHomeLayout[] s_records =
    [
        // Compact (Z-Home-Compact): 22 between zones, two 100 pins and two 88 tiles, the daylist stacked (a 140 art), one column of everything.
        Make(WidthTier.Compact, zoneGap: 22f, pins: 2, pinW: 100f, tiles: 2, tileW: 88f, ZuneDaylistForm.Stacked, daylistH: 140f, daylistArtW: 0f,
            clockW: 0f, pair: false, wide: 2, clusters: 1, tileCols: 2, chartsBeside: false, subtitles: false),
        // Small (Z-Home-Small): three pins, the daylist stacked (a 180 art), two wide tiles, two cluster columns, four Browse tiles.
        Make(WidthTier.Small, zoneGap: 28f, pins: 3, pinW: 120f, tiles: 3, tileW: 100f, ZuneDaylistForm.Stacked, daylistH: 180f, daylistArtW: 0f,
            clockW: 0f, pair: false, wide: 2, clusters: 2, tileCols: 4, chartsBeside: false, subtitles: true),
        // Standard (Z-Home): the tuned page.
        Make(WidthTier.Standard, zoneGap: 28f, pins: 4, pinW: 132f, tiles: 3, tileW: 100f, ZuneDaylistForm.Strip, daylistH: 176f, daylistArtW: 320f,
            clockW: 260f, pair: true, wide: 4, clusters: 2, tileCols: 4, chartsBeside: true, subtitles: true),
        // Huge (Z-Home-Huge): six pins, six wide tiles, six Browse tiles.
        Make(WidthTier.Huge, zoneGap: 28f, pins: 6, pinW: 132f, tiles: 4, tileW: 100f, ZuneDaylistForm.Strip, daylistH: 176f, daylistArtW: 320f,
            clockW: 260f, pair: true, wide: 6, clusters: 3, tileCols: 6, chartsBeside: true, subtitles: true),
        // Huge 4K: Huge's (the page caps at 2400, decision 2; plan "Home at 4K is built from the Huge rules").
        Make(WidthTier.Huge4K, zoneGap: 28f, pins: 6, pinW: 132f, tiles: 4, tileW: 100f, ZuneDaylistForm.Strip, daylistH: 176f, daylistArtW: 320f,
            clockW: 260f, pair: true, wide: 6, clusters: 3, tileCols: 6, chartsBeside: true, subtitles: true),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneHomeLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneHomeLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
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
