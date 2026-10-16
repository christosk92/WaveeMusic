// ── Theming/Policies/LibraryTierPolicy.cs — what a structure decides about the library's master-detail columns (themes plan 3.2, 3.6; TC7) ──
//
// The library page's STATE MACHINE is core (`User.LibraryPage`): the selection, the persisted column widths, the crumbs and the drill-in depth. WHETHER the two
// columns sit side by side or fold into a one-column breadcrumb drill-in is the structure's: it declares the LAYOUT ladder as a named sub-ladder of its
// `TierPolicy` (`LibraryLadders.Layout`, index 0 the one-column drill-in, every higher index a split layout), written with the engine's `TierLadder` so it holds
// like the window tiers. The host measures the page's own width through `UseTier`: a first mount resolves in its own frame from the same-frame estimate, and a
// crossing after it commits on the first frame that is not resized and animates (plan 3.3). A structure that declares nothing is always split: the page before tiers.
//
// THE NAVIGATOR'S DEFAULT WIDTH PER TIER (`LibraryTierPolicy`, contract 1.21). The persisted width (`library.<kind>.leftw`) is the user's: the host reads it as
// the user's only while they set it (`ThemeState.IsCustomized`: stored and not the key's default). Until then the navigator rests at the structure's width
// for the content tier (Classic Huge: 560, the artists' reader list 400, C-Library-Huge / C-LibraryList-Huge), and a crossing re-rests it; the first drag
// makes the width the user's, and no tier moves it again. Contract 1.22 adds the Small rest (Classic 240, C-Library-Small / C-LibraryList-Small) and the
// grid's cell floor there (Classic 96: the Small board's two 96 cells in a 240 navigator; the core's 116 floor would fit one).

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The id of the library page's sub-ladder in a structure's <see cref="TierPolicy.Ladders"/>.</summary>
public static class LibraryLadders
{
    /// <summary>The library's LAYOUT ladder, measured on the page's own width. Index 0 is the one-column drill-in; every higher index keeps the navigator and the
    /// pane side by side.</summary>
    public const string Layout = "library.layout";

    /// <summary>The structure's layout ladder, or one that always answers its top index (split) where the structure declares none.</summary>
    public static TierLadder Of(TierPolicy? policy)
        => policy is not null && policy.TryGetLadder(Layout, out var ladder) ? ladder : s_split;

    /// <summary>Is <paramref name="index"/> the one-column drill-in?</summary>
    public static bool IsSingleColumn(int index) => index <= 0;

    static readonly TierLadder s_split = new([new TierEdge(-1f)]);
}

/// <summary>What a structure decides about the library navigator's width per content tier, while the user has never dragged it (contract 1.21).</summary>
public sealed record LibraryTierPolicy
{
    /// <summary>The navigator's resting width at a Huge content tier (NaN: the key's own default at every tier).</summary>
    public float HugeNavWidth { get; init; } = float.NaN;

    /// <summary>The artists' navigator (the reader's list) at a Huge content tier (NaN: the key's own default).</summary>
    public float HugeReaderNavWidth { get; init; } = float.NaN;

    /// <summary>The navigator's resting width at a Small (or narrower, split) content tier (NaN: the key's own default; contract 1.22).</summary>
    public float SmallNavWidth { get; init; } = float.NaN;

    /// <summary>The artists' navigator at a Small (or narrower, split) content tier (NaN: the key's own default; contract 1.22).</summary>
    public float SmallReaderNavWidth { get; init; } = float.NaN;

    /// <summary>The full grid's cell minimum at size S in a Small content tier (the split page's narrow navigator): the navigator's card ladder starts here
    /// instead of the core's 116 (NaN: the core's ladder; contract 1.22). The S/M/L steps are the core's. A Compact page is one column wide and keeps the core's.</summary>
    public float SmallCardCellMin { get; init; } = float.NaN;

    /// <summary>The page before tiers: the key's own default everywhere.</summary>
    public static LibraryTierPolicy Default { get; } = new();

    /// <summary>The resting width at <paramref name="content"/> for the reader's list (<paramref name="reader"/>) or any other kind; NaN: the key's default.</summary>
    public float NavWidthFor(WidthTier content, bool reader)
        => content >= WidthTier.Huge ? (reader ? HugeReaderNavWidth : HugeNavWidth)
         : content <= WidthTier.Small ? (reader ? SmallReaderNavWidth : SmallNavWidth)
         : float.NaN;

    /// <summary>The full grid's cell minimum at size S for <paramref name="content"/>; NaN: the core's own ladder (contract 1.22).</summary>
    public float CardCellMinFor(WidthTier content) => content == WidthTier.Small ? SmallCardCellMin : float.NaN;
}
