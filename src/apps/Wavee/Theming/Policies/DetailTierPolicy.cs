// ── Theming/Policies/DetailTierPolicy.cs — what a structure decides about the detail pages per window tier (themes plan 3.2, 3.6; TC5) ──
//
// The detail frame's STATE MACHINE is core (`Detail.FrameHost`, `Track.TableHost`): it resolves the page MODE (the two-column rail arms or the
// vertical hero arm) from the page's own measured width, the table TIER (which lanes a width admits) from the list column's, and the HEIGHT rung (the
// rail title's cap and the description's clamp) from the window's height. WHERE those ladders break and how they hold is the structure's: it declares
// them as named sub-ladders of its `TierPolicy` (`DetailLadders.Mode` / `.Table` / `.Height`), written with the engine's `TierLadder`, so they hold
// like the window tiers and a crossing found after mount commits on the first frame that is not resized and animates (plan 3.3).
//
// WHAT EACH WINDOW TIER LAYS OUT is the structure's too (`DetailTierLayout`, one per band): the table's form (the Compact row, the lanes a band never
// admits, the row height and art floors), whether the insights pane docks beside the list instead of covering it, the rail's resting width where the
// user never dragged it, the hero action row's extra height (the Compact 40 targets) and the rail title's cap and clamp per height rung. A structure that
// declares nothing gets `Default`: the shell as it shipped (no Compact form, a sheet everywhere, the kinds' own rail widths, 40 / 28 and 6 / 3).

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The ids of the detail frame's sub-ladders in a structure's <see cref="TierPolicy.Ladders"/>, and their index maps.</summary>
public static class DetailLadders
{
    /// <summary>The page MODE ladder, measured on the page's own content column. Index 0 is the vertical hero arm, the top index the widest
    /// two-column arm (index = <c>VerticalMode - mode</c>).</summary>
    public const string Mode = "detail.mode";

    /// <summary>The track table's TIER ladder, measured on the list column. Index 0 is the narrowest tier, the top index tier 0 (the widest:
    /// index = <c>TableTiers - 1 - tier</c>).</summary>
    public const string Table = "detail.table";

    /// <summary>The HEIGHT rungs, measured on the window's height: index 0 short (the description's tight clamp), 1 regular, 2 tall (the rail
    /// title's tall cap).</summary>
    public const string Height = "detail.height";

    /// <summary>The page modes (0 the widest two-column arm … 3 the vertical arm).</summary>
    public const int Modes = 4;

    /// <summary>The table tiers (0 the widest … 6 the narrowest).</summary>
    public const int TableTiers = 7;

    /// <summary>The height rungs.</summary>
    public const int HeightRungs = 3;

    /// <summary>The mode of a mode-ladder index (an index past the ladder is the widest arm).</summary>
    public static int ModeOf(int index) => Math.Clamp(Modes - 1 - index, 0, Modes - 1);

    /// <summary>The mode-ladder index of a mode.</summary>
    public static int IndexOfMode(int mode) => Math.Clamp(Modes - 1 - mode, 0, Modes - 1);

    /// <summary>The table tier of a table-ladder index.</summary>
    public static int TierOf(int index) => Math.Clamp(TableTiers - 1 - index, 0, TableTiers - 1);

    /// <summary>The table-ladder index of a table tier.</summary>
    public static int IndexOfTier(int tier) => Math.Clamp(TableTiers - 1 - tier, 0, TableTiers - 1);

    /// <summary>The structure's ladder <paramref name="id"/>, or one that always answers its top index (the widest arm, tier 0, the tall
    /// rung: the shell before tiers) where the structure declares none.</summary>
    public static TierLadder Of(TierPolicy? policy, string id)
    {
        if (policy is not null && policy.TryGetLadder(id, out var ladder)) return ladder;
        return id switch { Mode => s_topMode, Table => s_topTable, _ => s_topHeight };
    }

    static readonly TierLadder s_topMode = AlwaysTop(Modes - 1);
    static readonly TierLadder s_topTable = AlwaysTop(TableTiers - 1);
    static readonly TierLadder s_topHeight = AlwaysTop(HeightRungs - 1);

    /// <summary>A ladder whose every edge lies below zero, so every usable value is at its top index.</summary>
    static TierLadder AlwaysTop(int edges)
    {
        var e = new TierEdge[edges];
        for (int i = 0; i < edges; i++) e[i] = new TierEdge(-(edges - i));
        return new TierLadder(e);
    }
}

/// <summary>What the track table lays out in one window tier, on top of what its width ladder admits.</summary>
/// <param name="CompactRows">The Compact row form: no column header (its hairline stays), the heart in the trailing lane, no versions chevron lane,
/// the duration folded into a Modern row's subline.</param>
/// <param name="AlbumColumn">The Album lane may show (false: never at this tier, whatever the width).</param>
/// <param name="PlaysColumn">The Plays lane may show.</param>
/// <param name="DateColumn">The Date added lane may show on a list that is not quiet (a quiet list, Liked Songs, keeps its date wherever the width does).</param>
/// <param name="ModernRow">The Modern row height floor (0: the density's), exact in the Compact form.</param>
/// <param name="ClassicRow">The Classic row height floor (0: the density's), exact in the Compact form.</param>
/// <param name="Art">The row art floor (0: the density's), exact in the Compact form.</param>
public sealed record DetailTableTier(bool CompactRows, bool AlbumColumn, bool PlaysColumn, bool DateColumn,
    float ModernRow = 0f, float ClassicRow = 0f, float Art = 0f)
{
    /// <summary>Every lane the width admits, the density's rows.</summary>
    public static DetailTableTier Standard { get; } = new(CompactRows: false, AlbumColumn: true, PlaysColumn: true, DateColumn: true);

    /// <summary>The row height of the skin (<paramref name="classic"/>) given the density's <paramref name="density"/>.</summary>
    public float RowFor(bool classic, float density)
    {
        float floor = classic ? ClassicRow : ModernRow;
        return floor <= 0f ? density : CompactRows ? floor : MathF.Max(floor, density);
    }

    /// <summary>The art edge given the density's <paramref name="density"/>.</summary>
    public float ArtFor(float density) => Art <= 0f ? density : CompactRows ? Art : MathF.Max(Art, density);
}

/// <summary>What the detail pages lay out in one window tier.</summary>
/// <param name="Table">The track table's form and lane caps.</param>
/// <param name="InsightsDocked">The insights pane docks beside the list (a column of its own) instead of covering it as a sheet.</param>
/// <param name="RailRest">The rail's resting width in the widest arm where the user never dragged it (0: the kind's own).</param>
/// <param name="HeroActionExtra">How much taller than the nominal 36 the vertical hero's action row is (the Compact 40 targets: 8).</param>
public sealed record DetailTierLayout(DetailTableTier Table, bool InsightsDocked, float RailRest, float HeroActionExtra)
{
    /// <summary>The shell as it shipped: the density's rows, the sheet, the kinds' own rail widths, the nominal action row.</summary>
    public static DetailTierLayout Standard { get; } = new(DetailTableTier.Standard, InsightsDocked: false, RailRest: 0f, HeroActionExtra: 0f);
}

/// <summary>What a structure decides for the detail pages per window tier and per height rung.</summary>
/// <param name="Compact">The Compact band's layout.</param>
/// <param name="Small">The Small band's.</param>
/// <param name="Standard">The Standard band's (the tuned design).</param>
/// <param name="Huge">The Huge band's (and Huge 4K's, where the structure draws none of its own).</param>
/// <param name="RailTitleShort">The rail title's size cap on a short or regular window.</param>
/// <param name="RailTitleTall">The rail title's size cap on a tall window.</param>
/// <param name="DescriptionShort">The rail description's clamp on a short window.</param>
/// <param name="DescriptionRegular">The rail description's clamp otherwise.</param>
public sealed record DetailTierPolicy(DetailTierLayout Compact, DetailTierLayout Small, DetailTierLayout Standard, DetailTierLayout Huge,
    float RailTitleShort, float RailTitleTall, int DescriptionShort, int DescriptionRegular)
{
    /// <summary>The shell as it shipped: one layout at every band, 28 / 40 and 3 / 6.</summary>
    public static DetailTierPolicy Default { get; } = new(DetailTierLayout.Standard, DetailTierLayout.Standard, DetailTierLayout.Standard,
        DetailTierLayout.Standard, RailTitleShort: 28f, RailTitleTall: 40f, DescriptionShort: 3, DescriptionRegular: 6);

    /// <summary>The layout of window tier <paramref name="tier"/> (Huge 4K reads Huge's).</summary>
    public DetailTierLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        _ => Huge,
    };

    /// <summary>The layout of the facts' window tier; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public DetailTierLayout For(in TierFacts facts) => facts.Policy is null ? Standard : For(facts.Window);

    /// <summary>The rail title's cap at height rung <paramref name="rung"/> (<see cref="DetailLadders.Height"/>).</summary>
    public float RailTitleFor(int rung) => rung >= DetailLadders.HeightRungs - 1 ? RailTitleTall : RailTitleShort;

    /// <summary>The rail description's clamp at height rung <paramref name="rung"/>.</summary>
    public int DescriptionFor(int rung) => rung <= 0 ? DescriptionShort : DescriptionRegular;
}
