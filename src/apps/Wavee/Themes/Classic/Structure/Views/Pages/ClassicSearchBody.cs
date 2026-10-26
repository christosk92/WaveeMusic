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
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>What Wavee Classic's Search facet body draws.</summary>
internal static class ClassicSearchBody
{
    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;

    /// <summary>The facet body for the host's model (read inside the skeleton region's content binding).</summary>
    public static Element Content(SearchPageProps props, ClassicSearchLayout layout, float firstW)
    {
        var m = props.Body();
        var ctx = m.Hits;
        switch (m.Kind)
        {
            case SearchBodyKind.Pending: return ClassicSearchPage.Shimmer(m.Facet, layout);
            case SearchBodyKind.Empty: return Empty(m.Facet, ctx.Query);
            case SearchBodyKind.All: return AllBody(m, props, layout, firstW);
            case SearchBodyKind.Genres: return GenreGrid(m.Genres, header: false, layout);
            case SearchBodyKind.Songs: return SongsGrid(m.Rows, in ctx, layout);
            case SearchBodyKind.FacetGrid: return FacetGrid(m.Row, ctx, layout, firstW);
            default: return HitsList(m.Rows, m.Facet, in ctx, layout);
        }
    }

    /// <summary>The All tab (W1): the Top Result and Best matches off the ranked list — or, for an answer that carried
    /// none, the fallback interleave — then the playlist rail, the genre links and the related queries.</summary>
    static Element AllBody(SearchBodyModel m, SearchPageProps props, ClassicSearchLayout layout, float firstW)
    {
        var ctx = m.Hits;
        var kids = new List<Element>(6);
        if (m.Hero is { } hero)
        {
            Element top = TopResult(hero.Ref, hero.Flags, in ctx, layout);
            bool beside = !float.IsNaN(layout.HeroBesideW);
            if (m.Best is { } best)
            {
                // Huge (C-Search-Huge): the top result is 700 wide with Best matches beside it, filling the rest of the row.
                float bestW = beside ? MathF.Max(0f, firstW - layout.HeroBesideW - ClassicSearchTiers.BesideGap) : firstW;
                Element bestMatches = BestMatches(best, props.ColsFor, ctx, layout, bestW);
                if (beside)
                    kids.Add(new BoxEl
                    {
                        Direction = 0, Gap = ClassicSearchTiers.BesideGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                        Children =
                        [
                            new BoxEl { Direction = 1, Width = layout.HeroBesideW, Shrink = 0f, Children = [top] },
                            new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [bestMatches] },
                        ],
                    });
                else { kids.Add(top); kids.Add(bestMatches); }
            }
            else kids.Add(top);
        }
        else if (m.Fallback is { } fallback) kids.Add(FallbackList(fallback, in ctx, layout));
        else kids.Add(Empty(SearchFacet.All, ctx.Query));

        if (m.Rail.Count > 0) kids.Add(PlaylistRail(m.Rail, props.SelectPlaylists, ctx, layout));
        // The artist rail (Huge, C-Search-Huge): keyed apart from the tier, it reveals on a crossing into Huge and collapses on the way out.
        if (layout.ArtistRail is { } artistBand && m.ArtistRail.Count > 0) kids.Add(ArtistRail(m.ArtistRail, props.SelectArtists, ctx, artistBand));
        // Genres (two parts) and Related searches (one) share a row at Huge.
        Element genres = GenreSection(m, props, layout), related = RelatedSection(m, props);
        if (layout.GenresBeside)
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = ClassicSearchTiers.BesideGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 2f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [genres] },
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [related] },
                ],
            });
        else { kids.Add(genres); kids.Add(related); }
        return new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>0.2.9's no-top-hits list: the host's interleave drawn as hit rows (the lead one large).</summary>
    static Element FallbackList(IReadOnlyList<Search.FallbackHit> plan, in Search.HitContext ctx, ClassicSearchLayout layout)
    {
        if (plan.Count == 0) return Empty(SearchFacet.All, ctx.Query);
        var rows = new Element[plan.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = HitRow(plan[i].Hit.Ref, plan[i].Hit.Flags, plan[i].Large, in ctx, layout);
        return HitColumn(rows);
    }

    static Element GenreSection(SearchBodyModel m, SearchPageProps props, ClassicSearchLayout layout)
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
    static Element HitRow(EntityRef hit, SearchHitFlags flags, bool large, in Search.HitContext ctx, ClassicSearchLayout layout)
    {
        if (hit.IsNone) return new BoxEl();
        bool hideTrackArt = ClassicOptions.HideTrackArt();   // tracked: the region's binding re-runs when the option flips
        // The tier's row: Compact's 44 art in a 60 floor (C-Search-Compact); the large lead row and a hidden-art row keep the media row's own.
        var shape = SearchHitRules.ShapeOf(large, artHidden: !SearchHitRules.ShowsArt(hit.Kind, hideTrackArt));
        if (SearchHitRules.ShowsArt(hit.Kind, hideTrackArt)) shape = layout.RowShape(shape);
        return Controls.Surface(Search.HitData(hit, flags, large, in ctx, hideTrackArt), shape) with { Key = "hit:" + Search.KeyOf(hit) };
    }

    /// <summary>A column of hit rows at the 8-DIP list gap.</summary>
    static Element HitColumn(ReadOnlySpan<Element> rows)
        => new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = rows.ToArray() };

    // ══ THE TOP RESULT (W4) ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 228-DIP top-result card: copy left, a 300×260 cover cropped off the right at −2.5°, a radial wash of
    /// the cover's own chrome grading (none when ungradeable), Play on that accent, Open page for a destination, the
    /// trailing control and the "…" only when a menu resolved. A static function, never a Component — the hit is read
    /// from the edge on every render (§1.5). The tier picks the face (<see cref="ClassicSearchHeroForm"/>): the tuned card, or at Huge the avatar form
    /// under its own "Top result" header (<c>C-Search-Huge</c>); the card itself, its wash, its click, menu and actions are one in both.</summary>
    static Element TopResult(EntityRef hit, SearchHitFlags flags, in Search.HitContext ctx, ClassicSearchLayout layout)
    {
        if (hit.IsNone) return new BoxEl();
        string uri = hit.Id.Text;
        string title = Search.TitleOf(hit);
        string? url = Search.CoverOf(hit);
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;                 // a landed grading re-renders the page
        Scheme? scheme = url is { Length: > 0 } ? Design.ChromeSchemeFor(url) : null;
        // Play takes the cover's accent unless Classic's "Accent from artwork" is off (CT9 U2: the system accent).
        ColorF accent = scheme is { } s && ClassicOptions.AccentFromArtwork() ? Design.Palette.Floor(Design.Palette.ChromeAccent(s)) : Tok.AccentDefault;

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

        bool avatar = layout.HeroForm == ClassicSearchHeroForm.Avatar;
        Element[] face = avatar
            ? AvatarFace(hit, flags, title, url, chips, actions, layout.HeroH)
            : [CardFace(hit, flags, title, url, chips, actions, layout.HeroH)];
        var card = new BoxEl
        {
            Height = layout.HeroH, MinWidth = 0f, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = open, ZStack = true, Draggable = Search.DragOf(hit), OnFocusChanged = cardFocus,
            Children =
            [
                scheme is { } graded
                    ? new BoxEl
                    {
                        HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Height = layout.HeroH,
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
                .. face,
            ],
        }.Interactive(Interaction.Card);   // OnClick is set above, so Interactive gives the box the hand (the engine's clickable default)
        Element result = menu is not null && !Controls.IsNullOverlay(ctx.Overlay) ? ContextSheets.Attach(card, ctx.Overlay, menu) : card;
        if (!avatar) return result;
        // The avatar form wears its own "Top result" header over the card (C-Search-Huge), level with Best matches' header beside it.
        return new BoxEl
        {
            Key = "search:top-result", Direction = 1, Gap = ClassicSearchTiers.AvatarHeaderGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [TickHeader(Loc.Get(Strings.Search.TopResult)), result],
        };
    }

    /// <summary>The tuned card's face (Compact to Standard): the eyebrow and the copy left, the cover cropped off the right at -2.5 degrees.</summary>
    static Element CardFace(EntityRef hit, SearchHitFlags flags, string title, string? url, List<Element> chips, List<Element> actions, float height)
    {
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
        return new BoxEl { Direction = 0, MinWidth = 0f, Height = height, AlignSelf = FlexAlign.Stretch, Children = [copy, cover] };
    }

    /// <summary>The avatar form's face (Huge, <c>C-Search-Huge</c>): two layers over the wash. The copy column, inset 28: the hit's 128 art at the top left (a
    /// circle for a person, the card radius otherwise), the 40/52 title under it, the kind line and the match chips. Over it a pass-through layer holds the
    /// actions at the card's bottom right, so the copy and the buttons never push each other out of the 300-tall card.</summary>
    static Element[] AvatarFace(EntityRef hit, SearchHitFlags flags, string title, string? url, List<Element> chips, List<Element> actions, float height)
    {
        float art = ClassicSearchTiers.AvatarArt;
        float corners = Search.RoundArt(hit.Kind) ? art / 2f : Radii.Card;
        float pad = ClassicSearchTiers.AvatarPad;
        var copy = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = ClassicSearchTiers.AvatarLineGap, Justify = FlexJustify.Start, AlignItems = FlexAlign.Start,
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Height = height, HitTestVisible = false,
            Padding = new Edges4(pad, pad, pad, pad),
            Children =
            [
                new BoxEl
                {
                    Width = art, Height = art, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(corners),
                    Margin = new Edges4(0f, 0f, 0f, ClassicSearchTiers.AvatarTitleGap),
                    Children = [Controls.Artwork(url, art, art, corners)],
                },
                R.Type.HeroTitle(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, AlignSelf = FlexAlign.Stretch },
                new TextEl(Search.SubtitleOf(hit, flags))
                {
                    Size = ClassicSearchTiers.AvatarMetaSize, LineHeight = ClassicSearchTiers.AvatarMetaLineHeight, Color = Tok.TextSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                chips.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, MinWidth = 0f, Children = chips.ToArray() },
            ],
        };
        var tools = new BoxEl
        {
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestPassThrough = true,
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Height = height,
            Padding = new Edges4(pad, pad, pad, pad),
            Children = actions.Count == 0 ? [] : [new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = actions.ToArray() }],
        };
        return [copy, tools];
    }

    // ══ BEST MATCHES, THE PLAYLIST RAIL, THE SONGS GRID (W5, W6, W1) ═════════════════════════════════════════════════

    /// <summary>The hits after the Top Result as a width-fitted N×N PagedShelf of hit rows with chevrons and pips.
    /// <paramref name="colsFor"/> is the page's hysteretic column count (<see cref="Search.ColsFor"/> over the host's own fields).
    /// The shelf sits in a wrapper box: a Key on PagedShelf's component root is ignored (§9 traps).
    /// <para>A crossing re-fits the SAME shelf and the SAME rows (plan 3.3; one key at every tier): the column cap, the rows per page
    /// (<see cref="ClassicSearchLayout.BestMatchRowsFor"/>: three at one column, two at two, three at three) and the row height
    /// (<see cref="ClassicSearchLayout.BestMatchRowH"/>: Compact's 60, the tuned 64) are live props of the mounted shelf (a re-paged shelf keeps the page
    /// holding its first item), and each row is a <see cref="BestMatchRowHost"/> that draws at the tier it is drawn in (Compact's 44 art in a 60 row, the
    /// tuned 48 in 64). The wrapper reflows (position and size, its size revealed), so the shelf's new height glides.</para></summary>
    static Element BestMatches(IReadOnlyList<Search.HitItem> items, Func<float, int> colsFor, Search.HitContext ctx, ClassicSearchLayout layout, float firstW)
    {
        if (items.Count == 0) return new BoxEl();
        var rowH = BestMatchHeight(layout.BestMatchRowH);
        return Stretch(Responsive.Of(ClassicSearchTiers.BestMatchesKey, w =>
        {
            int cols = colsFor(w);
            int rows = layout.BestMatchRowsFor(cols);   // live: a crossing re-pages the mounted shelf
            int maxCols = Search.MaxColumns(cols, rows, items.Count);
            return PagedShelf.Create(items,
                cardAt: (item, i, _) => BestMatchRow(item, ctx, layout),
                onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the row inside is click-less
                cardHeight: rowH,
                header: TickHeader(Loc.Get(Strings.Search.BestMatches)),
                pager: ShelfPager.Chevrons | ShelfPager.Pips,
                minCardW: Search.BestMatchCellMin, maxCardW: 9999f, gap: Search.BestMatchGap, rows: rows, maxColumns: maxCols,
                snap: ShelfSnap.Page, cardWidthAgnostic: true, edgeFade: ClassicSearchRules.BestMatchEdgeFade,
                prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
                parts: layout.PagerEndInset > 0f ? PagerEndParts(layout.PagerEndInset) : null,
                keyOf: static (item, _) => Search.KeyOf(item.Ref));
        }, fallback: firstW)) with { Layout = ClassicTierMotion.Reflow };
    }

    /// <summary>The pager's next chevron stands <paramref name="inset"/> in from the shelf's end (<see cref="ClassicSearchLayout.PagerEndInset"/>), its touch
    /// slop reaching left into the gap before it (the scroll bar's lane takes the side past its end).</summary>
    static TemplateParts PagerEndParts(float inset)
    {
        var parts = new TemplateParts();
        parts[PagedShelf.PartChevronNext] = b => b with { Margin = new Edges4(0f, 0f, inset, 0f), HitSlop = Wavee.Theming.TouchTargetPolicy.OneSide(b.Width.Value, left: true) };
        return parts;
    }

    static readonly Func<float, float> s_bestMatchHeight = static _ => Search.BestMatchRowH;
    static readonly Dictionary<float, Func<float, float>> s_bestMatchHeights = new();

    /// <summary>The shelf's row extent for a row height, one cached delegate per height: the shelf takes its card height as a live prop, and a fresh
    /// closure per render would read as a new height function on every push.</summary>
    static Func<float, float> BestMatchHeight(float rowH)
    {
        if (rowH == Search.BestMatchRowH) return s_bestMatchHeight;
        if (!s_bestMatchHeights.TryGetValue(rowH, out var height))
            s_bestMatchHeights[rowH] = height = _ => rowH;
        return height;
    }

    /// <summary>One Best matches row: a <see cref="BestMatchRowHost"/> (it reads the tier it is drawn in). Its skeleton is the row at the template's record.</summary>
    static Element BestMatchRow(Search.HitItem item, Search.HitContext ctx, ClassicSearchLayout layout)
        => Embed.Comp(new BestMatchRowProps(item, ctx), static () => new BestMatchRowHost())
            with { SkeletonProxy = () => HitRow(item.Ref, item.Flags, large: false, in ctx, layout) };

    /// <summary>A Best matches row's props: the hit (its version re-renders a hydrated row) and the hit context, both by value.</summary>
    sealed record BestMatchRowProps(Search.HitItem Item, Search.HitContext Ctx);

    /// <summary>A Best matches row that follows a tier crossing. A shelf re-renders a card only when its ITEM changes, so a row built from the template's
    /// record would keep the tier it mounted in; this host reads the facts its root provides (<c>TierContext.DrawnIn</c>: the read subscribes it, so a
    /// crossing re-renders it), never the process's, and draws <see cref="HitRow"/> at that tier's record: the SAME surface node (keyed by its hit),
    /// re-formed in place.</summary>
    sealed class BestMatchRowHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<BestMatchRowProps>();
            var tiers = global::Wavee.Theming.TierContext.DrawnIn(UseContext(global::Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            var ctx = p.Ctx;
            return HitRow(p.Item.Ref, p.Item.Flags, large: false, in ctx, ClassicSearchTiers.Of(tiers));
        }
    }

    /// <summary>The All tab's playlist rail: 148-188 DIP grid cards with a chevron header that SWITCHES to the Playlists
    /// tab (not a navigation, parity 24), never repeating the hero's own item.</summary>
    static Element PlaylistRail(IReadOnlyList<Search.HitItem> items, Action? openFacet, Search.HitContext ctx, ClassicSearchLayout layout)
    {
        if (items.Count == 0) return new BoxEl();
        var band = layout.Rail;
        return Stretch(PagedShelf.Create(items,
            cardAt: (item, i, cardW) => ShelfCardOf(item.Ref, cardW, in ctx),
            onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the card inside is click-less
            cardHeight: static w => SurfaceGeometry.StackExtent(in ClassicSearchRules.RailCard, w, 1f),   // the card's own shape: one-line subtitle
            header: TickHeader(Loc.Get(Strings.Search.Playlists), openFacet),
            pager: band.Pager ? ShelfPager.Chevrons | ShelfPager.Pips : ShelfPager.None,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW, gap: band.Gap,
            // NOT cardWidthAgnostic: the shelf card takes its width literally (its column and cover are sized from it), so
            // it needs the fitted width hint — an agnostic shelf hands every card maxCardW and the cards overrun their cells.
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, _) => Search.KeyOf(item.Ref)))
            // ONE key at every tier: the card band and the pager are live props, so a crossing re-fits the SAME rail (its cards and its page survive).
            with { Key = "search:rail" };
    }

    /// <summary>The All tab's artist rail (Huge, <c>C-Search-Huge</c>): the playlist rail's shelf of round cards (a person's art is a circle in the card data)
    /// under an "Artists" header that SWITCHES to the Artists tab, never repeating the hero's own artist. The region is keyed WITHOUT the tier (only Huge
    /// draws it, so its frozen band never changes under it) and mounts with the drawer reveal: the sections below ride its edge as it opens or closes.</summary>
    static Element ArtistRail(IReadOnlyList<Search.HitItem> items, Action? openFacet, Search.HitContext ctx, ClassicShelfBand band)
        => Stretch(PagedShelf.Create(items,
            cardAt: (item, i, cardW) => ShelfCardOf(item.Ref, cardW, in ctx),
            onInvoke: (item, _) => Search.OpenHit(item.Ref, ctx.Origin),   // the slot owns the click; the card inside is click-less
            cardHeight: static w => SurfaceGeometry.StackExtent(in ClassicSearchRules.RailCard, w, 1f),
            header: TickHeader(Loc.Get(Search.FacetNameKey(SearchFacet.Artists)), openFacet),
            pager: band.Pager ? ShelfPager.Chevrons | ShelfPager.Pips : ShelfPager.None,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW, gap: band.Gap,
            snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,
            keyOf: static (item, _) => Search.KeyOf(item.Ref)))
            with { Key = "search:artists", Animate = Design.Reveal.Drawer };

    /// <summary>A shelf card for a hit (the rail): the one surface at the shelf's card width. Inside the shelf's slot it
    /// renders click- and focus-less (the slot owns both); the host attaches the menu carried in the data.</summary>
    static Element ShelfCardOf(EntityRef hit, float cardW, in Search.HitContext ctx)
        => Controls.Surface(Search.CardDataOf(hit, ctx.Origin), ClassicSearchRules.RailCard, cardW);

    /// <summary>The Songs facet: a wrapping AutoGrid(280, 12) of hit rows scrolling with the page — no pager (W6). Its row
    /// height is the tallest cell (auto): a row that wears the "Lyrics match" eyebrow is 72 where the rest are 64.</summary>
    static Element SongsGrid(IReadOnlyList<Search.HitItem> rows, in Search.HitContext ctx, ClassicSearchLayout layout)
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
    static Element FacetGrid(Search row, Search.HitContext ctx, ClassicSearchLayout layout, float firstW)
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
    static Element HitsList(IReadOnlyList<Search.HitItem> rows, SearchFacet facet, in Search.HitContext ctx, ClassicSearchLayout layout)
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
    static Element GenreGrid(IReadOnlyList<BrowseTileModel> tiles, bool header, ClassicSearchLayout layout)
        => tiles.Count == 0 ? new BoxEl() : GenreBody(tiles, header, layout);

    /// <summary>The genre section's shimmer: the eight seed names through the same cells.</summary>
    static Element GenreSeed(ClassicSearchLayout layout)
    {
        var seeds = ClassicSearchRules.SeedGenres;
        var models = new BrowseTileModel[seeds.Length];
        for (int i = 0; i < models.Length; i++)
            models[i] = new BrowseTileModel(seeds[i], "seed:genre:" + i.ToString(CultureInfo.InvariantCulture), null, null, static () => { });
        return GenreBody(models, header: true, layout);
    }

    static Element GenreBody(IReadOnlyList<BrowseTileModel> models, bool header, ClassicSearchLayout layout)
    {
        // The genre links are the directory's own cell; the column count is the tier's fixed one (2 / 2 / 3 / 6) — no width read, no Responsive box.
        var browse = ClassicBrowseTiers.For(layout.Tier);
        int cols = layout.Genres.Columns(0f);
        var cells = new Element[models.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = ClassicBrowseCells.Link(models[i], browse);
        // One key at every column count: a crossing moves the same link cells into their new tracks.
        Element body = ClassicBrowseCells.StarGrid(cols, Spacing.M, browse.LinkRowGap, cells) with { Key = "search-genres-grid" };
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
