// ── Themes/Classic/Structure/Rules/ClassicAlbumLayout.cs — the numbers of Classic's album views (themes plan 6.1, T9b) ──────────────────────────────
//
// What `Entities\Album.UI.cs` and `Entities\Album.Page.cs` held as literals and file-level constants beside the pictures they size, moved with those pictures
// (`Views\Detail\ClassicAlbumArtist.cs`, `ClassicAlbumRelease.cs`, `ClassicAlbumTrailing.cs`): the every-artist flyout's rows, the countdown's ring, the
// release facts' label column, the music-video hero's thumb (200 × 116, its FAB the Video shape's own), the shelf's card range, the merch row, and the
// skeleton's bones. They are data, not decisions (WVTH003 allows literals only in `Rules\**`).
//
// WHAT IS NOT HERE, AND WHY. The skeleton's RESERVES (`Album.PageRules.Skel*`, `SkeletonHeight`, `SkeletonShape`) and the caps (`StackCap`, `FansCap`,
// `VideoCap`, `FanCardW`) are decisions the core host and the loaded band must agree on, so they stay core, pure and pinned
// (`AlbumPageRulesTests` pins them against this file's numbers and the shared surface's presets).

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's album views.</summary>
public static class ClassicAlbumLayout
{
    // ── the every-artist flyout (W16): 44-DIP rows in a 280 × 360 flyout ──

    /// <summary>The gap between the flyout's rows.</summary>
    public const float FlyoutListGap = 2f;

    /// <summary>A row's corner radius.</summary>
    public const float FlyoutRowRadius = 6f;

    /// <summary>A row's portrait edge.</summary>
    public const float FlyoutPortrait = 32f;

    /// <summary>A row's name: size and line height.</summary>
    public const float FlyoutNameSize = 14f, FlyoutNameLine = 20f;

    // ── the countdown card ──

    /// <summary>"Out now": size.</summary>
    public const float OutNowSize = 15f;

    /// <summary>The ring's edge.</summary>
    public const float CountdownRing = 34f;

    // ── about this release ──

    /// <summary>The ℗ / © notes' gap.</summary>
    public const float NotesGap = 2f;

    /// <summary>The facts list's label column: wide enough for "Released" / "Label" in the caption rung, so the values start on one line.</summary>
    public const float FactLabelW = 76f;

    /// <summary>The label's top nudge (it sits on the value's first baseline).</summary>
    public const float FactLabelNudge = 2f;

    /// <summary>The Other versions picker's vertical padding.</summary>
    public const float VersionsPadY = 2f;

    // ── the unresolvable prerelease (W18) ──

    /// <summary>The line's padding: sides, then top and bottom.</summary>
    public const float UnresolvedPadX = 16f, UnresolvedPadY = 24f;

    /// <summary>The line's size and line height.</summary>
    public const float UnresolvedSize = 14f, UnresolvedLine = 20f;

    // ── the music-video section ──

    /// <summary>The HERO arm's thumbnail is 0.2.9's exact 200×116.</summary>
    public const float HeroThumbW = 200f, HeroThumbH = 116f;

    /// <summary>The FAB over the hero's thumbnail is the Video shape's own: the hero's badge and a shelf card's are the same object.</summary>
    public static float HeroFab => Shape.Video.Fab;

    /// <summary>The hero's subtitle: size and line height.</summary>
    public const float VideoMetaSize = 12f, VideoMetaLine = 16f;

    /// <summary>The SHELF arm fits its own 16:9 thumb to whatever card width the shelf hands it, between these two (16:9 cards want more room than the
    /// 148–188 square-card shelf range).</summary>
    public const float VideoCardMinW = 200f, VideoCardMaxW = 280f;

    /// <summary>A video card's cover aspect.</summary>
    public const float VideoAspect = 16f / 9f;

    // ── the list sections' rows ──

    /// <summary>A row whose slot is gone, and a merch listing: the plated row's 64 floor.</summary>
    public const float PlaceholderRowH = 64f, MerchRowH = 64f;

    /// <summary>The merch row's art edge.</summary>
    public const float MerchArt = 48f;

    /// <summary>The merch price: size and line height.</summary>
    public const float MerchPriceSize = 12f, MerchPriceLine = 16f;

    // ── the skeleton's bones ──

    /// <summary>A bone's corner radius.</summary>
    public const float BoneRadius = 4f;

    /// <summary>The single-video hero's three bars: width and height of the eyebrow, the title and the meta.</summary>
    public const float SkelEyebrowW = 96f, SkelEyebrowH = 12f, SkelTitleW = 200f, SkelTitleH = 16f, SkelMetaW = 120f;

    /// <summary>A section header's bone width (its height is the rule's <c>SkelHeaderBarH</c>).</summary>
    public const float SkelHeaderBarW = 160f;

    /// <summary>The About card's name and bio bars, and its Follow cell's bone.</summary>
    public const float SkelAboutNameW = 180f, SkelAboutNameH = 20f, SkelAboutBioW = 320f;

    /// <summary>The About card's Follow cell: width, height and corner radius.</summary>
    public const float SkelFollowW = 96f, SkelFollowH = 32f, SkelFollowRadius = 16f;
}
