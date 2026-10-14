// ── Themes/Classic/Structure/Rules/ClassicSearchRules.cs — the numbers of Classic's Search page (themes plan 6.1, T10a2; P22) ──────────────
//
// What `Entities\Search.UI.cs` and `Entities\Search.Rules.cs` held beside the pictures they size: the facet underline and its grow, the eleven placeholder
// pill widths, the top-result card's height, cover and wash, the Songs grid's column floor, the three loading shapes' counts and bars, the Best-matches
// edge fade, the facet row's entrance rise, and the seed genre names the genre section shimmers from. They are data, not decisions (WVTH003 allows literals
// only in `Rules\**`); the DECISIONS stay core and pure (`Search.FacetsFrom`, `ColsFor` and its hysteresis, `FallbackRows`, `PlaylistShelfItems`,
// `FacetGridColumns`, `ShimmerFor`, `Slides`, `ChipRowSkeletal`), and the views call them.
//
// Public, because the chip skeleton's fact (one pill per facet) is pinned by `SearchChipSkeletonPolicyTests` over `ChipSkeletonWidths`.

using FluentGpu.Controls;
using FluentGpu.Dsl;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's Search views.</summary>
public static class ClassicSearchRules
{
    // ── the facet row ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The selected tab's underline height, and the time it takes to grow from its left edge.</summary>
    public const float FacetUnderlineH = 3f, FacetUnderlineMs = 260f;

    /// <summary>The real row rises this far into place when it replaces the skeleton row (the body's own 8-DIP rise).</summary>
    public const float FacetRowEnterDy = 8f;

    /// <summary>The eleven placeholder pill widths — the known facet superset, which wraps to two rows at typical widths.</summary>
    public static readonly float[] ChipSkeletonWidths = [40f, 68f, 76f, 74f, 88f, 68f, 96f, 116f, 76f, 76f, 76f];

    /// <summary>A placeholder pill's bar height (a real tab's label line).</summary>
    public const float ChipSkeletonBarH = 16f;

    // ── the top result ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The top-result card's height (and its wash and shimmer card).</summary>
    public const float HeroHeight = 228f;

    /// <summary>The cover's size, its rest offset and its tilt (cropped off the card's right edge).</summary>
    public const float HeroCoverW = 300f, HeroCoverH = 260f, HeroCoverOffsetX = 40f, HeroCoverOffsetY = -16f, HeroCoverRotation = -2.5f;

    /// <summary>The top result's subtitle line: a 12/16 run in secondary ink.</summary>
    public const float HeroSubtitleSize = 12f, HeroSubtitleLineHeight = 16f;

    /// <summary>A person's cover is a circle: the corner radius that makes the 300×260 slot read round.</summary>
    public const float HeroCoverRoundCorners = 130f;

    /// <summary>The wash behind the copy: the graded accent's alpha at the centre, the stop where it is gone, and the radial's centre and radii.</summary>
    public const float HeroWashAlpha = 0.55f, HeroWashFadeStop = 0.58f, HeroWashCenterX = 0.88f, HeroWashCenterY = 0.40f, HeroWashRadiusX = 1.2f, HeroWashRadiusY = 0.8f;

    // ── the lists and grids ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Songs grid's column floor.</summary>
    public const float SongsColMin = 280f;

    /// <summary>Best matches' edge fade.</summary>
    public const float BestMatchEdgeFade = 16f;

    // ── the three loading shapes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The seed cards' width: inside the rail's 148-188 card range.</summary>
    public const float ShimmerCardW = 168f;

    /// <summary>Ten seed rows at the hit row's own geometry; twelve seed shelf cards, wrapping.</summary>
    public const int ShimmerRows = 10, ShimmerCards = 12;

    /// <summary>The hero shimmer's bars (eyebrow, title, subtitle, button) and its cover placeholder.</summary>
    public const float ShimmerEyebrowW = 90f, ShimmerEyebrowH = 12f, ShimmerTitleW = 260f, ShimmerTitleH = 28f,
                       ShimmerSubtitleW = 170f, ShimmerSubtitleH = 16f, ShimmerButtonW = 100f, ShimmerButtonH = 32f;

    /// <summary>The eight seed names the All tab's genre section shimmers from (W8) — the SAME Link cells, so the bars are sized from real text runs.</summary>
    public static readonly string[] SeedGenres = ["Alternative", "Jazz", "Hip-Hop", "Classical", "R&B", "Electronic", "Indie", "Metal"];

    /// <summary>The rail card's shape: the shelf card with one caption line — what the rail's <c>cardHeight</c> reserves and the shimmer's seed cards draw.</summary>
    public static readonly SurfaceShape RailCard = Shape.Shelf(captionLines: 1, metaLine: false);
}
