// ── Themes/WinUi/Structure/Rules/WinUiSearchRules.cs — the numbers of WinUI's Search page (themes plan 6.2; P6 W5) ───────────────────────────
//
// What the Search boards draw beside the pictures they size: the facet strip's height, the eleven placeholder pill widths, the top-result card's inset and
// lines, the Songs grid's column floor, the three loading shapes' counts and bars, and the seed genre names the genre section shimmers from. They are data, not
// decisions (WVTH003 allows literals only in `Rules\**`); the DECISIONS stay core and pure (`Search.FacetsFrom`, `ColsFor`, `FallbackRows`, `PlaylistShelfItems`,
// `ShimmerFor`, `Slides`, `ChipRowSkeletal`), and the views call them.
//
// Public, because the chip skeleton's fact (one pill per facet) is pinned by `SearchChipSkeletonPolicyTests` over `ChipSkeletonWidths`.

using FluentGpu.Controls;
using FluentGpu.Dsl;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's Search views.</summary>
public static class WinUiSearchRules
{
    // ── the title and the facet row ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The facet strip's height (W-Search: 40-high SelectorBar items) and the hairline under it.</summary>
    public const float TabH = 40f, TabLineH = 1f;

    /// <summary>The air between the title and the strip (the board's 8), and the strip's step to the body (16).</summary>
    public const float TitleGap = 8f, StripGap = 16f;

    /// <summary>The real row rises this far into place when it replaces the skeleton row (the body's own 8-DIP rise).</summary>
    public const float FacetRowEnterDy = 8f;

    /// <summary>The eleven placeholder pill widths — the known facet superset.</summary>
    public static readonly float[] ChipSkeletonWidths = [40f, 68f, 76f, 74f, 88f, 68f, 96f, 116f, 76f, 76f, 76f];

    /// <summary>A placeholder pill's bar height (a real tab's label line).</summary>
    public const float ChipSkeletonBarH = 16f;

    // ── the top result (W-Search: a card of the card fill, r8, 20 inset) ───────────────────────────────────────────────

    /// <summary>The top-result card's inset and the gaps of its lines: the cover, then the title 12 below it, the kind line, the chips, the buttons.</summary>
    public const float HeroPad = 20f, HeroGap = 8f, HeroTitleGap = 12f;

    /// <summary>The top result's kind line: a 12/16 run in secondary ink.</summary>
    public const float HeroSubtitleSize = 12f, HeroSubtitleLineHeight = 16f;

    /// <summary>A person's cover is a circle: the corner radius that makes a square cover read round (half the largest edge).</summary>
    public const float HeroCoverRoundCorners = 62f;

    /// <summary>The shimmer's card height of the top result (the stood form).</summary>
    public const float HeroHeight = 300f;

    // ── the lists and grids ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Songs grid's column floor.</summary>
    public const float SongsColMin = 280f;

    // ── the three loading shapes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The seed cards' width: inside the rail's card range.</summary>
    public const float ShimmerCardW = 168f;

    /// <summary>Ten seed rows at the hit row's own geometry; twelve seed shelf cards, wrapping.</summary>
    public const int ShimmerRows = 10, ShimmerCards = 12;

    /// <summary>The hero shimmer's bars (eyebrow, title, subtitle, button) and its cover placeholder.</summary>
    public const float ShimmerEyebrowW = 90f, ShimmerEyebrowH = 12f, ShimmerTitleW = 260f, ShimmerTitleH = 28f,
                       ShimmerSubtitleW = 170f, ShimmerSubtitleH = 16f, ShimmerButtonW = 100f, ShimmerButtonH = 32f;

    /// <summary>The eight seed names the All tab's genre section shimmers from — the SAME cells, so the bars are sized from real text runs.</summary>
    public static readonly string[] SeedGenres = ["Alternative", "Jazz", "Hip-Hop", "Classical", "R&B", "Electronic", "Indie", "Metal"];

    /// <summary>The rail card's shape: the shelf card with one caption line — what the rail's <c>cardHeight</c> reserves and the shimmer's seed cards draw.</summary>
    public static readonly SurfaceShape RailCard = Shape.Shelf(captionLines: 1, metaLine: false);
}
