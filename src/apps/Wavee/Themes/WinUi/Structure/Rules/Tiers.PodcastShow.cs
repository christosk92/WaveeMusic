// ── Themes/WinUi/Structure/Rules/Tiers.PodcastShow.cs — the show page's tier record (podcast pages v2) ────────────────────────────────────────────
//
// Keyed on the WINDOW tier (the concept's table: 1008 and up = list + the 360 pane; 641-1007 one column, a row opens its episode; 640 and under stacked,
// 40 targets, rows open the episode). Huge reads Standard.

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The show page in one WINDOW tier.</summary>
public sealed record WinUiPodcastShowLayout
{
    public required WidthTier Tier { get; init; }
    /// <summary>The list with the 360 details pane beside it (1008 and up); else one column with About as an Expander above the list.</summary>
    public required bool Pane { get; init; }
    /// <summary>Stacked header (640 and under): the cover beside the words, then the full-width button, then the commands; a ComboBox filter.</summary>
    public required bool Stacked { get; init; }
    /// <summary>What a row's click does: select (pane) or open the episode.</summary>
    public required PodcastRowMode RowMode { get; init; }
    /// <summary>Rows carry one line of description (medium).</summary>
    public required bool RowDescription { get; init; }
    public required float Cover { get; init; }
    public required float CoverRadius { get; init; }
    /// <summary>The hero's air above and below, and the gap beside the cover.</summary>
    public required float HeroTop { get; init; }
    public required float HeroBottom { get; init; }
    public required float CoverGap { get; init; }
    /// <summary>The title: Title Large (40/52) or Title (28/36).</summary>
    public required bool TitleLarge { get; init; }
    /// <summary>The share button carries its word.</summary>
    public required bool ShareWord { get; init; }
    /// <summary>A control's height (32; 40 at Compact) and the play button's.</summary>
    public required float Target { get; init; }
    public required float PlayButton { get; init; }
    /// <summary>The search box's width (0 = the search icon button).</summary>
    public required float Search { get; init; }
    /// <summary>The up-next label's progress bar width.</summary>
    public required float UpBar { get; init; }
    /// <summary>A group header's height (36; 44 at Compact) and its jump button's (32; the 40 target at Compact).</summary>
    public required float GroupH { get; init; }
    public required float GroupButtonH { get; init; }
}

/// <summary>What a row's click does in a tier.</summary>
public enum PodcastRowMode : byte { Select, Open }

/// <summary>The show page's tier records.</summary>
public static class WinUiPodcastShowTiers
{
    static readonly WinUiPodcastShowLayout s_compact = new()
    {
        Tier = WidthTier.Compact, Pane = false, Stacked = true, RowMode = PodcastRowMode.Open, RowDescription = false,
        Cover = 96f, CoverRadius = 6f, HeroTop = 20f, HeroBottom = 16f, CoverGap = 16f, TitleLarge = false, ShareWord = false,
        Target = 40f, PlayButton = WinUiPodcastKitRules.PlayButtonCompact, Search = 0f, UpBar = 96f, GroupH = 44f, GroupButtonH = 40f,
    };
    static readonly WinUiPodcastShowLayout s_small = new()
    {
        Tier = WidthTier.Small, Pane = false, Stacked = false, RowMode = PodcastRowMode.Open, RowDescription = true,
        Cover = 136f, CoverRadius = 8f, HeroTop = 32f, HeroBottom = 24f, CoverGap = 24f, TitleLarge = false, ShareWord = false,
        Target = 32f, PlayButton = WinUiPodcastKitRules.PlayButton, Search = 168f, UpBar = 160f, GroupH = 36f, GroupButtonH = 32f,
    };
    static readonly WinUiPodcastShowLayout s_standard = new()
    {
        Tier = WidthTier.Standard, Pane = true, Stacked = false, RowMode = PodcastRowMode.Select, RowDescription = false,
        Cover = 160f, CoverRadius = 8f, HeroTop = 32f, HeroBottom = 24f, CoverGap = 24f, TitleLarge = true, ShareWord = true,
        Target = 32f, PlayButton = WinUiPodcastKitRules.PlayButton, Search = 208f, UpBar = 160f, GroupH = 36f, GroupButtonH = 32f,
    };

    /// <summary>The record for <paramref name="facts"/>' CONTENT tier (Huge reads Standard's), as the episode and audiobook pages read theirs: a docked
    /// queue or lyrics pane narrows the content, and the page gives way to the narrower form instead of squeezing the pane-and-list form into it.
    /// Standard where no frame resolved the tiers.</summary>
    public static WinUiPodcastShowLayout Of(in TierFacts facts) => facts.Policy is null ? s_standard : For(facts.Content);

    /// <summary>The list column's width under which the toolbar's search collapses to its icon button (the filters keep their room): the 1008-1100
    /// windows, where the list beside the 360 pane is about 350 wide.</summary>
    public const float SearchCollapseBelow = 640f;

    /// <summary>The list column's width under which the SelectorBar's four counted filters no longer fit beside the search and sort icons: the
    /// filter becomes the ComboBox (as at Compact).</summary>
    public const float FilterComboBelow = 460f;

    /// <summary>Whether the filter is the ComboBox in a list <paramref name="listWidth"/> wide.</summary>
    public static bool FilterCombo(WinUiPodcastShowLayout lay, float listWidth) => lay.Stacked || (listWidth > 0f && listWidth < FilterComboBelow);

    /// <summary>Whether the search box collapses to its icon in a list <paramref name="listWidth"/> wide.</summary>
    public static bool SearchCollapsed(WinUiPodcastShowLayout lay, float listWidth) => lay.Search <= 0f || (listWidth > 0f && listWidth < SearchCollapseBelow);

    public static WinUiPodcastShowLayout For(WidthTier window) => window switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        _ => s_standard,
    };
}
