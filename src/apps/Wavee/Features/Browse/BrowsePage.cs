using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>One browse category page: mica masthead, Home-grammar shelves, related categories as directory link
/// tiles. Which endpoint a drill uses is the route prefix the caller built — a shelf header opens
/// <c>browse-section:</c>, a card opens via <see cref="HomeCardNav"/>.
///
/// <para><b>T8 — scroll ownership + in-place paging.</b> <see cref="BrowsePageLayout"/> decides the body's SHAPE.
/// Shelves mode keeps the page-level <c>ScrollView</c> (this component now owns it — see <see cref="ShelvesBody"/>).
/// A flattened body instead hands scroll to its <c>Virtual.Custom</c> grid (<see cref="HomeModules.SectionGrid"/>),
/// exactly like <see cref="Wavee.HomeSectionPage"/>'s Body: a virtual viewport measures 0 natural height inside a
/// page ScrollView (the cover-shear trap), so the grid must own the scroll container it lives in. A flattened
/// single/concat shelf pages IN PLACE via <see cref="SectionPageState"/> rather than opening
/// <c>browse-section:</c> — see <see cref="Render"/>'s <c>LoadMore</c>.</para></summary>
sealed class BrowsePage : Component
{
    internal sealed record Model(
        string PageUri,
        Action<string, string> OnOpenCategory,
        Action<string> OnOpenFeature,
        Action<string, string?> Go,
        Action<string> Play,
        Action OnExploreAll,
        string RouteName,
        string? RouteArg);
    internal static readonly Context<Model?> Props = new(null);

    /// <summary>The in-place-paging overlay (T8) for a flattened shelf. <paramref name="Base"/> is the
    /// <see cref="UseResource"/> value this overlay was built against — <see cref="Render"/> compares it by
    /// REFERENCE, so a resource refresh (a new page load) silently drops a stale overlay instead of racing it.
    /// <paramref name="Cursor"/>/<paramref name="Exhausted"/> are keyed by section uri (FlattenTwoConcat has no
    /// pager, but FlattenOne's sole shelf still needs exactly one entry). <paramref name="LoadingUri"/> is the
    /// section currently mid-fetch, or null.</summary>
    sealed record SectionPageState(BrowsePageModel Base, BrowsePageModel Current,
        IReadOnlyDictionary<string, int> Cursor, IReadOnlySet<string> Exhausted, string? LoadingUri);

    static readonly IReadOnlyDictionary<string, int> EmptyCursor = new Dictionary<string, int>(StringComparer.Ordinal);
    static readonly IReadOnlySet<string> EmptyExhausted = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Adapts the flattened-shelf paging overlay's per-section <c>LoadingUri</c> string into the
    /// <c>IReadSignal&lt;bool&gt;</c> <see cref="HomeSectionAppendPreloader.Loading"/> expects — there is no
    /// standalone bool signal for "this section is mid-fetch" here (unlike HomeSectionPage's own `loadingMore`),
    /// only the shared <see cref="SectionPageState"/> overlay, so this reads it live rather than duplicating a
    /// second source of truth.</summary>
    sealed class PagedSectionLoadingSignal(Signal<SectionPageState?> paged, string sectionUri) : IReadSignal<bool>
    {
        public bool Value => paged.Value?.LoadingUri == sectionUri;
        public bool Peek() => paged.Peek()?.LoadingUri == sectionUri;
    }

    ActionServices? _acts;
    Action<string, string?, NavOrigin?>? _goOrigin;
    string _pageTitle = "";

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        _acts = UseContext(ActionServices.Slot);
        _goOrigin = UseContext(HistoryStore.GoWithOrigin);
        var overlay = UseContext(Overlay.Service);
        var model = UseContext(Props);
        var navPreview = UseContext(NavPreviewStore.Slot);
        var sectionPreview = UseContext(HomeSectionPreviewStore.Slot);
        var mastheadStore = UseContext(ShellMasthead.Slot);
        // G1: the same eviction problem BrowseDirectoryStore solves for the directory, one level deeper — see
        // BrowsePageStore's own doc comment. `pageStore` may be null (tests, or no provider mounted): every read
        // below falls straight through to today's behaviour in that case.
        var pageStore = UseContext(BrowsePageStore.Slot);
        // The in-place-paging overlay (T8) and its post-hop — same idiom as HomeSectionPage's loadingMore/cursor/
        // exhausted signals, folded into one record because a flattened page pages a SECTION, not the page itself.
        var paged = UseSignal<SectionPageState?>(null);
        var post = UsePost();
        // Infinite-scroll proximity gate for a FlattenOne grid's tail (HomeSectionAppendPreloader) — same idiom as
        // HomeSectionPage's own `nearTail`, driven off the SELF-SCROLLING SectionGrid viewport's scroll geometry
        // (FlattenBody's grid, like HomeSectionPage's, owns its own scroll — see FlattenBody's OnScrollGeometryChanged
        // wiring). Seeded true so a first page shorter than the viewport still fills the screen once; dropped after
        // every append so only a fresh scroll-geometry event continues the chain.
        var nearTail = UseSignal(true);

        // Seed: the last loaded value for THIS pageUri when the store has one (full content immediately, same as
        // BrowseDirectory's `cachedCats`/`cachedCharts`), else the shimmer shape below.
        var cachedSeed = model is not null && pageStore is not null && pageStore.TryGet(model.PageUri, out var cp, out _)
            ? cp
            : null;
        var page = UseResource(
            async ct =>
            {
                if (svc is null || model is null) return EmptyPage;
                return await LoadPageAsync(svc, pageStore, model.PageUri, ct).ConfigureAwait(false);
            },
            seed: cachedSeed ?? SkeletonPage,
            deps: (model?.PageUri ?? "", svc is null ? 0 : 1)).Loadable;

        // T12 (refinement A, verbatim): "for the shimmer, dont include the breadcrumb, the breadcrumb should stay
        // in place as much as possible without any single change — since we know the group title we will navigate
        // to, just load it immediately". The route already carries the destination's title (the tile that opened
        // this page passed it as RouteArg — Model.RouteArg), so the PUBLISH below (G2c-B) carries real content from
        // frame one, entirely OUTSIDE the Skel.Region: only the body shimmers.
        var loadedNow = page.Value.Value;
        // ReferenceEquals: the same T8 overlay-drop rule Body used to apply, just read up here now. G1: a
        // background-refreshed BrowsePageStore only ever affects the NEXT mount's seed (see LoadPageAsync below) — it
        // never touches `loadedNow`/`paged` on THIS mount, so there is no interaction between the store and in-place
        // paging here.
        var effective = paged.Value is { } st && ReferenceEquals(st.Base, loadedNow) ? st.Current : loadedNow;
        var layout = BrowsePageLayout.Of(effective);

        // Page a flattened shelf IN PLACE via browseSection — never navigate to browse-section: for a shelf this
        // page just flattened; that route exists for Shelves mode's own PagedShelf header, not for this grid.
        void LoadMore(string sectionUri)
        {
            if (svc is null) return;
            var current = paged.Value is { } s0 && ReferenceEquals(s0.Base, loadedNow)
                ? s0
                : new SectionPageState(loadedNow, loadedNow, EmptyCursor, EmptyExhausted, null);
            if (current.LoadingUri is not null || current.Exhausted.Contains(sectionUri)) return;

            int cursor;
            if (current.Cursor.TryGetValue(sectionUri, out var c))
            {
                cursor = c;
            }
            else
            {
                // No cursor yet: the base section IS the offset-0 page, so seed from ITS own server cursor (falling
                // back to offset+count vs total only when the server sent no pagingInfo at all — see
                // HomeSectionPaging.BrowseSectionNextOffset's doc comment).
                var baseSection = FindSection(current.Base, sectionUri);
                int? seed = baseSection is null
                    ? null
                    : HomeSectionPaging.BrowseSectionNextOffset(0, baseSection);
                if (seed is null)
                {
                    paged.Value = current with { Exhausted = WithAdded(current.Exhausted, sectionUri) };
                    return;
                }
                cursor = seed.Value;
            }

            paged.Value = current with { LoadingUri = sectionUri };
            _ = LoadMoreAsync(svc, sectionUri, cursor, post, paged, nearTail);
        }

        // Title: known immediately, never waiting on the load. The skeleton page's title is " " (whitespace), so a
        // still-pending page falls back to the route's own arg — the title the opener passed to
        // Go(BrowseRoutes.Page(uri), title) — exactly the fallback DrillTrail itself applies to liveTitle. G2c-B: the
        // trail itself is no longer computed here — the shell's ShellMastheadBand derives it from the ROUTE (this
        // published title is its liveTitle override); see the publish leg below.
        string title = !string.IsNullOrWhiteSpace(loadedNow.Title) ? loadedNow.Title.Trim()
                     : model?.RouteArg ?? "";
        _pageTitle = title;

        // Tools (FlattenOne's own "Show all") — computed from layout/paged exactly as FlattenBody used to; hoisted
        // here because the masthead is a PUBLICATION now (ShellMastheadBand renders the actual button from these
        // primitives — never an Element, see ShellMastheadState).
        bool toolsVisible = false;
        bool toolsLoading = false;
        Action? toolsAction = null;
        if (layout.Mode == BrowsePageLayout.Mode.FlattenOne)
        {
            BrowseSection? primary = null;
            foreach (var s in layout.Sections)
                if (s.Kind == BrowseSectionKind.Shelf) { primary = s; break; }
            if (primary is not null)
            {
                bool loading = paged.Value?.LoadingUri == primary.Uri;
                bool exhausted = paged.Value?.Exhausted.Contains(primary.Uri) ?? false;
                bool canLoadMore = model is not null && !exhausted && BrowsePageLayout.HasMore(primary);
                toolsVisible = canLoadMore;
                toolsLoading = loading;
                toolsAction = canLoadMore ? () => LoadMore(primary.Uri) : null;
            }
        }

        // One deps-leg publication: any value the published state captures is a dep. No owner token, no
        // UseActivation / unmount-clear — an entry for a non-active route is simply never rendered.
        UseEffect(() =>
        {
            if (mastheadStore is null || model is null) return;
            mastheadStore.Publish(model.RouteName, model.RouteArg,
                new ShellMastheadState(title, null, toolsVisible, toolsLoading, toolsAction));
        }, DepKey.From(HashCode.Combine(title, (int)layout.Mode, toolsVisible, toolsLoading)));

        // T12: outer column split — Padding owns the shared FrameX gutters and the overlay masthead reserve (the band
        // no longer takes in-flow height). Spacing.L under that reserve is the same net gap the masthead's own margin
        // used to give. The Skel.Region wraps ONLY the body, so a data load never shimmers or remounts anything above
        // it. Bottom stays the small Spacing.L margin flatten mode always used — Shelves mode's own bottom dock
        // clearance (PlayerDock.Reserve + Spacing.XXL) moves INSIDE its ScrollView's scrolled content instead (see
        // ShelvesBody), so neither mode's effective inset changes.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
            Padding = BrowseMastheadMetrics.FamilyBodyPad(Spacing.L),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    Children =
                    [
                        // The explicit-shimmerSource overload (SkeletonRegion.cs): the real Shelves-mode body
                        // renders its shelves through PagedShelf — a virtualized carousel that measures ZERO rows
                        // until its viewport width lands — so content(seed) cannot BE the shimmer here (see
                        // ShimmerBody's doc comment for the flicker this used to cause). FadeOnly (not None): None
                        // floors shimmer ExitMs at 400ms assuming the body owns its own entrance; this body has none,
                        // so None left the ghost under real content for 400ms.
                        Skel.Region(page,
                            shimmerSource: () => ShimmerBody(model),
                            content: _ => BodyBelow(effective, layout, model, navPreview, sectionPreview, svc, overlay,
                                paged, nearTail, LoadMore),
                            isEmpty: p => p.IsEmpty,
                            onEmpty: () => FramedContent(EmptyBody(model), model),
                            onFailed: () => FramedContent(ErrorState.Build(page.Error), model),
                            reveal: SkelReveal.FadeOnly, smoothResize: false),
                    ],
                },
            ],
        };
    }

    // ── T10 shimmer: a NON-virtualized stand-in for ShelvesBody's geometry ───────────────────────────────────────
    /// <summary>Why this exists instead of the ordinary derive-from-content(seed) skeleton: the real body's shelves
    /// render through <c>PagedShelf</c> (<c>FluentGpu.Controls.PagedShelf</c>) — a virtualized carousel that yields
    /// ZERO rows against an unmeasured viewport — which is exactly the documented case for the explicit-
    /// <c>shimmerSource</c> overload ("a streaming LIST where the seed yields zero rows",
    /// <c>FluentGpu.Hooks.SkeletonRegion.cs</c>'s <c>Skel.Region</c> remarks). Deriving from the seed instead used to
    /// paint only thin text bars, then the real virtual rows + <see cref="Responsive"/>'s measured width (900 →
    /// real) landed and reflowed the pending tree, then the final Ready swap reflowed AGAIN because the skeleton had
    /// never reserved the masthead line or a single card row.
    ///
    /// <para>NO <c>PagedShelf</c>, NO <c>Virtual</c> grid anywhere in THIS tree — every band below is plain, fixed-size
    /// boxes so nothing here measures zero and nothing here reflows on the real width landing. The ONE exception is
    /// <see cref="CategoryBlock"/>'s own <c>Responsive.Of</c> for the Related grid: that primitive carries its own
    /// skeleton derivation (<c>ResponsiveBox</c>'s <c>SkeletonProxy</c>, <c>DeriveRenderedOutput = true</c>) and
    /// self-measures a real width rather than virtualizing rows, so it is not the zero-rows case this method exists
    /// to route around.</para>
    ///
    /// <para>Every block is sized off the SAME constants <see cref="ShelvesBody"/> uses (<see cref="BrowseLayout.Frame"/>,
    /// <see cref="HomeModuleLayout.ShelfCardMin"/>/<see cref="HomeModuleLayout.ShelfCardHeight"/>), so the shimmer→real
    /// swap does not shift the frame, the masthead Y, or a single card's box.</para>
    ///
    /// <para>T12/G2c-B: BODY-ONLY — no masthead placeholder and no FrameTop/FrameX Padding here any more. The
    /// masthead is real from frame one (Render PUBLISHES it — see the shell-masthead leg — entirely outside this
    /// Skel.Region), so this shimmer only stands in for what actually loads: the Related block + two shelf bands.
    /// The bottom PlayerDock clearance stays (it still has to reserve the same room the real body's scrolled
    /// content will).</para></summary>
    static Element ShimmerBody(Model? model) => new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0f, Grow = 1f,
        Padding = new Edges4(0f, 0f, 0f, PlayerDock.Reserve + Spacing.XXL),
        Children =
        [
            ShimmerRelated(),
            ShimmerShelf(),
            ShimmerShelf(),
        ],
    };

    static Element ShimmerRelated()
    {
        var categories = new BrowseCategory[5];
        for (int i = 0; i < categories.Length; i++)
            categories[i] = new BrowseCategory("wavee:skeleton:related:" + i, "Placeholder", null);
        // CategoryBlock already tolerates a null Model (the shared inert BrowseTiles.ToModelNoop) — a shimmer has no
        // navigation host to wire.
        return CategoryBlock(new BrowseSection("", "Related", BrowseSectionKind.Related, [], categories, categories.Length), null);
    }

    static Element ShimmerShelf() => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            WaveeType.ModuleHeader("Shelf title"),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, MinWidth = 0f, ClipToBounds = true,
                Children = ShimmerCards(),
            },
        ],
    };

    static Element[] ShimmerCards()
    {
        var cards = new Element[6];
        for (int i = 0; i < cards.Length; i++) cards[i] = ShimmerCard();
        return cards;
    }

    // The real shelf-card shape (cover + title + subtitle), fixed at the shelf's MIN card width and the real card
    // height — a fixed BoxEl, never PagedShelf's own responsive/virtualized fit.
    static Element ShimmerCard() => new BoxEl
    {
        Width = HomeModuleLayout.ShelfCardMin,
        Height = HomeModuleLayout.ShelfCardHeight(HomeModuleLayout.ShelfCardMin),
        Shrink = 0f, Direction = 1, Gap = Spacing.XS,
        Children =
        [
            new BoxEl
            {
                Width = HomeModuleLayout.ShelfCardMin, Height = HomeModuleLayout.ShelfCardMin,
                Fill = Tok.FillSubtleSecondary, Corners = Radii.CardAll,
            },
            WaveeType.CardTitle("Title text"),
            WaveeType.TrackMeta("Subtitle"),
        ],
    };

    // ── body: mode dispatch (BrowsePageLayout decides the shape; this only wires it up). T12: renamed from `Body` —
    // effective/layout/LoadMore now live in Render (the masthead needs them too, to render OUTSIDE this region), so
    // this is just the switch, and it no longer carries a Header child in any branch. ─────────────────────────────
    Element BodyBelow(BrowsePageModel effective, BrowsePageLayout.Result layout, Model? model,
                      NavPreviewStore? navPreview, HomeSectionPreviewStore? sectionPreview,
                      Services? svc, IOverlayService overlay,
                      Signal<SectionPageState?> paged, Signal<bool> nearTail, Action<string> loadMore)
    {
        return layout.Mode switch
        {
            BrowsePageLayout.Mode.FlattenOne =>
                FlattenBody(effective, layout, model, navPreview, svc, overlay, concat: false, paged, nearTail, loadMore),
            BrowsePageLayout.Mode.FlattenTwoConcat =>
                FlattenBody(effective, layout, model, navPreview, svc, overlay, concat: true, paged, nearTail, loadMore),
            // FlattenTwoStacked — PRAGMATIC DEVIATION (T8, documented in the task plan): two independent virtual
            // grids cannot both own scroll, and folding them into one shared scroller reintroduces the exact
            // 0-height virtual-in-scroll trap this task exists to avoid. Render it as Shelves for now (two
            // headerless PagedShelf carousels, each pageable via its own browse-section: header) — the pure layer
            // still tells FlattenTwoStacked apart from FlattenTwoConcat (BrowsePageLayoutTests pins it), so a later
            // pass can special-case it without another BrowsePageLayout change. Rare in practice: it requires TWO
            // untitled shelves where at least one has more items than its first page.
            _ => ShelvesBody(effective, model, navPreview, sectionPreview),
        };
    }

    // ── Shelves mode: this component now owns the page ScrollView (moved out of BrowsePageHost, T8) ─────────────────
    Element ShelvesBody(BrowsePageModel page, Model? model, NavPreviewStore? navPreview, HomeSectionPreviewStore? sectionPreview)
    {
        // Mirrors BrowsePageHost's former route guard: no model means no identity to frame or scroll-key by.
        if (model is null) return new BoxEl { Grow = 1f };

        var children = new List<Element>(page.Sections.Count + 2);
        foreach (var section in page.Sections)
            if (SectionOf(section, model, navPreview, sectionPreview) is { } el)
                children.Add(el);
        children.Add(ExploreAll(model));

        // T12: the masthead moved OUT to Render's real, non-shimmering column — FrameX/FrameTop are the OUTER
        // column's own Padding now (so these shelves' gutters land under the masthead's, not doubled). Only the
        // bottom PlayerDock clearance stays HERE, inside the scrolled content, so the viewport itself still reaches
        // the pane's bottom edge while the last card clears the dock.
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, PlayerDock.Reserve + Spacing.XXL),
            Children = children.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, ScrollKey = "browse:" + model.PageUri };
    }

    /// <summary>The onEmpty/onFailed arms never run through <see cref="BodyBelow"/>'s mode dispatch (Skel.Region
    /// picks them before BodyBelow is even called), so they get the same scroll shell by hand — body-only, like
    /// every other branch here now (the masthead already frames them from outside).</summary>
    static Element FramedContent(Element content, Model? model)
    {
        if (model is null) return new BoxEl { Grow = 1f, Children = [content] };
        return ScrollView(new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, PlayerDock.Reserve + Spacing.XXL),
            Children = [content],
        }) with { Grow = 1f, MinHeight = 0f, ScrollKey = "browse:" + model.PageUri };
    }

    // ── Flatten modes: HomeSectionPage's exact shape — any CategoryGrid/Related link tiles and the ExploreAll
    // footer pin here rather than scroll (a flattened page's non-shelf sections are one short strip, never long
    // enough to earn their own scroll region) over a grid slot that owns its own scroll. T12: no masthead here any
    // more — Render builds it OUTSIDE this region, from the same layout/paged this method still reads for the
    // grid's own cards/key. ─────────────────────────────────────────────────────────────────────────────────────
    Element FlattenBody(BrowsePageModel effective, BrowsePageLayout.Result layout, Model? model,
                        NavPreviewStore? navPreview, Services? svc, IOverlayService overlay, bool concat,
                        Signal<SectionPageState?> paged, Signal<bool> nearTail, Action<string> loadMore)
    {
        var shelves = new List<BrowseSection>(2);
        var pinnedExtras = new List<Element>();
        foreach (var s in layout.Sections)
        {
            if (s.Kind == BrowseSectionKind.Shelf) shelves.Add(s);
            else if (NonShelfSection(s, model) is { } el) pinnedExtras.Add(el);
        }

        var primary = shelves[0];
        IReadOnlyList<HomeCard> cards;
        string sectionKey;
        // The infinite-scroll auto-page trigger (T8-style: this grid owns its own scroll, mirroring HomeSectionPage's
        // identical wiring) — only for a REAL pageable shelf (FlattenOne). FlattenTwoConcat has no pager at all:
        // BrowsePageLayout only picks it when NEITHER shelf has more to page.
        (Func<FluentGpu.Animation.ScrollGeometry, long> Project, Action<FluentGpu.Animation.ScrollGeometry> Action)? scrollWatch = null;
        Element? preloader = null;

        if (concat)
        {
            // FlattenTwoConcat: one grid over both shelves' cards, no pager — BrowsePageLayout only picks this mode
            // when NEITHER shelf has more to page (BrowsePageLayout.HasMore on both is false).
            var merged = new List<HomeCard>(primary.Cards.Count + (shelves.Count > 1 ? shelves[1].Cards.Count : 0));
            foreach (var s in shelves)
                foreach (var c in s.Cards) merged.Add(HomeBrowseCards.Card(c));
            cards = merged;
            sectionKey = effective.Uri;
        }
        else
        {
            var mapped = new HomeCard[primary.Cards.Count];
            for (int i = 0; i < mapped.Length; i++) mapped[i] = HomeBrowseCards.Card(primary.Cards[i]);
            cards = mapped;
            sectionKey = primary.Uri;
            // The "Show all" tools button for this shelf is computed in Render now (the masthead it rides on lives
            // there) — loadMore is still threaded through here only for the Responsive grid's own Open/paging path.

            bool exhausted = paged.Value?.Exhausted.Contains(primary.Uri) ?? false;
            if (model is not null && !exhausted && BrowsePageLayout.HasMore(primary))
            {
                string primaryUri = primary.Uri;
                scrollWatch = HomeSectionAppendPreloader.NearTailWatch(nearTail);
                int? cursorForKey = paged.Value?.Cursor.TryGetValue(primaryUri, out var c) == true ? c : null;
                preloader = Embed.Comp(() => new HomeSectionAppendPreloader
                {
                    Loading = new PagedSectionLoadingSignal(paged, primaryUri),
                    NearTail = nearTail,
                    Start = () => loadMore(primaryUri),
                }) with { Key = "browse-flatten-append:" + primaryUri + ":" + cursorForKey };
            }
        }

        void Open(HomeCard card)
        {
            if (model is not null) HomeCardNav.Open(card, navPreview, model.Go, model.Play);
        }

        var pinned = new List<Element>(1 + pinnedExtras.Count);
        pinned.AddRange(pinnedExtras);
        pinned.Add(ExploreAll(model));

        // No Padding here — FrameX and the bottom Spacing.L margin are the OUTER column's now (Render), so this
        // grid's gutters land under the masthead's rather than doubling them.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
            Children =
            [
                new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, Children = pinned.ToArray() },
                // Recents'/HomeSectionPage's list slot: Grow=1 + MinHeight=0 in a COLUMN whose parent has a definite
                // height. Without this the Responsive wrapper (and the Virtual.Grid inside it) measure as
                // content-sized; a virtual viewport's natural height is 0, so the grid is given a stub cross-size,
                // Fill/Grid item rects come out shorter than the square covers, and ClipToBounds shears them while
                // the page box still fills the pane (empty mica down to the player).
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    Children =
                    [
                        // grow:1 is LOAD-BEARING. ResponsiveBox defaults grow to 0, which in this COLUMN sizes it to
                        // content. The grid must inherit the slot height above, not hug a zero-height viewport.
                        Responsive.Of(width => HomeModules.SectionGrid(cards, sectionKey, width, Open, svc, _acts, overlay,
                            onScrollGeometryChanged: scrollWatch), fallback: HomeModuleLayout.FallbackWidth, grow: 1f),
                        preloader ?? new BoxEl(),
                    ],
                },
            ],
        };
    }

    static BrowseSection? FindSection(BrowsePageModel page, string uri)
    {
        foreach (var s in page.Sections)
            if (string.Equals(s.Uri, uri, StringComparison.Ordinal)) return s;
        return null;
    }

    static IReadOnlySet<string> WithAdded(IReadOnlySet<string> set, string item)
    {
        var next = new HashSet<string>(set, StringComparer.Ordinal) { item };
        return next;
    }

    static IReadOnlyDictionary<string, int> WithSet(IReadOnlyDictionary<string, int> map, string key, int value)
    {
        var next = new Dictionary<string, int>(map.Count + 1, StringComparer.Ordinal);
        foreach (var kv in map) next[kv.Key] = kv.Value;
        next[key] = value;
        return next;
    }

    static async Task LoadMoreAsync(Services svc, string sectionUri, int offset, Action<Action> post,
                                    Signal<SectionPageState?> paged, Signal<bool> nearTail)
    {
        BrowseSection? result = null;
        IReadOnlyList<BrowseCard>? cards = null;
        int total = 0;
        Exception? error = null;
        try
        {
            result = await svc.Browse.GetSectionAsync(sectionUri, offset).ConfigureAwait(false);
            if (result is { } r)
            {
                cards = r.Cards;
                total = r.Total;
            }
        }
        catch (Exception ex) { error = ex; }
        post(() =>
        {
            // Disarm until the next scroll-geometry event re-confirms the user is still heading down —
            // HomeSectionPage.LoadMoreAsync's identical rule, so only a FRESH scroll continues the auto-page chain.
            nearTail.Value = false;
            // The overlay this fetch was armed against may be gone by the time it lands (a resource refresh
            // replaced Base, or the section state moved on some other way) — never let a stale fetch clobber a
            // newer state.
            if (paged.Peek() is not { } current || !string.Equals(current.LoadingUri, sectionUri, StringComparison.Ordinal))
                return;

            if (cards is null || cards.Count == 0)
            {
                if (error is not null)
                {
                    // Transient: what we have stays visible and the button stays armed, so the user can retry — the
                    // same silent-keep-armed contract as HomeSectionPage.LoadMoreAsync's failure arm.
                    svc.Log?.Event(WaveeLogLevel.Warning, "browse", "browse.section.page.fail",
                        "Browse section paging failed; the loaded section remains visible", sectionUri, ex: error,
                        fields: [WaveeLogField.Of("offset", offset)]);
                    paged.Value = current with { LoadingUri = null };
                    return;
                }
                // The endpoint answered with nothing: disarm rather than leave a button that fetches nothing forever
                // (HomeSectionPaging's identical no-progress rule, on the Browse side of the cursor).
                paged.Value = current with { LoadingUri = null, Exhausted = WithAdded(current.Exhausted, sectionUri) };
                return;
            }

            // G1: `next` is NEVER written to BrowsePageStore. The store holds server offset-0 pages only (what a
            // fresh mount's own GetPageAsync(uri, 0, ct) returns) — an in-place-paged model has extra cards spliced
            // into one section, and caching it would seed a LATER mount with a page shape GetPageAsync itself never
            // produces at offset 0.
            int before = FindSection(current.Current, sectionUri)?.Cards.Count ?? 0;
            var next = current.Current.WithSectionCardsAppended(sectionUri, cards);
            int after = FindSection(next, sectionUri)?.Cards.Count ?? before;
            bool progressed = after > before;
            int? nextOffset = result is null ? null : HomeSectionPaging.BrowseSectionNextOffset(offset, result);

            var exhausted = current.Exhausted;
            var cursor = current.Cursor;
            if (nextOffset is null || !progressed)
            {
                exhausted = WithAdded(current.Exhausted, sectionUri);
            }
            else
            {
                cursor = WithSet(current.Cursor, sectionUri, nextOffset.Value);
            }
            paged.Value = current with { Current = next, Cursor = cursor, Exhausted = exhausted, LoadingUri = null };
        });
    }

    // G1: BrowseDirectory's T11 cache policy, mirrored onto ONE category page keyed by its own pageUri —
    //   FRESH (a cached value younger than BrowseDirectoryStore's shared TTL): return it — no network call at all.
    //   STALE (a cached value past the TTL): return the stale value immediately, so THIS mount paints now instead of
    //          a skeleton, and fire a DETACHED background refresh that fetches + writes the store for the NEXT mount
    //          to read. The refresh never touches this mount's own resource/ct — it must not flip an already-visible
    //          page back to a skeleton or ErrorState on failure, so its own failure is logged and swallowed (see
    //          RefreshPageAsync), never rethrown here.
    //   EMPTY (no cached value — first load of the session for this pageUri): fetch exactly as before, so a
    //          first-load failure with no cache to fall back on still fails LOUDLY into the retry ErrorState (the
    //          existing `?? EmptyPage` + implicit-rethrow contract is untouched); write the store only on a
    //          successful NON-empty result — the skeleton and EmptyPage sentinels are never cached.
    static async Task<BrowsePageModel> LoadPageAsync(
        Services svc, BrowsePageStore? store, string pageUri, System.Threading.CancellationToken ct)
    {
        if (store is not null && store.TryGet(pageUri, out var cached, out var fresh))
        {
            if (fresh) return cached;
            _ = RefreshPageAsync(svc, store, pageUri);   // detached — see policy comment above
            return cached;
        }
        var loaded = await svc.Browse.GetPageAsync(pageUri, 0, ct).ConfigureAwait(false) ?? EmptyPage;
        if (store is not null && !loaded.IsEmpty) store.Set(pageUri, loaded);
        return loaded;
    }

    static async Task RefreshPageAsync(Services svc, BrowsePageStore store, string pageUri)
    {
        try
        {
            // CancellationToken.None: this task outlives the mount that fired it (that is the whole point — the
            // NEXT mount reads whatever it writes), so it must not inherit this mount's resource ct.
            var fresh = await svc.Browse.GetPageAsync(pageUri, 0, System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (fresh is not null && !fresh.IsEmpty) store.Set(pageUri, fresh);
        }
        catch (Exception ex)
        {
            svc.Log?.Event(WaveeLogLevel.Warning, "browse", "browse.page.refresh.fail",
                "browse page background refresh failed", pageUri, ex: ex);
        }
    }

    static Element EmptyBody(Model? model) => new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0f,
        Children =
        [
            EmptyState.Build(Loc.Get(Strings.Browse.Unavailable)),
            ExploreAll(model),
        ],
    };

    Element? SectionOf(BrowseSection s, Model? model,
                       NavPreviewStore? navPreview, HomeSectionPreviewStore? sectionPreview)
    {
        if (s.Kind is BrowseSectionKind.CategoryGrid or BrowseSectionKind.Related)
            return s.Categories.Count == 0 ? null : CategoryBlock(s, model);
        return s.Cards.Count == 0 ? null : Shelf(s, model, navPreview, sectionPreview);
    }

    /// <summary>The CategoryGrid/Related half of <see cref="SectionOf"/>, reused by Flatten mode's pinned column —
    /// Shelf-kind sections there are never rendered this way (their cards feed the ONE grid instead).</summary>
    static Element? NonShelfSection(BrowseSection s, Model? model) =>
        s.Kind is BrowseSectionKind.CategoryGrid or BrowseSectionKind.Related && s.Categories.Count > 0
            ? CategoryBlock(s, model)
            : null;

    Element Shelf(BrowseSection s, Model? model,
                  NavPreviewStore? navPreview, HomeSectionPreviewStore? sectionPreview)
    {
        var cards = s.Cards;
        var mapped = HomeBrowseCards.Section(s, s.Title);
        Action? openHeader = model is null
            ? null
            : () => HomeCardNav.OpenBrowseSection(mapped, navPreview, sectionPreview, model.Go, model.Play,
                origin: new NavOrigin(_pageTitle, model.RouteName, model.RouteArg), goOrigin: _goOrigin);
        var acts = _acts;

        return PagedShelf.Create(
            cards.Count,
            cardAt: (i, w) =>
            {
                var c = cards[i];
                var card = HomeBrowseCards.Card(c);
                var drag = card.Kind is HomeCardKind.Track or HomeCardKind.Episode ? null
                    : Drag.Source(WaveeDragKinds.Resource,
                        () => WaveeResourceDragPayload.ForEntity(WaveeDragKindMap.Of(card.Kind), c.Uri, c.Title,
                                                                 c.Image, acts));
                return MediaCard.Shelf(c.Image, c.Title, c.Subtitle ?? "", c.Uri,
                    onClick: () =>
                    {
                        if (model is not null)
                            HomeCardNav.Open(card, navPreview, model.Go, model.Play);
                    },
                    onPlay: () => model?.Play(c.Uri),
                    cardW: w, drag: drag);
            },
            cardHeight: HomeModuleLayout.ShelfCardHeight,
            // Blank ⇒ no header row: an untitled shelf must not pay PagedShelf's header gap for a row that would
            // render empty.
            header: s.Title is { Length: > 0 } t ? HomeModules.DrillHeader(t, openHeader) : null,
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
            gap: Spacing.M, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            keyOf: i => "browse-shelf-card:" + cards[i].Uri)
            with { Key = "browse-shelf:" + s.Uri };
    }

    static Element CategoryBlock(BrowseSection s, Model? model)
    {
        var items = new BrowseTileModel[s.Categories.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = s.Categories[i];
            items[i] = ToTile(c, model);
        }
        var grid = Responsive.Of(width => BrowseTiles.LinkGrid(items, width), fallback: BrowseLayout.DirectoryFallbackWidth);
        if (s.Title is not { Length: > 0 } title) return grid;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children = [HomeModules.DrillHeader(title, null), grid],
        };
    }

    static BrowseTileModel ToTile(BrowseCategory c, Model? model) =>
        BrowseTiles.ToModel(c, model?.OnOpenCategory, model?.OnOpenFeature);

    static Element ExploreAll(Model? model) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
        FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        OnClick = model is null ? null : () => model.OnExploreAll(),
        Children = [WaveeType.TrackMeta(Loc.Get(Strings.Browse.ExploreAll)) with { Color = Tok.AccentTextPrimary }],
    };

    static readonly BrowsePageModel EmptyPage = new("", null, null, [], 0, null);

    static readonly BrowsePageModel SkeletonPage = new(
        "",
        " ",
        null,
        [
            new BrowseSection("", " ", BrowseSectionKind.Shelf,
            [
                new BrowseCard("", " ", null, null),
                new BrowseCard("", " ", null, null),
                new BrowseCard("", " ", null, null),
            ], [], 3),
            new BrowseSection("", " ", BrowseSectionKind.Related,
                [],
                [
                    new BrowseCategory("spotify:page:skeleton-rel-1", " ", null),
                    new BrowseCategory("spotify:page:skeleton-rel-2", " ", null),
                    new BrowseCategory("spotify:page:skeleton-rel-3", " ", null),
                ], 0),
        ],
        TotalSections: 2,
        NextSectionOffset: null);
}
