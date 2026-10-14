// ── Entities/Concert.Hub.cs ────────────────────────────────────────────────────────────────────────────────────────
// the Concerts hub's CORE HOST (0.2.9 ConcertHubPage) and the feed's append tail host; T10b3 moved the pictures to Classic (`Pages.Concerts`, `Concert.Tail`)
//
// Role: UI (core hosts)
// Owner: N (stream N-C)
// Spec: ch 17 §0 (the fifteen non-negotiables), §1 (the three trees), §2 W1-W5, W11, W12, §5 motion, §6 interaction, §7
//   readiness, §9 must-not-simplify
//
// The host keeps the page's state: the filter tuple as signals, its feed SUBJECT (`ConcertFeedKey`'s: a tuple change is a new subject with its own edges and
// cursor, so a late page can only land on the subject that asked for it, §9 #2), the demand and the bar's inputs (`Sync`), the near-tail watch and the append,
// the count memo behind the lazy grid, the readiness verdicts and the feed's PLAN (`ConcertHubBody`: the shelves, promo shelves, the grid and the tail in order).
// The view draws the two-clip sandwich over [header card] · [the filter bar, pinned under the masthead] · [the feed region] (§9 #1).
//
// PROPS FREEZE AT MOUNT. The filter bar reads the page's signals; the append tail is keyed per pagination key by the page; nothing ticks at rest.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Concert
{
    // ══ 1. THE APPEND TAIL (W11; §0 #15) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The hub feed's tail gate, keyed per pagination key by the page: (C) only NEAR the tail, (B) after a 300-ms arm
    /// re-checked when it fires, (A) never concurrently; three quiet failures collapse it. Far from the tail it is a 16-DIP
    /// spacer with no shimmer subtree (no standing pulse); near or loading it shimmers four tiles of the grid's family (the view,
    /// <see cref="ViewKeys.ConcertTail"/>, draws the three states).</summary>
    [CoreHost]
    sealed class TailHost(IReadSignal<bool> loading, IReadSignal<bool> nearTail, Action start) : Component
    {
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
            if (!busy && _attempts >= 3) return Slots.View(ViewKeys.ConcertTail, s_tailNone);
            if (!near && !busy) return Slots.View(ViewKeys.ConcertTail, s_tailSpacer);
            return Slots.View(ViewKeys.ConcertTail, s_tailShimmer);
        }

        static readonly ConcertTailProps s_tailNone = new(ConcertTailState.None), s_tailSpacer = new(ConcertTailState.Spacer), s_tailShimmer = new(ConcertTailState.Shimmer);

        void Fire()
        {
            if (loading.Peek() || !nearTail.Peek() || _attempts >= 3) return;
            _attempts++;
            start();
        }
    }

    // ══ 2. THE HUB (W1-W5, W12; §0 #2, #11; §9 #1-#3, #5, #6, #8) ════════════════════════════════════════════════════

    /// <summary>The <c>concerts</c> destination (0.2.9 <c>ConcertHubPage</c>). The filter tuple is the page's own state; its feed SUBJECT is
    /// <see cref="ConcertFeedKey"/>'s, so a tuple change is a new subject with its own edges and cursor, and a late page can only land on
    /// the subject that asked for it (§9 #2).</summary>
    [CoreHost]
    sealed class HubHost : Component
    {
        static uint s_countAsk;

        static readonly ConcertHubBody s_noLocation = new(ConcertHubBodyKind.NoLocation, Array.Empty<ConcertFeedBlock>());
        static readonly ConcertHubBody s_empty = new(ConcertHubBodyKind.Empty, Array.Empty<ConcertFeedBlock>());

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
        readonly Signal<bool> _nearTail = new(true), _appendLoading = new(false);
        readonly PlacePicker _picker = new();
        readonly HashSet<int> _countAsked = new();

        Scope? _scope, _settledScope;
        bool _locationAsked, _pending, _failed;
        NodeHandle _whereAnchor;
        IOverlayService? _overlay;
        ConcertHubBody? _body;
        int _keyPlace = -1, _keyRadius, _feedSlot, _gridStart = -1, _conceptsPlace = -1;
        ConcertWhen? _keyWhen;
        IReadOnlyList<string>? _keySelected;
        Scope? _keyScope;
        uint _conceptsVersion = uint.MaxValue;

        readonly ConcertFilterBarProps _bar;
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<TailHost> _tailFactory;
        readonly Func<int> _gridCount;                           // the count memo's compute (UseComputed in Render)
        readonly Func<int, int> _gridSlotAt;
        readonly Func<string, Element> _mountTail;
        Memo<int>? _gridCountMemo;                               // set by the first Render, before any grid mounts
        readonly Action _sync, _retry, _append, _openPicker, _watchScroll;
        readonly Action<bool> _onLocation, _onAppended;
        /// <summary>The hub viewport's scroll handle: the near-tail watch and the LazyGrid's page offset read it.</summary>
        readonly ScrollHandle _handle = new();
        long _scrollKey = long.MinValue;

        public HubHost()
        {
            _pendingFn = () => _pending;
            _failedFn = () => _failed;
            _sync = Sync;
            _retry = () => AskFeed(false, null, null);
            _append = Append;
            _openPicker = () => _picker.Toggle(_overlay, () => _whereAnchor);
            _onLocation = _ => { _settledScope = Entities.Current; _locationTick.Value = _locationTick.Peek() + 1; };
            _onAppended = ok => { _appendLoading.Value = false; if (ok) _nearTail.Value = false; };
            _bar = new ConcertFilterBarProps
            {
                Place = _place, Inferred = _inferred, Radius = _radius, When = _when, Selected = _selected, Concepts = _concepts, Count = _count,
                SetWhen = w => _when.Value = w,
                SetSelected = s => _selected.Value = s,
                SetRadius = km => _radius.SetIfChanged(km),
                CaptureWhere = h => _whereAnchor = h,
                WhereAnchor = () => _whereAnchor,
                OpenPicker = _openPicker,
            };
            _tailFactory = () => new TailHost(_appendLoading, _nearTail, _append);
            _mountTail = key => Embed.Comp(_tailFactory) with { Key = key };
            _gridCount = GridCount;
            _gridSlotAt = GridSlotAt;
            // The near-tail watch: the offset floored to 24 px × the content height floored to 48 px (an append's own
            // growth re-evaluates nearness); near = within 1.5 viewports of the end. The page scroll feeds the LazyGrid.
            _watchScroll = () =>
            {
                float y = (float)_handle.Offset.Value, vh = (float)_handle.ViewportSignal.Value, ch = (float)_handle.ExtentSignal.Value;
                long key = ((long)(y / 24f) << 20) ^ (long)(ch / 48f);
                if (key == _scrollKey) return;
                _scrollKey = key;
                _scroll.Value = y;
                _nearTail.SetIfChanged(y + vh >= ch - 1.5f * vh);
            };
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
            UseSignalEffect(_watchScroll);
            _gridCountMemo = UseComputed(_gridCount);

            int placeSlot = scope.SavedPlace;
            int feed = placeSlot > 0 ? FeedSlot(placeSlot, radius, when, selected) : Table.None;
            var state = feed > 0 ? e.FeedSection.Readiness(feed) : EdgeState.Unknown;
            _pending = placeSlot > 0 ? state == EdgeState.Unknown : !ReferenceEquals(_settledScope, scope);
            _failed = state == EdgeState.Failed;
            _body = _pending || _failed ? null : placeSlot <= 0 ? s_noLocation : FeedBody(scope, feed);

            var props = new ConcertHubProps
            {
                ScrollScope = UseContext(Shell.PageScrollScope),
                Scroll = _handle,
                Pending = _pendingFn,
                Failed = _failedFn,
                Body = _body,
                Retry = _retry,
                OpenPicker = _openPicker,
                Bar = _bar,
                GridCount = _gridCountMemo!,
                GridSlotAt = _gridSlotAt,
                Tail = _mountTail,
            };
            return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)_scroll, Slots.View(ViewKeys.ConcertsHub, props));
        }

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

        // ── the feed's plan (§1.1: shelves · promos · the virtualized grid · the tail) ───────────────────────────────

        ConcertHubBody FeedBody(Scope scope, int feed)
        {
            var e = scope.Edges;
            var targets = e.FeedSection.Targets(feed);
            var payload = e.FeedSection.Payload(feed);
            var promoTargets = e.FeedSectionPlaylists.Targets(feed);
            var promos = e.FeedSectionPlaylists.Payload(feed);
            if (ConcertHub.IsFeedEmpty(targets, promoTargets)) return s_empty;

            var blocks = new List<ConcertFeedBlock>(8);
            ulong placed = 0;
            bool grid = false;
            for (int i = 0; i < payload.Length;)
            {
                int end = ConcertFeedMerge.SectionEnd(payload, i);
                var head = payload[i];
                if (head.Kind == (byte)ConcertFeedSectionKind.AllEvents)
                {
                    if (!grid) { grid = true; blocks.Add(new ConcertFeedBlock(ConcertBlockKind.Grid, "hub-grid", false, Array.Empty<ConcertShelfItem>(), feed)); }
                }
                else blocks.Add(ConcertShelf(head, targets[i..end]));
                placed |= PromosFor(blocks, head, promoTargets, promos);
                i = end;
            }
            // A section that shipped promos but zero concerts still gets its promo shelf (parity 57).
            for (int g = 0, j = 0; j < promos.Length; g++)
            {
                int end = ConcertFeedMerge.SectionEnd(promos, j);
                if (g >= 64 || (placed & (1UL << g)) == 0) blocks.Add(PromoShelf(promos[j], promoTargets[j..end]));
                j = end;
            }
            var cursor = scope.ConcertFeeds.Row[feed].PaginationKey;
            if (!cursor.IsEmpty)   // absent entirely when the feed is complete
                blocks.Add(new ConcertFeedBlock(ConcertBlockKind.Tail, "concert-append:" + Entities.Strings.Resolve(cursor), false, Array.Empty<ConcertShelfItem>(), 0));
            return new ConcertHubBody(ConcertHubBodyKind.Feed, blocks);
        }

        static ulong PromosFor(List<ConcertFeedBlock> blocks, in FeedSectionEdge head, ReadOnlySpan<int> targets, ReadOnlySpan<FeedSectionEdge> promos)
        {
            long group = ConcertFeedMerge.GroupOf(head);
            for (int g = 0, j = 0; j < promos.Length && g < 64; g++)
            {
                int end = ConcertFeedMerge.SectionEnd(promos, j);
                if (ConcertFeedMerge.GroupOf(promos[j]) == group)
                {
                    blocks.Add(PromoShelf(promos[j], targets[j..end]));
                    return 1UL << g;
                }
                j = end;
            }
            return 0;
        }

        /// <summary>A Nearby / Recommended shelf: ≤ 16 tiles. "Near you" is Nearby's caption; every other non-grid kind is
        /// "Recommended for you" (§1.1).</summary>
        static ConcertFeedBlock ConcertShelf(in FeedSectionEdge head, ReadOnlySpan<int> slots)
            => new(ConcertBlockKind.Shelf,
                   "hub-shelf:" + head.Kind.ToString(CultureInfo.InvariantCulture) + ":" + Entities.Strings.Resolve(head.Key),
                   head.Kind == (byte)ConcertFeedSectionKind.Nearby, ConcertVerbs.ConcertItems(slots, 16), 0);

        static ConcertFeedBlock PromoShelf(in FeedSectionEdge head, ReadOnlySpan<int> slots)
            => new(ConcertBlockKind.Promos,
                   "hub-promos:" + head.Kind.ToString(CultureInfo.InvariantCulture) + ":" + Entities.Strings.Resolve(head.Key),
                   false, ConcertVerbs.PlaylistItems(slots, 16), 0);

        /// <summary>The AllEvents run of the subject's list (the wire ships it last, and a merge appends new sections at
        /// the end, so it is the list's tail from its first row). Runs as the count memo's compute, so it derives the
        /// SUBJECT from the same signals Render does (the scope, the saved place, the filter tuple) instead of reading the
        /// <c>_feedSlot</c> field: a memo is pulled by whichever grid render needs it, and the outgoing subject's grid
        /// may pull it before this page's own render has moved the field — deriving keeps the incoming grid's first count
        /// its own. The <c>_gridStart</c> side effect stays: the grid pulls the memo first in its pass, so the cell reads
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

        /// <summary>The concert slot of grid cell <paramref name="index"/>; <see cref="Table.None"/> for an append reconcile's skew.</summary>
        int GridSlotAt(int index)
        {
            var targets = Entities.Current.Edges.FeedSection.Targets(_feedSlot);
            int at = _gridStart + index;
            if (_gridStart < 0 || (uint)at >= (uint)targets.Length) return Table.None;   // an append reconcile's skew
            return targets[at];
        }
    }
}
