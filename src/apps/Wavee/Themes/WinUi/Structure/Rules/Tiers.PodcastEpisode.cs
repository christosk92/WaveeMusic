// ── Themes/WinUi/Structure/Rules/Tiers.PodcastEpisode.cs — the WinUI episode page's tier records (podcast pages v2, concept "Episode") ─────────────────
//
// The concept's three windows: 1400 (w-l: Standard and up), 900 (w-m: Small) and 500 (w-s: Compact).
//   · Standard: a 256 still beside the words, Title 28/36, labelled Saved and Mark as played; the 400 card (Chapters | About | Comments) beside the
//     Transcript, both 456 tall; six "More like this" cards.
//   · Small: a 208 still; Mark as played folds to its glyph; one column, the Transcript the fourth tab (440 tall); four cards.
//   · Compact: the still (176) over the words, Title 20/28, the split fills its row (40), Saved and Mark as played are 40 glyphs; the transcript drops
//     its timestamps, the caption and the find box; the cards scroll sideways at 136.

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>One tier of the WinUI episode page.</summary>
public sealed record WinUiPodcastEpisodeLayout
{
    public required WidthTier Tier { get; init; }
    /// <summary>The still's width (its height is 16:9) and corner.</summary>
    public required float StillW { get; init; }
    public required float StillRadius { get; init; }
    /// <summary>The still sits above the words (Compact) instead of beside them.</summary>
    public required bool Stacked { get; init; }
    /// <summary>The hero's top and bottom padding and the gap between the still and the words.</summary>
    public required float HeroTop { get; init; }
    public required float HeroBottom { get; init; }
    public required float HeroGap { get; init; }
    /// <summary>The control height (32; 40 at Compact).</summary>
    public required float Target { get; init; }
    /// <summary>Saved carries its word; Mark as played carries its word.</summary>
    public required bool SavedWord { get; init; }
    public required bool MarkWord { get; init; }
    /// <summary>The split fills the action row (Compact).</summary>
    public required bool SplitFills { get; init; }
    /// <summary>Chapters and the transcript side by side (the transcript is no tab).</summary>
    public required bool TwoCards { get; init; }
    public required float CardsH { get; init; }
    /// <summary>The transcript keeps its timestamps, its caption and the find box.</summary>
    public required bool TranscriptChrome { get; init; }
    /// <summary>"More like this": columns (0 = the sideways strip of 136 cards).</summary>
    public required int GridColumns { get; init; }
}

/// <summary>The WinUI episode page's records.</summary>
public static class WinUiPodcastEpisodeTiers
{
    public static WinUiPodcastEpisodeLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, StillW = 176f, StillRadius = 6f, Stacked = true, HeroTop = 20f, HeroBottom = 16f, HeroGap = 16f, Target = 40f,
        SavedWord = false, MarkWord = false, SplitFills = true, TwoCards = false, CardsH = WinUiPodcastEpisodeRules.CardsNarrowH,
        TranscriptChrome = false, GridColumns = 0,
    };

    public static WinUiPodcastEpisodeLayout Small { get; } = new()
    {
        Tier = WidthTier.Small, StillW = 208f, StillRadius = 8f, Stacked = false, HeroTop = 32f, HeroBottom = 20f, HeroGap = 24f, Target = 32f,
        SavedWord = true, MarkWord = false, SplitFills = false, TwoCards = false, CardsH = WinUiPodcastEpisodeRules.CardsNarrowH,
        TranscriptChrome = true, GridColumns = 4,
    };

    public static WinUiPodcastEpisodeLayout Standard { get; } = new()
    {
        Tier = WidthTier.Standard, StillW = 256f, StillRadius = 8f, Stacked = false, HeroTop = 32f, HeroBottom = 20f, HeroGap = 24f, Target = 32f,
        SavedWord = true, MarkWord = true, SplitFills = false, TwoCards = true, CardsH = WinUiPodcastEpisodeRules.CardsH,
        TranscriptChrome = true, GridColumns = 6,
    };

    public static WinUiPodcastEpisodeLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        _ => Standard,
    };

    /// <summary>The record of <paramref name="facts"/> (its content tier); Standard's for a view mounted alone.</summary>
    public static WinUiPodcastEpisodeLayout Of(in TierFacts facts) => facts.Policy is null ? Standard : For(facts.Content);
}
