// ── Themes/Classic/Structure/Rules/ClassicProfileListRules.cs — the numbers of Classic's profile list page (themes plan 6.1, T10c2) ───────────
//
// What `Entities\Profile.Lists.Page.cs` held beside the shimmer it draws: the tools' bones, the Follow bone and how many rows of seed cards stand in for the list. The grid fit
// (columns, the strip and the stacked-tools thresholds, the card row and the action line) is the page's decision and stays core (`ProfileGridFit`); the numbers here are data
// (WVTH003 allows literals only in `Rules\**`).

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's profile list page.</summary>
public static class ClassicProfileListRules
{
    /// <summary>One chip's bone (width; the height is the chip's own).</summary>
    public const float ChipBoneW = 96f;

    /// <summary>The find box's bone height.</summary>
    public const float FindBoneH = 32f;

    /// <summary>The Follow toggle's bone.</summary>
    public const float FollowBoneW = 96f, FollowBoneH = 32f;

    /// <summary>The shimmer's rows of seed cards, and the columns it assumes before the first measure.</summary>
    public const int ShimmerRows = 2, ShimmerFallbackCols = 4;

    /// <summary>The chips the Following facet shows (All, Artists, People): one bone each.</summary>
    public const int ChipBones = 3;
}
