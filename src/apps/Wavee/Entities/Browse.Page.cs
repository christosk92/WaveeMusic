// ── Entities/Browse.Page.cs ────────────────────────────────────────────────────────────────────────────────────────
// the Browse directory page, its Charts band and a Browse category page: the CORE HOSTS (0.2.9 BrowseDirectoryPage + BrowsePage +
// BrowsePageHost); T10a2 moved the pictures to Classic (`Pages.Browse`, `Browse.Charts`, `Pages.BrowseCategory`)
//
// Role: UI (core hosts)
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
// host publishes ONE `BrowseLoad` verdict and the view mounts ONE `SkelRegionEl` over it (Artist.Page's precedent), so a
// table publish re-renders the host and rebinds the region — it never remounts it.
//
// ── THE DIRECTORY (W15-W18) ──────────────────────────────────────────────────────────────────────────────────────────
//
// The masthead band paints NOTHING (live Mica), so the directory view cuts ITSELF at the band's lower edge: an 100-DIP spacer
// ABOVE a node clipped with `StickyClip(84)`, whose 24-DIP top feather arms only on the clip's engage edge — content
// dissolves into the band, is never guillotined by it, and never softens at rest. `SkelReveal.None`: the bands own the
// entrance (`ClassicBrowseDirectory`). The Charts band is its OWN host over the chart section rows, so a Featured outage never
// blanks the rest of the directory and a categories outage never blanks the Charts band (two retries, W22). The host keeps the
// memoized band list (`BrowseTaxonomy.Grouped` over the tile rows), rebuilt ONLY when the tile list or a tile row changed.
//
// ── THE CATEGORY PAGE (W19-W22) ──────────────────────────────────────────────────────────────────────────────────────
//
// The masthead title is PUBLISHED, outside the skeleton (`Shell.Mastheads`): until the page's own title lands the band
// already shows the route's arg, so the breadcrumb never shimmers (§9 must-not-simplify 9). `BrowsePageLayout` decides
// the body's SHAPE and the host hands it to the view with each band's rows: Shelves keeps a page ScrollView; a flattened body hands
// scroll to `HomeModules.SectionGrid`'s own virtual viewport inside a Grow-1 / MinHeight-0 slot (a virtual viewport inside a page
// ScrollView measures 0 and shears every cover — must-not-simplify 10). FlattenOne is the SINGLE-SECTION page
// (`BrowseSingleSection`, the official client's rule): the page's model is that section's WHOLE item list, which the query layer walks
// to the server's end (`Spotify.Api.Browse.cs`) — so the page never pages anything itself: no "Show all" tool, no tail preloader, no
// viewport demand (RCA 2026-09-30; the owner's "no page-side fetch windows" rule). The lone section's title heads the
// grid when it says more than the masthead. FlattenTwoStacked renders as Shelves (0.2.9's documented pragmatic
// deviation, kept). The shimmer is an explicit, NON-virtual stand-in (a PagedShelf yields zero rows against an
// unmeasured viewport — must-not-simplify 11), revealed FadeOnly.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

// ── the load-boundary rule (gap: a retry that could not re-arm) ────────────────────────────────────────────────────
//
// A row that is Known is Ready WHATEVER it holds — an empty answer is not a failure, it is an empty band — and a row
// only reads Failed once it has actually been demanded and nothing is left on the wire for it (`Asked && Inflight ==
// 0`, the same "asked, nothing coming" shape every other surface reads off the table); anything else is still
// Pending. Extracted the same way `LibraryNavReadiness` / `AlbumPaneReadiness` are (CLAUDE.md's "SetupGating"
// pattern): pure, engine-free, and the one truth table the directory page and a category page both gate their
// `SkelRegionEl` on, rather than three copies of the same three-way `if` with room for one of them to drift.

/// <summary>The three states a Browse load can be in. <see cref="Ready"/> covers a Known-but-empty answer too — an
/// empty deck or an empty directory is never a failure.</summary>
public enum BrowseLoad : byte { Pending, Ready, Failed }

/// <summary>Pure over the three marks a page already reads off its table. See the section header above for why
/// <paramref name="known"/> alone decides Ready, and why Failed needs BOTH a demand and an empty in-flight count.</summary>
public static class BrowseLoadGate
{
    public static BrowseLoad Of(bool known, bool demanded, bool inflight)
        => known ? BrowseLoad.Ready : demanded && !inflight ? BrowseLoad.Failed : BrowseLoad.Pending;
}

public readonly partial struct Browse
{
    // ══ 1. THE DIRECTORY PAGE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>browse</c> route's page (RouteKind.Browse). The directory is one row per scope; the only prop is the route's band (ZC1:
    /// <see cref="BrowseBands.Canonical"/>, "" for the whole directory). The slot is the same for every band (<c>KeyedByArg</c> is false), so a
    /// band switch re-pushes the props to the mounted page rather than remounting it.</summary>
    // MOUNT POINT (Home.InstallPages — contract §7)
    public static Element DirectoryPageFor(in Shell.Route route)
        => Embed.Comp(new DirectoryProps(BrowseBands.Canonical(Entities.Strings.Resolve(route.Arg))), static () => new DirectoryPage())
            with { Key = "browse-directory" };

    sealed record DirectoryProps(string Band);

    /// <summary>The origin a drill out of the directory hands over: the IA root itself, which is what keeps the trail at
    /// <c>Browse › X</c> rather than <c>Browse › Browse › X</c>.</summary>
    internal static Shell.NavOrigin DirectoryOrigin => new(Loc.Get(Strings.Browse.HomeTitle), new Shell.Route(Shell.RouteKind.Browse));

    [CoreHost]
    sealed class DirectoryPage : Component
    {
        static readonly IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> s_noGroups =
            Array.Empty<(BrowseGroup, IReadOnlyList<BrowseCategory>)>();

        Scope? _scope;
        Browse _dir;
        bool _demanded;
        long _dataKey = long.MinValue;
        string _band = "";
        IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> _groups = s_noGroups;

        readonly Action _demand, _retry;

        public DirectoryPage()
        {
            _demand = Demand;
            _retry = Retry;
        }

        public override Element Render()
        {
            string band = UseProps<DirectoryProps>().Band;
            if (!string.Equals(_band, band, StringComparison.Ordinal))
            {
                _band = band;
                _dataKey = long.MinValue;                         // the band is part of what the memoized group list holds
            }
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
            bool known = d.IsValid && (d.Knows(BrowseFields.Identity) || edges.BrowseDirectory.State(d.Slot) != EdgeState.Unknown);
            var load = BrowseLoadGate.Of(known, _demanded, scope.Browses.Inflight[d.Slot] != 0);
            if (load == BrowseLoad.Ready)
            {
                // The band list is rebuilt ONLY when the tile list or a tile row changed — the under-band edge re-render hands the
                // view the same instance.
                long key = ((long)edges.BrowseDirectory.Version(d.Slot) << 32) | scope.Browses.Changed.Peek();
                if (key != _dataKey)
                {
                    _dataKey = key;
                    var categories = CategoriesOf(edges.BrowseDirectory.Targets(d.Slot));
                    _groups = categories.Length == 0 ? s_noGroups : BrowseBands.Filter(BrowseTaxonomy.Grouped(categories), _band);
                }
            }

            return Slots.View(ViewKeys.BrowseDirectory, new BrowseDirectoryProps
            {
                // One offset per band: a filtered directory is a different page height, and the whole directory's key is unchanged.
                ScrollKey = UseContext(Shell.PageScrollScope) + (band.Length == 0 ? "browse" : "browse:" + band),
                Load = load,
                Groups = load == BrowseLoad.Ready ? _groups : null,
                Retry = _retry,
            });
        }

        void Demand()
        {
            var d = _dir;
            if (!d.IsValid) return;
            Entities.Ensure(d, BrowseFields.All);
            _demanded = true;
        }

        /// <summary>The directory's Retry vacancy: ask for everything it waits on AGAIN, whatever the scope sealed
        /// (<see cref="Entities.Refresh"/> un-asks first) — <see cref="Demand"/>'s plain <see cref="Entities.Ensure"/>
        /// would no-op on a row the planner already marked Asked-and-unanswered.</summary>
        void Retry()
        {
            var d = _dir;
            if (!d.IsValid) return;
            int slot = d.Slot;
            Entities.Refresh(Entities.Current.Browses, new ReadOnlySpan<int>(in slot), (uint)BrowseFields.All);
            _demanded = true;
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

    [CoreHost]
    sealed class CategoryPage : Component
    {
        static readonly BrowsePageLayout.Result s_noSections = new(BrowsePageLayout.Mode.Shelves, Array.Empty<BrowseSectionFacts>());
        static readonly IReadOnlyList<BrowseBand> s_noBands = Array.Empty<BrowseBand>();
        static readonly IReadOnlyList<HomeCard> s_noCards = Array.Empty<HomeCard>();

        CategoryProps? _props;
        Scope? _scope;
        string _uri = "";
        Browse _page;
        bool _demanded, _known, _failed;
        string _title = "";
        int _factsKey;
        bool _factsBuilt;
        BrowsePageLayout.Result _layout = s_noSections;

        // FlattenTwoConcat's merged list, memoized on the two view instances so the grid's gate compares by reference.
        HomeSectionView? _concatA, _concatB;
        IReadOnlyList<HomeCard> _concat = Array.Empty<HomeCard>();

        IOverlayService? _overlay;
        string _scrollScope = "";                         // the tab (Shell.PageScrollScope), composed onto the restore keys

        readonly Action _demand, _retry, _demandCards, _publish, _exploreAll;
        readonly Action<string, string> _openCategory;
        readonly Action<HomeSectionView> _openSection;

        public CategoryPage()
        {
            // A chip drills with this page as its origin; both inputs are read when the chip is CLICKED.
            _openCategory = (uri, title) => Shell.GoTo(BrowseTiles.PageRoute(uri, title),
                _props is { } p ? BrowseTileOrigin.For(isDirectory: false, _title, p.Route) : null);
            // A TITLED shelf's header drills into `browse-section:` with this page as the origin (read when the header is CLICKED).
            _openSection = view => HomeCardNav.OpenBrowseSection(view, _props is { } p ? new Shell.NavOrigin(_title, p.Route) : null);
            _demand = Demand;
            _retry = Retry;
            _demandCards = DemandCards;
            _publish = Publish;
            _exploreAll = GoDirectory;
        }

        public override Element Render()
        {
            var p = UseProps<CategoryProps>();
            _props = p;
            _scrollScope = UseContext(Shell.PageScrollScope);
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
            _failed = BrowseLoadGate.Of(_known, _demanded, scope.Browses.Inflight[b.Slot] != 0) == BrowseLoad.Failed;
            if (_known) Project(scope);
            else _layout = s_noSections;

            UseEffect(_publish, DepKey.From(HashCode.Combine(_title, (int)_layout.Mode), _page.Slot));

            return Slots.View(ViewKeys.BrowseCategory, new BrowseCategoryProps
            {
                Load = _known ? BrowseLoad.Ready : _failed ? BrowseLoad.Failed : BrowseLoad.Pending,
                Title = _title,
                Color = !b.IsValid ? null : b.Accent != 0u ? b.Accent : b.Color != 0u ? b.Color : null,
                Parent = Shell.Origins.For(p.Route) is { } origin && origin.Route.Kind is not Shell.RouteKind.Browse ? origin.Label : null,
                PageUri = _uri,
                ScrollKey = _scrollScope + "browse:" + _uri,
                Mode = _layout.Mode,
                Bands = _known ? Bands() : s_noBands,
                Merged = _known && _layout.Mode == BrowsePageLayout.Mode.FlattenTwoConcat ? Merged() : s_noCards,
                Overlay = _overlay,
                Retry = _retry,
                OpenCategory = _openCategory,
                OpenSection = _openSection,
                ExploreAll = _exploreAll,
            });
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

        /// <summary>Every answered band's rows, in wire order: a shelf band its card view, a grid / related band its category
        /// tiles (the section's category relation, titled tiles only). Built every render — a hydrating card row re-describes a shelf.</summary>
        IReadOnlyList<BrowseBand> Bands()
        {
            var sections = _layout.Sections;
            if (sections.Count == 0) return s_noBands;
            var bands = new BrowseBand[sections.Count];
            for (int i = 0; i < bands.Length; i++)
            {
                var f = sections[i];
                if (f.Kind == BrowseSectionKind.Shelf)
                {
                    bands[i] = new BrowseBand { Facts = f, Shelf = HomeSectionView.Of(new Section(f.Slot)) };
                    continue;
                }
                var targets = Entities.Current.Edges.SectionCategories.Targets(f.Slot);
                var tiles = new List<BrowseCategory>(targets.Length);
                for (int t = 0; t < targets.Length; t++)
                    if (CategoryAt(targets[t]) is { } c) tiles.Add(c);
                bands[i] = new BrowseBand { Facts = f, Tiles = tiles };
            }
            return bands;
        }

        /// <summary>FlattenTwoConcat's merged list: the first two shelves' cards, memoized on the two view instances.</summary>
        IReadOnlyList<HomeCard> Merged()
        {
            HomeSectionView? first = null, second = null;
            var sections = _layout.Sections;
            for (int i = 0; i < sections.Count; i++)
            {
                var f = sections[i];
                if (f.Kind != BrowseSectionKind.Shelf) continue;
                var view = HomeSectionView.Of(new Section(f.Slot));
                if (first is null) first = view;
                else second ??= view;
            }
            return Merge(first, second);
        }

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

        /// <summary>The category page's Retry vacancy: ask for the whole page model AGAIN, whatever the scope sealed
        /// (<see cref="Entities.Refresh"/> un-asks first) — <see cref="Demand"/>'s plain <see cref="Entities.Ensure"/>
        /// would no-op on a row the planner already marked Asked-and-unanswered.</summary>
        void Retry()
        {
            var b = _page;
            if (!b.IsValid) return;
            int slot = b.Slot;
            Entities.Refresh(Entities.Current.Browses, new ReadOnlySpan<int>(in slot), (uint)BrowseFields.All);
            _demanded = true;
        }

        /// <summary>Every shelf band's card rows, in one batch per band (<see cref="Home.EnsureSection"/>, browse routing);
        /// re-runs as the page's bands land.</summary>
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

        /// <summary>One publication per data change: the live title. The page has no tools — its whole model is walked by
        /// the query layer, so there is nothing to "Show all" in place.</summary>
        void Publish()
        {
            if (_props is not { } p) return;
            Shell.Mastheads.Publish(p.Route, new Shell.MastheadPublication(_title.Length > 0 ? _title : null));
        }
    }
}

/// <summary>The Charts band of the Browse directory (0.2.9 <c>BrowseDirectory.ChartsBand</c>): the SAME Fold deck Home's Charts row renders,
/// over the SAME five chart section rows, with the deck's own header (no eyebrow above it — parity 64) and no per-tile
/// eyebrow (parity 65). The host owns the demand, the deck and the Retry; the view (`Browse.Charts`) draws: Pending shimmers the three
/// blank folds; Ready-and-empty is the compact "No charts right now"; Failed is a compact vacancy WITH its own Retry, refetching only the
/// deck (parity 66, 97). Public because the directory view mounts it as a component.</summary>
[CoreHost]
public sealed class BrowseChartsBand : Component
{
    HomeLoad _state = HomeLoad.Pending;
    IReadOnlyList<HomeSectionView> _deck = Array.Empty<HomeSectionView>();

    readonly Action _demand;

    static readonly Action<HomeSectionView> s_openTile = static s => HomeCardNav.OpenBrowseSection(s, Browse.DirectoryOrigin);
    static readonly Action s_openHeader = static () => Shell.GoTo(BrowseTiles.PageRoute(ChartPages.Charts, Loc.Get(Strings.Home.Charts)));
    // Retry, never `_demand`: by the time this band reads Failed, `ChartSections.Featured` is exactly the row
    // shape `Entities.Ensure`'s dedupe skips (Asked survives the answer on purpose — Fetch.cs's "the seal that
    // stops a thin track re-resolving on every cluster update"). Only `Entities.Refresh` un-asks it, which is
    // what turns this button back into a real retry instead of a card that reappears no matter how many times
    // it is pressed.
    static readonly Action s_retry = Retry;

    public BrowseChartsBand()
    {
        _demand = HomeBrowseCards.EnsureChartDeck;
    }

    /// <summary>Ask every chart section row AGAIN, whatever the scope sealed — the Charts band's own Retry
    /// (ch 13 §9 gap 6: "Browse has two retry buttons, not one", this is the second one, and it refetches only
    /// the deck).</summary>
    static void Retry()
    {
        var scope = Entities.Current;
        var uris = ChartSections.All;
        for (int i = 0; i < uris.Count; i++)
        {
            int slot = Entities.BrowseSection(uris[i].AsSpan()).Slot;
            Entities.Refresh(scope.Sections, new ReadOnlySpan<int>(in slot), (uint)SectionFields.Identity);
        }
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
        return Slots.View(ViewKeys.BrowseCharts, new BrowseChartsProps
        {
            State = _state, Deck = _deck, OpenTile = s_openTile, OpenHeader = s_openHeader, Retry = s_retry,
        });
    }
}
