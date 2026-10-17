// ── Themes/Zune/Structure/Views/Browse/ZuneBrowseCategory.cs — Zune's `Pages.BrowseCategory` (themes plan 6.3; P5 Z5) ─────────────────────────
//
// Board Z-Browse-Category: a BANNER across the page (200 at Standard) in the accent (the props carry no category colour) with the category's name
// as a panorama word (120/130 at the light 200, lowercase, its line reserved, 40 in from the banner's top left) and its kind at the bottom right
// (13, 48 in, 24 up), then the page's sections 28 under it and 28 apart on the frame's gutter: a titled shelf is a heading row ("see all" drills
// into the section with this page as the origin) over 154 covers 16 apart; a tile block is its heading over six-across 96 plates; then "explore all".
// The board's band is 52 (no row 2); the plan's route-level row 2 (Browse's bands, ZC1) wins, so the band keeps its two rows here.
//
// The host decided the load verdict, the body's SHAPE (`BrowsePageLayout.Mode`) and each band's rows. SHELVES: the banner and the sections scroll
// together in the page's ScrollView. FLATTENED (one section, or two merged): the banner and the tile blocks stand above a Grow-1 slot whose
// virtual grid owns its own scroll (a virtual viewport inside a page ScrollView measures 0). Pending shimmers two shelves of seed cards under the
// real banner; the error and the empty vacancies sit under it with "explore all".

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneBrowseRules;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Browse category page.</summary>
internal static class ZuneBrowseCategory
{
    sealed record GridState(IReadOnlyList<HomeCard> Cards, string Key, bool Charts);

    /// <summary>The registered template of <see cref="ViewKeys.BrowseCategory"/>.</summary>
    public static Element Template(BrowseCategoryProps props, in ViewContext ctx)
    {
        var lay = ZuneBrowseTiers.Of(ctx.Tier());
        float firstW = ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width;
        Element? body = null;
        Element region = new SkelRegionEl(
            Pending: () => props.Load == BrowseLoad.Pending, Failed: () => props.Load == BrowseLoad.Failed,
            Content: () => body ??= props.Load == BrowseLoad.Ready ? Body(props, lay, firstW) : new BoxEl(),
            ShimmerSource: () => Scrolled(props, lay, Shimmer(lay)),
            OnFailed: () => Scrolled(props, lay, Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry)),
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        return new BoxEl
        {
            Key = B.CategoryKey, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [region],
        };
    }

    // ── the banner ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The banner: the accent plate, the panorama name (its line reserved, never pushing the page), the kind at the bottom right.</summary>
    static Element Banner(string title, ZuneBrowseLayout lay)
    {
        var name = (lay.CompactTitle ? R.Type.PanoramaCompact(title) : R.Type.Panorama(title)) with
        {
            Color = Prop.Of(static () => R.OnMedia.Ink), Case = R.Case.For(LabelKind.Heading),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Key = B.BannerKey, Height = lay.BannerH, Shrink = 0f, MinWidth = 0f, ZStack = true, ClipToBounds = true,
            Fill = Prop.Of(static () => R.Ink.AccentAction), Animate = ZuneMotion.TitleScale,
            Children =
            [
                new BoxEl
                {
                    Key = B.BannerTitleKey, Direction = 1, MinWidth = 0f, Justify = FlexJustify.Start,
                    Padding = new Edges4(B.BannerTitleInset, B.BannerTitleInset, B.BannerTitleInset, 0f), Children = [name],
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestVisible = false,
                    Padding = new Edges4(B.BannerMetaInsetX, 0f, B.BannerMetaInsetX, B.BannerMetaInsetY),
                    Children =
                    [
                        R.Type.DenseMeta(Loc.Get(Strings.Theme.Zune.Discovery.Category)) with
                        {
                            Color = Prop.Of(static () => R.OnMedia.InkSecondary), Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The sections' column on the gutter, 28 under the banner, 28 apart.</summary>
    static BoxEl Sections(ZuneBrowseLayout lay, Element[] kids) => new()
    {
        Direction = 1, Gap = ZuneHomeTiers.For(lay.Tier).ZoneGap, MinWidth = 0f, MaxWidth = R.Space.PageMaxW, AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(lay.Gutter, B.CategoryTop, lay.Gutter, B.CategoryBottom), Children = kids,
    };

    /// <summary>A page that scrolls as one: the banner over <paramref name="content"/> and "explore all".</summary>
    static Element Scrolled(BrowseCategoryProps p, ZuneBrowseLayout lay, Element content)
        => Ui.ScrollView(new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [Banner(p.Title, lay), Sections(lay, [content, ExploreAll(p)])],
        }) with { Grow = 1f, MinHeight = 0f, ScrollKey = p.ScrollKey };

    static Element ExploreAll(BrowseCategoryProps p) => K.Link("zune-browse-explore-all", Loc.Get(Strings.Browse.ExploreAll), p.ExploreAll);

    // ── the body ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Body(BrowseCategoryProps p, ZuneBrowseLayout lay, float firstW)
    {
        if (p.Bands.Count == 0)
            return Scrolled(p, lay, Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: ""));
        return p.Mode switch
        {
            BrowsePageLayout.Mode.FlattenOne => Flattened(p, concat: false, lay, firstW),
            BrowsePageLayout.Mode.FlattenTwoConcat => Flattened(p, concat: true, lay, firstW),
            // FlattenTwoStacked draws as shelves (two virtual grids cannot both own the scroll): Classic's documented deviation, kept.
            _ => Shelves(p, lay),
        };
    }

    /// <summary>Shelves: every section keyed by its own uri, then "explore all", scrolling with the banner.</summary>
    static Element Shelves(BrowseCategoryProps p, ZuneBrowseLayout lay)
    {
        var kids = new List<Element>(p.Bands.Count + 1);
        foreach (var band in p.Bands)
            kids.Add(band.Facts.Kind == BrowseSectionKind.Shelf ? Shelf(p, band) : Block(p, band, lay));
        kids.Add(ExploreAll(p));
        return Ui.ScrollView(new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [Banner(p.Title, lay), Sections(lay, kids.ToArray())],
        }) with { Grow = 1f, MinHeight = 0f, ScrollKey = p.ScrollKey };
    }

    static readonly Func<float, float> s_cardHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);

    /// <summary>A shelf: the heading row (a titled shelf's "see all" drills into the section) over 154 covers 16 apart.</summary>
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
        Element? header = string.IsNullOrWhiteSpace(f.Title) ? null
            : K.Heading(f.Title!, null, K.Link("see-all:" + f.Uri, Loc.Get(Strings.Home.SeeAll), () => open(view)));
        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) =>
            {
                var c = item.Card;
                return HomeCards.ShelfCell(in item, cardW, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                                           HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c));
            },
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),
            cardHeight: s_cardHeight,
            header: header,
            pager: ShelfPager.Chevrons,
            minCardW: H.ShelfCardW, maxCardW: H.ShelfCardW, fixedCardW: H.ShelfCardW, gap: H.ShelfGap, headerGap: H.HeadGap,
            edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,
            keyOf: static (item, i) => "zune-browse-card:" + item.Card.Uri)
            with { Key = B.CategorySectionPrefix + f.Uri };
    }

    /// <summary>A tile block: its heading over six-across plates; a tile drills with THIS page as its origin.</summary>
    static Element Block(BrowseCategoryProps p, BrowseBand band, ZuneBrowseLayout lay)
    {
        var cells = new Element[band.Tiles.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            var m = BrowseTiles.ToModel(band.Tiles[i], p.OpenCategory, BrowseTiles.OpenFeature);
            cells[i] = K.Plate("zune-plate:" + m.Uri, m.Title, m.Color, m.Artwork, lay.MoodH, m.Open);
        }
        Element grid = new GridEl
        {
            Key = "zune-browse-block-grid:" + band.Facts.Uri, MinColWidth = B.PlateMinCol, MaxColumns = lay.MoodCols,
            ColGap = B.PlateGap, RowGap = B.PlateGap, Children = cells,
        };
        return string.IsNullOrWhiteSpace(band.Facts.Title)
            ? grid with { Key = B.CategorySectionPrefix + band.Facts.Uri }
            : K.Section(K.Heading(band.Facts.Title!, null, null), grid) with { Key = B.CategorySectionPrefix + band.Facts.Uri };
    }

    /// <summary>A flattened body: the banner and the tile blocks stand above a Grow-1 slot whose virtual grid owns its scroll.</summary>
    static Element Flattened(BrowseCategoryProps p, bool concat, ZuneBrowseLayout lay, float firstW)
    {
        var pinned = new List<Element>(3);
        HomeSectionView? first = null, second = null;
        string key = p.PageUri;
        string? heading = null;
        foreach (var band in p.Bands)
        {
            var f = band.Facts;
            if (f.Kind != BrowseSectionKind.Shelf) { pinned.Add(Block(p, band, lay)); continue; }
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
        if (heading is not null) pinned.Insert(0, K.Heading(heading, null, null));
        pinned.Add(ExploreAll(p));

        IReadOnlyList<HomeCard> cards = concat ? p.Merged : first?.Cards ?? (IReadOnlyList<HomeCard>)Array.Empty<HomeCard>();
        bool charts = first is { IsChart: true } && (!concat || second is null || second.IsChart);
        Element grid = Responsive.Of("zune-browse-category:grid", new GridState(cards, key, charts), static (st, w) => HomeModules.SectionGrid(
            st.Cards, st.Key, w, static c => HomeCardNav.Open(in c), charts: st.Charts,
            minCardW: H.SectionMinCell, maxCardW: H.ShelfCardW * 2f), fallback: firstW, grow: 1f);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                Banner(p.Title, lay),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = ZuneHomeTiers.For(lay.Tier).ZoneGap,
                    Padding = new Edges4(lay.Gutter, B.CategoryTop, lay.Gutter, 0f),
                    Children =
                    [
                        new BoxEl { Direction = 1, Gap = ZuneHomeTiers.For(lay.Tier).ZoneGap, MinWidth = 0f, Children = pinned.ToArray() },
                        new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [grid] },
                    ],
                },
            ],
        };
    }

    // ── the shimmer ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two shelves of seed cards at the shelf's card (no virtual shelf: nothing measures zero), the real banner above them.</summary>
    static Element Shimmer(ZuneBrowseLayout lay)
    {
        Element ShimmerShelf()
        {
            var cards = new Element[B.ShimmerCards];
            for (int i = 0; i < cards.Length; i++) cards[i] = Controls.Surface(Controls.CardData.Seed, Shape.Shelf(), H.ShelfCardW);
            return K.Section(K.Heading(Loc.Get(Strings.Browse.ExploreAll), null, null),
                new BoxEl { Direction = 0, Gap = H.ShelfGap, MinWidth = 0f, ClipToBounds = true, Children = cards });
        }
        return new BoxEl { Direction = 1, Gap = ZuneHomeTiers.For(lay.Tier).ZoneGap, MinWidth = 0f, Children = [ShimmerShelf(), ShimmerShelf()] };
    }
}
