// ── Entities/Search.Page.cs ────────────────────────────────────────────────────────────────────────────────────────
// the search results page: the route, the page component (query echo, facet row, the keyed sliding facet body), the
// ONE demand, the readiness of each facet, and the facet dispatch
//
// Role: UI
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
// The facet body is keyed "facet-body:<facet>" and carries `MotionRecipes.PageSlideForward` / `PageSlideBack` only on a
// REAL chip change (`Search.Slides`): mounting never slides. The body region is `smoothResize: false` — a shimmer and a
// complete result set differ by thousands of DIP (§9 must-not-simplify 2).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

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

    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;
    static readonly Func<Element> s_genreSeed = static () => GenreSeed();

    /// <summary>The deadline after which an unanswered facet re-reads its marks.</summary>
    const float RecheckMs = 10_000f;

    enum Readiness : byte { Pending, Ready, Concluded, Failed }

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
        readonly Func<Element> _content, _shimmer, _onFailed;
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
            _content = Body;
            _shimmer = () => Shimmer(_facet);
            _demand = Demand;
            _demandTracked = DemandTracked;
            _retry = Retry;
            _onFailed = () => Failed(_retry);
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
            bool hideArt = Prefs.Appearance.TrackArtworkHidden();
            var overlay = UseContext(Overlay.Service);

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
            _ctx = new HitContext(_query, _origin, overlay, hideArt);

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

            if (!_all.IsValid) return Controls.Vacancy(Controls.VacancyVoice.Empty);

            var allReady = ReadinessOf(_all, SearchFields.Chips, _askedAll);
            // The tabs arrive WITH the first result, not a beat before it: the chip row stays skeletal until the body has
            // answered once for this query (Search.ChipRowSkeletal). Both rows are keyed so the swap is a mount, not an
            // in-place patch — an unkeyed swap never fires Enter — and the real row rides the body's own 8-DIP rise.
            _bodyAnswered |= BodyReadiness() != Readiness.Pending;
            Element chipRow = ChipRowSkeletal(_hadChips, allReady == Readiness.Pending, _bodyAnswered)
                ? FacetRowSkeleton() with { Key = "facets-skel" }
                : FacetRow(_all, _facets.AsSpan(0, _facetCount), Math.Min(chip, _facetCount - 1), _selectors) with
                {
                    Key = "facets",
                    Enter = new EnterExit(Dy: 8f, Opacity: 0f, Active: true),
                    Transition = MotionTokenDef.Eased(Design.Motion.Standard, Easing.SmoothOut),
                };

            var region = new SkelRegionEl(
                Pending: _pending, Failed: _failed, Content: _content, ShimmerSource: _shimmer, OnFailed: _onFailed,
                Reveal: _facet == SearchFacet.Tracks ? SkelReveal.None : SkelReveal.StaggerRows,
                Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
            LayoutTransition? motion = slide ? (forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack) : null;
            string facetId = ((int)_facet).ToString(CultureInfo.InvariantCulture);

            Element body = IsGridFacet(_facet)
                ? new BoxEl
                {
                    Key = "facet-body:" + facetId, Animate = motion,
                    Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
                    Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, 0f),
                    Children = [region],
                }
                : ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Design.Dock.Reserve + Spacing.XXL),
                    Children =
                    [
                        new BoxEl { Key = "facet-body:" + facetId, Animate = motion, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [region] },
                    ],
                }) with
                {
                    Grow = 1f, MinWidth = 0f, MinHeight = 0f, ScrollKey = "search:" + facetId,
                };

            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.S,
                        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.S),
                        Children = [QueryEcho(_echo), chipRow],
                    },
                    body,
                ],
            };
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

        static bool IsGridFacet(SearchFacet f) => f is SearchFacet.Albums or SearchFacet.Playlists;

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

        Element Body()
        {
            var r = BodyReadiness();
            if (r == Readiness.Pending) return Shimmer(_facet);
            var ctx = _ctx;
            var row = _row;
            if (r == Readiness.Concluded && _facet != SearchFacet.All) return Empty(_facet, _query);
            return _facet switch
            {
                SearchFacet.All => AllBody(in ctx),
                SearchFacet.Genres => GenreGrid(_all, header: false, _origin),
                SearchFacet.Tracks => row.ResultCount == 0 ? Empty(_facet, _query) : SongsGrid(row, in ctx),
                SearchFacet.Albums or SearchFacet.Playlists => row.ResultCount == 0 ? Empty(_facet, _query) : FacetGrid(row, ctx),
                SearchFacet.Artists => ArtistsList(row, in ctx),
                SearchFacet.Podcasts or SearchFacet.Episodes or SearchFacet.Profiles => HitsList(row, _facet, in ctx),
                // Audiobook and author hits have no table in 0.3 (reported): the facet is its own empty sentence.
                _ => Empty(_facet, _query),
            };
        }

        /// <summary>The All tab (W1): the Top Result and Best matches off the ranked list — or, for an answer that carried
        /// none, the fallback interleave — then the playlist rail, the genre links and the related queries.</summary>
        Element AllBody(in HitContext ctx)
        {
            var all = _all;
            int n = all.ResultCount;
            var kids = new List<Element>(6);
            EntityRef hero = default;
            bool collected = (all.RowFlags & SearchRowFlags.Collected) != 0;
            if (n > 0 && !collected)
            {
                hero = all.ResultRef(0);
                kids.Add(TopResult(hero, all.FlagsOf(hero), in ctx));
                if (n > 1)
                {
                    var items = new List<HitItem>(n - 1);
                    for (int i = 1; i < n; i++)
                    {
                        var h = all.ResultRef(i);
                        if (!h.IsNone) items.Add(new HitItem(h, VersionOf(h), all.FlagsOf(h)));
                    }
                    kids.Add(BestMatches(items, _colsFor, ctx));
                }
            }
            else if (n > 0) kids.Add(FallbackList(all, in ctx));
            else kids.Add(Empty(SearchFacet.All, ctx.Query));

            var rail = RailItems(all, hero);
            if (rail.Count > 0) kids.Add(PlaylistRail(rail, _selectPlaylists, ctx));
            kids.Add(GenreSection());
            kids.Add(RelatedSection());
            return new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
        }

        /// <summary>0.2.9's no-top-hits list: the pure interleave (<see cref="FallbackRows"/>) over the collected hits.</summary>
        static Element FallbackList(Search all, in HitContext ctx)
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
            if (m == 0) return Empty(SearchFacet.All, ctx.Query);
            var rows = new Element[m];
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
                rows[i] = HitRow(hit, all.FlagsOf(hit), plan[i].Large, in ctx);
            }
            return HitColumn(rows);
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

        Element GenreSection()
        {
            switch (ReadinessOf(_all, SearchFields.Genres, _askedAll))
            {
                case Readiness.Ready: return GenreGrid(_all, header: true, _origin);
                case Readiness.Failed: return SectionFailed(_retryAll);
                case Readiness.Concluded: return new BoxEl();
                default:
                    return new SkelRegionEl(
                        Pending: s_true, Failed: s_false, Content: s_genreSeed, ShimmerSource: null, OnFailed: null,
                        Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
            }
        }

        /// <summary>Related queries: nothing while pending (0.2.9), the compact error with Retry on failure — matching the
        /// genre section rather than vanishing silently (§9 gap 2).</summary>
        Element RelatedSection() => ReadinessOf(_all, SearchFields.Related, _askedAll) switch
        {
            Readiness.Ready => RelatedQueries(_all, _query),
            Readiness.Failed => SectionFailed(_retryAll),
            _ => new BoxEl(),
        };

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
