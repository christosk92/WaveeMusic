// ── Entities/Recents.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Recents page's CORE HOST: the model (the shape, the demand, the adoption, the midnight rollover, the refresh after a play),
// the drawer accordion's state, the sticky band's and the rail's arithmetic, the viewport-following page accent and its ONE
// wash leg, the playlist bylines' owner demand and every verb
//
// Role: CORE (the host; the picture is the structure's)
// Owner: P
// Wave: 5
// Spec: ch 16 §0, §1.1/§1.3 (the tree and the props-freeze map), §2 W1-W4, W8-W12, W14-W15, W22, W24-W25, §4.1-§4.2,
//   §5, §6.1, §7.1 (demand on mount), §9.1 #1-#4, #10-#17, §9.2
//
// T10c2 (themes plan 6.1): the masthead, the pivots, the grouped list with its pinned day band, the rows, the drawer, the rail
// and the calendar are CLASSIC'S (`Themes\Classic\Structure\Views\Pages\ClassicRecents*.cs`, `Pages.Recents`). `PageView` builds a
// `RecentsPageProps` per render and mounts the key; the structure's slot components read the host through `IRecentsHost`.
//
// THE MODEL. The page demands the recents relation ONCE on mount (`Entities.EnsureEdge(FetchEdge.Recents, me)`) and
// reads the edge; every row entity the page shows is demanded in ONE batched `Entities.Ensure` per kind when a
// publication is adopted (`RecentsLayout.DemandSlots`); playlist bylines through the owner leaf; a drawer's members when
// it opens. No visible-window fetching anywhere (ch 16 §7.1, contract §8).
//
// ONE SHAPE, SWAPPED ATOMICALLY (§9.1 #1). The snapshot, the pivot's cut, the morph flags, the sections, the calendar and
// the STATEFUL measured layout live on one immutable `RecentsShape`; `BuildShape` publishes it in 0.2.9's order (build →
// reuse-or-build the layout → prime it → publish → reset the drawer → re-resolve the sticky band → today in the
// readout → `ShapeEpoch` LAST). Two epochs: `Epoch` = the same list, new words (midnight); `ShapeEpoch` = a different
// list (Key remount of the list, the rail, the sticky band, the pivots and the calendar). A publication whose snapshot
// is value-equal to the adopted one changes NOTHING, so an entity-table or no-op publish never remounts a row.
//
// PROPS FREEZE AT MOUNT. The row slot is a BOUND slot (`ItemsView.CreateBound` + the scope's item signal); its handlers
// are built once per slot and resolve the current item at click time. The accent reaches every consumer as a bound
// `Prop` over pre-created thunks; the ONE cover-grading `Watch` lives in the 0×0 `AccentBinder` leaf, which also
// publishes the wash (§9.2: the page's Render never subscribes to a grading).
//
// THE MASTHEAD IS THE PAGE'S OWN. `Shell.TryMasthead` answers only the browse/home-section/concert families, so Recents
// (a root, no trail) keeps 0.2.9's hand-rolled SurfaceDisplay hero; it publishes nothing to `Shell.Mastheads` (a
// publication the band never reads would be a second copy waiting to double-expose).
//
// ZERO ALLOCATION ON THE SCROLL PATH. The sticky watch over the list's scroll handle (`WatchSticky`/`UpdateSticky`) is
// arithmetic, three change-gated signal writes and one pre-created posted delegate; per-row strings (when, uri, morph key, the
// joined subtitle) are cached on the shape, and the count phrases per count on the page.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Recents
{
    // MOUNT POINT (Wave 5 contract §5)
    /// <summary>The <c>recents</c> route's page (<see cref="Shell.RouteKind.Recents"/>). The route carries no data; the
    /// page reads the signed-in account's relation itself.</summary>
    public static Element PageFor(in Shell.Route route) => Embed.Comp(static () => new PageView());

    // ══ 2. THE PAGE (the core host: the model, the demand, the verbs; Classic draws it) ══════════════════════════════

    /// <summary>The page's CORE HOST. It builds a <see cref="RecentsPageProps"/> per render and mounts <see cref="ViewKeys.RecentsPage"/>; the structure's
    /// slot components read its live state through <see cref="IRecentsHost"/>.</summary>
    [CoreHost]
    internal sealed class PageView : Component, IRecentsHost
    {
        static readonly Func<int, string> s_itemCount = static n => Strings.Recents.ItemCount(n);
        static readonly Func<int, string> s_groupedFrom = static n => Strings.Recents.GroupedFrom(n);

        // ── signals (§1.3's props-freeze map) ──
        internal readonly Signal<int> Epoch = new(0), ShapeEpoch = new(0);
        internal readonly Signal<string?> Chip = new(null);
        internal readonly Signal<StringId> ExpandedRow = new(StringId.Empty);
        internal readonly Signal<int> StickyHeader = new(-1), RailMeasuredVersion = new(0), AccentDay = new(-1);
        internal readonly Signal<float> StickyPush = new(0f);
        internal readonly Signal<Design.PageAccent> Accent;
        internal readonly Signal<DateOnly> CalendarDay;
        internal readonly Signal<RecentsShape> ShapeSignal;

        /// <inheritdoc />
        public Signal<bool> IsZoomedOut { get; } = new(false);
        /// <inheritdoc />
        public ItemsViewController ListController { get; } = new();
        /// <inheritdoc />
        public ItemsViewController CalendarController { get; } = new();
        /// <inheritdoc />
        public AnnotatedScrollBarController ScrollController { get; } = new();
        /// <inheritdoc />
        public SemanticZoomController ZoomController { get; } = new();
        internal readonly object WashOwner = new();
        internal HomeWash? LastWash;

        /// <inheritdoc />
        public RecentsShape ShapeNow { get; private set; }
        readonly RecentsShape _pendingShape;
        /// <inheritdoc />
        public CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;
        internal DateTimeOffset Now = DateTimeOffset.Now;
        /// <inheritdoc />
        public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
        /// <inheritdoc />
        public string ScrollScope { get; private set; } = "";

        int _expandedRow = -1;
        int _pendingAccentDay = -1;
        bool _accentArmed;
        EntityId _refreshedFor;
        Action<Action> _post = static a => a();
        readonly List<int> _demand = new(128);
        readonly HashSet<int> _seen = new();
        string?[] _countLabels = new string?[64], _playedLabels = new string?[64], _savedLabels = new string?[64];

        // ── pre-created delegates: nothing on a hot path allocates a closure ──
        /// <inheritdoc />
        public Prop<ColorF> AccentInkProp { get; }
        /// <inheritdoc />
        public Prop<ColorF> AccentFillProp { get; }
        readonly Prop<ColorF>[] _densityFills = new Prop<ColorF>[6];
        /// <inheritdoc />
        public Prop<Affine2D> StickyTransform { get; }
        internal readonly Action DemandOwnersAction;
        /// <inheritdoc />
        public Action ResetCalendarDayAction { get; }
        /// <inheritdoc />
        public Action<WheelEventArgs> ForwardWheelAction { get; }
        readonly Action _resolveAccentDay, _rollover, _refresh, _ensureEdge, _adopt, _activated, _deactivated, _openOverview, _retry;
        readonly Action<KeyEventArgs> _onKeyDown;
        /// <summary>The sticky band's watch over the list's scroll handle (<see cref="ScrollController"/>'s): mounted by the list surface,
        /// it re-runs on every published offset/extent and writes only on a projected change.</summary>
        public Action WatchStickyAction { get; }
        long _stickyKey = long.MinValue;
        /// <inheritdoc />
        public Func<int, int> MapInToOutFunc { get; }
        /// <inheritdoc />
        public Func<int, int> MapOutToInFunc { get; }
        /// <inheritdoc />
        public Func<int, int> ContentTypeFunc { get; }
        /// <inheritdoc />
        public Action<int, RecentsFlatItem> InvokeFlatAction { get; }
        /// <inheritdoc />
        public Func<int, RecentsFlatItem, string> TextForFunc { get; }
        /// <inheritdoc />
        public Func<AnnotatedScrollBarLabel[]> RailLabelsFunc { get; }
        /// <inheritdoc />
        public Func<float[]> RailTicksFunc { get; }
        /// <inheritdoc />
        public Func<float, AnnotatedScrollBarLabel?> RailDetailFunc { get; }
        readonly Func<bool> _pending, _failed;
        readonly Func<AccentBinder> _binder;
        readonly Func<OwnerDemand> _owners;

        IReadSignal<RecentsShape> IRecentsHost.ShapeSignal => ShapeSignal;
        RecentsShape IRecentsHost.PendingShape => _pendingShape;
        IReadSignal<int> IRecentsHost.Epoch => Epoch;
        IReadSignal<int> IRecentsHost.ShapeEpoch => ShapeEpoch;
        IReadSignal<string?> IRecentsHost.Chip => Chip;
        IReadSignal<StringId> IRecentsHost.ExpandedRow => ExpandedRow;
        IReadSignal<int> IRecentsHost.StickyHeader => StickyHeader;
        IReadSignal<int> IRecentsHost.RailMeasuredVersion => RailMeasuredVersion;
        IReadSignal<Design.PageAccent> IRecentsHost.Accent => Accent;
        IReadSignal<DateOnly> IRecentsHost.CalendarDay => CalendarDay;

        public PageView()
        {
            // The skeleton's seed rows are stamped off the SAME local clock that groups them: one "Today", never two.
            var seed = RecentsView.PendingSeedRows(Now);
            var map = RecentsView.Filter(seed, null);
            var sections = RecentsView.BuildSections(seed, map, Now, Culture);
            var layout = new GroupedListVirtualLayout(sections.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
            layout.ContentExtent(sections.Items.Length, 0f);
            _pendingShape = new RecentsShape(seed, map, RecentsView.FirstOccurrence(seed), sections,
                RecentsView.DayDensity(seed, map, Now, Culture), layout, Now.Offset, loaded: false, summary: "");
            ShapeNow = _pendingShape;
            ShapeSignal = new Signal<RecentsShape>(_pendingShape);
            Accent = new Signal<Design.PageAccent>(FallbackAccent());
            CalendarDay = new Signal<DateOnly>(Today);

            AccentInkProp = Prop.Of(() => Accent.Value.Ink);
            AccentFillProp = Prop.Of(() => Accent.Value.Fill);
            _densityFills[0] = ColorF.Transparent;
            for (int level = 1; level <= 5; level++)
            {
                int l = level;
                // The heat's paint: the accent INK at A_subtle + (ink.A − A_subtle)·level/5 (§4.3), bound so a crossing
                // glides the whole heatmap without one cell re-rendering.
                _densityFills[level] = Prop.Of(() => DensityFillOf(l, Accent.Value.Ink));
            }
            StickyTransform = Prop.Of(() => Affine2D.Translation(0f, StickyPush.Value));
            ResetCalendarDayAction = () => CalendarDay.Value = Today;
            DemandOwnersAction = DemandOwners;
            ForwardWheelAction = e =>
            {
                // The same signed DIP the viewport consumes, animated like wheel over the body — never negated or snapped.
                ScrollController.ScrollBy(e.Delta, animate: true);
                e.Handled = true;
            };
            _resolveAccentDay = ResolveAccentDay;
            _rollover = RolloverMidnight;
            _refresh = RefreshAfterPlay;
            _ensureEdge = EnsureEdge;
            _adopt = Adopt;
            _activated = OnActivated;
            _deactivated = OnDeactivated;
            _openOverview = OpenOverviewFromMasthead;
            _retry = () => { if (Recents.Me.Slot > 0) Entities.RefreshEdge(FetchEdge.Recents, Recents.Me.Slot); };
            _onKeyDown = e =>
            {
                if (e.Handled || e.KeyCode != Keys.Escape || !IsZoomedOut.Peek()) return;
                ZoomController.ZoomInTo(-1);
                e.Handled = true;
            };
            WatchStickyAction = WatchSticky;
            MapInToOutFunc = i => RecentsLayout.MapInToOut(ShapeNow.Sections, ShapeNow.Calendar, i);
            MapOutToInFunc = i => RecentsLayout.MapOutToIn(ShapeNow.Sections, ShapeNow.Calendar, CalendarDay.Peek(), i);
            ContentTypeFunc = i => RecentsLayout.ContentTypeOf(ShapeNow.Sections, ShapeNow.Rows, i);
            InvokeFlatAction = (_, item) => InvokeFlat(item);
            TextForFunc = (_, item) => TextFor(item);
            RailLabelsFunc = RailLabels;
            RailTicksFunc = RailTicks;
            RailDetailFunc = RailDetail;
            _pending = () => { _ = ShapeEpoch.Value; _ = Recents.Changed.Value; return !ShapeNow.Loaded && Recents.Me.Readiness != EdgeState.Failed; };
            _failed = () => { _ = ShapeEpoch.Value; _ = Recents.Changed.Value; return !ShapeNow.Loaded && Recents.Me.Readiness == EdgeState.Failed; };
            _binder = () => new AccentBinder(this);
            _owners = () => new OwnerDemand(this);
        }

        public override Element Render()
        {
            _post = UsePost();
            _ = Entities.ScopeEpoch.Value;
            _ = Recents.Changed.Value;
            var me = Recents.Me;
            uint version = me.Version;
            var readiness = me.Readiness;
            ScrollScope = UseContext(Shell.PageScrollScope);

            // The ONE demand, on mount (and on an account switch); a publication is adopted in the same flush.
            UseEffect(_ensureEdge, DepKey.From((long)me.Slot));
            UseLayoutEffect(_adopt, DepKey.From(((long)version << 8) | (byte)readiness, (long)me.Slot));

            // Midnight: the day words go stale while the grouping stays (an Epoch bump, never a remount).
            UseTimeout(_rollover, RecentsLayout.RolloverDelayMs(DateTimeOffset.Now), DepKey.From(DateOnly.FromDateTime(DateTime.Now).DayNumber));
            // A now-playing identity change re-asks the relation 2 s later (a skip burst collapses to one ask).
            UseTimeout(_refresh, 2000f, DepKey.From((long)Playback.CurrentId.Value.GetHashCode()));
            UseActivation(onActivated: _activated, onDeactivated: _deactivated);

            int shapeEpoch = ShapeEpoch.Value;
            _ = Epoch.Value;
            var shape = ShapeNow;

            // The page PROVIDES its accent (the view wraps the tree): the equalizer, the # cell and the save button read "this page's accent"
            // through the context instead of knowing they sit in Recents (§0 #16).
            return ViewSlots.View(ViewKeys.RecentsPage, new RecentsPageProps
            {
                Host = this,
                Summary = shape.Summary,
                ShapeEpoch = shapeEpoch,
                Pending = _pending,
                Failed = _failed,
                Retry = _retry,
                OpenOverview = _openOverview,
                OnKeyDown = _onKeyDown,
                PageAccent = Accent,
                AccentBinder = Embed.Comp(_binder) with { Key = "recents-accent" },
                OwnerDemand = Embed.Comp(_owners) with { Key = "recents-owners" },
            });
        }

        // ── the model ────────────────────────────────────────────────────────────────────────────────────────────────

        void EnsureEdge()
        {
            int slot = Recents.Me.Slot;
            if (slot > 0) Entities.EnsureEdge(FetchEdge.Recents, slot);
        }

        /// <summary>Adopt an answered publication: rebuild only when the snapshot actually differs, then demand its rows.</summary>
        void Adopt()
        {
            var me = Recents.Me;
            if (me.Slot <= 0 || me.State == EdgeState.Unknown) return;
            var snapshot = RecentsSnapshot.Of(in me);
            if (ShapeNow.Loaded && snapshot.SameAs(ShapeNow.Rows)) return;
            _refreshedFor = Playback.CurrentId.Peek();
            // A pivot the new publication no longer has a row for falls back to All: an enabled tab never shows nothing.
            string? token = Chip.Peek();
            if (token is not null && !RecentsView.PivotAvailable(snapshot, token)) Chip.Value = token = null;
            BuildShape(snapshot, loaded: true, token);
            Demand(snapshot);
        }

        /// <summary>The single build-and-publish chokepoint (§9.1 #1), in 0.2.9's order.</summary>
        void BuildShape(RecentsSnapshot rows, bool loaded, string? token)
        {
            Now = DateTimeOffset.Now;
            Culture = CultureInfo.CurrentCulture;
            var map = RecentsView.Filter(rows, token);
            var sections = RecentsView.BuildSections(rows, map, Now, Culture);
            var calendar = RecentsView.DayDensity(rows, map, Now, Culture);
            var previous = ShapeNow;
            // An open drawer's cached extent may sit at an unrealized index: normalize before deciding reuse (§9.1 #4).
            ResetExpandedExtent(previous, _expandedRow);
            _expandedRow = -1;
            bool sameRows = ReferenceEquals(rows, previous.Rows);
            var layout = sameRows && sections.Items.AsSpan().SequenceEqual(previous.Sections.Items)
                ? previous.Layout
                : new GroupedListVirtualLayout(sections.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
            layout.ContentExtent(sections.Items.Length, 0f);
            string summary = loaded ? RecentsView.Summary(rows, Now, Culture, s_itemCount, s_groupedFrom) : "";
            ShapeNow = new RecentsShape(rows, map, sameRows ? previous.Morphable : RecentsView.FirstOccurrence(rows), sections, calendar,
                layout, Now.Offset, loaded, summary);
            ShapeSignal.Value = ShapeNow;
            ExpandedRow.Value = StringId.Empty;
            // Re-resolve the pinned band from the live offset: a re-cut mid-list never leaves a blank band (W24).
            UpdateSticky((float)ScrollController.Offset.Peek(), (float)ScrollController.ViewportLength.Peek());
            CalendarDay.Value = Today;
            ShapeEpoch.Value = ShapeEpoch.Peek() + 1;
        }

        public void SelectPivot(string? token)
        {
            if (string.Equals(token, Chip.Peek(), StringComparison.Ordinal)) return;
            Chip.Value = token;
            // CLIENT-SIDE, always: a pivot re-cuts the adopted snapshot and never reaches the network.
            BuildShape(ShapeNow.Rows, ShapeNow.Loaded, token);
        }

        void RolloverMidnight()
        {
            var now = DateTimeOffset.Now;
            Now = now;
            var s = ShapeNow;
            // A moved UTC offset (DST, a zone change) can move which day a stamp near midnight belongs to: full re-cut.
            if (s.BuiltOffset != now.Offset) { BuildShape(s.Rows, s.Loaded, Chip.Peek()); return; }
            ShapeNow = s.Relabeled(RecentsView.Relabel(s.Sections, now, Culture),
                s.Loaded ? RecentsView.Summary(s.Rows, now, Culture, s_itemCount, s_groupedFrom) : "");
            ShapeSignal.Value = ShapeNow;
            Epoch.Value = Epoch.Peek() + 1;
        }

        void RefreshAfterPlay()
        {
            var playing = Playback.CurrentId.Peek();
            if (!ShapeNow.Loaded || playing == _refreshedFor || Recents.Me.Slot <= 0) return;
            _refreshedFor = playing;
            Entities.RefreshEdge(FetchEdge.Recents, Recents.Me.Slot, FetchPriority.Prefetch);
        }

        void OnActivated()
        {
            // While row render effects are live, so the first return frame cannot replay the old drawer (§9.2).
            CollapseExpanded();
            if (ShapeNow.Loaded && Recents.Me.Slot > 0) Entities.RefreshEdge(FetchEdge.Recents, Recents.Me.Slot, FetchPriority.Prefetch);
        }

        void OnDeactivated()
        {
            // KeepAlive has begun parking: erase the cached drawer geometry, leave the signal for OnActivated.
            ResetExpandedExtent(ShapeNow, _expandedRow);
            _expandedRow = -1;
        }

        /// <summary>The mount demand: one batched Ensure per kind over the rows the page shows (contract §8).</summary>
        void Demand(RecentsSnapshot rows)
        {
            var scope = Entities.Current;
            DemandKind(rows, EntityKind.Track, scope.Tracks, (uint)TrackFields.Row);
            DemandKind(rows, EntityKind.Episode, scope.Episodes, (uint)EpisodeFields.Row);
            DemandKind(rows, EntityKind.Playlist, scope.Playlists, (uint)PlaylistFields.Row);
            DemandKind(rows, EntityKind.Album, scope.Albums, (uint)AlbumFields.Identity);
            DemandKind(rows, EntityKind.Artist, scope.Artists, (uint)ArtistFields.Identity);
            DemandKind(rows, EntityKind.Show, scope.Shows, (uint)ShowFields.Identity);
        }

        void DemandKind(RecentsSnapshot rows, EntityKind kind, Table table, uint fields)
        {
            _demand.Clear();
            RecentsLayout.DemandSlots(rows, kind, _demand, _seen);
            if (_demand.Count > 0) Entities.Ensure(table, CollectionsMarshal.AsSpan(_demand), fields);
        }

        /// <summary>The playlist bylines: owners whose name has not landed (run by the owner leaf per playlist publication).</summary>
        void DemandOwners()
        {
            var rows = ShapeNow.Rows;
            if (rows.Count == 0) return;
            var scope = Entities.Current;
            _demand.Clear();
            _seen.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows.Targets[i];
                if (t.Kind != EntityKind.Playlist || t.Slot <= 0 || t.Slot >= scope.Playlists.Count) continue;
                int owner = scope.Playlists.Owner[t.Slot];
                if (owner > 0 && !scope.Users.Knows(owner, (uint)UserFields.Identity) && _seen.Add(owner)) _demand.Add(owner);
            }
            if (_demand.Count > 0) Entities.Ensure(scope.Users, CollectionsMarshal.AsSpan(_demand), (uint)UserFields.Identity);
        }

        // ── the accordion (§9.1 #3-#4) ──────────────────────────────────────────────────────────────────────────────

        public void ToggleExpanded(int r)
        {
            var shape = ShapeNow;
            if ((uint)r >= (uint)shape.Rows.Count) return;
            var id = shape.Rows.Rows[r].ItemId;
            if (id.IsEmpty || shape.Sections.RowToFlat[r] < 0) return;
            bool closing = ExpandedRow.Peek() == id;
            // An OFF-SCREEN drawer has no live node to report its collapse: snap its extent first. A realized one rides
            // the drawer's exit reveal (Design.Reveal.Drawer) back to 64.
            if (!IsFlatRealized(shape, _expandedRow)) ResetExpandedExtent(shape, _expandedRow);
            _expandedRow = closing ? -1 : r;
            ExpandedRow.Value = closing ? StringId.Empty : id;
            if (closing) return;
            // The drawer's members, when it opens (what the drawer shows is demanded when it shows it).
            _demand.Clear();
            var kids = RecentsView.DrawerTargets(shape.Rows, r);
            for (int i = 0; i < kids.Length; i++) if (kids[i].Kind == EntityKind.Track && kids[i].Slot > 0) _demand.Add(kids[i].Slot);
            if (_demand.Count > 0) Entities.Ensure(Entities.Current.Tracks, CollectionsMarshal.AsSpan(_demand), (uint)TrackFields.Row);
        }

        void CollapseExpanded()
        {
            ResetExpandedExtent(ShapeNow, _expandedRow);
            _expandedRow = -1;
            if (!ExpandedRow.Peek().IsEmpty) ExpandedRow.Value = StringId.Empty;
        }

        void ResetExpandedExtent(RecentsShape shape, int r)
        {
            var rowToFlat = shape.Sections.RowToFlat;
            if ((uint)r >= (uint)rowToFlat.Length) return;
            int flat = rowToFlat[r];
            if ((uint)flat >= (uint)shape.Sections.Items.Length || shape.Sections.Items[flat].Kind != RecentsFlatItemKind.Row) return;
            shape.Layout.ContentExtent(shape.Sections.Items.Length, 0f);
            // The controller's atomic seam preserves the visible anchor and rebases in-flight scroll intent; a shape that
            // is not mounted has no viewport to anchor.
            if (!ListController.CorrectMeasuredExtent(shape.Layout, flat, RecentsLayout.RowHeight))
                shape.Layout.SetMeasured(flat, RecentsLayout.RowHeight, 0f);
        }

        bool IsFlatRealized(RecentsShape shape, int r)
        {
            var rowToFlat = shape.Sections.RowToFlat;
            return (uint)r < (uint)rowToFlat.Length && rowToFlat[r] >= 0 && ListController.IsItemRealized(rowToFlat[r]);
        }

        // ── the verbs ────────────────────────────────────────────────────────────────────────────────────────────────

        void InvokeFlat(RecentsFlatItem item)
        {
            if (item.Kind != RecentsFlatItemKind.Row) return;
            if (RecentsView.CanExpand(ShapeNow.Rows, item.OriginalRowIndex)) ToggleExpanded(item.OriginalRowIndex);
            else if (RecentsLayout.UsesTrackArm(ShapeNow.Rows, item.OriginalRowIndex)) Play(item.OriginalRowIndex);
            else Open(item.OriginalRowIndex);
        }

        string TextFor(RecentsFlatItem item)
        {
            var s = ShapeNow;
            if (item.Kind == RecentsFlatItemKind.DateHeader)
                return (uint)item.DayIndex < (uint)s.Sections.HeaderLabels.Length ? s.Sections.HeaderLabels[item.DayIndex] : "";
            return (uint)item.OriginalRowIndex < (uint)s.Rows.Count ? FactsOf(RecentsView.EntitySlotOf(s.Rows, item.OriginalRowIndex)).Title : "";
        }

        /// <summary>Open a row's context through the one route composer; a playable plays, Liked Songs is its own route.</summary>
        public void Open(int r)
        {
            var target = RecentsView.EntitySlotOf(ShapeNow.Rows, r);
            if (!RecentsView.Names(target)) return;
            if (target.Kind == EntityKind.Collection) { Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked)); return; }
            if (target.Kind is EntityKind.Track or EntityKind.Episode) { Playback.PlayContext(target.Id); return; }
            var route = Shell.For(new EntityUri(target.Id), FactsOf(target).Title);
            if (!route.IsNone) Shell.GoTo(route);
        }

        /// <summary>The cover FAB: a track or episode plays itself; anything else starts as a CONTEXT from the top.</summary>
        public void Play(int r)
        {
            var target = RecentsView.EntitySlotOf(ShapeNow.Rows, r);
            if (target.Kind == EntityKind.Collection) Playback.PlayOrToggleContext(EntityUri.LikedCollection);
            else if (!target.IsNone) Playback.PlayOrToggleContext(target.Id);
        }

        /// <summary>The container menu (W16): the strip over the container verbs, then the rows, under the card's header.</summary>
        public ContextMenuModel? MenuFor(int r)
        {
            var shape = ShapeNow;
            if ((uint)r >= (uint)shape.Rows.Count) return null;
            var target = RecentsView.EntitySlotOf(shape.Rows, r);
            var facts = FactsOf(target);
            var uri = target.Kind == EntityKind.Collection ? EntityUri.Parse(EntityUri.LikedCollection.AsSpan()) : new EntityUri(target.Id);
            ActionTarget at;
            switch (target.Kind)
            {
                case EntityKind.Album: at = ActionTarget.ForAlbum(uri, facts.Title); break;
                case EntityKind.Artist: at = ActionTarget.ForArtist(uri, facts.Title); break;
                case EntityKind.Playlist or EntityKind.Collection: at = ActionTarget.ForPlaylist(uri, facts.Title); break;
                default: return null;
            }
            string sub = SubtitleOf(shape, r, in shape.Rows.Rows[r], facts.Subtitle);
            bool liked = target.Kind == EntityKind.Collection;
            return Menus.Container(in at, facts.Cover, sub,
                new ContainerExtras { Liked = liked, HeaderLeading = liked ? Sidebar.Cover.Liked(38f) : null });
        }

        /// <summary>The resource drag chip; a kind with no Wavee resource starts no drag.</summary>
        public DragPayload? DragPayloadFor(int r)
        {
            var shape = ShapeNow;
            if ((uint)r >= (uint)shape.Rows.Count) return null;
            var target = RecentsView.EntitySlotOf(shape.Rows, r);
            var kind = Wavee.Drag.KindOf(target.Kind);
            if (kind == DragKind.Route) return null;
            string uri = UriOf(shape, r, target);
            var facts = FactsOf(target);
            return new DragPayload(kind, uri, uri, facts.Title, target, ArtUrl: facts.Cover);
        }

        // ── the per-row and per-count string caches ─────────────────────────────────────────────────────────────────

        public string UriOf(RecentsShape shape, int r, EntityRef target)
            => shape.Uri[r] ??= target.Kind == EntityKind.Collection ? EntityUri.LikedCollection
                : RecentsView.Names(target) ? new EntityUri(target.Id).Text : "";

        public string WhenOf(RecentsShape shape, int r) => shape.When[r] ??= RecentsView.PlayedAt(shape.Rows.Rows[r].PlayedAtMs, Now, Culture);

        /// <summary>The dormant shared-element key, on the FIRST occurrence of a target only (§9.1 #7).</summary>
        public string? MorphOf(RecentsShape shape, int r, EntityRef target, string uri)
            => shape.Morphable[r] && target.Kind is EntityKind.Album or EntityKind.Playlist
                ? shape.Morph[r] ??= MorphKeyOf(target.Kind, uri) : null;

        /// <summary>"Played N tracks · owner" — the phrase from the wire's declared count, never the member run.</summary>
        public string SubtitleOf(RecentsShape shape, int r, in RecentsEdge row, string? owner)
        {
            var meta = RecentsView.MetaFor(in row);
            string phrase = meta.Kind switch
            {
                RecentsMetaKind.PlayedCount => Cached(ref _playedLabels, meta.Count, static n => Strings.Recents.PlayedCount(n)),
                RecentsMetaKind.SavedCount => Cached(ref _savedLabels, meta.Count, static n => Strings.Recents.SavedCount(n)),
                _ => "",
            };
            owner ??= "";
            if (phrase.Length == 0) return owner;
            if (owner.Length == 0) return phrase;
            if (!ReferenceEquals(shape.SubOwner[r], owner)) { shape.SubOwner[r] = owner; shape.Sub[r] = phrase + " · " + owner; }
            return shape.Sub[r]!;
        }

        public string CountLabel(int n) => Cached(ref _countLabels, n, s_itemCount);

        public string PlayedAt(long playedAtMs) => RecentsView.PlayedAt(playedAtMs, Now, Culture);

        static string Cached(ref string?[] cache, int n, Func<int, string> format)
        {
            if (n < 0 || n > 1 << 16) return format(n);
            if (n >= cache.Length) Array.Resize(ref cache, Math.Max(n + 1, cache.Length * 2));
            return cache[n] ??= format(n);
        }

        public Prop<ColorF> DensityFillProp(int level) => _densityFills[Math.Clamp(level, 0, 5)];

        // ── the sticky band (§0 #4-#5, W4, W24) ─────────────────────────────────────────────────────────────────────

        void WatchSticky()
        {
            var handle = ScrollController.Handle;
            float offset = (float)handle.Offset.Value, viewport = (float)handle.ViewportSignal.Value;
            _ = handle.ExtentSignal.Value;   // a measured-extent correction re-projects too
            var s = ShapeNow;
            RecentsLayout.StickyMetrics(s.Sections, s.Layout, offset, viewport, out int header, out float push);
            long key = RecentsLayout.ProjectSticky(header, s.Layout.MeasuredVersion, push);
            if (key == _stickyKey) return;
            _stickyKey = key;
            UpdateSticky(offset, viewport);
        }

        /// <summary>The grouped layout's offsets ignore the cross size, so the viewport's scroll-axis length stands in
        /// for <c>StickyMetrics</c>' cross-size argument.</summary>
        void UpdateSticky(float offsetY, float viewport)
        {
            var s = ShapeNow;
            RecentsLayout.StickyMetrics(s.Sections, s.Layout, offsetY, viewport, out int header, out float push);
            float quantized = RecentsLayout.QuantizePush(push);
            if (StickyHeader.Peek() != header) StickyHeader.Value = header;
            if (!StickyPush.Peek().Equals(quantized)) StickyPush.Value = quantized;
            RailMeasuredVersion.SetIfChanged(s.Layout.MeasuredVersion);
            // The accent moves once per DAY crossing (never per row or per frame): the latest day is recorded and ONE
            // pre-created continuation commits whatever it settled on (§4.1 "Quantization").
            _pendingAccentDay = (uint)header < (uint)s.Sections.Items.Length ? s.Sections.Items[header].DayIndex : -1;
            if (AccentDay.Peek() != _pendingAccentDay && !_accentArmed)
            {
                _accentArmed = true;
                _post(_resolveAccentDay);
            }
            ProbeStickyAlignment(s, offsetY, header);
        }

        void ResolveAccentDay()
        {
            _accentArmed = false;
            AccentDay.SetIfChanged(_pendingAccentDay);
        }

        /// <summary>DEBUG: a realized row whose day disagrees with its header, or a pinned day that disagrees with the first
        /// visible row, is a stop, not a screenshot (§9.1 #17).</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        static void ProbeStickyAlignment(RecentsShape s, float offsetY, int stickyFlat)
        {
            var sections = s.Sections;
            if (sections.Items.Length == 0 || (uint)stickyFlat >= (uint)sections.Items.Length) return;
            int at = s.Layout.IndexAt(offsetY, 0f);
            int visibleDay = -1;
            for (int i = Math.Max(0, at), last = Math.Min(sections.Items.Length, at + 6); i < last; i++)
            {
                var item = sections.Items[i];
                if (item.Kind != RecentsFlatItemKind.Row) continue;
                if ((uint)item.DayIndex < (uint)sections.HeaderDates.Length && (uint)item.OriginalRowIndex < (uint)s.Rows.Count
                    && RecentsView.DateOf(s.Rows.Rows[item.OriginalRowIndex].PlayedAtMs, s.BuiltOffset) != sections.HeaderDates[item.DayIndex])
                    System.Diagnostics.Debug.Fail("recents: row day != header day at flat " + i);
                if (visibleDay < 0) visibleDay = item.DayIndex;
            }
            int stickyDay = sections.Items[stickyFlat].DayIndex;
            if (visibleDay >= 0 && stickyDay != visibleDay)
                System.Diagnostics.Debug.Fail("recents: sticky day " + stickyDay + " != first visible row day " + visibleDay);
        }

        // ── the zoom and the calendar readout (§6.1, W14) ────────────────────────────────────────────────────────────

        void OpenOverviewFromMasthead()
        {
            CalendarDay.Value = Today;
            ZoomController.ZoomOutTo(-1);
        }

        public void InvokeDay(int dayIndex)
        {
            var s = ShapeNow;
            if ((uint)dayIndex >= (uint)s.Sections.HeaderDates.Length) return;
            DateOnly date = s.Sections.HeaderDates[dayIndex];
            CalendarDay.Value = date;
            ZoomController.ZoomOutTo(RecentsLayout.HeaderFlatFor(s.Sections, date));
        }

        public void JumpIn(DateOnly date, int monthIndex)
        {
            CalendarDay.Value = date;
            if (IsZoomedOut.Peek()) { ZoomController.ZoomInTo(monthIndex); return; }
            // A calendar beside the list (a structure's docked Overview, C-Recents-Huge): nothing to zoom, so the list itself is brought to the day.
            int flat = MapOutToInFunc(monthIndex);
            if (flat >= 0) ListController.StartBringItemIntoView(flat, alignmentRatio: 0f, animate: true);
        }

        public void FocusDay(bool focused, DateOnly date)
        {
            if (focused) CalendarDay.SetIfChanged(date);
            else CalendarDay.Value = Today;
        }

        /// <summary>The ONE readout builder the band and the cell tooltip share: plays or "Nothing played", "top: X" once the
        /// top row's identity landed, and "Jump to d MMM" only for a day the list has a header for (no dead promise).</summary>
        public string CalendarReadout(RecentsShape s, DateOnly date)
        {
            var day = RecentsLayout.CalendarDay(s.Calendar, date);
            int plays = day?.PlayCount ?? 0;
            string readout = plays > 0 ? Strings.Recents.PlayCount(plays) : Loc.Get(Strings.Recents.NothingPlayed);
            if (day?.TopItem is { } top && (uint)top.OriginalRowIndex < (uint)s.Rows.Count
                && FactsOf(RecentsView.EntitySlotOf(s.Rows, top.OriginalRowIndex)) is { Known: true, Title.Length: > 0 } facts)
                readout += " · " + Strings.Recents.Mostly(facts.Title);
            if (RecentsLayout.HeaderFlatFor(s.Sections, date) >= 0)
                readout += " · " + Strings.Recents.JumpToDay(date.ToString("d MMM", Culture));
            return readout;
        }

        public string CalendarTooltip(RecentsShape s, DateOnly date) => date.ToString("dddd d MMMM", Culture) + " · " + CalendarReadout(s, date);

        string MonthKey(int i)
        {
            var months = ShapeNow.Calendar.Months;
            return (uint)i < (uint)months.Length
                ? "recents-month:" + FormatCache.Int(months[i].Year) + ":" + FormatCache.Int(months[i].Month)
                : "recents-month:none";
        }

        // ── the rail (W15, §9.1 #11) ─────────────────────────────────────────────────────────────────────────────────

        AnnotatedScrollBarLabel[] RailLabels()
        {
            var s = ShapeNow;
            var sections = s.Sections;
            var labels = new List<AnnotatedScrollBarLabel>();
            int priorMonth = -1, priorYear = -1;
            for (int i = 0; i < sections.HeaderIndices.Length; i++)
            {
                DateOnly date = sections.HeaderDates[i];
                if (date == DateOnly.MinValue || (date.Month == priorMonth && date.Year == priorYear)) continue;
                // Abbreviated months fit the 44-DIP lane; the year joins only when it changes.
                string text = priorYear != date.Year ? date.ToString("MMM yy", Culture) : date.ToString("MMM", Culture);
                labels.Add(new AnnotatedScrollBarLabel(s.Layout.OffsetOf(sections.HeaderIndices[i], 0f), text));
                priorMonth = date.Month;
                priorYear = date.Year;
            }
            return labels.ToArray();
        }

        float[] RailTicks()
        {
            var s = ShapeNow;
            var ticks = new float[s.Sections.HeaderIndices.Length];
            for (int i = 0; i < ticks.Length; i++) ticks[i] = s.Layout.OffsetOf(s.Sections.HeaderIndices[i], 0f);
            return ticks;
        }

        AnnotatedScrollBarLabel? RailDetail(float offset)
        {
            var s = ShapeNow;
            var sections = s.Sections;
            if (sections.Items.Length == 0) return null;
            int flat = Math.Clamp(s.Layout.IndexAt(offset, 0f), 0, sections.Items.Length - 1);
            int day = sections.Items[flat].DayIndex;
            if ((uint)day >= (uint)sections.HeaderLabels.Length) return null;
            return new AnnotatedScrollBarLabel(s.Layout.OffsetOf(sections.HeaderIndices[day], 0f), sections.HeaderLabels[day]);
        }

        // ── what a target says, and the verbs the structure's slots call ─────────────────────────────────────────────────

        /// <inheritdoc />
        public RecentsFacts FactsOf(EntityRef target) => Recents.FactsOf(target);

        /// <inheritdoc />
        public string? CoverOf(EntityRef target) => Recents.CoverOf(target);

        /// <inheritdoc />
        public void SubscribeRow(RecentsShape shape, int row) => Recents.SubscribeRow(shape, row);

        /// <inheritdoc />
        public void PlayContext(EntityId id) => Playback.PlayContext(id);

        /// <inheritdoc />
        public void HoverDay(DateOnly date) => CalendarDay.SetIfChanged(date);
    }

    // ══ 6. THE LEAVES: the accent + wash binder, the owner demand ════════════════════════════════════════════════════

    /// <summary>The ONE node that owns the cover-grading watch (§4.1, §9.2). Re-derives once per DAY crossing (the
    /// quantized <c>AccentDay</c>) and when an identity or a grading lands; publishes the page accent
    /// and ONE Hero leg of the shell wash — claim on the first publish and on reactivation, refresh while owner, never
    /// clear (§4.2). It publishes DATA only: whether the wash is drawn is the theme's (Classic's material layer reads its
    /// Surfaces option), and the page accent follows the artwork whatever that option says (owner NQ6): only the structure's separate
    /// "Accent from artwork" (CT9) turns it to the system accent.</summary>
    internal sealed class AccentBinder(PageView page) : Component
    {
        static readonly Func<string?, Scheme?> s_chromeScheme = static url => Design.ChromeSchemeFor(url.AsSpan());

        public override Element Render()
        {
            var slot = UseContext(ShellMaterial.Slot);
            _ = Entities.ScopeEpoch.Value;
            _ = page.AccentDay.Value;
            _ = page.ShapeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Tracks.Changed.Value; _ = scope.Albums.Changed.Value; _ = scope.Playlists.Changed.Value;
            _ = scope.Artists.Changed.Value; _ = scope.Shows.Changed.Value; _ = scope.Episodes.Changed.Value;

            var shape = page.ShapeNow;
            var rows = shape.Rows;
            int source = RecentsView.AccentSourceRow(shape.Sections, rows, page.StickyHeader.Peek());
            var target = source >= 0 ? RecentsView.EntitySlotOf(rows, source) : default;
            string? url = target.Slot > 0 ? FactsOf(target).Cover : null;

            var resolved = FallbackAccent();
            // The page accent follows the artwork unless the structure's "Accent from artwork" is off (CT9 U2; a tracked read, so a flip re-renders).
            if (url is { Length: > 0 } && ShellPolicy.Tracked.Artwork.AccentFromArtwork)
            {
                _ = Palette.Watch(url).Value;
                if (Design.ChromeSchemeFor(url) is { } scheme)
                    resolved = new Design.PageAccent(Design.Palette.Floor(Design.Palette.ChromeAccent(scheme)), ColorScience.Accent(scheme), url);
            }
            UseLayoutEffect(() => page.Accent.SetIfChanged(resolved), DepKey.From(resolved.GetHashCode()));

            // The wash's card: the accent's own row when its cover resolved, else the first resolved cover in the top 32
            // rows of the cut — never a colour invented before any artwork landed.
            HomeCard? card = url is { Length: > 0 } ? new HomeCard(target) : null;
            for (int i = 0; card is null && i < shape.Display.Length && i < 32; i++)
            {
                var t = RecentsView.EntitySlotOf(rows, shape.Display[i]);
                if (t.Slot > 0 && FactsOf(t).Cover is { Length: > 0 }) card = new HomeCard(t);
            }
            if (HomeWashSource.PlaneUrl(card) is { Length: > 0 } plane) _ = Palette.Watch(plane).Value;
            var pick = HomeWashSource.Pick(card, s_chromeScheme);
            HomeWash? wash = pick is { } p ? new HomeWash(new WashLayer(p.Color, p.Key), null, null) : null;
            page.LastWash = wash;

            var claimed = UseRef(false);
            UseEffect(() =>
            {
                ShellMaterial.Publish(slot, page.WashOwner, isClaim: !claimed.Value, definite: false, tint: null, wash);
                claimed.Value = true;
            }, DepKey.From(HashCode.Combine(pick?.Key, pick?.Color.R, pick?.Color.G, pick?.Color.B)));
            UseActivation(onActivated: () => ShellMaterial.Publish(slot, page.WashOwner, isClaim: true,
                definite: false, tint: null, page.LastWash));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    /// <summary>The playlist bylines' demand: re-run per playlist publication and per shape, at leaf scope.</summary>
    internal sealed class OwnerDemand(PageView page) : Component
    {
        public override Element Render()
        {
            _ = Entities.ScopeEpoch.Value;
            uint published = Entities.Current.Playlists.Changed.Value;
            int epoch = page.ShapeEpoch.Value;
            UseEffect(page.DemandOwnersAction, DepKey.From((long)published, (long)epoch));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }
}