// ── Themes/WinUi/Structure/Views/Pages/WinUiSearchBody.cs — the body of Classic's `Pages.Search` (themes plan 6.1, T10a2) ──────────────────
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
// survive are the facet body (the page), the grids and shelves (one key at every tier: "search-genres-grid", "search-facet-grid", "search:rail", "search:best-matches") and — not a data key
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
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>What WinUI's Search facet body draws.</summary>
internal static class WinUiSearchBody
{
    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;

    /// <summary>The facet body for the host's model (read inside the skeleton region's content binding).</summary>
    public static Element Content(SearchPageProps props, WinUiSearchLayout layout, float firstW)
    {
        var m = props.Body();
        var ctx = m.Hits;
        switch (m.Kind)
        {
            case SearchBodyKind.Pending: return WinUiSearchPage.Shimmer(m.Facet, layout);
            case SearchBodyKind.Empty: return Empty(m.Facet, ctx.Query);
            case SearchBodyKind.All: return AllBody(m, props, layout, firstW);
            case SearchBodyKind.Genres: return GenreGrid(m.Genres, header: false, layout);
            case SearchBodyKind.Songs: return SongsGrid(m.Rows, in ctx, layout);
            case SearchBodyKind.FacetGrid: return FacetGrid(m.Row, ctx, layout, firstW);
            default: return HitsList(m.Rows, m.Facet, in ctx, layout);
        }
    }

    /// <summary>The All tab: the Top result and the best matches off the ranked list — or, for an answer that carried none, the fallback interleave — then the
    /// artist rail, the playlist rail, the genre tiles and the related queries. From 900 of content the top result stands beside the list (W-Search: a 360
    /// card, "Songs" at its right); below it they stack (W-Search-Compact).</summary>
    static Element AllBody(SearchBodyModel m, SearchPageProps props, WinUiSearchLayout layout, float firstW)
    {
        var ctx = m.Hits;
        var kids = new List<Element>(6);
        if (m.Hero is { } hero)
        {
            Element top = TopResult(hero.Ref, hero.Flags, in ctx, layout);
            Element? best = m.Best is { Count: > 0 } list ? BestMatches(list, ctx, layout) : null;
            if (layout.Beside && best is not null)
                kids.Add(new BoxEl
                {
                    Key = "search:top-row", Direction = 0, Gap = WinUiSearchTiers.BesideGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Start,
                    Layout = WinUiDiscoveryMotion.Reflow,
                    Children =
                    [
                        new BoxEl { Direction = 1, Width = layout.HeroW, Shrink = 0f, Children = [top] },
                        new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [best.Value] },
                    ],
                });
            else
            {
                kids.Add(top);
                if (best is not null) kids.Add(best.Value);
            }
        }
        else if (m.Fallback is { } fallback) kids.Add(FallbackList(fallback, in ctx, layout));
        else kids.Add(Empty(SearchFacet.All, ctx.Query));

        if (m.ArtistRail.Count > 0) kids.Add(ArtistRail(m.ArtistRail, props.SelectArtists, ctx, layout.Rail, layout.Browse.HeaderToolsH));
        if (m.Rail.Count > 0) kids.Add(PlaylistRail(m.Rail, props.SelectPlaylists, ctx, layout));
        kids.Add(GenreSection(m, props, layout));
        kids.Add(RelatedSection(m, props));
        return new BoxEl { Direction = 1, Gap = WinUiSearchTiers.SectionGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>0.2.9's no-top-hits list: the host's interleave drawn as hit rows (the lead one large).</summary>
    static Element FallbackList(IReadOnlyList<Search.FallbackHit> plan, in Search.HitContext ctx, WinUiSearchLayout layout)
    {
        if (plan.Count == 0) return Empty(SearchFacet.All, ctx.Query);
        var rows = new Element[plan.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = HitRow(plan[i].Hit.Ref, plan[i].Hit.Flags, plan[i].Large, in ctx, layout);
        return HitColumn(rows);
    }

    static Element GenreSection(SearchBodyModel m, SearchPageProps props, WinUiSearchLayout layout)
    {
        switch (m.GenreState)
        {
            case SearchSection.Ready: return GenreGrid(m.Genres, header: true, layout);
            case SearchSection.Failed: return SectionFailed(props.RetryAll);
            case SearchSection.Concluded: return new BoxEl();
            default:
                return new SkelRegionEl(
                    Pending: s_true, Failed: s_false, Content: () => GenreSeed(layout), ShimmerSource: null, OnFailed: null,
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

    /// <summary>A section header (W-Search): the title at the Subtitle rung 20/28 semibold, and "See all" at its end where there is somewhere to go (a tab
    /// switch, not a navigation). 40 high at Compact (the target floor), 32 elsewhere.</summary>
    static Element TickHeader(string title, Action? open = null, float minH = 32f)
    {
        Element label = WinUiBrowseCells.BandHeading(title) with { Grow = 1f, Shrink = 1f };
        if (open is null) return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = minH, AlignSelf = FlexAlign.Stretch, Grow = 1f, Children = [label] };
        return new BoxEl
        {
            Direction = 0, Gap = WinUiHomeRules.HeaderGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = minH, AlignSelf = FlexAlign.Stretch, Grow = 1f,
            Children = [label, HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), open) with { Shrink = 0f }],
        };
    }

    /// <summary>A column that cross-stretches its child to the pane, so a section's ellipsised header receives width.</summary>
    static BoxEl Stretch(Element child) => new() { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [child] };

    // ══ THE HIT ROW (W9, §3 "Search rows") ═══════════════════════════════════════════════════════════════════════════

    /// <summary>THE search row: <see cref="Search.HitData"/> on the one media surface at <see cref="SearchHitRules.ShapeOf"/> — 48
    /// art in the 64 floor (a hidden-artwork track row: the 32 play square), 84 art in the 112 floor when <paramref name="large"/>.
    /// Keyed by the hit's kind + slot, so a result list that reorders carries each row's hover and menu with it.</summary>
    static Element HitRow(EntityRef hit, SearchHitFlags flags, bool large, in Search.HitContext ctx, WinUiSearchLayout layout)
    {
        if (hit.IsNone) return new BoxEl();
        bool hideTrackArt = !WinUiOptions.ArtworkInRows();   // tracked: the region's binding re-runs when the option flips
        // The tier's row: Compact's 44 art in a 60 floor (C-Search-Compact); the large lead row and a hidden-art row keep the media row's own.
        var shape = SearchHitRules.ShapeOf(large, artHidden: !SearchHitRules.ShowsArt(hit.Kind, hideTrackArt));
        if (SearchHitRules.ShowsArt(hit.Kind, hideTrackArt)) shape = layout.RowShape(shape);
        return Controls.Surface(Search.HitData(hit, flags, large, in ctx, hideTrackArt), shape) with { Key = "hit:" + Search.KeyOf(hit) };
    }

    /// <summary>A column of hit rows at the 8-DIP list gap.</summary>
    static Element HitColumn(ReadOnlySpan<Element> rows)
        => new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows.ToArray() };

    // ══ THE TOP RESULT (W4) ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The top-result card (W-Search): the card fill, r8, 20 inset; the hit's cover (124, r4; a circle for a person), the title (Title 28/36 display, the
    /// stacked form's 20/28), the kind line, the match chips and the actions (Play on the accent, the "Go to ..." link, the trailing control, the "..." only
    /// when a menu resolved). Beside the Songs it stands as a column; below 900 it is a row (a 72 cover beside the copy, the actions under). A static function,
    /// never a Component — the hit is read from the edge on every render. The card, its click, its menu and its actions are one in both forms.</summary>
    static Element TopResult(EntityRef hit, SearchHitFlags flags, in Search.HitContext ctx, WinUiSearchLayout layout)
    {
        if (hit.IsNone) return new BoxEl();
        string uri = hit.Id.Text;
        string title = Search.TitleOf(hit);
        string? url = Search.CoverOf(hit);

        var h = hit;
        var origin = ctx.Origin;
        Action open = () => Search.OpenHit(h, origin);
        var menu = Search.MenuOf(hit);

        var actions = new List<Element>(4);
        if (Search.CanPlay(hit.Kind)) actions.Add(Controls.PlayButton(Tok.AccentDefault, () => Search.PlayHit(h), layout.Tier == WidthTier.Compact ? ControlSize.Large : ControlSize.Medium) with { Shrink = 0f });
        if (Search.TrailingOf(hit, uri, title) is { } trailing) actions.Add(trailing);
        // The "..." is a hover/focus REVEAL on the card (the Surface host's own mechanism, which a hand-built card does not get for free): the wrapper is
        // NON-interactive, so the pointer anywhere on the card reveals the button (HoverOpacity); keyboard focus within the card reveals it through the folded
        // focus edges (Controls.CardChromeRules.FocusWithin).
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

        float cover = layout.HeroCover;
        float corners = Search.RoundArt(hit.Kind) ? cover / 2f : Radii.Control;
        Element art = new BoxEl
        {
            Width = cover, Height = cover, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(corners), HitTestVisible = false,
            Children = [Controls.Artwork(url, cover, cover, corners)],
        };
        Element titleEl = (layout.Beside ? R.Type.FoldTitle(title) : R.Type.ModuleHeader(title)) with
        {
            MinWidth = 0f, MaxLines = layout.Beside ? 2 : 1, Wrap = layout.Beside ? TextWrap.Wrap : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
        };
        Element kind = hit.Kind is EntityKind.Artist or EntityKind.User
            ? new TextEl(Search.KindWordOf(hit.Kind, flags))
            {
                Size = WinUiSearchRules.HeroSubtitleSize, LineHeight = WinUiSearchRules.HeroSubtitleLineHeight, Color = Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            }
            : new TextEl(Search.SubtitleOf(hit, flags))
            {
                Size = WinUiSearchRules.HeroSubtitleSize, LineHeight = WinUiSearchRules.HeroSubtitleLineHeight, Color = Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
        Element chipRow = chips.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, MinWidth = 0f, Children = chips.ToArray() };
        Element actionRow = actions.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = actions.ToArray() };

        Element face = layout.Beside
            ? new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = WinUiSearchRules.HeroGap, Padding = Edges4.All(WinUiSearchRules.HeroPad),
                Children =
                [
                    art,
                    new BoxEl { Height = WinUiSearchRules.HeroTitleGap - WinUiSearchRules.HeroGap, HitTestVisible = false },
                    titleEl, kind, chipRow, actionRow,
                ],
            }
            : new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = Spacing.M, Padding = Edges4.All(Spacing.L),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.L, MinWidth = 0f, AlignItems = FlexAlign.Start,
                        Children =
                        [
                            art,
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                                Children = [titleEl, kind, chipRow],
                            },
                        ],
                    },
                    actionRow,
                ],
            };
        var card = new BoxEl
        {
            Key = "search:top-result", MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Direction = 1, Grow = layout.Beside ? 0f : 1f,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = open, Draggable = Search.DragOf(hit), OnFocusChanged = cardFocus,
            Children = [face],
        }.Interactive(Interaction.Card);   // OnClick is set above, so Interactive gives the box the hand (the engine's clickable default)
        Element result = menu is not null && !Controls.IsNullOverlay(ctx.Overlay) ? ContextSheets.Attach(card, ctx.Overlay, menu) : card;
        // The section's header over the card (W-Search: "Top result").
        return new BoxEl
        {
            Direction = 1, Gap = WinUiSearchTiers.HeaderGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [TickHeader(Loc.Get(Strings.Search.TopResult), minH: 32f), result],
        };
    }

    // ══ BEST MATCHES, THE PLAYLIST RAIL, THE SONGS GRID (W5, W6, W1) ═════════════════════════════════════════════════

    /// <summary>The hits after the Top result as a list of hit rows under a "Best matches" header (W-Search's "Songs"): at most six, the one row factory, on
    /// the 4-DIP list gap. Beside the top result it is the column at its right; stacked it follows it at the page's width.</summary>
    static Element BestMatches(IReadOnlyList<Search.HitItem> items, Search.HitContext ctx, WinUiSearchLayout layout)
    {
        int n = Math.Min(items.Count, WinUiSearchTiers.BestRows);
        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = HitRow(items[i].Ref, items[i].Flags, large: false, in ctx, layout);
        return new BoxEl
        {
            Key = WinUiSearchTiers.BestMatchesKey, Direction = 1, Gap = WinUiSearchTiers.HeaderGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                TickHeader(Loc.Get(Strings.Search.BestMatches), minH: layout.Browse.HeaderToolsH),
                new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows },
            ],
        };
    }

    /// <summary>The All tab's playlist rail: 148-188 DIP grid cards with a chevron header that SWITCHES to the Playlists
    /// tab (not a navigation, parity 24), never repeating the hero's own item.</summary>
    static Element PlaylistRail(IReadOnlyList<Search.HitItem> items, Action? openFacet, Search.HitContext ctx, WinUiSearchLayout layout)
    {
        if (items.Count == 0) return new BoxEl();
        var band = layout.Rail;
        var shelf = PagedShelf.Create(items,
            cardAt: (item, i, cardW) => ShelfCardOf(item.Ref, cardW, in ctx),
            onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the card inside is click-less
            cardHeight: static w => SurfaceGeometry.StackExtent(in WinUiSearchRules.RailCard, w, 1f),   // the card's own shape: one-line subtitle
            pager: ShelfPager.None,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW, gap: band.Gap,
            // NOT cardWidthAgnostic: the shelf card takes its width literally (its column and cover are sized from it), so
            // it needs the fitted width hint — an agnostic shelf hands every card maxCardW and the cards overrun their cells.
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, _) => Search.KeyOf(item.Ref))
            // ONE key at every tier: the card band and the pager are live props, so a crossing re-fits the SAME rail (its cards and its page survive).
            with { Key = "search:rail" };
        return RailSection(TickHeader(Loc.Get(Strings.Search.Playlists), openFacet, layout.Browse.HeaderToolsH), shelf);
    }

    /// <summary>The All tab's artist rail (Huge, <c>C-Search-Huge</c>): the playlist rail's shelf of round cards (a person's art is a circle in the card data)
    /// under an "Artists" header that SWITCHES to the Artists tab, never repeating the hero's own artist. The region is keyed WITHOUT the tier (only Huge
    /// draws it, so its frozen band never changes under it) and mounts with the drawer reveal: the sections below ride its edge as it opens or closes.</summary>
    static Element ArtistRail(IReadOnlyList<Search.HitItem> items, Action? openFacet, Search.HitContext ctx, WinUiShelfBand band, float minH = 32f)
        => RailSection(TickHeader(Loc.Get(Search.FacetNameKey(SearchFacet.Artists)), openFacet, minH), PagedShelf.Create(items,
            cardAt: (item, i, cardW) => ShelfCardOf(item.Ref, cardW, in ctx),
            onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the card inside is click-less
            cardHeight: static w => SurfaceGeometry.StackExtent(in WinUiSearchRules.RailCard, w, 1f),
            pager: ShelfPager.None,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW, gap: band.Gap,
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,
            keyOf: static (item, _) => Search.KeyOf(item.Ref))
            with { Key = "search:artists" });

    /// <summary>A rail under its own header row: the title at the start and "See all" at the end of the band (the shelf's own header slot hugs its content).</summary>
    static Element RailSection(Element header, Element shelf) => new BoxEl
    {
        Direction = 1, Gap = WinUiSearchTiers.HeaderGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [header, shelf],
    };

    /// <summary>A shelf card for a hit (the rail): the one surface at the shelf's card width. Inside the shelf's slot it
    /// renders click- and focus-less (the slot owns both); the host attaches the menu carried in the data.</summary>
    static Element ShelfCardOf(EntityRef hit, float cardW, in Search.HitContext ctx)
        => Controls.Surface(Search.CardDataOf(hit, ctx.Origin), WinUiSearchRules.RailCard, cardW);

    /// <summary>The Songs facet: a wrapping AutoGrid(280, 12) of hit rows scrolling with the page — no pager (W6). Its row
    /// height is the tallest cell (auto): a row that wears the "Lyrics match" eyebrow is 72 where the rest are 64.</summary>
    static Element SongsGrid(IReadOnlyList<Search.HitItem> rows, in Search.HitContext ctx, WinUiSearchLayout layout)
    {
        var cells = new Element[rows.Count];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = Stretch(HitRow(rows[i].Ref, rows[i].Flags, large: false, in ctx, layout));
        var grid = AutoGrid(layout.Songs.MinCol, Spacing.M, float.NaN, cells);
        // The tier's column cap (Compact 1, Small 2); Standard and Huge fit the 280 floor to the width.
        return Stretch(layout.Songs.MaxCols == int.MaxValue ? grid : grid with { MaxColumns = layout.Songs.MaxCols });
    }

    // ══ THE ALBUMS / PLAYLISTS FACET GRID (W7) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The dedicated Albums / Playlists tab: a uniform virtualized card grid over the facet row's OWN edge. The
    /// extent comes from the stated total (capped at <see cref="Search.FacetHitCeiling"/>), so an index whose page has not landed
    /// renders a card-shaped placeholder and scrolling never shifts the rows; the page pages the edge from its demand
    /// (the host), never from the visible window.</summary>
    static Element FacetGrid(Search row, Search.HitContext ctx, WinUiSearchLayout layout, float firstW)
        => Responsive.Of("search:facet-grid", w =>
        {
            int cols = layout.FacetGridColumns(w);
            int count = Math.Min(Math.Max(row.ResultCount, row.ResultTotal), Search.FacetHitCeiling);
            var r = row;
            var c = ctx;
            return new VirtualListEl
            {
                ItemCount = count,
                // A square cover + the 50-DIP label chrome; the 4 extra DIP turn the 16 column gap into the 20 row gap. One shared layout per column count
                // (pure geometry), so a re-fit swaps the viewport's layout and keeps the viewport, its scroll offset and its keyed cells.
                ItemLayout = FacetGridLayout(cols),
                RenderItem = i => GridCell(r, i, in c),
                KeyOf = i => r.ResultRef(i) is { IsNone: false } hit ? Search.KeyOf(hit) : "search-card:placeholder:" + i.ToString(CultureInfo.InvariantCulture),
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            } with { Key = "search-facet-grid" };
        }, fallback: firstW, grow: 1f);

    static readonly Dictionary<int, AspectGridVirtualLayout> s_facetGridLayouts = new();

    /// <summary>The facet grid's layout at <paramref name="cols"/> columns: stateless geometry, so one instance per count serves every mount.</summary>
    static AspectGridVirtualLayout FacetGridLayout(int cols)
    {
        if (!s_facetGridLayouts.TryGetValue(cols, out var layout))
            s_facetGridLayouts[cols] = layout = new AspectGridVirtualLayout(cols, 1f, Search.FacetGridChrome + (Search.FacetGridRowGap - Search.FacetGridGap), Search.FacetGridGap);
        return layout;
    }

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
    static Element HitsList(IReadOnlyList<Search.HitItem> rows, SearchFacet facet, in Search.HitContext ctx, WinUiSearchLayout layout)
    {
        if (rows.Count == 0) return Empty(facet, ctx.Query);
        var cells = new Element[rows.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = HitRow(rows[i].Ref, rows[i].Flags, large: false, in ctx, layout);
        return HitColumn(cells);
    }

    // ══ GENRES AND RELATED QUERIES (W8, W1) ══════════════════════════════════════════════════════════════════════════

    /// <summary>The genre tiles as browse-category LINKS — the directory's own cell, column count and star-grid math, with
    /// every pip the semantic accent (Color null, §0.15). A genre opens its browse page with the search LOOKUP origin (the tile's own verb).
    /// <paramref name="header"/> false is the dedicated Genres tab; an empty list is an empty box, no sentence (parity 38).</summary>
    static Element GenreGrid(IReadOnlyList<BrowseTileModel> tiles, bool header, WinUiSearchLayout layout)
        => tiles.Count == 0 ? new BoxEl() : GenreBody(tiles, header, layout);

    /// <summary>The genre section's shimmer: the eight seed names through the same cells.</summary>
    static Element GenreSeed(WinUiSearchLayout layout)
    {
        var seeds = WinUiSearchRules.SeedGenres;
        var models = new BrowseTileModel[seeds.Length];
        for (int i = 0; i < models.Length; i++)
            models[i] = new BrowseTileModel(seeds[i], "seed:genre:" + i.ToString(CultureInfo.InvariantCulture), null, null, static () => { });
        return GenreBody(models, header: true, layout);
    }

    static Element GenreBody(IReadOnlyList<BrowseTileModel> models, bool header, WinUiSearchLayout layout)
    {
        // The genre tiles are the directory's own colour tiles on the tier's columns; one key at every count, so a width change moves the same cells.
        Element body = WinUiBrowseCells.TileGrid(models, layout.Browse, "search-genres-grid");
        if (!header) return Stretch(body);
        return new BoxEl
        {
            Direction = 1, Gap = WinUiSearchTiers.HeaderGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [TickHeader(Loc.Get(Strings.Search.Genres), minH: layout.Browse.HeaderToolsH), body],
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
