using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// "Your Library" — WaveeMusic's master–detail skeleton (AlbumsLibraryView / ArtistsLibraryView). A LEFT navigator (a
// sort/view-size dropdown + filter, then a list-or-grid bound to the cached LibraryStore) and a RIGHT pane that is a
// COMPACT detail panel (104px hero + actions + content) for the selected item — NOT the full page. Albums/podcasts =
// two columns; ARTISTS = three (artist list | discography | the picked release's tracks). Columns are GridSplitter-
// resizable. Selection drives the panes via stable per-selection loadables (UseResource re-driven by the selection
// key), so picking a different item reactively re-skins the pane in place — no navigation, no stale freeze.
sealed class LibraryPage : Component
{
    readonly string _kind;   // "albums" | "artists" | "podcasts"

    readonly IAppSettings? _settings;                  // per-kind persisted state (seed in ctor, save on change)
    readonly Signal<string> _selectedKey;              // selected item route key (album:/artist:/show: + uri)
    readonly Signal<string> _albumKey;                 // artists only: the release picked in the discography (3rd column)
    readonly Signal<int> _view;                        // 0 CompactList · 1 List · 2 CompactGrid · 3 Grid
    readonly Signal<int> _sort;                        // 0 Recents · 1 Recently added · 2 Alphabetical · 3 Creator · 4 Release date
    readonly Signal<bool> _desc;
    readonly Signal<int> _size;                        // grid card size: 0 S · 1 M · 2 L
    readonly Signal<string> _filter = new("");         // NOT persisted — the search filter starts empty each launch
    readonly Signal<float> _leftW, _midW;              // resizable column widths
    // Artists column-2 (discography) controls, mirrored from the left picker's set: sort/direction/view-type/grid-size.
    readonly Signal<int> _aSort, _aView, _aSize;
    readonly Signal<bool> _aDesc;
    readonly Signal<string> _aFilter = new("");        // NOT persisted
    readonly SelectionModel _navSel = new();           // master list/grid single-selection (the WinUI ItemsView selection)
    // The imperative handle beside it, for ONE thing: scrolling a programmatically-moved selection back into view
    // (WinUI SetCurrentElementIndex → StartBringIntoView). Load-bearing for search select-in-place, where the committed
    // item is usually far outside the viewport the browse list was left at.
    readonly ItemsViewController _navCtl = new();
    // Search-mode drill-down selection — INDEPENDENT of the browse selection so clearing the query restores browse.
    readonly Signal<string> _sArtist = new("");        // selected matched-artist uri (artists view)
    readonly Signal<string> _sAlbum = new("");         // selected matched-album uri
    // One reveal group for THIS page's search columns (left | discography | tracks): the engine settles all three in a
    // single window instead of three unsynchronized blur-reveals. Per instance, so two library tabs never couple.
    readonly object _skelGroup = new();
    Services? _svcRef;                                 // cached in Render → play a track from the search facets
    ActionServices? _actsRef;                          // cached in Render → the library items' drag payloads (resolver + rootlist)
    // Responsive collapse (F2): under a narrow content area the multi-column row becomes a single-column breadcrumb
    // drill-in. `_collapsed` is written from the root OnBoundsChanged (value-gated, hysteresis via LibraryLayoutBreakpoints);
    // `_depth` is the visible level (0 master · 1 discography/detail · 2 tracks). Both are ignored in the wide layout.
    readonly Signal<bool> _collapsed = new(false);
    readonly Signal<int> _depth = new(0);
    // Guards the PROGRAMMATIC selection writes in SyncNav/SyncDisco. ItemsView forwards every SelectionModel mutation —
    // including one this page just made itself — to the change handler, so a plain re-sync re-entered the USER-pick path
    // and did its side effects: Select() wiped the persisted _albumKey (the saved release never survived a launch) and
    // the discography sync fired onDrill, skipping the collapsed discography level entirely. A programmatic sync is a
    // VIEW update, never a preference.
    bool _syncingSel;

    static readonly string[] NoSuggest = Array.Empty<string>();
    // Full-text search is the only debounced read (see Render): a fast typist fires ONE library search, not one per
    // keystroke. Deliberately shorter than the omnibar's 250ms — this corpus is local/cache-only.
    const float SearchDebounceMs = 180f;
    // The search resource keeps the PREVIOUS query's results mounted while the next one loads (stale-while-revalidate),
    // which is what replaced the hand-rolled snapshot signal this page used to carry.
    static readonly ResourceOptions KeepPrevious = new() { KeepPreviousData = true };
    // Shimmer stand-ins: the row builders below are the ONE row definition, so the skeleton is derived from them with a
    // blank item rather than from a second hand-authored tree. They are real (non-null) instances because those builders
    // read .Uri/.Name/.Match unconditionally.
    static readonly LibraryArtistGroup SkelArtist = new("", "", null, 0, 0, Array.Empty<LibraryAlbumGroup>());
    static readonly LibraryAlbumGroup SkelAlbum = new("", "", null, 0, AlbumKind.Album, 0, 0, Array.Empty<LibraryTrackHit>());
    static readonly LibraryTrackHit SkelTrack = new("", "", null, 0, 0, 0);
    // Immediate typeahead with small, local row motion. Stable URI keys let retained hits stay put; only actual
    // insert/remove/reorder changes animate, so fast typing never pulses the entire three-pane surface.
    static readonly LayoutTransition SearchRowChange = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(90f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: 3f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(70f, Easing.SmoothOut));

    // Seed every persisted signal from settings in the ctor (like the sidebar width) so the FIRST frame already uses the
    // saved widths/sort/view/selection — no default→saved flash. A null store (or a missing key) falls back to the key's
    // default. Filter signals are created empty above and never persisted.
    public LibraryPage(string kind, IAppSettings? settings = null)
    {
        _kind = kind; _settings = settings;
        float Gf(SettingKey<float> k) => settings is null ? k.Default : settings.Get(k);
        int Gi(SettingKey<int> k) => settings is null ? k.Default : settings.Get(k);
        bool Gb(SettingKey<bool> k) => settings is null ? k.Default : settings.Get(k);
        string Gs(SettingKey<string> k) => settings is null ? k.Default : settings.Get(k);

        _leftW = new(Gf(LibraryStateKeys.LeftW(kind)));
        _midW = new(Gf(LibraryStateKeys.MidW(kind)));
        _sort = new(Gi(LibraryStateKeys.Sort(kind)));
        _desc = new(Gb(LibraryStateKeys.Desc(kind)));
        _view = new(Gi(LibraryStateKeys.View(kind)));
        _size = new(Gi(LibraryStateKeys.Size(kind)));
        _selectedKey = new(Gs(LibraryStateKeys.Selected(kind)));
        _albumKey = new(Gs(LibraryStateKeys.AlbumKey(kind)));
        _aSort = new(Gi(LibraryStateKeys.AlbumSort(kind)));
        _aDesc = new(Gb(LibraryStateKeys.AlbumDesc(kind)));
        _aView = new(Gi(LibraryStateKeys.AlbumView(kind)));
        _aSize = new(Gi(LibraryStateKeys.AlbumSize(kind)));
    }

    readonly record struct NavItem(Image? Cover, string Title, string Subtitle, string Uri, bool Circular, string RouteKey, int Year);

    bool IsArtists => _kind == "artists";
    bool HasCreator => _kind != "artists";    // album → artist, podcast → publisher
    bool HasRelease => _kind == "albums";

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        _actsRef = UseContext(ActionServices.Slot);
        var store = UseContext(LibraryStore.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var ui = UseContext(ShellUi.Slot);   // rail state (Task B4): the 3-column artist layout tightens its mid pane when the rail is open
        if (svc is null || store is null) return new BoxEl { Grow = 1f };
        _svcRef = svc;
        var shown = Filtered(Project(store));
        // Warm the collection cover art at the kind-matched decode size the moment the list lands, so a first scroll
        // reveals resident textures instead of decoding+uploading on the UI thread mid-scroll (the first-pass jank).
        // Prefetch priority → background workers (visible cards still decode first); idempotent (a re-hit cache entry is a
        // dictionary lookup), so a re-render costs nothing. The engine ImageCache + MemoryGovernor bound the residency.
        int warmPx = _size.Value == 0 ? 64 : _size.Value == 2 ? 256 : 168;
        foreach (var it in shown)
            if (it.Cover?.Url is { Length: > 0 } warmUrl) PrefetchImage(warmUrl, warmPx);
        // Full-text library search REPLACES the browse title-filter for the album/artist views: typing searches the
        // followed artists ▸ their albums ▸ tracks (artists view) or saved albums ▸ tracks (albums view), grouped +
        // highlighted. Podcasts keep the plain title filter. Cache-only + off-thread; keyed on kind+query so it re-drives
        // per query only. Placed with the other loads below (fixed hook order); the flag/query are computed here.
        string raw = _filter.Value.Trim();   // subscribe — the RAW box text
        // TWO reads of one box, on purpose. The browse title-filter (`Filtered` above) narrows a list already in memory,
        // so it must stay INSTANT on the raw text. The full-text search hits the store-backed catalog, so it rides a
        // trailing-edge debounce: one search per pause, not one per keystroke. UNCONDITIONAL and in a fixed position —
        // all three kinds are the same LibraryPage type (see the hook-order note below), so this must never be branched.
        string query = UseDebouncedValue(() => _filter.Value.Trim(), SearchDebounceMs).Value;   // subscribe
        // Full search needs the persistent store-backed catalog (its cached discographies + tracklists). On the fake/demo
        // backend (RealStore null) it has no corpus, so we keep the plain browse title-filter there instead of a dead
        // "Nothing matches". Podcasts always keep the title filter.
        // The MODE flips on the RAW text, not the debounced one: a cleared box must restore the browse panes on the same
        // frame (select-in-place clears the filter as part of its commit, and a 180ms limbo there reads as a dead click).
        // The un-answered window that the debounce opens is covered by `awaiting` below, which drives the shimmer.
        bool fullSearch = raw.Length > 0 && _kind != "podcasts" && svc.RealStore is not null;

        // Keep the ItemsView SelectionModel pointed at the selected item across load/filter/sort/view + auto-select first.
        // Skips while the search results view is up — selection there is driven by result clicks. Includes fullSearch in
        // the key so toggling in/out of search re-syncs the browse selection.
        UseEffect(() => SyncNav(shown, fullSearch), NavHash(shown) + "|" + _selectedKey.Value + "|" + fullSearch);
        // Persist per-kind page state (column widths persist on drag-end via the grips, NOT here). Keyed on a composite of
        // every persisted signal so it writes only on discrete user actions — never per-frame. Filter is excluded on purpose.
        UseEffect(SaveState, $"{_sort.Value}|{_desc.Value}|{_view.Value}|{_size.Value}|{_selectedKey.Value}|{_albumKey.Value}|{_aSort.Value}|{_aDesc.Value}|{_aView.Value}|{_aSize.Value}");

        string sel = _selectedKey.Value;   // subscribe
        bool artists = IsArtists;
        bool railOpen = ui?.RailOpen.Value ?? false;   // subscribe → re-render (tighter mid pane) on a rail toggle
        string albumKey = _albumKey.Value;   // subscribe

        // Hooks must NEVER be branched. All three kinds are the same LibraryPage type, so a branched hook count let the
        // reconciler reuse a sibling's hook slot → an EffectCell→AsyncResourceCell cast crash. Call all three loads
        // unconditionally in a FIXED order; the off-kind ones key on "" → resolve to Empty with no real fetch.
        var detail = UseResource(ct => LoadDetail(svc, artists ? "" : sel, ct), DetailModel.Empty, artists ? "" : sel).Loadable;
        var artist = UseResource(ct => LoadArtist(svc, artists ? sel : "", ct), EmptyArtist(""), artists ? sel : "").Loadable;
        var albumTracks = UseResource(ct => LoadDetail(svc, artists ? albumKey : "", ct), DetailModel.Empty, artists ? albumKey : "").Loadable;
        // KeepPreviousData IS the stale-while-revalidate this page used to hand-roll with a `_searchSnapshot` signal +
        // two effects: on a query change the resource holds the previous Ready results while the next ones load, so the
        // three panes never flash rows → ellipsis → rows. The snapshot is gone; the resource owns the kept value.
        var searchRes = UseResource(ct => SearchLib(svc, _kind, fullSearch ? query : "", ct), LibrarySearchResults.Empty,
            fullSearch ? _kind + "|" + query : "", KeepPrevious);
        var search = searchRes.Loadable;
        // "No answer for what is on screen yet": either a fetch is in flight, or the debounce has not caught up with the
        // typed text. Drives the shimmer (only when there is nothing worth keeping) and the refining cue on the counts.
        bool awaiting = searchRes.IsFetching.Value || query != raw;   // subscribe
        var searchValue = search.Value.Value;   // subscribe — the column Content closures capture this

        // Resolve the hierarchical results once (subscribes). They drive a DRILL-DOWN across the master-detail columns:
        // matched artists (left) ▸ the selected artist's matched albums (middle) ▸ the selected album's matched tracks
        // (right). Auto-select the first artist/album so results appear immediately. Browse selection is untouched until
        // a hit is explicitly clicked (SelectArtist/SelectAlbum commit into it), so clearing the query restores the user.
        var sr = fullSearch ? searchValue : LibrarySearchResults.Empty;
        // The shimmer gate, threaded to every search column so all three share ONE boundary shape and ONE reveal group.
        var skel = new SearchSkelState(search, awaiting && searchValue.IsEmpty, _skelGroup, awaiting);
        string rhash = sr.Artists.Count + ":" + sr.Albums.Count + ":" + query;
        string sArtist = _sArtist.Value;   // subscribe
        string sAlbum = _sAlbum.Value;     // subscribe

        UseEffect(() => AutoSelectTop(sr, fullSearch), "sauto|" + fullSearch + "|" + rhash);
        UseEffect(() => AutoSelectAlbum(sr, fullSearch && artists), "salbum|" + sArtist + "|" + rhash);

        // F2 — responsive collapse. `_collapsed` is written from the root's OnBoundsChanged below; entering collapsed
        // resets the drill-in to the master list (depth 0) so a narrow window always starts at the list.
        bool collapsed = _collapsed.Value;   // subscribe
        UseEffect(() => { if (collapsed) _depth.Value = 0; }, "collapse|" + collapsed);

        Element inner;
        if (collapsed)
            inner = CollapsedLayout(shown, sr, skel, fullSearch, sArtist, sAlbum, svc, bridge, artist, albumTracks, detail);
        else
        {
            Element right = fullSearch
                ? (artists ? SearchArtistColumns(sr, skel, sArtist, sAlbum, railOpen) : SearchAlbumDetail(sr, skel, sAlbum))
                : (artists ? ArtistColumns(artist, albumTracks, svc, bridge, sel.Length > 0, railOpen)
                           : DetailColumn(detail, svc, bridge, sel.Length > 0));
            inner = new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children = [LeftColumn(shown, sr, skel, fullSearch, sArtist, sAlbum), Grip(_leftW, 240f, 560f, () => _settings?.Set(LibraryStateKeys.LeftW(_kind), _leftW.Peek())), right],
            };
        }

        // Self-measure the content-area width (the real slot — accounts for the sidebar/rail without any ShellUi math) and
        // flip `_collapsed` across the breakpoint. Value-gated write → re-renders only on a boundary cross, no feedback loop.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, AlignItems = FlexAlign.Stretch,
            OnBoundsChanged = r =>
            {
                if (r.W <= 0f) return;
                bool c = LibraryLayoutBreakpoints.Collapsed(r.W, _collapsed.Peek());
                if (c != _collapsed.Peek()) _collapsed.Value = c;
            },
            Children = [inner],
        };
    }

    void DrillToTracks() { if (_collapsed.Peek()) _depth.Value = 2; }

    // ── F2: collapsed single-column drill-in (master ▸ discography/detail ▸ tracks) with breadcrumbs ──
    Element CollapsedLayout(NavItem[] shown, LibrarySearchResults sr, SearchSkelState skel, bool fullSearch,
        string sArtist, string sAlbum, Services svc, PlaybackBridge? bridge,
        Loadable<Artist> artist, Loadable<DetailModel> albumTracks, Loadable<DetailModel> detail)
    {
        int maxDepth = IsArtists ? 2 : 1;
        int depth = Math.Clamp(_depth.Value, 0, maxDepth);   // subscribe

        // Crumb names come from the live loadables (browse) or the matched groups (search); "…" while a name loads.
        string level1 = IsArtists
            ? Crumb(fullSearch ? (FindArtist(sr, sArtist)?.Name ?? "") : artist.Value.Value.Name)
            : Crumb(fullSearch ? (FindAlbum(sr.Albums, sAlbum)?.Name ?? "") : detail.Value.Value.Title);
        string level2 = Crumb(fullSearch
            ? (FindAlbum(FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>(), sAlbum)?.Name ?? "")
            : albumTracks.Value.Value.Title);

        var crumbs = new List<string>(3) { KindCrumb() };
        if (depth >= 1) crumbs.Add(level1);
        if (depth >= 2) crumbs.Add(level2);

        Element body = depth == 0
            ? new BoxEl { Direction = 1, Grow = 1f, Children = [Toolbar(), fullSearch ? LeftSearchBody(sr, skel, sArtist, sAlbum) : ListBody(shown)] }
            : depth == 1
                ? (IsArtists ? CollapsedDiscography(sr, skel, fullSearch, sArtist, sAlbum, artist)
                             : CollapsedDetail(sr, skel, fullSearch, sAlbum, detail, svc, bridge))
                : CollapsedTracks(sr, skel, fullSearch, sArtist, sAlbum, albumTracks, svc, bridge);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [CollapsedCrumbBar(crumbs), body],
        };
    }

    Element CollapsedCrumbBar(IReadOnlyList<string> crumbs) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Fill = Tok.FillLayerDefault,
        Children =
        [
            new BoxEl { Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                Children = [BreadcrumbBar.Create(crumbs, i => _depth.Value = i)] },
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    // Artists view, depth 1 — the selected artist's discography (browse) or matched albums (search).
    Element CollapsedDiscography(LibrarySearchResults sr, SearchSkelState skel, bool fullSearch, string sArtist, string sAlbum, Loadable<Artist> artist) => Pane with
    {
        Key = "col:disco", Grow = 1f, Basis = 0f,
        Children = [ fullSearch
            ? SearchSkel(skel, SkelAlbumRow, () => SearchScroll(FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>(), a => AlbumRow(a, a.Uri == sAlbum)))
            : Embed.Comp(() => new LibraryArtistPane(artist, _albumKey, _aSort, _aDesc, _aView, _aSize, _aFilter, onDrill: DrillToTracks)) ],
    };

    // Artists view, depth 2 — the selected album's tracks (browse detail pane) or matched tracks (search).
    Element CollapsedTracks(LibrarySearchResults sr, SearchSkelState skel, bool fullSearch, string sArtist, string sAlbum,
        Loadable<DetailModel> albumTracks, Services svc, PlaybackBridge? bridge)
    {
        if (fullSearch)
        {
            var albG = FindAlbum(FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>(), sAlbum);
            var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
            string albumUri = albG?.Uri ?? "";
            return Pane with { Key = "col:tracks", Grow = 1f, Basis = 0f,
                Children = [ SearchSkel(skel, SkelTrackRow, () => SearchScroll(tracks, t => TrackHitRow(t, albumUri))) ] };
        }
        return Pane with { Key = "col:tracks", Grow = 1f, Basis = 0f,
            Children = [Embed.Comp(() => new LibraryDetailPane(albumTracks, false, svc, bridge))] };
    }

    // Albums/podcasts view, depth 1 — the album/show detail (browse) or the album's matched tracks (search).
    Element CollapsedDetail(LibrarySearchResults sr, SearchSkelState skel, bool fullSearch, string sAlbum,
        Loadable<DetailModel> detail, Services svc, PlaybackBridge? bridge)
    {
        if (fullSearch)
        {
            var albG = FindAlbum(sr.Albums, sAlbum);
            var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
            string albumUri = albG?.Uri ?? "";
            return Pane with { Key = "col:detail", Grow = 1f, Basis = 0f,
                Children = [ SearchSkel(skel, SkelTrackRow, () => SearchScroll(tracks, t => TrackHitRow(t, albumUri))) ] };
        }
        return Pane with { Key = "col:detail", Grow = 1f, Basis = 0f,
            Children = [Embed.Comp(() => new LibraryDetailPane(detail, _kind == "podcasts", svc, bridge))] };
    }

    string KindCrumb() => _kind switch
    {
        "artists" => Loc.Get(Strings.Search.Artists),
        "albums" => Loc.Get(Strings.Search.Albums),
        _ => Loc.Get(Strings.Podcast.Show),
    };
    static string Crumb(string s) => s.Length > 0 ? s : "…";

    // The page already lives INSIDE the shell's content card (rounded FileArea + shadow, WaveeShell.cs), so the columns
    // must NOT be nested cards — that double-cards and reads heavy. Depth here is subtle + WinUI: the navigator gets a
    // faint recede layer, the detail panes stay on the base content surface, and a 1px hairline (the resize grip) divides
    // them. Outer corners come free from the content card's rounded clip, so the columns themselves stay square.
    // THE THREE COINCIDENT WHITES, and why the fill alone cannot fix them. The navigator painted
    // FillLayerDefault (#80FFFFFF), the crumb bar painted the same value, and the reading Pane painted NOTHING — so it
    // showed the content region's own #80FFFFFF underlay. Three surfaces, effectively one colour, separated only by an
    // α .059 divider: in light the whole master–detail browser read as one white sheet.
    //
    // The honest measurement, because it decides the fix: over the bare light ground the shell's plate lands ≈249.6,
    // the content region's smoke ≈252.3, the navigator's layer on top of that ≈253.7 and a card rung ≈254.2. Light
    // layering by white-alpha has RUN OUT OF HEADROOM — the whole remaining ladder is 2/255, and no choice of white
    // fill separates these columns. That is not a reason to skip the rung; it is the reason the WinUI card recipe is a
    // FILL PLUS A STROKE and not a fill. So: the reading pane takes the card rung (CardBackgroundFillColorDefault,
    // the rung a content surface is supposed to be on), the navigator keeps the layer rung, and the 1-DIP
    // StrokeCardDefault seam in the column grip does the separating work the fills physically cannot.
    static BoxEl NavPanel => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillLayerDefault };
    static BoxEl Pane => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillCardDefault };

    void Select(NavItem it)
    {
        _selectedKey.Value = it.RouteKey;
        if (IsArtists) _albumKey.Value = "";   // a new artist resets the 3rd-column release selection
    }

    // Persist the current per-kind page state (invoked by the composite-keyed effect on any change). Widths are handled
    // separately (drag-end). Album-side keys only apply to the artists (3-column) view.
    void SaveState()
    {
        if (_settings is null) return;
        _settings.Set(LibraryStateKeys.Sort(_kind), _sort.Peek());
        _settings.Set(LibraryStateKeys.Desc(_kind), _desc.Peek());
        _settings.Set(LibraryStateKeys.View(_kind), _view.Peek());
        _settings.Set(LibraryStateKeys.Size(_kind), _size.Peek());
        _settings.Set(LibraryStateKeys.Selected(_kind), _selectedKey.Peek());
        if (IsArtists)
        {
            _settings.Set(LibraryStateKeys.AlbumKey(_kind), _albumKey.Peek());
            _settings.Set(LibraryStateKeys.AlbumSort(_kind), _aSort.Peek());
            _settings.Set(LibraryStateKeys.AlbumDesc(_kind), _aDesc.Peek());
            _settings.Set(LibraryStateKeys.AlbumView(_kind), _aView.Peek());
            _settings.Set(LibraryStateKeys.AlbumSize(_kind), _aSize.Peek());
        }
    }

    // ── data ──
    NavItem[] Project(LibraryStore store) => _kind switch
    {
        "artists" => Warm(store.EnsureArtists, store.Artists).Select(a => new NavItem(a.Image, a.Name, Loc.Get(Strings.Search.TypeArtist), a.Uri, true, "artist:" + a.Uri, 0)).ToArray(),
        "podcasts" => Warm(store.EnsureShows, store.Shows).Select(s => new NavItem(s.Cover, s.Name, s.Publisher, s.Uri, false, "show:" + s.Uri, 0)).ToArray(),
        _ => Warm(store.EnsureAlbums, store.Albums).Select(a => new NavItem(a.Cover, a.Name, a.Artists.Count > 0 ? a.Artists[0].Name : "", a.Uri, false, "album:" + a.Uri, a.Year)).ToArray(),
    };

    static IReadOnlyList<T> Warm<T>(Action ensure, Loadable<IReadOnlyList<T>> cell) { ensure(); return cell.Value.Value; }

    NavItem[] Filtered(NavItem[] items)
    {
        string q = _filter.Value.Trim(); int sort = _sort.Value; bool desc = _desc.Value;   // subscribe
        var arr = (q.Length == 0 ? items : items.Where(it => it.Title.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();
        Comparison<NavItem>? cmp = sort switch
        {
            2 => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
            3 => (a, b) => string.Compare(a.Subtitle, b.Subtitle, StringComparison.OrdinalIgnoreCase),
            4 => (a, b) => a.Year.CompareTo(b.Year),
            _ => null,
        };
        if (cmp is not null) Array.Sort(arr, cmp);
        else if (sort == 1) Array.Reverse(arr);   // "recently added" ≈ reverse of the cached (recents) order
        if (desc && cmp is not null) Array.Reverse(arr);
        return arr;
    }

    static string Strip(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : "";
    static Artist EmptyArtist(string uri) => new("", uri, "", null);

    static Task<DetailModel> LoadDetail(Services svc, string routeKey, CancellationToken ct)
    {
        if (routeKey.Length == 0) return Task.FromResult(DetailModel.Empty);
        var (kind, id) = DetailPage.ParseDetail(new Route(routeKey));
        return DetailPage.LoadAsync(svc, kind, id, ct);
    }

    static Task<Artist> LoadArtist(Services svc, string routeKey, CancellationToken ct)
    {
        string uri = Strip(routeKey, "artist:");
        return uri.Length == 0 ? Task.FromResult(EmptyArtist("")) : svc.Library.GetArtistAsync(uri, ct: ct);
    }

    // ── left navigator ──
    Element LeftColumn(NavItem[] shown, LibrarySearchResults sr, SearchSkelState skel, bool fullSearch, string sArtist, string sAlbum) => NavPanel with
    {
        Width = _leftW.Value, Shrink = 0f,
        // Searching swaps the browse list for the top-level matches — matched artists (artists view) or matched albums
        // (albums view); the detail columns drill into the selection. Otherwise the normal self-scrolling ItemsView.
        Children = [Toolbar(title: true), fullSearch ? LeftSearchBody(sr, skel, sArtist, sAlbum) : ListBody(shown)],
    };

    /// <summary>The master column's head: the page's big-type TITLE, then the sort/view picker, then the filter box.
    ///
    /// <para>The title is <see cref="WaveeType.PageHero"/> — the same 28/36/600 moment Search's directory, History and
    /// every detail page open on — and it lives INSIDE this toolbar rather than above the columns because the toolbar
    /// IS this page's header: the library is a master–detail browser whose right-hand panes are owned by whatever is
    /// selected, so a full-width band above them would be a title for three surfaces at once. The master column floors
    /// at 240 DIP (the library splitter's min), which fits "Podcasts" — the longest of the three — on one line.</para>
    ///
    /// <para><paramref name="title"/> is false in the COLLAPSED single-column layout, where <c>CollapsedCrumbBar</c>
    /// already names the kind as the breadcrumb root: two titles for one column is the double-title this converges
    /// away from, and the crumb has to stay because it is also the drill-out affordance.</para></summary>
    Element Toolbar(bool title = false) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.M, title ? Spacing.L : Spacing.M, Spacing.M, Spacing.S),
        Children = title
            ?
            [
                WaveeType.PageHero(ShellNav.Dest(_kind).Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
                ToolbarPicker(),
                ToolbarFilter(),
            ]
            : [ToolbarPicker(), ToolbarFilter()],
    };

    Element ToolbarPicker() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center,
        Children = [Embed.Comp(() => new LibrarySortView(_sort, _desc, _view, _size, HasCreator, HasRelease)), new BoxEl { Grow = 1f }],
    };

    Element ToolbarFilter()
        => AutoSuggestBox.Create(NoSuggest, Loc.Get(Strings.Library.Filter), text: _filter, queryIcon: Icons.Search,
            grow: 1f, maxFillWidth: 9999f, minHeight: 32f, cornerRadius: Radii.Control);

    // The master list/grid IS the engine's ItemsView (WinUI ListView/GridView): single-selection, keyboard nav, and the
    // proper accent-bar / selected-state chrome painted by the item container. Keyed by the displayed set so a
    // filter/sort/view change remounts with the right slots; the SelectionModel is external so selection survives.
    // NavRow/NavCard supply only the CONTENT — the container paints selection.
    Element ListBody(NavItem[] shown)
    {
        int view = _view.Value; int size = _size.Value;   // subscribe
        // A filtered-to-nothing column IS an empty state (rail scale - these panes floor at 220-300 DIP); an
        // unfiltered empty column is still LOADING, and a big-type "nothing here" would be a lie about it.
        if (shown.Length == 0)
            return _filter.Peek().Length > 0
                ? EmptyState.Compact(Loc.Get(Strings.Library.NoMatch))
                : new BoxEl { Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL), Children = [Caption("…").Secondary()] };

        bool grid = view >= 2; bool compact = view == 0 || view == 2;
        string key = "nav:" + view + ":" + size + ":" + NavHash(shown);

        if (grid)
            return new BoxEl
            {
                Key = key, Grow = 1f, Direction = 1, Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
                Children = [ItemsView.Create(shown.Length, i => NavCardContent(shown[i], compact),
                    RepeatLayout.GridFit((compact ? 88f : 116f) + size * (compact ? 16f : 24f), 8f),
                    new ListOptions { SelectionMode = ItemsSelectionMode.Single, Selection = _navSel, Selector = SelectorVisual.Border, OnChange = () => OnNavSel(shown), Controller = _navCtl, Grow = 1f })],
            };

        return new BoxEl
        {
            Key = key, Grow = 1f, Direction = 1,
            Children = [ItemsView.List(shown.Length, i => NavRowContent(shown[i], compact),
                selectionMode: ItemsSelectionMode.Single, selection: _navSel,
                onSelectionIndexChanged: i => OnNavSelIdx(shown, i), controller: _navCtl,
                itemExtent: compact ? 40f : 60f, grow: 1f)],
        };
    }

    void OnNavSel(NavItem[] shown) => OnNavSelIdx(shown, _navSel.FirstSelectedIndex);
    void OnNavSelIdx(NavItem[] shown, int i)
    {
        // ItemsView reports EVERY SelectionModel mutation, including the re-sync SyncNav performs itself. Only a genuine
        // user pick may reset the discography key or drill the collapsed view (see _syncingSel).
        if (_syncingSel || i < 0 || i >= shown.Length) return;
        Select(shown[i]);
        if (_collapsed.Peek()) _depth.Value = 1;   // collapsed: tapping a master item drills into it
    }

    /// <summary>Move the model to <paramref name="idx"/> WITHOUT re-entering the user-pick handler, then scroll it into
    /// view. The bring-into-view is MINIMAL (alignmentRatio NaN, the default BringIntoViewOptions), so a row already on
    /// screen never moves; it only acts when the selection jumped somewhere the viewport isn't — the launch restore and
    /// the search select-in-place commit. Unanimated on purpose: a smooth scroll across a 10k-row library is not a cue,
    /// it's a journey. A no-op before the viewport realizes.</summary>
    void SyncSelect(SelectionModel model, int idx)
    {
        _syncingSel = true;
        try { if (idx < 0) model.DeselectAll(); else model.Select(idx); }
        finally { _syncingSel = false; }
        if (idx >= 0) _navCtl.StartBringItemIntoView(idx);
    }

    void SyncNav(NavItem[] shown, bool fullSearch)
    {
        if (fullSearch) return;   // search results view drives selection via clicks (+ the search-empty clear effect)
        if (shown.Length == 0)
        {
            // A title filter (podcasts) that matched nothing → drop the stale selection so the right pane clears. An
            // empty set with NO filter text is the still-loading state — keep the persisted selection (launch restore).
            if (_filter.Peek().Length > 0 && _selectedKey.Peek().Length > 0)
            {
                _selectedKey.Value = "";
                if (IsArtists) _albumKey.Value = "";
                if (_navSel.SelectedCount > 0) SyncSelect(_navSel, -1);
            }
            return;
        }
        string key = _selectedKey.Peek();
        int idx = key.Length == 0 ? 0 : Array.FindIndex(shown, it => it.RouteKey == key);
        // No selection, or a key this set no longer contains (an unfollowed item, a not-yet-streamed list) → adopt the
        // first row. Written out explicitly because the re-sync below no longer round-trips through the user-pick
        // handler to do it as a side effect (see _syncingSel); the outcome is the same one this always produced.
        if (idx < 0 || key.Length == 0) { idx = 0; Select(shown[0]); }
        if (_navSel.FirstSelectedIndex != idx) SyncSelect(_navSel, idx);
    }

    // ── search-mode selection (drill-down) ──
    void AutoSelectTop(LibrarySearchResults sr, bool fullSearch)
    {
        if (!fullSearch) return;
        if (IsArtists)
        {
            if (sr.Artists.Count == 0) { if (_sArtist.Peek().Length > 0) { _sArtist.Value = ""; _sAlbum.Value = ""; } return; }
            if (FindArtist(sr, _sArtist.Peek()) is null) { _sArtist.Value = sr.Artists[0].Uri; _sAlbum.Value = ""; }
        }
        else
        {
            if (sr.Albums.Count == 0) { if (_sAlbum.Peek().Length > 0) _sAlbum.Value = ""; return; }
            if (FindAlbum(sr.Albums, _sAlbum.Peek()) is null) _sAlbum.Value = sr.Albums[0].Uri;
        }
    }

    void AutoSelectAlbum(LibrarySearchResults sr, bool active)
    {
        if (!active) return;
        var g = FindArtist(sr, _sArtist.Peek());
        var albums = g?.Albums;
        if (albums is not { Count: > 0 }) { if (_sAlbum.Peek().Length > 0) _sAlbum.Value = ""; return; }
        if (FindAlbum(albums, _sAlbum.Peek()) is null) _sAlbum.Value = albums[0].Uri;
    }

    // Search hits are SELECT-IN-PLACE, not destinations. "Your Library" is a master–detail BROWSER (see the class note
    // above): its entire job is to keep you in the panes you are browsing. Routing an artist/album hit through the shell
    // ejected the user onto a full page — losing the three-column context, the resized columns and the query they were
    // refining — for what is structurally the same act as clicking a row in the left navigator. Song hits already play
    // in place (PlayTrack below); artist/album hits now COMMIT into the persisted browse selection instead, clear the
    // filter so the browse panes come back, and let SyncNav / SyncDisco point the ItemsView selections at the result.
    // The real page is still one click away from the pane the user lands in: its hero title and "Go to artist" navigate.
    // The commit RULE (which keys move, and the collapsed drill level) is the pure LibrarySelectionCommit; this half is
    // only the signal writes.
    void SelectArtist(string uri)
    {
        _sArtist.Value = uri; _sAlbum.Value = "";
        Apply(LibrarySelectionCommit.ForArtist(IsArtists, _collapsed.Peek(), uri));
    }
    void SelectAlbum(string uri)
    {
        _sAlbum.Value = uri;
        // In the artists view the owning artist is whatever the results column has selected — a hit is reachable without
        // ever clicking that artist row (the first match auto-selects), so the commit carries it explicitly.
        Apply(LibrarySelectionCommit.ForAlbum(IsArtists, _collapsed.Peek(), uri, _sArtist.Peek()));
    }

    // A null field means "leave that signal alone" — see LibrarySelectionCommit.
    void Apply(in LibrarySelectionCommit c)
    {
        if (c.SelectedKey is { } sk) _selectedKey.Value = sk;
        if (c.AlbumKey is { } ak) _albumKey.Value = ak;
        if (c.ClearFilter && _filter.Peek().Length > 0) _filter.Value = "";
        if (c.Depth is { } d) _depth.Value = d;
    }
    void PlayTrack(string albumUri, int index)
    {
        if (albumUri.Length > 0 && _svcRef is { } svc) _ = svc.Player.PlayAsync(albumUri, System.Math.Max(0, index));
    }

    static LibraryArtistGroup? FindArtist(LibrarySearchResults sr, string uri)
    { if (uri.Length == 0) return null; foreach (var a in sr.Artists) if (a.Uri == uri) return a; return null; }
    static LibraryAlbumGroup? FindAlbum(IReadOnlyList<LibraryAlbumGroup> albums, string uri)
    { if (uri.Length == 0) return null; foreach (var a in albums) if (a.Uri == uri) return a; return null; }

    // ── search-mode column bodies ──
    static Task<LibrarySearchResults> SearchLib(Services svc, string kind, string query, CancellationToken ct)
    {
        if (query.Length == 0) return Task.FromResult(LibrarySearchResults.Empty);
        var scope = kind == "artists" ? LibrarySearchScope.Artists : LibrarySearchScope.Albums;
        return svc.Library.SearchLibraryAsync(query, scope, ct);
    }

    // Left column while searching: the top-level matches (artists, or albums in the albums view).
    Element LeftSearchBody(LibrarySearchResults sr, SearchSkelState skel, string sArtist, string sAlbum) => IsArtists
        ? SearchSkel(skel, SkelArtistRow, () => sr.Artists.Count == 0
            ? SearchMessage(Loc.Get(Strings.Library.NoMatch))
            : SearchScroll(sr.Artists, g => ArtistRow(g, g.Uri == sArtist)))
        : SearchSkel(skel, SkelAlbumRow, () => sr.Albums.Count == 0
            ? SearchMessage(Loc.Get(Strings.Library.NoMatch))
            : SearchScroll(sr.Albums, g => AlbumRow(g, g.Uri == sAlbum, explainMatch: true)));

    // Artists-view detail columns while searching: the selected artist's albums | grip | the selected album's tracks.
    Element SearchArtistColumns(LibrarySearchResults sr, SearchSkelState skel, string sArtist, string sAlbum, bool railOpen)
    {
        var albums = FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>();
        var albG = FindAlbum(albums, sAlbum);
        var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
        string albumUri = albG?.Uri ?? "";

        Element albumPane = Pane with
        {
            Key = "s:albums", Basis = _midW.Value, MinWidth = railOpen ? 220f : 300f, MaxWidth = _midW.Value, Shrink = 1f, Grow = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Albums), skel.Shimmer ? -1 : albums.Count, skel.Refining),
                        SearchSkel(skel, SkelAlbumRow, () => SearchScroll(albums, a => AlbumRow(a, a.Uri == sAlbum)))],
        };
        Element trackPane = Pane with
        {
            Key = "s:tracks", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), skel.Shimmer ? -1 : tracks.Count, skel.Refining),
                        SearchSkel(skel, SkelTrackRow, () => SearchScroll(tracks, t => TrackHitRow(t, albumUri)))],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
            Children = [albumPane, Grip(_midW, 300f, 620f, () => _settings?.Set(LibraryStateKeys.MidW(_kind), _midW.Peek())), trackPane],
        };
    }

    // Albums-view single right pane while searching: the selected album's matched tracks.
    Element SearchAlbumDetail(LibrarySearchResults sr, SearchSkelState skel, string sAlbum)
    {
        var albG = FindAlbum(sr.Albums, sAlbum);
        var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
        string albumUri = albG?.Uri ?? "";
        return Pane with
        {
            Key = "s:detail", Grow = 1f, Basis = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), skel.Shimmer ? -1 : tracks.Count, skel.Refining),
                        SearchSkel(skel, SkelTrackRow, () => SearchScroll(tracks, t => TrackHitRow(t, albumUri)))],
        };
    }

    // ── the search columns' skeleton boundary ──
    /// <summary>Per-render skeleton inputs, threaded to every search column. <paramref name="Shimmer"/> is the decided
    /// "there is nothing worth keeping on screen" gate; <paramref name="Refining"/> is the softer "a newer answer is
    /// coming" cue for the facet counts.</summary>
    readonly record struct SearchSkelState(Loadable<LibrarySearchResults> Search, bool Shimmer, object Group, bool Refining);

    // How many placeholder rows a pending column stands up. Enough to fill a short column without pretending to know
    // the result count.
    const int SkelRows = 6;

    Element SkelArtistRow() => ArtistRow(SkelArtist, false);
    Element SkelAlbumRow() => AlbumRow(SkelAlbum, false);
    Element SkelTrackRow() => TrackHitRow(SkelTrack, "");

    /// <summary>
    /// The engine's skeleton boundary over a search column: shimmer rows DERIVED from the same row builder the real
    /// rows use (one row definition, never a hand-authored second tree), a staggered blur-reveal on load, and one shared
    /// group token so the three columns settle together.
    /// <para>The pending test is widened past <c>Skel.Region</c>'s built-in one on purpose. That one reads the loadable's
    /// state alone, and <c>KeepPreviousData</c> deliberately holds it at Ready across a re-query — correct for a REFINE
    /// (the previous rows stay put, which is the whole point) but wrong for the first query of a session, where the value
    /// being kept is the empty seed and Content would paint "Nothing matches" for the length of the fetch. So: shimmer
    /// while Pending, or while the answer in hand is EMPTY and a newer one is still coming (fetch in flight, or the
    /// debounce not yet caught up with the typed text). Non-empty results are never replaced by a shimmer.</para>
    /// </summary>
    static Element SearchSkel(SearchSkelState skel, Func<Element> rowTemplate, Func<Element> content)
    {
        var loadable = skel.Search;
        bool shimmer = skel.Shimmer;
        return new SkelRegionEl(
            Pending: () => loadable.State.Value == (byte)LoadState.Pending || shimmer,
            Failed: () => loadable.State.Value == (byte)LoadState.Failed,
            Content: content,
            ShimmerSource: () => ShimmerStack(rowTemplate),
            // Rare (KeepPreviousData keeps a prior Ready value through a failed refetch, so only a cold failure lands
            // here) — but a blank column is not an answer, so say so rather than leaving the pane empty.
            OnFailed: () => ErrorState.Build(loadable.Error),
            Reveal: SkelReveal.StaggerRows,
            Style: SkeletonStyle.Default,
            Group: skel.Group);
    }

    // Rows are keyed apart: the row builders carry a per-uri Key that the deriver preserves, and N identical keys among
    // siblings is not a shape the reconciler should ever be handed.
    static Element ShimmerStack(Func<Element> row)
    {
        var kids = new Element[SkelRows];
        for (int i = 0; i < SkelRows; i++) kids[i] = row() with { Key = "skel:" + i };
        return new BoxEl
        {
            Direction = 1, Gap = 2f,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Children = kids,
        };
    }

    static Element SearchScroll<T>(IReadOnlyList<T> items, Func<T, Element> row)
    {
        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++) rows[i] = row(items[i]);
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = 2f,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, PlayerDock.Reserve + Spacing.XL),
            Children = rows,
        }) with { Grow = 1f };
    }

    // `refining` = a newer answer is on its way while these (kept) results stay on screen. The count is the one thing on
    // the header that is about to change, so it — and only it — fades back. No spinner, no bar: the rows are still true.
    static Element FacetHeader(string label, int count, bool refining = false) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
        Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
        Children =
        [
            WaveeType.Eyebrow(label) with { Color = Tok.TextTertiary },
            count >= 0
                ? new TextEl(count.ToString())
                  {
                      Size = 12f, LineHeight = 16f, Weight = 600, BrushTransitionMs = WaveeMotion.Faster,
                      Color = refining ? Tok.TextTertiary with { A = Tok.TextTertiary.A * 0.4f } : Tok.TextTertiary,
                  }
                : new BoxEl(),
        ],
    };

    static Element SearchMessage(string text) => new BoxEl
    {
        Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL),
        Children = [new TextEl(text) { Size = 14f, LineHeight = 20f, Color = Tok.TextTertiary }],
    };

    // ── search-mode rows ──
    // Artists are always top-level results, so an artist that matched through one of its albums/tracks (name unmatched)
    // always carries its "why" caption.
    Element ArtistRow(LibraryArtistGroup g, bool selected) =>
        SelectableRow(g.Image, g.Uri, g.Name, "", circular: true, selected, g.MatchStart, g.MatchLen, () => SelectArtist(g.Uri),
            eyebrow: MatchEyebrow(g.Match));

    // The "why" caption shows ONLY when this album stands as a top-level result (explainMatch) — never in the artists-
    // view drill-down column, where the album is browse context under an already-explained matched artist.
    Element AlbumRow(LibraryAlbumGroup g, bool selected, bool explainMatch = false) =>
        SelectableRow(g.Cover, g.Uri, g.Name, (g.Year > 0 ? g.Year + " · " : "") + KindLabelOf(g.Kind), circular: false, selected, g.MatchStart, g.MatchLen, () => SelectAlbum(g.Uri),
            eyebrow: explainMatch ? MatchEyebrow(g.Match) : null);

    // The WinUI eyebrow: the reason a non-exact hit appeared, quoted from the field that actually matched. Rendered only
    // when the reason is attributable AND is not the hit's own name (name hits are self-evident via the inline
    // highlight). Honesty rule: an unattributable reason (None) renders nothing.
    static string? MatchEyebrow(MatchReason r)
    {
        if (!r.ShouldExplain) return null;
        return r.Kind switch
        {
            LibraryMatchKind.Album => Strings.Library.MatchedAlbum(r.Term!),
            LibraryMatchKind.Track => Strings.Library.MatchedSong(r.Term!),
            _ => null,
        };
    }

    static Element SelectableRow(Image? cover, string uri, string title, string subtitle, bool circular, bool selected,
        int matchStart, int matchLen, Action onClick, string? eyebrow = null)
    {
        var textKids = new List<Element>(3);
        // The eyebrow sits ABOVE the title (WinUI caption/eyebrow order): a small, secondary-color line naming the match
        // reason. Only present for non-name hits with an attributable reason.
        if (!string.IsNullOrEmpty(eyebrow))
            textKids.Add(new TextEl(eyebrow!) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis });
        textKids.Add(SearchHighlight.Row(title, matchStart, matchLen, 14f, 600, Tok.TextPrimary));
        if (subtitle.Length > 0)
            textKids.Add(new TextEl(subtitle) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        return new BoxEl
        {
            Key = "search:" + uri, Animate = SearchRowChange,
            Direction = 0, Height = 56f, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            // The BROWSE-LIST selection language, not a bespoke accent tint. Every other list in the app marks the
            // selected row with the subtle-fill ladder (that is what SelectorVisual/ItemContainer paint); this one
            // painted Tok.AccentSubtle instead, so the SAME state looked like two different states depending on which
            // list you were in - and it spent the page's accent on a row that is not an action.
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = onClick,
            Children =
            [
                new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(circular ? Radii.Full : Radii.Control), ClipToBounds = true,
                    SkeletonOverride = CoverSkeleton(44f, circular ? Radii.Full : Radii.Control),
                    Children = [Surfaces.Artwork(cover, uri.GetHashCode() & 0x7fffffff, 44f, 44f, circular ? Radii.Full : Radii.Control)] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, ClipToBounds = true, Children = textKids.ToArray() },
            ],
        };
    }

    // Artwork's inner CoverShimmer is an opaque component boundary to the skeleton deriver, which would emit ONE default
    // 160px bar inside the cover square. The slot IS the honest placeholder here, so declare it: a same-sized tile in the
    // shimmer's own bar colour, matching every other cover placeholder in the app. Consulted only while deriving.
    static Element CoverSkeleton(float size, float corners) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(corners),
        Fill = SkeletonStyle.Default.BarColor, IsEnabled = false, HitTestVisible = false,
    };

    Element TrackHitRow(LibraryTrackHit t, string albumUri)
    {
        bool showArtwork = !AppearancePrefs.TrackArtworkHidden(_settings);
        var children = new List<Element>(2);
        if (showArtwork)
            children.Add(new BoxEl
            {
                Width = 36f, Height = 36f, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                SkeletonOverride = CoverSkeleton(36f, Radii.Control),
                Children = [Surfaces.Artwork(t.Cover, t.Uri.GetHashCode() & 0x7fffffff, 36f, 36f, Radii.Control)],
            });
        children.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
            Children = [SearchHighlight.Row(t.Title, t.MatchStart, t.MatchLen, 13f, 600, Tok.TextPrimary)],
        });
        return new BoxEl
        {
            Key = "search:" + t.Uri + ":art=" + showArtwork, Animate = SearchRowChange,
            Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            OnClick = () => PlayTrack(albumUri, t.AlbumIndex),
            Children = children.ToArray(),
        }.Interactive(Interaction.Subtle);
    }

    static string KindLabelOf(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };

    static string NavHash(NavItem[] shown) { int h = 17; foreach (var it in shown) h = h * 31 + it.RouteKey.GetHashCode(); return (h & 0x7fffffff).ToString(); }

    Element NavRowContent(NavItem it, bool compact)
    {
        var children = new List<Element>(2);
        if (!compact)
            children.Add(new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(it.Circular ? 20f : 5f), ClipToBounds = true,
                Children = [Surfaces.Artwork(it.Cover, it.Uri.GetHashCode() & 0x7fffffff, 40f, 40f, it.Circular ? 20f : 5f)] });
        children.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
            Children =
            [
                new TextEl(it.Title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                compact ? new BoxEl() : new TextEl(it.Subtitle) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ] });
        return new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Draggable = NavDrag(it), Children = children.ToArray() };
    }

    // Pure content — fills whatever cell the grid layouter hands it (no width passed in); the engine measures it at the
    // slot width so a long title truncates. Circular (artist) covers get extra pad so the round, blurry covers don't touch.
    Element NavCardContent(NavItem it, bool compact)
    {
        float pad = it.Circular ? 16f : Spacing.S;
        var children = new List<Element>(2) { Surfaces.ArtworkFill(it.Cover, it.Circular ? Radii.Full : 6f) };
        if (!compact) children.Add(new TextEl(it.Title) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, AlignSelf = it.Circular ? FlexAlign.Center : FlexAlign.Start });
        return new BoxEl { Direction = 1, Gap = Spacing.S, ClipToBounds = true, Padding = new Edges4(pad, pad, pad, pad), Draggable = NavDrag(it), Children = children.ToArray() };
    }

    /// <summary>A library item is a DRAG SOURCE only — this list has Single selection and no reorder, so there is
    /// nothing to drop INTO it. The kind comes from the item's own route key (the list is per-kind, but the search
    /// facets mix them), which is the one value that is authoritative on every branch.
    /// <para>CLICK-PRIMARY (×2 mouse drag box, WinUI's LISTVIEWBASEITEM_MOUSE_DRAG_THRESHOLD_MULTIPLIER): selecting a
    /// row/card is the constant intent here and dragging one out the exception, so a click landed while the pointer is
    /// still travelling must not be eaten by a drag promotion.</para></summary>
    DragSource NavDrag(NavItem it) => Drag.Source(WaveeDragKinds.Resource,
        () => WaveeResourceDragPayload.ForEntity(KindOfRoute(it.RouteKey), it.Uri, it.Title, it.Cover, _actsRef),
        thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier);

    static WaveeResourceKind KindOfRoute(string routeKey) =>
        routeKey.StartsWith("artist:", StringComparison.Ordinal) ? WaveeResourceKind.Artist
        : routeKey.StartsWith("show:", StringComparison.Ordinal) ? WaveeResourceKind.Show
        : routeKey.StartsWith("pl:", StringComparison.Ordinal) ? WaveeResourceKind.Playlist
        : WaveeResourceKind.Album;

    // ── right pane(s) ──
    Element DetailColumn(Loadable<DetailModel> detail, Services svc, PlaybackBridge? bridge, bool hasSel)
    {
        if (!hasSel) return Placeholder(_kind == "podcasts" ? Strings.Library.SelectShow : Strings.Library.SelectAlbum);
        return Pane with { Key = "lib:detail", Grow = 1f, Basis = 0f,
            Children = [Embed.Comp(() => new LibraryDetailPane(detail, _kind == "podcasts", svc, bridge))] };
    }

    Element ArtistColumns(Loadable<Artist> artist, Loadable<DetailModel> albumTracks, Services svc, PlaybackBridge? bridge, bool hasSel, bool railOpen)
    {
        if (!hasSel) return Placeholder(Strings.Library.SelectArtist);
        Element artistPane = Pane with
        {
            // Basis = the user's chosen width, but Shrink so a narrow shell never paints the grid wider than this column
            // (Shrink=0 + fixed Width let the viewport outgrow the flex slot → discography tiles under the tracks pane).
            // When the rail is open the 3-column sum-of-minimums must fit a narrower content region → drop this floor 300→220.
            Basis = _midW.Value, MinWidth = railOpen ? 220f : 300f, MaxWidth = _midW.Value, Shrink = 1f, Grow = 0f,
            Children = [Embed.Comp(() => new LibraryArtistPane(artist, _albumKey, _aSort, _aDesc, _aView, _aSize, _aFilter, onDrill: DrillToTracks))],
        };
        Element tracksPane = _albumKey.Value.Length > 0
            ? Pane with { Key = "lib:tracks", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
                Children = [Embed.Comp(() => new LibraryDetailPane(albumTracks, false, svc, bridge))] }
            : Pane with { Key = "lib:tracks:empty", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
                Children = [EmptyState.Compact(Loc.Get(Strings.Library.SelectAlbumTracks))] };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
            Children = [artistPane, Grip(_midW, 300f, 620f, () => _settings?.Set(LibraryStateKeys.MidW(_kind), _midW.Peek())), tracksPane],
        };
    }

    static Element Placeholder(string key) => Pane with
    {
        Key = "lib:empty", Grow = 1f,
        Children = [EmptyState.Compact(Loc.Get(key))],
    };

    // The grip carries Grow=1 to fill the column HEIGHT — so it must be boxed in a fixed-width / Shrink=0
    // wrapper, else that Grow leaks into the horizontal row and the grip eats half the leftover width (the empty-gap bug).
    // The width is Splitter.StripW (16), not a local number: the strip IS the hit target, and all three library
    // splitters plus the detail rail's must be the same target.
    //
    // THE SEAM. Every column boundary on this page is a Grip, so one 1-DIP StrokeCardDefault line centred in the strip
    // separates nav|pane and pane|pane everywhere at once. It sits BEHIND the grip's reveal indicator and is
    // HitTestVisible = false, so the 16-DIP drag target is unchanged.
    static Element Grip(Signal<float> w, float min, float max, Action onCommit) => new BoxEl
    {
        Width = Splitter.StripW, Shrink = 0f, ZStack = true,
        Children =
        [
            new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Center,
                        HitTestVisible = false, Fill = Prop.Of(() => Tok.StrokeCardDefault) },
            new BoxEl { Direction = 1, AlignItems = FlexAlign.Stretch,
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Children = [Splitter.Create(w, onCommit, new() { Min = min, Max = max })] },
        ],
    };
}

// The compact detail pane (WaveeMusic LibraryDetailPanel) for an album/show: a 104px hero + an action row + the content
// list (tracks or episodes). Reads a STABLE Loadable<DetailModel> (re-driven by the host's selection key) so a new
// selection re-skins it in place. NOT the full DetailPage — this is the right pane of the master-detail split.
sealed class LibraryDetailPane : Component
{
    readonly Loadable<DetailModel> _model;
    readonly bool _show;
    readonly Services _svc;
    readonly PlaybackBridge? _bridge;
    // Track-list view state, so the embedded TrackList (the SAME virtualized list + cell as the full detail page) has its
    // own per-pane sort/filter/density. The route is fixed to kind=Album (the pane only ever shows album/release tracks);
    // the actual cfg — Album vs Compilation (various-artists → per-track artist subline) — is re-derived from the loaded
    // model's ReleaseKind inside TrackList, so a compilation in the library shows artists just like its detail page would.
    readonly Signal<DetailTrackSort> _sort = new(DetailTrackSort.Default);
    readonly Signal<string> _query = new("");
    readonly Signal<TrackFilterState> _filters = new(TrackFilterState.Default);
    readonly Signal<bool> _tempoColumn = new(false);
    readonly Signal<int> _density = new(1);
    readonly Signal<Route> _trackRoute = new(new Route("album:lib"));
    public LibraryDetailPane(Loadable<DetailModel> model, bool show, Services svc, PlaybackBridge? bridge)
    { _model = model; _show = show; _svc = svc; _bridge = bridge; }

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var navPreview = UseContext(NavPreviewStore.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var st = (LoadState)_model.State.Value;   // subscribe
        var m = _model.Value.Value;               // subscribe
        if (st != LoadState.Ready || m is null || m.Title.Length == 0) return Skeleton();

        string uri = m.ContextUri ?? "";
        void Play() { if (uri.Length > 0) _ = _svc.Player.PlayAsync(uri, 0); }
        void Shuffle() { if (uri.Length > 0) { _ = _svc.Player.SetShuffleAsync(true); _ = _svc.Player.PlayAsync(uri, 0); } }

        // Podcast show → the compact episode list (episodes aren't tracks). Album / release → the SAME virtualized
        // TrackList the detail page uses, embedded (no album trailing, no toolbar — the pane owns the hero + actions
        // above). So the rows get the identical cell: number↔play/pause on hover, the now-playing equalizer, the per-row
        // heart, art/columns the tier system fits to the pane width, and multi-select — image #2 now matches image #3.
        Element body = _show
            ? ScrollView(CompactEpisodes(m.Episodes ?? Array.Empty<Episode>(), i => { if (uri.Length > 0) _ = _svc.Player.PlayAsync(uri, i); })) with { Grow = 1f }
            : Embed.Comp(() => new TrackList(_trackRoute, _model, _bridge, TrackHandlers(go, lib), showToolbar: false, embedded: true));

        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            // DetailNoticeBar between the actions and the list: the SAME strip the full detail page mounts, reading the
            // SAME model fact (DetailModel.Notice — for an album, "the rows below are still the minified gid-only
            // view"). Zero-height when there is nothing to say; no thin-checking here — the model already knows.
            Children = [Hero(m, go, navPreview), Actions(uri, m.Title, Play, Shuffle, _show ? null : ViewFullAlbum(m, uri, go, navPreview)), DetailNoticeBar.For(_model), body],
        };
    }

    // The pane's SECOND way out (the hero title is the first — see Hero below): a plain navigation to the full album
    // page, where the trailing band's Full ask heals whatever the embedded list cannot (it never mounts trailing, so it
    // never self-heals a minified album). PURE NAVIGATION — no data semantics, always offered for an album regardless
    // of the model's state. Same click body as TitleLink: stash the resolved model as the nav preview so the
    // destination paints its header from real data on frame one. Albums only (a show's full page adds nothing the pane
    // lacks), and never a dead target: no ContextUri ⇒ no button (the file's rule — see TitleLink).
    static Element? ViewFullAlbum(DetailModel m, string uri, Action<string, string?> go, NavPreviewStore? navPreview)
    {
        if (uri.Length == 0) return null;
        string routeKey = "album:" + uri;
        return Button.Create(Loc.Get(Strings.Library.ViewFullAlbum),
            () => { navPreview?.Set(routeKey, m); go(routeKey, m.Title); },
            ButtonAppearance.Subtle, ControlSize.Small);
    }

    // Minimal handlers for the embedded TrackList. They act on the pane's CURRENT model (read at call-time via _model, so
    // a later selection is honoured even though the component froze these at mount) + the host navigation. Sort/filter/
    // density are per-pane signals; the list's own toolbar is hidden, so they just carry sensible defaults.
    DetailHandlers TrackHandlers(Action<string, string?> go, LibraryBridge? lib)
    {
        DetailModel? Cur() => _model.Value.Peek();
        string Ctx() => Cur()?.ContextUri ?? "";
        void Play(int i) { var u = Ctx(); if (u.Length > 0) _ = _svc.Player.PlayAsync(u, Math.Max(0, i)); }
        void Shuffle() { var u = Ctx(); if (u.Length > 0) { _ = _svc.Player.SetShuffleAsync(true); _ = _svc.Player.PlayAsync(u, 0); } }
        void PlayContext(string u) { if (u.Length > 0) _ = _svc.Player.PlayAsync(u, 0); }
        void AddToQueue()
        {
            var m = Cur(); if (m is null) return;
            int n = DetailQueueActions.AddToEnd(_svc.Player, m.Tracks);
            if (n > 0) Toast.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), new ToastOptions { Severity = InfoBarSeverity.Success });
        }
        void PlayNext()
        {
            var m = Cur(); if (m is null) return;
            int n = DetailQueueActions.PlayNext(_svc.Player, m.Tracks);
            if (n > 0) Toast.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), new ToastOptions { Severity = InfoBarSeverity.Success });
        }
        void AddToPlaylist()
        {
            var m = Cur(); if (lib is null || m is null || m.Tracks.Count == 0) return;
            var (plUri, plName) = lib.AddToDefaultPlaylist(m.Tracks);
            Toast.Show(Strings.Detail.AddedToPlaylist(plName), new ToastOptions
            {
                Severity = InfoBarSeverity.Success,
                ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => go("pl:" + plUri, plName),
            });
        }
        return new DetailHandlers(Play, () => Play(0), Shuffle, PlayContext, go, Tok.AccentDefault,
            _sort, s => _sort.Value = s, _query, _filters, f => _filters.Value = f, _density, d => _density.Value = d,
            // The embedded library list never offers the BPM·Key column (no overflow menu to toggle it), so this is a
            // constant-off pair rather than another persisted setting.
            _tempoColumn, on => _tempoColumn.Value = on,
            PlayNext, AddToQueue, AddToPlaylist,
            // The embedded TrackList has no trailing shelves, so these are never invoked here; route through DetailNav
            // (no preview/morph store) so behaviour stays a plain nav if that ever changes.
            a => DetailNav.OpenAlbum(null, go, a), p => DetailNav.OpenPlaylist(null, go, p));
    }

    // The pane is a COMPACT panel; its header carries the first of its two ways out (the action row's "View full album"
    // button is the other): the title opens the album/show's real page and the artist line opens each billed artist.
    // Both were dead text before — and the library search hits that used to navigate now select in place, so this
    // header is where "take me to the actual page" lives.
    Element Hero(DetailModel m, Action<string, string?> go, NavPreviewStore? navPreview)
    {
        string uri = m.ContextUri ?? "";
        // Route key by kind — the pane renders shows and albums from the same model.
        string routeKey = uri.Length > 0 ? (_show ? "show:" : "album:") + uri : "";
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
            Children =
            [
                // Artwork is CONTENT, not a floating card: no static elevation under it (the app's stroke-only content rule).
                new BoxEl { Width = 104f, Height = 104f, Shrink = 0f, Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                    Children = [Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, 104f, 104f, Radii.Card, decodePx: 256)] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f,
                    Children =
                    [
                        WaveeType.Eyebrow(m.BadgeType ?? (_show ? Loc.Get(Strings.Podcast.Show) : "")) with { Color = Tok.TextTertiary },
                        TitleLink(m, routeKey, go, navPreview),
                        // A podcast's publisher is a plain name with no uri to open — it stays text. Album artists are
                        // real entities, so each billed name is its own link (the same span row the track rows use).
                        m.Publisher is { } pub
                            ? new TextEl(pub) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                            : TrackRow.ArtistLinks(m.Artists, go, size: 14f, weight: 600),
                        new TextEl(m.MetaLine) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ] },
            ],
        };
    }

    // The clickable title, on the "Go to artist" pill idiom: the text itself carries the hover ink change, the plate
    // around it carries the hit target, the cursor, focus and the Button role. Negative margin cancels the plate's
    // padding so the title stays optically flush with the badge and artist lines above/below it.
    // The FULL DetailModel is stashed as the nav preview (richer than DetailPreview.FromAlbum — this pane already
    // resolved tracks, meta and release info), so the destination page paints its header from real data on frame one.
    // No ContextUri ⇒ nothing to open ⇒ plain text, never a dead click target.
    static Element TitleLink(DetailModel m, string routeKey, Action<string, string?> go, NavPreviewStore? navPreview)
    {
        var text = new TextEl(m.Title)
        {
            // Title (28/36/600) - the pane header IS a page hero. Was 23/800: neither a ramp size nor a ramp weight.
            Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = WaveeMotion.Faster,
            MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
        };
        if (routeKey.Length == 0) return text;
        return new BoxEl
        {
            // Deliberately NOT AlignSelf.Start: the title must keep the full column width it wraps/ellipsizes against.
            Corners = Radii.ControlAll,
            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Margin = new Edges4(-Spacing.S, -Spacing.XXS, -Spacing.S, -Spacing.XXS),
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
            OnClick = () => { navPreview?.Set(routeKey, m); go(routeKey, m.Title); },
            Children = [text],
        }.Interactive(Interaction.Subtle);
    }

    // `viewFullAlbum` is built by Render (null for a show / an empty uri) so this row stays a dumb layout: the
    // navigation wiring (routeKey, nav preview, go) lives beside TitleLink's, where the pane's other exit is defined.
    Element Actions(string uri, string? name, Action play, Action shuffle, Element? viewFullAlbum) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.XL, 0f, Spacing.XL, Spacing.M),
        Children =
        [
            // The shared media pill on the SYSTEM accent (no artwork extraction on this surface). WaveeCta picks the
            // on-fill ink from the fill's WCAG luminance, so for Tok.AccentDefault it resolves what Tok.OnAccent bakes —
            // the accent-keyed ink, not the theme-keyed TextOnAccentPrimary the stock ramp uses.
            WaveeCta.Play(Tok.AccentDefault, play),
            Fab(Icons.Shuffle, shuffle),
            _show ? Embed.Comp(() => new FollowButton(uri, name)) : Embed.Comp(() => new SaveButton(uri, name: name)),
            // Small/subtle on purpose — a second accent CTA would compete with Play; this is a doorway, not an action.
            .. (viewFullAlbum is null ? Array.Empty<Element>() : new Element[] { viewFullAlbum }),
        ],
    };

    static Element Fab(string glyph, Action onClick) => new BoxEl
    {
        Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(40f),
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press, OnClick = onClick,
        Children = [Icon(glyph, 16f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    static Element CompactEpisodes(IReadOnlyList<Episode> eps, Action<int> onPlay)
    {
        var rows = new Element[eps.Count];
        for (int i = 0; i < eps.Count; i++)
        {
            int idx = i; var e = eps[i];
            rows[i] = new BoxEl
            {
                Direction = 0, MinHeight = 56f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = Edges4.All(Spacing.S), Corners = Radii.ControlAll,
                OnClick = () => onPlay(idx),
                Children =
                [
                    new BoxEl { Width = 32f, Height = 32f, Shrink = 0f, Corners = Radii.Circle(32f), Fill = Tok.FillSubtleSecondary, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.Play, 12f, Tok.TextSecondary)] },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                        Children =
                        [
                            new TextEl(e.Title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(DetailFormat.DurationCell(e.DurationMs)) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
                        ] },
                ],
            }.Interactive(Interaction.Subtle);
        }
        return new BoxEl { Direction = 1, Gap = 2f, Padding = new Edges4(Spacing.M, 0f, Spacing.M, PlayerDock.Reserve + Spacing.XL), Children = rows };
    }

    static Element Skeleton() => new BoxEl
    {
        Direction = 1, Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.XL), Gap = Spacing.L,
        Children =
        [
            new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                Children = [new BoxEl { Width = 104f, Height = 104f, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault },
                    new BoxEl { Direction = 1, Grow = 1f, Gap = Spacing.S, Children = [new BoxEl { Width = 80f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, new BoxEl { Width = 200f, Height = 22f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, new BoxEl { Width = 140f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }] }] },
            new BoxEl { Direction = 1, Gap = Spacing.S, Children = Enumerable.Range(0, 6).Select(_ => (Element)new BoxEl { Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault, Margin = new Edges4(0f, Spacing.S, 0f, 0f) }).ToArray() },
        ],
    };
}

// The compact artist pane (WaveeMusic ArtistsLibraryView column 2): the artist's releases grid only. Picking a release
// sets the host's _albumKey → the 3rd column (tracks).
sealed class LibraryArtistPane : Component
{
    readonly Loadable<Artist> _artist;
    readonly Signal<string> _albumKey;
    readonly Signal<int> _aSort, _aView, _aSize;
    readonly Signal<bool> _aDesc;
    readonly Signal<string> _aFilter;
    readonly Action? _onDrill;                   // collapsed drill-in: notify the host when a release is picked (→ tracks level)
    readonly SelectionModel _discoSel = new();   // discography grid single-selection (drives the 3rd column)
    readonly ItemsViewController _discoCtl = new();   // see SyncSelect: scroll a programmatically-moved pick into view
    bool _syncingSel;                            // see SyncSelect: a programmatic re-sync must not re-enter Pick
    ActionServices? _actsRef;                    // cached in Render → the discography items' drag payloads
    static readonly string[] NoSuggest = Array.Empty<string>();

    public LibraryArtistPane(Loadable<Artist> artist, Signal<string> albumKey,
        Signal<int> aSort, Signal<bool> aDesc, Signal<int> aView, Signal<int> aSize, Signal<string> aFilter, Action? onDrill = null)
    { _artist = artist; _albumKey = albumKey; _aSort = aSort; _aDesc = aDesc; _aView = aView; _aSize = aSize; _aFilter = aFilter; _onDrill = onDrill; }

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        _actsRef = UseContext(ActionServices.Slot);
        var st = (LoadState)_artist.State.Value;   // subscribe
        var a = _artist.Value.Value;               // subscribe
        var albums = a?.TopAlbums ?? Array.Empty<Album>();
        var shown = FilterSortAlbums(albums, _aFilter.Value, _aSort.Value, _aDesc.Value);   // subscribe (filter/sort/direction)
        // Keep the discography selection synced to the chosen release — UNCONDITIONAL hook, BEFORE any early return (else
        // the effect-slot count changes when the artist flips Pending→Ready → an out-of-range hook crash). Driven off the
        // SHOWN (filtered/sorted) list so the selection index matches the rendered ItemsView.
        UseEffect(() => SyncDisco(shown), _albumKey.Value + "|" + shown.Length + "|" + (shown.Length > 0 ? shown[0].Uri : ""));

        // The toolbar (album sort/view controls + Filter + "Go to artist") renders even while the artist loads; only the
        // body swaps skeleton→grid/list. So the controls never flash in/out and stay put across a selection change.
        Element body = (st != LoadState.Ready || a is null || a.Name.Length == 0) ? Skeleton() : Body(shown);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [Toolbar(a, go), body],
        };
    }

    // The discography toolbar — the SAME controls as the left picker (LibrarySortView pill + Filter box), but over the
    // artist's releases: hasCreator:false (a single artist), hasRelease:true (Release-date sort applies). "Go to artist"
    // stays top-right (shown once the artist is loaded).
    Element Toolbar(Artist? a, Action<string, string?>? go) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
        Children =
        [
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children =
            [
                Embed.Comp(() => new LibrarySortView(_aSort, _aDesc, _aView, _aSize, hasCreator: false, hasRelease: true)),
                new BoxEl { Grow = 1f },
                a is not null && go is not null ? GoToArtist(a, go) : new BoxEl(),
            ] },
            AutoSuggestBox.Create(NoSuggest, Loc.Get(Strings.Library.Filter), text: _aFilter, queryIcon: Icons.Search, grow: 1f, maxFillWidth: 9999f, minHeight: 32f, cornerRadius: Radii.Control),
        ],
    };

    // "Go to artist" — the discography pane's ONLY route to the artist page, so it has to read as a live link.
    //
    // It used to be a grey 16-radius PILL: capsule geometry (the app's CTA grammar) filled with nothing, labelled in
    // TextSecondary, sitting in a toolbar next to real controls. Capsule + grey + secondary ink is, everywhere else in
    // this app, what a DISABLED pill looks like — so the one navigation affordance in the pane advertised itself as
    // unavailable. It is now the stock HyperlinkButton treatment (HyperlinkButton_themeresources.xaml): AccentTextFill
    // ink on the rest/hover/pressed ramp, ControlCornerRadius (4) — NOT a capsule, so it cannot be mistaken for a CTA —
    // and the SubtleFill hover/pressed plate. The ↗ glyph stays: this navigates AWAY from the master-detail pane, which
    // is exactly what that glyph means, and it now takes the link's ink like the label. Hand-rolled rather than
    // HyperlinkButton.Create only because the control owns its Children slot and this link has a trailing glyph.
    static Element GoToArtist(Artist a, Action<string, string?> go) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        BrushTransitionMs = WaveeMotion.Faster,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
        OnClick = () => go("artist:" + a.Uri, a.Name),
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.GoToArtist))
            {
                Size = 14f, LineHeight = 20f, Weight = 600,
                Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary, PressedColor = Tok.AccentTextTertiary,
            },
            Icon(Icons.OpenInNewWindow, 14f, Tok.AccentTextPrimary),
        ],
    };

    // Filter (title contains) + sort over the artist's releases. Sort codes mirror the picker: 0 = as returned by the API
    // (≈ release-date desc), 1 = reversed, 2 = Alphabetical, 4 = Release date (by Year). Direction flips the sorted forms.
    static Album[] FilterSortAlbums(IReadOnlyList<Album> albums, string filter, int sort, bool desc)
    {
        string q = filter.Trim();
        var arr = (q.Length == 0 ? albums : albums.Where(al => al.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();
        Comparison<Album>? cmp = sort switch
        {
            2 => (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase),
            4 => (x, y) => x.Year.CompareTo(y.Year),
            _ => null,
        };
        if (cmp is not null) Array.Sort(arr, cmp);
        else if (sort == 1) Array.Reverse(arr);
        if (desc && cmp is not null) Array.Reverse(arr);
        return arr;
    }

    // Grid (view>=2) vs list, grid-size S/M/L — the SAME view-type semantics as the left picker's ListBody. Keyed by
    // view/size/set so a view change remounts with the right slots; the external _discoSel keeps the selection. Picking a
    // release sets _albumKey → 3rd column. size=1 non-compact grid → 124px min width (matches the previous fixed grid).
    Element Body(IReadOnlyList<Album> albums)
    {
        if (albums.Count == 0)
            return _aFilter.Peek().Length > 0
                ? EmptyState.Compact(Loc.Get(Strings.Library.NoMatch))
                : new BoxEl { Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL), Children = [Caption("…").Secondary()] };

        int view = _aView.Value, size = _aSize.Value;   // subscribe
        bool grid = view >= 2, compact = view == 0 || view == 2;
        string key = "disco:" + view + ":" + size + ":" + albums.Count + ":" + albums[0].Uri;
        void Pick(int i) { if (_syncingSel || i < 0 || i >= albums.Count) return; _albumKey.Value = "album:" + albums[i].Uri; _onDrill?.Invoke(); }

        if (grid)
            return new BoxEl
            {
                Key = key, Grow = 1f, Basis = 0f, MinHeight = 0f, Direction = 1, ClipToBounds = true,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Children = [ItemsView.Create(albums.Count, i => DiscoCardContent(albums[i], compact),
                    RepeatLayout.GridFit((compact ? 84f : 100f) + size * (compact ? 16f : 24f), 8f),
                    new ListOptions { SelectionMode = ItemsSelectionMode.Single, Selection = _discoSel, Selector = SelectorVisual.Border, OnChange = () => Pick(_discoSel.FirstSelectedIndex), Controller = _discoCtl, Grow = 1f })],
            };

        return new BoxEl
        {
            Key = key, Grow = 1f, Basis = 0f, MinHeight = 0f, Direction = 1,
            Children = [ItemsView.List(albums.Count, i => DiscoRowContent(albums[i], compact),
                selectionMode: ItemsSelectionMode.Single, selection: _discoSel,
                onSelectionIndexChanged: Pick, controller: _discoCtl,
                itemExtent: compact ? 44f : 60f, grow: 1f)],
        };
    }

    /// <summary>
    /// Point the discography grid at <c>_albumKey</c>.
    /// <para>SNAP-TO-FIRST IS AN AUTO-SELECT, NOT A CORRECTION. It fires only when <c>_albumKey</c> is EMPTY — which is
    /// exactly the state <c>LibraryPage.Select</c> puts it in on every artist change, so "pick an artist, get their
    /// first release" is unchanged. It used to fire whenever the key was missing from this list, and that clobbered any
    /// release chosen deliberately: a library-search hit committed in place, or a key restored from settings, was
    /// overwritten by whatever happened to sort first the moment the discography landed.</para>
    /// <para>A key that is set but absent from the SHOWN list simply leaves the grid with no highlighted row. That list
    /// is filtered and sorted, and an artist's cached TopAlbums need not carry every release — while <c>_albumKey</c>
    /// drives its own loadable, so the tracks column still resolves the release regardless of what this grid shows.
    /// Nothing here is authoritative over the key; it only renders it.</para>
    /// </summary>
    void SyncDisco(IReadOnlyList<Album> albums)
    {
        string ak = _albumKey.Peek();
        if (ak.Length == 0)
        {
            if (albums.Count > 0) _albumKey.Value = "album:" + albums[0].Uri;
            else if (_discoSel.SelectedCount > 0) SyncSelect(-1);
            return;
        }
        int idx = -1;
        for (int i = 0; i < albums.Count; i++) if ("album:" + albums[i].Uri == ak) { idx = i; break; }
        if (idx < 0) { if (_discoSel.SelectedCount > 0) SyncSelect(-1); }
        else if (_discoSel.FirstSelectedIndex != idx) SyncSelect(idx);
    }

    // ItemsView forwards EVERY SelectionModel mutation — including this re-sync — to Pick, whose side effect is
    // onDrill. Collapsed, that meant the discography level was skipped entirely: tapping an artist landed on depth 1,
    // SyncDisco immediately selected the auto-picked release, and Pick drilled straight through to the tracks.
    // …and, like the navigator's, scroll the moved pick back into view (minimal + unanimated: a visible tile never moves).
    void SyncSelect(int idx)
    {
        _syncingSel = true;
        try { if (idx < 0) _discoSel.DeselectAll(); else _discoSel.Select(idx); }
        finally { _syncingSel = false; }
        if (idx >= 0) _discoCtl.StartBringItemIntoView(idx);
    }

    // Grid card: cover + title, plus a "year · KIND" subtitle in non-compact grids (compact drops it, like the picker).
    Element DiscoCardContent(Album al, bool compact)
    {
        var children = new List<Element>(3)
        {
            Surfaces.ArtworkFill(al.Cover, 6f),
            new TextEl(al.Name) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (!compact)
            children.Add(new TextEl((al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        return new BoxEl { Direction = 1, Gap = Spacing.XS, ClipToBounds = true, Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS), Draggable = AlbumDrag(al), Children = children.ToArray() };
    }

    // List row: 40px cover (dropped when compact) + title + "year · KIND" subtitle — mirrors the left picker's NavRowContent.
    Element DiscoRowContent(Album al, bool compact)
    {
        var children = new List<Element>(2);
        if (!compact)
            children.Add(new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = Radii.ControlAll, ClipToBounds = true,
                Children = [Surfaces.Artwork(al.Cover, al.Uri.GetHashCode() & 0x7fffffff, 40f, 40f, 5f)] });
        children.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
            Children =
            [
                new TextEl(al.Name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                compact ? new BoxEl() : new TextEl((al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ] });
        return new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Draggable = AlbumDrag(al), Children = children.ToArray() };
    }

    // Click-primary, same as the left navigator's rows: picking the release (which drives the 3rd column) is the constant
    // intent, dragging it out the exception — so the mouse drag box gets WinUI's ×2 list-item multiplier.
    DragSource AlbumDrag(Album al) => Drag.Source(WaveeDragKinds.Resource,
        () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Album, al.Uri, al.Name, al.Cover, _actsRef),
        thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier);

    static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };

    static Element Skeleton() => new BoxEl
    {
        Direction = 1, Grow = 1f, Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M), Gap = Spacing.S,
        Children = Enumerable.Range(0, 8).Select(_ => (Element)new BoxEl { Height = 148f, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault }).ToArray(),
    };
}
