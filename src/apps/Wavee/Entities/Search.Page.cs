// ── Entities/Search.Page.cs ────────────────────────────────────────────────────────────────────────────────────────
// the search results page's CORE HOST: the route, the page component (the query, the facet strip, the slide, the readiness
// of each facet), the ONE demand, and the facet dispatch into the body MODEL the theme draws (T10a2: `Pages.Search`)
//
// Role: UI (core host)
// Owner: P (stream P3)
// Wave: 5
// Budget: 550 lines
// Spec: ch 13 §0, §1.1, §1.5, §5 (the facet slide), §6.1, §7 (data & readiness), §9 (traps, gaps 1-3, 9)
//
// ── ONE PAGE PER QUERY ───────────────────────────────────────────────────────────────────────────────────────────────
//
// The query is `route.Arg`, frozen per mount (a new query is a new keep-alive slot, keyed "search-page:<q>"); a blank
// query never reaches here (`Shell.Parse` rewrites it to Browse, §0.1). The page binds the query's All row
// (`Entities.Search(q)`) and, for a dedicated facet, that facet's own row; nothing below the page takes either as a
// constructor argument (§1.5).
//
// ── THE DEMAND (§7) ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// One effect per (row, scope): `Entities.Ensure(all, SearchFields.All)` — chips, the ranked hits, the genre tiles and the
// related queries in one planner call, where 0.2.9 issued three requests from three components — plus
// `Entities.Ensure(row, Results)` for a dedicated facet. A tracked effect then asks every hit's entity row
// (<Kind>Fields.Row / Identity, one batch per kind) and pages a dedicated grid facet's edge while it is Partial, up to
// `Search.FacetHitCeiling` — the page never fetches a visible window.
//
// ── READINESS ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Known → content. Not known and the group UN-ASKED after this page asked (a terminal transport failure,
// `Fetch.Failed`) → the error vacancy WITH Retry (§9 gap 9). Asked, nothing in flight and still not known → the answer
// concluded without it → the facet's empty. Otherwise pending. A row failure publishes no signal, so a one-shot
// deadline (re-armed only while a request is still out) re-reads the marks; nothing ticks once the page has settled.
//
// ── THE SLIDE (§0.5) ─────────────────────────────────────────────────────────────────────────────────────────────────
//
// The host decides WHETHER the body slides and which way (`Search.Slides`, `SearchSlide`): only on a REAL chip change,
// mounting never slides. The picture (the facet body keyed "facet-body:<facet>" carrying `MotionRecipes.PageSlideForward` /
// `PageSlideBack`, the region's `smoothResize: false` — a shimmer and a complete result set differ by thousands of DIP,
// §9 must-not-simplify 2) is Classic's (`Views\Pages\ClassicSearchPage.cs`).
//
// ── WHAT THE VIEW GETS ───────────────────────────────────────────────────────────────────────────────────────────────
//
// The page's own state as `SearchPageProps`, and the BODY as a `SearchBodyModel` the host builds each time the skeleton
// region asks for its content (`Body`): the hits as `HitItem`s over the live handles, the fallback interleave, the rail,
// the genre tiles and the related queries. The view draws them; nothing here builds an element.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Search
{
    /// <summary><see cref="Shell.RouteKind.Search"/>: the results for <c>route.Arg</c>.</summary>
    // MOUNT POINT (contract §4)
    public static Element PageFor(in Shell.Route route)
    {
        string query = (Shell.ArgOf(route) ?? "").Trim();
        return Embed.Comp(new PageProps(query), static () => new PageHost()) with { Key = "search-page:" + query };
    }

    sealed record PageProps(string Query);

    /// <summary>Hit entity groups a page asks for, one batch per kind (contract §8: &lt;Kind&gt;Fields.Row where the kind has
    /// one, Identity otherwise).</summary>
    static readonly (EntityKind Kind, uint Wanted)[] s_hitGroups =
    [
        (EntityKind.Track, (uint)TrackFields.Row), (EntityKind.Album, (uint)AlbumFields.Identity),
        (EntityKind.Artist, (uint)ArtistFields.Identity), (EntityKind.Playlist, (uint)PlaylistFields.Row),
        (EntityKind.Show, (uint)ShowFields.Identity), (EntityKind.Episode, (uint)EpisodeFields.Row),
        (EntityKind.User, (uint)UserFields.Identity),
    ];

    /// <summary>The deadline after which an unanswered facet re-reads its marks.</summary>
    const float RecheckMs = 10_000f;

    enum Readiness : byte { Pending, Ready, Concluded, Failed }

    [CoreHost]
    sealed class PageHost : Component
    {
        readonly Signal<int> _chip = new(0);
        readonly Signal<int> _recheck = new(0);
        readonly SearchFacet[] _facets = new SearchFacet[SearchTable.FacetCount];
        readonly Action[] _selectors = new Action[SearchTable.FacetCount];
        int _facetCount = 1;

        Scope? _scope;
        string _query = "", _echo = "";
        Search _all, _row;
        SearchFacet _facet;
        Shell.NavOrigin? _origin;
        HitContext _ctx;
        int _prevChip, _cols = 2;
        bool _slideArmed, _hadChips, _bodyAnswered, _colsInit, _askedAll;
        uint _askedFacets;
        int[] _hitScratch = new int[64];
        TimerHandle _deadline;

        readonly Func<float, int> _colsFor;
        readonly Func<bool> _pending, _failed;
        readonly Func<SearchBodyModel> _body;
        readonly Action _demand, _demandTracked, _retry, _retryAll, _recheckNow, _clampChip, _selectPlaylists;

        public PageHost()
        {
            for (int i = 0; i < _selectors.Length; i++)
            {
                int index = i;
                _selectors[i] = () => _chip.Value = index;
            }
            _facets[0] = SearchFacet.All;
            _colsFor = ColsNow;
            _pending = () => BodyReadiness() == Readiness.Pending;
            _failed = () => BodyReadiness() == Readiness.Failed;
            _body = Body;
            _demand = Demand;
            _demandTracked = DemandTracked;
            _retry = Retry;
            _retryAll = RetryAll;
            _recheckNow = Recheck;
            _clampChip = () => { if (_chip.Peek() >= _facetCount) _chip.Value = 0; };
            _selectPlaylists = () => SelectFacet(SearchFacet.Playlists);
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint scopeEpoch = Entities.ScopeEpoch.Value;          // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            Subscribe(scope);
            _ = _recheck.Value;
            var overlay = UseContext(Overlay.Service);
            string scrollScope = UseContext(Shell.PageScrollScope);

            if (!ReferenceEquals(_scope, scope) || !string.Equals(_query, p.Query, StringComparison.Ordinal))
            {
                _scope = scope;
                _query = p.Query;
                _echo = p.Query.ToLowerInvariant();
                _all = p.Query.Length == 0 ? default : Entities.Search(p.Query.AsSpan());
                _origin = p.Query.Length == 0 ? null : new Shell.NavOrigin(p.Query, Shell.Parse("search".AsSpan(), p.Query.AsSpan()));
                _askedAll = false;
                _askedFacets = 0;
                _hadChips = false;
                _bodyAnswered = false;
            }
            _ctx = new HitContext(_query, _origin, overlay);

            // The facet list: the All row's chip strip once it has ever landed; before that, the lone All tab (behind the
            // eleven-pill skeleton while the first answer is pending).
            bool chips = _all.IsValid && _all.Knows(SearchFields.Chips);
            _hadChips |= chips;
            if (chips) _facetCount = Math.Max(1, _all.FacetsFrom(_facets));
            else if (!_hadChips) { _facets[0] = SearchFacet.All; _facetCount = 1; }

            int chip = _chip.Value;
            UseEffect(_clampChip, _facetCount);
            _facet = (uint)chip < (uint)_facetCount ? _facets[chip] : SearchFacet.All;
            _row = _facet is SearchFacet.All or SearchFacet.Genres || _query.Length == 0 ? _all : Entities.Search(_query.AsSpan(), _facet);

            UseEffect(_demand, DepKey.From(_row.Slot, (int)scopeEpoch));
            UseEffect(_demandTracked);
            _deadline = UseTimeout(_recheckNow, RecheckMs, DepKey.From(_row.Slot, (int)_facet));

            bool slide = Slides(_slideArmed, _prevChip, chip);
            bool forward = chip > _prevChip;
            _prevChip = chip;
            _slideArmed = true;

            string scrollKey = scrollScope + "search:" + ((int)_facet).ToString(CultureInfo.InvariantCulture);
            if (!_all.IsValid)
                return Slots.View(ViewKeys.SearchPage, PropsOf(vacant: true, echo: "", chipsSkeletal: true, tabs: Array.Empty<SearchTab>(),
                                                              selected: 0, SearchSlide.None, scrollKey));

            var allReady = ReadinessOf(_all, SearchFields.Chips, _askedAll);
            // The tabs arrive WITH the first result, not a beat before it: the chip row stays skeletal until the body has
            // answered once for this query (Search.ChipRowSkeletal). The view keys both rows so the swap is a mount, not an
            // in-place patch.
            _bodyAnswered |= BodyReadiness() != Readiness.Pending;
            bool skeletal = ChipRowSkeletal(_hadChips, allReady == Readiness.Pending, _bodyAnswered);
            SearchTab[] tabs = skeletal ? Array.Empty<SearchTab>() : TabsOf(_all, _facets.AsSpan(0, _facetCount));

            return Slots.View(ViewKeys.SearchPage, PropsOf(vacant: false, _echo, skeletal, tabs, Math.Min(chip, _facetCount - 1),
                                                          slide ? (forward ? SearchSlide.Forward : SearchSlide.Back) : SearchSlide.None, scrollKey));
        }

        SearchPageProps PropsOf(bool vacant, string echo, bool chipsSkeletal, IReadOnlyList<SearchTab> tabs, int selected, SearchSlide slide, string scrollKey)
            => new()
            {
                Vacant = vacant, Echo = echo, ChipsSkeletal = chipsSkeletal, Tabs = tabs, Selected = selected, Selectors = _selectors,
                Facet = _facet, Slide = slide, ScrollKey = scrollKey,
                Pending = _pending, Failed = _failed, Body = _body,
                Retry = _retry, RetryAll = _retryAll, SelectPlaylists = _selectPlaylists, ColsFor = _colsFor,
            };

        /// <summary>The facet strip over the All row's chip strip: server rank first, All always first, counts omitted for
        /// All and for 0 (W3).</summary>
        static SearchTab[] TabsOf(Search all, ReadOnlySpan<SearchFacet> facets)
        {
            var tabs = new SearchTab[facets.Length];
            for (int i = 0; i < facets.Length; i++)
            {
                var f = facets[i];
                int count = f == SearchFacet.All ? 0 : FacetCount(all.RawTotalOf(f), LocalCountOf(all, f));
                tabs[i] = new SearchTab(f, count);
            }
            return tabs;
        }

        /// <summary>Every table the page paints from: a hydrating hit row, a landed page, a genre tile.</summary>
        static void Subscribe(Scope scope)
        {
            _ = scope.Searches.Changed.Value;
            var e = scope.Edges;
            _ = e.SearchResult.Changed.Value;
            _ = e.SearchGenres.Changed.Value;
            _ = e.SearchRelated.Changed.Value;
            _ = e.SearchHits.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Shows.Changed.Value;
            _ = scope.Episodes.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = scope.Browses.Changed.Value;
        }

        // ── readiness ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The four answers a group of a search row can give (file header).</summary>
        static Readiness ReadinessOf(Search s, SearchFields group, bool askedByPage)
        {
            if (!s.IsValid) return Readiness.Pending;
            if (s.Knows(group)) return Readiness.Ready;
            var t = Entities.Current.Searches;
            bool asked = (t.Asked[s.Slot] & (uint)group) != 0;
            if (!asked) return askedByPage ? Readiness.Failed : Readiness.Pending;
            return t.Inflight[s.Slot] == 0 ? Readiness.Concluded : Readiness.Pending;
        }

        Readiness BodyReadiness()
        {
            _ = Entities.ScopeEpoch.Value;
            Subscribe(Entities.Current);
            _ = _recheck.Value;
            return _facet == SearchFacet.Genres
                ? ReadinessOf(_all, SearchFields.Genres, _askedAll)
                : ReadinessOf(_row, SearchFields.Results, _facet == SearchFacet.All ? _askedAll : (_askedFacets & (1u << (int)_facet)) != 0);
        }

        // ── the facet dispatch (§1.1 ResultsFor) ────────────────────────────────────────────────────────────────────

        SearchBodyModel Body()
        {
            var r = BodyReadiness();
            var ctx = _ctx;
            var row = _row;
            SearchBodyModel Of(SearchBodyKind kind) => new() { Kind = kind, Facet = _facet, Hits = ctx };
            if (r == Readiness.Pending) return Of(SearchBodyKind.Pending);
            if (r == Readiness.Concluded && _facet != SearchFacet.All) return Of(SearchBodyKind.Empty);
            switch (_facet)
            {
                case SearchFacet.All:
                    return AllBody(in ctx);
                case SearchFacet.Genres:
                    return Of(SearchBodyKind.Genres) with { Genres = GenreTiles(_all, _origin) };
                case SearchFacet.Tracks:
                    return row.ResultCount == 0 ? Of(SearchBodyKind.Empty) : Of(SearchBodyKind.Songs) with { Rows = HitsOf(row) };
                case SearchFacet.Albums or SearchFacet.Playlists:
                    return row.ResultCount == 0 ? Of(SearchBodyKind.Empty) : Of(SearchBodyKind.FacetGrid) with { Row = row };
                case SearchFacet.Artists or SearchFacet.Podcasts or SearchFacet.Episodes or SearchFacet.Profiles:
                    return Of(SearchBodyKind.Rows) with { Rows = HitsOf(row) };
                // Audiobook and author hits have no table in 0.3 (reported): the facet is its own empty sentence.
                default:
                    return Of(SearchBodyKind.Empty);
            }
        }

        /// <summary>A row's hits in server order, holes dropped, each with its chrome flags.</summary>
        static List<HitItem> HitsOf(Search row)
        {
            int n = row.ResultCount;
            var items = new List<HitItem>(n);
            for (int i = 0; i < n; i++)
            {
                var hit = row.ResultRef(i);
                if (!hit.IsNone) items.Add(new HitItem(hit, VersionOf(hit), row.FlagsOf(hit)));
            }
            return items;
        }

        /// <summary>The All tab (W1): the Top Result and Best matches off the ranked list — or, for an answer that carried
        /// none, the fallback interleave — then the playlist rail, the genre links and the related queries.</summary>
        SearchBodyModel AllBody(in HitContext ctx)
        {
            var all = _all;
            int n = all.ResultCount;
            EntityRef hero = default;
            HitItem? top = null;
            List<HitItem>? best = null;
            IReadOnlyList<FallbackHit>? fallback = null;
            bool collected = (all.RowFlags & SearchRowFlags.Collected) != 0;
            if (n > 0 && !collected)
            {
                hero = all.ResultRef(0);
                top = new HitItem(hero, VersionOf(hero), all.FlagsOf(hero));
                if (n > 1)
                {
                    best = new List<HitItem>(n - 1);
                    for (int i = 1; i < n; i++)
                    {
                        var h = all.ResultRef(i);
                        if (!h.IsNone) best.Add(new HitItem(h, VersionOf(h), all.FlagsOf(h)));
                    }
                }
            }
            else if (n > 0) fallback = FallbackList(all);

            var genreState = SectionOf(ReadinessOf(_all, SearchFields.Genres, _askedAll));
            var relatedState = SectionOf(ReadinessOf(_all, SearchFields.Related, _askedAll));
            return new SearchBodyModel
            {
                Kind = SearchBodyKind.All, Facet = SearchFacet.All, Hits = ctx,
                Hero = top, Best = best, Fallback = fallback,
                Rail = RailItems(all, hero),
                GenreState = genreState,
                Genres = genreState == SearchSection.Ready ? GenreTiles(_all, _origin) : Array.Empty<BrowseTileModel>(),
                RelatedState = relatedState,
                Related = relatedState == SearchSection.Ready ? RelatedOf(_all, _query) : Array.Empty<string>(),
            };
        }

        /// <summary>A readiness as the All tab's section state: a section not asked yet, or still out, is Pending.</summary>
        static SearchSection SectionOf(Readiness r) => r switch
        {
            Readiness.Ready => SearchSection.Ready,
            Readiness.Failed => SearchSection.Failed,
            Readiness.Concluded => SearchSection.Concluded,
            _ => SearchSection.Pending,
        };

        /// <summary>0.2.9's no-top-hits list: the pure interleave (<see cref="FallbackRows"/>) over the collected hits.</summary>
        static List<FallbackHit> FallbackList(Search all)
        {
            int n = all.ResultCount;
            var tracks = new List<EntityRef>(n);
            var artists = new List<EntityRef>(n);
            var albums = new List<EntityRef>(n);
            var playlists = new List<EntityRef>(n);
            for (int i = 0; i < n; i++)
            {
                var h = all.ResultRef(i);
                switch (h.Kind)
                {
                    case EntityKind.Track: tracks.Add(h); break;
                    case EntityKind.Artist: artists.Add(h); break;
                    case EntityKind.Album: albums.Add(h); break;
                    case EntityKind.Playlist or EntityKind.Collection: playlists.Add(h); break;
                }
            }
            Span<FallbackRow> plan = stackalloc FallbackRow[FallbackMaxRows];
            int m = FallbackRows(tracks.Count, artists.Count, albums.Count, playlists.Count, plan);
            var rows = new List<FallbackHit>(m);
            for (int i = 0; i < m; i++)
            {
                var list = plan[i].Facet switch
                {
                    SearchFacet.Tracks => tracks,
                    SearchFacet.Artists => artists,
                    SearchFacet.Albums => albums,
                    _ => playlists,
                };
                var hit = list[plan[i].Index];
                rows.Add(new FallbackHit(new HitItem(hit, VersionOf(hit), all.FlagsOf(hit)), plan[i].Large));
            }
            return rows;
        }

        /// <summary>The rail: the ranked hits' playlists, then the Playlists facet's own list when it has landed.</summary>
        List<HitItem> RailItems(Search all, EntityRef hero)
        {
            int n = all.ResultCount;
            var hits = new EntityRef[n];
            for (int i = 0; i < n; i++) hits[i] = all.ResultRef(i);
            var facetRow = Entities.Search(_query.AsSpan(), SearchFacet.Playlists);
            ReadOnlySpan<int> own = facetRow.Knows(SearchFields.Results) ? facetRow.ResultSlots : default;
            var slots = new int[n + own.Length];
            int m = PlaylistShelfItems(hits, own, hero, slots);
            var playlists = Entities.Current.Playlists;
            var items = new List<HitItem>(m);
            for (int i = 0; i < m; i++)
                items.Add(new HitItem(new EntityRef(EntityKind.Playlist, slots[i]), playlists.Version[slots[i]], SearchHitFlags.None));
            return items;
        }

        static uint VersionOf(EntityRef hit) => hit.Table is { } t && (uint)hit.Slot < (uint)t.Count ? t.Version[hit.Slot] : 0u;

        /// <summary>The genre tiles as browse-category tile models: each opens its browse page (or a re-committed search on its name)
        /// carrying the search LOOKUP origin (<see cref="OpenGenre"/>). An empty list draws nothing (parity 38).</summary>
        static BrowseTileModel[] GenreTiles(Search all, Shell.NavOrigin? origin)
        {
            var slots = all.GenreSlots;
            var models = new BrowseTileModel[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                var node = new Browse(slots[i]);
                string uri = node.Id.Text;
                string name = Entities.Strings.Resolve(node.TitleId);
                models[i] = new BrowseTileModel(name, uri, null, null, () => OpenGenre(uri, name, origin));
            }
            return models;
        }

        /// <summary>The related queries (server order) with the typed query dropped (parity 27-28).</summary>
        static List<string> RelatedOf(Search all, string typed)
        {
            var related = all.Related;
            var queries = new List<string>(related.Length);
            for (int i = 0; i < related.Length; i++)
            {
                string q = Entities.Strings.Resolve(related[i].Query);
                if (KeepsRelated(q, typed)) queries.Add(q);
            }
            return queries;
        }

        // ── columns, facet selection ────────────────────────────────────────────────────────────────────────────────

        int ColsNow(float w)
        {
            int cols = ColsFor(w, _cols, _colsInit);
            _cols = cols;
            _colsInit = true;
            return cols;
        }

        void SelectFacet(SearchFacet facet)
        {
            for (int i = 0; i < _facetCount; i++)
                if (_facets[i] == facet) { _chip.Value = i; return; }
        }

        // ── demand ──────────────────────────────────────────────────────────────────────────────────────────────────

        void Demand()
        {
            if (!_all.IsValid) return;
            Entities.Ensure(_all, SearchFields.All);
            _askedAll = true;
            if (_facet is not (SearchFacet.All or SearchFacet.Genres) && _row.IsValid)
            {
                Entities.Ensure(_row, SearchFields.Results);
                _askedFacets |= 1u << (int)_facet;
            }
            _deadline.Restart();
        }

        void DemandTracked()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Searches.Changed.Value;
            _ = scope.Edges.SearchResult.Changed.Value;
            if (!_all.IsValid) return;
            EnsureHits(_all);
            if (_row != _all && _row.IsValid)
            {
                EnsureHits(_row);
                if (IsGridFacet(_facet) && KeepsPaging(_row.ResultState, _row.ResultCount))
                    Entities.EnsureEdge(FetchEdge.SearchResults, _row.Slot, _row.ResultCount);
            }
        }

        /// <summary>One batched ensure per kind for a row's hits.</summary>
        void EnsureHits(Search row)
        {
            var slots = row.ResultSlots;
            var kinds = row.ResultKinds;
            int n = Math.Min(slots.Length, kinds.Length);
            if (n == 0) return;
            if (_hitScratch.Length < n) _hitScratch = new int[Math.Max(n, _hitScratch.Length * 2)];
            foreach (var (kind, wanted) in s_hitGroups)
            {
                int m = 0;
                for (int i = 0; i < n; i++)
                    if (kinds[i].Kind == kind && slots[i] > Table.None) _hitScratch[m++] = slots[i];
                if (m > 0 && Entities.TableFor(kind) is { } table) Entities.Ensure(table, _hitScratch.AsSpan(0, m), wanted);
            }
        }

        /// <summary>Retry the facet body: re-ask what a terminal failure un-asked, then re-read the marks.</summary>
        void Retry()
        {
            if (_facet is SearchFacet.All or SearchFacet.Genres) { RetryAll(); return; }
            if (!_row.IsValid) return;
            Entities.Ensure(_row, SearchFields.Results);
            _deadline.Restart();
            _recheck.Value = _recheck.Peek() + 1;
        }

        void RetryAll()
        {
            if (!_all.IsValid) return;
            Entities.Ensure(_all, SearchFields.All);
            _deadline.Restart();
            _recheck.Value = _recheck.Peek() + 1;
        }

        /// <summary>The deadline: re-read the marks, and re-arm only while a request for the bound rows is still out.</summary>
        void Recheck()
        {
            if (_scope is null || !ReferenceEquals(_scope, Entities.Current)) return;
            _recheck.Value = _recheck.Peek() + 1;
            var t = _scope.Searches;
            bool outstanding = (_all.IsValid && t.Inflight[_all.Slot] != 0) || (_row.IsValid && t.Inflight[_row.Slot] != 0);
            if (outstanding) _deadline.Restart();
        }
    }
}
