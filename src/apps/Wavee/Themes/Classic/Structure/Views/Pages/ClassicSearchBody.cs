// ── Themes/Classic/Structure/Views/Pages/ClassicSearchBody.cs — the body of Classic's `Pages.Search` (themes plan 6.1, T10a2) ──────────────────
//
// From `Entities\Search.UI.cs` §3-§10 and `Entities\Search.Page.cs` (`AllBody`, `FallbackList`, `GenreSection`, `RelatedSection`): what the facet body DRAWS from the
// host's `SearchBodyModel` — the hit row, the top-result hero, Best matches, the playlist rail, the Songs grid, the Albums / Playlists facet grid, the flat hit
// lists, the genre links, the related queries, and the empty / error vacancies. The host decided WHICH body, WHICH hits and in what order (ranking, the fallback
// interleave, the rail's items, readiness); the data adapters (`Search.HitData`, `OpenHit`, `PlayHit`, `MenuOf`, `DragOf`, `TrailingOf`) are core verbs and data
// over a hit's `EntityRef`.
//
// ── ONE HIT ROW ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Every hit — All-tab list, Best matches cell, Songs cell, Artists / Podcasts / Episodes / Profiles lists — is `HitRow`: the app's ONE media surface
// (`Controls.Surface`) at `Shape.Row(48)` (`Shape.RowLarge` for a fallback list's lead row) over `Search.HitData`. The surface owns the hover and press plate, the
// hand cursor, the focus ring, the play FAB and the hot-revealed "…", so a podcast row looks identical wherever it came from (ch 13 §0.8) and nothing here paints
// a row. Which kind follows, saves, drags or wears the eyebrow is `SearchHitRules` — pure, pinned by SearchHitDataTests.
//
// ── CARDS ARE THE ONE SURFACE ────────────────────────────────────────────────────────────────────────────────────────
//
// The playlist rail's shelf card and the Albums / Playlists grid's cell are `Controls.Surface(data, Shape.Shelf() / Shape.Grid)` — the app's one media surface — and the
// context menu travels IN the data (`Search.CardDataOf`, `Search.HitData`): the host attaches it and the card's own "…" re-enters it, so nothing here wraps a card. A
// blank grid cell is the same host on `CardData.Seed`. A shelf's cards sit in a bound slot, so the SHELF owns their click: the rail and Best matches pass `onInvoke`.
//
// Every factory is a static function over the model's handles, run by the region's own content binding, so a hydrating row REBINDS. The three key families that
// survive are the facet body (the page), the column-count keys on the Responsive grids ("search-genres-grid:<cols>", "search-facet-grid:<cols>") and — not a data key
// — the per-uri Save / Follow embeds, whose props freeze by design (Controls.cs §4).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>What Wavee Classic's Search facet body draws.</summary>
internal static class ClassicSearchBody
{
    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;
    static readonly Func<Element> s_genreSeed = static () => GenreSeed();

    /// <summary>The facet body for the host's model (read inside the skeleton region's content binding).</summary>
    public static Element Content(SearchPageProps props)
    {
        var m = props.Body();
        var ctx = m.Hits;
        switch (m.Kind)
        {
            case SearchBodyKind.Pending: return ClassicSearchPage.Shimmer(m.Facet);
            case SearchBodyKind.Empty: return Empty(m.Facet, ctx.Query);
            case SearchBodyKind.All: return AllBody(m, props);
            case SearchBodyKind.Genres: return GenreGrid(m.Genres, header: false);
            case SearchBodyKind.Songs: return SongsGrid(m.Rows, in ctx);
            case SearchBodyKind.FacetGrid: return FacetGrid(m.Row, ctx);
            default: return HitsList(m.Rows, m.Facet, in ctx);
        }
    }

    /// <summary>The All tab (W1): the Top Result and Best matches off the ranked list — or, for an answer that carried
    /// none, the fallback interleave — then the playlist rail, the genre links and the related queries.</summary>
    static Element AllBody(SearchBodyModel m, SearchPageProps props)
    {
        var ctx = m.Hits;
        var kids = new List<Element>(6);
        if (m.Hero is { } hero)
        {
            kids.Add(TopResult(hero.Ref, hero.Flags, in ctx));
            if (m.Best is { } best) kids.Add(BestMatches(best, props.ColsFor, ctx));
        }
        else if (m.Fallback is { } fallback) kids.Add(FallbackList(fallback, in ctx));
        else kids.Add(Empty(SearchFacet.All, ctx.Query));

        if (m.Rail.Count > 0) kids.Add(PlaylistRail(m.Rail, props.SelectPlaylists, ctx));
        kids.Add(GenreSection(m, props));
        kids.Add(RelatedSection(m, props));
        return new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>0.2.9's no-top-hits list: the host's interleave drawn as hit rows (the lead one large).</summary>
    static Element FallbackList(IReadOnlyList<Search.FallbackHit> plan, in Search.HitContext ctx)
    {
        if (plan.Count == 0) return Empty(SearchFacet.All, ctx.Query);
        var rows = new Element[plan.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = HitRow(plan[i].Hit.Ref, plan[i].Hit.Flags, plan[i].Large, in ctx);
        return HitColumn(rows);
    }

    static Element GenreSection(SearchBodyModel m, SearchPageProps props)
    {
        switch (m.GenreState)
        {
            case SearchSection.Ready: return GenreGrid(m.Genres, header: true);
            case SearchSection.Failed: return SectionFailed(props.RetryAll);
            case SearchSection.Concluded: return new BoxEl();
            default:
                return new SkelRegionEl(
                    Pending: s_true, Failed: s_false, Content: s_genreSeed, ShimmerSource: null, OnFailed: null,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        }
    }

    /// <summary>Related queries: nothing while pending (0.2.9), the compact error with Retry on failure — matching the
    /// genre section rather than vanishing silently (§9 gap 2).</summary>
    static Element RelatedSection(SearchBodyModel m, SearchPageProps props) => m.RelatedState switch
    {
        SearchSection.Ready => RelatedQueries(m.Related),
        SearchSection.Failed => SectionFailed(props.RetryAll),
        _ => new BoxEl(),
    };

    // ══ SECTION LABELS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A section label: the 3×14 accent tick (Browse's, shared with Browse's band label) beside a rail
    /// header, with a 12-DIP chevron and a hyperlink role only when there is somewhere to go.</summary>
    static Element TickHeader(string title, Action? open = null)
    {
        var label = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Shrink = 1f,
            Children =
            [
                new BoxEl { Width = ClassicBrowseRules.TickW, Height = ClassicBrowseRules.TickH, Shrink = 0f, Corners = CornerRadius4.All(Spacing.XXS), Fill = Tok.AccentDefault },
                R.Type.RailHeader(title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        if (open is null) return label with { AlignSelf = FlexAlign.Stretch };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f, OnClick = open,
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            Children = [label, Icon(Icons.ChevronRight, 12f, Tok.TextTertiary) with { Shrink = 0f }],
        };
    }

    /// <summary>A column that cross-stretches its child to the pane, so a section's ellipsised header receives width.</summary>
    static BoxEl Stretch(Element child) => new() { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [child] };

    // ══ THE HIT ROW (W9, §3 "Search rows") ═══════════════════════════════════════════════════════════════════════════

    /// <summary>THE search row: <see cref="Search.HitData"/> on the one media surface at <see cref="SearchHitRules.ShapeOf"/> — 48
    /// art in the 64 floor (a hidden-artwork track row: the 32 play square), 84 art in the 112 floor when <paramref name="large"/>.
    /// Keyed by the hit's kind + slot, so a result list that reorders carries each row's hover and menu with it.</summary>
    static Element HitRow(EntityRef hit, SearchHitFlags flags, bool large, in Search.HitContext ctx)
    {
        if (hit.IsNone) return new BoxEl();
        bool hideTrackArt = ClassicOptions.HideTrackArt();   // tracked: the region's binding re-runs when the option flips
        var shape = SearchHitRules.ShapeOf(large, artHidden: !SearchHitRules.ShowsArt(hit.Kind, hideTrackArt));
        return Controls.Surface(Search.HitData(hit, flags, large, in ctx, hideTrackArt), shape) with { Key = "hit:" + Search.KeyOf(hit) };
    }

    /// <summary>A column of hit rows at the 8-DIP list gap.</summary>
    static Element HitColumn(ReadOnlySpan<Element> rows)
        => new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows.ToArray() };

    // ══ THE TOP RESULT (W4) ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 228-DIP top-result card: copy left, a 300×260 cover cropped off the right at −2.5°, a radial wash of
    /// the cover's own chrome grading (none when ungradeable), Play on that accent, Open page for a destination, the
    /// trailing control and the "…" only when a menu resolved. A static function, never a Component — the hit is read
    /// from the edge on every render (§1.5).</summary>
    static Element TopResult(EntityRef hit, SearchHitFlags flags, in Search.HitContext ctx)
    {
        if (hit.IsNone) return new BoxEl();
        string uri = hit.Id.Text;
        string title = Search.TitleOf(hit);
        string? url = Search.CoverOf(hit);
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;                 // a landed grading re-renders the page
        Scheme? scheme = url is { Length: > 0 } ? Design.ChromeSchemeFor(url) : null;
        ColorF accent = scheme is { } s ? Design.Palette.ChromeAccent(s) : Tok.AccentDefault;

        var h = hit;
        var origin = ctx.Origin;
        Action open = () => Search.OpenHit(h, origin);
        var menu = Search.MenuOf(hit);

        var actions = new List<Element>(4);
        if (Search.CanPlay(hit.Kind)) actions.Add(Controls.PlayButton(accent, () => Search.PlayHit(h)) with { Shrink = 0f });
        if (Search.TrailingOf(hit, uri, title) is { } trailing) actions.Add(trailing);
        // The "…" is a hover/focus REVEAL on the card (the Surface host's own mechanism, which a hand-built card does not
        // get for free): the wrapper is NON-interactive, so the engine's hover cascade resolves to the card and the pointer
        // anywhere on it reveals the button (HoverOpacity); keyboard focus within the card reveals it through the folded
        // focus edges (Controls.CardChromeRules.FocusWithin — the shell's own edge and the wrapper's subtree edge, so Tab from the
        // card onto the "…" does not read as a loss). The button itself rests fully opaque inside the wrapper.
        Action<bool>? cardFocus = null;
        if (menu is not null)
        {
            var revealed = new Signal<bool>(false);
            bool selfFocus = false, innerFocus = false;
            cardFocus = f => { selfFocus = f; revealed.Value = Controls.CardChromeRules.FocusWithin(selfFocus, innerFocus); };
            actions.Add(new BoxEl
            {
                Shrink = 0f, Opacity = Prop.Of(() => revealed.Value ? 1f : 0f), HoverOpacity = 1f,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnFocusChanged = f => { innerFocus = f; revealed.Value = Controls.CardChromeRules.FocusWithin(selfFocus, innerFocus); },
                Children = [Controls.Named(Controls.MoreButton(null, requestsContext: true, restOpacity: 1f), Loc.Get(Strings.Common.More))],
            });
        }

        var chips = new List<Element>(2);
        if ((flags & SearchHitFlags.MatchedTitle) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Search.MatchedTitle)));
        if ((flags & SearchHitFlags.MatchedLyrics) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Search.LyricsMatch)));

        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S, Justify = FlexJustify.End,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.XL, Spacing.L),
            Children =
            [
                R.Type.Eyebrow(Loc.Get(Strings.Search.TopResult) + " · " + Search.KindWordOf(hit.Kind, flags)) with
                    { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                R.Type.PageHero(title) with { MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                hit.Kind is EntityKind.Artist or EntityKind.User
                    ? new BoxEl()
                    : new TextEl(Search.SubtitleOf(hit, flags))
                    {
                        Size = ClassicSearchRules.HeroSubtitleSize, LineHeight = ClassicSearchRules.HeroSubtitleLineHeight, Color = Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                chips.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, MinWidth = 0f, Children = chips.ToArray() },
                actions.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = actions.ToArray() },
            ],
        };
        var cover = new BoxEl
        {
            Width = ClassicSearchRules.HeroCoverW, Height = ClassicSearchRules.HeroCoverH, Shrink = 0f, AlignSelf = FlexAlign.Center,
            OffsetX = ClassicSearchRules.HeroCoverOffsetX, OffsetY = ClassicSearchRules.HeroCoverOffsetY, Rotation = ClassicSearchRules.HeroCoverRotation,
            HitTestVisible = false, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            // Unscaled decode (no explicit decodePx): buckets to the 300x260 slot's own aspect, same as 0.2.10 — the
            // square decodePx: 512 this replaced forced a 512x512 decode/upload (~3.4x the bytes) for one hero image.
            Children = [Controls.Artwork(url, ClassicSearchRules.HeroCoverW, ClassicSearchRules.HeroCoverH, Search.RoundArt(hit.Kind) ? ClassicSearchRules.HeroCoverRoundCorners : Radii.Card)],
        };
        var card = new BoxEl
        {
            Height = ClassicSearchRules.HeroHeight, MinWidth = 0f, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = open, ZStack = true, Draggable = Search.DragOf(hit), OnFocusChanged = cardFocus,
            Children =
            [
                scheme is { } graded
                    ? new BoxEl
                    {
                        HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Height = ClassicSearchRules.HeroHeight,
                        Gradient = new GradientSpec(GradientShape.Radial, 0f,
                        [
                            new GradientStop(0f, Design.Palette.ChromeAccent(graded) with { A = ClassicSearchRules.HeroWashAlpha }),
                            new GradientStop(ClassicSearchRules.HeroWashFadeStop, Design.Palette.ChromeAccent(graded) with { A = 0f }),
                        ])
                        {
                            RadialCenter = new Point2(ClassicSearchRules.HeroWashCenterX, ClassicSearchRules.HeroWashCenterY),
                            RadialRadius = new Point2(ClassicSearchRules.HeroWashRadiusX, ClassicSearchRules.HeroWashRadiusY),
                        },
                    }
                    : new BoxEl { HitTestVisible = false },
                new BoxEl { Direction = 0, MinWidth = 0f, Height = ClassicSearchRules.HeroHeight, AlignSelf = FlexAlign.Stretch, Children = [copy, cover] },
            ],
        }.Interactive(Interaction.Card);   // OnClick is set above, so Interactive gives the box the hand (the engine's clickable default)
        return menu is not null && !Controls.IsNullOverlay(ctx.Overlay) ? ContextMenu.Attach(card, ctx.Overlay, menu) : card;
    }

    // ══ BEST MATCHES, THE PLAYLIST RAIL, THE SONGS GRID (W5, W6, W1) ═════════════════════════════════════════════════

    /// <summary>The hits after the Top Result as a width-fitted N×N PagedShelf of hit rows with chevrons and pips.
    /// <paramref name="colsFor"/> is the page's hysteretic column count (<see cref="Search.ColsFor"/> over the host's own fields).
    /// The shelf sits in a wrapper box: a Key on PagedShelf's component root is ignored (§9 traps).</summary>
    static Element BestMatches(IReadOnlyList<Search.HitItem> items, Func<float, int> colsFor, Search.HitContext ctx)
    {
        if (items.Count == 0) return new BoxEl();
        return Stretch(Responsive.Of("search:best-matches", w =>
        {
            int cols = colsFor(w);
            int rows = Search.RowsFor(cols);
            int maxCols = Search.MaxColumns(cols, rows, items.Count);
            return PagedShelf.Create(items,
                cardAt: (item, i, _) => HitRow(item.Ref, item.Flags, large: false, in ctx),
                onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the row inside is click-less
                cardHeight: static _ => Search.BestMatchRowH,
                header: TickHeader(Loc.Get(Strings.Search.BestMatches)),
                pager: ShelfPager.Chevrons | ShelfPager.Pips,
                minCardW: Search.BestMatchCellMin, maxCardW: 9999f, gap: Search.BestMatchGap, rows: rows, maxColumns: maxCols,
                snap: ShelfSnap.Page, cardWidthAgnostic: true, edgeFade: ClassicSearchRules.BestMatchEdgeFade,
                prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
                keyOf: static (item, _) => Search.KeyOf(item.Ref));
        }, fallback: 0f));
    }

    /// <summary>The All tab's playlist rail: 148-188 DIP grid cards with a chevron header that SWITCHES to the Playlists
    /// tab (not a navigation, parity 24), never repeating the hero's own item.</summary>
    static Element PlaylistRail(IReadOnlyList<Search.HitItem> items, Action? openFacet, Search.HitContext ctx)
    {
        if (items.Count == 0) return new BoxEl();
        return Stretch(PagedShelf.Create(items,
            cardAt: (item, i, cardW) => ShelfCardOf(item.Ref, cardW, in ctx),
            onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the card inside is click-less
            cardHeight: static w => SurfaceGeometry.StackExtent(in ClassicSearchRules.RailCard, w, 1f),   // the card's own shape: one-line subtitle
            header: TickHeader(Loc.Get(Strings.Search.Playlists), openFacet),
            pager: ShelfPager.Chevrons | ShelfPager.Pips,
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax, gap: HomeModuleLayout.ShelfGap,
            // NOT cardWidthAgnostic: the shelf card takes its width literally (its column and cover are sized from it), so
            // it needs the fitted width hint — an agnostic shelf hands every card maxCardW and the cards overrun their cells.
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, _) => Search.KeyOf(item.Ref)));
    }

    /// <summary>A shelf card for a hit (the rail): the one surface at the shelf's card width. Inside the shelf's slot it
    /// renders click- and focus-less (the slot owns both); the host attaches the menu carried in the data.</summary>
    static Element ShelfCardOf(EntityRef hit, float cardW, in Search.HitContext ctx)
        => Controls.Surface(Search.CardDataOf(hit, ctx.Origin), ClassicSearchRules.RailCard, cardW);

    /// <summary>The Songs facet: a wrapping AutoGrid(280, 12) of hit rows scrolling with the page — no pager (W6). Its row
    /// height is the tallest cell (auto): a row that wears the "Lyrics match" eyebrow is 72 where the rest are 64.</summary>
    static Element SongsGrid(IReadOnlyList<Search.HitItem> rows, in Search.HitContext ctx)
    {
        var cells = new Element[rows.Count];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = Stretch(HitRow(rows[i].Ref, rows[i].Flags, large: false, in ctx));
        return Stretch(AutoGrid(ClassicSearchRules.SongsColMin, Spacing.M, float.NaN, cells));
    }

    // ══ THE ALBUMS / PLAYLISTS FACET GRID (W7) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The dedicated Albums / Playlists tab: a uniform virtualized card grid over the facet row's OWN edge. The
    /// extent comes from the stated total (capped at <see cref="Search.FacetHitCeiling"/>), so an index whose page has not landed
    /// renders a card-shaped placeholder and scrolling never shifts the rows; the page pages the edge from its demand
    /// (the host), never from the visible window.</summary>
    static Element FacetGrid(Search row, Search.HitContext ctx)
        => Responsive.Of("search:facet-grid", w =>
        {
            float width = w > 0f ? w : HomeModuleLayout.FallbackWidth;
            int cols = Search.FacetGridColumns(width);
            int count = Math.Min(Math.Max(row.ResultCount, row.ResultTotal), Search.FacetHitCeiling);
            var r = row;
            var c = ctx;
            return new VirtualListEl
            {
                ItemCount = count,
                // A square cover + the 50-DIP label chrome; the 4 extra DIP turn the 16 column gap into the 20 row gap.
                ItemLayout = new AspectGridVirtualLayout(cols, 1f, Search.FacetGridChrome + (Search.FacetGridRowGap - Search.FacetGridGap), Search.FacetGridGap),
                RenderItem = i => GridCell(r, i, in c),
                KeyOf = i => r.ResultRef(i) is { IsNone: false } hit ? Search.KeyOf(hit) : "search-card:placeholder:" + i.ToString(CultureInfo.InvariantCulture),
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            } with { Key = "search-facet-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
        }, fallback: HomeModuleLayout.FallbackWidth, grow: 1f);

    /// <summary>One grid cell: the one surface at the grid shape (the host attaches the menu carried in the data) — or, for
    /// an unrealised index, the SAME host on <see cref="Controls.CardData.Seed"/>: exactly a real card's box, so a landing
    /// page shifts nothing and the cell is not a different component type when its hit arrives.</summary>
    static Element GridCell(Search row, int index, in Search.HitContext ctx)
    {
        var hit = row.ResultRef(index);
        return Controls.Surface(hit.IsNone ? Controls.CardData.Seed : Search.CardDataOf(hit, ctx.Origin), Shape.Grid);
    }

    // ══ THE FLAT LISTS: hits, artists (W9, W10) ══════════════════════════════════════════════════════════════════════

    /// <summary>A dedicated facet's hits (Artists, Podcasts, Episodes, Profiles) through the ONE row factory, or that facet's own
    /// empty sentence (parity 36). The Artists facet is the same row with circular art, its subtitle the kind word "Artist" — with the menu, drag and
    /// follow control 0.2.9's thinner row lacked (W10, §9 gap 8).</summary>
    static Element HitsList(IReadOnlyList<Search.HitItem> rows, SearchFacet facet, in Search.HitContext ctx)
    {
        if (rows.Count == 0) return Empty(facet, ctx.Query);
        var cells = new Element[rows.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = HitRow(rows[i].Ref, rows[i].Flags, large: false, in ctx);
        return HitColumn(cells);
    }

    // ══ GENRES AND RELATED QUERIES (W8, W1) ══════════════════════════════════════════════════════════════════════════

    /// <summary>The genre tiles as browse-category LINKS — the directory's own cell, column count and star-grid math, with
    /// every pip the semantic accent (Color null, §0.15). A genre opens its browse page with the search LOOKUP origin (the tile's own verb).
    /// <paramref name="header"/> false is the dedicated Genres tab; an empty list is an empty box, no sentence (parity 38).</summary>
    static Element GenreGrid(IReadOnlyList<BrowseTileModel> tiles, bool header)
        => tiles.Count == 0 ? new BoxEl() : GenreBody(tiles, header);

    /// <summary>The genre section's shimmer: the eight seed names through the same cells.</summary>
    static Element GenreSeed()
    {
        var seeds = ClassicSearchRules.SeedGenres;
        var models = new BrowseTileModel[seeds.Length];
        for (int i = 0; i < models.Length; i++)
            models[i] = new BrowseTileModel(seeds[i], "seed:genre:" + i.ToString(CultureInfo.InvariantCulture), null, null, static () => { });
        return GenreBody(models, header: true);
    }

    static Element GenreBody(IReadOnlyList<BrowseTileModel> models, bool header)
    {
        Element body = Responsive.Of("search:genres", width =>
        {
            int cols = ClassicBrowseTiers.LinkColumns(width > 0f ? width : ClassicBrowseRules.DirectoryFallbackWidth);
            var cells = new Element[models.Count];
            for (int i = 0; i < cells.Length; i++) cells[i] = ClassicBrowseCells.Link(models[i]);
            return ClassicBrowseCells.StarGrid(cols, Spacing.M, Spacing.S, cells) with { Key = "search-genres-grid:" + cols.ToString(CultureInfo.InvariantCulture) };
        }, fallback: ClassicBrowseRules.DirectoryFallbackWidth);
        if (!header) return Stretch(body);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [TickHeader(Loc.Get(Strings.Search.Genres)), body],
        };
    }

    /// <summary>The related queries: lowercase links at the 20/28 rung, Weight 400, accent on hover, the typed query
    /// dropped (parity 27-28; the host dropped it). Committing one is a new search.</summary>
    static Element RelatedQueries(IReadOnlyList<string> queries)
    {
        var links = new List<Element>(queries.Count);
        for (int i = 0; i < queries.Count; i++)
        {
            string committed = queries[i];
            links.Add(new BoxEl
            {
                Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
                Key = "rel:" + committed,
                OnClick = () => Search.OpenRelated(committed),
                Children =
                [
                    R.Type.ModuleHeader(committed.ToLowerInvariant()) with
                    {
                        Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary, Weight = 400,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            });
        }
        if (links.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                TickHeader(Loc.Get(Strings.Search.RelatedSearches)),
                new BoxEl { Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = links.ToArray() },
            ],
        };
    }

    // ══ EMPTY AND ERROR (W11, §9 gaps 1, 2, 9) ═══════════════════════════════════════════════════════════════════════

    /// <summary>A facet with nothing: its own sentence for Audiobooks / Podcasts / Episodes / Profiles / Authors, an
    /// empty box for Genres, and the ONE captioned generic empty for everything else (gap 1 picks the captioned one).</summary>
    static Element Empty(SearchFacet facet, string query)
    {
        if (facet == SearchFacet.Genres) return new BoxEl();
        return Search.EmptyKey(facet) is { } key
            ? Controls.Vacancy(Controls.VacancyVoice.NoMatch, title: Loc.Get(key), subtitle: "")
            : Controls.Vacancy(Controls.VacancyVoice.NoMatch, title: Loc.Get(Strings.Search.NoResults), subtitle: Strings.Search.NoResultsSub(query));
    }

    /// <summary>A section-level failure on the All tab (the genre section and the related queries alike, gap 2): the
    /// compact error vacancy inside the section's own slot.</summary>
    static Element SectionFailed(Action retry)
        => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry);
}
