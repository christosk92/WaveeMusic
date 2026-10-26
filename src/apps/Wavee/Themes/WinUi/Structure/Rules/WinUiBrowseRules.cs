// ── Themes/WinUi/Structure/Rules/WinUiBrowseRules.cs — the numbers of WinUI's Browse views (themes plan 6.2; P6 W5) ─────────────────────────
//
// What the Browse boards draw beside the pictures they size: the colour tiles' radius and the Top tile's icon box, the For you chip's dot, the band heading, the
// subtitle under the page title. They are data, not decisions (WVTH003 allows literals only in `Rules\**`); the column RECORDS are `Rules\Tiers.Discovery.Browse.cs`,
// the DECISIONS (the band taxonomy, the page layout, the tile model) stay core and pure. Public, so the tests pin them.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's Browse views.</summary>
public static class WinUiBrowseRules
{
    /// <summary>The Top tile's icon box: a black wash at 25 % (W-Browse), r4; its title is the Subtitle rung 20/28 semibold on the tile's foot.</summary>
    public static ColorF DiscFill => ColorF.FromRgba(0, 0, 0) with { A = 0.25f };

    /// <summary>The Top tile's gap between the icon box and its title (8; 12 at Huge).</summary>
    public const float TopDiscGap = 8f, TopDiscGapHuge = 12f;

    /// <summary>The For you chip: the identity dot's edge (16, a disc), the gap to the word, the chip's side padding.</summary>
    public const float ChipDot = 16f, ChipGap = 12f, ChipPad = 12f;

    /// <summary>A tile's title: 16/20 semibold (the genre, mood and "More" tiles).</summary>
    public const float TileTitleSize = 16f, TileTitleLine = 20f;

    /// <summary>The "Explore all categories" cell at Huge: the outlined 56-high cell closing For you's grid.</summary>
    public const float ExploreH = 56f;

    /// <summary>The subtitle under the page title: body 14/20 secondary, 4 under the title (the head's slot), 8 above the first band.</summary>
    public const float SubtitleLine = 20f, SubtitleGap = 4f;

    /// <summary>The shimmer's seed cards in each of its two shelf bands.</summary>
    public const int ShimmerShelfCards = 6;

    /// <summary>The tick of Search's headers and its height (the section tick kept by the Search page).</summary>
    public const float TickW = 3f, TickH = 14f;
}
