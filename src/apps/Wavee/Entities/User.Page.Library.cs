// ── Entities/User.Page.Library.cs ──────────────────────────────────────────────────────────────────────────────────
// the albums / artists / podcasts MASTER-DETAIL page: LibraryPageFor + LibraryPage (the three-column browser, its
// persisted per-kind signals, the collapse arm, the search mode) and its right-column panes — the compact album pane
// (the embedded track table), the compact show pane (bound episodes) and the artist discography pane
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 1300 lines (+30 % = 1690)
// Spec: ch 15 §0-§9 (0.2.9 Features/Library/LibraryPage.cs); plan §2 A3. WHERE: ch 15 §1.2 names Album.Page.cs /
//       Show.Page.cs / Artist.Page.cs for the three panes, but those files are owners M's and N's and run in parallel —
//       the plan's "where" wins, so the panes live HERE (reported as a third answer).
//
// ── THE SHAPE ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//  LibraryPage (keyed "library:"+route)            ─ seeds every persisted signal in its CONSTRUCTOR (frame one = saved)
//  └─ root BoxEl (OnBoundsChanged → _collapsed, 640/24 hysteresis)
//     ├─ WIDE: NavPanel[Toolbar(title) · ListBody | LeftSearchBody] │ ColumnGrip │ right
//     │        right = DetailColumn (albums/podcasts) | ArtistColumns (artists) | the two search column shapes
//     └─ COLLAPSED: CrumbBar · depth 0 (toolbar + list) | depth 1 (disco / detail) | depth 2 (tracks)
//
// THE NAVIGATOR IS A MEMO, NOT A RENDER. `ComputeShape` (a UseComputed) filters the relation's targets into a pooled
// buffer, sorts them with the reusable `LibraryNavSorter`, and publishes a value `NavShape(count, OrderKey, FactsKey)`;
// the bound list reads its rows out of the sorted buffer. Nothing is copied into a record array, so a selection click
// allocates no row data (ch 15 §9's "per-render array churn" trap). The ItemsView remount key stays
// `view:size:OrderKey:FactsKey` and the always-on `library.nav.remount` line names which half moved.
//
// SELECTION NEVER REMOUNTS AND NEVER NAVIGATES. A pick writes `_selectedKey` (a persisted "album:"/"artist:"/"show:"
// route key); the panes re-skin in place. `_syncingSel` guards the page's OWN re-sync from re-entering the user-pick
// path (two shipped 0.2.9 fixes). Row 0 is adopted whenever the key is empty or absent from a non-empty shown set.
//
// DATA: the relation (Edges.SavedAlbums / FollowedArtists / SavedShows off User.Me) is demanded ONCE per scope when it
// is Unknown, the rows' identity from an auto-tracked effect; the panes demand their whole model on mount. No page reads
// Platform.Args.Fake. Search is the cache-only matcher (`User.SearchLibrary`) over the resident tables, debounced 180 ms.

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct User
{
    // ══ 1. THE FACTORY ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>RouteKind.LibraryAlbums / LibraryArtists / LibraryPodcasts → the master-detail browser. ONE component keyed
    /// by the route's name, so each kind keeps its own keep-alive instance (the kind freezes at mount).</summary>
    // MOUNT POINT (stage B contract)
    public static Element LibraryPageFor(in Shell.Route route)
    {
        var kind = route.Kind is Shell.RouteKind.LibraryArtists or Shell.RouteKind.LibraryPodcasts ? route.Kind : Shell.RouteKind.LibraryAlbums;
        return Embed.Comp(() => new LibraryPage(kind)) with { Key = "library:" + Shell.NameOf(route) };
    }

    /// <summary>What the navigator shows, as a VALUE: the two remount-key halves plus the count.</summary>
    sealed record NavShape(int Count, string OrderKey, string FactsKey)
    {
        public static readonly NavShape Empty = new(0, "0:cbf29ce484222325", "");
    }

    sealed record PaneProps(int Slot);

    static string PrefixOf(EntityKind kind) => kind switch
    {
        EntityKind.Artist => SidebarPinId.ArtistPrefix,
        EntityKind.Show => SidebarPinId.ShowPrefix,
        _ => SidebarPinId.AlbumPrefix,
    };

    /// <summary>The persisted route key for a row ("album:spotify:album:…").</summary>
    static string KeyOf(EntityKind kind, int slot) => slot <= Table.None ? "" : PrefixOf(kind) + LibraryRows.IdOf(kind, slot).Text;

    /// <summary>A persisted key back to a handle slot (the factory allocates an empty row for an unseen uri, which is the
    /// state the pane renders as its skeleton). 0 for an empty or foreign key.</summary>
    static int SlotOfKey(EntityKind kind, string key)
    {
        string prefix = PrefixOf(kind);
        if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal)) return Table.None;
        var id = EntityId.Parse(key.AsSpan(prefix.Length));
        return id.IsValid && id.Kind == kind && Entities.TableFor(kind) is { } table ? table.Slot(id) : Table.None;
    }

    static Element EmptyCompact(string text) => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact, title: text, subtitle: "");

    static Element LoadingCaption() => new BoxEl
    {
        Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL),
        Children = [Caption("…").Secondary()],
    };

    static readonly string[] s_noSuggest = [];
    static readonly Func<bool> s_true = static () => true, s_false = static () => false;
    static readonly Action s_noop = static () => { };

    // ══ 2. THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class LibraryPage : Component
    {
        const float SearchDebounceMs = 180f;
        const int SkelRows = 6;

        readonly Shell.RouteKind _route;
        readonly string _kind;                 // "albums" | "artists" | "podcasts" — the persisted key segment
        readonly EntityKind _entity;
        readonly LibraryEdgeKind _relation;
        readonly FetchEdge _fetch;

        // persisted per kind, seeded here so frame one paints the saved layout (§0 #4)
        internal readonly Signal<float> LeftW, MidW;
        internal readonly Signal<int> Sort, View, Size, ASort, AView, ASize;
        internal readonly Signal<bool> Desc, ADesc;
        internal readonly Signal<string> SelectedKey, AlbumKey;
        internal readonly Signal<string> Filter = new(""), AFilter = new("");   // NOT persisted

        readonly SelectionModel _navSel = new();
        readonly ItemsViewController _navCtl = new();
        readonly ScrollOptions _navScroll;
        readonly Signal<int> _sArtist = new(0), _sAlbum = new(0);   // the search drill-down, by slot, apart from browse
        readonly object _skelGroup = new();
        readonly Signal<bool> _collapsed = new(false);
        readonly Signal<int> _depth = new(0);
        bool _syncingSel, _fullSearch;
        string? _lastOrderKey;
        int _lastView = -1, _lastSize = -1, _lastCount;

        int[] _filtered = new int[64], _perm = new int[64], _sorted = new int[64];
        long[] _played = new long[64];
        int _sortedCount;
        readonly LibraryNavSorter<LibraryRows> _sorter = new();
        readonly LibraryHits _hits = new();

        Memo<NavShape>? _shape;
        internal Memo<int>? SelectedSlot, AlbumSlot;   // NOT "Album": a member named like the type would shadow it
        Memo<bool>? _searchActive;
        IReadSignal<string>? _query;
        BoundItemsSource<LibraryNavItem>? _items;
        ListOptions<LibraryNavItem>? _navOptions;
        Element? _pill, _leftGrip, _midGrip, _artistPane;

        readonly Func<NavShape> _computeShape;
        readonly Func<int> _resolveSelected, _resolveAlbum;
        readonly Func<bool> _isSearching;
        readonly Func<string> _filterText;
        readonly Func<uint> _runSearch;
        readonly Action _demandEdge, _demandRows, _syncNav, _noteKey, _saveState, _resetDepth, _autoTop, _autoAlbum;
        readonly Action _onNavSel, _commitLeft, _commitMid;
        readonly Action<RectF> _onBounds;
        readonly Action<int> _pickDepth;
        readonly Func<BoundItemScope<LibraryNavItem>, Element> _rowT, _rowCompactT, _cardT, _cardCompactT;

        bool IsArtists => _entity == EntityKind.Artist;
        bool IsPodcasts => _entity == EntityKind.Show;

        public LibraryPage(Shell.RouteKind route)
        {
            _route = route;
            (_kind, _entity, _relation, _fetch) = route switch
            {
                Shell.RouteKind.LibraryArtists => ("artists", EntityKind.Artist, LibraryEdgeKind.FollowedArtists, FetchEdge.FollowedArtists),
                Shell.RouteKind.LibraryPodcasts => ("podcasts", EntityKind.Show, LibraryEdgeKind.SavedShows, FetchEdge.SavedShows),
                _ => ("albums", EntityKind.Album, LibraryEdgeKind.SavedAlbums, FetchEdge.SavedAlbums),
            };
            var s = Platform.Settings;
            _navScroll = new ScrollOptions { ScrollKey = "lib:nav:" + _kind };
            LeftW = new(s.Get(Platform.Keys.LibraryLeftW(_kind)));
            MidW = new(s.Get(Platform.Keys.LibraryMidW(_kind)));
            Sort = new(s.Get(Platform.Keys.LibrarySort(_kind)));
            Desc = new(s.Get(Platform.Keys.LibraryDesc(_kind)));
            View = new(s.Get(Platform.Keys.LibraryView(_kind)));
            Size = new(s.Get(Platform.Keys.LibrarySize(_kind)));
            SelectedKey = new(s.Get(Platform.Keys.LibrarySelected(_kind)));
            AlbumKey = new(s.Get(Platform.Keys.LibraryAlbumKey(_kind)));
            ASort = new(s.Get(Platform.Keys.LibraryAlbumSort(_kind)));
            ADesc = new(s.Get(Platform.Keys.LibraryAlbumDesc(_kind)));
            AView = new(s.Get(Platform.Keys.LibraryAlbumView(_kind)));
            ASize = new(s.Get(Platform.Keys.LibraryAlbumSize(_kind)));

            _computeShape = ComputeShape;
            _resolveSelected = () => { _ = Entities.ScopeEpoch.Value; return SlotOfKey(_entity, SelectedKey.Value); };
            _resolveAlbum = () => { _ = Entities.ScopeEpoch.Value; return SlotOfKey(EntityKind.Album, AlbumKey.Value); };
            _isSearching = () => Filter.Value.AsSpan().Trim().Length > 0;
            _filterText = () => Filter.Value.Trim();
            _runSearch = RunSearch;
            _demandEdge = DemandEdge;
            _demandRows = DemandRows;
            _syncNav = SyncNav;
            _noteKey = NoteNavKey;
            _saveState = SaveState;
            _resetDepth = () => { if (_collapsed.Peek()) _depth.Value = 0; };
            _autoTop = AutoSelectTop;
            _autoAlbum = AutoSelectAlbum;
            _onNavSel = OnNavSel;
            _commitLeft = () => Platform.Settings.Set(Platform.Keys.LibraryLeftW(_kind), LeftW.Peek());
            _commitMid = () => Platform.Settings.Set(Platform.Keys.LibraryMidW(_kind), MidW.Peek());
            _onBounds = r =>
            {
                if (r.W <= 0f) return;
                bool c = LibraryLayoutBreakpoints.Collapsed(r.W, _collapsed.Peek());
                if (c != _collapsed.Peek()) _collapsed.Value = c;
            };
            _pickDepth = i => _depth.Value = i;
            var entity = _entity;
            _rowT = scope => NavRow(scope, entity, compact: false);
            _rowCompactT = scope => NavRow(scope, entity, compact: true);
            _cardT = scope => NavCard(scope, entity, compact: false);
            _cardCompactT = scope => NavCard(scope, entity, compact: true);
        }

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;          // FIRST: a scope switch re-points every table below
            _shape = UseComputed(_computeShape);
            SelectedSlot = UseComputed(_resolveSelected);
            AlbumSlot = UseComputed(_resolveAlbum);
            _searchActive = UseComputed(_isSearching);
            _query = UseDebouncedValue(_filterText, SearchDebounceMs);
            uint hitsGeneration = UseComputed(_runSearch).Value;
            UseEffect(_demandEdge, DepKey.From((int)epoch, Entities.Current.MeSlot));   // once per scope; a failed ask never re-arms here
            UseEffect(_demandRows);                                                     // re-runs as the relation lands

            _items ??= BoundItems.Project(_shape, static s => s.Count, (_, i) => ItemAt(i), default(LibraryNavItem));
            _navOptions ??= new ListOptions<LibraryNavItem>
            {
                SelectionMode = ItemsSelectionMode.Single, Selection = _navSel, Controller = _navCtl, Grow = 1f,
                Scroll = _navScroll, OnChange = _onNavSel,
                ItemTextTyped = static (_, it) => LibraryRows.TitleOf(it.Kind, it.Slot),   // typeahead (item 68)
            };

            var shape = _shape.Value;
            string raw = Filter.Value.Trim();
            string query = _query.Value;
            bool fullSearch = raw.Length > 0 && !IsPodcasts;
            _fullSearch = fullSearch;
            bool awaiting = fullSearch && !string.Equals(query, raw, StringComparison.Ordinal);
            bool shimmer = awaiting && _hits.IsEmpty;
            int view = View.Value, size = Size.Value;
            string sel = SelectedKey.Value, albumKey = AlbumKey.Value;
            int selectedSlot = SelectedSlot.Value, albumSlot = AlbumSlot.Value;
            bool railOpen = Shell.Ui.RailOpen.Value;
            bool collapsed = _collapsed.Value;
            int sArtist = _sArtist.Value;

            UseEffect(_syncNav, shape.OrderKey + "|" + sel + "|" + (fullSearch ? "s" : "b"));
            UseEffect(_noteKey, shape.OrderKey + "|" + shape.FactsKey + "|" + view + "|" + size);
            UseEffect(_saveState, Sort.Value + "|" + Desc.Value + "|" + view + "|" + size + "|" + sel + "|" + albumKey + "|"
                                  + ASort.Value + "|" + ADesc.Value + "|" + AView.Value + "|" + ASize.Value);
            UseEffect(_resetDepth, DepKey.From(collapsed));
            UseEffect(_autoTop, DepKey.From((int)hitsGeneration, fullSearch ? 1 : 0));
            UseEffect(_autoAlbum, DepKey.From((int)hitsGeneration, sArtist));

            Element inner;
            if (collapsed)
                inner = Collapsed(shape, fullSearch, shimmer, awaiting, selectedSlot, albumSlot);
            else
            {
                Element right = fullSearch
                    ? (IsArtists ? SearchArtistColumns(railOpen, shimmer, awaiting) : SearchAlbumDetail(shimmer, awaiting))
                    : (IsArtists ? ArtistColumns(sel.Length > 0, albumKey.Length > 0, albumSlot, railOpen) : DetailColumn(sel.Length > 0, selectedSlot));
                inner = new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch,
                    Children = [LeftColumn(shape, fullSearch, shimmer), _leftGrip ??= ColumnGrip(LeftW, 240f, 560f, _commitLeft), right],
                };
            }
            // The page publishes no shell material and tints nothing: the library is the app's accent-neutral browser.
            return new BoxEl { Direction = 1, Grow = 1f, AlignItems = FlexAlign.Stretch, OnBoundsChanged = _onBounds, Children = [inner] };
        }

        // ── the navigator shape ─────────────────────────────────────────────────────────────────────────────────────

        NavShape ComputeShape()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var relation = Relation(_relation);
            _ = relation.Changed.Value;
            _ = Entities.TableFor(_entity)!.Changed.Value;
            if (_entity == EntityKind.Album) { _ = scope.Edges.AlbumArtists.Changed.Value; _ = scope.Artists.Changed.Value; }
            string filter = Filter.Value;
            var sort = (LibraryNavSort)Math.Clamp(Sort.Value, 0, 4);
            bool desc = Desc.Value;

            var source = scope.MeSlot > Table.None ? relation.Targets(scope.MeSlot) : default;
            Grow(source.Length);
            int n = LibraryRows.Filter(_entity, source, filter, _filtered);
            long[]? played = null;
            if (sort == LibraryNavSort.Recents)
            {
                _ = Shell.PlayLog.Version.Value;                 // a play re-orders Recents in place
                LibraryRows.FillPlayed(_entity, _filtered.AsSpan(0, n), Shell.PlayLog.Recency, _played);
                played = _played;
            }
            _sorter.Order(new LibraryRows(_entity, _filtered, n, played), sort, desc, _perm);
            for (int i = 0; i < n; i++) _sorted[i] = _filtered[_perm[i]];
            _sortedCount = n;
            var display = new LibraryRows(_entity, _sorted, n);
            return new NavShape(n, LibraryNavOrder.OrderKey(display), LibraryNavOrder.FactsKey(display));
        }

        void Grow(int n)
        {
            if (_filtered.Length >= n) return;
            int size = Math.Max(n, _filtered.Length * 2);
            _filtered = new int[size]; _perm = new int[size]; _sorted = new int[size]; _played = new long[size];
        }

        LibraryNavItem ItemAt(int i) => (uint)i < (uint)_sortedCount ? LibraryNavItem.Of(_entity, _sorted[i]) : default;

        int IndexOfSlot(int slot)
        {
            if (slot <= Table.None) return -1;
            for (int i = 0; i < _sortedCount; i++) if (_sorted[i] == slot) return i;
            return -1;
        }

        // ── demand ──────────────────────────────────────────────────────────────────────────────────────────────────

        void DemandEdge()
        {
            int me = Entities.Current.MeSlot;
            if (me > Table.None && Relation(_relation).State(me) == EdgeState.Unknown) Entities.EnsureEdge(_fetch, me);
        }

        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var relation = Relation(_relation);
            _ = relation.Changed.Value;
            int me = Entities.Current.MeSlot;
            var slots = me > Table.None ? relation.Targets(me) : default;
            if (slots.Length == 0) return;
            switch (_entity)
            {
                case EntityKind.Album: Entities.Ensure(MemoryMarshal.Cast<int, Album>(slots), AlbumFields.Identity); break;
                case EntityKind.Artist: Entities.Ensure(MemoryMarshal.Cast<int, Artist>(slots), ArtistFields.Identity); break;
                default: Entities.Ensure(MemoryMarshal.Cast<int, Show>(slots), ShowFields.Identity); break;
            }
        }

        // ── selection ───────────────────────────────────────────────────────────────────────────────────────────────

        void Select(int slot)
        {
            SelectedKey.Value = KeyOf(_entity, slot);
            if (IsArtists) AlbumKey.Value = "";   // a new artist resets the third column's release
        }

        void OnNavSel()
        {
            int i = _navSel.FirstSelectedIndex;
            if (_syncingSel || i < 0 || i >= _sortedCount) return;   // the page's own re-sync is a VIEW update, never a pick
            Select(_sorted[i]);
            if (_collapsed.Peek()) _depth.Value = 1;
        }

        void SyncSelect(int idx)
        {
            _syncingSel = true;
            try { if (idx < 0) _navSel.DeselectAll(); else _navSel.Select(idx); }
            finally { _syncingSel = false; }
            if (idx >= 0) _navCtl.StartBringItemIntoView(idx);   // minimal + unanimated: a visible row never moves
        }

        void SyncNav()
        {
            if (_fullSearch) return;
            if (_sortedCount == 0)
            {
                // A filter that matched nothing drops the selection (and the release); an empty set with NO filter is still
                // loading — keep the persisted selection (the launch restore).
                if (Filter.Peek().Length > 0 && SelectedKey.Peek().Length > 0)
                {
                    SelectedKey.Value = "";
                    if (IsArtists) AlbumKey.Value = "";
                    if (_navSel.SelectedCount > 0) SyncSelect(-1);
                }
                return;
            }
            string key = SelectedKey.Peek();
            int idx = key.Length == 0 ? -1 : IndexOfSlot(SlotOfKey(_entity, key));
            if (idx < 0) { idx = 0; Select(_sorted[0]); }   // no selection, or a key this set no longer holds → row 0
            if (_navSel.FirstSelectedIndex != idx) SyncSelect(idx);
        }

        void NoteNavKey()
        {
            var shape = _shape!.Peek();
            int view = View.Peek(), size = Size.Peek();
            if (_lastOrderKey is not null)
            {
                string reason = view != _lastView || size != _lastSize ? "view" : shape.OrderKey != _lastOrderKey ? "order" : "facts";
                Log.Event(WaveeLogLevel.Info, "ui", "library.nav.remount", "Library navigator remounted", null, -1, null,
                    WaveeLogField.Of("kind", _kind), WaveeLogField.Of("reason", reason),
                    WaveeLogField.Of("before", _lastCount), WaveeLogField.Of("after", shape.Count));
            }
            _lastOrderKey = shape.OrderKey; _lastView = view; _lastSize = size; _lastCount = shape.Count;
        }

        void SaveState()
        {
            var s = Platform.Settings;
            s.Set(Platform.Keys.LibrarySort(_kind), Sort.Peek());
            s.Set(Platform.Keys.LibraryDesc(_kind), Desc.Peek());
            s.Set(Platform.Keys.LibraryView(_kind), View.Peek());
            s.Set(Platform.Keys.LibrarySize(_kind), Size.Peek());
            s.Set(Platform.Keys.LibrarySelected(_kind), SelectedKey.Peek());
            if (!IsArtists) return;
            s.Set(Platform.Keys.LibraryAlbumKey(_kind), AlbumKey.Peek());
            s.Set(Platform.Keys.LibraryAlbumSort(_kind), ASort.Peek());
            s.Set(Platform.Keys.LibraryAlbumDesc(_kind), ADesc.Peek());
            s.Set(Platform.Keys.LibraryAlbumView(_kind), AView.Peek());
            s.Set(Platform.Keys.LibraryAlbumSize(_kind), ASize.Peek());
        }

        internal void DrillToTracks() { if (_collapsed.Peek()) _depth.Value = 2; }

        // ── the left column ─────────────────────────────────────────────────────────────────────────────────────────

        Element LeftColumn(NavShape shape, bool fullSearch, bool shimmer) => NavPanel with
        {
            Width = LeftW, Shrink = 0f,
            Children = [Toolbar(title: true), fullSearch ? LeftSearchBody(shimmer) : ListBody(shape)],
        };

        /// <summary>The master column's head: the page title (PageHero, one line) INSIDE the toolbar, the pill, the filter.
        /// The collapsed layout drops the title — the crumb root names the kind.</summary>
        Element Toolbar(bool title)
        {
            Element picker = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center,
                Children = [_pill ??= SortViewPill(Sort, Desc, View, Size, hasCreator: !IsArtists, hasRelease: _entity == EntityKind.Album), new BoxEl { Grow = 1f }],
            };
            Element filter = AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Library.Filter), text: Filter, queryIcon: Icons.Search,
                grow: 1f, maxFillWidth: 9999f, minHeight: 32f, cornerRadius: Radii.Control);
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Shrink = 0f,
                Padding = new Edges4(Spacing.M, title ? Spacing.L : Spacing.M, Spacing.M, Spacing.S),
                Children = title
                    ? [Design.Type.PageHero(Shell.Dest(new Shell.Route(_route)).Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis }, picker, filter]
                    : [picker, filter],
            };
        }

        /// <summary>The navigator: an empty set with filter text is the big-type no-match; with none it is still LOADING
        /// ("…"). Otherwise the bound ItemsView, remounted only when its frozen shape would lie.</summary>
        Element ListBody(NavShape shape)
        {
            if (shape.Count == 0)
                return Filter.Peek().Length > 0 ? EmptyCompact(Loc.Get(Strings.Library.NoMatch)) : LoadingCaption();
            int view = View.Value, size = Size.Value;
            bool grid = IsGridView(view), compact = IsCompactView(view);
            var layout = grid
                ? RepeatLayout.GridFit((compact ? 88f : 116f) + size * (compact ? 16f : 24f), 8f)
                : RepeatLayout.Stack(compact ? 40f : 60f);
            var template = grid ? (compact ? _cardCompactT : _cardT) : (compact ? _rowCompactT : _rowT);
            return new BoxEl
            {
                Key = "nav:" + view + ":" + size + ":" + shape.OrderKey + ":" + shape.FactsKey,
                Grow = 1f, Direction = 1, MinHeight = 0f,
                Padding = grid ? new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f) : default,
                Children = [ItemsView.CreateBound(_items!, template, layout, _navOptions)],
            };
        }

        // ── the right column (browse) ───────────────────────────────────────────────────────────────────────────────

        Element DetailColumn(bool hasSelection, int slot)
        {
            if (!hasSelection) return Placeholder(IsPodcasts ? Strings.Library.SelectShow : Strings.Library.SelectAlbum);
            return ReadingPane with { Key = "lib:detail", Grow = 1f, Basis = 0f, Children = [PaneFor(_entity, slot)] };
        }

        static Element PaneFor(EntityKind kind, int slot) => kind == EntityKind.Show
            ? Embed.Comp(new PaneProps(slot), static () => new LibraryShowPane()) with { Key = "show-pane:" + slot }
            : Embed.Comp(new PaneProps(slot), static () => new LibraryAlbumPane());

        Element ArtistColumns(bool hasSelection, bool hasRelease, int albumSlot, bool railOpen)
        {
            if (!hasSelection) return Placeholder(Strings.Library.SelectArtist);
            // Basis = the chosen width but Shrink 1 and MaxWidth = it: Shrink 0 + a fixed width let the viewport outgrow the
            // flex slot and slide the discography under the tracks pane. The rail-open floor drops 300 → 220.
            Element artistPane = ReadingPane with
            {
                Basis = MidW.Value, MinWidth = railOpen ? 220f : 300f, MaxWidth = MidW.Value, Shrink = 1f, Grow = 0f,
                Children = [_artistPane ??= Embed.Comp(() => new LibraryArtistPane(this))],
            };
            Element tracksPane = hasRelease
                ? ReadingPane with { Key = "lib:tracks", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f, Children = [PaneFor(EntityKind.Album, albumSlot)] }
                : ReadingPane with { Key = "lib:tracks:empty", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
                                     Children = [EmptyCompact(Loc.Get(Strings.Library.SelectAlbumTracks))] };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
                Children = [artistPane, _midGrip ??= ColumnGrip(MidW, 300f, 620f, _commitMid), tracksPane],
            };
        }

        static Element Placeholder(string key) => ReadingPane with { Key = "lib:empty", Grow = 1f, Children = [EmptyCompact(Loc.Get(key))] };

        // ── the collapsed drill-in (W14-W17, W25) ───────────────────────────────────────────────────────────────────

        Element Collapsed(NavShape shape, bool fullSearch, bool shimmer, bool awaiting, int selectedSlot, int albumSlot)
        {
            int depth = Math.Clamp(_depth.Value, 0, IsArtists ? 2 : 1);
            int sArtist = _sArtist.Value, sAlbum = _sAlbum.Value;
            string level1 = IsArtists
                ? NameOrEllipsis(EntityKind.Artist, fullSearch ? sArtist : selectedSlot)
                : NameOrEllipsis(_entity, fullSearch ? sAlbum : selectedSlot);
            string level2 = NameOrEllipsis(EntityKind.Album, fullSearch ? sAlbum : albumSlot);
            var crumbs = new List<string>(3) { KindCrumb() };
            if (depth >= 1) crumbs.Add(level1);
            if (depth >= 2) crumbs.Add(level2);

            Element body;
            if (depth == 0)
                body = new BoxEl { Direction = 1, Grow = 1f, Children = [Toolbar(title: false), fullSearch ? LeftSearchBody(shimmer) : ListBody(shape)] };
            else if (depth == 1 && IsArtists)
                body = ReadingPane with
                {
                    Key = "col:disco", Grow = 1f, Basis = 0f,
                    Children = [fullSearch ? AlbumHits(shimmer, explain: false) : (_artistPane ??= Embed.Comp(() => new LibraryArtistPane(this)))],
                };
            else if (depth == 1)
                body = ReadingPane with
                {
                    Key = "col:detail", Grow = 1f, Basis = 0f,
                    Children = [fullSearch ? TrackHits(shimmer) : PaneFor(_entity, selectedSlot)],
                };
            else
                body = ReadingPane with
                {
                    Key = "col:tracks", Grow = 1f, Basis = 0f,
                    Children = [fullSearch ? TrackHits(shimmer) : PaneFor(EntityKind.Album, albumSlot)],
                };
            return new BoxEl { Direction = 1, Grow = 1f, ClipToBounds = true, Children = [CrumbBar(crumbs, _pickDepth), body] };
        }

        string KindCrumb() => _entity switch
        {
            EntityKind.Artist => Loc.Get(Strings.Search.Artists),
            EntityKind.Album => Loc.Get(Strings.Search.Albums),
            _ => Loc.Get(Strings.Podcast.Show),
        };

        static string NameOrEllipsis(EntityKind kind, int slot)
        {
            string name = slot > Table.None ? LibraryRows.TitleOf(kind, slot) : "";
            return name.Length > 0 ? name : "…";
        }

        // ── search mode (W11-W13, W23, W26) ─────────────────────────────────────────────────────────────────────────

        uint RunSearch()
        {
            _ = Entities.ScopeEpoch.Value;
            bool active = _searchActive!.Value && !IsPodcasts;
            string q = _query!.Value;
            if (active)
            {
                var scope = Entities.Current;
                _ = scope.Edges.FollowedArtists.Changed.Value; _ = scope.Edges.SavedAlbums.Changed.Value;
                _ = scope.Edges.ArtistAlbums.Changed.Value; _ = scope.Edges.ArtistSingles.Changed.Value;
                _ = scope.Edges.ArtistCompilations.Changed.Value; _ = scope.Edges.AlbumTracks.Changed.Value;
                _ = scope.Albums.Changed.Value; _ = scope.Artists.Changed.Value; _ = scope.Tracks.Changed.Value;
            }
            SearchLibrary(IsArtists ? LibrarySearchScope.Artists : LibrarySearchScope.Albums, active ? q : "", _hits);
            return _hits.Generation;
        }

        void AutoSelectTop()
        {
            if (!_fullSearch) return;
            if (IsArtists)
            {
                var artists = _hits.Artists;
                if (artists.Length == 0) { if (_sArtist.Peek() != 0) { _sArtist.Value = 0; _sAlbum.Value = 0; } return; }
                if (LibraryHits.IndexOf(artists, _sArtist.Peek()) < 0) { _sArtist.Value = artists[0].Slot; _sAlbum.Value = 0; }
                return;
            }
            var albums = _hits.Albums;
            if (albums.Length == 0) { if (_sAlbum.Peek() != 0) _sAlbum.Value = 0; return; }
            if (LibraryHits.IndexOf(albums, _sAlbum.Peek()) < 0) _sAlbum.Value = albums[0].Slot;
        }

        void AutoSelectAlbum()
        {
            if (!_fullSearch || !IsArtists) return;
            var albums = AlbumsOfSelectedArtist();
            if (albums.Length == 0) { if (_sAlbum.Peek() != 0) _sAlbum.Value = 0; return; }
            if (LibraryHits.IndexOf(albums, _sAlbum.Peek()) < 0) _sAlbum.Value = albums[0].Slot;
        }

        ReadOnlySpan<LibraryAlbumHit> AlbumsOfSelectedArtist()
        {
            int ai = LibraryHits.IndexOf(_hits.Artists, _sArtist.Peek());
            return ai < 0 ? default : _hits.AlbumsOf(_hits.Artists[ai]);
        }

        (int AlbumSlot, Element[] Rows) SelectedTrackRows()
        {
            var albums = IsArtists ? AlbumsOfSelectedArtist() : _hits.Albums;
            int ali = LibraryHits.IndexOf(albums, _sAlbum.Peek());
            if (ali < 0) return (Table.None, Array.Empty<Element>());
            var album = albums[ali];
            var tracks = _hits.TracksOf(album);
            bool showArt = !Prefs.Appearance.TrackArtworkHidden();
            var rows = new Element[tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = tracks[i];
                int albumSlot = album.Slot, trackSlot = t.Slot;
                rows[i] = TrackHitRow("search:t" + trackSlot, Controls.ArtUrl(LibraryRows.ImageOf(EntityKind.Track, trackSlot)),
                    LibraryRows.TitleOf(EntityKind.Track, trackSlot), t.MatchStart, t.MatchLen, showArt,
                    () => Playback.PlayContext(LibraryRows.IdOf(EntityKind.Album, albumSlot), LibraryRows.IdOf(EntityKind.Track, trackSlot)));
            }
            return (album.Slot, rows);
        }

        Element LeftSearchBody(bool shimmer)
        {
            if (!IsArtists) return AlbumHits(shimmer, explain: true);
            var artists = _hits.Artists;
            int selected = _sArtist.Value;
            var rows = new Element[artists.Length];
            for (int i = 0; i < artists.Length; i++)
            {
                var a = artists[i];
                int slot = a.Slot;
                rows[i] = SearchRow("search:a" + slot, Controls.ArtUrl(LibraryRows.ImageOf(EntityKind.Artist, slot)), circular: true,
                    LibraryRows.TitleOf(EntityKind.Artist, slot), a.MatchStart, a.MatchLen, "", MatchEyebrow(a.Match),
                    slot == selected, () => SelectArtist(slot));
            }
            return SearchSkel(shimmer, s_skelArtistRow, rows.Length == 0 ? () => SearchMessage(Loc.Get(Strings.Library.NoMatch)) : () => SearchScroll(rows));
        }

        /// <summary>Album hits: the albums view's LEFT column (explained, with its zero-hit message) or the artists view's
        /// middle column (browse context under an explained artist: no eyebrow, no message — W23).</summary>
        Element AlbumHits(bool shimmer, bool explain)
        {
            var albums = explain ? _hits.Albums : AlbumsOfSelectedArtist();
            int selected = _sAlbum.Value;
            var rows = new Element[albums.Length];
            for (int i = 0; i < albums.Length; i++)
            {
                var al = albums[i];
                int slot = al.Slot;
                rows[i] = SearchRow("search:al" + slot, Controls.ArtUrl(LibraryRows.ImageOf(EntityKind.Album, slot)), circular: false,
                    LibraryRows.TitleOf(EntityKind.Album, slot), al.MatchStart, al.MatchLen, AlbumSubtitle(slot),
                    explain ? MatchEyebrow(al.Match) : null, slot == selected, () => SelectAlbum(slot));
            }
            Func<Element> content = explain && rows.Length == 0 ? () => SearchMessage(Loc.Get(Strings.Library.NoMatch)) : () => SearchScroll(rows);
            return SearchSkel(shimmer, s_skelAlbumRow, content);
        }

        Element TrackHits(bool shimmer)
        {
            var (_, rows) = SelectedTrackRows();
            return SearchSkel(shimmer, s_skelTrackRow, () => SearchScroll(rows));
        }

        Element SearchArtistColumns(bool railOpen, bool shimmer, bool awaiting)
        {
            _ = _sAlbum.Value; _ = _sArtist.Value;
            int albumCount = AlbumsOfSelectedArtist().Length;
            var (_, trackRows) = SelectedTrackRows();
            Element albumPane = ReadingPane with
            {
                Key = "s:albums", Basis = MidW.Value, MinWidth = railOpen ? 220f : 300f, MaxWidth = MidW.Value, Shrink = 1f, Grow = 0f,
                Children = [FacetHeader(Loc.Get(Strings.Search.Albums), shimmer ? -1 : albumCount, awaiting), AlbumHits(shimmer, explain: false)],
            };
            Element trackPane = ReadingPane with
            {
                Key = "s:tracks", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
                Children = [FacetHeader(Loc.Get(Strings.Search.Songs), shimmer ? -1 : trackRows.Length, awaiting),
                            SearchSkel(shimmer, s_skelTrackRow, () => SearchScroll(trackRows))],
            };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
                Children = [albumPane, _midGrip ??= ColumnGrip(MidW, 300f, 620f, _commitMid), trackPane],
            };
        }

        Element SearchAlbumDetail(bool shimmer, bool awaiting)
        {
            _ = _sAlbum.Value;
            var (_, rows) = SelectedTrackRows();
            return ReadingPane with
            {
                Key = "s:detail", Grow = 1f, Basis = 0f,
                Children = [FacetHeader(Loc.Get(Strings.Search.Songs), shimmer ? -1 : rows.Length, awaiting),
                            SearchSkel(shimmer, s_skelTrackRow, () => SearchScroll(rows))],
            };
        }

        /// <summary>The engine's skeleton boundary: shimmer rows DERIVED from the same row builder, a staggered reveal, and
        /// ONE group so the columns settle together. Shimmer only while there is nothing worth keeping on screen.</summary>
        Element SearchSkel(bool shimmer, Func<Element> row, Func<Element> content) => new SkelRegionEl(
            Pending: shimmer ? s_true : s_false, Failed: s_false, Content: content,
            ShimmerSource: () => ShimmerStack(row), OnFailed: null,
            Reveal: SkelReveal.StaggerRows, Style: SkeletonStyle.Default, Group: _skelGroup);

        static Element ShimmerStack(Func<Element> row)
        {
            var kids = new Element[SkelRows];
            for (int i = 0; i < SkelRows; i++) kids[i] = row() with { Key = "skel:" + i };
            return new BoxEl { Direction = 1, Gap = 2f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Children = kids };
        }

        static Element SearchScroll(Element[] rows) => ScrollView(new BoxEl
        {
            Direction = 1, Gap = 2f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Design.Dock.Reserve + Spacing.XL),
            Children = rows,
        }) with { Grow = 1f };

        static readonly Func<Element> s_skelArtistRow = static () => SearchRow("", null, true, "", 0, 0, "", null, false, s_noop);
        static readonly Func<Element> s_skelAlbumRow = static () => SearchRow("", null, false, "", 0, 0, "", null, false, s_noop);
        static readonly Func<Element> s_skelTrackRow = static () => TrackHitRow("", null, "", 0, 0, true, s_noop);

        static string AlbumSubtitle(int slot)
        {
            int year = LibraryRows.YearOf(EntityKind.Album, slot);
            var a = new Album(slot);
            string kind = Detail.Text.KindLabel(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);
            return year > 0 ? year + " · " + kind : kind;
        }

        static string? MatchEyebrow(in MatchReason reason)
        {
            if (!reason.ShouldExplain) return null;
            return reason.Kind switch
            {
                LibraryMatchKind.Album => Strings.Library.MatchedAlbum(reason.Term!),
                LibraryMatchKind.Track => Strings.Library.MatchedSong(reason.Term!),
                _ => null,
            };
        }

        // Search hits SELECT IN PLACE (the pure LibrarySelectionCommit decides which signals move).
        void SelectArtist(int slot)
        {
            _sArtist.Value = slot;
            _sAlbum.Value = 0;
            Apply(LibrarySelectionCommit.ForArtist(IsArtists, _collapsed.Peek(), LibraryRows.IdOf(EntityKind.Artist, slot).Text));
        }

        void SelectAlbum(int slot)
        {
            _sAlbum.Value = slot;
            int owner = _sArtist.Peek();
            Apply(LibrarySelectionCommit.ForAlbum(IsArtists, _collapsed.Peek(), LibraryRows.IdOf(EntityKind.Album, slot).Text,
                owner > Table.None ? LibraryRows.IdOf(EntityKind.Artist, owner).Text : ""));
        }

        void Apply(in LibrarySelectionCommit c)
        {
            if (c.SelectedKey is { } selected) SelectedKey.Value = selected;
            if (c.AlbumKey is { } album) AlbumKey.Value = album;
            if (c.ClearFilter && Filter.Peek().Length > 0) Filter.Value = "";
            if (c.Depth is { } d) _depth.Value = d;
        }
    }

    // ══ 3. THE COMPACT ALBUM PANE (the right column; W1, W7, §6 "Detail pane") ═══════════════════════════════════════

    sealed class LibraryAlbumPane : Component
    {
        int _slot;
        readonly Action _demand, _demandRows, _play, _shuffle, _open;

        public LibraryAlbumPane()
        {
            _demand = () =>
            {
                var a = new Album(_slot);
                if (!a.IsValid) return;
                Entities.Ensure(a, AlbumFields.Detail);
                if (Entities.Current.Edges.AlbumTracks.State(_slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.AlbumTracks, _slot);
            };
            _demandRows = () =>
            {
                _ = Entities.ScopeEpoch.Value;
                _ = Entities.Current.Edges.AlbumTracks.Changed.Value;
                var a = new Album(_slot);
                if (a.IsValid && a.TrackSlots.Length > 0)
                    Entities.Ensure(MemoryMarshal.Cast<int, Track>(a.TrackSlots), TrackFields.Row | TrackFields.Audio | TrackFields.Tags | TrackFields.Video);
            };
            _play = () => { var a = new Album(_slot); if (a.IsValid) Playback.PlayContext(a.Id); };
            _shuffle = () => { var a = new Album(_slot); if (!a.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(a.Id); };
            _open = () => { var a = new Album(_slot); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Title)); };
        }

        public override Element Render()
        {
            var p = UseProps<PaneProps>();
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Albums.Changed.Value; _ = scope.Tracks.Changed.Value; _ = scope.Artists.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value; _ = scope.Edges.AlbumArtists.Changed.Value;
            _slot = p.Slot;
            UseEffect(_demand, DepKey.From(p.Slot, (int)epoch));   // the whole model, once per album per scope
            UseEffect(_demandRows);

            var a = new Album(_slot);
            // Readiness: the identity AND an answered tracklist — partial rows popping in is a regression (ch 15 §7).
            if (!a.IsValid || !a.Knows(AlbumFields.Title) || scope.Edges.AlbumTracks.State(_slot) == EdgeState.Unknown) return PaneSkeleton();

            var id = Detail.Identity.For(a);
            string uri = a.Uri.Text;
            string eyebrow = a.Knows(AlbumFields.Kind) ? Detail.Text.KindLabel(a.Kind) : "";
            var notice = Detail.NoticeRules.ForAlbum(MemoryMarshal.Cast<int, Track>(a.TrackSlots));
            var table = Track.Table(new Track.TableArgs
            {
                Source = Track.TableSource.ForAlbum(a),
                Profile = Track.TableProfile.From(Detail.Config.For(DetailKind.Album, a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album)),
                ShowToolbar = false, Embedded = true, ScrollKey = "lib:tracks:" + uri,
            });
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    PaneHero(id.CoverUrl, eyebrow, id.Title, _open, ArtistLine(id.Artists), id.Meta ?? ""),
                    PaneActions(_play, _shuffle,
                        Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = id.Title }) with { Key = "save:" + uri },
                        Button.Create(Loc.Get(Strings.Library.ViewFullAlbum), _open, ButtonAppearance.Subtle, ControlSize.Small)),
                    // The pane never self-heals a minified tracklist (it mounts no trailing band), so it states it.
                    Detail.NoticeBar(notice, showMinifiedAlbum: true, goLibrary: null),
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, Children = [table] },
                ],
            };
        }

        static Element ArtistLine(IReadOnlyList<Controls.Face>? artists)
        {
            if (artists is not { Count: > 0 }) return new BoxEl();
            var kids = new Element[artists.Count * 2 - 1];
            for (int i = 0, k = 0; i < artists.Count; i++)
            {
                if (i > 0) kids[k++] = new TextEl(", ") { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary };
                var face = artists[i];
                var label = new TextEl(face.Name)
                {
                    Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };
                kids[k++] = face.OnClick is { } go
                    ? new BoxEl { Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = go, Shrink = 1f, MinWidth = 0f, Children = [label] }
                    : label;
            }
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids };
        }
    }

    // ══ 4. THE COMPACT SHOW PANE (W3: Follow, no "View full album", compact episodes) ════════════════════════════════

    sealed record EpisodeShape(int Count, uint Version);

    sealed class LibraryShowPane : Component
    {
        int _slot;
        Memo<EpisodeShape>? _shape;
        BoundItemsSource<LibraryNavItem>? _items;
        readonly Action _demand, _demandRows, _play, _shuffle, _open;
        readonly Func<EpisodeShape> _compute;

        public LibraryShowPane()
        {
            _demand = () =>
            {
                var sh = new Show(_slot);
                if (!sh.IsValid) return;
                Entities.Ensure(sh, ShowFields.All);
                if (Entities.Current.Edges.ShowEpisodes.State(_slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ShowEpisodes, _slot);
            };
            _demandRows = () =>
            {
                _ = Entities.ScopeEpoch.Value;
                _ = Entities.Current.Edges.ShowEpisodes.Changed.Value;
                var sh = new Show(_slot);
                if (sh.IsValid && sh.EpisodeSlots.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Episode>(sh.EpisodeSlots), EpisodeFields.Row);
            };
            _play = () => { var sh = new Show(_slot); if (sh.IsValid) Playback.PlayContext(sh.Id); };
            _shuffle = () => { var sh = new Show(_slot); if (!sh.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(sh.Id); };
            _open = () => { var sh = new Show(_slot); if (sh.IsValid) Shell.GoTo(Shell.For(sh.Uri, sh.Title)); };
            _compute = () =>
            {
                _ = Entities.ScopeEpoch.Value;
                var e = Entities.Current.Edges.ShowEpisodes;
                _ = e.Changed.Value;
                _ = Entities.Current.Episodes.Changed.Value;
                return new EpisodeShape(e.Count(_slot), e.Version(_slot));
            };
        }

        public override Element Render()
        {
            var p = UseProps<PaneProps>();      // keyed per show by the caller, so the slot is stable for this instance
            _slot = p.Slot;
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _shape = UseComputed(_compute);
            UseEffect(_demand, DepKey.From(p.Slot, (int)epoch));
            UseEffect(_demandRows);
            _items ??= BoundItems.Project(_shape, static s => s.Count, (_, i) => EpisodeAt(i), default(LibraryNavItem));

            var sh = new Show(_slot);
            if (!sh.IsValid || !sh.Knows(ShowFields.Title)) return PaneSkeleton();
            string uri = sh.Uri.Text, title = sh.Title;
            string publisher = Entities.Strings.Resolve(sh.PublisherId);
            _ = _shape.Value;
            Element body = ItemsView.CreateBound(_items, s_episodeRow, RepeatLayout.VariableList(64f), new ListOptions<LibraryNavItem>
            {
                SelectionMode = ItemsSelectionMode.None, Grow = 1f,
                Scroll = new ScrollOptions { ScrollKey = "lib:episodes:" + uri },
            });
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    PaneHero(Controls.ArtUrl(sh.ImageId), Loc.Get(Strings.Podcast.Show), title, _open,
                        publisher.Length > 0 ? new TextEl(publisher) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } : new BoxEl(),
                        Detail.Text.ShowMeta(publisher, sh.TotalEpisodes) ?? ""),
                    PaneActions(_play, _shuffle, Embed.Comp(() => new Controls.FollowButton { Uri = uri, Name = title }) with { Key = "follow:" + uri }, null),
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Children = [body] },
                ],
            };
        }

        LibraryNavItem EpisodeAt(int i)
        {
            var slots = Entities.Current.Edges.ShowEpisodes.Targets(_slot);
            return (uint)i < (uint)slots.Length ? LibraryNavItem.Of(EntityKind.Episode, slots[i]) : default;
        }

        // Compact episode row: MinH 56, a 32 circular play chip, a 2-line title and the duration (W3).
        static readonly Func<BoundItemScope<LibraryNavItem>, Element> s_episodeRow = static scope => new BoxEl
        {
            Direction = 0, MinHeight = 56f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = Edges4.All(Spacing.S),
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = scope.Invoke(static it => PlayEpisode(it.Slot)),
            Children =
            [
                new BoxEl { Width = 32f, Height = 32f, Shrink = 0f, Corners = Radii.Circle(32f), Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.Play, 12f, Tok.TextSecondary)] },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(scope.Text(static it => it.Slot > Table.None ? new Episode(it.Slot).Title : ""))
                            { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(scope.Duration(static it => it.Slot > Table.None ? (long)new Episode(it.Slot).DurationMs : 0L))
                            { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);

        static void PlayEpisode(int slot)
        {
            if (slot <= Table.None) return;
            var ep = new Episode(slot);
            if (ep.Show.IsValid) Playback.PlayContext(ep.Show.Id, ep.Id);
            else Playback.PlayContext(ep.Id);
        }
    }

    // ══ 5. THE ARTIST DISCOGRAPHY PANE (W2, W8; the toolbar stays up across a selection change) ══════════════════════

    sealed class LibraryArtistPane : Component
    {
        readonly LibraryPage _page;
        readonly SelectionModel _discoSel = new();
        readonly ItemsViewController _discoCtl = new();
        bool _syncingSel;
        int[] _all = new int[32], _filtered = new int[32], _perm = new int[32], _sorted = new int[32];
        long[] _played = new long[32];
        int _sortedCount, _artist;
        readonly LibraryNavSorter<LibraryRows> _sorter = new();
        readonly HashSet<int> _seen = new();
        Memo<NavShape>? _shape;
        BoundItemsSource<LibraryNavItem>? _items;
        Element? _pill;
        readonly Func<NavShape> _compute;
        readonly Action _demand, _demandRows, _syncDisco, _pick, _goArtist;
        readonly Func<BoundItemScope<LibraryNavItem>, Element> _rowT, _rowCompactT, _cardT, _cardCompactT;

        public LibraryArtistPane(LibraryPage page)
        {
            _page = page;
            _compute = Compute;
            _demand = () =>
            {
                var a = new Artist(_artist);
                if (a.IsValid) Entities.Ensure(a, ArtistFields.Identity);
            };
            // N's facet demand (Artist.Discography.cs): first page while Unknown, the next while Partial, the cards of every
            // listed release. Auto-tracked, so it re-runs as pages land and when the selected artist moves.
            _demandRows = () =>
            {
                _ = Entities.ScopeEpoch.Value;
                var e = Entities.Current.Edges;
                _ = e.ArtistAlbums.Changed.Value; _ = e.ArtistSingles.Changed.Value; _ = e.ArtistCompilations.Changed.Value;
                int artist = _page.SelectedSlot?.Value ?? 0;
                if (artist > Table.None) Artist.DemandDiscography(new Artist(artist));
            };
            _syncDisco = SyncDisco;
            _pick = Pick;
            _goArtist = () => { var a = new Artist(_artist); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Name)); };
            _rowT = scope => DiscoRow(scope, compact: false);
            _rowCompactT = scope => DiscoRow(scope, compact: true);
            _cardT = scope => DiscoCard(scope, compact: false);
            _cardCompactT = scope => DiscoCard(scope, compact: true);
        }

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            int artist = _page.SelectedSlot!.Value;
            _artist = artist;
            _ = Entities.Current.Artists.Changed.Value;
            _shape = UseComputed(_compute);
            UseEffect(_demand, DepKey.From(artist, (int)epoch));
            UseEffect(_demandRows);
            _items ??= BoundItems.Project(_shape, static s => s.Count, (_, i) => (uint)i < (uint)_sortedCount ? LibraryNavItem.Of(EntityKind.Album, _sorted[i]) : default, default(LibraryNavItem));
            var shape = _shape.Value;
            UseEffect(_syncDisco, _page.AlbumKey.Value + "|" + shape.OrderKey);

            var a = new Artist(artist);
            var e = Entities.Current.Edges;
            bool loading = !a.IsValid || !a.Knows(ArtistFields.Name)
                || (e.ArtistAlbums.State(artist) == EdgeState.Unknown && e.ArtistSingles.State(artist) == EdgeState.Unknown
                    && e.ArtistCompilations.State(artist) == EdgeState.Unknown);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children = [Toolbar(a.IsValid && a.Knows(ArtistFields.Name)), loading ? DiscoSkeleton() : Body(shape, a)],
            };
        }

        NavShape Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            int artist = _page.SelectedSlot!.Value;
            var e = Entities.Current.Edges;
            _ = e.ArtistAlbums.Changed.Value; _ = e.ArtistSingles.Changed.Value; _ = e.ArtistCompilations.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            string filter = _page.AFilter.Value;
            var sort = (LibraryNavSort)Math.Clamp(_page.ASort.Value, 0, 4);
            bool desc = _page.ADesc.Value;
            if (artist <= Table.None) { _sortedCount = 0; return NavShape.Empty; }

            int total = e.ArtistAlbums.Count(artist) + e.ArtistSingles.Count(artist) + e.ArtistCompilations.Count(artist);
            Grow(total);
            _seen.Clear();
            int n = Union(e.ArtistAlbums.Targets(artist), 0);
            n = Union(e.ArtistSingles.Targets(artist), n);
            n = Union(e.ArtistCompilations.Targets(artist), n);
            int shown = LibraryRows.Filter(EntityKind.Album, _all.AsSpan(0, n), filter, _filtered);
            long[]? played = null;
            if (sort == LibraryNavSort.Recents)
            {
                _ = Shell.PlayLog.Version.Value;
                LibraryRows.FillPlayed(EntityKind.Album, _filtered.AsSpan(0, shown), Shell.PlayLog.Recency, _played);
                played = _played;
            }
            _sorter.Order(new LibraryRows(EntityKind.Album, _filtered, shown, played), sort, desc, _perm);
            for (int i = 0; i < shown; i++) _sorted[i] = _filtered[_perm[i]];
            _sortedCount = shown;
            return new NavShape(shown, LibraryNavOrder.OrderKey(new LibraryRows(EntityKind.Album, _sorted, shown)), "");
        }

        int Union(ReadOnlySpan<int> slots, int n)
        {
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] > Table.None && _seen.Add(slots[i])) _all[n++] = slots[i];
            return n;
        }

        void Grow(int n)
        {
            if (_all.Length >= n) return;
            int size = Math.Max(n, _all.Length * 2);
            _all = new int[size]; _filtered = new int[size]; _perm = new int[size]; _sorted = new int[size]; _played = new long[size];
        }

        /// <summary>The discography's OWN pill + filter (hasCreator false, hasRelease true) and "Go to artist" — rendered even
        /// while the body skeletons, so the controls never flash across a selection change.</summary>
        Element Toolbar(bool named) => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Shrink = 0f, Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Children = [_pill ??= SortViewPill(_page.ASort, _page.ADesc, _page.AView, _page.ASize, hasCreator: false, hasRelease: true),
                                new BoxEl { Grow = 1f }, named ? GoToArtistLink(_goArtist) : new BoxEl()],
                },
                AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Library.Filter), text: _page.AFilter, queryIcon: Icons.Search,
                    grow: 1f, maxFillWidth: 9999f, minHeight: 32f, cornerRadius: Radii.Control),
            ],
        };

        Element Body(NavShape shape, Artist a)
        {
            if (shape.Count == 0)
                return _page.AFilter.Peek().Length > 0 ? EmptyCompact(Loc.Get(Strings.Library.NoMatch)) : LoadingCaption();
            int view = _page.AView.Value, size = _page.ASize.Value;
            bool grid = IsGridView(view), compact = IsCompactView(view);
            // ScrollKey scoped to the ARTIST: switching artists is a legitimate remount with its own restored offset.
            var options = new ListOptions<LibraryNavItem>
            {
                SelectionMode = ItemsSelectionMode.Single, Selection = _discoSel, Controller = _discoCtl, Grow = 1f, OnChange = _pick,
                Scroll = new ScrollOptions { ScrollKey = "lib:disco:" + a.Uri.Text },
                ItemTextTyped = static (_, it) => LibraryRows.TitleOf(it.Kind, it.Slot),
            };
            var layout = grid
                ? RepeatLayout.GridFit((compact ? 84f : 100f) + size * (compact ? 16f : 24f), 8f)
                : RepeatLayout.Stack(compact ? 44f : 60f);
            return new BoxEl
            {
                Key = "disco:" + view + ":" + size + ":" + shape.OrderKey,
                Grow = 1f, Basis = 0f, MinHeight = 0f, Direction = 1, ClipToBounds = grid,
                Padding = grid ? new Edges4(Spacing.M, 0f, Spacing.M, 0f) : default,
                Children = [ItemsView.CreateBound(_items!, grid ? (compact ? _cardCompactT : _cardT) : (compact ? _rowCompactT : _rowT), layout, options)],
            };
        }

        void Pick()
        {
            int i = _discoSel.FirstSelectedIndex;
            if (_syncingSel || i < 0 || i >= _sortedCount) return;
            _page.AlbumKey.Value = KeyOf(EntityKind.Album, _sorted[i]);
            _page.DrillToTracks();
        }

        /// <summary>SNAP-TO-FIRST IS AN AUTO-SELECT, NOT A CORRECTION: it fires only for an EMPTY album key. A key that is
        /// set but absent from the shown list leaves the grid unhighlighted; the tracks column still resolves it.</summary>
        void SyncDisco()
        {
            string key = _page.AlbumKey.Peek();
            if (key.Length == 0)
            {
                if (_sortedCount > 0) _page.AlbumKey.Value = KeyOf(EntityKind.Album, _sorted[0]);
                else if (_discoSel.SelectedCount > 0) SyncSelect(-1);
                return;
            }
            int slot = SlotOfKey(EntityKind.Album, key), idx = -1;
            for (int i = 0; i < _sortedCount; i++) if (_sorted[i] == slot) { idx = i; break; }
            if (idx < 0) { if (_discoSel.SelectedCount > 0) SyncSelect(-1); }
            else if (_discoSel.FirstSelectedIndex != idx) SyncSelect(idx);
        }

        void SyncSelect(int idx)
        {
            _syncingSel = true;
            try { if (idx < 0) _discoSel.DeselectAll(); else _discoSel.Select(idx); }
            finally { _syncingSel = false; }
            if (idx >= 0) _discoCtl.StartBringItemIntoView(idx);
        }

        static Element DiscoSkeleton()
        {
            var blocks = new Element[8];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = new BoxEl { Height = 148f, Shrink = 0f, Corners = Radii.CardAll, Fill = Tok.FillCardDefault };
            return new BoxEl { Direction = 1, Grow = 1f, Padding = Edges4.All(Spacing.M), Gap = Spacing.S, ClipToBounds = true, Children = blocks };
        }
    }

    // ══ 6. THE PANES' SHARED PIECES (hero 104, the action row, the hand-rolled skeleton) ═════════════════════════════

    /// <summary>The compact hero: 104 cover (r 8, no elevation) · eyebrow · the title LINK (28/36/600, 2 lines, accent on
    /// hover) · the attribution · the meta line. The library is accent-NEUTRAL: nothing here reads a palette.</summary>
    static Element PaneHero(string? cover, string eyebrow, string title, Action open, Element attribution, string meta) => new BoxEl
    {
        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, Shrink = 0f,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Children =
        [
            new BoxEl { Width = 104f, Height = 104f, Shrink = 0f, Corners = Radii.CardAll, ClipToBounds = true,
                        Children = [Controls.Artwork(cover, 104f, 104f, Radii.Card, decodePx: 256)] },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f, MinWidth = 0f,
                Children =
                [
                    Design.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary },
                    new BoxEl
                    {
                        Corners = Radii.ControlAll, Direction = 1,
                        Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Margin = new Edges4(-Spacing.S, -Spacing.XXS, -Spacing.S, -Spacing.XXS),
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
                        Children =
                        [
                            new TextEl(title)
                            {
                                Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                                BrushTransitionMs = Design.Motion.Faster, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    }.Interactive(Interaction.Subtle),
                    attribution,
                    new TextEl(meta) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
        ],
    };

    /// <summary>Play (the SYSTEM accent — the one stated exception) · the 40×40 Shuffle fab · Save/Follow · the Subtle/Small
    /// "View full album" doorway (albums only). Never a second accent CTA.</summary>
    static Element PaneActions(Action play, Action shuffle, Element save, Element? viewFull)
    {
        Element fab = new BoxEl
        {
            Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(40f),
            HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = shuffle,
            Children = [Icon(Icons.Shuffle, 16f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Padding = new Edges4(Spacing.XL, 0f, Spacing.XL, Spacing.M),
            Children = viewFull is null
                ? [Controls.Play(Tok.AccentDefault, play), Controls.Named(fab, Loc.Get(Strings.Detail.Shuffle)), save]
                : [Controls.Play(Tok.AccentDefault, play), Controls.Named(fab, Loc.Get(Strings.Detail.Shuffle)), save, viewFull],
        };
    }

    /// <summary>W7: HAND-AUTHORED solid blocks — no shimmer pulse, no blur reveal (a pulsing skeleton here is wrong).</summary>
    static Element PaneSkeleton()
    {
        var bars = new Element[6];
        for (int i = 0; i < bars.Length; i++)
            bars[i] = new BoxEl { Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault, Margin = new Edges4(0f, Spacing.S, 0f, 0f) };
        return new BoxEl
        {
            Direction = 1, Padding = Edges4.All(Spacing.XL), Gap = Spacing.L,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = 104f, Height = 104f, Corners = Radii.CardAll, Fill = Tok.FillCardDefault },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Gap = Spacing.S,
                            Children =
                            [
                                new BoxEl { Width = 80f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                                new BoxEl { Width = 200f, Height = 22f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                                new BoxEl { Width = 140f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                            ],
                        },
                    ],
                },
                new BoxEl { Direction = 1, Gap = Spacing.S, Children = bars },
            ],
        };
    }
}
