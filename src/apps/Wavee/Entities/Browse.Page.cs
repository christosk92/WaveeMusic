// ── Entities/Browse.Page.cs ────────────────────────────────────────────────────────────────────────────────────────
// the Browse directory page and a Browse category page (0.2.9 BrowseDirectoryPage + BrowsePage + BrowsePageHost)
//
// Role: UI
// Owner: P (stream P3)
// Wave: 5
// Budget: 700 lines
// Spec: ch 13 §0.13-14, §1.3-1.5, W15-W16, W18-W22, §6.3, §7 (browse rows), §9 (must-not-simplify 2, 9-12; traps)
//
// ── THE STORES ARE THE TABLES ────────────────────────────────────────────────────────────────────────────────────────
//
// 0.2.9 held the directory and each page in two hand-rolled caches (`BrowseDirectoryStore`, `BrowsePageStore`) with a
// three-way fresh / stale / empty policy, because its page models died with a keep-alive eviction. Both are gone: a page
// demands its whole model ONCE per (row, scope) through `Entities.Ensure` and reads rows. A remount after an eviction
// finds the rows already there and paints full-height content on its first frame, so the keyed scroll offset restores
// against real layout (parity 69, 81).
//
// READINESS IS READ OFF THE COLUMNS, NEVER A LOADABLE: known = the row Knows its group; failed = this page demanded it,
// nothing is in flight and it is still unknown (a terminal failure un-asks the group); anything else is pending. Each
// page mounts ONE `SkelRegionEl` over cached delegates reading fields the render latched (Artist.Page's precedent), so a
// table publish re-renders the page and rebinds the region — it never remounts it.
//
// ── THE DIRECTORY (W15-W18) ──────────────────────────────────────────────────────────────────────────────────────────
//
// The masthead band paints NOTHING (live Mica), so the directory cuts ITSELF at the band's lower edge: an 100-DIP spacer
// ABOVE a node clipped with `ClipBelow(84)`, whose 24-DIP top feather arms only on the clip's engage edge — content
// dissolves into the band, is never guillotined by it, and never softens at rest. `SkelReveal.None`: the bands own the
// entrance (Browse.UI.cs). The Charts band is its OWN component over the chart section rows, so a Featured outage never
// blanks the rest of the directory and a categories outage never blanks the Charts band (two retries, W22).
//
// ── THE CATEGORY PAGE (W19-W22) ──────────────────────────────────────────────────────────────────────────────────────
//
// The masthead title is PUBLISHED, outside the skeleton (`Shell.Mastheads`): until the page's own title lands the band
// already shows the route's arg, so the breadcrumb never shimmers (§9 must-not-simplify 9). `BrowsePageLayout` decides
// the body's SHAPE: Shelves keeps a page ScrollView; a flattened body hands scroll to `HomeModules.SectionGrid`'s own
// virtual viewport inside a Grow-1 / MinHeight-0 slot (a virtual viewport inside a page ScrollView measures 0 and shears
// every cover — must-not-simplify 10). FlattenOne pages IN PLACE through `Home.RequestNextPage` — the masthead "Show all"
// and the silent tail preloader both — never through a navigation. FlattenTwoStacked renders as Shelves (0.2.9's
// documented pragmatic deviation, kept). The shimmer is an explicit, NON-virtual stand-in (a PagedShelf yields zero rows
// against an unmeasured viewport — must-not-simplify 11), revealed FadeOnly.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Browse
{
    // ══ 1. THE DIRECTORY PAGE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>browse</c> route's page (RouteKind.Browse). No props: the directory is one row per scope.</summary>
    // MOUNT POINT (Home.InstallPages — contract §7)
    public static Element DirectoryPageFor(in Shell.Route route)
        => Embed.Comp(static () => new DirectoryPage()) with { Key = "browse-directory" };

    /// <summary>The origin a drill out of the directory hands over: the IA root itself, which is what keeps the trail at
    /// <c>Browse › X</c> rather than <c>Browse › Browse › X</c>.</summary>
    static Shell.NavOrigin DirectoryOrigin => new(Loc.Get(Strings.Browse.HomeTitle), new Shell.Route(Shell.RouteKind.Browse));

    sealed class DirectoryPage : Component
    {
        Scope? _scope;
        Browse _dir;
        bool _demanded, _ready, _failed;
        long _dataKey = long.MinValue;
        Element? _body, _skeleton;
        readonly Signal<bool> _underBand = new(false);

        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Action _demand;
        readonly Action<bool> _onClip;
        readonly Func<int, Element> _chartsBandAt;

        public DirectoryPage()
        {
            _pendingFn = () => !_ready && !_failed;
            _failedFn = () => _failed;
            _contentFn = () => _body ?? new BoxEl();
            _shimmerFn = () => _skeleton ??= DirectorySkeleton();
            _demand = Demand;
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _demand);
            // Written only on the clip's engage / release EDGE, never per scroll frame.
            _onClip = v => { if (_underBand.Peek() != v) _underBand.Value = v; };
            _chartsBandAt = static index => new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Animate = Design.Entrance.Row(index),
                Children = [Embed.Comp(static () => new ChartsBand())],
            };
        }

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;              // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            _ = scope.Browses.Changed.Value;
            _ = scope.Edges.BrowseDirectory.Changed.Value;
            if (!ReferenceEquals(_scope, scope))
            {
                _scope = scope;
                _dir = Entities.BrowseDirectory();                  // allocates the subject row: the page binds before the answer
                _demanded = false;
                _dataKey = long.MinValue;
            }
            UseEffect(_demand, DepKey.From(_dir.Slot, (int)epoch));   // once per scope

            var d = _dir;
            var edges = scope.Edges;
            _ready = d.IsValid && (d.Knows(BrowseFields.Identity) || edges.BrowseDirectory.State(d.Slot) != EdgeState.Unknown);
            _failed = !_ready && _demanded && d.IsValid && scope.Browses.Inflight[d.Slot] == 0;
            if (_ready)
            {
                // The body is rebuilt ONLY when the tile list or a tile row changed — the under-band edge re-render below
                // hands the region the same instance.
                long key = ((long)edges.BrowseDirectory.Version(d.Slot) << 32) | scope.Browses.Changed.Peek();
                if (key != _dataKey)
                {
                    _dataKey = key;
                    var categories = CategoriesOf(edges.BrowseDirectory.Targets(d.Slot));
                    _body = categories.Length == 0
                        ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: "")
                        : DirectoryBody(BrowseTaxonomy.Grouped(categories), live: true, _chartsBandAt);
                }
            }

            // smoothResize false: a page-level region whose branches differ by hundreds of DIP lands its height at once.
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null,
                SmoothResize: false);

            Element directory = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Gap = Spacing.L,
                Padding = BrowseMastheadMetrics.FamilyUnderBandPad(Design.Dock.Reserve + Spacing.XXL),
                EdgeFade = _underBand.Value ? new EdgeFadeSpec(EdgeMask.Top, BrowseMastheadMetrics.ClipFadeBand) : null,
                Children = [region],
            }.ClipBelow(BrowseMastheadMetrics.ClipInset, _onClip);

            return ScrollView(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                // The reserve is a SPACER above the clipped node, not padding inside it: the cut engages exactly when the
                // content reaches the band.
                Children = [new BoxEl { Height = BrowseMastheadMetrics.BodyTop, HitTestVisible = false }, directory],
            }) with { Grow = 1f, MinWidth = 0f, ScrollKey = "browse" };
        }

        void Demand()
        {
            var d = _dir;
            if (!d.IsValid) return;
            Entities.Ensure(d, BrowseFields.All);
            _demanded = true;
        }
    }

    /// <summary>The Charts band (0.2.9 <c>BrowseDirectory.ChartsBand</c>): the SAME Fold deck Home's Charts row renders,
    /// over the SAME five chart section rows, with the deck's own header (no eyebrow above it — parity 64) and no per-tile
    /// eyebrow (parity 65). Pending shimmers the three blank folds; Ready-and-empty is the compact "No charts right now";
    /// Failed is a compact vacancy WITH its own Retry, refetching only the deck (parity 66, 97).</summary>
    sealed class ChartsBand : Component
    {
        HomeLoad _state = HomeLoad.Pending;
        IReadOnlyList<HomeSectionView> _deck = Array.Empty<HomeSectionView>();

        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Action _demand, _openHeader;
        readonly Action<HomeSectionView> _openTile;

        public ChartsBand()
        {
            _demand = HomeBrowseCards.EnsureChartDeck;
            _openTile = static s => HomeCardNav.OpenBrowseSection(s, DirectoryOrigin);
            _openHeader = static () => Shell.GoTo(BrowseTiles.PageRoute(ChartPages.Charts, Loc.Get(Strings.Home.Charts)));
            _pendingFn = () => _state == HomeLoad.Pending;
            _failedFn = () => _state == HomeLoad.Failed;
            _contentFn = () => _deck.Count == 0
                ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Home.ChartsEmpty), subtitle: "")
                : HomeModules.FoldDeck(_deck, Loc.Get(Strings.Browse.Charts), _openTile, _openHeader);
            _shimmerFn = static () => HomeModules.FoldDeck(HomeBrowseCards.ChartDeckSeed, Loc.Get(Strings.Browse.Charts), s_noopSection);
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: _demand);
        }

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Sections.Changed.Value;
            _ = scope.Edges.SectionCards.Changed.Value;
            // The fold covers read their card rows live: a hydrating cover re-describes the tile.
            _ = scope.Playlists.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Shows.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            UseEffect(_demand, DepKey.From((int)epoch));

            // hasLiveCatalog: a page never asks "am I fake" — the seed answers every chart row, so the deck is Ready.
            _deck = HomeBrowseCards.ChartDeck(hasLiveCatalog: true, out _state);
            return new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null,
                SmoothResize: false);
        }
    }

    // ══ 2. THE CATEGORY PAGE ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>browse:&lt;pageUri&gt;</c> route's page (RouteKind.BrowseCategory; the page uri is
    /// <see cref="Shell.Route.Subject"/>, the tile's title the arg). Keyed by the page uri — a different page is a
    /// different subtree (one of ch 13's three surviving key families).</summary>
    // MOUNT POINT (Home.InstallPages — contract §7)
    public static Element CategoryPageFor(in Shell.Route route)
    {
        string uri = route.Subject.Text;
        return Embed.Comp(new CategoryProps(route, uri), static () => new CategoryPage()) with { Key = "browse-page:" + uri };
    }

    sealed record CategoryProps(Shell.Route Route, string PageUri);

    /// <summary>The state the flattened grid's responsive box is gated on: the memoized card list BY REFERENCE, its key,
    /// the DECODED chart bit (never a uri lookup — ch 12 trap 10) and whether the tail watch is armed.</summary>
    sealed record GridState(IReadOnlyList<HomeCard> Cards, string Key, bool Charts, bool Watch);

    sealed class CategoryPage : Component
    {
        static readonly BrowsePageLayout.Result s_noSections = new(BrowsePageLayout.Mode.Shelves, Array.Empty<BrowseSectionFacts>());

        CategoryProps? _props;
        Scope? _scope;
        string _uri = "";
        Browse _page;
        bool _demanded, _known, _failed;
        string _title = "";
        int _factsKey;
        bool _factsBuilt;
        BrowsePageLayout.Result _layout = s_noSections;

        // FlattenOne's primary shelf: the in-place pager's subject.
        Section _primary;
        HomeSectionView _primaryView = HomeSectionView.Empty;
        bool _toolsVisible, _toolsLoading;
        int _tailSlot, _tailCount = -1;

        // FlattenTwoConcat's merged list, memoized on the two view instances so the grid's gate compares by reference.
        HomeSectionView? _concatA, _concatB;
        IReadOnlyList<HomeCard> _concat = Array.Empty<HomeCard>();

        Element? _body;
        IOverlayService? _overlay;
        readonly Signal<bool> _nearTail = new(true);     // seeded true: a first page shorter than the viewport fills once
        readonly AppendLoading _loading = new();
        readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) _tailWatch;

        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _failedPanelFn;
        readonly Action _demand, _demandCards, _publish, _loadMore, _disarmTail;
        readonly Action<HomeCard> _openCard;

        public CategoryPage()
        {
            _tailWatch = HomeSectionAppendPreloader.NearTailWatch(_nearTail);
            _pendingFn = () => !_known && !_failed;
            _failedFn = () => _failed;
            _contentFn = () => _body ?? new BoxEl();
            _demand = Demand;
            _demandCards = DemandCards;
            _publish = Publish;
            _loadMore = LoadMore;
            _disarmTail = DisarmTail;
            _openCard = static c => HomeCardNav.Open(in c);
            // W22 + 0.3's Retry (§9 gap 9): the error arm offers Retry AND the Explore link, inside the scroll frame.
            _failedPanelFn = () => Framed(Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _demand));
        }

        public override Element Render()
        {
            var p = UseProps<CategoryProps>();
            _props = p;
            uint epoch = Entities.ScopeEpoch.Value;              // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Browses.Changed.Value;
            _ = scope.Sections.Changed.Value;
            _ = e.BrowseSections.Changed.Value;
            _ = e.SectionCards.Changed.Value;
            _ = e.SectionCategories.Changed.Value;
            // A shelf card snapshots its title for PagedShelf's value gate, so a hydrating card row must re-render the page.
            _ = scope.Playlists.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Shows.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Episodes.Changed.Value;

            if (!ReferenceEquals(_scope, scope) || !string.Equals(_uri, p.PageUri, StringComparison.Ordinal))
            {
                _scope = scope;
                _uri = p.PageUri;
                // Either spelling resolves to ONE node (a genre uri folds onto its page uri, Browse.cs).
                _page = Browse.IsNodeUri(_uri.AsSpan()) ? Entities.BrowseNode(_uri.AsSpan()) : default;
                _demanded = false;
                _factsBuilt = false;
                _tailCount = -1;
            }
            _overlay = UseContext(Overlay.Service);
            UseEffect(_demand, DepKey.From(_page.Slot, (int)epoch));   // the page model, once per page per scope
            UseEffect(_demandCards);                                      // the shelves' card rows, re-run as bands land

            var b = _page;
            // Title: known immediately, never waiting on the load — the node's title, else the route's own arg.
            string? nodeTitle = b.IsValid && !b.TitleId.IsEmpty ? Entities.Strings.Resolve(b.TitleId) : null;
            _title = !string.IsNullOrWhiteSpace(nodeTitle) ? nodeTitle.Trim() : Shell.ArgOf(p.Route)?.Trim() ?? "";

            // An address that is not a browse node has nothing to load: it reads as the "unavailable" arm at once.
            _known = !b.IsValid || b.Knows(BrowseFields.Page);
            _failed = !_known && _demanded && scope.Browses.Inflight[b.Slot] == 0;
            if (_known) Project(scope);
            else _layout = s_noSections;

            ResolvePrimary();
            UseEffect(_disarmTail, DepKey.From(_primaryView.Cards.Count, _primary.Slot));
            UseEffect(_publish, DepKey.From(
                HashCode.Combine(_title, (int)_layout.Mode), _toolsVisible ? 1 : 0, _toolsLoading ? 1 : 0, _page.Slot));

            _body = _known ? Body() : null;

            // FadeOnly: this body owns no entrance, and None would floor the shimmer's exit at 400 ms and leave a ghost
            // under real content (must-not-simplify 12). smoothResize false (must-not-simplify 2).
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: s_shimmer,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null,
                SmoothResize: false);

            // The outer column owns the frame: the family gutters and the overlay masthead's reserve (the band takes no
            // in-flow height), with Spacing.L under the body; only the region below it ever shimmers.
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Padding = BrowseMastheadMetrics.FamilyBodyPad(Spacing.L),
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [region] },
                ],
            };
        }

        // ── the model ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The page's rule input off its rows: one <see cref="BrowseSectionFacts"/> per answered band, in wire
        /// order — shelf cards counted AFTER the per-section dedupe (<see cref="HomeSectionView.Of"/>), tiles off the
        /// section's category relation — then <see cref="BrowsePageLayout.Of"/>. Rebuilt only when a relation, a section
        /// row or the title moved.</summary>
        void Project(Scope scope)
        {
            var e = scope.Edges;
            var b = _page;
            int key = b.IsValid
                ? HashCode.Combine(e.BrowseSections.Version(b.Slot), scope.Sections.Changed.Peek(), e.SectionCards.Changed.Peek(),
                                   e.SectionCategories.Changed.Peek(), _title)
                : 0;
            if (_factsBuilt && key == _factsKey) return;
            _factsBuilt = true;
            _factsKey = key;
            if (!b.IsValid) { _layout = s_noSections; return; }

            var targets = e.BrowseSections.Targets(b.Slot);
            var facts = new List<BrowseSectionFacts>(targets.Length);
            for (int i = 0; i < targets.Length; i++)
            {
                var s = new Section(targets[i]);
                if (!s.IsValid || !s.Knows(SectionFields.Identity)) continue;
                var kind = BrowsePageLayout.KindOf(s.Kind);
                int cards = kind == BrowseSectionKind.Shelf ? HomeSectionView.Of(s).Cards.Count : 0;
                int tiles = kind == BrowseSectionKind.Shelf ? 0 : e.SectionCategories.Targets(s.Slot).Length;
                string? title = s.TitleId.IsEmpty ? null : Entities.Strings.Resolve(s.TitleId);
                string uri = s.Id.Form == EntityForm.None ? "" : s.Id.Text;
                facts.Add(new BrowseSectionFacts(uri, title, kind, cards, tiles, s.Total, s.Slot));
            }
            _layout = BrowsePageLayout.Of(new BrowsePageFacts(_uri, _title, facts));
        }

        /// <summary>FlattenOne's pager state (0.2.9 <c>BrowsePage.Render</c>'s tools leg): "Show all" is visible while the
        /// shelf has more (<see cref="BrowsePageLayout.HasMore"/>) and the server has not said Complete; it is LOADING
        /// while the next page is asked and has not landed.</summary>
        void ResolvePrimary()
        {
            _primary = default;
            _primaryView = HomeSectionView.Empty;
            _toolsVisible = _toolsLoading = false;
            _loading.Slot = Table.None;
            if (_layout.Mode != BrowsePageLayout.Mode.FlattenOne) return;
            var sections = _layout.Sections;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Kind != BrowseSectionKind.Shelf) continue;
                var f = sections[i];
                _primary = new Section(f.Slot);
                _primaryView = HomeSectionView.Of(_primary);
                _loading.Slot = f.Slot;
                _toolsVisible = _primary.NextOffset != SectionPaging.Complete && BrowsePageLayout.HasMore(in f);
                _toolsLoading = _toolsVisible && _loading.Peek();
                return;
            }
        }

        // ── the body ─────────────────────────────────────────────────────────────────────────────────────────────────

        Element Body()
        {
            if (_layout.Sections.Count == 0) return Framed(EmptyVacancy());
            return _layout.Mode switch
            {
                BrowsePageLayout.Mode.FlattenOne => FlattenBody(concat: false),
                BrowsePageLayout.Mode.FlattenTwoConcat => FlattenBody(concat: true),
                // FlattenTwoStacked — 0.2.9's pragmatic deviation, kept: two virtual grids cannot both own scroll, and one
                // shared scroller re-creates the zero-height trap. The pure rule still tells the two modes apart.
                _ => ShelvesBody(),
            };
        }

        static Element EmptyVacancy()
            => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: "");

        /// <summary>Shelves: the page owns a ScrollView (keyed by the page uri); every section keyed by its own uri, then
        /// the Explore link; the dock clearance lives INSIDE the scrolled content so the viewport reaches the pane edge.</summary>
        Element ShelvesBody()
        {
            var sections = _layout.Sections;
            var kids = new Element[sections.Count + 1];
            for (int i = 0; i < sections.Count; i++)
            {
                var f = sections[i];
                kids[i] = f.Kind == BrowseSectionKind.Shelf ? Shelf(in f) : CategoryBlockOf(in f);
            }
            kids[^1] = ExploreAll(GoDirectory);
            return ScrollView(new BoxEl
            {
                Direction = 1, Gap = Spacing.L, MinWidth = 0f,
                Padding = new Edges4(0f, 0f, 0f, Design.Dock.Reserve + Spacing.XXL),
                Children = kids,
            }) with { Grow = 1f, MinHeight = 0f, ScrollKey = "browse:" + _uri };
        }

        /// <summary>The empty and error arms get the same scroll shell by hand (the region picks them before the mode
        /// dispatch), with the Explore link under the vacancy (parity 79-80).</summary>
        Element Framed(Element content) => ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, Design.Dock.Reserve + Spacing.XXL),
            Children = [content, ExploreAll(GoDirectory)],
        }) with { Grow = 1f, MinHeight = 0f, ScrollKey = "browse:" + _uri };

        /// <summary>One shelf band (0.2.9 <c>BrowsePage.Shelf</c>): a PagedShelf of the house shelf cell — play FAB, card
        /// menu, and a drag source for everything but tracks and episodes. A TITLED shelf's header drills into
        /// <c>browse-section:</c> with this page as the origin; an untitled one has no header row at all (parity 77).</summary>
        Element Shelf(in BrowseSectionFacts f)
        {
            var view = HomeSectionView.Of(new Section(f.Slot));
            var items = new HomeCards.ShelfItem[view.Cards.Count];
            for (int i = 0; i < items.Length; i++)
            {
                var c = view.Cards[i];
                items[i] = HomeCards.ShelfItemOf(in c, HomeCards.PlainText(c.Subtitle), c.Kind == HomeCardKind.Artist);
            }
            var host = _overlay;
            Element? header = null;
            string? shelfTitle = f.Title;
            if (!string.IsNullOrWhiteSpace(shelfTitle) && _props is { } props)
            {
                var origin = new Shell.NavOrigin(_title, props.Route);
                header = HomeModules.DrillHeader(shelfTitle, () => HomeCardNav.OpenBrowseSection(view, origin));
            }
            return PagedShelf.Create<HomeCards.ShelfItem>(items,
                (item, i, cardW) =>
                {
                    var c = item.Card;
                    return HomeCards.ShelfCell(in item, cardW, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                                               HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c));
                },
                cardHeight: HomeModuleLayout.ShelfCardHeight,
                header: header,
                minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
                gap: Spacing.M, edgeFade: HomeModuleLayout.ShelfEdgeFade,
                prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
                keyOf: static (item, i) => "browse-shelf-card:" + item.Card.Uri)
               with { Key = "browse-shelf:" + f.Uri };
        }

        /// <summary>A grid / related band: its category tiles as live link cells under a LABEL header.</summary>
        static Element CategoryBlockOf(in BrowseSectionFacts f)
        {
            var targets = Entities.Current.Edges.SectionCategories.Targets(f.Slot);
            var tiles = new List<BrowseTileModel>(targets.Length);
            for (int i = 0; i < targets.Length; i++)
                if (CategoryAt(targets[i]) is { } c) tiles.Add(BrowseTiles.ModelOf(c, live: true));
            return CategoryBlock(f.Title, tiles) with { Key = "browse-cats:" + f.Uri };
        }

        /// <summary>A flattened body (0.2.9 <c>BrowsePage.FlattenBody</c>): the non-shelf blocks and the Explore link PIN
        /// above a Grow-1 / MinHeight-0 slot whose grid owns its own scroll. FlattenOne pages its shelf in place (the tail
        /// preloader, keyed per cursor — its Key is the prop channel); FlattenTwoConcat merges both shelves, no pager.</summary>
        Element FlattenBody(bool concat)
        {
            var pinned = new List<Element>(3);
            HomeSectionView? first = null, second = null;
            string key = _uri;
            var sections = _layout.Sections;
            for (int i = 0; i < sections.Count; i++)
            {
                var f = sections[i];
                if (f.Kind != BrowseSectionKind.Shelf) { pinned.Add(CategoryBlockOf(in f)); continue; }
                var view = HomeSectionView.Of(new Section(f.Slot));
                if (first is null) { first = view; if (!concat) key = f.Uri; }
                else second ??= view;
            }
            pinned.Add(ExploreAll(GoDirectory));

            IReadOnlyList<HomeCard> cards;
            if (concat) cards = Merge(first, second);
            else cards = first?.Cards ?? (IReadOnlyList<HomeCard>)Array.Empty<HomeCard>();
            bool charts = first is { IsChart: true } && (!concat || second is null || second.IsChart);

            Element preloader = new BoxEl();
            bool watch = !concat && _toolsVisible;
            if (watch)
            {
                string cursor = HomeSectionPaging.NextOffset(_primaryView).ToString(CultureInfo.InvariantCulture);
                preloader = Embed.Comp(() => new HomeSectionAppendPreloader { Loading = _loading, NearTail = _nearTail, Start = _loadMore })
                    with { Key = "browse-flatten-append:" + key + ":" + cursor };
            }

            Element grid = Responsive.Of(new GridState(cards, key, charts, watch), (st, w) => HomeModules.SectionGrid(
                st.Cards, st.Key, w, _openCard, WatchOf(st.Watch), charts: st.Charts), fallback: HomeModuleLayout.FallbackWidth, grow: 1f);

            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
                Children =
                [
                    new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, Children = pinned.ToArray() },
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [grid, preloader] },
                ],
            };
        }

        (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? WatchOf(bool armed)
            => armed ? _tailWatch : null;

        IReadOnlyList<HomeCard> Merge(HomeSectionView? a, HomeSectionView? b)
        {
            if (ReferenceEquals(a, _concatA) && ReferenceEquals(b, _concatB)) return _concat;
            _concatA = a;
            _concatB = b;
            var merged = new List<HomeCard>((a?.Cards.Count ?? 0) + (b?.Cards.Count ?? 0));
            if (a is not null) for (int i = 0; i < a.Cards.Count; i++) merged.Add(a.Cards[i]);
            if (b is not null) for (int i = 0; i < b.Cards.Count; i++) merged.Add(b.Cards[i]);
            _concat = merged;
            return merged;
        }

        // ── the demands and the masthead ─────────────────────────────────────────────────────────────────────────────

        void Demand()
        {
            var b = _page;
            if (!b.IsValid) return;
            Entities.Ensure(b, BrowseFields.All);
            _demanded = true;
        }

        /// <summary>Every shelf band's card rows, in one batch per band (<see cref="Home.EnsureSection"/>, browse routing);
        /// re-runs as the page's bands and an appended page land.</summary>
        void DemandCards()
        {
            _ = Entities.ScopeEpoch.Value;
            var e = Entities.Current.Edges;
            _ = e.BrowseSections.Changed.Value;
            _ = e.SectionCards.Changed.Value;
            var b = _page;
            if (!b.IsValid) return;
            var targets = e.BrowseSections.Targets(b.Slot);
            for (int i = 0; i < targets.Length; i++)
            {
                var s = new Section(targets[i]);
                if (s.IsValid && BrowsePageLayout.KindOf(s.Kind) == BrowseSectionKind.Shelf) Home.EnsureSection(s, browse: true);
            }
        }

        /// <summary>One publication per data change: the live title and FlattenOne's "Show all" (disabled while a page is
        /// in flight, gone when the shelf is exhausted). The band resolves the LATEST delegate at click time.</summary>
        void Publish()
        {
            if (_props is not { } p) return;
            Shell.Mastheads.Publish(p.Route, new Shell.MastheadPublication(
                _title.Length > 0 ? _title : null, _toolsVisible, _toolsLoading, _toolsVisible ? _loadMore : null));
        }

        /// <summary>"Show all" and the tail preloader: the next page of the flattened shelf, IN PLACE (never a navigation).
        /// A failure leaves the loaded cards and the button armed (the relation un-asks the page); an empty answer stops
        /// the pager (parity 102).</summary>
        void LoadMore()
        {
            if (_primary.IsValid) Home.RequestNextPage(_primary, browse: true);
        }

        /// <summary>After an append LANDS, drop the near-tail flag so only a FRESH scroll continues the chain (0.2.9's
        /// post-hop rule, parity 76). The first observation is the baseline, never a disarm.</summary>
        void DisarmTail()
        {
            int slot = _primary.Slot, count = _primaryView.Cards.Count;
            if (slot == _tailSlot && _tailCount >= 0 && count > _tailCount && _nearTail.Peek()) _nearTail.Value = false;
            _tailSlot = slot;
            _tailCount = count;
        }
    }

    /// <summary>"The next page of this shelf is asked and has not landed" as a read signal for the preloader and the
    /// masthead: the relation's ask mark at <c>HomeSectionPaging.NextOffset</c>. Reading <see cref="Value"/> subscribes
    /// the relation, so a landing page (which moves the offset past the mark) re-evaluates it.</summary>
    sealed class AppendLoading : IReadSignal<bool>
    {
        public int Slot;

        public bool Value
        {
            get
            {
                _ = Entities.Current.Edges.SectionCards.Changed.Value;
                return Peek();
            }
        }

        public bool Peek()
        {
            if (Slot <= Table.None) return false;
            var s = new Section(Slot);
            return s.IsValid && Entities.Current.Edges.SectionCards.WasAsked(Slot, HomeSectionPaging.NextOffset(HomeSectionView.Of(s)));
        }
    }

    // ══ 3. THE CATEGORY SHIMMER (W21) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>A NON-virtualized stand-in for the Shelves geometry: a Related block over five placeholder tiles and two
    /// shelf bands of six 148 × 220 cards — no PagedShelf, no virtual grid, every box sized off the same constants the
    /// real body uses, so nothing measures zero and nothing reflows when the real width lands. Body only: the masthead
    /// is real from frame one. Its words are never shown (the deriver paints bars in their place).</summary>
    static readonly Func<Element> s_shimmer = static () => new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0f, Grow = 1f,
        Padding = new Edges4(0f, 0f, 0f, Design.Dock.Reserve + Spacing.XXL),
        Children = [ShimmerRelated(), ShimmerShelf(), ShimmerShelf()],
    };

    static readonly string[] s_shimmerTileUris =
        ["wavee:skeleton:related:0", "wavee:skeleton:related:1", "wavee:skeleton:related:2", "wavee:skeleton:related:3", "wavee:skeleton:related:4"];

    static Element ShimmerRelated()
    {
        var tiles = new BrowseTileModel[s_shimmerTileUris.Length];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = new BrowseTileModel(Loc.Get(Strings.Browse.Genres), s_shimmerTileUris[i], null, null, BrowseTiles.ToModelNoop);
        return CategoryBlock(Loc.Get(Strings.Browse.More), tiles);
    }

    static Element ShimmerShelf()
    {
        var cards = new Element[6];
        for (int i = 0; i < cards.Length; i++) cards[i] = ShimmerCard();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                Design.Type.ModuleHeader(Loc.Get(Strings.Browse.ExploreAll)),
                new BoxEl { Direction = 0, Gap = Spacing.M, MinWidth = 0f, ClipToBounds = true, Children = cards },
            ],
        };
    }

    /// <summary>The real shelf card's shape (cover + title + subtitle) at the shelf's MIN width and the real height.</summary>
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
            Design.Type.CardTitle(Loc.Get(Strings.Browse.Genres)),
            Design.Type.TrackMeta(Loc.Get(Strings.Browse.Charts)),
        ],
    };
}
