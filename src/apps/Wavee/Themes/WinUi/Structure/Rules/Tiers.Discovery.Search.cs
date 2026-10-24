// ── Themes/WinUi/Structure/Rules/Tiers.Discovery.Search.cs — WinUI's Search results page per content tier (themes plan 3.6, 6.2; P6 W5) ──────────
//
// ONE record per CONTENT tier, read by the page and its body through `WinUiSearchTiers.Of(ctx.Tier())`. Boards W-Search, W-Search-Compact / -Small / -Huge,
// W-NoResults, W-NoResults-Compact.
//
//   tier      page inset     title             facet row                         top result                           Songs / rails
//   Compact   16             28/36 echo        one 40 strip, scrolls, faded      stacked: a row card (72 cover)       rows full width; shelves 2 columns
//   Small     24             28/36 echo        one 40 strip, scrolls, faded      stacked: a row card (72 cover)       rows; shelves 4 columns
//   Standard  36             Title Large       one 40 strip, hairline under      360 card BESIDE the Songs from 900   rows; shelves 6 columns
//   Huge      48             Title Large       the same                          520 card beside the Songs            rows; shelves 10 columns
//
// THE TOP RESULT STACKS BELOW 900 (W-Tiers: "its top result stacks below 900"): the named content rung `BesideRung` (committing at the line on widening and
// held 24 on narrowing, like every WinUI edge) says whether the card stands beside the Songs; the record reads it from the facts. Public, so tests pin it.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>What WinUI's Search page is in one content tier.</summary>
public sealed record WinUiSearchLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The title is Title Large 40/52 (Standard, Huge); false is Title 28/36 (Compact, Small).</summary>
    public required bool TitleLarge { get; init; }

    /// <summary>The page's side inset: the frame's (16 / 24 / 36 / 48).</summary>
    public required float Inset { get; init; }

    /// <summary>The top result stands beside the Songs (content wide enough: the rung); false stacks them.</summary>
    public required bool Beside { get; init; }

    /// <summary>The top result card's width when beside.</summary>
    public required float HeroW { get; init; }

    /// <summary>The cover's edge on the top result: 124 (the stood card), 72 (the stacked row card).</summary>
    public float HeroCover => Beside ? BesideCover : StackedCover;

    /// <summary>The Songs grid's columns (the Songs facet).</summary>
    public required WinUiGridBand Songs { get; init; }

    /// <summary>The playlist and artist rails' shelf.</summary>
    public required WinUiShelfBand Rail { get; init; }

    /// <summary>The genre tiles: the browse record's tile columns (the same colour tiles as the directory).</summary>
    public required WinUiBrowseLayout Browse { get; init; }

    /// <summary>The Albums and Playlists facet grid's columns (a floor-fit at the grid's own width).</summary>
    public required WinUiGridBand FacetGrid { get; init; }

    /// <summary>A hit row's art edge: 40 (W-Search).</summary>
    public required float RowArt { get; init; }

    /// <summary>A hit row's floor height: 48 at Standard and Huge, 56 below.</summary>
    public required float RowMinH { get; init; }

    /// <summary>The facet strip's height (40).</summary>
    public float TabStripH => WinUiSearchRules.TabH;

    /// <summary>A hit row's shape at the tier: the board's 40 art in the tier's floor; the large lead row keeps the media row's own.</summary>
    public SurfaceShape RowShape(SurfaceShape shape) => shape.IsLargeRow ? shape : shape with { ArtEdge = RowArt, MinHeight = RowMinH };

    /// <summary>The Albums / Playlists grid's columns at <paramref name="width"/>: the core rule's floor-fit held to the tier's cap.</summary>
    public int FacetGridColumns(float width) => FacetGrid.Columns(width, Search.FacetGridGap);

    /// <summary>The stood top result's cover and the stacked row card's.</summary>
    public const float BesideCover = 124f, StackedCover = 72f;
}

/// <summary>WinUI's Search records.</summary>
public static class WinUiSearchTiers
{
    /// <summary>The named content rung: the top result stands beside the Songs from this width (committing at the line on widening, held 24 on narrowing).</summary>
    public const string BesideRung = "winui.discovery.search.beside";

    /// <summary>The best matches list's key: ONE at every tier.</summary>
    public const string BestMatchesKey = "search:best-matches";

    /// <summary>The content width the top result stacks below (W-Tiers: 900).</summary>
    public const float BesideFrom = 900f;

    /// <summary>The record for <paramref name="facts"/>' content tier; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiSearchLayout Of(in TierFacts facts)
    {
        var tier = facts.Policy is null ? WidthTier.Standard : facts.Content;
        bool beside = facts.Policy is null || (facts.Policy.IndexOfRung(BesideRung) >= 0 && facts.Rung(BesideRung));
        return For(tier, WinUiTiers.InsetOf(in facts), beside);
    }

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's) at the page inset <paramref name="inset"/>.</summary>
    public static WinUiSearchLayout For(WidthTier content, float inset, bool beside)
    {
        var tier = (WidthTier)Math.Min((int)content, (int)WidthTier.Huge);
        var browse = WinUiBrowseTiers.For(tier);
        return tier switch
        {
            WidthTier.Compact => new WinUiSearchLayout
            {
                Tier = tier, TitleLarge = false, Inset = inset, Beside = false, HeroW = float.NaN,
                Songs = new WinUiGridBand(WinUiSearchRules.SongsColMin, 1), Rail = browse.Shelf, Browse = browse,
                FacetGrid = new WinUiGridBand(140f, 2), RowArt = 40f, RowMinH = 56f,
            },
            WidthTier.Small => new WinUiSearchLayout
            {
                Tier = tier, TitleLarge = false, Inset = inset, Beside = beside, HeroW = HeroBesideW,
                Songs = new WinUiGridBand(WinUiSearchRules.SongsColMin, 2), Rail = browse.Shelf, Browse = browse,
                FacetGrid = new WinUiGridBand(Search.FacetGridMinCol, int.MaxValue), RowArt = 40f, RowMinH = 56f,
            },
            WidthTier.Standard => new WinUiSearchLayout
            {
                Tier = tier, TitleLarge = true, Inset = inset, Beside = beside, HeroW = HeroBesideW,
                Songs = new WinUiGridBand(WinUiSearchRules.SongsColMin, int.MaxValue), Rail = browse.Shelf, Browse = browse,
                FacetGrid = new WinUiGridBand(Search.FacetGridMinCol, int.MaxValue), RowArt = 40f, RowMinH = 48f,
            },
            _ => new WinUiSearchLayout
            {
                Tier = tier, TitleLarge = true, Inset = inset, Beside = beside, HeroW = HugeHeroW,
                Songs = new WinUiGridBand(WinUiSearchRules.SongsColMin, int.MaxValue), Rail = browse.Shelf, Browse = browse,
                FacetGrid = new WinUiGridBand(Search.FacetGridMinCol, 10), RowArt = 40f, RowMinH = 48f,
            },
        };
    }

    /// <summary>The stood top result's width: 360 at Standard (W-Search), 520 at Huge (W-Search-Huge).</summary>
    public const float HeroBesideW = 360f, HugeHeroW = 520f;

    /// <summary>The gap between the top result and the Songs beside it, and between the page's sections (24).</summary>
    public const float BesideGap = 24f, SectionGap = 24f;

    /// <summary>A section header's step to its body (12).</summary>
    public const float HeaderGap = 12f;

    /// <summary>The most Best matches rows the list draws (W-Search: five or six).</summary>
    public const int BestRows = 6;
}
