// ── Theming/Policies/LibraryTierPolicy.cs — what a structure decides about the library's master-detail columns (themes plan 3.2, 3.6; TC7) ──
//
// The library page's STATE MACHINE is core (`User.LibraryPage`): the selection, the persisted column widths, the crumbs and the drill-in depth. WHETHER the two
// columns sit side by side or fold into a one-column breadcrumb drill-in is the structure's: it declares the LAYOUT ladder as a named sub-ladder of its
// `TierPolicy` (`LibraryLadders.Layout`, index 0 the one-column drill-in, every higher index a split layout), written with the engine's `TierLadder` so it holds
// like the window tiers. The host measures the page's own width through `UseTier`: a first mount resolves in its own frame from the same-frame estimate, and a
// crossing after it commits on the first frame that is not resized and animates (plan 3.3). A structure that declares nothing is always split: the page before tiers.

using FluentGpu.Foundation;

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
