// ── Entities/Recents.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the page: masthead, pivots, the measured grouped list with its pinned day band, the drawer accordion, the annotated
// rail, the semantic zoom + calendar surface, the viewport-following page accent and its ONE wash leg
//
// Role: UI
// Owner: P
// Wave: 5
// Budget: 1150 lines
// Spec: ch 16 §0, §1.1/§1.3 (the tree and the props-freeze map), §2 W1-W4, W8-W12, W14-W15, W22, W24-W25, §4.1-§4.2,
//   §5, §6.1, §7.1 (demand on mount), §9.1 #1-#4, #10-#17, §9.2
//
// THE MODEL. The page demands the recents relation ONCE on mount (`Entities.EnsureEdge(FetchEdge.Recents, me)`) and
// reads the edge; every row entity the page shows is demanded in ONE batched `Entities.Ensure` per kind when a
// publication is adopted (`RecentsLayout.DemandSlots`); playlist bylines through the owner leaf; a drawer's members when
// it opens. No visible-window fetching anywhere (ch 16 §7.1, contract §8).
//
// ONE SHAPE, SWAPPED ATOMICALLY (§9.1 #1). The snapshot, the pivot's cut, the morph flags, the sections, the calendar and
// the STATEFUL measured layout live on one immutable `Shape`; `BuildShape` publishes it in 0.2.9's order (build →
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
// ZERO ALLOCATION ON THE SCROLL PATH. The scroll-geometry observer (`ProjectSticky`/`UpdateSticky`) is arithmetic,
// three change-gated signal writes and one pre-created posted delegate; per-row strings (when, uri, morph key, the
// joined subtitle) are cached on the Shape, and the count phrases per count on the page.

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

public readonly partial struct Recents
{
    // MOUNT POINT (Wave 5 contract §5)
    /// <summary>The <c>recents</c> route's page (<see cref="Shell.RouteKind.Recents"/>). The route carries no data; the
    /// page reads the signed-in account's relation itself.</summary>
    public static Element PageFor(in Shell.Route route) => Embed.Comp(static () => new PageView());

    static readonly RecentsFlatItem EmptyFlat = new(RecentsFlatItemKind.Row, -1, -1, -1);

    // ══ 1. THE SHAPE ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Everything that must agree with everything else, as ONE reference (§9.1 #1). The per-row string caches are
    /// filled lazily on first bind and dropped with the shape.</summary>
    internal sealed class Shape
    {
        public readonly RecentsSnapshot Rows;
        public readonly int[] Display;
        public readonly bool[] Morphable;
        public readonly RecentsSections Sections;
        public readonly RecentsCalendar Calendar;
        public readonly GroupedListVirtualLayout Layout;
        public readonly TimeSpan BuiltOffset;
        /// <summary>Built from an ANSWERED relation (false for the pending seed): the skeleton's gate.</summary>
        public readonly bool Loaded;
        public readonly string Summary;
        internal readonly string?[] When, Uri, Morph, Sub;
        internal readonly string?[] SubOwner;

        public Shape(RecentsSnapshot rows, int[] display, bool[] morphable, RecentsSections sections, RecentsCalendar calendar,
                     GroupedListVirtualLayout layout, TimeSpan builtOffset, bool loaded, string summary)
        {
            Rows = rows; Display = display; Morphable = morphable; Sections = sections; Calendar = calendar;
            Layout = layout; BuiltOffset = builtOffset; Loaded = loaded; Summary = summary;
            int n = rows.Count;
            When = new string?[n]; Uri = new string?[n]; Morph = new string?[n]; Sub = new string?[n]; SubOwner = new string?[n];
        }

        /// <summary>The midnight relabel: the same list and layout, new day words and a fresh "when" cache.</summary>
        public Shape Relabeled(RecentsSections sections, string summary)
            => new(Rows, Display, Morphable, sections, Calendar, Layout, BuiltOffset, Loaded, summary);
    }

    /// <summary>One recycled slot's handlers, built ONCE per slot; each resolves the slot's CURRENT item at invocation.</summary>
    internal sealed class RowActions
    {
        public readonly Action Toggle, Open, Play;
        public readonly Func<ContextMenuModel?> Menu;
        public readonly DragSource DragFrom;

        public RowActions(PageView page, IReadSignal<RecentsFlatItem> item)
        {
            Toggle = () => page.ToggleExpanded(item.Peek().OriginalRowIndex);
            Open = () => page.Open(item.Peek().OriginalRowIndex);
            Play = () => page.Play(item.Peek().OriginalRowIndex);
            Menu = () => page.MenuFor(item.Peek().OriginalRowIndex);
            DragFrom = Wavee.Drag.Source(() => page.DragPayloadFor(item.Peek().OriginalRowIndex));
        }
    }

    // ══ 2. THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════

    internal sealed class PageView : Component
    {
        static readonly Func<int, string> s_itemCount = static n => Strings.Recents.ItemCount(n);
        static readonly Func<int, string> s_groupedFrom = static n => Strings.Recents.GroupedFrom(n);
        static readonly LayoutTransition BodyFlip = new(TransitionChannels.Position | TransitionChannels.Opacity,
            MotionTok.ContentResize.ToDynamics(),
            Enter: new EnterExit(Dy: Spacing.S, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

        // ── signals (§1.3's props-freeze map) ──
        internal readonly Signal<int> Epoch = new(0), ShapeEpoch = new(0);
        internal readonly Signal<string?> Chip = new(null);
        internal readonly Signal<StringId> ExpandedRow = new(StringId.Empty);
        internal readonly Signal<int> StickyHeader = new(-1), RailMeasuredVersion = new(0), AccentDay = new(-1);
        internal readonly Signal<float> StickyPush = new(0f);
        internal readonly Signal<Design.PageAccent> Accent;
        internal readonly Signal<bool> IsZoomedOut = new(false);
        internal readonly Signal<DateOnly> CalendarDay;
        internal readonly Signal<Shape> ShapeSignal;

        internal readonly ItemsViewController ListController = new(), CalendarController = new();
        internal readonly AnnotatedScrollBarController ScrollController = new();
        internal readonly SemanticZoomController ZoomController = new();
        internal readonly object WashOwner = new();
        internal HomeWash? LastWash;

        internal Shape ShapeNow;
        readonly Shape _pendingShape;
        internal CultureInfo Culture = CultureInfo.CurrentCulture;
        internal DateTimeOffset Now = DateTimeOffset.Now;
        internal DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

        int _expandedRow = -1;
        int _pendingAccentDay = -1;
        bool _accentArmed;
        EntityId _refreshedFor;
        Action<Action> _post = static a => a();
        readonly List<int> _demand = new(128);
        readonly HashSet<int> _seen = new();
        string?[] _countLabels = new string?[64], _playedLabels = new string?[64], _savedLabels = new string?[64];

        // ── pre-created delegates: nothing on a hot path allocates a closure ──
        internal readonly Prop<ColorF> AccentInkProp, AccentFillProp;
        readonly Prop<ColorF>[] _densityFills = new Prop<ColorF>[6];
        internal readonly Prop<Affine2D> StickyTransform;
        internal readonly Action ResetCalendarDayAction, DemandOwnersAction;
        internal readonly Action<WheelEventArgs> ForwardWheelAction;
        internal readonly Func<int, Element> MonthCardFactory;
        internal readonly Func<int, string> MonthKeyFunc;
        internal readonly Func<BoundItemScope<RecentsFlatItem>, Element> SlotFactory;
        readonly Action _resolveAccentDay, _rollover, _refresh, _ensureEdge, _adopt, _activated, _deactivated, _openOverview, _retry;
        readonly Action<KeyEventArgs> _onKeyDown;
        internal readonly Func<ScrollGeometry, long> ProjectStickyFunc;
        internal readonly Action<ScrollGeometry> UpdateStickyAction;
        internal readonly Func<int, int> MapInToOutFunc, MapOutToInFunc, ContentTypeFunc;
        internal readonly Action<int, RecentsFlatItem> InvokeFlatAction;
        internal readonly Func<int, RecentsFlatItem, string> TextForFunc;
        internal readonly Func<AnnotatedScrollBarLabel[]> RailLabelsFunc;
        internal readonly Func<float[]> RailTicksFunc;
        internal readonly Func<float, AnnotatedScrollBarLabel?> RailDetailFunc;
        readonly Func<bool> _pending, _failed;
        readonly Func<Element> _content, _shimmer, _failedPanel;
        readonly Func<PivotTabs> _pivots;
        readonly Func<AccentBinder> _binder;
        readonly Func<OwnerDemand> _owners;

        public PageView()
        {
            // The skeleton's seed rows are stamped off the SAME local clock that groups them: one "Today", never two.
            var seed = RecentsView.PendingSeedRows(Now);
            var map = RecentsView.Filter(seed, null);
            var sections = RecentsView.BuildSections(seed, map, Now, Culture);
            var layout = new GroupedListVirtualLayout(sections.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
            layout.ContentExtent(sections.Items.Length, 0f);
            _pendingShape = new Shape(seed, map, RecentsView.FirstOccurrence(seed), sections,
                RecentsView.DayDensity(seed, map, Now, Culture), layout, Now.Offset, loaded: false, summary: "");
            ShapeNow = _pendingShape;
            ShapeSignal = new Signal<Shape>(_pendingShape);
            Accent = new Signal<Design.PageAccent>(Fallback());
            CalendarDay = new Signal<DateOnly>(Today);

            AccentInkProp = Prop.Of(() => Accent.Value.Ink);
            AccentFillProp = Prop.Of(() => Accent.Value.Fill);
            _densityFills[0] = ColorF.Transparent;
            for (int level = 1; level <= 5; level++)
            {
                int l = level;
                // The heat's paint: the accent INK at A_subtle + (ink.A − A_subtle)·level/5 (§4.3), bound so a crossing
                // glides the whole heatmap without one cell re-rendering.
                _densityFills[level] = Prop.Of(() => RecentsLayout.DensityFill(l, Accent.Value.Ink, Tok.AccentSubtle.A));
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
            MonthCardFactory = i => Embed.Comp(() => new MonthCard(this, i)) with { Key = MonthKey(i) };
            MonthKeyFunc = MonthKey;
            SlotFactory = scope => Embed.Comp(() => new RowSlot(this, scope));
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
            ProjectStickyFunc = ProjectSticky;
            UpdateStickyAction = UpdateSticky;
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
            _content = Content;
            _shimmer = PendingContent;
            _failedPanel = () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: _retry);
            _pivots = () => new PivotTabs(this);
            _binder = () => new AccentBinder(this);
            _owners = () => new OwnerDemand(this);
        }

        /// <summary>The accent before any cover grades, with washes off, or with no source row: byte-identical to what
        /// every consumer painted before the page had a dynamic accent (§4.1).</summary>
        internal static Design.PageAccent Fallback() => new(Tok.AccentTextPrimary, Tok.AccentDefault, "");

        public override Element Render()
        {
            _post = UsePost();
            _ = Entities.ScopeEpoch.Value;
            _ = Recents.Changed.Value;
            var me = Recents.Me;
            uint version = me.Version;
            var readiness = me.Readiness;

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

            Element body = new SkelRegionEl(_pending, _failed, _content, _shimmer, _failedPanel,
                SkelReveal.None, SkeletonStyle.Default, null, SmoothResize: true);
            var kids = new Element[]
            {
                Hero(shape),
                new BoxEl
                {
                    Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, Spacing.S),
                    // Keyed on the SHAPE, not the token: a pivot switch re-renders the live strip (its underline enters).
                    Children = [Embed.Comp(_pivots) with { Key = "recents-pivots:" + FormatCache.Int(shapeEpoch) }],
                },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
                    // No dock reserve (§9.1 #10): the shell clips above the player bar; Spacing.L is breathing room.
                    Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, Spacing.L),
                    // The FLIP on a pivot switch. Reduced motion is a VALUE: no transition at all.
                    Layout = Design.Reduced ? null : BodyFlip,
                    Children = [body],
                },
                Embed.Comp(_binder) with { Key = "recents-accent" },
                Embed.Comp(_owners) with { Key = "recents-owners" },
            };
            Element page = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Focusable = true,
                OnKeyDown = _onKeyDown, Children = kids,
            };
            // The page PROVIDES its accent: the equalizer, the # cell and the save button read "this page's accent"
            // through the context instead of knowing they sit in Recents (§0 #16).
            return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)Accent, page);
        }

        // ── the masthead (W3, §0 #1-#2) ──────────────────────────────────────────────────────────────────────────────

        Element Hero(Shape shape)
        {
            var title = Design.Type.SurfaceDisplay(Loc.Get(Strings.Home.Recents)) with
            {
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f,
                Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
            };
            var overview = Button.Create(Loc.Get(Strings.Recents.Overview), _openOverview, ButtonAppearance.Subtle,
                ControlSize.Small, glyph: Icons.Calendar) with { Shrink = 0f };
            Element titleRow = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Children = [title, overview],
            };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.XS,
                Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, Spacing.L),
                // The two lines arrive 45 ms apart; reduced motion is a VALUE (0), never a branch.
                Stagger = Design.Reduced ? 0f : Design.Motion.MastheadStaggerMs,
                Children = shape.Summary.Length == 0
                    ? [titleRow]
                    :
                    [
                        titleRow,
                        Caption(shape.Summary) with
                        {
                            Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            // A reserved measure in lieu of tabular figures: the line never reflows (§0 #2).
                            MinWidth = 220f,
                            Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                        },
                    ],
            };
        }

        // ── the body arms ────────────────────────────────────────────────────────────────────────────────────────────

        Element Content()
        {
            int epoch = ShapeEpoch.Value;
            string? token = Chip.Value;
            if (ShapeNow.Rows.Count == 0)
                // W8: the display sentence alone — no subtitle, no glyph, no action.
                return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                    Loc.Get(Strings.Sidebar.Section.EmptyRecents), "");
            return Embed.Comp(() => new SemanticSurface(this, token, epoch))
                with { Key = "recents-semantic:" + (token ?? "all") + ":" + FormatCache.Int(epoch) };
        }

        /// <summary>The shimmer SOURCE (§9.1 #12): the real day header and the row's real geometry over the pending
        /// seed — the stateful list must not mount while pending, and the deriver maps these leaves to bars.</summary>
        Element PendingContent()
        {
            var shape = _pendingShape;
            var items = shape.Sections.Items;
            var kids = new Element[items.Length];
            for (int i = 0; i < items.Length; i++)
                kids[i] = items[i].Kind == RecentsFlatItemKind.DateHeader
                    ? DayHeader(this, shape, items[i].DayIndex, overlay: false, s_noop)
                    : new BoxEl
                    {
                        Direction = 0, Height = RecentsLayout.RowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), MinWidth = 0f,
                        Children =
                        [
                            new BoxEl { Width = CardArt, Height = CardArt, Shrink = 0f, Corners = Radii.ControlAll },
                            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS, Children = [Design.Type.TrackTitle(""), Caption("")] },
                            new BoxEl { Direction = 0, Gap = Spacing.M, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [Caption(""), Caption("")] },
                        ],
                    };
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Children = kids,
            };
        }

        static readonly Action s_noop = static () => { };

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
            ShapeNow = new Shape(rows, map, sameRows ? previous.Morphable : RecentsView.FirstOccurrence(rows), sections, calendar,
                layout, Now.Offset, loaded, summary);
            ShapeSignal.Value = ShapeNow;
            ExpandedRow.Value = StringId.Empty;
            // Re-resolve the pinned band from the live offset: a re-cut mid-list never leaves a blank band (W24).
            float offset = ScrollController.Offset.Peek(), viewport = ScrollController.ViewportLength.Peek();
            UpdateSticky(new ScrollGeometry(0f, offset, viewport, viewport, 0f, 0f, 0f, 0f, 0));
            CalendarDay.Value = Today;
            ShapeEpoch.Value = ShapeEpoch.Peek() + 1;
        }

        internal void SelectPivot(string? token)
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

        internal void ToggleExpanded(int r)
        {
            var shape = ShapeNow;
            if ((uint)r >= (uint)shape.Rows.Count) return;
            var id = shape.Rows.Rows[r].ItemId;
            if (id.IsEmpty || shape.Sections.RowToFlat[r] < 0) return;
            bool closing = ExpandedRow.Peek() == id;
            // An OFF-SCREEN drawer has no live node to report its collapse: snap its extent first. A realized one follows
            // the Reflow exit and eases back to 64.
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

        void ResetExpandedExtent(Shape shape, int r)
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

        bool IsFlatRealized(Shape shape, int r)
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
        internal void Open(int r)
        {
            var target = RecentsView.EntitySlotOf(ShapeNow.Rows, r);
            if (!RecentsView.Names(target)) return;
            if (target.Kind == EntityKind.Collection) { Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked)); return; }
            if (target.Kind is EntityKind.Track or EntityKind.Episode) { Playback.PlayContext(target.Id); return; }
            var route = Shell.For(new EntityUri(target.Id), FactsOf(target).Title);
            if (!route.IsNone) Shell.GoTo(route);
        }

        /// <summary>The cover FAB: a track or episode plays itself; anything else starts as a CONTEXT from the top.</summary>
        internal void Play(int r)
        {
            var target = RecentsView.EntitySlotOf(ShapeNow.Rows, r);
            if (target.Kind == EntityKind.Collection) Playback.PlayContext(EntityUri.LikedCollection);
            else if (!target.IsNone) Playback.PlayContext(target.Id);
        }

        /// <summary>The container menu (W16): the strip over the container verbs, then the rows, under the card's header.</summary>
        internal ContextMenuModel? MenuFor(int r)
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
            var ctx = new ActionContext(at, Actions.Services);
            var rows = new List<MenuFlyoutItem>(8);
            AppBarCommand[] strip;
            if (target.Kind == EntityKind.Artist)
            {
                strip = Actions.Menu.Strip(in ctx, [ActionId.PlayContext]);
                Actions.Menu.AddRows(rows, in ctx, [ActionId.SaveContext, ActionId.OpenItem, ActionId.PinToSidebar]);
            }
            else
            {
                strip = Actions.Menu.Strip(in ctx, [ActionId.PlayContext, ActionId.PlayContextNext, ActionId.AddContextToQueue, ActionId.SaveContext]);
                if (target.Kind == EntityKind.Album)
                    Actions.Menu.AddRows(rows, in ctx, [ActionId.AddContextToPlaylist, ActionId.OpenItem, ActionId.PinToSidebar, ActionId.GoToAlbumArtist]);
                else
                    Actions.Menu.AddRows(rows, in ctx, [ActionId.AddContextToPlaylist, ActionId.OpenItem, ActionId.PinToSidebar]);
            }
            if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);
            string sub = SubtitleOf(shape, r, in shape.Rows.Rows[r], facts.Subtitle);
            return new ContextMenuModel(strip, rows,
                Actions.Menu.Header(facts.Cover, facts.Title, sub.Length > 0 ? sub : Actions.Menu.KindWord(at.Kind),
                                    circular: target.Kind == EntityKind.Artist,
                                    leading: target.Kind == EntityKind.Collection ? Sidebar.Cover.Liked(38f) : null));
        }

        /// <summary>The resource drag chip; a kind with no Wavee resource starts no drag.</summary>
        internal DragPayload? DragPayloadFor(int r)
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

        internal string UriOf(Shape shape, int r, EntityRef target)
            => shape.Uri[r] ??= target.Kind == EntityKind.Collection ? EntityUri.LikedCollection
                : RecentsView.Names(target) ? new EntityUri(target.Id).Text : "";

        internal string WhenOf(Shape shape, int r) => shape.When[r] ??= RecentsView.PlayedAt(shape.Rows.Rows[r].PlayedAtMs, Now, Culture);

        /// <summary>The dormant shared-element key, on the FIRST occurrence of a target only (§9.1 #7).</summary>
        internal string? MorphOf(Shape shape, int r, EntityRef target, string uri)
            => shape.Morphable[r] && target.Kind is EntityKind.Album or EntityKind.Playlist
                ? shape.Morph[r] ??= Design.MorphKeys.For(target.Kind, uri) : null;

        /// <summary>"Played N tracks · owner" — the phrase from the wire's declared count, never the member run.</summary>
        internal string SubtitleOf(Shape shape, int r, in RecentsEdge row, string? owner)
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

        internal string CountLabel(int n) => Cached(ref _countLabels, n, s_itemCount);

        internal string PlayedAt(long playedAtMs) => RecentsView.PlayedAt(playedAtMs, Now, Culture);

        static string Cached(ref string?[] cache, int n, Func<int, string> format)
        {
            if (n < 0 || n > 1 << 16) return format(n);
            if (n >= cache.Length) Array.Resize(ref cache, Math.Max(n + 1, cache.Length * 2));
            return cache[n] ??= format(n);
        }

        internal Prop<ColorF> DensityFillProp(int level) => _densityFills[Math.Clamp(level, 0, 5)];

        // ── the sticky band (§0 #4-#5, W4, W24) ─────────────────────────────────────────────────────────────────────

        long ProjectSticky(ScrollGeometry g)
        {
            var s = ShapeNow;
            RecentsLayout.StickyMetrics(s.Sections, s.Layout, g.OffsetY, g.ViewportW, out int header, out float push);
            return RecentsLayout.ProjectSticky(header, s.Layout.MeasuredVersion, push);
        }

        void UpdateSticky(ScrollGeometry g)
        {
            var s = ShapeNow;
            RecentsLayout.StickyMetrics(s.Sections, s.Layout, g.OffsetY, g.ViewportW, out int header, out float push);
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
            ProbeStickyAlignment(s, g, header);
        }

        void ResolveAccentDay()
        {
            _accentArmed = false;
            AccentDay.SetIfChanged(_pendingAccentDay);
        }

        /// <summary>DEBUG: a realized row whose day disagrees with its header, or a pinned day that disagrees with the first
        /// visible row, is a stop, not a screenshot (§9.1 #17).</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        static void ProbeStickyAlignment(Shape s, ScrollGeometry g, int stickyFlat)
        {
            var sections = s.Sections;
            if (sections.Items.Length == 0 || (uint)stickyFlat >= (uint)sections.Items.Length) return;
            int at = s.Layout.IndexAt(g.OffsetY, 0f);
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

        internal void InvokeDay(int dayIndex)
        {
            var s = ShapeNow;
            if ((uint)dayIndex >= (uint)s.Sections.HeaderDates.Length) return;
            DateOnly date = s.Sections.HeaderDates[dayIndex];
            CalendarDay.Value = date;
            ZoomController.ZoomOutTo(RecentsLayout.HeaderFlatFor(s.Sections, date));
        }

        internal void JumpIn(DateOnly date, int monthIndex)
        {
            CalendarDay.Value = date;
            ZoomController.ZoomInTo(monthIndex);
        }

        internal void FocusDay(bool focused, DateOnly date)
        {
            if (focused) CalendarDay.SetIfChanged(date);
            else CalendarDay.Value = Today;
        }

        /// <summary>The ONE readout builder the band and the cell tooltip share: plays or "Nothing played", "top: X" once the
        /// top row's identity landed, and "Jump to d MMM" only for a day the list has a header for (no dead promise).</summary>
        internal string CalendarReadout(Shape s, DateOnly date)
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

        internal string CalendarTooltip(Shape s, DateOnly date) => date.ToString("dddd d MMMM", Culture) + " · " + CalendarReadout(s, date);

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
    }

    // ══ 3. THE PIVOTS (W3, W10, §0 #3) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Four FIXED tabs on frame one; a tab with no rows is dimmed and inert, never hidden. Only the selected tab
    /// MOUNTS its underline, so the scaleX 0 → 1 enter is a real mount; the others reserve the 2-DIP baseline.</summary>
    internal sealed class PivotTabs(PageView page) : Component
    {
        static readonly MotionTokenDef UnderlineEnter = MotionTokenDef.Eased(260f, Easing.SmoothOut);

        public override Element Render()
        {
            string? selected = page.Chip.Value;
            _ = page.ShapeEpoch.Value;
            var rows = page.ShapeNow.Rows;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    Tab(null, Loc.Get(Strings.Detail.Filter.All), true, selected is null),
                    Tab(RecentsView.PivotMusic, Loc.Get(Strings.Recents.Chip.Music), RecentsView.PivotAvailable(rows, RecentsView.PivotMusic),
                        string.Equals(selected, RecentsView.PivotMusic, StringComparison.OrdinalIgnoreCase)),
                    Tab(RecentsView.PivotPodcasts, Loc.Get(Strings.Recents.Chip.Podcasts), RecentsView.PivotAvailable(rows, RecentsView.PivotPodcasts),
                        string.Equals(selected, RecentsView.PivotPodcasts, StringComparison.OrdinalIgnoreCase)),
                    Tab(RecentsView.PivotArtists, Loc.Get(Strings.Recents.Pivot.Artists), RecentsView.PivotAvailable(rows, RecentsView.PivotArtists),
                        string.Equals(selected, RecentsView.PivotArtists, StringComparison.OrdinalIgnoreCase)),
                ],
            };
        }

        Element Tab(string? token, string label, bool available, bool isSelected)
        {
            Element text = Design.Type.PivotLabel(label) with
            {
                Color = !available ? Tok.TextDisabled : isSelected ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1,
            };
            Element underline = isSelected
                ? new BoxEl
                {
                    Key = "underline", Height = 2f, Corners = Radii.FullAll,
                    Fill = page.AccentFillProp, BrushTransitionMs = AccentTransitionMs,
                    TransformOriginX = 0f, Enter = new EnterExit(Sx: 0f, Active: true), Transition = UnderlineEnter,
                }
                : new BoxEl { Key = "baseline", Height = 2f, Fill = ColorF.Transparent };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, Shrink = 0f,
                Role = AutomationRole.Button, Focusable = available, Cursor = available ? CursorId.Hand : CursorId.Arrow,
                IsEnabled = available, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = available ? () => page.SelectPivot(token) : null,
                Children = [text, underline],
            };
        }
    }

    // ══ 4. THE SEMANTIC SURFACE, THE LIST, THE SLOT, THE PINNED BAND, THE RAIL ═══════════════════════════════════════

    /// <summary>Both views stay mounted (the zoom's keep-alive); the overview enters from 1.08 while the list recedes to
    /// 0.94. Keyed on (token, shapeEpoch) by its parent — a deliberate remount on a re-cut.</summary>
    internal sealed class SemanticSurface(PageView page, string? token, int shapeEpoch) : Component
    {
        public override Element Render()
        {
            string suffix = (token ?? "all") + ":" + FormatCache.Int(shapeEpoch);
            Element detail = Embed.Comp(() => new ListSurface(page, token)) with { Key = "recents-list:" + suffix };
            Element rail = Embed.Comp(() => new RailView(page)) with { Key = "recents-rail:" + suffix };
            Element zoomedIn = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Children = [detail] },
                    // The HStack item is a stretch COLUMN around the rail: Grow on the rail's own root would steal width.
                    new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinHeight = 0f, Children = [rail] },
                ],
            };
            Element zoomedOut = Embed.Comp(() => new CalendarSurface(page, shapeEpoch)) with { Key = "recents-calendar:" + FormatCache.Int(shapeEpoch) };
            return SemanticZoom.Create(
                new SemanticZoomSlots(new SemanticZoomView(zoomedIn, page.ListController), new SemanticZoomView(zoomedOut, page.CalendarController)),
                new SemanticZoomOptions
                {
                    IsZoomedOut = page.IsZoomedOut, Controller = page.ZoomController,
                    MapInToOut = page.MapInToOutFunc, MapOutToIn = page.MapOutToInFunc,
                });
        }
    }

    /// <summary>ONE measured virtual list over the grouped projection with the pinned day band over it (§0 #4).</summary>
    internal sealed class ListSurface(PageView page, string? token) : Component
    {
        public override Element Render()
        {
            var layout = page.ShapeNow.Layout;
            var items = UseMemo(() => BoundItems.Project(page.ShapeSignal, static s => s.Sections.Items.Length,
                static (s, i) => s.Sections.Items[i], EmptyFlat), DepKey.Empty);
            Element list = ItemsView.CreateBound(items, page.SlotFactory, RepeatLayout.Measured(layout), new ListOptions<RecentsFlatItem>
            {
                // The rows are cards with their own chrome; a list selector would be a second, competing cue.
                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
                IsItemInvokedEnabled = true, OnInvokedTyped = page.InvokeFlatAction, ItemTextTyped = page.TextForFunc,
                Controller = page.ListController, Overscan = 6, Grow = 1f,
                // One recycle pool per ARM, so a card slot never rebinds into the track-grid shape (§9.1 #13).
                ContentType = page.ContentTypeFunc,
                Scroll = new ScrollOptions
                {
                    // Each pivot remembers its OWN offset (§9.1 #16).
                    ScrollKey = "recents:" + (token ?? "all"), AutoEdgeFade = true,
                    VerticalScrollController = page.ScrollController, SuppressScrollBar = true,
                    OnScrollGeometryChanged = (page.ProjectStickyFunc, page.UpdateStickyAction),
                    // Rows scroll UNDER the pinned band: clipped at exactly 48 with a 24-DIP feather (W4).
                    ItemClipTopInset = RecentsLayout.DateHeaderHeight, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                },
                // The engine's cold-realize ramp: bounded to the realized window, never 1,708 authored delays.
                Entrance = new EntranceOptions { StaggerColdRealize = true },
            });
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                Children = [list, Embed.Comp(() => new StickyDayHeader(page))],
            };
        }
    }

    /// <summary>A BOUND slot: the template runs once per recycled slot and rebinds through the scope's item signal. It
    /// subscribes to exactly what its row paints — the target's table (identity landing), the accordion, the epoch.</summary>
    internal sealed class RowSlot : Component
    {
        readonly PageView _page;
        readonly BoundItemScope<RecentsFlatItem> _scope;
        readonly RowActions _actions;
        readonly Action _onHeader;

        public RowSlot(PageView page, BoundItemScope<RecentsFlatItem> scope)
        {
            _page = page;
            _scope = scope;
            _actions = new RowActions(page, scope.Item);
            _onHeader = () => page.InvokeDay(scope.Item.Peek().DayIndex);
        }

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var flat = _scope.Item.Value;
            _ = _scope.Index.Value;
            _ = _page.Epoch.Value;
            var shape = _page.ShapeNow;
            if (flat.Kind == RecentsFlatItemKind.DateHeader) return DayHeader(_page, shape, flat.DayIndex, overlay: false, _onHeader);
            int r = flat.OriginalRowIndex;
            if ((uint)r >= (uint)shape.Rows.Count) return new BoxEl { Height = RecentsLayout.RowHeight };

            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var target = RecentsView.EntitySlotOf(shape.Rows, r);
            _ = TableOf(scope, target.Kind)?.Changed.Value;
            if (target.Kind == EntityKind.Playlist) _ = scope.Users.Changed.Value;
            if (shape.Rows.Rows[r].Why == RecentsReason.Saved) _ = scope.Tracks.Changed.Value;

            if (RecentsLayout.UsesTrackArm(shape.Rows, r)) return SingleRow(shape, r, _actions);
            var id = shape.Rows.Rows[r].ItemId;
            bool expanded = !id.IsEmpty && _page.ExpandedRow.Value == id;
            return Card(_page, shape, r, expanded, _actions, overlay);
        }
    }

    static Table? TableOf(Scope scope, EntityKind kind) => kind switch
    {
        EntityKind.Track => scope.Tracks,
        EntityKind.Episode => scope.Episodes,
        EntityKind.Album => scope.Albums,
        EntityKind.Artist => scope.Artists,
        EntityKind.Playlist => scope.Playlists,
        EntityKind.Show => scope.Shows,
        _ => null,
    };

    /// <summary>The pinned day band: ALWAYS mounted, 48 tall; before the first resolve it is present but invisible and
    /// non-hit-testable (W24). The push is a bound transform — a scroll frame never re-renders it.</summary>
    internal sealed class StickyDayHeader : Component
    {
        readonly PageView _page;
        readonly Action _onClick;

        public StickyDayHeader(PageView page)
        {
            _page = page;
            _onClick = () =>
            {
                var s = page.ShapeNow;
                int flat = page.StickyHeader.Peek();
                if ((uint)flat < (uint)s.Sections.Items.Length) page.InvokeDay(s.Sections.Items[flat].DayIndex);
            };
        }

        public override Element Render()
        {
            _ = _page.Epoch.Value;
            int flat = _page.StickyHeader.Value;
            var shape = _page.ShapeNow;
            int day = (uint)flat < (uint)shape.Sections.Items.Length ? shape.Sections.Items[flat].DayIndex : -1;
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, Height = RecentsLayout.DateHeaderHeight,
                HitTestVisible = day >= 0, HitTestPassThrough = true, Opacity = day >= 0 ? 1f : 0f,
                Transform = _page.StickyTransform,
                OnPointerWheel = _page.ForwardWheelAction,
                Children = [DayHeader(_page, shape, day, overlay: true, _onClick)],
            };
        }
    }

    /// <summary>The 44-DIP annotated rail — the list's scrollbar. Labels and ticks are memoized on (shapeEpoch, the
    /// layout's measured-extent version), which bumps only on a REAL extent delta (§9.1 #11).</summary>
    internal sealed class RailView(PageView page) : Component
    {
        int _logged;

        public override Element Render()
        {
            float slotH = UseMeasuredBounds().Value.H;
            int measured = page.RailMeasuredVersion.Value;
            int epoch = page.ShapeEpoch.Peek();
            var labels = UseMemo(page.RailLabelsFunc, DepKey.From(epoch, measured));
            var ticks = UseMemo(page.RailTicksFunc, DepKey.From(epoch, measured));
            // The one diagnostic the parity walk cross-checks (item 39): the last label against the scroll maximum.
            float max = page.ScrollController.MaximumOffset.Peek();
            float lastOff = labels.Length > 0 ? labels[^1].ScrollOffset : 0f;
            int key = HashCode.Combine((int)slotH, (int)max, (int)lastOff, labels.Length);
            if (key != _logged)
            {
                _logged = key;
                Log.Info("recents.rail", "slotH=" + (int)slotH + " max=" + (int)max + " lastOff=" + (int)lastOff
                    + " lastMinusMax=" + (int)(lastOff - max) + " labels=" + labels.Length + " ticks=" + ticks.Length);
            }
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f,
                Children =
                [
                    AnnotatedScrollBar.Create(page.ScrollController, new AnnotatedScrollBarOptions
                    {
                        Labels = labels, TickOffsets = ticks,
                        // The slot's real height, so the last date is the bottom of the track (no dead band).
                        Height = slotH > 0f ? slotH : float.NaN,
                        DetailLabelAtOffset = page.RailDetailFunc,
                    }),
                ],
            };
        }
    }

    // ══ 5. THE CALENDAR SURFACE (W11-W14) ════════════════════════════════════════════════════════════════════════════

    /// <summary>The overview: the readout band + legend over a GridFit of month cards (290 min, 36 gutters, the TALLEST
    /// month's height as the row estimate). Its scroll key carries the shape epoch: a re-cut forgets (§9.1 #16).</summary>
    internal sealed class CalendarSurface(PageView page, int shapeEpoch) : Component
    {
        public override Element Render()
        {
            var calendar = page.ShapeNow.Calendar;
            Element grid = ItemsView.Create(calendar.Months.Length, page.MonthCardFactory,
                RepeatLayout.GridFit(RecentsLayout.CalGridW, Spacing.PageWide, RecentsLayout.MonthCardHeight(RecentsView.MaxWeeks(calendar))),
                new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
                    Controller = page.CalendarController, Grow = 1f, Overscan = 1, KeyOf = page.MonthKeyFunc,
                    Scroll = new ScrollOptions { ScrollKey = "recents-calendar:" + FormatCache.Int(shapeEpoch), AutoEdgeFade = true },
                });
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
                OnPointerExit = page.ResetCalendarDayAction,
                Children = [Embed.Comp(() => new CalendarBand(page)), grid],
            };
        }
    }

    /// <summary>ONE band: the hovered day over its readout, the legend beside them; the readout gives width back.</summary>
    internal sealed class CalendarBand(PageView page) : Component
    {
        public override Element Render()
        {
            DateOnly selected = page.CalendarDay.Value;
            _ = page.Epoch.Value;
            var shape = page.ShapeNow;
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                        Children =
                        [
                            Caption(selected.ToString("dddd d MMMM", page.Culture)) with
                            { Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            Caption(page.CalendarReadout(shape, selected)) with
                            { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                    Legend(page),
                ],
            };
        }
    }

    // ══ 6. THE LEAVES: the accent + wash binder, the owner demand ════════════════════════════════════════════════════

    /// <summary>The ONE node that owns the cover-grading watch (§4.1, §9.2). Re-derives once per DAY crossing (the
    /// quantized <c>AccentDay</c>), on a washes toggle, and when an identity or a grading lands; publishes the page accent
    /// and ONE Hero leg of the shell wash — claim on the first publish and on reactivation, refresh while owner, never
    /// clear (§4.2).</summary>
    internal sealed class AccentBinder(PageView page) : Component
    {
        static readonly Func<string?, Scheme?> s_chromeScheme = static url => Design.ChromeSchemeFor(url.AsSpan());

        public override Element Render()
        {
            var slot = UseContext(ShellMaterial.Slot);
            bool washes = Prefs.Appearance.ColorWashes();
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

            var resolved = PageView.Fallback();
            if (washes && url is { Length: > 0 })
            {
                _ = Palette.Watch(url).Value;
                if (Design.ChromeSchemeFor(url) is { } scheme)
                    resolved = new Design.PageAccent(Design.Palette.ChromeAccent(scheme), Design.Palette.Accent(scheme), url);
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
            if (washes && HomeWashSource.PlaneUrl(card) is { Length: > 0 } plane) _ = Palette.Watch(plane).Value;
            var pick = washes ? HomeWashSource.Pick(card, s_chromeScheme) : null;
            HomeWash? wash = pick is { } p ? new HomeWash(new WashLayer(p.Color, p.Key), null, null) : null;
            page.LastWash = wash;

            var claimed = UseRef(false);
            UseEffect(() =>
            {
                ShellMaterial.Publish(slot, page.WashOwner, isClaim: !claimed.Value, definite: !washes, tint: null, wash);
                claimed.Value = true;
            }, DepKey.From(HashCode.Combine(washes, pick?.Key, pick?.Color.R, pick?.Color.G, pick?.Color.B)));
            UseActivation(onActivated: () => ShellMaterial.Publish(slot, page.WashOwner, isClaim: true,
                definite: !Prefs.Appearance.ColorWashes(), tint: null, page.LastWash));
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
