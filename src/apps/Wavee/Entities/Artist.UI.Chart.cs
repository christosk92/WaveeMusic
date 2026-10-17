// ── Entities/Artist.UI.Chart.cs ────────────────────────────────────────────────────────────────────────────────────
// The artist page's "Top tracks" CHART (0.2.9 Features/Detail/ArtistPopular.cs): ONE PagedShelf collection — a five-row
// virtual strip with page-mandatory snapping, at most two columns, a ‹ ●● › pip pager in the header — whose realized
// cells are chart ROW components keyed by track. The named partial ch 08 §9 and plan §2 ask for, inside Artist.UI.cs's
// 1,500-line budget. The PICTURE is the structure's (`StyleKeys.ArtistChart`: T8c the rows, the cells, the header, the skeleton, the failed
// panel and the pager; T10b2 the frame, the shelf's holder and the selection bar); this file keeps the host, its gate, its asks and its verbs.
//
// Role: UI
// Owner: N (stream N-A)
// Wave: 5
// Budget: part of Artist.UI.cs's 1500 lines
// Spec: ch 08 §0 #6, #13; W8-W11, W25-W26; §9 traps
//
// ── WHAT 0.3 CHANGES, AND WHY THE KEYS GOT SIMPLER ───────────────────────────────────────────────────────────────────
//
// 0.2.9 charted a FROZEN IReadOnlyList<Track> and had to remount the whole shelf ("chart:{total}:{counted}:facts=…") every
// time the extended list, the kind-185 counts or the media facts landed. Here the shelf's items are (index, slot) value
// pairs and every row reads `Edges.ArtistPopular` + its Track row LIVE through a memo over (slot, version, row state):
// a table publish that changes a title, a count or a like re-skins exactly that row and remounts nothing (ReuseGuard).
// The only remount keys left are the ones that change a row's CHILD COUNT or the shelf's style:
//  · the cell key   "row:" + uri + "|art"/"|noart" + "|classic"/"|modern"  ("chart#" + i for an empty uri) — ch 08 audit #5;
//  · the shelf key  art × classic. The column cap, the rows per column and the row height are LIVE props of the mounted PagedShelf, so a
//    tier crossing (Huge 4K's four columns of four against Huge's three of five) re-fits the SAME shelf: its page and selection survive.
//
// ── ZERO ALLOCATION ON A SCROLL FRAME ────────────────────────────────────────────────────────────────────────────────
//
// Nothing here runs per scroll frame. A column crossing realizes a band of rows in one frame, so the row builder counts
// first and allocates exact arrays (never List + ToArray), the feat line allocates its featured list only on the "+N"
// branch, the selection pill is an always-mounted BOUND opacity (selecting is compositor-only) and the per-row playback
// state sits behind a memo so a skip between two other tracks renders no row (ch 08 §9 traps a-e).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Artist
{
    // ══ 1. THE ENTRY POINT ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Top-tracks chart for <paramref name="a"/>. <paramref name="accent"/> is the page's WATCHED chrome
    /// accent (a grading landing re-tints the header rule and the selection pills in place). The page waits for the
    /// chart in <see cref="ArtistReadiness.BodyReady"/>, so this inner region does not join a page-level skeleton
    /// group (that coordination faded the hero from 0 after the photo was already decoded).</summary>
    internal static Element Chart(Artist a, IReadSignal<ColorF> accent, object? group = null)
        => Embed.Comp(new ChartProps(a, accent, group), static () => new ChartHost());

    sealed record ChartProps(Artist A, IReadSignal<ColorF> Accent, object? Group)
    {
        public bool Equals(ChartProps? o) => o is not null && o.A == A && ReferenceEquals(o.Accent, Accent) && Equals(o.Group, Group);
        public override int GetHashCode() => A.Slot;
    }

    /// <summary>One charted position: its ordinal and the track slot it held when the shelf items were built. Value
    /// equality is the shelf's gate — an equal (index, slot) list after a publish rebuilds no card.</summary>
    readonly record struct ChartItem(int Index, int Slot);

    // The geometry of the shelf (the row height, the gaps, the 264 card minimum that is a BREAKPOINT for two columns, the edge fade) and the
    // check lane's motion are the structure's: `ArtistChartStyle.Metrics` (Classic: `Rules\ClassicArtistChartLayout.cs`, ArtistPopular.cs:88-108).

    // The chart shelf's key per STYLE (artwork column, Classic or modern rows): no column or row count is in it — both are live props of the
    // mounted shelf, so a tier crossing re-fits the SAME chart (its page and the row selection's slots survive) instead of remounting it.
    static readonly string[] s_chartShelfKeys =
    [
        "chart-shelf:noart:modern", "chart-shelf:noart:classic", "chart-shelf:art:modern", "chart-shelf:art:classic",
    ];

    /// <summary>The most chart columns the chart lays out (a structure that offers more is held to it).</summary>
    const int ChartKeyColumns = 4;

    // ══ 2. THE HOST ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chart component: readiness (the owner's identity and artwork gate; play counts fill after reveal), the shelf, the
    /// selection model (a chart row selection survives the page flips that slide the realized window), the drag payload
    /// and the row menu. Its shelf closures read FIELDS written by render, never render locals (§9 trap e).</summary>
    sealed class ChartHost : Component
    {
        internal readonly SelectionModel Selection = new() { Mode = ItemsSelectionMode.Extended };
        internal Artist Owner;
        internal bool Classic, ShowArtwork;
        internal IReadSignal<ColorF>? AccentSignal;

        ChartItem[] _items = [];
        string[] _keys = [];
        bool _keysArt, _keysClassic;
        ArtistPopularLayout.Tier? _tier;
        bool _tierClassic;
        bool _ready, _failed;
        /// <summary>The tier's widest chart and its rows per column, written by render (the derived shimmer reads them outside it).</summary>
        int _maxColumns = ArtistSections.ChartMaxColumns, _rows = ArtistSections.ChartMaxRows;
        Element? _shelf;
        IOverlayService? _overlay;

        /// <summary>The structure's chart templates (T8c), resolved once per host from the ambient registry; the rows and the pager (components
        /// of their own) draw from it through their owner.</summary>
        ArtistChartStyle? _style;
        internal ArtistChartStyle? Style => _style;
        /// <summary>The shelf's row-height function, rebuilt only when the structure's row height moves (the shelf reads it as ctor configuration,
        /// so a stable instance keeps the shelf's props equal across renders).</summary>
        Func<float, float>? _cardHeightFn;
        float _cardHeightFor = float.NaN;

        // ── the failure gate's "after we asked" half (Search.Page.cs's `_askedAll`) ──
        // `ArtistReadiness.ChartFailed` reads "not asked" as "un-asked by a failure", which is only true once this
        // host's own asks have run for the (artist, scope) it is showing; the target marks likewise count only for the
        // popular list version the host has asked rows for. Plain fields: written by effects, read by the next render.
        int _demandedSlot = Table.None, _targetsSlot = Table.None;
        uint _demandedEpoch, _targetsVersion;
        /// <summary>Bumped by the recheck deadline and the Retry so the region re-reads the marks — an un-ask publishes
        /// no signal, and a batch that fails terminally publishes nothing at all.</summary>
        readonly Signal<int> _recheck = new(0);
        TimerHandle _deadline;
        /// <summary>The deadline after which the chart re-reads its marks while a request is still out (Search's).</summary>
        const float RecheckMs = 10_000f;

        readonly Func<ChartItem, int, float, Element> _cardAt;
        readonly Func<ChartItem, int, string> _keyOf;
        readonly Func<ShelfPagerContext, Element> _pager;
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Func<int, Track> _trackAt;
        readonly Prop<ColorF> _pillFill;
        readonly Action _retry, _demand, _demandTargets, _recheckNow;
        // WP3 — the shelf's invoke (Tap on a row / Enter / Space-on-single / double-click) and the check lane's visibility.
        readonly Action<ChartItem, int> _invoke;
        internal readonly Func<bool> ChecksVisible;
        readonly List<int> _dragIndices = new(8);

        static readonly Func<int, int> s_noMembership = static _ => -1;
        static readonly Track.MenuOptions s_menuOptions = new(ShowGoToAlbum: true);

        public ChartHost()
        {
            _cardAt = Card;
            _keyOf = (_, i) => (uint)i < (uint)_keys.Length ? _keys[i] : "chart#" + i;
            _pager = ctx => Embed.Comp(
                new ChartPagerProps(_style!, ctx.Page, ctx.PageCount, ctx.CanPrev, ctx.CanNext, ctx.Prev, ctx.Next, ctx.GoTo, _items.Length),
                static () => new ChartPager());
            _pendingFn = () => !_ready && !_failed;
            _failedFn = () => _failed;
            _contentFn = () => _shelf ?? new BoxEl();
            _shimmerFn = () => _style is null ? new BoxEl()
                : _style.Skeleton(ArtistChartSkeletonArgs.For(Owner.PopularSlots.Length, Classic, ShowArtwork, _maxColumns, _rows));
            _retry = Retry;
            _failedPanelFn = () => _style is null ? new BoxEl()
                : _style.Failed(Loc.Get(Strings.Artist.TopTracks), AccentSignal?.Peek() ?? Tok.AccentDefault, _retry);
            _trackAt = TrackAt;
            _pillFill = Prop.Of(() => AccentSignal?.Value ?? Tok.AccentDefault);
            _demand = Demand;
            _demandTargets = DemandTargets;
            _recheckNow = Recheck;
            _invoke = (_, i) => InvokeAt(i);
            ChecksVisible = () => { _ = Selection.Version.Value; return SelectionBarRules.ChecksVisible(Selection.SelectedCount, false); };
        }

        public override Element Render()
        {
            var p = UseProps<ChartProps>();
            _overlay = UseContext(Overlay.Service);
            var registry = UseContext(global::FluentGpu.Theming.ViewRegistry.Context);
            var facts = Theming.TierContext.DrawnIn(UseContext(Theming.ThemeFactsContext.Signal));   // the tiers this chart is drawn in
            uint scopeEpoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ArtistPopular.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Tracks.Changed.Value;                  // the gate: every target Knows(TrackFields.Row)
            _ = _recheck.Value;                              // the deadline and the Retry re-read the marks through this
            Owner = p.A;
            AccentSignal = p.Accent;
            ColorF accent = p.Accent.Value;
            Classic = ShellPolicy.Tracked.Lists.ClassicRows;
            ShowArtwork = ShellPolicy.Tracked.Lists.TrackArtwork;

            // The chart's own asks (the same ones the page makes, so they dedupe to nothing) and the latch behind the
            // failure gate; the deadline re-arms with the artist and the scope, and `Recheck` keeps it alive while a
            // request is still out.
            UseEffect(_demand, DepKey.From(Owner.Slot, (int)scopeEpoch));
            UseEffect(_demandTargets);
            _deadline = UseTimeout(_recheckNow, RecheckMs, DepKey.From(Owner.Slot, (int)scopeEpoch));

            var style = _style ??= ListStyleLookup.Of(registry, StyleKeys.ArtistChart);
            if (style is null) return new BoxEl();
            var metrics = style.Metrics(Classic);
            if (_cardHeightFn is null || _cardHeightFor != metrics.RowHeight)
            {
                float rowHeight = metrics.RowHeight;
                _cardHeightFn = _ => rowHeight;
                _cardHeightFor = rowHeight;
            }

            _ready = ArtistReadiness.Chart(Owner);
            _failed = !_ready && ChartFailedNow(scope);
            NoteGate(scope);
            RefreshItems();
            Selection.ItemCount = _items.Length;

            // The tier's widest chart and its rows per column (TC6: two columns at Standard, more at Huge, one at Compact; Huge 4K's four rows), read
            // tracked: a window crossing re-renders the chart and re-fits the mounted shelf to both (live props, never a key). The skeleton reads the
            // same pair (the fields), so it keeps the chart's columns per tier.
            var chartTiers = ShellPolicy.Tracked.ArtistTiers;
            var tierFacts = facts.Value.Tiers;
            _maxColumns = Math.Min(ChartKeyColumns, chartTiers.ChartMaxColumns(tierFacts));
            _rows = Math.Max(1, chartTiers.ChartRows(tierFacts));
            int cols = ArtistSections.ChartColumns(_items.Length, _maxColumns, _rows);
            int shelfKey = (ShowArtwork ? 2 : 0) + (Classic ? 1 : 0);   // no column or row count: the fit is live (the crossing re-fits the same shelf)
            Element shelf = PagedShelf.Create(
                _items,
                cardAt: _cardAt,
                cardHeight: _cardHeightFn!,
                header: style.Header(Loc.Get(Strings.Artist.TopTracks), accent),
                pager: ShelfPager.None,
                customPager: _pager,
                minCardW: metrics.MinCardWidth,
                maxCardW: 9999f,
                gap: metrics.CellGap,
                headerGap: metrics.HeaderGap,
                rows: _rows,
                edgeFade: metrics.EdgeFade,
                keyOf: _keyOf,
                maxColumns: cols,
                snap: ShelfSnap.Page,
                maxItems: ArtistSections.ChartMaxTracks,
                // WP3 — the slot root owns click, focus and the keyboard (arrows, Enter, Space, Ctrl+A, Esc) over the
                // host-owned model, so a selection survives the page flips that slide the realized window.
                onInvoke: _invoke,
                selectionMode: ItemsSelectionMode.Extended,
                selection: Selection);
            // The shelf's style key rides the shelf itself; the structure's holder keeps it a CHILD (a key on a component's ROOT is inert). The
            // rows per column are a live prop like the column cap (PagedShelf re-fits a multi-row strip to another row count in place and keeps
            // the page holding its first row), so nothing here keys on the tier.
            _shelf = style.Shelf(shelf with { Key = s_chartShelfKeys[shelfKey] });

            // The selection bar is its own component, so a select/clear re-renders the bar and the bound row chrome, never this host or the shelf.
            return style.Frame(new ArtistChartFrameArgs
            {
                Pending = _pendingFn, Failed = _failedFn, Content = _contentFn, Shimmer = _shimmerFn, OnFailed = _failedPanelFn,
                Bar = Embed.Comp(() => new ChartBar(this)) with { Key = "chart-bar" },
            });
        }

        // ── readiness: the failure gate, its latch, and the recheck ─────────────────────────────────────────────────

        /// <summary>Has the chart stopped coming (<see cref="ArtistReadiness.ChartFailed"/>)? Reads the four marks off the
        /// live tables for the artist and — once this host has asked rows for the list it is showing — for every target.
        /// False until <see cref="Demand"/> has run for this (artist, scope): before that "not asked" is "not asked yet".</summary>
        bool ChartFailedNow(Scope scope)
        {
            int slot = Owner.Slot;
            if (!Owner.IsValid || _demandedSlot != slot || _demandedEpoch != scope.Epoch) return false;
            var edges = scope.Edges.ArtistPopular;
            var tracks = scope.Tracks;
            ReadOnlySpan<int> targets = _targetsSlot == slot && _targetsVersion == edges.Version(slot)
                ? edges.Targets(slot) : ReadOnlySpan<int>.Empty;
            int n = targets.Length;
            // Three parallel columns for at most ChartMaxTracks rows: one stack buffer, sliced. A longer list (never seen;
            // the chart itself is capped) pays one array.
            int stride = Math.Max(n, 64);
            Span<uint> marks = n <= 64 ? stackalloc uint[192] : new uint[n * 3];
            Span<uint> known = marks.Slice(0, n), asked = marks.Slice(stride, n), inflight = marks.Slice(2 * stride, n);
            for (int i = 0; i < n; i++)
            {
                int t = targets[i];
                bool live = t > Table.None && t < tracks.Count;
                known[i] = live ? tracks.Known[t] : 0u;
                asked[i] = live ? tracks.Asked[t] : 0u;
                inflight[i] = live ? tracks.Inflight[t] : 0u;
            }
            return ArtistReadiness.ChartFailed(Owner.Knows(ArtistFields.Chart), scope.Artists.Asked[slot], scope.Artists.Inflight[slot],
                                               edges.Readiness(slot), known, asked, inflight);
        }

        /// <summary>The chart's asks for this (artist, scope): the same two the page's own Demand makes (Artist.Page.cs), so
        /// whichever runs first sends and the other dedupes to nothing. What this host needs from it is the LATCH: from
        /// here on, a Chart bit that is not asked was un-asked by a failure, and the gate may say so.</summary>
        void Demand()
        {
            if (!Owner.IsValid) return;
            var scope = Entities.Current;
            Entities.Ensure(Owner, ArtistFields.All);
            if (scope.Edges.ArtistPopular.State(Owner.Slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistPopular, Owner.Slot);
            _demandedSlot = Owner.Slot;
            _demandedEpoch = scope.Epoch;
            _deadline.Restart();
        }

        /// <summary>Auto-tracked on the popular list: as it lands, ask its rows at the chart's row shape (the page asks
        /// the same; the planner sends one request) and latch the list VERSION the rows were asked for — the gate reads
        /// the target marks only for that version.</summary>
        void DemandTargets()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var edges = scope.Edges.ArtistPopular;
            _ = edges.Changed.Value;
            if (!Owner.IsValid) return;
            int slot = Owner.Slot;
            var popular = edges.Targets(slot);
            if (popular.Length > 0) Entities.Ensure(scope.Tracks, popular, (uint)(TrackFields.Row | TrackFields.Video));
            _targetsSlot = slot;
            _targetsVersion = edges.Version(slot);
        }

        /// <summary>The Retry vacancy's action: ask for everything the chart waits on AGAIN, whatever the edge's state —
        /// the popular list (<see cref="Entities.RefreshEdge"/>), the artist's Chart bit and the targets' row shape
        /// (<see cref="Entities.Refresh"/>, which un-seals what an earlier answer left asked). The rows already there keep
        /// rendering until the answers replace them; the deadline and the recheck start over.</summary>
        void Retry()
        {
            if (!Owner.IsValid) return;
            var scope = Entities.Current;
            int slot = Owner.Slot;
            // Evidence (2026-09-25): the Retry vacancy is a USER act that re-plans the whole chart — the owner's log could
            // not tell a click from a timer. One always-on line per press, before the asks it causes.
            if (Log.IsEnabled(WaveeLogLevel.Info))
                Log.Event(WaveeLogLevel.Info, "ui", "ui.action", "", null, -1, null,
                    WaveeLogField.Of("action", "chart.retry"),
                    WaveeLogField.Of("artist", Owner.Uri.Text),
                    WaveeLogField.Of("targets", scope.Edges.ArtistPopular.Targets(slot).Length),
                    WaveeLogField.Of("state", _gateState.ToString()));
            Entities.RefreshEdge(FetchEdge.ArtistPopular, slot);
            Entities.Refresh(scope.Artists, new ReadOnlySpan<int>(in slot), (uint)ArtistFields.Chart);
            var popular = scope.Edges.ArtistPopular.Targets(slot);
            if (popular.Length > 0) Entities.Refresh(scope.Tracks, popular, (uint)(TrackFields.Row | TrackFields.Video));
            _deadline.Restart();
            _recheck.Value = _recheck.Peek() + 1;
        }

        // ── evidence: the gate's flips (2026-09-25) ──────────────────────────────────────────────────────────────────
        //
        // `artist.chart artist= state= prev= reason= index= slot= uri= missing= known= asked= inflight= targets= edge=
        // chart=` — ONE line each time the chart's verdict (pending / ready / failed) CHANGES, naming the input that
        // decided it (`ArtistReadiness.Explain`): which charted row, which of its groups are missing, and its asked /
        // in-flight marks. It is the line that says "row 8682 is missing PlayCount, asked, nothing in flight ⇒ failed".
        // A steady render writes nothing (one enum compare).

        enum GateState : byte { None, Pending, Ready, Failed }
        GateState _gateState;

        void NoteGate(Scope scope)
        {
            GateState state = _ready ? GateState.Ready : _failed ? GateState.Failed : GateState.Pending;
            if (state == _gateState) return;
            GateState prev = _gateState;
            _gateState = state;
            if (!Owner.IsValid || !Log.IsEnabled(WaveeLogLevel.Info)) return;
            int slot = Owner.Slot;
            var edges = scope.Edges.ArtistPopular;
            var tracks = scope.Tracks;
            ReadOnlySpan<int> targets = edges.Targets(slot);
            int n = targets.Length;
            var known = new uint[n];
            var asked = new uint[n];
            var inflight = new uint[n];
            for (int i = 0; i < n; i++)
            {
                int t = targets[i];
                bool live = t > Table.None && t < tracks.Count;
                known[i] = live ? tracks.Known[t] : 0u;
                asked[i] = live ? tracks.Asked[t] : 0u;
                inflight[i] = live ? tracks.Inflight[t] : 0u;
            }
            var trace = ArtistReadiness.Explain(Owner.Knows(ArtistFields.Chart), scope.Artists.Asked[slot], scope.Artists.Inflight[slot],
                edges.State(slot), edges.Readiness(slot), known, asked, inflight);
            int deciding = trace.Index >= 0 && trace.Index < n ? targets[trace.Index] : 0;
            string uri = deciding > Table.None && deciding < tracks.Count ? new Track(deciding).Uri.Text : "-";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Log.Event(WaveeLogLevel.Info, "ui", "artist.chart", "", null, -1, null,
                WaveeLogField.Of("artist", Owner.Uri.Text),
                WaveeLogField.Of("state", state.ToString()),
                WaveeLogField.Of("prev", prev.ToString()),
                WaveeLogField.Of("reason", trace.Reason.ToString()),
                WaveeLogField.Of("index", trace.Index),
                WaveeLogField.Of("slot", deciding),
                WaveeLogField.Of("uri", uri),
                WaveeLogField.Of("missing", "0x" + trace.Missing.ToString("x", inv)),
                WaveeLogField.Of("known", "0x" + trace.Known.ToString("x", inv)),
                WaveeLogField.Of("asked", "0x" + trace.Asked.ToString("x", inv)),
                WaveeLogField.Of("inflight", (long)trace.Inflight),
                WaveeLogField.Of("targets", n),
                WaveeLogField.Of("edge", edges.State(slot).ToString()),
                WaveeLogField.Of("chart", Owner.Knows(ArtistFields.Chart)));
        }

        /// <summary>The deadline: re-read the marks (a failed batch publishes nothing, so nothing else would), and re-arm
        /// only while a request for the artist, a target or the popular list is still out.</summary>
        void Recheck()
        {
            if (!Owner.IsValid) return;
            var scope = Entities.Current;
            int slot = Owner.Slot;
            _recheck.Value = _recheck.Peek() + 1;
            if (_ready) return;
            var edges = scope.Edges.ArtistPopular;
            bool outstanding = scope.Artists.Inflight[slot] != 0
                            || (edges.State(slot) == EdgeState.Unknown && !edges.IsFailed(slot) && edges.WasAsked(slot, 0));
            var targets = edges.Targets(slot);
            var tracks = scope.Tracks;
            for (int i = 0; !outstanding && i < targets.Length; i++)
                outstanding = targets[i] > Table.None && targets[i] < tracks.Count && tracks.Inflight[targets[i]] != 0;
            if (outstanding) _deadline.Restart();
        }

        /// <summary>Rebuild the (index, slot) snapshot and the row keys only when the live list or an appearance key
        /// changed — a publish that leaves the chart alone hands the shelf the SAME array.</summary>
        void RefreshItems()
        {
            var slots = Owner.PopularSlots;
            int n = Math.Min(slots.Length, ArtistSections.ChartMaxTracks);
            bool same = n == _items.Length && _keysArt == ShowArtwork && _keysClassic == Classic;
            for (int i = 0; same && i < n; i++) same = _items[i].Slot == slots[i];
            if (same) return;

            var items = new ChartItem[n];
            var keys = new string[n];
            string suffix = (ShowArtwork ? "|art" : "|noart") + (Classic ? "|classic" : "|modern");
            for (int i = 0; i < n; i++)
            {
                items[i] = new ChartItem(i, slots[i]);
                var track = new Track(slots[i]);
                string uri = track.IsValid ? track.Uri.Text : "";
                keys[i] = uri.Length > 0 ? "row:" + uri + suffix : "chart#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            _items = items;
            _keys = keys;
            _keysArt = ShowArtwork;
            _keysClassic = Classic;
        }

        /// <summary>One cell at the fitted width. The TIER is decided here with hysteresis and re-pushed as a PROP (never
        /// a key); the row identity is the track (ArtistPopular.cs:230-269).</summary>
        Element Card(ChartItem item, int index, float cellW)
        {
            bool classic = Classic;
            if (_tier is not null && _tierClassic != classic) _tier = null;   // a classic/modern toggle starts fresh
            _tierClassic = classic;
            var tier = ArtistPopularLayout.Decide(cellW, classic, _tier);
            _tier = tier;

            Element content = Embed.Comp(new ChartRowProps(this, index, tier), static () => new ChartRow())
                with { Key = _keyOf(item, index) };
            int at = index;
            var row = _style!.Cell(new ArtistChartCellArgs
            {
                Content = content,
                // A vertical lift wins the drag; a horizontal sweep yields to the shelf's pan (the engine's arena).
                Draggable = Drag.Source(() => DragPayloadFor(at)),
                PillFill = _pillFill,
                // The accent pill is the single-selection cue; with the check lane up the checkbox says it instead.
                PillOpacity = Prop.Of(() => { _ = Selection.Version.Value; return Selection.IsSelected(at) && !ChecksVisible() ? 1f : 0f; }),
                Classic = classic,
            });
            var overlay = _overlay;
            return Controls.IsNullOverlay(overlay) ? row : row.WithContextMenu(overlay, () => MenuFor(at));
        }

        internal Track TrackAt(int index)
        {
            var slots = Owner.PopularSlots;
            return (uint)index < (uint)slots.Length ? new Track(slots[index]) : default;
        }

        /// <summary>Play the chart FROM this row, with the chart's OWN rows as the queue — the same funnel every other
        /// track list goes through (<c>Track.Table.StartVisible</c>, and the album / playlist / liked pages).
        /// <para>It must not be <c>PlayContext(artistUri, trackUri)</c>. The server resolves an artist context to its
        /// own, different list, so a row past the top ten is simply absent from it: <c>RemotePlan.StartIndex</c>
        /// byte-matches the uri, finds nothing, and the load falls back to row 0 — which is why clicking #41 played
        /// something else entirely (`context load rows=74 start=0` in the log). Queuing what the user can actually see
        /// is also the more honest answer to "play from here".</para></summary>
        internal void StartAt(int index)
        {
            var slots = Owner.PopularSlots;
            if ((uint)index >= (uint)slots.Length) return;
            var refs = System.Buffers.ArrayPool<EntityRef>.Shared.Rent(slots.Length);
            try
            {
                for (int i = 0; i < slots.Length; i++) refs[i] = new EntityRef(EntityKind.Track, slots[i]);
                Playback.PlayRows(refs.AsSpan(0, slots.Length), index, Owner.Id);
            }
            finally { System.Buffers.ArrayPool<EntityRef>.Shared.Return(refs); }
        }

        /// <summary>The selection-aware track menu — the same Explorer semantics the album drawer's rows use, so the
        /// chart's 0.2.9 BuildSingle/Build asymmetry (W27) is deliberately unified.</summary>
        ContextMenuModel? MenuFor(int index)
            => Track.RowMenu(Selection, index, _trackAt, s_noMembership, in s_menuOptions);

        /// <summary>The whole selection when the pressed row is part of it, else that row. A COPY everywhere — the chart is
        /// no playlist. Runs once, at promotion.</summary>
        DragPayload? DragPayloadFor(int index)
        {
            var slots = Owner.PopularSlots;
            if ((uint)index >= (uint)slots.Length) return null;
            var pressed = new Track(slots[index]);
            SelectionDrag.Indices(Selection, index, _dragIndices);
            var tracks = new List<Track>(_dragIndices.Count);
            for (int k = 0; k < _dragIndices.Count; k++)
            {
                int i = _dragIndices[k];
                if ((uint)i < (uint)slots.Length) tracks.Add(new Track(slots[i]));
            }
            if (tracks.Count == 0) tracks.Add(pressed);
            string uri = pressed.Uri.Text;
            return new DragPayload(DragKind.Track, uri, uri, pressed.Title, new EntityRef(EntityKind.Track, pressed.Slot),
                Tracks: tracks.ToArray(), ArtUrl: Controls.ArtUrl(pressed.ImageId));
        }

        /// <summary>The shelf's invoke (a Tap on an unmodified row once it is the only selection, Enter, double-click):
        /// play the chart FROM this row.</summary>
        void InvokeAt(int index)
        {
            var t = TrackAt(index);
            if (!t.IsValid) return;
            Track.Invoke(t, () => StartAt(index));
        }

        /// <summary>The selected tracks, in chart order — the selection lane's provider (called at render and at the "…" open).</summary>
        internal IReadOnlyList<Track> SelectedTracks()
        {
            var slots = Owner.PopularSlots;
            var picked = new List<Track>(Selection.SelectedCount);
            for (int r = 0; r < Selection.RangeCount; r++)
            {
                var (s, e) = Selection.GetRange(r);
                for (int i = s; i <= e && i < slots.Length; i++)
                    if (slots[i] > Table.None) picked.Add(new Track(slots[i]));
            }
            return picked;
        }
    }

    /// <summary>The chart's floating selection bar: a component of its own so selecting re-renders only it (the structure draws it).</summary>
    sealed class ChartBar : Component
    {
        readonly ChartHost _host;
        readonly Func<int, Element> _lane;
        readonly Func<IReadOnlyList<Track>> _tracks;
        readonly Action _exit, _selectAll;

        public ChartBar(ChartHost host)
        {
            _host = host;
            _tracks = host.SelectedTracks;
            _exit = host.Selection.DeselectAll;
            _selectAll = host.Selection.SelectAll;
            _lane = fit => Track.SelectionLane(fit,
                SelectionLaneArgs.ForTracks(host.Selection.SelectedCount, _tracks, _exit, _selectAll));
        }

        public override Element Render()
        {
            _ = _host.Selection.Version.Value;
            int n = _host.Selection.SelectedCount;
            return _host.Style!.Bar(SelectionBarRules.Visible(n, false) ? n : 0, _lane);
        }
    }

    // ══ 3. THE ROW ═══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record ChartRowProps(ChartHost Owner, int Index, ArtistPopularLayout.Tier Tier);

    /// <summary>Everything a row paints that can change after mount, as ONE value: the memo's equality is the row's
    /// render gate, so a table publish that leaves this row's track, its first featured artist and its playback state
    /// alone schedules nothing.</summary>
    readonly record struct ChartPresentation(int Index, int Slot, uint Version, Track.RowState State, int FeatSlot,
                                             uint FeatVersion, int FeatCount);

    /// <summary>One chart row: reads its track LIVE off the popular edge (ch 08 §1.2's live list), its tier from the
    /// re-pushed props. Single click selects, double click plays FROM THIS ROW with the chart's own list as the queue
    /// (<see cref="ChartHost.StartAt"/> — never by uri into the server's artist context), the # cell plays/pauses, the
    /// heart likes.</summary>
    sealed class ChartRow : Component
    {
        ChartRowProps? _latest;
        int _likeSlot;
        bool _likeSaved;
        readonly Func<ChartPresentation> _compute;
        readonly Action _play, _like;

        public ChartRow()
        {
            _compute = Compute;
            _play = Play;
            _like = Like;
        }

        ChartPresentation Compute()
        {
            // Reading the props here subscribes the memo to a re-pushed index/tier, not only the render.
            var p = UsePropsOrDefault<ChartRowProps>();
            if (p is null) return default;
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ArtistPopular.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Artists.Changed.Value;
            var t = p.Owner.TrackAt(p.Index);
            if (!t.IsValid) return new ChartPresentation(p.Index, 0, 0, default, 0, 0, 0);
            var st = Track.StateOf(t);
            var (featSlot, featCount) = FeatOf(t, p.Owner.Owner.Slot);
            uint featVersion = featSlot > 0 ? new Artist(featSlot).Version : 0;
            return new ChartPresentation(p.Index, t.Slot, t.Version, st, featSlot, featVersion, featCount);
        }

        public override Element Render()
        {
            var p = UseProps<ChartRowProps>();
            _latest = p;
            var hovered = UseSignal(false);
            // The shelf slot root's scope (the slot owns click, focus and keys; this content is click-less). Read BEFORE
            // the early return so the hook order never depends on the row being live.
            RowScope? slot = UseContext(ItemsView.SlotRow);
            var memo = UseComputed(_compute);
            var pr = memo.Value;
            if (pr.Slot <= 0) return new BoxEl();
            var t = new Track(pr.Slot);
            var owner = p.Owner;
            bool classic = owner.Classic;
            bool pop = Track.LikeEdge(ref _likeSlot, ref _likeSaved, t, pr.State.Saved);
            uint plays = t.PlayCount;
            // A pathfinder-thin track hit may carry no cover of its own (S3's LIVE-answer counterpart: an older
            // cached row, or a kind the pathfinder fix does not reach) — its album's is the same fallback the
            // pathfinder decoder now hands down at commit, applied here too for rows staged before that fix ran.
            StringId art = t.ImageId.IsEmpty ? t.Album.ImageId : t.ImageId;
            var cells = new ArtistChartCells(
                t.Title, Controls.ArtUrl(art), Track.Format.DurationCell(t.DurationMs),
                plays > 0 ? Strings.Detail.Row.PlaysFull(Track.Format.PlaysLabel(plays)) : "",
                plays > 0 ? Strings.Detail.Row.PlaysFull(plays.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)) : "",
                t.IsExplicit, t.HasVideo);
            return owner.Style!.Row(new ArtistChartRowArgs
            {
                Cells = cells, Index = p.Index, State = pr.State, Tier = p.Tier,
                Track = t, PageArtist = owner.Owner.Slot, FeatSlot = pr.FeatSlot, FeatCount = pr.FeatCount,
                OnPlay = _play, OnLike = t.IsValid ? _like : null, Pop = pop,
                Hovered = hovered, Slot = slot, ChecksVisible = owner.ChecksVisible,
                ShowArtwork = owner.ShowArtwork, Classic = classic,
            });
        }

        void Play()
        {
            if (_latest is not { } p) return;
            var t = p.Owner.TrackAt(p.Index);
            if (!t.IsValid) return;
            int at = p.Index;
            Track.Invoke(t, () => p.Owner.StartAt(at));
        }

        void Like()
        {
            if (_latest is not { } p) return;
            var t = p.Owner.TrackAt(p.Index);
            var me = User.Me;
            if (me.Slot <= 0 || !t.IsValid) return;
            if (me.Likes(t)) me.Unlike(t); else me.Like(t);
        }
    }

    /// <summary>The first featured artist and how many there are — only when the page artist is credited AND someone
    /// else is (repeating the page artist under every row is noise). Counts, allocates nothing.</summary>
    static (int FirstSlot, int Count) FeatOf(Track t, int pageArtist)
    {
        var slots = t.ArtistSlots;
        if (slots.Length == 0 || pageArtist <= 0) return (0, 0);
        bool pageIn = false;
        int count = 0, first = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == pageArtist) { pageIn = true; continue; }
            if (first == 0) first = slots[i];
            count++;
        }
        return pageIn && count > 0 ? (first, count) : (0, 0);
    }

    // ══ 4. THE PAGER ═════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record ChartPagerProps(ArtistChartStyle Style, int Page, int PageCount, bool CanPrev, bool CanNext, Action Prev, Action Next,
                                  Action<int> GoTo, int Total);

    /// <summary>‹ ●● › at two or more pages; the bare track count at one (W11). The pips are a CONTROLLED pager
    /// mirrored from the shelf's page through an effect; onChange AND onReselect both call GoTo — after a partial pan
    /// the strip rests between pages and the re-click is the request to be put back on the boundary.</summary>
    sealed class ChartPager : Component
    {
        readonly Signal<int> _selected = new(0);

        public override Element Render()
        {
            var p = UseProps<ChartPagerProps>();
            int page = p.Page;
            UseEffect(() => { if (_selected.Peek() != page) _selected.Value = page; }, DepKey.From(page));
            return p.Style.Pager(new ArtistChartPagerArgs
            {
                Page = page, PageCount = p.PageCount, CanPrev = p.CanPrev, CanNext = p.CanNext, Prev = p.Prev, Next = p.Next,
                GoTo = p.GoTo, Total = p.Total, Selected = _selected,
            });
        }
    }

}
