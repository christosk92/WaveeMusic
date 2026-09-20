// ── Entities/Artist.UI.Chart.cs ────────────────────────────────────────────────────────────────────────────────────
// The artist page's "Top tracks" CHART (0.2.9 Features/Detail/ArtistPopular.cs): ONE PagedShelf collection — a five-row
// virtual strip with page-mandatory snapping, at most two columns, a ‹ ●● › pip pager in the header — whose realized
// cells are chart ROW components keyed by track. The named partial ch 08 §9 and plan §2 ask for, inside Artist.UI.cs's
// 1,500-line budget.
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
// The only remount keys left are the ones that change a row's CHILD COUNT or the shelf's mount configuration:
//  · the cell key   "row:" + uri + "|art"/"|noart" + "|classic"/"|modern"  ("chart#" + i for an empty uri) — ch 08 audit #5;
//  · the shelf key  columns (1/2) × art × classic — `maxColumns` and `cardHeight` are PagedShelf ctor configuration.
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

    // Geometry (ArtistPopular.cs:88-108): Modern 56 rows on a 12 gap, Classic 48 rows on 8; the 264 card minimum is a
    // BREAKPOINT (two columns ⇔ a 540 chart column), and the maximum is uncapped on purpose.
    const float ChartRowH = 56f, ChartClassicRowH = 48f, ChartCellGap = 12f, ChartHeaderGap = 10f;
    const float ChartColBreakW = 540f;
    const float ChartMinCardW = (ChartColBreakW - ChartCellGap) / 2f;
    const float ChartEdgeFade = 16f;

    static readonly Func<float, float> s_chartModernH = static _ => ChartRowH;
    static readonly Func<float, float> s_chartClassicH = static _ => ChartClassicRowH;

    // columns 1/2 × art × classic
    static readonly string[] s_chartShelfKeys =
    [
        "chart-shelf:1:noart:modern", "chart-shelf:1:noart:classic", "chart-shelf:1:art:modern", "chart-shelf:1:art:classic",
        "chart-shelf:2:noart:modern", "chart-shelf:2:noart:classic", "chart-shelf:2:art:modern", "chart-shelf:2:art:classic",
    ];

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
        Element? _shelf;
        IOverlayService? _overlay;

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

        static readonly Func<int, int> s_noMembership = static _ => -1;
        static readonly Track.MenuOptions s_menuOptions = new(ShowGoToAlbum: true);

        public ChartHost()
        {
            _cardAt = Card;
            _keyOf = (_, i) => (uint)i < (uint)_keys.Length ? _keys[i] : "chart#" + i;
            _pager = ctx => Embed.Comp(
                new ChartPagerProps(ctx.Page, ctx.PageCount, ctx.CanPrev, ctx.CanNext, ctx.Prev, ctx.Next, ctx.GoTo, _items.Length),
                static () => new ChartPager());
            _pendingFn = () => !_ready && !_failed;
            _failedFn = () => _failed;
            _contentFn = () => _shelf ?? new BoxEl();
            _shimmerFn = () => ChartSkeleton(Owner.PopularSlots.Length, Classic, ShowArtwork);
            _failedPanelFn = () => new BoxEl
            {
                Direction = 1, Gap = ChartHeaderGap,
                Children =
                [
                    Controls.AccentHeader(Loc.Get(Strings.Artist.TopTracks), AccentSignal?.Peek() ?? Tok.AccentDefault),
                    Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: _retry),
                ],
            };
            _trackAt = TrackAt;
            _pillFill = Prop.Of(() => AccentSignal?.Value ?? Tok.AccentDefault);
            _retry = Retry;
            _demand = Demand;
            _demandTargets = DemandTargets;
            _recheckNow = Recheck;
        }

        public override Element Render()
        {
            var p = UseProps<ChartProps>();
            _overlay = UseContext(Overlay.Service);
            uint scopeEpoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ArtistPopular.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Tracks.Changed.Value;                  // the gate: every target Knows(TrackFields.Row)
            _ = _recheck.Value;                              // the deadline and the Retry re-read the marks through this
            Owner = p.A;
            AccentSignal = p.Accent;
            ColorF accent = p.Accent.Value;
            Classic = Prefs.Appearance.TrackRowStyle() == 1;
            ShowArtwork = !Prefs.Appearance.TrackArtworkHidden();

            // The chart's own asks (the same ones the page makes, so they dedupe to nothing) and the latch behind the
            // failure gate; the deadline re-arms with the artist and the scope, and `Recheck` keeps it alive while a
            // request is still out.
            UseEffect(_demand, DepKey.From(Owner.Slot, (int)scopeEpoch));
            UseEffect(_demandTargets);
            _deadline = UseTimeout(_recheckNow, RecheckMs, DepKey.From(Owner.Slot, (int)scopeEpoch));

            _ready = ArtistReadiness.Chart(Owner);
            _failed = !_ready && ChartFailedNow(scope);
            RefreshItems();
            Selection.ItemCount = _items.Length;

            int cols = ArtistSections.ChartColumns(_items.Length);
            int shelfKey = (cols - 1) * 4 + (ShowArtwork ? 2 : 0) + (Classic ? 1 : 0);
            Element shelf = PagedShelf.Create(
                _items,
                cardAt: _cardAt,
                cardHeight: Classic ? s_chartClassicH : s_chartModernH,
                header: Controls.AccentHeader(Loc.Get(Strings.Artist.TopTracks), accent),
                pager: ShelfPager.None,
                customPager: _pager,
                minCardW: ChartMinCardW,
                maxCardW: 9999f,
                gap: Classic ? Spacing.S : ChartCellGap,
                headerGap: Classic ? Spacing.S : ChartHeaderGap,
                rows: ArtistSections.ChartMaxRows,
                edgeFade: ChartEdgeFade,
                keyOf: _keyOf,
                maxColumns: cols,
                snap: ShelfSnap.Page,
                maxItems: ArtistSections.ChartMaxTracks);
            // A key on a component's ROOT is inert (ReconcileSingleChild pairs by type alone), so the keyed shelf is a
            // CHILD of a pass-through column (ch 08 §9 trap 2).
            _shelf = new BoxEl { Direction = 1, MinWidth = 0f, Children = [shelf with { Key = s_chartShelfKeys[shelfKey] }] };

            return new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new SkelRegionEl(
                        Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                        OnFailed: _failedPanelFn, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                        Group: null, SmoothResize: false),
                ],
            };
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
            Entities.RefreshEdge(FetchEdge.ArtistPopular, slot);
            Entities.Refresh(scope.Artists, new ReadOnlySpan<int>(in slot), (uint)ArtistFields.Chart);
            var popular = scope.Edges.ArtistPopular.Targets(slot);
            if (popular.Length > 0) Entities.Refresh(scope.Tracks, popular, (uint)(TrackFields.Row | TrackFields.Video));
            _deadline.Restart();
            _recheck.Value = _recheck.Peek() + 1;
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
            var row = new BoxEl
            {
                ZStack = true,
                // A vertical lift wins the drag; a horizontal sweep yields to the shelf's pan (the engine's arena).
                Draggable = Drag.Source(() => DragPayloadFor(at)),
                Children =
                [
                    content,
                    new BoxEl
                    {
                        Key = "row-pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                        Corners = classic ? CornerRadius4.All(Radii.None) : CornerRadius4.All(1.5f),
                        AlignSelf = FlexAlign.Center, Fill = _pillFill, HitTestVisible = false,
                        Opacity = Prop.Of(() => { _ = Selection.Version.Value; return Selection.IsSelected(at) ? 1f : 0f; }),
                    },
                ],
            };
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
            Track[] tracks;
            if (!Selection.IsSelected(index) || Selection.SelectedCount <= 1)
                tracks = [pressed];
            else
            {
                int n = Math.Min(slots.Length, Selection.ItemCount);
                tracks = new Track[Math.Min(n, Selection.SelectedCount)];
                int k = 0;
                for (int i = 0; i < n && k < tracks.Length; i++)
                    if (Selection.IsSelected(i)) tracks[k++] = new Track(slots[i]);
                if (k < tracks.Length) Array.Resize(ref tracks, k);
            }
            string uri = pressed.Uri.Text;
            return new DragPayload(DragKind.Track, uri, uri, pressed.Title, new EntityRef(EntityKind.Track, pressed.Slot),
                Tracks: tracks, ArtUrl: Controls.ArtUrl(pressed.ImageId));
        }

        /// <summary>Plain = replace, Ctrl = toggle, Shift = range from the anchor.</summary>
        internal void SelectRow(int index, KeyModifiers mods)
        {
            Selection.OnInteractedAction(index, (mods & KeyModifiers.Ctrl) != 0, (mods & KeyModifiers.Shift) != 0);
            if ((mods & KeyModifiers.Shift) == 0) Selection.AnchorIndex = index;
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
        readonly Action<PointerEventArgs> _released;

        public ChartRow()
        {
            _compute = Compute;
            _play = Play;
            _like = Like;
            _released = args =>
            {
                if (_latest is not { } p) return;
                if (args.ClickCount >= 2) Play();
                else p.Owner.SelectRow(p.Index, args.Mods);
            };
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
            var memo = UseComputed(_compute);
            var pr = memo.Value;
            if (pr.Slot <= 0) return new BoxEl();
            var t = new Track(pr.Slot);
            var owner = p.Owner;
            bool classic = owner.Classic;
            bool pop = Track.LikeEdge(ref _likeSlot, ref _likeSaved, t, pr.State.Saved);
            Element? feat = FeatLine(t, owner.Owner.Slot, pr.FeatCount, pr.FeatSlot);
            uint plays = t.PlayCount;
            // A pathfinder-thin track hit may carry no cover of its own (S3's LIVE-answer counterpart: an older
            // cached row, or a kind the pathfinder fix does not reach) — its album's is the same fallback the
            // pathfinder decoder now hands down at commit, applied here too for rows staged before that fix ran.
            StringId art = t.ImageId.IsEmpty ? t.Album.ImageId : t.ImageId;
            var cells = new ChartCells(
                t.Title, Controls.ArtUrl(art), Track.Format.DurationCell(t.DurationMs),
                plays > 0 ? Strings.Detail.Row.PlaysFull(Track.Format.PlaysLabel(plays)) : "",
                plays > 0 ? Strings.Detail.Row.PlaysFull(plays.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)) : "",
                t.IsExplicit, t.HasVideo);
            return ChartRowView(in cells, p.Index, pr.State, p.Tier, feat, _play, t.IsValid ? _like : null, pop,
                hovered, _released, owner.ShowArtwork, classic);
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

    /// <summary>"feat. X +N": the first featured name is a link; "+N" opens the credit flyout of the rest. The featured
    /// list is built only on the +N branch (§9 trap b).</summary>
    static Element? FeatLine(Track t, int pageArtist, int featCount, int firstSlot)
    {
        if (featCount == 0 || firstSlot <= 0) return null;
        var first = new Artist(firstSlot);
        var kids = new Element[featCount > 1 ? 3 : 2];
        kids[0] = Ui.Caption(Loc.Get(Strings.Artist.Feat)) with { Color = Tok.TextTertiary, Shrink = 0f };
        kids[1] = new SpanTextEl([new TextSpan(first.Name, OnClick: () => Track.GoToArtist(first))])
        {
            Size = Ui.Caption("").Size, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        if (featCount > 1)
        {
            var credits = t.ArtistSlots;
            Span<int> featured = featCount <= 32 ? stackalloc int[featCount] : new int[featCount];
            int k = 0;
            for (int i = 0; i < credits.Length && k < featured.Length; i++)
                if (credits[i] != pageArtist) featured[k++] = credits[i];
            kids[2] = Track.ArtistMoreButton(featured[..k], 1) with { Key = "featmore:" + firstSlot };
        }
        return new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Children = kids };
    }

    /// <summary>The strings a row paints — the live row's resolved values, or the skeleton's representative shape.</summary>
    readonly record struct ChartCells(string Title, string? ArtUrl, string Duration, string PlaysCompact, string PlaysExact,
                                      bool Explicit, bool Video);

    /// <summary>The prototype row (ArtistPopular.cs:365-522), shared by live rows and the skeleton so the two cannot
    /// drift. Modern: 56 tall, r6, a 1-DIP transparent border that paints on hover, NO resting fill — now-playing is
    /// content (the equalizer + the accent title). Classic: 48 tall, square, a hairline per row, and the now-playing
    /// accent spreads to the explicit mark, the dots, the video glyph, the plays and the duration (W10, parity 83).</summary>
    static Element ChartRowView(in ChartCells c, int index, in Track.RowState st, ArtistPopularLayout.Tier tier,
                                Element? feat, Action? onPlay, Action? onLike, bool pop, Signal<bool>? hovered,
                                Action<PointerEventArgs>? onReleased, bool showArtwork, bool classic)
    {
        bool classicNow = classic && st.IsNow;
        ColorF? nowInk = classicNow ? Tok.AccentTextPrimary : null;
        bool hasPlays = c.PlaysCompact.Length > 0;
        // The third line needs ALL THREE: a stacking tier, a feat credit and a play count (ch 08 audit #17).
        bool stacked = tier.StackSub && feat is not null && hasPlays;
        bool playsInSub = hasPlays && !stacked;

        int parts = (c.Explicit ? 1 : 0) + (c.Video ? 1 : 0) + (feat is not null ? 1 : 0) + (playsInSub ? 1 : 0);
        var sub = parts == 0 ? Array.Empty<Element>() : new Element[parts * 2 - 1];
        int n = 0;
        if (c.Explicit) sub[n++] = classic ? Track.ClassicExplicitBadge(nowInk) : Controls.ExplicitBadge(14f);
        if (c.Video)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            sub[n++] = Icon(Icons.Movie, 13f, nowInk ?? Tok.TextTertiary);
        }
        if (feat is not null)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            sub[n++] = feat;
        }
        if (playsInSub)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            // ONE format: compact in the row, the exact count in the tooltip — never squeezing the feat credit (§9 #10).
            sub[n++] = ToolTip.Wrap(Ui.Caption(c.PlaysCompact) with
            {
                Color = nowInk ?? Tok.TextTertiary, MaxLines = 1, Shrink = 0f,
            }, c.PlaysExact);
        }

        var title = new TextEl(c.Title)
        {
            Size = 14f, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element subLine = sub.Length > 0
            ? new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub }
            : new BoxEl();
        Element[] mid = stacked
            ? [title, subLine, Ui.Caption(c.PlaysExact) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f }]
            : [title, subLine];

        var trail = new Element[tier.ShowDuration ? 2 : 1];
        trail[0] = Track.Heart(st.Saved, onLike, pop, classic);
        if (tier.ShowDuration)
            trail[1] = Design.Type.DenseMeta(c.Duration) with { Color = nowInk ?? Tok.TextSecondary };

        var rowChildren = new Element[showArtwork ? 4 : 3];
        int child = 0;
        rowChildren[child++] = new BoxEl
        {
            Width = 24f, Height = 24f, Shrink = 0f,
            Children = [Track.NumberCell(index, in st, onPlay, hovered, chartStatus: 0, classic: classic)],
        };
        // TRACK ARTWORK HIDDEN: the cell is not built at all (rowChildren is 3 long), the mid column takes the width.
        if (showArtwork)
            rowChildren[child++] = new BoxEl
            {
                Width = tier.Art, Height = tier.Art, Shrink = 0f,
                Children = [Controls.Artwork(c.ArtUrl, tier.Art, tier.Art, Radii.Control, decodePx: 64)],
            };
        rowChildren[child++] = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Gap = classic ? Spacing.XS : 1f, Justify = FlexJustify.Center,
            Children = mid,
        };
        rowChildren[child] = new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail };

        float rowHeight = classic ? ChartClassicRowH : ChartRowH;
        var body = new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = rowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = classic ? new Edges4(Spacing.XS, 0f, Spacing.XS, 0f) : new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children = rowChildren,
        };

        return new BoxEl
        {
            ZStack = true, MinHeight = rowHeight, MinWidth = 0f, ClipToBounds = classic,
            Corners = classic ? CornerRadius4.All(Radii.None) : CornerRadius4.All(6f),
            Fill = ColorF.Transparent,
            HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
            PressScale = Design.Motion.ScaleSubtle.Press,
            BorderWidth = classic ? 0f : 1f, BorderColor = ColorF.Transparent,
            HoverBorderColor = classic ? ColorF.Transparent : Tok.StrokeCardDefault,
            Role = AutomationRole.Button,
            // Single click SELECTS, double click plays; with no selection model behind it (the skeleton) a click invokes.
            OnClick = onReleased is null ? onPlay : null,
            OnPointerReleased = onReleased,
            // Real hover edges: the PointerBit every HoverOpacity descendant inherits AND the equalizer's pause gate.
            OnHoverMove = hovered is null ? null : _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = hovered is null ? null : () => { if (hovered.Peek()) hovered.Value = false; },
            Children = classic
                ? [body, new BoxEl
                  {
                      Key = "classic-hairline", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch,
                      Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                  }]
                : [body],
        };
    }

    static Element ChartDot(ColorF? ink) => Ui.Caption("·") with { Color = ink ?? Tok.TextTertiary, Shrink = 0f };

    /// <summary>The chart's derived-skeleton source: the SAME row builder over a representative shape, at the seed's
    /// column count (≤ 10 charted ⇒ 2 × 5; an empty edge assumes the overview's ten, W5).</summary>
    internal static Element ChartSkeleton(int knownCount, bool classic, bool showArtwork)
    {
        int total = knownCount > 0 ? Math.Min(knownCount, ArtistPopularTracks.OverviewSeedCap) : ArtistPopularTracks.OverviewSeedCap;
        int cols = ArtistSections.ChartColumns(total);
        int perCol = Math.Min(ArtistSections.ChartMaxRows, Math.Max(1, (total + cols - 1) / cols));
        var tier = classic ? ArtistPopularLayout.Tier.Classic(true) : new ArtistPopularLayout.Tier(44f, true, false);
        var cells = new ChartCells("Top track title", null, "3:30", "1.2M", "1,234,567", false, false);
        var state = default(Track.RowState);
        var columns = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int rows = Math.Min(perCol, Math.Max(0, total - c * perCol));
            var kids = new Element[rows];
            for (int r = 0; r < rows; r++)
                kids[r] = ChartRowView(in cells, c * perCol + r, in state, tier, null, null, null, false, null, null,
                                       showArtwork, classic);
            columns[c] = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = classic ? Spacing.S : ChartCellGap, Children = kids,
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = classic ? Spacing.S : ChartHeaderGap,
            Children =
            [
                // The header at its NATURAL width, so the derived bar is title-wide in both trees.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Controls.AccentHeader(Loc.Get(Strings.Artist.TopTracks), Tok.AccentDefault)],
                },
                new BoxEl { Direction = 0, Gap = classic ? Spacing.S : ChartCellGap, Children = columns },
            ],
        };
    }

    // ══ 4. THE PAGER ═════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record ChartPagerProps(int Page, int PageCount, bool CanPrev, bool CanNext, Action Prev, Action Next,
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
            if (p.PageCount <= 1)
                return Ui.Caption(p.Total.ToString(System.Globalization.CultureInfo.CurrentCulture))
                    with { Weight = 600, Color = Tok.TextTertiary };
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Children =
                [
                    ChartChevron(Icons.ChevronLeft, p.CanPrev, p.Prev),
                    PipsPager.Create(p.PageCount, _selected, onChange: p.GoTo, onReselect: p.GoTo),
                    ChartChevron(Icons.ChevronRight, p.CanNext, p.Next),
                ],
            };
        }
    }

    /// <summary>The chart's own 28-DIP transparent chevron (not the stock 32 filled circle); a disabled one has no hover
    /// fill and a tertiary glyph.</summary>
    static Element ChartChevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), OnClick = enabled ? onClick : null,
        Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : null,
        Children = [Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary)],
    };
}
