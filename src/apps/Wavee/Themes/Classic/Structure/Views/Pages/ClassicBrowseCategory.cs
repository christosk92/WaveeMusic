// ── Themes/Classic/Structure/Views/Pages/ClassicBrowseCategory.cs — Classic's `Pages.BrowseCategory` (themes plan 6.1, T10a2) ────────────────
//
// From `Entities\Browse.Page.cs` (`CategoryPage.Render`, `Body`, `ShelvesBody`, `Framed`, `Shelf`, `CategoryBlockOf`, `FlattenBody`) and §3 (THE CATEGORY
// SHIMMER, W21): the category page's picture. The host decided the load verdict, the body's SHAPE (`BrowsePageLayout`) and each band's rows; this draws
// them.
//
// The outer column owns the frame: the family gutters and the overlay masthead's drill-in reserve (the trail's slot over the title; the band takes no
// in-flow height), with Spacing.L under the body; only the region below it ever shimmers. FadeOnly: this body owns no entrance, and None would floor the shimmer's exit at 400 ms and leave a ghost
// under real content (must-not-simplify 12). smoothResize false (must-not-simplify 2).
//
// SHELVES: the page owns a ScrollView (keyed by the page uri); every section keyed by its own uri, then the Explore link; the dock clearance lives INSIDE the
// scrolled content so the viewport reaches the pane edge. FLATTENED: the non-shelf blocks and the Explore link PIN above a Grow-1 / MinHeight-0 slot whose grid
// owns its own scroll (a virtual viewport inside a page ScrollView measures 0 and shears every cover — must-not-simplify 10). FlattenOne is the
// single-section page: its grid is the section's WHOLE list as the query layer walked it — nothing here pages — headed by the section's title when that says
// more than the masthead (`BrowsePageLayout.TitleIsRedundant`). FlattenTwoConcat merges both shelves (the host's memoized list). FlattenTwoStacked renders as
// Shelves (0.2.9's documented pragmatic deviation, kept).
//
// THE SHIMMER is a NON-virtualized stand-in for the Shelves geometry: a Related block over five placeholder tiles and two shelf bands of six shared-surface seed
// cards — no PagedShelf, no virtual grid, every box sized off the same constants the real body uses, so nothing measures zero and nothing reflows when the real
// width lands. Body only: the masthead is real from frame one. Its words are never shown (the deriver paints bars in their place).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Browse category page.</summary>
internal static class ClassicBrowseCategory
{
    /// <summary>The state the flattened grid's responsive box is gated on: the memoized card list BY REFERENCE, its key and
    /// the DECODED chart bit (never a uri lookup — ch 12 trap 10).</summary>
    sealed record GridState(IReadOnlyList<HomeCard> Cards, string Key, bool Charts);

    /// <summary>The registered template of <see cref="ViewKeys.BrowseCategory"/>.</summary>
    public static Element Template(BrowseCategoryProps props, in ViewContext ctx)
    {
        Element? body = null;

        // FadeOnly: this body owns no entrance, and None would floor the shimmer's exit at 400 ms and leave a ghost
        // under real content (must-not-simplify 12). smoothResize false (must-not-simplify 2).
        Element region = new SkelRegionEl(
            Pending: () => props.Load == BrowseLoad.Pending, Failed: () => props.Load == BrowseLoad.Failed,
            Content: () => body ??= props.Load == BrowseLoad.Ready ? Body(props) : new BoxEl(),
            ShimmerSource: s_shimmer,
            // W22 + 0.3's Retry (§9 gap 9): the error arm offers Retry AND the Explore link, inside the scroll frame.
            OnFailed: () => Framed(props, Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry)),
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null,
            SmoothResize: false);

        // The outer column owns the frame: the family gutters and the overlay masthead's reserve (the band takes no
        // in-flow height), with Spacing.L under the body; only the region below it ever shimmers.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = ClassicPageHeadRules.DrillBodyPad(Spacing.L),
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [region] },
            ],
        };
    }

    // ── the body ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Body(BrowseCategoryProps p)
    {
        if (p.Bands.Count == 0) return Framed(p, EmptyVacancy());
        return p.Mode switch
        {
            BrowsePageLayout.Mode.FlattenOne => FlattenBody(p, concat: false),
            BrowsePageLayout.Mode.FlattenTwoConcat => FlattenBody(p, concat: true),
            // FlattenTwoStacked — 0.2.9's pragmatic deviation, kept: two virtual grids cannot both own scroll, and one
            // shared scroller re-creates the zero-height trap. The pure rule still tells the two modes apart.
            _ => ShelvesBody(p),
        };
    }

    static Element EmptyVacancy()
        => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: "");

    /// <summary>Shelves: the page owns a ScrollView (keyed by the page uri); every section keyed by its own uri, then
    /// the Explore link; the dock clearance lives INSIDE the scrolled content so the viewport reaches the pane edge.</summary>
    static Element ShelvesBody(BrowseCategoryProps p)
    {
        var bands = p.Bands;
        var kids = new Element[bands.Count + 1];
        for (int i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            kids[i] = band.Facts.Kind == BrowseSectionKind.Shelf ? Shelf(p, band) : CategoryBlockOf(p, band);
        }
        kids[^1] = ClassicBrowseCells.ExploreAll(p.ExploreAll);
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, R.Space.DockReserve + Spacing.XXL),
            Children = kids,
        }) with { Grow = 1f, MinHeight = 0f, ScrollKey = p.ScrollKey };
    }

    /// <summary>The empty and error arms get the same scroll shell by hand (the region picks them before the mode
    /// dispatch), with the Explore link under the vacancy (parity 79-80).</summary>
    static Element Framed(BrowseCategoryProps p, Element content) => ScrollView(new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0f,
        Padding = new Edges4(0f, 0f, 0f, R.Space.DockReserve + Spacing.XXL),
        Children = [content, ClassicBrowseCells.ExploreAll(p.ExploreAll)],
    }) with { Grow = 1f, MinHeight = 0f, ScrollKey = p.ScrollKey };

    /// <summary>One shelf band (0.2.9 <c>BrowsePage.Shelf</c>): a PagedShelf of the house shelf cell — play FAB, card
    /// menu, and a drag source for everything but tracks and episodes. A TITLED shelf's header drills into
    /// <c>browse-section:</c> with this page as the origin; an untitled one has no header row at all (parity 77).</summary>
    static Element Shelf(BrowseCategoryProps p, BrowseBand band)
    {
        var f = band.Facts;
        var view = band.Shelf!;
        var items = new HomeCards.ShelfItem[view.Cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = view.Cards[i];
            items[i] = HomeCards.ShelfItemOf(in c, HomeCards.PlainText(c.Subtitle), c.Kind == HomeCardKind.Artist);
        }
        var host = p.Overlay;
        var open = p.OpenSection;
        Element? header = null;
        string? shelfTitle = f.Title;
        if (!string.IsNullOrWhiteSpace(shelfTitle))
            header = HomeModules.DrillHeader(shelfTitle, () => open(view));
        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) =>
            {
                var c = item.Card;
                return HomeCards.ShelfCell(in item, cardW, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                                           HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c));
            },
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),   // the slot owns the click: the card's own open
            cardHeight: HomeModuleLayout.ShelfCardHeight,
            header: header,
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
            gap: HomeModuleLayout.ShelfGap, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, i) => "browse-shelf-card:" + item.Card.Uri)
           with { Key = "browse-shelf:" + f.Uri };
    }

    /// <summary>A grid / related band: its category tiles as live link cells under a LABEL header. A tile drills with THIS
    /// page as its origin (<see cref="BrowseTileOrigin"/>), so Decades › 00s keeps its parent crumb; the origin is read at
    /// click time from the host's own fields, never frozen into a mount-time closure.</summary>
    static Element CategoryBlockOf(BrowseCategoryProps p, BrowseBand band)
    {
        var tiles = new List<BrowseTileModel>(band.Tiles.Count);
        for (int i = 0; i < band.Tiles.Count; i++) tiles.Add(BrowseTiles.ToModel(band.Tiles[i], p.OpenCategory, BrowseTiles.OpenFeature));
        return ClassicBrowseCells.CategoryBlock("browse-cats:" + band.Facts.Uri, band.Facts.Title, tiles);
    }

    /// <summary>A flattened body (0.2.9 <c>BrowsePage.FlattenBody</c>): the non-shelf blocks and the Explore link PIN
    /// above a Grow-1 / MinHeight-0 slot whose grid owns its own scroll. FlattenOne is the single-section page: its
    /// grid is the section's WHOLE list as the query layer walked it — nothing here pages — headed by the section's
    /// title when that says more than the masthead (<see cref="BrowsePageLayout.TitleIsRedundant"/>).
    /// FlattenTwoConcat merges both shelves.</summary>
    static Element FlattenBody(BrowseCategoryProps p, bool concat)
    {
        var pinned = new List<Element>(3);
        HomeSectionView? first = null, second = null;
        string key = p.PageUri;
        string? heading = null;
        var bands = p.Bands;
        for (int i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            var f = band.Facts;
            if (f.Kind != BrowseSectionKind.Shelf) { pinned.Add(CategoryBlockOf(p, band)); continue; }
            var view = band.Shelf!;
            if (first is null)
            {
                first = view;
                if (!concat)
                {
                    key = f.Uri;
                    if (!BrowsePageLayout.TitleIsRedundant(f.Title, p.Title)) heading = f.Title!.Trim();
                }
            }
            else second ??= view;
        }
        if (heading is not null) pinned.Insert(0, HomeModules.DrillHeader(heading, null));
        pinned.Add(ClassicBrowseCells.ExploreAll(p.ExploreAll));

        IReadOnlyList<HomeCard> cards = concat ? p.Merged : first?.Cards ?? (IReadOnlyList<HomeCard>)Array.Empty<HomeCard>();
        bool charts = first is { IsChart: true } && (!concat || second is null || second.IsChart);

        Element grid = Responsive.Of("browse-category:grid", new GridState(cards, key, charts), static (st, w) => HomeModules.SectionGrid(
            st.Cards, st.Key, w, static c => HomeCardNav.Open(in c), charts: st.Charts), fallback: HomeModuleLayout.FallbackWidth, grow: 1f);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
            Children =
            [
                new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, Children = pinned.ToArray() },
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [grid] },
            ],
        };
    }

    // ══ THE CATEGORY SHIMMER (W21) ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>A NON-virtualized stand-in for the Shelves geometry: a Related block over five placeholder tiles and two
    /// shelf bands of six shared-surface seed cards — no PagedShelf, no virtual grid, every box sized off the same constants the
    /// real body uses, so nothing measures zero and nothing reflows when the real width lands. Body only: the masthead
    /// is real from frame one. Its words are never shown (the deriver paints bars in their place).</summary>
    static readonly Func<Element> s_shimmer = static () => new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0f, Grow = 1f,
        Padding = new Edges4(0f, 0f, 0f, R.Space.DockReserve + Spacing.XXL),
        Children = [ShimmerRelated(), ShimmerShelf(), ShimmerShelf()],
    };

    static readonly string[] s_shimmerTileUris =
        ["wavee:skeleton:related:0", "wavee:skeleton:related:1", "wavee:skeleton:related:2", "wavee:skeleton:related:3", "wavee:skeleton:related:4"];

    static Element ShimmerRelated()
    {
        var tiles = new BrowseTileModel[s_shimmerTileUris.Length];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = new BrowseTileModel(Loc.Get(Strings.Browse.Genres), s_shimmerTileUris[i], null, null, BrowseTiles.ToModelNoop);
        return ClassicBrowseCells.CategoryBlock("browse-more", Loc.Get(Strings.Browse.More), tiles);
    }

    static Element ShimmerShelf()
    {
        var cards = new Element[ClassicBrowseRules.ShimmerShelfCards];
        for (int i = 0; i < cards.Length; i++) cards[i] = ShimmerCard();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                R.Type.ModuleHeader(Loc.Get(Strings.Browse.ExploreAll)),
                new BoxEl { Direction = 0, Gap = HomeModuleLayout.ShelfGap, MinWidth = 0f, ClipToBounds = true, Children = cards },
            ],
        };
    }

    /// <summary>The real shelf card's SEED face (cover + title bar + caption bar) at the shelf's MIN width — the shared
    /// surface's own skeleton, exactly as tall as <see cref="HomeModuleLayout.ShelfCardHeight"/> says the live card is.</summary>
    static Element ShimmerCard() => Controls.Surface(Controls.CardData.Seed, Shape.Shelf(), HomeModuleLayout.ShelfCardMin);
}
