// ── Themes/Zune/Structure/Views/Search/ZuneSearchPage.cs — Zune's `Pages.Search` (themes plan 6.3; P5 Z5) ────────────────────────────────────
//
// Board Z-Search. The facet strip is the band's row 2 (`Search.PageHost` publishes it through the page's sink, each facet with its count), so the
// page draws NO query echo and NO facet row: it is the facet body alone, on the frame's gutter, 8 under the band (`ZuneSearchRules`):
//
//   All        top result (the "top result" heading and the hit's kind, the 180 art beside the copy: the name 40/48 light, its line, the matched
//              words in the accent, the play ring and the hit's own save or follow control) | best matches (two columns of 56 rows), 56 apart;
//              then the playlist shelf (154 covers, "see all" switches to the Playlists facet); then genres · related searches as inline words
//   Genres     the genre names as a list (the Browse directory's)
//   Songs      the hit rows in the tier's columns, scrolling with the page
//   Albums · Playlists   the facet row's virtual card grid, owning its scroll (a virtual viewport inside a page ScrollView measures 0)
//   the rest   a column of hit rows
//
// Every hit row is the shared search row (`Search.HitData` on `Controls.Surface`, drawn by Zune's surface style): the menu, the drag, the play
// affordance and the save or follow control travel in its data. THE SLIDE is the production one: the facet body is keyed per facet and slides only
// on a real chip change (the host's `SearchSlide`). The shimmer stands in the facet's own shape (seed rows, seed cards), never one bar stack.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using S = Wavee.Themes.Zune.ZuneSearchRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Search results page.</summary>
internal static class ZuneSearchPage
{
    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;

    /// <summary>The registered template of <see cref="ViewKeys.SearchPage"/>.</summary>
    public static Element Template(SearchPageProps props, in ViewContext ctx)
    {
        var lay = ZuneSearchTiers.Of(ctx.Tier());
        float firstW = ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width;
        if (props.Vacant) return Controls.Vacancy(Controls.VacancyVoice.Empty);

        SearchFacet facet = props.Facet;
        var region = new SkelRegionEl(
            Pending: props.Pending, Failed: props.Failed, Content: () => Content(props, lay, firstW), ShimmerSource: () => Shimmer(facet, lay),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
            Reveal: facet == SearchFacet.Tracks ? SkelReveal.None : SkelReveal.StaggerRows,
            Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        LayoutTransition? motion = props.Slide switch
        {
            SearchSlide.Forward => MotionRecipes.PageSlideForward,
            SearchSlide.Back => MotionRecipes.PageSlideBack,
            _ => null,
        };
        string facetId = ((int)facet).ToString(CultureInfo.InvariantCulture);

        Element body = Search.IsGridFacet(facet)
            // Both bodies stand in the page column (A1): capped at 2400 + the gutters and centred, on the band's x at every width.
            ? ZuneFrameRules.PageRow(new BoxEl
            {
                Key = "facet-body:" + facetId, Animate = motion,
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(0f, H.PageTop, 0f, 0f),
                Children = [region],
            }, lay.Gutter)
            : Ui.ScrollView(ZuneFrameRules.PageRow(new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(0f, H.PageTop, 0f, H.PageBottom),
                Children =
                [
                    new BoxEl { Key = "facet-body:" + facetId, Animate = motion, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [region] },
                ],
            }, lay.Gutter)) with
            {
                Grow = 1f, MinWidth = 0f, MinHeight = 0f, ScrollKey = props.ScrollKey,
            };

        return new BoxEl
        {
            Key = S.PageKey, Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch, Children = [body],
        };
    }

    /// <summary>The facet body for the host's model (read inside the skeleton region's content binding).</summary>
    static Element Content(SearchPageProps props, ZuneSearchLayout lay, float firstW)
    {
        var m = props.Body();
        var ctx = m.Hits;
        return m.Kind switch
        {
            SearchBodyKind.Pending => Shimmer(m.Facet, lay),
            SearchBodyKind.Empty => Empty(m.Facet, ctx.Query),
            SearchBodyKind.All => All(m, props, lay),
            SearchBodyKind.Genres => m.Genres.Count == 0 ? new BoxEl() : ZuneBrowseDirectory.LinkGrid(m.Genres, lay.GenreCols, "search-genres", lay.GenreRowH),
            SearchBodyKind.Songs => Grid(m.Rows, ctx, lay),
            SearchBodyKind.FacetGrid => FacetGrid(m.Row, ctx, firstW),
            _ => m.Rows.Count == 0 ? Empty(m.Facet, ctx.Query) : Column(m.Rows, ctx, lay),
        };
    }

    // ══ ALL ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element All(SearchBodyModel m, SearchPageProps props, ZuneSearchLayout lay)
    {
        var ctx = m.Hits;
        var kids = new List<Element>(4);
        if (m.Hero is { } hero)
        {
            Element top = TopResult(hero.Ref, hero.Flags, ctx, lay);
            if (m.Best is { Count: > 0 } best)
            {
                bool beside = lay.TopBeside;
                kids.Add(new BoxEl
                {
                    Key = "zune-search-top-row", Direction = (byte)(beside ? 0 : 1), Gap = beside ? lay.TopGap : lay.ZoneGap,
                    MinWidth = 0f, AlignItems = FlexAlign.Start, Layout = H.ChapterResize,
                    Children =
                    [
                        new BoxEl { Key = S.TopKey, Direction = 1, MinWidth = 0f, Width = beside ? lay.TopW : float.NaN, Shrink = 0f, AlignSelf = beside ? FlexAlign.Start : FlexAlign.Stretch, Children = [top] },
                        new BoxEl
                        {
                            Key = S.BestKey, Direction = 1, MinWidth = 0f, Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN,
                            AlignSelf = FlexAlign.Stretch,
                            Children = [K.Section(K.Heading(Loc.Get(Strings.Search.BestMatches), null, null), Grid(Take(best, S.BestMax), ctx, lay))],
                        },
                    ],
                });
            }
            else kids.Add(new BoxEl { Key = S.TopKey, Direction = 1, MinWidth = 0f, Children = [top] });
        }
        else if (m.Fallback is { } fallback)
        {
            if (fallback.Count == 0) kids.Add(Empty(SearchFacet.All, ctx.Query));
            else
            {
                var rows = new Element[fallback.Count];
                for (int i = 0; i < rows.Length; i++) rows[i] = HitRow(fallback[i].Hit.Ref, fallback[i].Hit.Flags, fallback[i].Large, ctx, lay);
                kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
            }
        }
        else kids.Add(Empty(SearchFacet.All, ctx.Query));

        if (m.Rail.Count > 0) kids.Add(Rail(m.Rail, props.SelectPlaylists, ctx, lay));

        Element genres = Genres(m, props, lay), related = Related(m, props, lay);
        bool runs = lay.RunsBeside;
        kids.Add(new BoxEl
        {
            Key = "zune-search-runs", Direction = (byte)(runs ? 0 : 1), Gap = runs ? S.RunsGap : lay.ZoneGap,
            MinWidth = 0f, AlignItems = FlexAlign.Start, Children = [genres, related],
        });
        return new BoxEl { Direction = 1, Gap = lay.ZoneGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    static IReadOnlyList<Search.HitItem> Take(IReadOnlyList<Search.HitItem> items, int n)
    {
        if (items.Count <= n) return items;
        var kept = new Search.HitItem[n];
        for (int i = 0; i < n; i++) kept[i] = items[i];
        return kept;
    }

    /// <summary>The top result: the heading (with the hit's kind), the art beside the copy (over it at Huge), the whole card one click target with
    /// the hit's menu. The name stands in the copy (Standard, Huge), over the art row (Compact) or under it (Small): the tier's record.</summary>
    static Element TopResult(EntityRef hit, SearchHitFlags flags, Search.HitContext ctx, ZuneSearchLayout lay)
    {
        if (hit.IsNone) return new BoxEl();
        string uri = hit.Id.Text;
        string title = Search.TitleOf(hit);
        string? url = Search.CoverOf(hit);
        var h = hit;
        var origin = ctx.Origin;
        var menu = Search.MenuOf(hit);
        float art = lay.HeroArt;

        var matched = new List<string>(2);
        if ((flags & SearchHitFlags.MatchedTitle) != 0) matched.Add(Loc.Get(Strings.Search.MatchedTitle));
        if ((flags & SearchHitFlags.MatchedLyrics) != 0) matched.Add(Loc.Get(Strings.Search.LyricsMatch));

        var actions = new List<Element>(2);
        if (Search.CanPlay(hit.Kind))
            actions.Add(K.Ring("zune-search-top-play", Icons.Play, Loc.Get(Strings.Home.Play), lay.RingLabels, primary: true, () => Search.PlayHit(h), lay.RingSize));
        if (Search.TrailingOf(hit, uri, title) is { } trailing) actions.Add(trailing);

        TextEl name = (lay.HeroNameHero ? R.Type.Hero(title) : R.Type.PageHero(title)) with
        {
            Key = "zune-search-top-name", MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var copy = new List<Element>(4);
        if (lay.HeroName == ZuneSearchHeroName.InCopy) copy.Add(name);
        if (hit.Kind is not (EntityKind.Artist or EntityKind.User))
            copy.Add(R.Type.DenseMeta(Search.SubtitleOf(hit, flags)) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (matched.Count > 0)
            copy.Add(R.Type.MicroMeta(string.Join(" · ", matched)) with { Color = Tok.AccentTextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, MinWidth = 0f });
        if (actions.Count > 0)
            copy.Add(new BoxEl
            {
                Direction = 0, Gap = H.DaylistActionsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Margin = new Edges4(0f, H.DaylistActionsTop, 0f, 0f), Children = actions.ToArray(),
            });

        float corners = Search.RoundArt(hit.Kind) ? R.Shape.For(ShapeRole.Avatar) : R.Shape.For(ShapeRole.Cover);
        bool stacked = lay.HeroStacked;
        var row = new BoxEl
        {
            Key = "zune-search-top-hero", Direction = (byte)(stacked ? 1 : 0), Gap = S.HeroGap, AlignItems = stacked ? FlexAlign.Start : FlexAlign.End, MinWidth = 0f,
            Children =
            [
                new BoxEl { Width = art, Height = art, Shrink = 0f, Children = [Controls.Artwork(url, art, art, corners)] },
                new BoxEl { Direction = 1, Gap = S.HeroLineGap, Grow = stacked ? 0f : 1f, Shrink = 1f, MinWidth = 0f, Children = copy.ToArray() },
            ],
        };
        Element[] parts = lay.HeroName switch
        {
            ZuneSearchHeroName.Above => new Element[] { name, row },
            ZuneSearchHeroName.Below => new Element[] { row, name },
            _ => new Element[] { row },
        };
        var card = new BoxEl
        {
            Key = "zune-search-top-card", Direction = 1, Gap = S.HeroLineGap, MinWidth = 0f,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, Draggable = Search.DragOf(hit),
            OnClick = () => Search.OpenHit(h, origin),
            Children = parts,
        };
        Element face = menu is not null && !Controls.IsNullOverlay(ctx.Overlay) ? ContextSheets.Attach(card, ctx.Overlay, menu) : card;
        return K.Section(K.Heading(Loc.Get(Strings.Search.TopResult), Search.KindWordOf(hit.Kind, flags), null), face);
    }

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The search row at the tier's art and floor.</summary>
    static SurfaceShape RowShape(ZuneSearchLayout lay) => Shape.Row(lay.BestArt) with { MinHeight = lay.BestRowH };

    /// <summary>THE search row (the shared row over <see cref="Search.HitData"/>); the large one keeps the large shape. Keyed by the hit, so a list
    /// that reorders carries each row's hover and menu with it.</summary>
    static Element HitRow(EntityRef hit, SearchHitFlags flags, bool large, Search.HitContext ctx, ZuneSearchLayout lay)
    {
        if (hit.IsNone) return new BoxEl();
        var shape = large ? SearchHitRules.ShapeOf(large: true, artHidden: false) : RowShape(lay);
        return Controls.Surface(Search.HitData(hit, flags, large, in ctx, hideTrackArt: false), shape) with { Key = "hit:" + Search.KeyOf(hit) };
    }

    /// <summary>Hit rows in the tier's columns (best matches, the songs): ONE keyed grid whose column count is a live prop.</summary>
    static Element Grid(IReadOnlyList<Search.HitItem> rows, Search.HitContext ctx, ZuneSearchLayout lay)
    {
        var cells = new Element[rows.Count];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new BoxEl { Direction = 1, MinWidth = 0f, Children = [HitRow(rows[i].Ref, rows[i].Flags, large: false, ctx, lay)] };
        return new GridEl
        {
            Key = "zune-search-grid", MinColWidth = lay.BestMinCol, MaxColumns = lay.BestCols, ColGap = S.BestColGap, Children = cells,
        };
    }

    /// <summary>A flat facet's hits as one column.</summary>
    static Element Column(IReadOnlyList<Search.HitItem> rows, Search.HitContext ctx, ZuneSearchLayout lay)
    {
        var cells = new Element[rows.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = HitRow(rows[i].Ref, rows[i].Flags, large: false, ctx, lay);
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Children = cells };
    }

    // ── the playlist shelf ───────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Func<float, float> s_cardHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);

    static Element Rail(IReadOnlyList<Search.HitItem> items, Action select, Search.HitContext ctx, ZuneSearchLayout lay)
        => PagedShelf.Create(items,
            cardAt: (item, i, cardW) => Controls.Surface(Search.CardDataOf(item.Ref, ctx.Origin), Shape.Shelf(), cardW),
            onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),
            cardHeight: s_cardHeight,
            header: K.Heading(Loc.Get(Strings.Search.Playlists), null, K.Link("zune-search-playlists-all", Loc.Get(Strings.Home.SeeAll), select, lay.LinkTarget)),
            pager: ShelfPager.Chevrons,
            // Standard: the fixed 154 card; elsewhere the shelf FITS up to the tier's columns (live props of ONE shelf).
            minCardW: lay.ShelfMinW, maxCardW: lay.ShelfCols > 0 ? H.WideMaxW : H.ShelfCardW, fixedCardW: lay.ShelfCols > 0 ? 0f : H.ShelfCardW,
            maxColumns: lay.ShelfCols, gap: H.ShelfGap, headerGap: H.HeadGap,
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,
            keyOf: static (item, _) => Search.KeyOf(item.Ref))
            with { Key = S.RailKey };

    // ── the Albums / Playlists facet grid ────────────────────────────────────────────────────────────────────────────────

    static Element FacetGrid(Search row, Search.HitContext ctx, float firstW)
        => Responsive.Of("zune-search:facet-grid", w =>
        {
            int cols = Math.Max(1, GridEl.AutoFillColumnCount(w, H.SectionMinCell, Search.FacetGridGap));
            int count = Math.Min(Math.Max(row.ResultCount, row.ResultTotal), Search.FacetHitCeiling);
            var r = row;
            var c = ctx;
            return new VirtualListEl
            {
                ItemCount = count,
                ItemLayout = FacetGridLayout(cols),
                RenderItem = i => GridCell(r, i, in c),
                KeyOf = i => r.ResultRef(i) is { IsNone: false } hit ? Search.KeyOf(hit) : "search-card:placeholder:" + i.ToString(CultureInfo.InvariantCulture),
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            } with { Key = "zune-search-facet-grid" };
        }, fallback: firstW, grow: 1f);

    static readonly Dictionary<int, AspectGridVirtualLayout> s_layouts = new();

    static AspectGridVirtualLayout FacetGridLayout(int cols)
    {
        if (!s_layouts.TryGetValue(cols, out var layout))
            s_layouts[cols] = layout = new AspectGridVirtualLayout(cols, 1f, Search.FacetGridChrome + (Search.FacetGridRowGap - Search.FacetGridGap), Search.FacetGridGap);
        return layout;
    }

    static Element GridCell(Search row, int index, in Search.HitContext ctx)
    {
        var hit = row.ResultRef(index);
        return Controls.Surface(hit.IsNone ? Controls.CardData.Seed : Search.CardDataOf(hit, ctx.Origin), Shape.Grid);
    }

    // ── genres and related searches ──────────────────────────────────────────────────────────────────────────────────────

    static Element Genres(SearchBodyModel m, SearchPageProps props, ZuneSearchLayout lay)
    {
        switch (m.GenreState)
        {
            case SearchSection.Ready:
                if (m.Genres.Count == 0) return new BoxEl();
                var words = new Element[m.Genres.Count];
                for (int i = 0; i < words.Length; i++) words[i] = Word("genre:" + m.Genres[i].Uri, m.Genres[i].Title, cased: false, primary: true, m.Genres[i].Open, lay.LinkTarget);
                return Run(S.GenresKey, Loc.Get(Strings.Search.Genres), words);
            case SearchSection.Failed:
                return Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: props.RetryAll);
            case SearchSection.Concluded:
                return new BoxEl();
            default:
                var seeds = new Element[S.ShimmerGenres];
                for (int i = 0; i < seeds.Length; i++)
                    seeds[i] = Word("genre-seed:" + i.ToString(CultureInfo.InvariantCulture), Loc.Get(Strings.Search.Genres), cased: false, primary: true, static () => { }, lay.LinkTarget);
                return new SkelRegionEl(
                    Pending: s_true, Failed: s_false, Content: () => Run(S.GenresKey, Loc.Get(Strings.Search.Genres), seeds), ShimmerSource: null, OnFailed: null,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        }
    }

    static Element Related(SearchBodyModel m, SearchPageProps props, ZuneSearchLayout lay)
    {
        switch (m.RelatedState)
        {
            case SearchSection.Ready:
                if (m.Related.Count == 0) return new BoxEl();
                var words = new Element[m.Related.Count];
                for (int i = 0; i < words.Length; i++)
                {
                    string q = m.Related[i];
                    words[i] = Word("rel:" + q, q, cased: true, primary: false, () => Search.OpenRelated(q), lay.LinkTarget);
                }
                return Run(S.RelatedKey, Loc.Get(Strings.Search.RelatedSearches), words);
            case SearchSection.Failed:
                return Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: props.RetryAll);
            default:
                return new BoxEl();
        }
    }

    /// <summary>A heading over an inline run of words that wraps.</summary>
    static Element Run(string key, string heading, Element[] words)
        => K.Section(K.Heading(heading, null, null), new BoxEl { Direction = 0, Wrap = true, Gap = S.WordGap, MinWidth = 0f, Children = words })
            with { Key = key, Shrink = 1f };

    /// <summary>One word of a run: the 15 voice, a link (a genre is a name and keeps its casing; a related query is lowercased by the case role).</summary>
    static Element Word(string key, string text, bool cased, bool primary, Action go, float target) => new BoxEl
    {
        // A short word ("pop") is still a 40 x 40 target at Compact: the box is at least that wide as well as tall.
        Key = key, Shrink = 0f, MinHeight = target, MinWidth = target, Direction = 0, AlignItems = FlexAlign.Center,
        Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = go,
        Children =
        [
            R.Type.TrackTitle(text) with
            {
                Color = primary ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = primary ? Tok.TextSecondary : Tok.TextPrimary,
                Case = cased ? R.Case.For(LabelKind.Label) : TextCase.AsWritten, MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    // ── empty and the shimmer ────────────────────────────────────────────────────────────────────────────────────────────

    static Element Empty(SearchFacet facet, string query)
    {
        if (facet == SearchFacet.Genres) return new BoxEl();
        return Search.EmptyKey(facet) is { } key
            ? Controls.Vacancy(Controls.VacancyVoice.NoMatch, title: Loc.Get(key), subtitle: "")
            : Controls.Vacancy(Controls.VacancyVoice.NoMatch, title: Loc.Get(Strings.Search.NoResults), subtitle: Strings.Search.NoResultsSub(query));
    }

    /// <summary>The facet's own shape in bones: seed rows (the row's own geometry), or seed cards for a card facet.</summary>
    static Element Shimmer(SearchFacet facet, ZuneSearchLayout lay)
    {
        if (Search.ShimmerFor(facet) == Search.ShimmerShape.CardGrid)
        {
            var cards = new Element[S.ShimmerRows];
            for (int i = 0; i < cards.Length; i++) cards[i] = Seed(Shape.Shelf(), H.ShelfCardW);
            return new BoxEl { Direction = 0, Wrap = true, Gap = H.ShelfGap, AlignSelf = FlexAlign.Stretch, Children = cards };
        }
        var rows = new Element[S.ShimmerRows];
        var shape = RowShape(lay);
        for (int i = 0; i < rows.Length; i++) rows[i] = Seed(shape);
        return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Children = rows };
    }

    static Element Seed(SurfaceShape shape, float width = float.NaN)
    {
        Element seed = SurfaceParts.Seed(in shape, width);
        return seed.Skel(seed);
    }
}
