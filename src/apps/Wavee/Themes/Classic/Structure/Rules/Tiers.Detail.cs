// ── Themes/Classic/Structure/Rules/Tiers.Detail.cs — Classic's detail ladders and the detail pages' tier records (themes plan 3.2, 3.6; TC5) ──
//
// THE THREE SUB-LADDERS (moved out of core `Entities\Detail.cs` `Detail.Breakpoints` and `Detail.UI.cs`'s height rungs, plan 3.1/3.6), written
// with the engine's `TierLadder` so their holds behave like the window tiers' and a crossing found after mount commits on the first frame that
// is not resized and animates (plan 3.3). Classic's `TierPolicy` names them (`ClassicTiers.Policy.Ladders`); the core frame and table resolve
// them through `UseTier` by id (`Wavee.Theming.DetailLadders`) and never see a number.
//
//   mode    measured on the page's own content column. Index 0 the VERTICAL hero arm, 1 mode 2 (the narrow two-column arm, the 188 rail), 2 mode 1
//           (the 224 rail), 3 mode 0 (the wide arm, the kind's own rail). Edges 560 (held 20 BOTH ways: the vertical arm is entered below 540 and
//           left at 580, the old band), 660 and 820 (narrowed at once, re-admitted 24 past the edge, the old band). The old core ladder left the
//           vertical arm for the NOMINAL mode of the width, skipping the widening holds of every edge above 580 (an exit at 830 jumped straight to
//           the wide arm, where a widening from mode 1 holds until 844), and it snapped: the mode was a plain signal write in a layout callback.
//           Through the ladder the exit takes the edges' own holds like any other crossing (830 lands in mode 1), and the commit is deferred to a
//           non-resized frame, where the arms cross-fade (`ClassicDetailFrame`).
//   table   measured on the list column. Index 0 the narrowest tier (6, below 300) … 6 tier 0 (from 860). Edges 300, 340, 440, 560, 720 and 860,
//           held 24 on WIDENING (a lane yields at once and is re-admitted with margin, the old band).
//   height  measured on the window's height. Index 0 short (below 760: the rail description's tight clamp), 1 regular, 2 tall (from 900: the
//           rail title's tall cap). Held 40 on widening, like the window's Short | Regular edge.
//
// THE RECORDS (one per window band, boards C-Album / C-Playlist / C-Liked / C-Show / C-Episode with their -Compact / -Small / -Huge twins, C-TrackList,
// C-RowStates-Compact, C-MultiSelect-Compact). `Core` is what the core frame and table read (`ThemePolicies.DetailTiers`); the rest is what Classic's
// own views read:
//   · Compact: the vertical arm (the page is below 560); the C-TrackList compact row (56 Modern / 44 Classic, 40 art, the heart trailing, a 40 more
//     lane, no column header and no chevron lane, the duration in the Modern subline); the list bar's commands and the hero's actions 40 x 40, the
//     sort word leading the bar and the view tools as glyphs; the selection bar tinted with the accent (C-MultiSelect-Compact).
//   · Small: the two-column arm (mode 1's 224 rail at 800); no Album and no Plays lane, and the Date lane only on a quiet list (C-TrackList small).
//   · Standard: the tuned design (P3b), unchanged.
//   · Huge (and Huge 4K): the rail rests at 320 where it was never dragged, the rows at the cozy rung (56 / 44, 40 art), the insights pane docks as a
//     360 column beside the list (C-Liked-Huge) instead of covering it and rests OPEN there; a two-column page docks its ASIDE (contract 1.21): the album's
//     520 release / artist / fans / "More by" column (C-Album-Huge), a playlist's 440 recommendations and insights (C-Playlist-Huge), or the 400 insights
//     alone (C-PlaylistFollowed-Huge), right of the list behind a hairline, the list keeping its 8 inset before it.
//   · Huge 4K (`HugeWideRung`, from 3200 of window width, held 40 narrowing: the midpoint of the 2560 and 3840 boards): C-Album-Huge4K's 360 rail (336
//     art), the 640 album aside with its 360 artist band and 120 fans.
//
// Public, so `Wavee.Tests` pins the ladders and the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's detail ladders and the detail pages' tier records.</summary>
public static class ClassicDetailTiers
{
    // ── the mode ladder ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The two-column arm begins here (mode 2); below it the vertical hero arm.</summary>
    public const float TwoColumnFrom = 560f;

    /// <summary>The vertical arm's hold, both ways: entered below 540, left at 580.</summary>
    public const float VerticalHold = 20f;

    /// <summary>Mode 1 (the 224 rail) begins here.</summary>
    public const float MidFrom = 660f;

    /// <summary>Mode 0 (the wide arm) begins here.</summary>
    public const float WideFrom = 820f;

    /// <summary>The two-column edges' hold on widening (a narrower arm is taken at once).</summary>
    public const float ModeHold = 24f;

    /// <summary>The page-mode ladder (index = 3 - mode).</summary>
    public static TierLadder Mode { get; } = TierLadder.Of(
        new TierEdge(TwoColumnFrom, NarrowHold: VerticalHold, WidenHold: VerticalHold),
        new TierEdge(MidFrom, NarrowHold: 0f, WidenHold: ModeHold),
        new TierEdge(WideFrom, NarrowHold: 0f, WidenHold: ModeHold));

    // ── the table ladder ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The table tiers' edges, narrowest first: tier 5 from 300, 4 from 340, 3 from 440, 2 from 560, 1 from 720, 0 from 860.</summary>
    public static ImmutableArray<float> TableEdges { get; } = ImmutableArray.Create(300f, 340f, 440f, 560f, 720f, 860f);

    /// <summary>The table edges' hold on widening: a lane yields at once and must re-earn this much.</summary>
    public const float TableHold = 24f;

    /// <summary>The table-tier ladder (index = 6 - tier).</summary>
    public static TierLadder Table { get; } = BuildTable();

    static TierLadder BuildTable()
    {
        var edges = new TierEdge[TableEdges.Length];
        for (int i = 0; i < edges.Length; i++) edges[i] = new TierEdge(TableEdges[i], NarrowHold: 0f, WidenHold: TableHold);
        return new TierLadder(edges);
    }

    // ── the height rungs ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Below this window height the rail description clamps to its short rung.</summary>
    public const float RegularFrom = 760f;

    /// <summary>From this window height the rail title takes its tall cap.</summary>
    public const float TallFrom = 900f;

    /// <summary>The height rungs' hold on widening (the window's Short | Regular hold).</summary>
    public const float HeightHold = 40f;

    /// <summary>The height-rung ladder.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(
        new TierEdge(RegularFrom, NarrowHold: 0f, WidenHold: HeightHold),
        new TierEdge(TallFrom, NarrowHold: 0f, WidenHold: HeightHold));

    /// <summary>The three ladders as Classic's policy names them.</summary>
    public static ImmutableArray<NamedLadder> Ladders { get; } = ImmutableArray.Create(
        new NamedLadder(DetailLadders.Mode, Mode),
        new NamedLadder(DetailLadders.Table, Table),
        new NamedLadder(DetailLadders.Height, Height));

    // ── the pure reads (a view or a test that resolves a ladder itself, with no hook) ─────────────────────────────────────

    /// <summary>The page mode of a <paramref name="width"/>-wide column, held against <paramref name="previous"/> once
    /// <paramref name="initialized"/> (a fresh resolve uses the edges themselves). An unmeasured width keeps the previous mode.</summary>
    public static int ModeFor(float width, int previous, bool initialized = true)
    {
        if (!(width > 0f)) return previous;
        return DetailLadders.ModeOf(Mode.Resolve(width, initialized ? DetailLadders.IndexOfMode(previous) : -1));
    }

    /// <summary>The table tier of a <paramref name="width"/>-wide list column, held like <see cref="ModeFor"/>.</summary>
    public static int TierFor(float width, int previous, bool initialized = true)
    {
        if (!(width > 0f)) return previous;
        return DetailLadders.TierOf(Table.Resolve(width, initialized ? DetailLadders.IndexOfTier(previous) : -1));
    }

    // ── the records (per window tier) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Compact hit target (the brief's Compact rule) and the tuned 32 elsewhere.</summary>
    public const float CompactTarget = 40f, Target = 32f;

    /// <summary>The cozy rung the Huge rows sit at, and the Compact row (C-TrackList): Modern, Classic, art.</summary>
    public const float RowModern = 56f, RowClassic = 44f, RowArt = 40f;

    /// <summary>The rail's resting width at Huge where it was never dragged (C-Album-Huge, C-Playlist-Huge).</summary>
    public const float HugeRailRest = 320f;

    /// <summary>The widest a Huge detail page's row grows (C-Breakpoints: the Huge grids fill to 2400).</summary>
    public const float HugePageMax = 2400f;

    /// <summary>The docked insights column at Huge (C-Liked-Huge).</summary>
    public const float DockedInsightsWidth = 360f;

    /// <summary>The Huge 4K rung (C-Album-Huge4K): from this window width a Huge window reads <see cref="Huge4K"/>. The midpoint of the 2560 and 3840 boards.</summary>
    public const string HugeWideRung = "detail.huge.wide";

    /// <summary>Where the Huge 4K rung begins, and its hold on narrowing (the window tiers' 40).</summary>
    public const float HugeWideFrom = 3200f, HugeWideHold = 40f;

    /// <summary>The rung as Classic's tier policy declares it (window width).</summary>
    public static NamedRung HugeWide { get; } = new(HugeWideRung, new TierEdge(HugeWideFrom, NarrowHold: HugeWideHold, WidenHold: 0f), RungAxis.Window);

    /// <summary>The rail's resting width at Huge 4K where it was never dragged (C-Album-Huge4K: 360, its art 336).</summary>
    public const float Huge4KRailRest = 360f;

    /// <summary>The Play split's whole width at a button <paramref name="height"/>: both halves grow by what the height adds over the tuned 32
    /// ([Play 88 │ ⌄ 32] → [Play 96 │ ⌄ 40] at Compact, board C-Album-Compact). The skeleton reserves the same box.</summary>
    public static float PlaySplitWidthFor(float height)
        => Detail.RailLayout.PlaySplitWidth + 2f * MathF.Max(0f, height - Wavee.Controls.ButtonHeight);

    /// <summary>How much the Compact hero's action row outgrows the nominal 36: its 40 targets.</summary>
    public const float CompactActionExtra = CompactTarget - Target;

    /// <summary>Compact (C-Album-Compact, C-Playlist-Compact, C-Liked-Compact, C-TrackList compact, C-RowStates-Compact, C-MultiSelect-Compact).</summary>
    public static ClassicDetailTier Compact { get; } = new()
    {
        Core = new DetailTierLayout(
            new DetailTableTier(CompactRows: true, AlbumColumn: false, PlaysColumn: false, DateColumn: false, RowModern, RowClassic, RowArt),
            InsightsDocked: false, RailRest: 0f, HeroActionExtra: CompactActionExtra),
        Tier = WidthTier.Compact, Target = CompactTarget, SortLeadsBar = true, SelectionTinted = true,
    };

    /// <summary>Small (C-Album-Small, C-Playlist-Small, C-TrackList small).</summary>
    public static ClassicDetailTier Small { get; } = new()
    {
        Core = new DetailTierLayout(
            new DetailTableTier(CompactRows: false, AlbumColumn: false, PlaysColumn: false, DateColumn: false),
            InsightsDocked: false, RailRest: 0f, HeroActionExtra: 0f),
        Tier = WidthTier.Small, Target = Target, SortLeadsBar = false, SelectionTinted = false,
    };

    /// <summary>Standard: the tuned design.</summary>
    public static ClassicDetailTier Standard { get; } = new()
    {
        Core = DetailTierLayout.Standard,
        Tier = WidthTier.Standard, Target = Target, SortLeadsBar = false, SelectionTinted = false,
    };

    /// <summary>Huge and Huge 4K (C-Album-Huge, C-Album-Huge4K, C-Playlist-Huge, C-Liked-Huge).</summary>
    public static ClassicDetailTier Huge { get; } = new()
    {
        Core = new DetailTierLayout(
            new DetailTableTier(CompactRows: false, AlbumColumn: true, PlaysColumn: true, DateColumn: true, RowModern, RowClassic, RowArt),
            InsightsDocked: true, RailRest: HugeRailRest, HeroActionExtra: 0f, AsideDocked: true, InsightsRestOpen: true),
        Tier = WidthTier.Huge, Target = Target, SortLeadsBar = false, SelectionTinted = false, Aside = ClassicDetailAside.Huge,
    };

    /// <summary>Huge 4K (C-Album-Huge4K): Huge's, with the 360 rail and the wider aside.</summary>
    public static ClassicDetailTier Huge4K { get; } = Huge with
    {
        Core = Huge.Core with { RailRest = Huge4KRailRest },
        Aside = ClassicDetailAside.Huge4K,
    };

    /// <summary>The record of window tier <paramref name="tier"/> (Huge 4K reads Huge's: Classic draws no 4K band).</summary>
    public static ClassicDetailTier For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        _ => Huge,
    };

    /// <summary>The record of <paramref name="facts"/> (Huge 4K past <see cref="HugeWideRung"/>); Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicDetailTier Of(in TierFacts facts)
        => facts.Policy is null ? Standard : Policy.IsHugeWide(in facts) ? Huge4K : For(facts.Window);

    /// <summary>The record on screen now, read TRACKED (the running render or effect re-runs on a crossing): for the chrome kit and the row
    /// templates, which run inside the core table's components and carry no view context.</summary>
    public static ClassicDetailTier Now => Of(ThemeHost.Facts.Value.Tiers);

    /// <summary>The policy the structure publishes for the core frame and table.</summary>
    public static DetailTierPolicy Policy { get; } = new(Compact.Core, Small.Core, Standard.Core, Huge.Core,
        RailTitleShort: 28f, RailTitleTall: 40f, DescriptionShort: 3, DescriptionRegular: 6)
    { HugeWide = Huge4K.Core, HugeWideRung = HugeWideRung };
}

/// <summary>The docked aside of a Huge two-column page (contract 1.21): its width per kind, the hairline before it and what its sections measure.</summary>
public sealed record ClassicDetailAside
{
    /// <summary>The album aside (C-Album-Huge 520, -Huge4K 640).</summary>
    public required float AlbumWidth { get; init; }

    /// <summary>A playlist's aside with recommendations (C-Playlist-Huge 440).</summary>
    public float PlaylistWidth { get; init; } = 440f;

    /// <summary>The aside with the insights alone (C-PlaylistFollowed-Huge 400).</summary>
    public float InsightsWidth { get; init; } = 400f;

    /// <summary>The album aside's artist band (270, 360 at 4K).</summary>
    public required float ArtistBand { get; init; }

    /// <summary>The fans' circle (96, 120 at 4K).</summary>
    public required float FanArt { get; init; }

    /// <summary>The fans grid's row gap (12, 16 at 4K).</summary>
    public required float FanRowGap { get; init; }

    /// <summary>Fans a row.</summary>
    public int FanColumns { get; init; } = 4;

    /// <summary>The gap between two fans of a row.</summary>
    public float FanColumnGap { get; init; } = 8f;

    /// <summary>The list column's right inset before the hairline: the rows keep it and never run to the card's edge.</summary>
    public float ListInset { get; init; } = 8f;

    /// <summary>The hairline's margins: 24 over it, 16 each side.</summary>
    public Edges4 HairlineMargin { get; init; } = new(16f, 24f, 16f, 0f);

    /// <summary>The album aside's padding over its first section (24), on the right (the card's edge, 24) and under its last (24).</summary>
    public Edges4 AlbumPad { get; init; } = new(0f, 24f, 24f, 24f);

    /// <summary>A playlist aside's padding (C-Playlist-Huge: 20 over it).</summary>
    public Edges4 PlaylistPad { get; init; } = new(0f, 20f, 24f, 24f);

    /// <summary>The gap between the album aside's sections.</summary>
    public float AlbumGap { get; init; } = 24f;

    /// <summary>The gap between a playlist aside's sections.</summary>
    public float PlaylistGap { get; init; } = 20f;

    /// <summary>The gap inside the insights section (its title over the cards).</summary>
    public float InsightsGap { get; init; } = 12f;

    /// <summary>The hairline and its two margins: what the aside slot adds beside its column.</summary>
    public float HairlineSpan => HairlineMargin.Left + 1f + HairlineMargin.Right;

    /// <summary>The aside column's width for a page of <paramref name="kind"/> (<paramref name="body"/>: the page has a body of its own, not only the insights).</summary>
    public float WidthFor(DetailKind kind, bool body) => kind == DetailKind.Album ? AlbumWidth : body ? PlaylistWidth : InsightsWidth;

    /// <summary>The whole slot: the hairline span and the column.</summary>
    public float SlotWidthFor(DetailKind kind, bool body) => HairlineSpan + WidthFor(kind, body);

    /// <summary>The column's padding for <paramref name="kind"/>.</summary>
    public Edges4 PadFor(DetailKind kind) => kind == DetailKind.Album ? AlbumPad : PlaylistPad;

    /// <summary>The column's content measure (less its right pad).</summary>
    public float ContentWidthFor(DetailKind kind, bool body) => WidthFor(kind, body) - PadFor(kind).Right;

    /// <summary>The width of one fan cell in a <paramref name="content"/>-wide aside.</summary>
    public float FanCellFor(float content) => MathF.Max(0f, (content - (FanColumns - 1) * FanColumnGap) / FanColumns);

    /// <summary>Huge (C-Album-Huge, C-Playlist-Huge, C-PlaylistFollowed-Huge).</summary>
    public static ClassicDetailAside Huge { get; } = new() { AlbumWidth = 520f, ArtistBand = 270f, FanArt = 96f, FanRowGap = 12f };

    /// <summary>Huge 4K (C-Album-Huge4K): the playlist asides keep Huge's (no 4K board draws them).</summary>
    public static ClassicDetailAside Huge4K { get; } = new() { AlbumWidth = 640f, ArtistBand = 360f, FanArt = 120f, FanRowGap = 16f };
}

/// <summary>What Classic's detail views lay out in one window tier.</summary>
public sealed record ClassicDetailTier
{
    /// <summary>What the core frame and table read (<see cref="ThemePolicies.DetailTiers"/>).</summary>
    public required DetailTierLayout Core { get; init; }

    /// <summary>The window tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The edge of the hero's action buttons and the list bar's commands: 40 at Compact, 32 otherwise.</summary>
    public required float Target { get; init; }

    /// <summary>The sort word leads the list bar and the view tools (Filter, Find, ⋯) are glyphs (C-Album-Compact).</summary>
    public required bool SortLeadsBar { get; init; }

    /// <summary>The selection bar is tinted with the accent (C-MultiSelect-Compact).</summary>
    public required bool SelectionTinted { get; init; }

    /// <summary>The docked aside's geometry (Huge: C-Album-Huge, C-Playlist-Huge; Huge 4K: C-Album-Huge4K); null where the tier docks none.</summary>
    public ClassicDetailAside? Aside { get; init; }

    /// <summary>The insights pane docks beside the list.</summary>
    public bool InsightsDocked => Core.InsightsDocked;

    /// <summary>The widest the page's centred row grows: the page measure (<c>R.Space.PageMaxW</c>, 1600) up to Standard, and Huge's own fill
    /// rule, the 2400 the Huge boards fill to (C-Breakpoints: "Huge grids to 2400"), so a Huge page is laid out rather than a Standard page
    /// centred in dead margins.</summary>
    public float PageMaxWidth => Tier >= WidthTier.Huge ? ClassicDetailTiers.HugePageMax : R.Space.PageMaxW;
}
