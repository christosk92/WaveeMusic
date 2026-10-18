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

/// <summary>The gaps of the navigator's card grid: <see cref="Column"/> between the cells of a row, <see cref="Row"/> between rows (contract addition, after 1.22).</summary>
public readonly record struct LibraryGridGaps(float Column, float Row)
{
    /// <summary>The core's grid: the same 8 both ways (<see cref="LibraryNavMount.GridGap"/>).</summary>
    public static LibraryGridGaps Core => new(LibraryNavMount.GridGap, LibraryNavMount.GridGap);

    /// <summary>True when both gaps are usable (finite, not negative).</summary>
    public bool IsValid => float.IsFinite(Column) && Column >= 0f && float.IsFinite(Row) && Row >= 0f;
}

/// <summary>What a structure decides about the library navigator's width per content tier, while the user has never dragged it (contract 1.21).</summary>
public sealed record LibraryTierPolicy
{
    /// <summary>The structure draws the ALBUMS library as the board does: an "all artists" column, then the picked artist's albums, then the album's songs. The host then groups
    /// the albums by their first billed artist (<see cref="LibraryPageProps.Groups"/>) on the split page and narrows the navigator to the picked artist; false (the default,
    /// Classic's) leaves the albums navigator exactly as it was and publishes no groups. Contract addition after 1.22.</summary>
    public bool GroupAlbumsByArtist { get; init; }

    /// <summary>With <see cref="GroupAlbumsByArtist"/>: the narrowest content tier that draws the album's songs pane beside the albums. Below it the pane is not drawn, so the host opens
    /// an album's page when it is picked (default Compact: the pane is drawn at every split tier). Contract addition after 1.33.</summary>
    public WidthTier GroupPaneFrom { get; init; } = WidthTier.Compact;

    /// <summary>The structure draws the PODCASTS library as the board does: "your episodes" as a pinned first row of the shows list, the episodes feed (or the picked show) beside it, and no
    /// mode words in the head. The host then opens on Your Episodes until a show has been picked, and a show picked in the list switches back to its pane; false (the default,
    /// Classic's) leaves the two mode words and their swap as they were. Contract addition after 1.34.</summary>
    public bool PodcastsEpisodesFirst { get; init; }

    /// <summary>The structure draws the PLAYLISTS library as the board does: the rootlist grouped (made by you, made for you, followed) beside the picked playlist's pane (cover, verbs, songs).
    /// False (the default, Classic's) keeps the flat list page. Contract addition after 1.34.</summary>
    public bool PlaylistsMasterDetail { get; init; }

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

    /// <summary>The navigator's card-grid gaps per content tier (null: the core's 8 and 8; contract addition after 1.22). They are LIVE: a crossing re-fits the mounted grid in place (the
    /// mount key never carries them), keeping the anchor row.</summary>
    public LibraryGridGaps? CompactGridGaps { get; init; }

    /// <inheritdoc cref="CompactGridGaps"/>
    public LibraryGridGaps? SmallGridGaps { get; init; }

    /// <inheritdoc cref="CompactGridGaps"/>
    public LibraryGridGaps? StandardGridGaps { get; init; }

    /// <inheritdoc cref="CompactGridGaps"/>
    public LibraryGridGaps? HugeGridGaps { get; init; }

    /// <summary>The grid's gaps at <paramref name="content"/> (Huge 4K reads Huge's); the core's where the structure says nothing for the tier.</summary>
    public LibraryGridGaps GridGapsFor(WidthTier content)
    {
        var gaps = content switch
        {
            WidthTier.Compact => CompactGridGaps,
            WidthTier.Small => SmallGridGaps,
            WidthTier.Standard => StandardGridGaps,
            _ => HugeGridGaps,
        };
        return gaps is { IsValid: true } g ? g : LibraryGridGaps.Core;
    }

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
