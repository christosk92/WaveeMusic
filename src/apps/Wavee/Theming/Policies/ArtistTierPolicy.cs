// ── Theming/Policies/ArtistTierPolicy.cs — what a structure decides about the artist and people pages per tier (themes plan 3.2, 3.6; TC6) ──
//
// The artist and profile pages' STATE MACHINES are core (`Artist.PageHost`, `Profile.PageHost`, the library's artist reader, the profile list page): they
// measure their own widths, resolve the hero's sub-tier, the reader's arms and the list's strip arm, latch the metrics the hero, the band, the magazine and
// the wash share, and decode the photograph at the size the metrics name. WHERE those sub-ladders break and how they hold is the structure's: it declares
// them as named ladders of its `TierPolicy` (`ArtistLadders.Hero` / `.Reader` / `.ProfileList`), written with the engine's `TierLadder`, so they hold like
// the window tiers and a crossing found after mount commits on the first frame that is not resized and animates (plan 3.3).
//
// WHAT EACH TIER LAYS OUT is the structure's too: the hero's metrics for a sub-tier in the window tier on screen (its height, gutter, veil, copy measure and
// photograph), the profile hero's, how many columns and rows the Top-tracks chart may take and the profile list's narrowest card. A structure that declares nothing
// gets `Default`: one Wide hero at every width and the chart's two columns (the shell before tiers).

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The ids of the artist and people pages' sub-ladders in a structure's <see cref="TierPolicy.Ladders"/>, and their index maps.</summary>
public static class ArtistLadders
{
    /// <summary>The artist (and profile) hero's sub-tier, measured on the page's own width. The index is the <see cref="ArtistHeroTier"/>
    /// (0 Narrow … 3 Wide).</summary>
    public const string Hero = "artist.hero";

    /// <summary>The library artist reader's arms, measured on the reader's own width: 0 narrow (the 88 cover, the stacked album head), 1 medium (no cover
    /// spine, the stacked band), 2 wide.</summary>
    public const string Reader = "artist.reader";

    /// <summary>The profile list page's arm, measured on its body: 0 narrow (no A–Z strip, the tools stacked), 1 wide.</summary>
    public const string ProfileList = "profile.list";

    /// <summary>The hero sub-tiers.</summary>
    public const int HeroTiers = 4;

    /// <summary>The reader's arms.</summary>
    public const int ReaderArms = 3;

    /// <summary>The reader's narrow arm and its medium arm (<see cref="Reader"/>'s indices).</summary>
    public const int ReaderNarrow = 0, ReaderMedium = 1;

    /// <summary>The hero sub-tier of a hero-ladder index.</summary>
    public static ArtistHeroTier HeroOf(int index) => (ArtistHeroTier)Math.Clamp(index, 0, HeroTiers - 1);

    /// <summary>The structure's ladder <paramref name="id"/>, or one that always answers its top index (the Wide hero, the wide reader, the wide list:
    /// the shell before tiers) where the structure declares none.</summary>
    public static TierLadder Of(TierPolicy? policy, string id)
    {
        if (policy is not null && policy.TryGetLadder(id, out var ladder)) return ladder;
        return id switch { Hero => s_topHero, Reader => s_topReader, _ => s_topList };
    }

    static readonly TierLadder s_topHero = AlwaysTop(HeroTiers - 1);
    static readonly TierLadder s_topReader = AlwaysTop(ReaderArms - 1);
    static readonly TierLadder s_topList = AlwaysTop(1);

    /// <summary>A ladder whose every edge lies below zero, so every usable value is at its top index.</summary>
    static TierLadder AlwaysTop(int edges)
    {
        var e = new TierEdge[edges];
        for (int i = 0; i < edges; i++) e[i] = new TierEdge(-(edges - i));
        return new TierLadder(e);
    }
}

/// <summary>The geometry of one chart row in a tier (contract 1.31, additive): <paramref name="RowHeight"/> the row's height, <paramref name="Art"/> its cover edge,
/// <paramref name="Lead"/> the rank lane's width and <paramref name="ColumnGap"/> the air between two columns (the style splits it over the two inner edges). A
/// zero field is "the style's own" (the shell before tiers and Classic answer <c>default</c>).</summary>
public readonly record struct ArtistChartRowShape(float RowHeight, float Art, float Lead = 0f, float ColumnGap = 0f);

/// <summary>What a structure lays out on the artist and people pages per tier. The core asks it with the tier facts on screen; a structure overrides what it
/// designs. <see cref="Default"/> is the shell before tiers.</summary>
public class ArtistTierPolicy
{
    /// <summary>The shell before tiers: one Wide hero (440 tall, a 36 gutter, the horizontal veil, a 1120 copy measure), the chart's two columns.</summary>
    public static ArtistTierPolicy Default { get; } = new();

    /// <summary>The artist hero's metrics for sub-tier <paramref name="tier"/> of a <paramref name="width"/>-wide page in the tiers <paramref name="facts"/>.</summary>
    public virtual ArtistHeroMetrics Hero(ArtistHeroTier tier, in TierFacts facts, float width)
        => new(ArtistHeroTier.Wide, 440f, 36f, ArtistHeroVeilAxis.Horizontal, 1120f);

    /// <summary>The profile hero's metrics for sub-tier <paramref name="tier"/> of a <paramref name="width"/>-wide page in the tiers <paramref name="facts"/>.</summary>
    public virtual ProfileHeroMetrics ProfileHero(ArtistHeroTier tier, in TierFacts facts, float width)
        => new(ArtistHeroTier.Wide, 260f, 36f, 184f, 96f, 1, 1, 16f, 32f, 1120f);

    /// <summary>The most columns the Top-tracks chart may take in the tiers <paramref name="facts"/> (the chart's own row count still caps them).</summary>
    public virtual int ChartMaxColumns(in TierFacts facts) => 2;

    /// <summary>The rows of one chart column in the tiers <paramref name="facts"/> (contract 1.22): five where the structure declares none.</summary>
    public virtual int ChartRows(in TierFacts facts) => ArtistSections.ChartMaxRows;

    /// <summary>The chart row's geometry in the tiers <paramref name="facts"/> (contract 1.31): <c>default</c> where the structure keeps its style's own.</summary>
    public virtual ArtistChartRowShape ChartRowShape(in TierFacts facts) => default;

    /// <summary>The chart spreads its rows over its columns in reading order (1 2 / 3 4 / 5), even a short list (five tracks stand 3 + 2), instead of filling a
    /// column before the next (contract 1.31). A list that needs more than one page keeps filling columns on its last page.</summary>
    public virtual bool ChartBalanced(in TierFacts facts) => false;

    /// <summary>How far under the top of the page's viewport a pivot click parks a section, and the scroll spy's band edge, in the tiers
    /// <paramref name="facts"/> (optional): the sticky band's height where the band sits INSIDE the viewport (the shell before tiers and Classic: 56), 0
    /// where the structure draws its band outside the viewport (Zune). The artist and the profile pages both read it.</summary>
    public virtual float BandClearance(in TierFacts facts) => Detail.BandLayout.Height;

    /// <summary>The profile list grid's narrowest card in the tiers <paramref name="facts"/>.</summary>
    public virtual float ProfileListMinCell(in TierFacts facts) => 176f;
}
