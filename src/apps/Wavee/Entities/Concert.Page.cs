// ── Entities/Concert.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// hub, filter bar, artist schedule, month board, detail, shy pill + ticker + preloader
//
// Role: UI
// Owner: N (stream N-C)
// Wave: 5
// Budget: 1900 lines
// Spec: ch 17 §0 (the fifteen non-negotiables), §1 (the three trees), §2 W1-W22, §5 motion, §6 interaction, §7
//   readiness, §9 must-not-simplify; WP-5.N contract §5 (routes), §7 (install + the hub seam)
//
// THE THREE PAGES READ `Entities.Current`. The schedule demands its edge through Fetch
// (`EnsureEdge(FetchEdge.ArtistConcerts, artist)` while Unknown); the detail demands `Entities.Ensure(concert, All)`.
// The HUB is the one surface whose data is not a row a Fetch batch can plan — a feed subject, a count preview, a place's
// concepts, a place search / save — so it asks `ConcertHost.Current` (Concert.cs), which commits and publishes like any
// answer; the page then re-reads the tables. What 0.2.9 did with a query GENERATION is structural here: every filter tuple
// is its own feed subject (`ConcertFeedKey`), so a late answer lands on ITS subject and can never paint another.
//
// PROPS FREEZE AT MOUNT. Every page/child takes signals, delegates or a keyed remount (§1.4, §9 #9): the filter bar reads
// the page's signals; the month board is keyed on its composed signature and reads a revision signal; the append tail is
// keyed per pagination key; the ticker's clock and the shy pill's linger clock are keyed per arm. Nothing ticks at rest:
// the ticker's clock exists only mid-tween, the linger clock only while the pill shows.

using System.Globalization;
using System.Runtime.InteropServices;
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

public readonly partial struct Concert
{
    // ══ 1. INSTALL AND THE MOUNT POINTS (contract §7) ═════════════════════════════════════════════════════════════════

    /// <summary>Owner N-C's install, called by <c>Artist.InstallPages</c>: the three concert route kinds, the loc copy the
    /// rules speak, the hub's data seam and the sidebar's concert feed seam (G-174).</summary>
    internal static void InstallPages()
    {
        ConcertCopy.Current = ConcertCopy.Localized;
        ConcertHost.Current = Spotify.Api.Concerts;
        Sidebar.ConcertsFetch = static (place, radiusKm, feed) =>
            ConcertHost.Current.Feed(feed, new ConcertFeedQuery(ConcertPlaces.From(place), null, radiusKm), false, null);
        Shell.SetPage(Shell.RouteKind.Concerts, MountHub);
        Shell.SetPage(Shell.RouteKind.ArtistConcerts, MountSchedule);
        Shell.SetPage(Shell.RouteKind.Concert, MountDetail);
    }

    /// <summary><see cref="Shell.RouteKind.Concerts"/>: one hub per KeepAlive slot (its accumulated pages survive a park).</summary>
    // MOUNT POINT (stage B contract)
    public static Element MountHub(in Shell.Route route)
        => Embed.Comp(static () => new HubPage()) with { Key = "concert-hub" };

    /// <summary><see cref="Shell.RouteKind.ArtistConcerts"/>, keyed by the route: artist → artist is a fresh page.</summary>
    // MOUNT POINT (stage B contract)
    public static Element MountSchedule(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, key, route.Arg), static () => new SchedulePage()) with { Key = "artist-schedule:" + key };
    }

    /// <summary><see cref="Shell.RouteKind.Concert"/>, keyed by the route.</summary>
    // MOUNT POINT (stage B contract)
    public static Element MountDetail(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, key, route.Arg), static () => new DetailPage()) with { Key = "concert-detail:" + key };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey, StringId Arg);

    // ══ 2. THE SHARED FRAME PIECES ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A node that scrolls under the masthead: cut at the reserve's lower edge, feathered 24 DIP only while the cut
    /// is engaged (§4 row 10: nothing is softened at rest).</summary>
    static Element UnderBand(BoxEl node, bool engaged, Action<bool> onEdge)
        => (node with { EdgeFade = engaged ? new EdgeFadeSpec(EdgeMask.Top, BrowseMastheadMetrics.ClipFadeBand) : null })
            .ClipBelow(BrowseMastheadMetrics.ClipInset, onEdge);

    /// <summary>The schedule/detail page frame (§1.2, §1.3): the 84 + 40 reserve as a SPACER, then the 32-gutter body cut
    /// under the band, one vertical viewport.</summary>
    static ScrollEl SubPageScroll(string scrollKey, Element region, bool engaged, Action<bool> onEdge) => ScrollView(new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl { Height = BrowseMastheadMetrics.Reserve + 40f, HitTestVisible = false },
            UnderBand(new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(32f, 0f, 32f, Design.Dock.Reserve + 40f), Children = [region],
            }, engaged, onEdge),
        ],
    }) with { Grow = 1f, MinHeight = 0f, ScrollKey = scrollKey };

    /// <summary>A region that is its shimmer seed while pending and the content once ready (§0 #11: SkelReveal.Soft).</summary>
    static Element Region(Func<bool> pending, Func<bool> failed, Func<Element> content, Func<Element> shimmer, Func<Element>? onFailed, string group)
        => new SkelRegionEl(pending, failed, content, shimmer, onFailed, SkelReveal.Soft, SkeletonStyle.Default, group, SmoothResize: false);

    static void GoToConcert(int slot) => Shell.GoTo(DetailRoute(new Concert(slot)));

    /// <summary>A shelf item as a VALUE (slot + version): PagedShelf compares items by value, so a publish that changes
    /// nothing a tile paints rebuilds no tile.</summary>
    readonly record struct ShelfItem(int Slot, uint Version);

    static readonly Func<ShelfItem, int, string> s_itemKey = static (item, _) => item.Slot.ToString(CultureInfo.InvariantCulture);
    static readonly Func<ShelfItem, int, float, Element> s_concertCard = static (item, _, _) =>
    {
        int slot = item.Slot;
        return Tile(TileOf(new Concert(slot)), () => GoToConcert(slot));
    };

    static ShelfItem[] ConcertItems(ReadOnlySpan<int> slots, int cap)
    {
        var items = new ShelfItem[Math.Min(slots.Length, cap)];
        var t = Entities.Current.Concerts;
        for (int i = 0; i < items.Length; i++) items[i] = new ShelfItem(slots[i], t.Version[slots[i]]);
        return items;
    }

    // ══ 3. THE COUNT TICKER (§0 #5, §5) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The events figure: a FIXED 64-DIP right-aligned number box + " events". A new target eases the shown value
    /// over 380 ms cubic-out on the FRAME clock (<see cref="Design.FrameTime.NowQpc"/> — 0.2.9 sampled the wall clock); the
    /// per-frame clock child mounts only while a tween runs (keyed per target, so a retarget restarts cleanly).</summary>
    sealed class CountTicker : Component
    {
        const double TweenMs = 380.0;
        readonly IReadSignal<int?> _target;
        readonly Signal<double> _shown = new(0);
        readonly Func<TickerClock> _clock;
        readonly string _suffix = " " + Loc.Get(Strings.Concerts.Filter.Events);
        int _to = int.MinValue;
        double _from;
        long _startQpc;

        public CountTicker(IReadSignal<int?> target)
        {
            _target = target;
            Action step = Step;
            _clock = () => new TickerClock(step);
        }

        public override Element Render()
        {
            int goal = _target.Value ?? 0;
            double shown = _shown.Value;
            bool animating = (int)Math.Round(shown) != goal;
            if (animating && _to != goal) { _to = goal; _from = shown; _startQpc = 0; }
            var kids = new Element[animating ? 3 : 2];
            kids[0] = new BoxEl
            {
                Width = 64f, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                Children = [Design.Type.TrackTitle(((int)Math.Round(shown)).ToString("N0", CultureInfo.CurrentCulture)) with { MaxLines = 1 }],
            };
            kids[1] = Body(_suffix) with { Color = Tok.TextSecondary, MaxLines = 1 };
            if (animating) kids[2] = Embed.Comp(_clock) with { Key = "count-tick:" + goal.ToString(CultureInfo.InvariantCulture) };
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f, Children = kids };
        }

        void Step()
        {
            long now = Design.FrameTime.NowQpc;
            if (_startQpc == 0) _startQpc = now;
            double p = Math.Min(1.0, (now - _startQpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / TweenMs);
            p = 1.0 - Math.Pow(1.0 - p, 3);
            _shown.Value = p >= 1.0 ? _to : _from + (_to - _from) * p;
        }
    }

    /// <summary>A 0-size leaf subscribed to the frame tick while mounted (the Track.Table ticker shape): the mount run only
    /// subscribes; every later frame calls the step.</summary>
    sealed class TickerClock(Action step) : Component
    {
        bool _subscribed;

        public override Element Render()
        {
            var tick = UseContextSignal(FrameClock.Tick);
            UseSignalEffect(() =>
            {
                _ = tick.Value;
                if (!_subscribed) { _subscribed = true; return; }
                step();
            });
            return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
        }
    }

    // ══ 4. THE APPEND TAIL (W11; §0 #15) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The hub feed's tail, keyed per pagination key by the page: (C) only NEAR the tail, (B) after a 300-ms arm
    /// re-checked when it fires, (A) never concurrently; three quiet failures collapse it. Far from the tail it is a 16-DIP
    /// spacer with no shimmer subtree (no standing pulse); near or loading it shimmers four tiles of the grid's family.</summary>
    sealed class AppendTail(IReadSignal<bool> loading, IReadSignal<bool> nearTail, Action start) : Component
    {
        static readonly Func<bool> s_always = static () => true, s_never = static () => false;
        static readonly Func<Element> s_empty = static () => new BoxEl(), s_shimmer = ShimmerGrid;
        int _attempts;
        TimerHandle _arm;

        public override Element Render()
        {
            bool near = nearTail.Value, busy = loading.Value;
            _arm = UseTimeout(Fire, 300f);
            UseSignalEffect(() =>
            {
                bool n = nearTail.Value, l = loading.Value;
                if (!n || l || _attempts >= 3) { _arm.Cancel(); return; }
                _arm.Restart();
            });
            if (!busy && _attempts >= 3) return new BoxEl();
            if (!near && !busy) return new BoxEl { Height = Spacing.L };
            return Region(s_always, s_never, s_empty, s_shimmer, null, "concert-hub-append");
        }

        void Fire()
        {
            if (loading.Peek() || !nearTail.Peek() || _attempts >= 3) return;
            _attempts++;
            start();
        }

        static Element ShimmerGrid()
        {
            var cards = new Element[4];
            for (int i = 0; i < cards.Length; i++) cards[i] = Tile(SeedTile("append:" + i.ToString(CultureInfo.InvariantCulture)), null);
            return AutoGrid(ConcertLayout.GridMinColumn, ConcertLayout.GridColumnGap, float.NaN, cards);
        }
    }

    // ══ 5. THE HUB (W1-W5, W12; §0 #2, #11; §9 #1-#3, #5, #6, #8) ════════════════════════════════════════════════════

    /// <summary>What the pinned bar reads (live signals and the page's verbs) — a frozen reference whose members are live.</summary>
    sealed record BarInputs(
        Signal<ConcertPlace?> Place, Signal<bool?> Inferred, Signal<int> Radius, Signal<ConcertWhen> When,
        Signal<IReadOnlyList<string>> Selected, Signal<IReadOnlyList<ConcertConcept>> Concepts, IReadSignal<int?> Count,
        Action<NodeHandle> CaptureWhere, Func<NodeHandle> WhereAnchor, Action OpenPicker);

    /// <summary>The <c>concerts</c> destination (0.2.9 <c>ConcertHubPage</c>): ONE viewport over [header card ‖ clipped] ·
    /// [the filter bar, pinned at 84] · [the feed region ‖ clipped] — the two-clip sandwich (§9 #1). The filter tuple is the
    /// page's own state; its feed SUBJECT is <see cref="ConcertFeedKey"/>'s, so a tuple change is a new subject with its own
    /// edges and cursor, and a late page can only land on the subject that asked for it (§9 #2).</summary>
    sealed class HubPage : Component
    {
        static uint s_countAsk;

        readonly Signal<int> _radius = new(ConcertHub.DefaultRadiusKm);
        readonly Signal<ConcertWhen> _when = new(ConcertWhen.Any);
        readonly Signal<IReadOnlyList<string>> _selected = new(Array.Empty<string>());
        readonly Signal<ConcertPlace?> _place = new(null);
        readonly Signal<bool?> _inferred = new(null);
        readonly Signal<IReadOnlyList<ConcertConcept>> _concepts = new(Array.Empty<ConcertConcept>());
        readonly Signal<int?> _count = new(null);
        readonly Signal<int> _locationTick = new(0);
        readonly Signal<float> _scroll = new(0f);
        // Seeded TRUE so a first page shorter than the viewport still fills once; dropped after every append (§0 #15).
        readonly Signal<bool> _nearTail = new(true), _appendLoading = new(false), _headerUnder = new(false), _bodyUnder = new(false);
        readonly PlacePicker _picker = new();
        readonly HashSet<int> _countAsked = new();

        Scope? _scope, _settledScope;
        bool _locationAsked, _pending, _failed;
        NodeHandle _whereAnchor;
        IOverlayService? _overlay;
        Element? _body;
        int _keyPlace = -1, _keyRadius, _feedSlot, _gridStart = -1, _conceptsPlace = -1;
        ConcertWhen? _keyWhen;
        IReadOnlyList<string>? _keySelected;
        Scope? _keyScope;
        uint _conceptsVersion = uint.MaxValue;

        readonly BarInputs _bar;
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _failedPanelFn;
        readonly Func<FilterBar> _barFactory;
        readonly Func<AppendTail> _tailFactory;
        readonly Func<LazyGrid> _gridFactory;
        readonly Func<int> _gridCount;                           // the count memo's compute (UseComputed in Render)
        Memo<int>? _gridCountMemo;                               // set by the first Render, before any grid mounts
        readonly Action _sync, _retry, _append, _openPicker;
        readonly Action<bool> _onHeaderEdge, _onBodyEdge, _onLocation, _onAppended;
        readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) _scrollWatch;

        public HubPage()
        {
            _pendingFn = () => _pending;
            _failedFn = () => _failed;
            _contentFn = () => _body ?? new BoxEl();
            _sync = Sync;
            _retry = () => AskFeed(false, null, null);
            _append = Append;
            _openPicker = () => _picker.Toggle(_overlay, () => _whereAnchor);
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _retry);
            _onHeaderEdge = v => _headerUnder.SetIfChanged(v);
            _onBodyEdge = v => _bodyUnder.SetIfChanged(v);
            _onLocation = _ => { _settledScope = Entities.Current; _locationTick.Value = _locationTick.Peek() + 1; };
            _onAppended = ok => { _appendLoading.Value = false; if (ok) _nearTail.Value = false; };
            _bar = new BarInputs(_place, _inferred, _radius, _when, _selected, _concepts, _count,
                h => _whereAnchor = h, () => _whereAnchor, _openPicker);
            _barFactory = () => new FilterBar(_bar);
            _tailFactory = () => new AppendTail(_appendLoading, _nearTail, _append);
            _gridCount = GridCount;
            // The churn-free count (W3-A4): the grid subscribes to the count MEMO only — a FeedSection publication that
            // leaves the AllEvents run's length unchanged never re-renders the grid or rebuilds its realized cells. The
            // memo is created by the first Render (UseComputed), which runs before any grid can mount through this factory.
            _gridFactory = () => new LazyGrid(count: _gridCountMemo!, cell: GridCell, ensureRange: static (_, _) => { },
                minColWidth: ConcertLayout.GridMinColumn, gap: ConcertLayout.GridColumnGap,
                rowExtra: ConcertLayout.GridRowExtra, overscanRows: 3);
            // The near-tail watch: the offset floored to 24 px × the content height floored to 48 px (an append's own
            // growth re-evaluates nearness); near = within 1.5 viewports of the end. The page scroll feeds the LazyGrid.
            _scrollWatch = (static g => ((long)(g.OffsetY / 24f) << 20) ^ (long)(g.ContentH / 48f), g =>
            {
                _scroll.Value = g.OffsetY;
                _nearTail.SetIfChanged(g.OffsetY + g.ViewportH >= g.ContentH - 1.5f * g.ViewportH);
            });
        }

        public override Element Render()
        {
            _ = Entities.ScopeEpoch.Value;                        // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Places.Changed.Value;
            _ = scope.ConcertFeeds.Changed.Value;
            _ = scope.Concerts.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = e.FeedSection.Changed.Value;
            _ = e.FeedSectionPlaylists.Changed.Value;
            _ = _locationTick.Value;
            int radius = _radius.Value;
            var when = _when.Value;
            var selected = _selected.Value;
            _overlay = UseContext(Overlay.Service);
            if (!ReferenceEquals(_scope, scope))
            {
                _scope = scope;
                _locationAsked = false;
                _countAsked.Clear();
                _conceptsVersion = uint.MaxValue;
            }
            UseEffect(_sync);
            _gridCountMemo = UseComputed(_gridCount);

            int placeSlot = scope.SavedPlace;
            int feed = placeSlot > 0 ? FeedSlot(placeSlot, radius, when, selected) : Table.None;
            var state = feed > 0 ? e.FeedSection.Readiness(feed) : EdgeState.Unknown;
            _pending = placeSlot > 0 ? state == EdgeState.Unknown : !ReferenceEquals(_settledScope, scope);
            _failed = state == EdgeState.Failed;
            _body = _pending || _failed ? null : placeSlot <= 0 ? NoLocation() : FeedBody(scope, feed);

            Element region = Region(_pendingFn, _failedFn, _contentFn, FeedSeed, _failedPanelFn, "concert-hub");
            var content = new BoxEl
            {
                Direction = 1,
                Children =
                [
                    // The reserve + 24 as a SPACER: the clipped nodes start where the content does, so each cut engages
                    // only once it scrolls (BrowseMastheadMetrics.FamilyUnderBandPad).
                    new BoxEl { Height = BrowseMastheadMetrics.Reserve + Spacing.XXL, HitTestVisible = false },
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.L,
                        Padding = BrowseMastheadMetrics.FamilyUnderBandPad(Design.Dock.Reserve + Spacing.PageWide),
                        Children =
                        [
                            UnderBand(HeaderCard(), _headerUnder.Value, _onHeaderEdge),
                            Embed.Comp(_barFactory) with { Key = "concert-filter-bar" },
                            UnderBand(new BoxEl { Direction = 1, MinWidth = 0f, Children = [region] }, _bodyUnder.Value, _onBodyEdge),
                        ],
                    },
                ],
            };
            Element scroll = ScrollView(content) with
            {
                Grow = 1f, MinHeight = 0f, ScrollKey = "concert-hub", OnScrollGeometryChanged = _scrollWatch,
            };
            return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)_scroll, scroll);
        }

        static BoxEl HeaderCard() => new()
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.XS, Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                SectionCaption(Loc.Get(Strings.Concerts.LiveMusic)),
                Design.Type.PageHero(Loc.Get(Strings.Concerts.Title)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Body(Loc.Get(Strings.Concerts.Subtitle)) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };

        /// <summary>W3: no place ⇒ the prompt, whose one quiet action anchors the picker at the WHERE PILL.</summary>
        Element NoLocation() => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.EmptyTitle),
            subtitle: Loc.Get(Strings.Concerts.EmptyWithoutLocation), actionLabel: Loc.Get(Strings.Concerts.Location.Set),
            onAction: _openPicker);

        // ── the demand + the bar's inputs (an effect: it re-runs as the tables publish and the tuple moves) ──────────

        void Sync()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Places.Changed.Value;
            _ = scope.Concepts.Changed.Value;
            _ = scope.ConcertFeeds.Changed.Value;
            _ = e.PlaceConcepts.Changed.Value;
            _ = e.FeedSection.Changed.Value;
            int radius = _radius.Value;
            var when = _when.Value;
            var selected = _selected.Value;
            var host = ConcertHost.Current;

            int placeSlot = scope.SavedPlace;
            if (placeSlot <= 0 && !_locationAsked)
            {
                _locationAsked = true;
                host.ResolveLocation(_onLocation);
            }
            var place = ConcertPlaces.From(placeSlot);
            _place.SetIfChanged(place);
            _inferred.SetIfChanged(ConcertPlaces.IsInferred(placeSlot));
            if (place is null)
            {
                if (_concepts.Peek().Count > 0) _concepts.Value = Array.Empty<ConcertConcept>();
                return;
            }

            // Concepts: asked once per place; a selection survives a place change only while still offered, and the
            // feed requeries only when the kept set actually shrank (§9 #3) — the new tuple re-runs this effect.
            var concepts = e.PlaceConcepts;
            if (concepts.State(placeSlot) == EdgeState.Unknown && !concepts.IsFailed(placeSlot))
                host.Concepts(placeSlot, selected.Count > 0 ? selected[0] : null, null);
            uint version = concepts.Version(placeSlot);
            if (version != _conceptsVersion || placeSlot != _conceptsPlace)
            {
                _conceptsVersion = version;
                _conceptsPlace = placeSlot;
                var all = ConcertPlaces.ConceptsOf(placeSlot);
                _concepts.Value = all;
                if (concepts.State(placeSlot) == EdgeState.Complete)
                {
                    var kept = ConcertHub.ReconcileConcepts(selected, all);
                    if (!ReferenceEquals(kept, selected)) { _selected.Value = kept; return; }
                }
            }

            int feed = FeedSlot(placeSlot, radius, when, selected);
            var query = Query(place, radius, when, selected, null);
            var feeds = e.FeedSection;
            // Never re-ask a FAILED subject from here: its Retry does (an auto re-ask would loop on a dead network).
            if (feeds.State(feed) == EdgeState.Unknown && !feeds.IsFailed(feed)) host.Feed(feed, query, false, null);
            ref readonly var row = ref scope.ConcertFeeds.Row[feed];
            if (row.CountVersion > 0) _count.SetIfChanged(row.Count);
            else if (_countAsked.Add(feed)) host.Count(feed, query, ++s_countAsk, null);
        }

        static ConcertFeedQuery Query(ConcertPlace place, int radius, ConcertWhen when, IReadOnlyList<string> selected, string? cursor)
            => new(place, selected.Count > 0 ? selected : null, radius, when.Range, cursor);

        /// <summary>The subject slot for the tuple — recomputed (and its key interned) only when an input changed.</summary>
        int FeedSlot(int placeSlot, int radius, ConcertWhen when, IReadOnlyList<string> selected)
        {
            var scope = Entities.Current;
            if (placeSlot == _keyPlace && radius == _keyRadius && ReferenceEquals(when, _keyWhen)
                && ReferenceEquals(selected, _keySelected) && ReferenceEquals(scope, _keyScope)) return _feedSlot;
            _keyPlace = placeSlot;
            _keyRadius = radius;
            _keyWhen = when;
            _keySelected = selected;
            _keyScope = scope;
            string key = ConcertFeedKey.For(ConcertFeedKey.PlaceKey(ConcertPlaces.From(placeSlot)), radius, when.Range, selected);
            return _feedSlot = ConcertPlaces.FeedSlot(key);
        }

        void AskFeed(bool append, string? cursor, Action<bool>? done)
        {
            var scope = Entities.Current;
            if (ConcertPlaces.From(scope.SavedPlace) is not { } place || _feedSlot <= 0) { done?.Invoke(false); return; }
            ConcertHost.Current.Feed(_feedSlot, Query(place, _radius.Peek(), _when.Peek(), _selected.Peek(), cursor), append, done);
        }

        void Append()
        {
            if (_appendLoading.Peek() || _feedSlot <= 0) return;
            var cursor = Entities.Current.ConcertFeeds.Row[_feedSlot].PaginationKey;
            if (cursor.IsEmpty) return;
            _appendLoading.Value = true;
            AskFeed(true, Entities.Strings.Resolve(cursor), _onAppended);
        }

        // ── the feed body (§1.1: shelves · promos · the virtualized grid · the tail) ──────────────────────────────────

        Element FeedBody(Scope scope, int feed)
        {
            var e = scope.Edges;
            var targets = e.FeedSection.Targets(feed);
            var payload = e.FeedSection.Payload(feed);
            var promoTargets = e.FeedSectionPlaylists.Targets(feed);
            var promos = e.FeedSectionPlaylists.Payload(feed);
            if (ConcertHub.IsFeedEmpty(targets, promoTargets))
                return Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.EmptyTitle),
                    subtitle: Loc.Get(Strings.Concerts.EmptyForLocation));

            var kids = new List<Element>(8);
            ulong placed = 0;
            bool grid = false;
            for (int i = 0; i < payload.Length;)
            {
                int end = ConcertFeedMerge.SectionEnd(payload, i);
                var head = payload[i];
                if (head.Kind == (byte)ConcertFeedSectionKind.AllEvents)
                {
                    if (!grid) { grid = true; kids.Add(EventsGrid(feed)); }
                }
                else kids.Add(ConcertShelf(head, targets[i..end]));
                placed |= PromosFor(kids, head, promoTargets, promos);
                i = end;
            }
            // A section that shipped promos but zero concerts still gets its promo shelf (parity 57).
            for (int g = 0, j = 0; j < promos.Length; g++)
            {
                int end = ConcertFeedMerge.SectionEnd(promos, j);
                if (g >= 64 || (placed & (1UL << g)) == 0) kids.Add(PromoShelf(promos[j], promoTargets[j..end]));
                j = end;
            }
            var cursor = scope.ConcertFeeds.Row[feed].PaginationKey;
            if (!cursor.IsEmpty)   // absent entirely when the feed is complete
                kids.Add(Embed.Comp(_tailFactory) with { Key = "concert-append:" + Entities.Strings.Resolve(cursor) });
            return new BoxEl { Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = kids.ToArray() };
        }

        static ulong PromosFor(List<Element> kids, in FeedSectionEdge head, ReadOnlySpan<int> targets, ReadOnlySpan<FeedSectionEdge> promos)
        {
            long group = ConcertFeedMerge.GroupOf(head);
            for (int g = 0, j = 0; j < promos.Length && g < 64; g++)
            {
                int end = ConcertFeedMerge.SectionEnd(promos, j);
                if (ConcertFeedMerge.GroupOf(promos[j]) == group)
                {
                    kids.Add(PromoShelf(promos[j], targets[j..end]));
                    return 1UL << g;
                }
                j = end;
            }
            return 0;
        }

        /// <summary>A Nearby / Recommended shelf: measured PagedShelf, headerGap 8, ≤ 16 tiles. "Near you" is Nearby's
        /// caption; every other non-grid kind is "Recommended for you" (§1.1).</summary>
        static Element ConcertShelf(in FeedSectionEdge head, ReadOnlySpan<int> slots)
            => PagedShelf.Create(ConcertItems(slots, 16), s_concertCard,
                   header: SectionCaption(Loc.Get(head.Kind == (byte)ConcertFeedSectionKind.Nearby ? Strings.Concerts.NearYou : Strings.Concerts.RecommendedForYou)),
                   headerGap: Spacing.S, measured: true, keyOf: s_itemKey, maxItems: 16)
               with { Key = "hub-shelf:" + head.Kind.ToString(CultureInfo.InvariantCulture) + ":" + Entities.Strings.Resolve(head.Key) };

        static Element PromoShelf(in FeedSectionEdge head, ReadOnlySpan<int> slots)
        {
            var items = new ShelfItem[Math.Min(slots.Length, 16)];
            var playlists = Entities.Current.Playlists;
            for (int i = 0; i < items.Length; i++) items[i] = new ShelfItem(slots[i], playlists.Version[slots[i]]);
            return PagedShelf.Create(items, s_promoCard, header: SectionCaption(Loc.Get(Strings.Concerts.PlaylistsForScene)),
                       headerGap: Spacing.S, measured: true, keyOf: s_itemKey, maxItems: 16)
                   with { Key = "hub-promos:" + head.Kind.ToString(CultureInfo.InvariantCulture) + ":" + Entities.Strings.Resolve(head.Key) };
        }

        static readonly Func<ShelfItem, int, float, Element> s_promoCard = static (item, _, w) => PromoCard(item.Slot, w);

        /// <summary>The playlist promo (§0 #12): cover + a 2-line title + its source, NO play FAB; the one concert-surface
        /// drag source, because it stands for a real playlist.</summary>
        static Element PromoCard(int slot, float cardW)
        {
            var pl = new Playlist(slot);
            var s = Entities.Strings;
            string title = s.Resolve(pl.TitleId), uri = pl.Uri.Text;
            string source = pl.Owner.IsValid ? s.Resolve(pl.Owner.NameId) : s.Resolve(pl.DescriptionId);
            string? cover = Controls.ArtUrl(pl.ImageId);
            float inner = MathF.Max(48f, cardW - 2f * Spacing.S);
            var target = pl.Uri;
            return new BoxEl
            {
                Key = "promo:" + slot.ToString(CultureInfo.InvariantCulture),
                Direction = 1, Gap = Spacing.S, Grow = 1f, ClipToBounds = true,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M), Corners = Radii.CardAll,
                Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand,
                OnClick = () => Shell.GoTo(Shell.For(target, title)),
                Draggable = Drag.Source(() => new DragPayload(DragKind.Playlist, uri, uri, title, new EntityRef(EntityKind.Playlist, slot), ArtUrl: cover)),
                Children =
                [
                    Controls.Artwork(cover, inner, inner, Radii.Control, decodePx: 256),
                    Design.Type.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                    Design.Type.TrackMeta(source) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }

        /// <summary>"All events": the LazyGrid over the page scroll (minCol 240, gap 12, rowExtra 86, overscan 3). An append
        /// grows the COUNT read through the delegate, never the realized tree (§9 #8); keyed per subject.</summary>
        Element EventsGrid(int feed) => new BoxEl
        {
            Key = "hub-grid", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                SectionCaption(Loc.Get(Strings.Concerts.AllEvents)),
                Embed.Comp(_gridFactory) with { Key = "hub-grid-lazy:" + feed.ToString(CultureInfo.InvariantCulture) },
            ],
        };

        /// <summary>The AllEvents run of the subject's list (the wire ships it last, and a merge appends new sections at
        /// the end, so it is the list's tail from its first row). Runs as the count memo's compute, so it derives the
        /// SUBJECT from the same signals Render does (the scope, the saved place, the filter tuple) instead of reading the
        /// <c>_feedSlot</c> field: a memo is pulled by whichever grid render needs it, and the outgoing subject's grid
        /// may pull it before this page's own render has moved the field — deriving keeps the incoming grid's first count
        /// its own. The <c>_gridStart</c> side effect stays: the grid pulls the memo first in its pass, so GridCell reads
        /// the run start of the same payload.</summary>
        int GridCount()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Places.Changed.Value;
            _ = _locationTick.Value;
            int placeSlot = scope.SavedPlace;
            int feed = placeSlot > 0 ? FeedSlot(placeSlot, _radius.Value, _when.Value, _selected.Value) : Table.None;
            var edge = scope.Edges.FeedSection;
            _ = edge.Changed.Value;
            var payload = edge.Payload(feed);
            _gridStart = -1;
            for (int i = 0; i < payload.Length; i++)
                if (payload[i].Kind == (byte)ConcertFeedSectionKind.AllEvents) { _gridStart = i; break; }
            return _gridStart < 0 ? 0 : payload.Length - _gridStart;
        }

        Element GridCell(int index, float width)
        {
            var targets = Entities.Current.Edges.FeedSection.Targets(_feedSlot);
            int at = _gridStart + index;
            if (_gridStart < 0 || (uint)at >= (uint)targets.Length) return new BoxEl();   // an append reconcile's skew
            int slot = targets[at];
            return Tile(TileOf(new Concert(slot)), () => GoToConcert(slot));
        }

        /// <summary>The pending silhouette (§9 #4, #6): four shelf tiles, then six grid tiles in a PLAIN AutoGrid — never the
        /// LazyGrid, which must not mount inside a shimmer derivation.</summary>
        static Element FeedSeed()
        {
            var shelf = new TileData[4];
            for (int i = 0; i < shelf.Length; i++) shelf[i] = SeedTile("seed:n:" + i.ToString(CultureInfo.InvariantCulture));
            var cards = new Element[6];
            for (int i = 0; i < cards.Length; i++) cards[i] = Tile(SeedTile("seed:a:" + i.ToString(CultureInfo.InvariantCulture)), null);
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.XL, MinWidth = 0f,
                Children =
                [
                    PagedShelf.Create(shelf, static (d, _, _) => Tile(d, null), header: SectionCaption(Loc.Get(Strings.Concerts.NearYou)),
                        headerGap: Spacing.S, measured: true, keyOf: static (d, _) => d.Key),
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.S,
                        Children = [SectionCaption(Loc.Get(Strings.Concerts.AllEvents)), AutoGrid(ConcertLayout.GridMinColumn, ConcertLayout.GridColumnGap, float.NaN, cards)],
                    },
                ],
            };
        }
    }

    // ══ 6. THE FILTER BAR (W6-W9; §0 #3, #4) ═════════════════════════════════════════════════════════════════════════

    /// <summary>The pinned filter card (0.2.9 <c>ConcertFilterBar</c>): the "FILTER BY" caption + the ticker, then ONE
    /// horizontally scrolling row [where ▾] │ [when] │ [All · top 3 · +N]. It owns NO queries — every edit writes one of the
    /// page's signals, and the page's effect turns the new tuple into a new subject. Pinned under the masthead at 84 (§0 #2).</summary>
    sealed class FilterBar : Component
    {
        readonly BarInputs _io;
        readonly Signal<bool> _expanded = new(false);
        readonly Func<CountTicker> _ticker;
        readonly Action _openWhen, _openWhere, _thisWeekend, _clearGenres, _expand, _collapse, _searchCities;
        readonly Action<NodeHandle> _captureWhen;
        readonly Func<NodeHandle> _whenAnchor;
        readonly Func<Element> _whenContent, _whereContent;
        NodeHandle _whenNode;
        IOverlayService? _overlay;
        OverlayHandle? _whenHandle, _whereHandle;

        public FilterBar(BarInputs io)
        {
            _io = io;
            _ticker = () => new CountTicker(io.Count);
            _openWhen = OpenWhen;
            _openWhere = OpenWhere;
            _captureWhen = h => _whenNode = h;
            _whenAnchor = () => _whenNode;
            _thisWeekend = () => io.When.Value = new ConcertWhen(ConcertWhenKind.ThisWeekend, Loc.Get(Strings.Concerts.Filter.ThisWeekend),
                ConcertHub.PresetRange(ConcertWhenKind.ThisWeekend, DateTimeOffset.Now));
            _clearGenres = () => { if (io.Selected.Peek().Count > 0) io.Selected.Value = Array.Empty<string>(); };
            _expand = () => _expanded.Value = true;
            _collapse = () => _expanded.Value = false;
            _searchCities = () => { _whereHandle?.Close(); io.OpenPicker(); };
            Action<ConcertWhen> pickWhen = w => { _whenHandle?.Close(); io.When.Value = w; };
            Action<int> pickRadius = km => io.Radius.SetIfChanged(km);
            _whenContent = () => Embed.Comp(() => new WhenFlyout(pickWhen));
            // "Use my location" hands off to the picker, whose own button runs the consent flow (0.2.9's TogglePicker).
            _whereContent = () => Embed.Comp(() => new WhereFlyout(io.Radius, pickRadius, _searchCities, _searchCities));
        }

        public override Element Render()
        {
            _overlay = UseContext(Overlay.Service);
            var place = _io.Place.Value;
            bool? inferred = _io.Inferred.Value;
            int radius = _io.Radius.Value;
            var when = _io.When.Value;
            var selected = _io.Selected.Value;
            var all = _io.Concepts.Value;
            bool expanded = _expanded.Value;

            Element caption = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = Spacing.S,
                Children = [SectionCaption(Loc.Get(Strings.Concerts.Filter.FilterBy)), new BoxEl { Grow = 1f }, Embed.Comp(_ticker) with { Key = "concert-count" }],
            };
            // decision (ch 17 §8, parity 71): the pill states an INFERRED place as "Near X (approximate)"
            Element where = WherePill(ConcertHub.WhereLabel(place, inferred, radius), _openWhere) with { OnRealized = _io.CaptureWhere };
            Element row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Children = [where, BarDivider(), WhenArea(when), BarDivider(), Genres(all, selected, expanded)],
            };
            Element scroller = ScrollView(new BoxEl { Direction = 0, Children = [row] }, horizontal: true) with
            {
                Grow = 0f, Height = 44f, AutoEdgeFade = true, AutoEdgeFadeBand = 36f, SuppressScrollBar = true, ScrollKey = "concert-hub-filter",
            };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                Corners = Radii.CardAll, Fill = Tok.FillLayerDefault, BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
                Children = [caption, scroller],
            }.Sticky(BrowseMastheadMetrics.Reserve);
        }

        /// <summary>Rest: [📅 Dates ▾] + the "This weekend" chip. Active: the SAME "when-pill" node fused (§0 #3).</summary>
        Element WhenArea(ConcertWhen when)
        {
            Element[] kids = when.Kind == ConcertWhenKind.Any || when.Range is null
                ?
                [
                    RestDatePill(_openWhen) with { OnRealized = _captureWhen },
                    FilterToken(Loc.Get(Strings.Concerts.Filter.ThisWeekend), false, _thisWeekend) with { Key = "when-chip", Animate = ChipExit },
                ]
                : [FusedPill("when-pill", SegmentedPillStyle.Accent, when.Name, ConcertHub.WhenLabel(when.Range, CultureInfo.CurrentCulture), _openWhen) with { OnRealized = _captureWhen }];
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = kids };
        }

        Element Genres(IReadOnlyList<ConcertConcept> all, IReadOnlyList<string> selected, bool expanded)
        {
            var shown = ConcertHub.TopConcepts(all, selected, expanded);
            var kids = new List<Element>(shown.Count + 2) { FilterToken(Loc.Get(Strings.Concerts.AllGenres), selected.Count == 0, _clearGenres) };
            for (int i = 0; i < shown.Count; i++)
            {
                string uri = shown[i].Uri;
                bool active = false;
                for (int k = 0; k < selected.Count && !active; k++) active = string.Equals(selected[k], uri, StringComparison.Ordinal);
                kids.Add(FilterToken(ConcertHub.ConceptLabel(shown[i].Name, Loc.Get(Strings.Concerts.GenreFallback)), active,
                    () => _io.Selected.Value = ConcertHub.ToggleConcept(_io.Selected.Peek(), uri)));
            }
            if (all.Count > shown.Count) kids.Add(MoreToken(Strings.Concerts.Filter.MoreGenres(all.Count - shown.Count), false, _expand));
            else if (expanded && all.Count > ConcertHub.TopConceptCount) kids.Add(MoreToken(Loc.Get(Strings.Concerts.Filter.ShowLess), true, _collapse));
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = kids.ToArray() };
        }

        void OpenWhen()
        {
            if (Controls.IsNullOverlay(_overlay)) return;
            if (_whenHandle is { IsOpen: true } open) { open.Close(); return; }
            _whenHandle = _overlay.Open(_whenAnchor, _whenContent, FlyoutPlacement.BottomEdgeAlignedLeft, FlyoutOptions);
            _whenHandle.ClosedAction = () => _whenHandle = null;
        }

        void OpenWhere()
        {
            if (Controls.IsNullOverlay(_overlay)) return;
            if (_whereHandle is { IsOpen: true } open) { open.Close(); return; }
            _whereHandle = _overlay.Open(_io.WhereAnchor, _whereContent, FlyoutPlacement.BottomEdgeAlignedLeft, FlyoutOptions);
            _whereHandle.ClosedAction = () => _whereHandle = null;
        }
    }

    // ══ 7. THE ARTIST SCHEDULE (W13-W18; §0 #7-#10) ══════════════════════════════════════════════════════════════════

    /// <summary>Page-owned state the boards and the shy pill share (§1.4 traps): the expansion sets survive a board's keyed
    /// remount; the tracker is read imperatively through <c>SceneStore.AbsoluteRect</c>, never through an ancestor.</summary>
    sealed class BoardState
    {
        public readonly HashSet<string> Months = new(StringComparer.Ordinal), Runs = new(StringComparer.Ordinal);
        public readonly Signal<int> Revision = new(0);
        public NodeHandle Viewport, PanelHeader;
        public string Label = "";
        public void Bump() => Revision.Value = Revision.Peek() + 1;
    }

    /// <summary>The artist's tour dashboard (0.2.9 <c>ArtistSchedulePage</c>): the split hero owns the next show; ONE month
    /// at a time renders in the tour-dates card, remembered by its <c>yyyy-MM</c> key; the shy month pill floats over the
    /// viewport. Demands the schedule edge through Fetch while Unknown.</summary>
    sealed class SchedulePage : Component
    {
        readonly Signal<string?> _month = new(null);
        readonly Signal<float> _scroll = new(0f);
        readonly Signal<bool> _under = new(false);
        readonly BoardState _board = new();
        readonly PlacePicker _picker = new();

        Scope? _scope;
        EntityUri _subject;
        Artist _artist;
        string _routeKey = "", _routeName = "";
        bool _pending, _failed, _wasWide, _wideInit, _locationAsked;
        Element? _body;
        IOverlayService? _overlay;
        NodeHandle _anchor;

        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Func<float, Element> _build, _buildSeed;
        readonly Func<MonthBoard> _monthFactory;
        readonly Func<ShyMonthPill> _pillFactory;
        readonly Action _demand, _retry, _openPicker, _openSpotlight;
        readonly Action<bool> _onEdge;
        readonly Action<NodeHandle> _captureAnchor, _captureViewport;
        readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) _scrollWatch;
        int _spotlightSlot;

        public SchedulePage()
        {
            _pendingFn = () => _pending;
            _failedFn = () => _failed;
            _contentFn = () => _body ?? new BoxEl();
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _retry);
            _demand = Demand;
            _retry = () => { if (_artist.IsValid) Entities.EnsureEdge(FetchEdge.ArtistConcerts, _artist.Slot); };
            _openPicker = () => _picker.Toggle(_overlay, () => _anchor);
            _openSpotlight = () => { if (_spotlightSlot > 0) GoToConcert(_spotlightSlot); };
            _onEdge = v => _under.SetIfChanged(v);
            _captureAnchor = h => _anchor = h;
            _captureViewport = h => _board.Viewport = h;
            _build = w => Dashboard(w, seed: false);
            _buildSeed = w => Dashboard(w, seed: true);
            _shimmerFn = () => Responsive.Of(_buildSeed, fallback: 900f);
            _monthFactory = () => new MonthBoard(_board);
            _pillFactory = () => new ShyMonthPill(_board, _scroll);
            _scrollWatch = (static g => (long)(g.OffsetY / 8f), g => _scroll.Value = g.OffsetY);
            // A saved place moves the near-you bits: the schedule is re-asked against it.
            _picker.Saved = () => { if (_artist.IsValid) Entities.RefreshEdge(FetchEdge.ArtistConcerts, _artist.Slot); };
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Artists.Changed.Value;
            _ = scope.Concerts.Changed.Value;
            _ = scope.Places.Changed.Value;
            _ = e.ArtistConcerts.Changed.Value;
            _ = _month.Value;
            _overlay = UseContext(Overlay.Service);
            _routeKey = p.RouteKey;
            _routeName = Entities.Strings.Resolve(p.Arg);
            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                _artist = p.Subject.IsValid ? Entities.Artist(p.Subject) : default;
                _locationAsked = false;
            }
            var a = _artist;
            UseEffect(_demand, DepKey.From(a.Slot, (int)epoch));

            var readiness = a.IsValid ? e.ArtistConcerts.Readiness(a.Slot) : EdgeState.Failed;
            _pending = readiness == EdgeState.Unknown;
            _failed = readiness == EdgeState.Failed;
            _body = _pending || _failed ? null
                : e.ArtistConcerts.Count(a.Slot) == 0
                    ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Schedule.NoUpcoming),
                        subtitle: Loc.Get(Strings.Concerts.Schedule.NoUpcomingSubtitle))
                    : Responsive.Of(_build, fallback: 900f);

            Element region = Region(_pendingFn, _failedFn, _contentFn, _shimmerFn, _failedPanelFn, "artist-schedule:" + p.RouteKey);
            Element scroll = SubPageScroll("artist-schedule:" + p.RouteKey, region, _under.Value, _onEdge) with
            {
                OnScrollGeometryChanged = _scrollWatch, OnRealized = _captureViewport,
            };
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ZStack = true,
                Children = [scroll, Embed.Comp(_pillFactory) with { Key = "shy-month:" + p.RouteKey }],
            };
        }

        void Demand()
        {
            var a = _artist;
            if (!a.IsValid) return;
            var scope = Entities.Current;
            if (scope.Edges.ArtistConcerts.State(a.Slot) == EdgeState.Unknown && !scope.Edges.ArtistConcerts.IsFailed(a.Slot))
                Entities.EnsureEdge(FetchEdge.ArtistConcerts, a.Slot);
            if (!a.Knows(ArtistFields.Identity)) Entities.Ensure(a, ArtistFields.Identity);
            if (_locationAsked) return;
            _locationAsked = true;
            // The label reads the saved place, else the artist page's own location read (a second, independent read).
            if (scope.SavedPlace <= 0) ConcertHost.Current.ResolveLocation(null);
            if (scope.ArtistPagePlace <= 0) ConcertHost.Current.ResolveArtistPageLocation(null);
        }

        /// <summary>The loaded (or seeded) dashboard at a measured width — the 760 / 720 hysteresis held in page fields.</summary>
        Element Dashboard(float width, bool seed)
        {
            bool wide = ConcertLayout.ScheduleWide(width, _wasWide, _wideInit);
            _wasWide = wide;
            _wideInit = true;
            var now = DateTimeOffset.FromUnixTimeSeconds(Store.ToUnix(Entities.Now));
            IReadOnlyList<ConcertShow> shows = seed ? SeedShows(now) : ConcertShows.From(Entities.Current.Edges.ArtistConcerts.Targets(_artist.Slot));
            var chronological = ConcertSchedules.Chronological(shows);
            var spotlight = ConcertScheduleShaping.Spotlight(chronological, now);
            var itinerary = ConcertScheduleShaping.BoardConcerts(chronological, spotlight);
            var groups = ConcertScheduleShaping.GroupByMonth(itinerary);
            var near = new List<ConcertShow>();
            for (int i = 0; i < chronological.Count; i++) if (chronological[i].IsNearUser) near.Add(chronological[i]);
            var nearby = ConcertScheduleShaping.GuardedNearSet(itinerary, near);
            string name = seed ? " " : ArtistName();

            var sections = new List<Element>(2) { Hero(name, spotlight, ConcertScheduleShaping.Stats(chronological), wide, now, seed) };
            int selected = ConcertScheduleShaping.ResolveMonthIndex(groups, _month.Peek(), spotlight);
            if (selected >= 0)
            {
                var group = groups[selected];
                if (!seed)
                    _board.Label = new DateTime(group.Year, group.Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture)
                                 + " · " + Strings.Concerts.Schedule.ShowCount(group.ShowCount);
                sections.Add(TourDates(groups, selected, nearby, name, wide, seed));
            }
            else
            {
                if (!seed) { _board.Label = ""; _board.PanelHeader = NodeHandle.Null; }
                // W18: the hero rendered but no month remains (the one show on sale IS the spotlight).
                sections.Add(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(Spacing.XL, Spacing.XXL, Spacing.XL, Spacing.XXL),
                    Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Children =
                    [
                        Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Schedule.NoMoreDates),
                            subtitle: Loc.Get(Strings.Concerts.Schedule.NoMoreDatesSubtitle),
                            actionLabel: Loc.Get(Strings.Concerts.Location.Set), onAction: _openPicker),
                    ],
                });
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XL, Children = sections.ToArray() };
        }

        string ArtistName()
        {
            string name = _artist.IsValid ? _artist.Name : "";
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return _routeName.Length > 0 ? _routeName : Loc.Get(Strings.Concerts.Detail.ArtistFallback);
        }

        string LocationLabel()
        {
            var scope = Entities.Current;
            if (ConcertPlaces.From(scope.SavedPlace)?.Name is { Length: > 0 } saved) return saved;
            if (ConcertPlaces.From(scope.ArtistPagePlace)?.Name is { Length: > 0 } page) return page;
            return Loc.Get(Strings.Concerts.Location.Set);
        }

        /// <summary>W13: "On tour" · the name (≤ 2 lines) · the stats line; with a spotlight a hairline, the next-show row
        /// and "View event" beside a 220-wide location button (stacked when narrow). Without one (W13b) the location button
        /// stands alone at full width and the media pane is untinted.</summary>
        Element Hero(string name, ConcertShow? spotlight, in ConcertTourStats stats, bool wide, DateTimeOffset now, bool seed)
        {
            var blocks = new List<Element>(6)
            {
                SectionCaption(Loc.Get(Strings.Concerts.Schedule.OnTour)),
                Design.Type.PageHero(name) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                Body(ConcertScheduleShaping.StatsLine(stats)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            };
            uint accent = 0;
            _spotlightSlot = 0;
            if (spotlight is { } next)
            {
                if (!seed)
                {
                    _spotlightSlot = Entities.Current.Concerts.Slot(next.Uri.AsSpan());
                    accent = new Concert(_spotlightSlot).Accent;
                }
                var text = ConcertScheduleShaping.TileText(next, name);
                blocks.Add(new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault });
                blocks.Add(new BoxEl
                {
                    Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        DateBlock(next.Date),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                            Children =
                            [
                                SectionCaption(Strings.Concerts.Schedule.NextShow(ConcertScheduleShaping.RelativeTime(next.Date, now))),
                                Design.Type.TrackTitle(text.Primary) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                Design.Type.TrackMeta(text.Secondary) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ],
                        },
                    ],
                });
            }
            BoxEl location = LocationButton(seed ? " " : LocationLabel(), _openPicker) with { OnRealized = _captureAnchor };
            blocks.Add(spotlight is null
                ? location
                : wide
                    ? new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        Children =
                        [
                            Button.Accent(Loc.Get(Strings.Concerts.Schedule.ViewEvent), _openSpotlight) with { Shrink = 0f },
                            new BoxEl { Width = 220f, MinWidth = 0f, Children = [location] },
                        ],
                    }
                    : new BoxEl { Direction = 1, Gap = Spacing.S, Children = [Button.Accent(Loc.Get(Strings.Concerts.Schedule.ViewEvent), _openSpotlight), location] });
            var copy = new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.M, Children = blocks.ToArray() };
            return SplitHero(seed ? null : Controls.ArtUrl(_artist.HeaderId), accent, copy, wide);
        }

        /// <summary>The tour-dates card: "Tour dates" 28/36/600, the month tabs in a 48-high edge-faded scroller, then the
        /// selected month's board — keyed on its composed signature (props freeze at mount, §9 #9).</summary>
        Element TourDates(IReadOnlyList<ConcertMonthGroup> groups, int selected, HashSet<string> nearby, string name, bool wide, bool seed)
        {
            var labels = ConcertScheduleShaping.MonthTabLabels(groups, CultureInfo.CurrentCulture);
            Element tabs = ScrollView(new BoxEl
            {
                Direction = 0,
                Children = [SelectorBar.Create(labels, new Signal<int>(selected), onChange: i => _month.Value = groups[i].Key)],
            }, horizontal: true) with
            {
                Height = 48f, Grow = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = 36f, SuppressScrollBar = true,
                ScrollKey = "artist-schedule-months:" + _routeKey,
            };
            var group = groups[selected];
            string signature = group.Key + ":" + group.Runs.Count.ToString(CultureInfo.InvariantCulture) + ":"
                + group.ShowCount.ToString(CultureInfo.InvariantCulture) + ":" + (group.Runs.Count > 0 ? group.Runs[0].Uri : "") + (wide ? ":w" : ":n");
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, ClipToBounds = true, Corners = Radii.CardAll,
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, Gap = Spacing.S, Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.S),
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Concerts.Schedule.TourDates)) { Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                            tabs,
                        ],
                    },
                    Embed.Comp(new MonthProps(group, nearby, name, wide, seed), _monthFactory) with { Key = "dashboard-month:" + signature },
                ],
            };
        }

        /// <summary>The shimmer seed (§9 #4): eight dates with a 2-night run at +33/+34.</summary>
        static ConcertShow[] SeedShows(DateTimeOffset now)
        {
            int[] offsets = [5, 12, 26, 33, 34, 48, 62, 76];
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, 20, 0, 0, now.Offset);
            var shows = new ConcertShow[offsets.Length];
            for (int i = 0; i < shows.Length; i++)
                shows[i] = new ConcertShow("seed:" + i.ToString(CultureInfo.InvariantCulture), "",
                    i is 3 or 4 ? "Residency venue placeholder" : "Venue name placeholder", "City placeholder", start.AddDays(offsets[i]));
            return shows;
        }
    }

    sealed record MonthProps(ConcertMonthGroup Group, HashSet<string> Near, string ArtistName, bool Wide, bool Seed);

    /// <summary>The selected month (0.2.9 <c>MonthBoard</c>): header (MinH 58, the month 20/28/600 + "N shows"), a hairline,
    /// one column — or at wide two BALANCED chronological columns split by a 1-DIP divider — and, over six tiles, a
    /// divider + "Show all {shows}" (§0 #10). A multi-night run is ONE tile that expands in place.</summary>
    sealed class MonthBoard(BoardState state) : Component
    {
        MonthProps _p = null!;
        readonly Action<NodeHandle> _header = h => state.PanelHeader = h;

        public override Element Render()
        {
            _p = UseProps<MonthProps>();
            _ = state.Revision.Value;
            var runs = _p.Group.Runs;
            bool capped = !state.Months.Contains(_p.Group.Key) && _p.Group.OverflowsCap;
            int visible = capped ? ConcertScheduleShaping.TileCap : runs.Count;
            Element body;
            if (_p.Wide && visible > 1)
            {
                var split = ConcertScheduleShaping.BalancedColumns(runs, visible);
                body = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f,
                    Children =
                    [
                        Column(split.Left, split.Left.Count) with { Grow = 1f, Basis = 0f },
                        new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                        Column(split.Right, split.Right.Count) with { Grow = 1f, Basis = 0f },
                    ],
                };
            }
            else body = Column(runs, visible);

            var culture = CultureInfo.CurrentCulture;
            var kids = new List<Element>(5)
            {
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, MinHeight = 58f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M), OnRealized = _p.Seed ? null : _header,
                    Children =
                    [
                        new TextEl(new DateTime(_p.Group.Year, _p.Group.Month, 1).ToString("MMMM yyyy", culture))
                        { Size = 20f, LineHeight = 28f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Body(Strings.Concerts.Schedule.ShowCount(_p.Group.ShowCount)) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f },
                    ],
                },
                Divider(), body,
            };
            if (capped)
            {
                kids.Add(Divider());
                kids.Add(new BoxEl
                {
                    Direction = 0, MinWidth = 0f, MinHeight = 44f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.XS,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = () => { if (state.Months.Add(_p.Group.Key)) state.Bump(); },
                    Children =
                    [
                        // "Show all {SHOWS}" while the cap counts TILES (parity 63).
                        BodyStrong(Strings.Concerts.Schedule.ShowAll(_p.Group.ShowCount)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                        Icon(Icons.ChevronDown, 14f, Tok.AccentTextPrimary) with { Shrink = 0f },
                    ],
                }.Interactive(Interaction.Subtle));
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
        }

        BoxEl Column(IReadOnlyList<ConcertBoardRun> runs, int count)
        {
            var kids = new List<Element>(Math.Max(0, count * 2 - 1));
            for (int i = 0; i < count; i++)
            {
                if (i > 0) kids.Add(Divider());
                var run = runs[i];
                bool near = false;
                for (int k = 0; k < run.NightCount && !near; k++) near = _p.Near.Contains(run.Nights[k].Uri);
                if (!run.IsMultiNight) kids.Add(NightTile(run.First, near));
                else if (state.Runs.Contains(run.Uri))
                {
                    var nights = new List<Element>(run.NightCount * 2 - 1);
                    for (int k = 0; k < run.NightCount; k++)
                    {
                        if (k > 0) nights.Add(Divider());
                        nights.Add(NightTile(run.Nights[k], near));
                    }
                    kids.Add(new BoxEl { Key = "run:" + run.Uri, Direction = 1, MinWidth = 0f, Children = nights.ToArray() });
                }
                else kids.Add(RunTile(run, near));
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element NightTile(ConcertShow c, bool near)
        {
            var text = ConcertScheduleShaping.TileText(c, _p.ArtistName);
            var secondary = new List<Element>(2);
            if (near) secondary.Add(Icon(Icons.MapPin, 12f, Tok.AccentTextPrimary) with { Shrink = 0f });
            if (text.Secondary.Length > 0) secondary.Add(Meta(text.Secondary));
            string uri = c.Uri;
            bool seed = _p.Seed;
            return Tile(uri, near, DayColumn(c.Date.Day.ToString(CultureInfo.CurrentCulture), c.Date.ToString("ddd", CultureInfo.CurrentCulture), 44f),
                text.Primary, secondary, Icons.ChevronRight,
                () => { if (!seed) GoToConcert(Entities.Current.Concerts.Slot(uri.AsSpan())); });
        }

        /// <summary>The run tile's secondary row, in BUILT order (W15): a venue-primary run reads [pin] City [N nights]; a
        /// city-primary run reads [pin] [N nights] with …; the pin only inside the guarded near set.</summary>
        Element RunTile(ConcertBoardRun run, bool near)
        {
            var c = run.First;
            var text = ConcertScheduleShaping.TileText(c, _p.ArtistName);
            var meta = new List<Element>(4);
            if (near) meta.Add(Icon(Icons.MapPin, 12f, Tok.AccentTextPrimary) with { Shrink = 0f });
            meta.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(Spacing.S, 1f, Spacing.S, 1f),
                Corners = Radii.FullAll, Fill = Tok.AccentSubtle,
                Children = [Caption(ConcertCopy.Current.Nights(run.NightCount)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 }],
            });
            if (text.CityIsPrimary)
            {
                if (ConcertScheduleShaping.SupportActs(c, _p.ArtistName) is { Length: > 0 } support) meta.Add(Meta(support));
            }
            else if (c.City.Length > 0) meta.Insert(near ? 1 : 0, Meta(c.City));
            var culture = CultureInfo.CurrentCulture;
            string uri = run.Uri;
            return Tile("run:" + uri, near,
                DayColumn(c.Date.Day.ToString(culture) + "–" + run.Last.Date.Day.ToString(culture),
                          c.Date.ToString("ddd", culture) + "–" + run.Last.Date.ToString("ddd", culture), 60f),
                text.Primary, meta, Icons.ChevronDown,
                () => { if (state.Runs.Add(uri)) state.Bump(); });
        }

        /// <summary>A board tile: MinH 68, pad (12,8,12,8), gap 12 — the near rail (always laid out, transparent when not
        /// near, so widths never differ), the day column, primary/secondary lines, the trailing 16-DIP glyph.</summary>
        static Element Tile(string key, bool near, Element day, string primary, List<Element> secondary, string glyph, Action onClick) => new BoxEl
        {
            Key = key, Direction = 0, MinHeight = 68f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl { Width = 3f, Height = 32f, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = near ? Tok.AccentDefault : ColorF.Transparent },
                day,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center,
                    Children =
                    [
                        BodyStrong(primary) with { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = secondary.ToArray() },
                    ],
                },
                Icon(glyph, 16f, Tok.TextSecondary) with { Shrink = 0f },
            ],
        }.Interactive(Interaction.Subtle);

        /// <summary>The growing text run: Grow 1 + Shrink 1 + MinWidth 0 over its MEASURED basis (never Basis 0, §9 #7).</summary>
        static TextEl Meta(string text)
            => Body(text) with { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

        static Element DayColumn(string day, string weekday, float width) => new BoxEl
        {
            Width = width, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 1f,
            Children =
            [
                BodyStrong(day) with { Color = Tok.TextPrimary, MaxLines = 1 },
                Design.Type.Eyebrow(weekday) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        };

        static Element Divider() => new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault };
    }

    /// <summary>The shy month pill (0.2.9 <c>ShyMonthPill</c>, §0 #9): a 32-DIP acrylic capsule, top-centre 12 down, shown only
    /// while scrolling AND once the board header has crossed the viewport top (≤ +4 DIP), gone at once above it, lingering
    /// 1 s past the last step on the ANIM clock. Never hit-testable: the scroller beneath always wins the wheel.</summary>
    sealed class ShyMonthPill(BoardState state, IReadSignal<float> scroll) : Component
    {
        static readonly LayoutTransition s_presence = new(
            TransitionChannels.Opacity, TransitionDynamics.Tween(200f, Easing.SmoothOut),
            Enter: new EnterExit(Dy: -8f, Opacity: 0f, Active: true), Exit: new EnterExit(Dy: -6f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(260f, Easing.SmoothOut));

        readonly Signal<bool> _active = new(false);
        readonly Signal<string> _label = new("");
        readonly Signal<int> _arm = new(0);
        float _last = float.NaN;
        Func<LingerClock>? _clock;

        public override Element Render()
        {
            var clock = _clock ??= () => new LingerClock(() => _active.SetIfChanged(false));
            UseSignalEffect(() =>
            {
                float offset = scroll.Value;
                Reactive.Untrack(() => Step(offset));
            });
            int arm = _arm.Value;
            bool show = _active.Value;
            string text = _label.Value;
            Element pill = Flow.Show(() => _active.Value, new BoxEl
            {
                Animate = s_presence, TransformOriginX = 0.5f, TransformOriginY = 0f, HitTestVisible = false,
                AlignSelf = FlexAlign.Center, Height = 32f, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = Radii.FullAll,
                Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
                BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
                Children = [SectionCaption(text)],
            });
            return new BoxEl
            {
                HitTestVisible = false, HitTestPassThrough = true, Grow = 1f, Direction = 1,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Padding = new Edges4(0f, Spacing.M, 0f, 0f),
                Children = show ? [pill, Embed.Comp(clock) with { Key = "shy-linger:" + arm.ToString(CultureInfo.InvariantCulture) }] : [pill],
            };
        }

        void Step(float offset)
        {
            float previous = _last;
            _last = offset;
            if (float.IsNaN(previous) || MathF.Abs(offset - previous) < 0.5f) return;   // the baseline, and sub-half-DIP noise
            var scene = Context.Scene;
            var viewport = state.Viewport;
            var header = state.PanelHeader;
            bool crossed = scene is not null && !viewport.IsNull && scene.IsLive(viewport) && !header.IsNull && scene.IsLive(header)
                && state.Label.Length > 0 && scene.AbsoluteRect(header).Y <= scene.AbsoluteRect(viewport).Y + 4f;
            if (!crossed) { _active.SetIfChanged(false); return; }             // above the boards: gone at once (parity 70)
            _label.SetIfChanged(state.Label);
            _active.SetIfChanged(true);
            _arm.Value = _arm.Peek() + 1;                                       // re-arm the 1 s linger
        }
    }

    /// <summary>An invisible, re-armable 1 s countdown on the engine's ANIM clock: it seeds a duration track on its own node
    /// and fires once when the track settles. A new key re-arms it; it costs a frame wake only while mounted.</summary>
    sealed class LingerClock(Action elapsed) : Component
    {
        NodeHandle _self;
        bool _seeded, _fired;

        public override Element Render()
        {
            var tick = UseContextSignal(FrameClock.Tick);
            UseSignalEffect(() =>
            {
                _ = tick.Value;
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (_fired || anim is null || scene is null || _self.IsNull || !scene.IsLive(_self)) return;
                if (!_seeded) { _seeded = true; anim.Animate(_self, AnimChannel.Opacity, 1f, 1f, 1000f, Easing.Linear); return; }
                if (!anim.HasTracks(_self)) { _fired = true; elapsed(); }
            });
            return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f, OnRealized = h => _self = h };
        }
    }

    // ══ 8. THE CONCERT DETAIL (W19-W22; §0 #13, #14) ══════════════════════════════════════════════════════════════════

    readonly record struct OfferView(string Provider, string? Availability, string? Price, string? Sale, string? Url);
    readonly record struct ActView(string Name, string? Image, int ArtistSlot, bool Navigable);

    /// <summary>Everything the detail paints, as VALUES — read off the handle, or seeded for the shimmer (§9 #4: facts + two
    /// offers + three lineup rows, the exact silhouette of the loaded page).</summary>
    sealed record DetailView(string Title, string DateLine, string? Doors, string Place, string? Ages, string? Status,
                             OfferView[] Offers, ActView[] Lineup, int[] Related, string? ImageUrl, uint Accent)
    {
        public static DetailView Of(Concert c, string routeTitle)
        {
            var s = Entities.Strings;
            var culture = CultureInfo.CurrentCulture;
            string title = s.Resolve(c.TitleId), venue = s.Resolve(c.VenueId);
            title = !string.IsNullOrWhiteSpace(title) ? title : venue.Length > 0 ? venue
                  : routeTitle.Length > 0 ? routeTitle : Loc.Get(Strings.Concerts.Detail.Concert);
            string location = ConcertDetailInfo.LocationLine(s.Resolve(c.CityId), s.Resolve(c.RegionId), s.Resolve(c.CountryId));
            string ages = s.Resolve(c.AgeRestrictionId);

            var offerRows = c.Offers;
            var offers = new OfferView[offerRows.Length];
            for (int i = 0; i < offers.Length; i++)
            {
                ref readonly var o = ref offerRows[i];
                string provider = s.Resolve(o.Provider);
                offers[i] = new OfferView(provider.Length > 0 ? provider : Loc.Get(Strings.Concerts.Detail.TicketProvider),
                    ConcertOffers.AvailabilityLabel((ConcertOfferAvailability)o.Availability), ConcertOffers.PriceLabel(in o),
                    ConcertOffers.SaleWindowLabel(in o, culture), ConcertOffers.ValidTicketUrl(s.Resolve(o.Url)));
            }

            // The hero photo, in strict order: the first act with a banner, the show's own image, the first act's avatar.
            // The tint the other way round: the show's accent, then that banner act's (W19).
            var acts = c.Lineup;
            var slots = c.LineupSlots;
            var lineup = new ActView[acts.Length];
            string? banner = null, avatar = null;
            uint bannerAccent = 0;
            for (int i = 0; i < acts.Length; i++)
            {
                ref readonly var act = ref acts[i];
                int slot = i < slots.Length ? slots[i] : Table.None;
                var artist = slot > Table.None ? new Artist(slot) : default;
                string name = s.Resolve(act.BillingName);
                if (name.Length == 0 && artist.IsValid) name = artist.Name;
                string? image = Controls.ArtUrl(act.Image) ?? (artist.IsValid ? Controls.ArtUrl(artist.ImageId) : null);
                lineup[i] = new ActView(name.Length > 0 ? name : Loc.Get(Strings.Concerts.Detail.ArtistFallback), image, slot,
                    (act.Flags & LineupEdge.Navigable) != 0 && artist.IsValid);
                if (banner is null && Controls.ArtUrl(act.HeaderImage) is { } header) { banner = header; bannerAccent = act.Accent; }
                avatar ??= image;
            }
            var related = c.RelatedSlots;
            return new DetailView(title, ConcertTime.DetailDateLine(c.LocalDate, culture),
                c.DoorsLocal is { } doors ? Strings.Concerts.Detail.DoorsOpen(doors.ToString("t", culture)) : null,
                venue.Length > 0 && location.Length > 0 ? venue + " · " + location : venue.Length > 0 ? venue : location,
                ages.Length > 0 ? Strings.Concerts.Detail.Ages(ages) : null,
                ConcertDetailInfo.DisplayStatus(s.Resolve(c.StatusTextId)),
                offers, lineup, related[..Math.Min(related.Length, 15)].ToArray(),
                banner ?? Controls.ArtUrl(c.ImageId) ?? avatar, c.Accent != 0 ? c.Accent : bannerAccent);
        }

        public static readonly DetailView Seed = new(" ", "Saturday, January 1, 2030 · 20:00", "Doors open 19:00",
            "Venue name placeholder · City placeholder", null, null,
            [new(" ", " ", " ", null, "https://seed.invalid/"), new(" ", null, null, null, null)],
            [new(" ", null, 0, true), new(" ", null, 0, true), new(" ", null, 0, false)], [], null, 0);
    }

    /// <summary>The concert page (0.2.9 <c>ConcertDetailPage</c>): the split hero carries the identity and the provider's
    /// local-clock facts; tickets ride a 320 rail at wide (920 enter / 860 leave) ONLY when offers exist — else one full
    /// column — and move inline after the hero when narrow; then the lineup card and the related shelf, each omitted
    /// when empty. Demands the whole row (<see cref="ConcertFields.All"/>) once per slot per scope.</summary>
    sealed class DetailPage : Component
    {
        static readonly Action s_goHub = static () => Shell.GoTo(HubRoute);
        static readonly Func<Element> s_never = static () => new BoxEl();
        static readonly Func<ShelfItem, int, float, Element> s_relatedCard = static (item, _, _) =>
        {
            if (item.Slot <= Table.None) return BrowseAllCard(s_goHub);
            int slot = item.Slot;
            return Tile(TileOf(new Concert(slot)), () => GoToConcert(slot));
        };

        readonly Signal<bool> _under = new(false);
        Scope? _scope;
        EntityUri _subject;
        Concert _concert;
        string _routeTitle = "";
        bool _pending, _wasWide, _wideInit;
        Element? _body;
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn;
        readonly Func<float, Element> _build, _buildSeed;
        readonly Action _demand;
        readonly Action<bool> _onEdge;

        public DetailPage()
        {
            _pendingFn = () => _pending;
            _failedFn = static () => false;
            _contentFn = () => _body ?? new BoxEl();
            _build = w => Compose(DetailView.Of(_concert, _routeTitle), Wide(w));
            _buildSeed = w => Compose(DetailView.Seed, Wide(w));
            _shimmerFn = () => Responsive.Of(_buildSeed, fallback: 1000f);
            _demand = () => { if (_concert.IsValid && !_concert.Knows(ConcertFields.All)) Entities.Ensure(_concert, ConcertFields.All); };
            _onEdge = v => _under.SetIfChanged(v);
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Concerts.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = e.ConcertOffers.Changed.Value;
            _ = e.ConcertLineup.Changed.Value;
            _ = e.ConcertRelated.Changed.Value;
            _routeTitle = Entities.Strings.Resolve(p.Arg);
            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                _concert = p.Subject.IsValid ? Entities.Concert(p.Subject) : default;
            }
            var c = _concert;
            UseEffect(_demand, DepKey.From(c.Slot, (int)epoch));

            // Settled = the whole row was asked and nothing is in flight: a group that never landed stays unknown (W22).
            bool all = c.IsValid && c.Knows(ConcertFields.All);
            bool settled = !c.IsValid || ((scope.Concerts.Asked[c.Slot] & (uint)ConcertFields.All) != 0 && scope.Concerts.Inflight[c.Slot] == 0);
            _pending = !all && !settled;
            _body = _pending ? null
                : !c.IsValid || !c.Knows(ConcertFields.Identity)
                    ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Detail.NotAvailable),
                        subtitle: Loc.Get(Strings.Concerts.Detail.NotAvailableSubtitle))
                    : Responsive.Of(_build, fallback: 1000f);
            Element region = Region(_pendingFn, _failedFn, _contentFn, _shimmerFn, s_never, "concert-detail:" + p.RouteKey);
            return SubPageScroll("concert-detail:" + p.RouteKey, region, _under.Value, _onEdge);
        }

        bool Wide(float width)
        {
            bool wide = ConcertLayout.DetailWide(width, _wasWide, _wideInit);
            _wasWide = wide;
            _wideInit = true;
            return wide;
        }

        static Element Compose(DetailView d, bool wide)
        {
            Element? tickets = d.Offers.Length > 0 ? Tickets(d.Offers) : null;
            var main = new List<Element>(4) { SplitHero(d.ImageUrl, d.Accent, Identity(d), wide) };
            if (!wide && tickets is not null) main.Add(tickets);                  // narrow: inline, right after the facts
            if (d.Lineup.Length > 0) main.Add(Lineup(d.Lineup));
            if (d.Related.Length > 0) main.Add(Related(d.Related));
            var column = new BoxEl { Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = main.ToArray() };
            if (!wide || tickets is null) return column;                           // wide with NO offers: no rail (parity 64)
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.XL, MinWidth = 0f,
                Children = [column with { Grow = 1f, Basis = 0f }, new BoxEl { Width = 320f, Shrink = 0f, Direction = 1, Children = [tickets] }],
            };
        }

        /// <summary>The hero's copy: "Concert", the title (≤ 2 lines), the status pill when notable, then the facts — each
        /// with its 16-DIP glyph and omitted when absent — in the PROVIDER's clock.</summary>
        static Element Identity(DetailView d)
        {
            var headline = new List<Element>(3)
            {
                SectionCaption(Loc.Get(Strings.Concerts.Detail.Concert)),
                Design.Type.PageHero(d.Title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            };
            if (d.Status is { } status)
                headline.Add(new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Padding = new Edges4(Spacing.S, 2f, Spacing.S, 2f), Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
                    Children = [Caption(status) with { Color = Tok.SystemFillCritical, Weight = 600, MaxLines = 1 }],
                });
            var facts = new List<Element>(4) { Fact(Icons.Calendar, d.DateLine) };
            if (d.Doors is { } doors) facts.Add(Fact(Icons.Clock, doors));
            if (d.Place.Length > 0) facts.Add(Fact(Icons.MapPin, d.Place));
            if (d.Ages is { } ages) facts.Add(Fact(Icons.Info, ages));
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 1, Gap = 4f, MinWidth = 0f, Children = headline.ToArray() },
                    new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = facts.ToArray() },
                ],
            };
        }

        static Element Fact(string glyph, string text) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                Icon(glyph, 16f, Tok.TextSecondary) with { Shrink = 0f },
                Body(text) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            ],
        };

        /// <summary>"Tickets" + one card per offer: provider, availability, price, sale window, then Buy (a validated
        /// external link, lifted 4 DIP) or the quiet "Tickets not available online" — never a dead button (§0 #13).</summary>
        static Element Tickets(OfferView[] offers)
        {
            var kids = new Element[offers.Length + 1];
            kids[0] = SectionCaption(Loc.Get(Strings.Concerts.Detail.Tickets));
            for (int i = 0; i < offers.Length; i++)
            {
                var o = offers[i];
                var lines = new List<Element>(5)
                {
                    Design.Type.TrackTitle(o.Provider) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                };
                if (o.Availability is { } availability) lines.Add(Design.Type.TrackMeta(availability) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
                if (o.Price is { } price) lines.Add(Body(price) with { Color = Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
                if (o.Sale is { } sale) lines.Add(Design.Type.TrackMeta(sale) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
                if (o.Url is { } url)
                    lines.Add(new BoxEl
                    {
                        Direction = 0, Padding = new Edges4(0f, Spacing.XS, 0f, 0f),
                        Children = [Button.Accent(Loc.Get(Strings.Concerts.Detail.BuyTickets), () => Actions.Services.OpenExternal?.Invoke(url))],
                    });
                else lines.Add(Design.Type.TrackMeta(Loc.Get(Strings.Concerts.Detail.TicketsUnavailable)) with { MaxLines = 1 });
                kids[i + 1] = new BoxEl
                {
                    Key = "offer:" + i.ToString(CultureInfo.InvariantCulture) + ":" + o.Provider,
                    Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = Edges4.All(Spacing.M), Corners = Radii.CardAll,
                    Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = lines.ToArray(),
                };
            }
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids };
        }

        /// <summary>The lineup card: "Lineup" + "N artists", a divider, 60-DIP rows with 44 round avatars. Only a canonical
        /// artist navigates; a billing-only act is inert — no fill, no chevron, arrow cursor, not focusable (parity 49).</summary>
        static Element Lineup(ActView[] acts)
        {
            var rows = new Element[acts.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var act = acts[i];
                bool live = act.Navigable;
                int slot = act.ArtistSlot;
                var kids = new Element[live ? 3 : 2];
                kids[0] = new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(22f), ClipToBounds = true,
                    Children = [Controls.Artwork(act.Image, 44f, 44f, 22f)],
                };
                kids[1] = Design.Type.TrackTitle(act.Name) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
                if (live) kids[2] = Icon(Icons.ChevronRight, 14f, Tok.TextSecondary) with { Shrink = 0f };
                rows[i] = new BoxEl
                {
                    Key = live ? "act:" + slot.ToString(CultureInfo.InvariantCulture) : "lineup:" + i.ToString(CultureInfo.InvariantCulture) + ":" + act.Name,
                    Direction = 0, MinHeight = 60f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                    HoverFill = live ? Tok.FillSubtleSecondary : ColorF.Transparent, PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
                    Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
                    Cursor = live ? CursorId.Hand : CursorId.Arrow,
                    OnClick = live ? () => Track.GoToArtist(new Artist(slot)) : null,
                    Children = kids,
                };
            }
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, ClipToBounds = true, Corners = Radii.CardAll,
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                        Children =
                        [
                            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [SectionCaption(Loc.Get(Strings.Concerts.Detail.Lineup))] },
                            Design.Type.Eyebrow(Strings.Concerts.Detail.ArtistCount(acts.Length)) with { Color = Tok.TextSecondary, MaxLines = 1 },
                        ],
                    },
                    new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeSurfaceDefault },
                    new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.S), Children = rows },
                ],
            };
        }

        /// <summary>"Related concerts": cell 0 is the Browse-all tile (to the hub), then ≤ 15 related tiles.</summary>
        static Element Related(int[] related)
        {
            var items = new ShelfItem[related.Length + 1];
            var concerts = Entities.Current.Concerts;
            for (int i = 0; i < related.Length; i++) items[i + 1] = new ShelfItem(related[i], concerts.Version[related[i]]);
            return PagedShelf.Create(items, s_relatedCard, header: SectionCaption(Loc.Get(Strings.Concerts.Detail.RelatedConcerts)),
                headerGap: Spacing.S, measured: true, keyOf: s_itemKey);
        }
    }
}
