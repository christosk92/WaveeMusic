// ── Themes/Classic/Structure/Rules/ClassicBrowseRules.cs — the numbers of Classic's Browse views (themes plan 6.1, T10a2; P22) ───────────
//
// What `Entities\Browse.Rules.cs`'s `BrowseLayout` and `Entities\Browse.UI.cs` held beside the pictures they size: the five cell densities'
// heights, the pip, tick and hanging-cover sizes, the corner wash's radial geometry and its hover pair, the Peek cover's rest pose and hover
// deltas, and the first-frame width the Responsive grids share. They are data, not decisions (WVTH003 allows literals only in `Rules\**`); the
// column LADDERS are `Rules\Tiers.Browse.cs`, the DECISIONS (the band taxonomy, the page layout, the tile model) stay core and pure.
//
// `BrowseMastheadMetrics` stays core: the Concert page, the module page and Classic's Home drill page read the same reserve.

using FluentGpu.Dsl;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's Browse views.</summary>
public static class ClassicBrowseRules
{
    /// <summary>First-frame width guess the directory's Responsive grids share before a real measure lands.</summary>
    public const float DirectoryFallbackWidth = 900f;

    /// <summary>Top's pill: the tallest (36), the only one at BodyStrong.</summary>
    public const float WordChipH = 36f;

    /// <summary>For you's pill, one rung down (32).</summary>
    public const float NameChipH = 32f;

    /// <summary>The wrapping pill rows' gap.</summary>
    public static float ChipGap => Spacing.S;

    /// <summary>The 3-DIP left hairline of Bar / Peek, and the section tick of Search's headers.</summary>
    public const float TickW = 3f;

    /// <summary>The section tick's height (Search's rail headers).</summary>
    public const float TickH = 14f;

    /// <summary>The Mood plate's height.</summary>
    public const float BarHeight = 52f;

    /// <summary>The More plate's height.</summary>
    public const float MoreHeight = 88f;

    /// <summary>The hanging cover's edge.</summary>
    public const float Peek = 80f;

    /// <summary>How much of a More plate's width the title column may take beside the hanging cover.</summary>
    public const float PeekCopyFrac = 0.62f;

    /// <summary>The identity pip (a rounded square).</summary>
    public const float Pip = 8f;

    /// <summary>The corner wash's node opacity at rest (the raw colour's alpha rides the node).</summary>
    public const float WashOpacity = 0.20f;

    /// <summary>The corner wash's opacity on hover (the house ~83 ms cross-fade).</summary>
    public const float WashHoverOpacity = 0.34f;

    /// <summary>The wash's transparent stop (the radial gradient fades out by here).</summary>
    public const float WashFadeStop = 0.72f;

    /// <summary>The Mood plate's wash: vertical centre and radii (HomeFoldTile's radial technique).</summary>
    public const float BarWashCenterY = 0.45f, BarWashRadiusX = 0.65f, BarWashRadiusY = 1.40f;

    /// <summary>The More plate's wash: vertical centre and radii.</summary>
    public const float MoreWashCenterY = 0.28f, MoreWashRadiusX = 0.70f, MoreWashRadiusY = 1.30f;

    /// <summary>The cover hangs past both edges by this much (Peek × 0.3 = 24 DIP) and is clipped by the plate.</summary>
    public const float PeekHang = Peek * 0.3f;

    /// <summary>The hanging cover's rest rotation, and its hover deltas (slide and rotate on top of the rest pose).</summary>
    public const float PeekRestRotation = -8f, PeekHoverX = -6f, PeekHoverY = 2f, PeekHoverRotation = 3f;

    /// <summary>The hanging cover's decode edge.</summary>
    public const int PeekDecodePx = 128;

    /// <summary>The shimmer's seed cards in each of its two shelf bands.</summary>
    public const int ShimmerShelfCards = 6;
}
