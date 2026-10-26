// ── Themes/Classic/Structure/Rules/Tiers.Search.cs — Classic's Search results page per content tier (themes plan 3.6, TC4) ─────────────────────────
//
// ONE record per CONTENT tier, read by the page and its body through `ClassicSearchTiers.Of(ctx.Tier())`. Boards: `C-Search` (Standard, the tuned design: its
// numbers are the ones the page held before tiers), `C-Search-Compact`, `C-Search-Small`, `C-Search-Huge`.
//
//   tier      page inset            facet row                        hit rows                    songs grid        top result         genres / related
//   Compact   rows flush, echo 16   one 44 high strip, no wrap, fade  44 art in a 60 row, flush  1 column         228 card           2 columns
//   Small     gutter 32             one 36 high strip, no wrap, fade  tuned (48 in 64)            up to 2 columns   228 card           2 columns
//   Standard  gutter 36             wrapping row (tuned)             tuned                       fit 280           228 card           3 columns
//   Huge      gutter 36             wrapping row                     tuned                       fit 280           300 x 700 beside   6 columns beside Related
//                                                                                                                  Best matches (3)
//
// THE TOP RESULT'S FORM. Compact to Standard keep the tuned card (the eyebrow, the copy left, the cover cropped off the right at -2.5 degrees). Huge
// (C-Search-Huge) draws the AVATAR form: a "Top result" header over the card, and in the 300-tall card the hit's 128 art at the top left (round for a
// person), the 40/52 title under it, the kind line, and the actions at the card's bottom right, all inset 28.
// THE ARTIST RAIL. Huge alone draws an "Artists" rail of round cards between the playlist rail and Genres (10 columns, the playlist rail's band);
// the region reveals and collapses on a crossing into or out of Huge (a size reveal: the sections below ride its edge).
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's Search page is in one content tier.</summary>
public sealed record ClassicSearchLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The page's side inset: the page gutter (16 / 32 / 36 / 36).</summary>
    public required float Gutter { get; init; }

    /// <summary>How far the best matches' pager stands in from the shelf's end (Compact: 4, where the shelf's end lies 8 from the window's and the page's
    /// 12 scroll bar lane would take the next chevron's last 4 DIP, and so its touch floor; 0 elsewhere).</summary>
    public float PagerEndInset { get; init; }

    /// <summary>The hit rows run edge to edge (Compact): the body takes no side inset and each row carries its own 16, so the hover plate reaches the card's edges.</summary>
    public required bool Flush { get; init; }

    /// <summary>The facet tabs wrap onto further rows (Standard, Huge); false is one strip that scrolls, its trailing edge faded (Compact, Small).</summary>
    public required bool TabsWrap { get; init; }

    /// <summary>A facet tab's height (the Compact board's 44 target; 0 is the tuned tab: 8 over, 4 under).</summary>
    public required float TabH { get; init; }

    /// <summary>A hit row's art edge and its floor, where the tier differs from the media row's own (Compact: 44 in 60); 0 keeps the shape's.</summary>
    public required float RowArt { get; init; }

    /// <summary>A hit row's floor height; 0 keeps the shape's.</summary>
    public required float RowMinH { get; init; }

    /// <summary>The Songs grid's columns.</summary>
    public required ClassicGridBand Songs { get; init; }

    /// <summary>The top-result card's height (228; Huge 300).</summary>
    public required float HeroH { get; init; }

    /// <summary>The top result sits BESIDE Best matches at this width (Huge: 700); NaN stacks them.</summary>
    public required float HeroBesideW { get; init; }

    /// <summary>The playlist rail's shelf.</summary>
    public required ClassicShelfBand Rail { get; init; }

    /// <summary>The artist rail's shelf (Huge: <c>C-Search-Huge</c>'s ten round cards); null draws no artist rail.</summary>
    public required ClassicShelfBand? ArtistRail { get; init; }

    /// <summary>How the top result is drawn.</summary>
    public required ClassicSearchHeroForm HeroForm { get; init; }

    /// <summary>The genre links' columns.</summary>
    public required ClassicGridBand Genres { get; init; }

    /// <summary>Genres (2 parts) and Related searches (1 part) share a row (Huge).</summary>
    public required bool GenresBeside { get; init; }

    /// <summary>The Albums and Playlists facet grid's columns (a floor-fit at the grid's own width).</summary>
    public required ClassicGridBand FacetGrid { get; init; }

    /// <summary>The body's side inset: the page gutter, less the 8 a hit row's own plate padding adds where the rows run edge to edge (Compact), so a row's
    /// text still starts on the gutter and its hover plate reaches nearly to the card's edge.</summary>
    public float BodyInset => Flush ? MathF.Max(0f, Gutter - Spacing.S) : Gutter;

    /// <summary>The tab strip's height: the tier's, or the tuned tab's own 36.</summary>
    public float TabStripH => TabH > 0f ? TabH : ClassicSearchRules.TabStripH;

    /// <summary>A hit row's shape at the tier: Compact's 44 art in a 60 row; the media row's own elsewhere (and always for the large lead row).</summary>
    public SurfaceShape RowShape(SurfaceShape shape) => RowArt > 0f && !shape.IsLargeRow ? shape with { ArtEdge = RowArt, MinHeight = RowMinH } : shape;

    /// <summary>A Best matches row's height in its shelf: the tier's row floor (Compact's 60, <c>C-Search-Compact</c>), else the tuned 64.</summary>
    public float BestMatchRowH => RowMinH > 0f ? RowMinH : Search.BestMatchRowH;

    /// <summary>Best matches' rows per page at <paramref name="columns"/> columns: square (two at two, three at three), and three at one (never a one-cell
    /// pager). The same at every tier; a live prop of the mounted shelf, so a crossing that changes the column count re-pages the same shelf.</summary>
    public int BestMatchRowsFor(int columns) => Search.RowsFor(columns);

    /// <summary>The Albums / Playlists grid's columns at <paramref name="width"/>: the core rule's floor-fit held to the tier's cap.</summary>
    public int FacetGridColumns(float width) => FacetGrid.Columns(width, Search.FacetGridGap);
}

/// <summary>Classic's Search records.</summary>
public static class ClassicSearchTiers
{
    /// <summary>The record for <paramref name="facts"/>' content tier; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicSearchLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content, ClassicTiers.PageOf(facts).Gutter);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's) at the page gutter <paramref name="gutter"/>.</summary>
    public static ClassicSearchLayout For(WidthTier content, float gutter) => (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
    {
        WidthTier.Compact => new ClassicSearchLayout
        {
            Tier = WidthTier.Compact, Gutter = gutter, Flush = true, TabsWrap = false, TabH = 44f, RowArt = 44f, RowMinH = 60f, PagerEndInset = 4f,
            Songs = new ClassicGridBand(ClassicSearchRules.SongsColMin, 1), HeroH = ClassicSearchRules.HeroHeight, HeroBesideW = float.NaN,
            HeroForm = ClassicSearchHeroForm.Card, ArtistRail = null,
            Rail = new ClassicShelfBand(148f, 188f, 0, FixedCardW: 164f, Gap: 0f, Pager: false),
            Genres = ClassicGridBand.Fixed(2), GenresBeside = false,
            FacetGrid = new ClassicGridBand(140f, 2),
        },
        WidthTier.Small => new ClassicSearchLayout
        {
            Tier = WidthTier.Small, Gutter = gutter, Flush = false, TabsWrap = false, TabH = 36f, RowArt = 0f, RowMinH = 0f,
            Songs = new ClassicGridBand(ClassicSearchRules.SongsColMin, 2), HeroH = ClassicSearchRules.HeroHeight, HeroBesideW = float.NaN,
            HeroForm = ClassicSearchHeroForm.Card, ArtistRail = null,
            Rail = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
            Genres = ClassicGridBand.Fixed(2), GenresBeside = false,
            FacetGrid = new ClassicGridBand(Search.FacetGridMinCol, int.MaxValue),
        },
        WidthTier.Standard => new ClassicSearchLayout
        {
            Tier = WidthTier.Standard, Gutter = gutter, Flush = false, TabsWrap = true, TabH = 0f, RowArt = 0f, RowMinH = 0f,
            Songs = new ClassicGridBand(ClassicSearchRules.SongsColMin, int.MaxValue), HeroH = ClassicSearchRules.HeroHeight, HeroBesideW = float.NaN,
            HeroForm = ClassicSearchHeroForm.Card, ArtistRail = null,
            Rail = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
            Genres = ClassicGridBand.Fixed(3), GenresBeside = false,
            FacetGrid = new ClassicGridBand(Search.FacetGridMinCol, int.MaxValue),
        },
        _ => new ClassicSearchLayout
        {
            Tier = WidthTier.Huge, Gutter = gutter, Flush = false, TabsWrap = true, TabH = 0f, RowArt = 0f, RowMinH = 0f,
            Songs = new ClassicGridBand(ClassicSearchRules.SongsColMin, int.MaxValue), HeroH = HugeHeroH, HeroBesideW = HugeHeroW,
            HeroForm = ClassicSearchHeroForm.Avatar,
            Rail = new ClassicShelfBand(R.Space.ShelfCardMin, 9999f, 10, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
            ArtistRail = new ClassicShelfBand(R.Space.ShelfCardMin, 9999f, 10, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
            Genres = ClassicGridBand.Fixed(6), GenresBeside = true,
            FacetGrid = new ClassicGridBand(Search.FacetGridMinCol, 10),
        },
    };

    /// <summary>The Best matches shelf's key: ONE at every tier, so a crossing re-fits the same shelf and its rows (the mounted crossing tests read it).</summary>
    public const string BestMatchesKey = "search:best-matches";

    /// <summary>Huge's top-result card: 700 wide and 300 tall (<c>C-Search-Huge</c>), Best matches filling the rest of its row.</summary>
    public const float HugeHeroW = 700f, HugeHeroH = 300f;

    /// <summary>The gap between the top result and Best matches beside it, and between Genres and Related searches (<c>C-Search-Huge</c>: 32).</summary>
    public const float BesideGap = 32f;

    /// <summary>The avatar form (<c>C-Search-Huge</c>): the art's edge, the card's inset on every side, the step from the art to the title, and the gap
    /// between the card's lines.</summary>
    public const float AvatarArt = 128f, AvatarPad = 28f, AvatarTitleGap = 10f, AvatarLineGap = 6f;

    /// <summary>The avatar form's kind line: 14/20, secondary ink (<c>C-Search-Huge</c>).</summary>
    public const float AvatarMetaSize = 14f, AvatarMetaLineHeight = 20f;

    /// <summary>The step from the "Top result" header to the card under it (<c>C-Search-Huge</c>: 10).</summary>
    public const float AvatarHeaderGap = 10f;

    /// <summary>The header's line over the avatar card: the rail header's 20/28 (the shimmer reserves it).</summary>
    public const float AvatarHeaderH = 28f;
}

/// <summary>How a tier draws the Search top result.</summary>
public enum ClassicSearchHeroForm : byte
{
    /// <summary>The tuned card: the eyebrow and the copy left, the cover cropped off the right edge at -2.5 degrees (Compact to Standard).</summary>
    Card,
    /// <summary>A "Top result" header over the card; in it the hit's art at the top left (round for a person), the title, the kind line, the actions at
    /// the bottom right (Huge: <c>C-Search-Huge</c>).</summary>
    Avatar,
}
