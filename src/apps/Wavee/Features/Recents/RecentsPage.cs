using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The Recents page ──────────────────────────────────────────────────────────────────────────────────────────────────
// One virtualized list over the WHOLE grouped recents snapshot (~1,708 rows on a real account) plus a viewport-driven
// hydration pump, under a Zune-ish typographic masthead and a Mica wash.
//
// THE ONE FACT THE WHOLE PAGE IS SHAPED BY: recents is a POINTER LIST. `GET /playlist/v2/list/recents/page` returns item
// ids, uris, timestamps and group child-counts and NOT ONE readable string — Title/Subtitle/Image are null on every
// freshly fetched row BY DESIGN. So the page owns three things the other list pages do not:
//   1. it never pages the wire (the whole list arrives at once → VirtualCollection.FromSnapshot, no remote paging);
//   2. it HYDRATES the entities the user actually realized, and only those (OnVisibleRange);
//   3. it re-renders exactly the realized slots when hydration lands, never rebuilding a 1,708-row list.
//
// HYDRATION GOES THROUGH THE FAÇADE. `Services.Hydrator` is the app's ONE metadata entry
// point: SWR cache, in-flight dedup, partial-cache skip (a fresh uri never hits the network), ETag/304 conditional
// reads, and — the part that matters most here — PROJECTION INTO THE STORE, which is how every other surface shares the
// same facts and how they survive a restart via CachedStore. The rows therefore hold NO copied strings: a row renders by
// resolving its uri against the store, and a store change re-skins the realized window. A page-local metadata cache
// would have been thrown away on navigate-away and shared with nobody.
//
// Filtering is CLIENT-SIDE, always: the official client hits /recents/page/diff on a chip click, but those bodies
// are the same items with only the list-level filters attribute permuted. A chip change re-cuts the loaded snapshot
// and never touches the network.
//
// The page takes NO constructor dependency (app-page rule) — everything resolves through UseContext in Render.
sealed class RecentsPage : Component
{
    /// <summary>MediaCard.Row's plain arm is a fixed 64-DIP row; the measured layout seeds from that and corrects on
    /// realize, so the (defensive) track arm may differ without the viewport mis-sizing.</summary>
    const float RowHeight = 64f;
    /// <summary>Date-header band. ModuleHeader is 20/28; 48 DIP is the house thumb rung that fits that type plus
    /// the prototype's label + rule + count row without clipping. Distinct from <see cref="TrackRow.HeaderHeight"/>
    /// (36), which is a playlist section label.</summary>
    const float DateHeaderHeight = WaveeSize.Thumb48;
    const float ChildRowHeight = WaveeSize.Thumb40;
    const int OverscanRows = 6;
    const float PageInset = Spacing.PageWide;
    /// <summary>The summary line reserves its width instead of reflowing as the count/date resolve. This engine has NO
    /// tabular-figures seam (ConcertUi and FlipCountdown both say so in as many words), so a reserved measure is the
    /// app's established substitute for tabular numerals.</summary>
    const float SummaryMinWidth = 220f;
    /// <summary>Hero entrance stagger. Applied to the MASTHEAD's two lines only — never to the list, whose entrance is
    /// the engine's realized-window-bounded StaggerColdRealize. 1,708 authored delays is a bug, not a choreography.
    /// <para>The value now lives in <see cref="WaveeMotion.MastheadStaggerMs"/>, shared with the app's other drill-in
    /// masthead (HomeSectionPage) so the two surfaces cannot drift apart by a number.</para></summary>
    const float HeroStaggerMs = WaveeMotion.MastheadStaggerMs;
    /// <summary>W2.9: the implicit brush-transition budget every dynamic-accent consumer on this page shares — a
    /// section crossing glides rather than snaps. <c>Motion.ReducedMotion</c> is a VALUE at the call site (the
    /// house idiom — see <see cref="WaveeMotion.MastheadStaggerMs"/>'s remarks), never a hook branch.</summary>
    static float AccentTransitionMs => Motion.ReducedMotion ? 0f : WaveeMotion.Standard;
    /// <summary>How long after a now-playing identity change to revision-sync. Same 2 s trailing window as
    /// <see cref="PlayLogStore.SaveDebounceMs"/> so a skip burst becomes one <c>/page/diff</c>, and so we don't beat
    /// the server's recents write (capture: Spotify's full snapshot landed ~10 s after play).</summary>
    const float DiffAfterPlayMs = PlayLogStore.SaveDebounceMs;

    /// <summary>The recycled-slot fallback: a bound slot transiently outside the range renders nothing.</summary>
    static readonly RecentsFlatItem EmptyFlat = new(RecentsFlatItemKind.Row, -1, -1, -1);

    // ── reactive surface (three signals; everything else is a plain field the slots read at render time) ──────────────
    /// <summary>Bumped when hydration lands in the STORE (or a snapshot is adopted). The bound projection carries it, so
    /// exactly the realized slots re-render — the DetailTracks mechanism.</summary>
    readonly Signal<int> _epoch = new(0);
    /// <summary>Snapshot/filter shape only. Metadata hydration must never replace the stateful grouped layout.</summary>
    readonly Signal<int> _shapeEpoch = new(0);
    /// <summary>The selected content-type TOKEN (wire spelling), null = "All". Never a label — the label is derived.</summary>
    readonly Signal<string?> _chip = new(null);
    /// <summary>Exactly one disclosure is open. The wire item id is the identity; entity URIs repeat.</summary>
    readonly Signal<string> _expandedRow = new("");
    /// <summary>The open disclosure's index in <see cref="Shape.Rows"/>. Kept beside the wire id because the measured
    /// virtual layout is indexed by the FLAT projection: when an open row is recycled offscreen, no live node remains
    /// to report its collapsed height, so the transition itself must normalize that cached extent.</summary>
    int _expandedOriginalRow = -1;
    readonly Signal<int> _stickyHeader = new(-1);
    readonly Signal<float> _stickyPush = new(0f);
    /// <summary>W1.4c: the layout's own measured-extent version (<see cref="GroupedListVirtualLayout.MeasuredVersion"/>)
    /// published by <see cref="UpdateSticky"/>. Folded into the <see cref="RecentsRail"/> label/tick memo keys (replacing
    /// the old dead "layoutReady" bit — a Shape always carries a live Layout now, so readiness is no longer in question)
    /// and into <see cref="ProjectSticky"/>'s packed key, so a drawer-driven extent correction invalidates both the
    /// rail's cached offsets and the sticky gate on the same scroll-geometry callback.
    /// <para>The layout bumps that version only on a REAL delta (compare-before-set inside <c>SetMeasured</c>), so this
    /// is exact invalidation — it replaced a 128-DIP <c>ContentH</c> bucket that could miss a correction which happened
    /// to leave the total extent inside the same bucket, and the rail's cached <c>OffsetOf</c> values with it.</para></summary>
    readonly Signal<int> _railMeasuredVersion = new(0);
    /// <summary>W2.9: the sticky bucket's DAY, quantized so the dynamic accent moves once per section crossing —
    /// never per row, per extent correction, or per scroll frame. -1 = no sticky header yet (the page fallback).
    /// Written only by the coalesced <see cref="ResolveAccentDay"/> post, mirroring <see cref="Pump"/>'s own
    /// arm-then-post idiom so a scroll burst reporting several day changes before the post drains still commits
    /// only the LATEST one.</summary>
    readonly Signal<int> _accentDay = new(-1);
    /// <summary>W2.9: the page's own viewport-following accent, published by <see cref="RecentsAccentBinder"/> — the
    /// leaf that owns the cover-grading <c>Watch</c> subscription (the <c>Design/CoverPaletteLeaves.cs</c> idiom: a
    /// page <c>Render</c> must never subscribe to a grading arrival itself). Provided to the subtree via
    /// <see cref="WaveeAccentCtx"/>; consumers read it as a PROP (a bound <c>Fill</c>/<c>Color</c>), never
    /// re-rendering when the accent shifts.</summary>
    readonly Signal<PageAccent> _accent = new(FallbackAccent());
    readonly Signal<bool> _isZoomedOut = new(false);
    readonly Signal<DateOnly> _calendarDay = new(DateOnly.FromDateTime(DateTime.Now));
    readonly object _washOwner = new();

    // ── the snapshot, owned as ONE atomic reference (never a signal: a 1,708-element list is not a value to diff) ─────
    // W1.1: every field that must agree with every OTHER field to describe one coherent list — the wire rows, the
    // chip's display cut, the morph-eligibility flags, the grouped sections, the calendar, and the STATEFUL grouped
    // layout that measures them — lives on one Shape instance instead of six loose fields. The old shape had a real
    // bug class (A1/A2/A10): _sections and _vc/_groupedLayout could be swapped non-atomically across two statements,
    // so a reader landing between them saw one generation of sections against another of everything else. A single
    // REFERENCE SWAP is atomic here by construction — UI-thread only, every read and every write happens
    // synchronously on that one thread, never interleaved with a concurrent reader — so publishing a new Shape can
    // never be observed half-updated. EVERY engine-invoked member captures `var s = _shape;` ONCE at its own entry
    // and resolves fields only through that local (never re-reading `_shape` mid-method), so one logical operation
    // always sees one generation even though `_shape` itself may already point somewhere else by the time the NEXT
    // engine callback runs.
    sealed class Shape
    {
        public readonly RecentsRow[] Rows;                 // wire order
        public readonly RecentsRow[] Display;              // the chip's cut of Rows (== Rows when nothing is selected)
        public readonly bool[] Morphable;                  // first occurrence of each uri → may claim the shared-element tag
        public readonly RecentsSections Sections;
        public readonly RecentsCalendar Calendar;
        // STATEFUL — W1.2b reuses this SAME instance across a Recut only when the row snapshot and complete flat
        // projection are unchanged, preserving ordinary measured corrections without transferring them to other rows.
        // Ephemeral drawer height is normalized before every recut and page deactivation.
        public readonly GroupedListVirtualLayout Layout;
        public readonly TimeSpan BuiltOffset;              // the local UTC offset the sections were grouped under (W1.6)

        public Shape(RecentsRow[] rows, RecentsRow[] display, bool[] morphable, RecentsSections sections,
                     RecentsCalendar calendar, GroupedListVirtualLayout layout, TimeSpan builtOffset)
        {
            Rows = rows; Display = display; Morphable = morphable; Sections = sections;
            Calendar = calendar; Layout = layout; BuiltOffset = builtOffset;
        }
    }

    Shape _shape;
    /// <summary>The same seeded shape handed to <c>UseResource</c>, retained so the loading branch can run the real
    /// date-header/row builders against placeholder data and let <c>SkeletonDeriver</c> map that output.</summary>
    readonly Shape _pendingShape;
    /// <summary>W1.5: the per-mount fabricated skeleton, built once in the constructor from the SAME local clock
    /// (<see cref="DateTimeOffset.Now"/> taken there, not <see cref="DateTimeOffset.UtcNow"/>) that grouped the
    /// initial <see cref="Shape"/> — see <see cref="RecentsView.PendingSeedRows"/> for why a frozen UTC instant would
    /// split one skeleton "Today" bucket into two near a local midnight. Also handed to <c>UseResource</c> as the
    /// pending value so the loadable and the initial shape agree before the first fetch resolves.</summary>
    readonly RecentsSnapshot _pendingSeed;
    string? _revision;
    bool _hasSnapshot;

    /// <summary>The resident collection the viewport reads through. Snapshot-backed on purpose: recents arrives whole,
    /// so virtualization here is about MOUNTED UI, not remote paging.</summary>
    readonly VirtualCollection<RecentsFlatItem> _vc;
    readonly ItemsViewController _listController = new();
    readonly ItemsViewController _calendarController = new();
    readonly AnnotatedScrollBarController _scrollController = new();
    readonly SemanticZoomController _zoomController = new();
    /// <summary>Integration seam for SemanticZoom: inline and overlay day headers share this callback.</summary>
    internal Action<DateOnly>? DateHeaderInvoked { get; set; }

    // ── hydration bookkeeping. UI-THREAD ONLY: every mutation happens in Render, in Pump, or in a posted continuation. ─
    // NOTE this is NOT a metadata cache — that is the chokepoint's job. It only stops the SAME uri being handed to
    // the facade twice while one call is still in flight; freshness, dedup and skipping belong to the hydration ledger.
    readonly HashSet<string> _inflight = new(StringComparer.Ordinal);
    readonly List<string> _batch = new(RecentsView.BatchCap);
    /// <summary>W2.8: reused across pumps like <see cref="_batch"/> — the realized window's unresolved playlist owner
    /// uris, asked through the façade's User ladder each pump. A different LADDER from the entity uris
    /// <see cref="_batch"/> feeds, so it gets its own scratch list rather than sharing one.</summary>
    readonly List<string> _ownerBatch = new(16);
    int _rangeFirst, _rangeEnd;
    bool _pumpArmed;
    bool _storeDirty;
    /// <summary>W2.9: the LATEST day <see cref="UpdateSticky"/> has seen this turn — mirrors <see cref="_rangeFirst"/>/
    /// <see cref="_rangeEnd"/>'s role for <see cref="Pump"/>: recorded immediately (never captured by the arm site),
    /// read fresh by the posted <see cref="ResolveAccentDay"/>.</summary>
    int _pendingAccentDay = -1;
    bool _accentArmed;
    /// <summary>Pre-created once (constructor) — never a per-frame closure — so <c>_post(_resolveAccentDay)</c> costs
    /// no per-call delegate allocation on the scroll-hot <see cref="UpdateSticky"/> path.</summary>
    readonly Action _resolveAccentDay;

    // Services + callbacks, refreshed at the top of every render so a bound slot never holds a mount-time instance.
    Services? _svc;
    IStore? _store;
    IEntityHydrator? _hydrator;
    Action<Action> _post = static a => a();
    Action<string, string?> _go = static (_, _) => { };
    NavPreviewStore? _preview;
    CancellationTokenSource? _cts;
    CultureInfo _culture = CultureInfo.CurrentCulture;
    DateTimeOffset _now = DateTimeOffset.Now;

    /// <summary>The atomic value the bound rows observe: the hydration/adoption epoch, the collection's own version
    /// (bumped by a snapshot replacement), and the collection itself — so both selectors derive solely from ONE snapshot
    /// rather than reading mutable page fields.</summary>
    readonly record struct RowsView(int Epoch, int Version, VirtualCollection<RecentsFlatItem> Rows);

    /// <summary>What a row displays, resolved from the STORE at render time. Never stored on the row.</summary>
    readonly record struct RowFacts(string? Title, string? Subtitle, Image? Cover);

    static RecentsRow[] CopyRows(IReadOnlyList<RecentsRow> incoming)
    {
        var rows = new RecentsRow[incoming.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = incoming[i];
        return rows;
    }

    public RecentsPage()
    {
        var now = DateTimeOffset.Now;
        _pendingSeed = new RecentsSnapshot(null, RecentsView.PendingSeedRows(now));
        var rows = CopyRows(_pendingSeed.Rows);
        var morphable = RecentsView.FirstOccurrence(rows);
        var displayToRow = RecentsView.Filter(rows, null);
        var culture = CultureInfo.CurrentCulture;
        var sections = RecentsView.BuildSections(rows, displayToRow, now, culture);
        var calendar = RecentsView.DayDensity(rows, displayToRow, now, culture);
        var layout = new GroupedListVirtualLayout(sections.HeaderIndices, DateHeaderHeight, RowHeight);
        layout.ContentExtent(sections.Items.Length, 0f);   // prime the extent table before publish (W1.1 ordering)
        _pendingShape = new Shape(rows, rows, morphable, sections, calendar, layout, now.Offset);
        _shape = _pendingShape;
        _vc = VirtualCollection<RecentsFlatItem>.FromSnapshot(sections.Items);
        DateHeaderInvoked = OpenOverview;
        _resolveAccentDay = ResolveAccentDay;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var preview = UseContext(NavPreviewStore.Slot);
        var shellMaterial = UseContext(ShellMaterial.Slot);
        var post = UsePost();
        _post = post;
        _go = go;
        _preview = preview;
        _svc = svc;
        _store = svc?.RealStore;
        _hydrator = svc?.Hydrator;
        _culture = CultureInfo.CurrentCulture;
        _now = DateTimeOffset.Now;
        if (svc is null) return new BoxEl { Grow = 1f };

        // ── the cold read. One page-scoped CTS also cancels every hydration batch on unmount. ─────────────────────────
        var recents = UseResource(ct => FetchResourceAsync(svc.Recents, ct), _pendingSeed);

        // ── W1.6 midnight rollover. Keyed on the current LOCAL day number so the timer re-arms once per day instead of
        //    drifting; the callback re-reads the clock itself, so the exact ms computed here only has to land sometime
        //    after the boundary, never exactly on it (the "+1s" settle below).
        DateOnly today = DateOnly.FromDateTime(_now.ToOffset(_now.Offset).DateTime);
        DateTimeOffset nextMidnight = new(today.AddDays(1).ToDateTime(TimeOnly.MinValue), _now.Offset);
        float msUntilRollover = (float)Math.Max(1000d, (nextMidnight - _now).TotalMilliseconds + 1000d);
        UseTimeout(RolloverMidnight, msUntilRollover, DepKey.From(today.DayNumber));

        // In-page freshness: a track identity change while this page is live re-arms a trailing /page/diff.
        // UseActivation still diffs on nav-back; this is the path the capture showed Wavee missing (zero recents
        // GETs while playing). Keyed on the uri so a skip burst collapses to one fetch after the last change.
        string playUri = svc.Playback.CurrentTrack.Value?.Uri ?? "";
        UseTimeout(() => { if (_hasSnapshot) recents.Refresh(); }, DiffAfterPlayMs,
            DepKey.From(playUri.GetHashCode(), playUri.Length));

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            return (Action?)(() =>
            {
                _cts = null;
                try { cts.Cancel(); cts.Dispose(); } catch { }
            });
        }, DepKey.Empty);

        // ── the store is the row model, so a store WRITE is what makes rows readable. Subscribe once and coalesce: the
        //    playback path writes tracks constantly, and one epoch bump per write would re-render the realized window on
        //    every heartbeat. One posted bump per turn is enough — the rows re-read the store when they re-render.
        var store = svc.RealStore;
        UseEffect(() =>
        {
            if (store is null) return (Action?)null;
            var sub = store.Changes.Subscribe(Observers.From<StoreChange>(_ => MarkStoreDirty()));
            return (Action?)(() => sub.Dispose());
        }, DepKey.FromRef(store));

        // (W2.8's second subscription is gone: an owner profile resolving IS a store write now — UserHydration
        //  upserts the Owner — so the one subscription above already marks the window dirty for it.)

        int epoch = _epoch.Value;          // subscribe: hydration re-renders the chrome (summary + wash) too
        int shapeEpoch = _shapeEpoch.Value;
        string? token = _chip.Value;
        int accentDay = _accentDay.Value;  // W2.9: subscribe — a section crossing re-derives WashCard's source row

        // ── the shell MATERIAL (Mica wash). Recents publishes ONE leg — the most recent hydrated cover — through the
        //    same HomeWashSource resolution Home uses, so the colour is the page's own content and never invented.
        _ = AppearancePrefs.Epoch.Value;   // the Settings toggle applies LIVE (the DisableColorWashes idiom)
        bool washesDisabled = svc.Settings.Get(WaveeSettings.DisableColorWashes);
        var washCard = WashCard();
        // Watch exactly the ONE artwork whose grading the wash is still waiting on — never the plane's global epoch,
        // which every scrolling batch of this very list would bump.
        if (HomeWashSource.PlaneUrl(washCard) is { Length: > 0 } planeUrl)
            _ = SpotifyLive.CoverColorPlane.Current.Watch(planeUrl).Value;
        var pick = washesDisabled ? null : HomeWashSource.Pick(washCard, Surfaces.ChromeSchemeFor);
        HomeWash? wash = washesDisabled || pick is null
            ? null
            : new HomeWash(new WashLayer(pick.Value.Color, pick.Value.Key), null, null);

        // Owner-gated exactly like HomePage/DetailShell: a page clears the material only while it is still the owner,
        // so a "park this page + activate the destination" nav lands on the destination's material whichever effect
        // fires first.
        void SetWash(HomeWash? w)
        {
            if (shellMaterial is not null) shellMaterial.Value = new ShellMaterialState(_washOwner, null, w);
        }
        void ClearWash()
        {
            if (shellMaterial is not null && ReferenceEquals(shellMaterial.Peek().Owner, _washOwner))
                shellMaterial.Value = default;
        }
        UseEffect(() => SetWash(wash),
            DepKey.From(HashCode.Combine(washesDisabled, pick?.Key, pick?.Color.R, pick?.Color.G, pick?.Color.B)));
        UseActivation(
            onActivated: () =>
            {
                // KeepAlive un-parks this subtree before it raises activation. Clear the retained disclosure here—while
                // row render effects are live—so the first return frame cannot replay the old drawer out of the parked
                // scene. Deactivation below only normalizes the measured table; writing the signal after parking would
                // defer row reconciliation behind the page replay budget.
                CollapseExpanded(_shape);
                SetWash(wash);
                // Revision sync on REACTIVATION (and, separately, after a now-playing identity change — see the
                // DiffAfterPlayMs timeout in Render). A null diff answer means "unchanged": do nothing at all.
                if (_hasSnapshot) recents.Refresh();
            },
            onDeactivated: () =>
            {
                ClearWash();
                // KeepAlive has begun parking by the time this callback runs. Erase the cached geometry now, but leave
                // the disclosure signal for onActivated to clear after the subtree is live again (see above).
                ResetExpandedExtent(_shape, _expandedOriginalRow);
                _expandedOriginalRow = -1;
            });
        // …and on UNMOUNT too: onDeactivated fires only on PARK, so a nav that evicts this page without parking it
        // would otherwise leave a wash owned by a gone page. Owner-gated, so it can never clobber the next page's.
        UseEffect(() => (Action?)ClearWash, DepKey.Empty);

        // ── chrome ────────────────────────────────────────────────────────────────────────────────────────────────────
        Element hero = Hero();
        // W2.3: the Zune pivot strip replaces ContentFilterChips here — a FIXED All/Music/Podcasts/Artists set (never
        // the wire-derived variable chip bar), so it renders on frame one instead of popping in once a content-type
        // token resolves. Keyed on shapeEpoch (not token) so a pivot switch re-renders the same live component instead
        // of remounting it — only a genuine snapshot/filter rebuild tears it down.
        Element pivots = Embed.Comp(() => new PivotTabs(this)) with { Key = "recents-pivots:" + shapeEpoch };

        // The EXPLICIT-shimmerSource overload, which the docs reserve for exactly this case: content is a STATEFUL
        // component (the semantic-zoom surface, its virtual list, its controllers) that must not mount during load —
        // so the deriver would hit an unrendered ComponentEl with no SkeletonProxy and fall back to ONE 160×10 bar.
        // PendingContentSource invokes the real date-header/row builders against the same placeholder Shape supplied
        // to UseResource; the engine derives their shimmer, so row design still has one source of truth.
        Element body = Skel.Region(
            recents.Loadable,
            shimmerSource: PendingContentSource,
            content: _ => Embed.Comp(() => new RecentsSemanticSurface(this, token, shapeEpoch)) with
            { Key = "recents-semantic:" + (token ?? "all") + ":" + shapeEpoch },
            reveal: SkelReveal.None,
            isEmpty: snapshot => snapshot.Rows.Count == 0,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Sidebar.Section.EmptyRecents)),
            onFailed: () => ErrorState.Build(recents.Loadable.Error, onRetry: () => recents.Refresh()));

        var kids = new List<Element>(4)
        {
            hero,
            new BoxEl { Padding = new Edges4(PageInset, 0f, PageInset, Spacing.S), Children = [pivots] },
        };
        kids.Add(new BoxEl
        {
            Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            // NO dock reserve anywhere on this page: the shell already clips the content region above the docked
            // player bar (WaveeShell — "its bottom edge IS the player bar's top"), so a reserve — as wrapper padding
            // OR as a trailing in-scroller spacer — could never scroll content clear of anything; it only parked a
            // dead band at the end of the scroll while the rail advertised that unreachable range (W-bug-2's second
            // regression). Spacing.L is tail breathing so the last row is not glued to the bar's seam.
            Padding = new Edges4(PageInset, 0f, PageInset, Spacing.L),
            // The FLIP: a chip switch changes the list's identity (below), and this wrapper glides the swap instead of
            // cutting to a differently-sized list. Motion.ReducedMotion is a VALUE, so this is a null vs a transition,
            // never a divergent hook path.
            Layout = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                MotionTok.ContentResize.ToDynamics(),
                Enter: new EnterExit(Dy: Spacing.S, Opacity: 0f, Active: true),
                Exit: new EnterExit(Opacity: 0f, Active: true)),
            Children = [body],
        });
        // W2.9: always-mounted, zero-size — the ONE leaf that owns the accent's cover-grading Watch subscription
        // (see RecentsAccentBinder's own remarks). It publishes into _accent; nothing in this Render subscribes to
        // the grading Watch itself.
        kids.Add(Embed.Comp(new RecentsAccentBinder.Props(this), () => new RecentsAccentBinder()) with
        { Key = "recents-accent" });

        _ = epoch; _ = accentDay;   // read above; the explicit subscriptions this chrome depends on
        Element page = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Focusable = true,
            OnKeyDown = e =>
            {
                if (e.Handled || e.KeyCode != Keys.Escape || !_isZoomedOut.Peek()) return;
                _zoomController.ZoomInTo(-1);
                e.Handled = true;
            },
            Children = kids.ToArray(),
        };
        // W2.9: the ambient, content-derived accent this page publishes — MediaCard's now-playing equalizer and
        // TrackRow's number cell (and any future shared component) read it with UseContext(WaveeAccentCtx.Slot)
        // instead of knowing they are embedded in Recents. Modeled on how the shell provides ShellMaterial.Slot.
        return Ctx.Provide(WaveeAccentCtx.Slot, (IReadSignal<PageAccent>?)_accent, page);
    }

    // ── the loading source ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Render the real list rows against <see cref="_pendingShape"/>. The result is only an input to
    /// <c>SkeletonDeriver</c>; callbacks are stripped and semantic leaves become shimmer bars. Materializing the eight
    /// seed rows is bounded and intentional—the stateful bound virtual list itself must not mount while pending.</summary>
    Element PendingContentSource()
    {
        var shape = _pendingShape;
        var items = shape.Sections.Items;
        var children = new List<Element>(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            var flat = items[i];
            if (flat.Kind == RecentsFlatItemKind.DateHeader)
            {
                children.Add(DayHeader(shape, flat.DayIndex, overlay: false));
                continue;
            }
            int rowIndex = flat.OriginalRowIndex;
            if ((uint)rowIndex >= (uint)shape.Rows.Length) continue;
            children.Add(RowContent(shape.Rows[rowIndex], new RowFacts("", "", null), rowIndex, expanded: false));
        }
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children = children.ToArray(),
        };
    }

    // ── masthead ──────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>An oversized, light display cut of the surface's own name over one thin metadata line. The stagger lives
    /// on the CONTAINER (two children), the enter on each line — the engine's own idiom.</summary>
    Element Hero()
    {
        // The count is the page's ONE authored word on this line; the window either side of it stays culture-table
        // formatting (RecentsView owns no copy and is engine-free — see its Summary doc for the seam).
        string summary = _hasSnapshot
            ? RecentsView.Summary(_shape.Rows, _now, _culture,
                static n => Strings.Recents.ItemCount(n),
                static n => Strings.Recents.GroupedFrom(n))
            : "";
        // Recents is a ROOT — DrillTrail.Of returns empty for a root route, so it carries no "Home > Recents" crumb;
        // that ancestry never existed. A drilled-in Browse-family surface renders its trail INLINE in its own
        // title line instead (BrowseMasthead — Zune's breadcrumb-as-title); Recents has no trail to render, so it
        // keeps hand-rolling its own plain title here rather than routing through that surface.
        var title = WaveeType.SurfaceDisplay(Loc.Get(Strings.Home.Recents)) with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = 1f, Basis = 0f,
            Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };
        var overview = Button.Create(Loc.Get(Strings.Recents.Overview), OpenOverviewFromMasthead,
            ButtonAppearance.Subtle, ControlSize.Small, glyph: Icons.Calendar) with { Shrink = 0f };
        var lines = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                Children = [title, overview],
            },
        };
        if (summary.Length > 0)
            lines.Add(Caption(summary) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                // Reserved measure in lieu of tabular figures (no such seam exists here): the line does not reflow as
                // the count and the played window resolve.
                MinWidth = SummaryMinWidth,
                Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
            });
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS,
            Padding = new Edges4(PageInset, Spacing.XXL, PageInset, Spacing.L),
            Stagger = HeroStaggerMs,
            Children = lines.ToArray(),
        };
    }

    /// <summary>W2.3: the Zune pivot strip that replaces the wire-derived <c>ContentFilterChips</c> bar for this
    /// surface — a FIXED four tabs (All / Music / Podcasts / Artists) in <see cref="WaveeType.PivotLabel"/>, always
    /// shown (never popping in once a content-type token resolves); a tab with no matching row is shown disabled
    /// rather than hidden, the same "evidenced or dimmed, never removed" rule <c>ContentFilterChips</c> already uses.
    /// Reads <see cref="_chip"/>/<see cref="_shape"/> straight off the page (no props to freeze — this is a page-local
    /// component the same way <see cref="RecentsRail"/> is), so a pivot switch re-renders it live; only a genuine
    /// shape rebuild remounts it (the embed Key at the call site carries <c>shapeEpoch</c>, not the token).</summary>
    sealed class PivotTabs : Component
    {
        const float UnderlineHeight = 2f;
        /// <summary>~260ms per the prototype's pivot-underline entrance (target-design doc, "Pivot tabs").</summary>
        const float UnderlineEnterMs = 260f;

        readonly RecentsPage _page;
        public PivotTabs(RecentsPage page) => _page = page;

        public override Element Render()
        {
            string? selected = _page._chip.Value;
            var rows = _page._shape.Rows;
            bool hasMusic = RecentsView.PivotAvailable(rows, RecentsView.PivotMusic);
            bool hasPodcasts = RecentsView.PivotAvailable(rows, RecentsView.PivotPodcasts);
            bool hasArtist = RecentsView.PivotAvailable(rows, RecentsView.PivotArtists);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    Tab(null, Loc.Get(Strings.Detail.Filter.All), available: true, selected is null),
                    Tab(RecentsView.PivotMusic, Loc.Get(Strings.Recents.Chip.Music), hasMusic,
                        string.Equals(selected, RecentsView.PivotMusic, StringComparison.OrdinalIgnoreCase)),
                    Tab(RecentsView.PivotPodcasts, Loc.Get(Strings.Recents.Chip.Podcasts), hasPodcasts,
                        string.Equals(selected, RecentsView.PivotPodcasts, StringComparison.OrdinalIgnoreCase)),
                    Tab(RecentsView.PivotArtists, Loc.Get(Strings.Recents.Pivot.Artists), hasArtist,
                        string.Equals(selected, RecentsView.PivotArtists, StringComparison.OrdinalIgnoreCase)),
                ],
            };
        }

        Element Tab(string? token, string label, bool available, bool isSelected)
        {
            Element text = WaveeType.PivotLabel(label) with
            {
                Color = !available ? Tok.TextDisabled : isSelected ? Tok.TextPrimary : Tok.TextSecondary,
                MaxLines = 1,
            };
            // The underline only MOUNTS on the selected tab — swapping which tab carries it is what makes the
            // scaleX 0→1 in EnterExit fire as a genuine mount (Presence enter), not a property tween on a
            // continuously-present bar. The non-selected placeholder reserves the same 2px baseline so every tab
            // sits at the same height regardless of selection.
            // W2.9: bound to the page's viewport-following accent (Fill — the raw graded plate, matching what this
            // bar replaced: Tok.AccentDefault) rather than read once — a Prop, so a section crossing mid-hover glides
            // the bar's colour without this tab re-rendering.
            Element underline = isSelected
                ? new BoxEl
                {
                    Key = "underline",
                    Height = UnderlineHeight, Corners = Radii.FullAll,
                    Fill = Prop.Of(() => _page._accent.Value.Fill),
                    BrushTransitionMs = RecentsPage.AccentTransitionMs,
                    TransformOriginX = 0f,
                    Enter = new EnterExit(Sx: 0f, Active: true),
                    Transition = MotionTokenDef.Eased(UnderlineEnterMs, Easing.SmoothOut),
                }
                : new BoxEl { Height = UnderlineHeight, Fill = ColorF.Transparent };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, Shrink = 0f,
                Role = AutomationRole.Button, Focusable = available, Cursor = available ? CursorId.Hand : CursorId.Arrow,
                IsEnabled = available, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = available ? () => _page.SelectPivot(token) : null,
                Children = [text, underline],
            };
        }
    }

    /// <summary>W2.3's selection — the four fixed pivot tokens, chosen directly (never the old label→token lookup
    /// <c>ContentFilterChips</c> needed). CLIENT-SIDE like every other chip switch: re-cuts the loaded snapshot,
    /// never reaches the network.</summary>
    void SelectPivot(string? token)
    {
        if (string.Equals(token, _chip.Peek(), StringComparison.Ordinal)) return;
        _chip.Value = token;
        Recut(token);
    }

    // ── the list ──────────────────────────────────────────────────────────────────────────────────────────────────────
    void OpenOverviewFromMasthead()
    {
        _calendarDay.Value = DateOnly.FromDateTime(_now.ToOffset(_now.Offset).DateTime);
        _zoomController.ZoomOutTo(-1);
    }

    void OpenOverview(DateOnly date)
    {
        _calendarDay.Value = date;
        _zoomController.ZoomOutTo(HeaderFlatFor(date));
    }

    int HeaderFlatFor(DateOnly date)
    {
        var s = _shape;
        for (int i = 0; i < s.Sections.HeaderDates.Length; i++)
            if (s.Sections.HeaderDates[i] == date) return s.Sections.HeaderIndices[i];
        return -1;
    }

    int MonthFor(DateOnly date)
    {
        var s = _shape;
        for (int i = 0; i < s.Calendar.Months.Length; i++)
        {
            var month = s.Calendar.Months[i];
            if (month.Year == date.Year && month.Month == date.Month) return i;
        }
        return -1;
    }

    int MapInToOut(int flatIndex)
    {
        var s = _shape;
        if ((uint)flatIndex >= (uint)s.Sections.Items.Length) return -1;
        int day = s.Sections.Items[flatIndex].DayIndex;
        return (uint)day < (uint)s.Sections.HeaderDates.Length ? MonthFor(s.Sections.HeaderDates[day]) : -1;
    }

    int MapOutToIn(int monthIndex)
    {
        var s = _shape;
        DateOnly selected = _calendarDay.Peek();
        int exact = HeaderFlatFor(selected);
        if (exact >= 0 && MonthFor(selected) == monthIndex) return exact;
        if ((uint)monthIndex >= (uint)s.Calendar.Months.Length) return -1;
        var month = s.Calendar.Months[monthIndex];
        for (int i = 0; i < s.Sections.HeaderDates.Length; i++)
        {
            var date = s.Sections.HeaderDates[i];
            if (date.Year == month.Year && date.Month == month.Month) return s.Sections.HeaderIndices[i];
        }
        return -1;
    }

    sealed class RecentsSemanticSurface : Component
    {
        readonly RecentsPage _page;
        readonly string? _token;
        readonly int _shapeEpoch;

        public RecentsSemanticSurface(RecentsPage page, string? token, int shapeEpoch)
        {
            _page = page; _token = token; _shapeEpoch = shapeEpoch;
        }

        public override Element Render()
        {
            Element detail = Embed.Comp(() => new RecentsListSurface(_page, _token, _shapeEpoch)) with
            { Key = "recents-detail:" + (_token ?? "all") + ":" + _shapeEpoch };
            Element rail = Embed.Comp(() => new RecentsRail(_page, _shapeEpoch)) with
            { Key = "recents-rail:" + (_token ?? "all") + ":" + _shapeEpoch };
            Element zoomedIn = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.M,
                AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                        Children = [detail],
                    },
                    // BoxEl is the HStack flex item. RecentsRail is a ComponentEl — Grow/AlignSelf on its root are
                    // mirrored onto the HStack child, so Grow=1 there would steal WIDTH from the list. This column
                    // takes the row's stretched HEIGHT; RecentsRail Grow=1 fills that column instead.
                    new BoxEl
                    {
                        Direction = 1, AlignSelf = FlexAlign.Stretch, MinHeight = 0f,
                        Children = [rail],
                    },
                ],
            };
            Element zoomedOut = Embed.Comp(() => new CalendarOverviewSurface(_page, _shapeEpoch)) with
            { Key = "recents-calendar:" + _shapeEpoch };
            return SemanticZoom.Create(
                new SemanticZoomSlots(
                    new SemanticZoomView(zoomedIn, _page._listController),
                    new SemanticZoomView(zoomedOut, _page._calendarController)),
                new SemanticZoomOptions
                {
                    IsZoomedOut = _page._isZoomedOut,
                    Controller = _page._zoomController,
                    MapInToOut = _page.MapInToOut,
                    MapOutToIn = _page.MapOutToIn,
                });
        }
    }

    /// <summary>Owns the annotated rail so extent-signal churn cannot rebuild label arrays. GroupedListVirtualLayout
    /// ignores cross size (<c>OffsetOf</c>/<c>IndexAt</c> take 0); labels are memoized on <see cref="_shapeEpoch"/> and
    /// the W1.4c measured-extent version (a new grouping OR a measured-extent correction is what can move offsets).</summary>
    sealed class RecentsRail : Component
    {
        static int s_geomKey;
        readonly RecentsPage _page;
        readonly int _shapeEpoch;
        readonly Func<AnnotatedScrollBarLabel[]> _labels;
        readonly Func<float[]> _ticks;
        readonly Func<float, AnnotatedScrollBarLabel?> _detail;

        public RecentsRail(RecentsPage page, int shapeEpoch)
        {
            _page = page;
            _shapeEpoch = shapeEpoch;
            _labels = page.RailLabels;
            _ticks = page.RailTicks;
            _detail = page.RailDetail;
        }

        public override Element Render()
        {
            // The HStack item is a stretch column around this component (RecentsSemanticSurface). Grow=1 fills THAT
            // column's height. Do not put Grow=1/AlignSelf=Stretch on this root for the HStack itself: ComponentEl
            // mirrors those onto the HStack child, and Grow=1 there steals WIDTH from the list. Once the slot has
            // a real height, pass it as ASB Height so today is the top of the rail and the last date is the bottom.
            float slotH = UseMeasuredBounds().Value.H;
            // W1.4c: a Shape always carries a live Layout now (W1.1), so the old "has the layout landed yet" bit is
            // dead. What actually invalidates the rail's cached label/tick offsets is the extent table CORRECTING
            // (drawer expand/collapse, realized-row measurement) — the layout's own MeasuredVersion, published by
            // UpdateSticky. Exact (delta-gated by the layout itself), not a 128-DIP heuristic on ContentH.
            int measuredVersion = _page._railMeasuredVersion.Value;
            var labels = UseMemo(_labels, DepKey.From(_shapeEpoch, measuredVersion));
            var ticks = UseMemo(_ticks, DepKey.From(_shapeEpoch, measuredVersion));
            LogGeometry(slotH, labels, ticks);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f,
                Children =
                [
                    AnnotatedScrollBar.Create(_page._scrollController, new AnnotatedScrollBarOptions
                    {
                        Labels = labels,
                        TickOffsets = ticks,
                        // Parent-determined slot (this Grow=1 box fills the HStack stretch column). Passing it
                        // pins the bar so thumb/ticks/labels share that height — NaN stretch grew the dotted
                        // track while thumb math stayed on a shorter railHeight, leaving a dead band under Max.
                        Height = slotH > 0f ? slotH : float.NaN,
                        DetailLabelAtOffset = _detail,
                    }),
                ],
            };
        }

        void LogGeometry(float slotH, AnnotatedScrollBarLabel[] labels, float[] ticks)
        {
            var c = _page._scrollController;
            float min = c.MinimumOffset.Peek();
            float max = c.MaximumOffset.Peek();
            float off = c.Offset.Peek();
            float vp = c.ViewportLength.Peek();
            float lastOff = labels.Length > 0 ? labels[^1].ScrollOffset : float.NaN;
            int key = HashCode.Combine((int)slotH, (int)max, (int)vp, (int)lastOff, labels.Length);
            if (key == s_geomKey) return;
            s_geomKey = key;
            float range = MathF.Max(0f, max - min);
            float thumb01 = range > 0f ? (Math.Clamp(off, min, max) - min) / range : 0f;
            float last01 = range > 0f && float.IsFinite(lastOff)
                ? (Math.Clamp(lastOff, min, max) - min) / range : 0f;
            WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "recents.rail",
                "Annotated rail geometry",
                fields:
                [
                    WaveeLogField.Of("slotH", slotH),
                    WaveeLogField.Of("viewport", vp),
                    WaveeLogField.Of("min", min),
                    WaveeLogField.Of("max", max),
                    WaveeLogField.Of("offset", off),
                    WaveeLogField.Of("thumb01", thumb01),
                    WaveeLogField.Of("lastLabel", labels.Length > 0 ? labels[^1].Text : ""),
                    WaveeLogField.Of("lastOff", lastOff),
                    WaveeLogField.Of("last01", last01),
                    WaveeLogField.Of("lastMinusMax", lastOff - max),
                    WaveeLogField.Of("labels", labels.Length),
                    WaveeLogField.Of("ticks", ticks.Length),
                ]);
        }
    }

    AnnotatedScrollBarLabel[] RailLabels()
    {
        var s = _shape;
        var layout = s.Layout;
        var sections = s.Sections;
        var labels = new List<AnnotatedScrollBarLabel>();
        int priorMonth = -1, priorYear = -1;
        for (int i = 0; i < sections.HeaderIndices.Length; i++)
        {
            DateOnly date = sections.HeaderDates[i];
            if (date == DateOnly.MinValue || date.Month == priorMonth && date.Year == priorYear) continue;
            // The rail is LabelsMinWidth (44). Full month names ellipsis to "Augu.." and make the
            // labels unreadable; abbreviated months fit, and the pointer flag carries the day.
            string text = priorYear != date.Year
                ? date.ToString("MMM yy", _culture)
                : date.ToString("MMM", _culture);
            labels.Add(new AnnotatedScrollBarLabel(layout.OffsetOf(sections.HeaderIndices[i], 0f), text));
            priorMonth = date.Month; priorYear = date.Year;
        }
        return labels.ToArray();
    }

    float[] RailTicks()
    {
        var s = _shape;
        var layout = s.Layout;
        var sections = s.Sections;
        var ticks = new float[sections.HeaderIndices.Length];
        for (int i = 0; i < ticks.Length; i++) ticks[i] = layout.OffsetOf(sections.HeaderIndices[i], 0f);
        return ticks;
    }

    AnnotatedScrollBarLabel? RailDetail(float offset)
    {
        var s = _shape;
        var layout = s.Layout;
        var sections = s.Sections;
        if (sections.Items.Length == 0) return null;
        int lastContent = sections.Items.Length - 1;
        int flat = Math.Clamp(layout.IndexAt(offset, 0f), 0, lastContent);
        int day = sections.Items[flat].DayIndex;
        if ((uint)day >= (uint)sections.HeaderLabels.Length) return null;
        return new AnnotatedScrollBarLabel(layout.OffsetOf(sections.HeaderIndices[day], 0f),
            sections.HeaderLabels[day]);
    }

    // ── calendar geometry (ONE source of truth; the app's own precedent is ConcertDateFlyout's picker grid) ───────────
    /// <summary>A day cell, verbatim the ConcertDateFlyout picker's rung (38 × 32 with a 4-DIP gutter). FIXED, never
    /// <c>Grow = 1</c>: a stretchy cell turns the heatmap into a row of ragged accent bars whose width says nothing,
    /// and two months in different grid columns then disagree on what "one day" looks like.</summary>
    const float CalCellW = 38f, CalCellH = 32f;
    /// <summary>The weekday-initial band above the grid — the picker's 38 × 20 header cell.</summary>
    const float CalHeaderH = 20f;
    /// <summary>Both gutters (between columns and between week rows), the picker's 4.</summary>
    const float CalGap = Spacing.XS;
    /// <summary>A month card's exact content width: 7 cells + 6 gutters = 290. It is the card's <c>Width</c> AND the
    /// grid's min cell width, so a column break can never cut a month in half.</summary>
    const float CalGridW = 7f * CalCellW + 6f * CalGap;
    /// <summary>The card's title line: <c>WaveeType.ModuleHeader</c> is Ui.Subtitle's 20/28 ramp, so the header row
    /// measures exactly one 28-DIP line box (its trailing meta caption is shorter and centers inside it).</summary>
    const float CalTitleH = 28f;
    /// <summary>The DETERMINISTIC height of a <see cref="CalendarMonthCard"/> with <paramref name="weeks"/> week rows —
    /// the ModuleHeader line (Ui.Subtitle's 28 line box) + the card gap + the weekday band + one gutter + the week rows
    /// and their gutters. 4/5/6 weeks ⇒ 200/236/272.
    /// <para>It exists because <c>RepeatLayout.GridFit</c> takes a row-height ESTIMATE, and the old guess
    /// (<c>RailAlbum + Thumb64 + PageWide</c>) was ~120 DIP short of a real 6-week card — which is what visibly cut the
    /// bottom week row off every card in the overview. Derived from the same consts the card lays out with, so the two
    /// cannot drift.</para></summary>
    static float MonthCardHeight(int weeks)
        => CalTitleH + Spacing.S + CalHeaderH + CalGap + weeks * (CalCellH + CalGap) - CalGap;

    sealed class CalendarOverviewSurface : Component
    {
        readonly RecentsPage _page;
        readonly int _shapeEpoch;
        public CalendarOverviewSurface(RecentsPage page, int shapeEpoch) { _page = page; _shapeEpoch = shapeEpoch; }

        public override Element Render()
        {
            _ = _page._epoch.Value;
            DateOnly selected = _page._calendarDay.Value;
            var calendar = _page._shape.Calendar;
            Element months = ItemsView.Create(
                calendar.Months.Length,
                i => Embed.Comp(() => new CalendarMonthCard(_page, i)) with
                { Key = "recents-month:" + calendar.Months[i].Year + ":" + calendar.Months[i].Month },
                // The card's OWN width as the min cell width, and the TALLEST month's exact height as the row estimate:
                // a grid sizes every row from one estimate, so seeding it from a 4-week month clips the 6-week cards
                // until they measure. Over-estimating corrects downward invisibly; under-estimating is the visible bug.
                RepeatLayout.GridFit(CalGridW, Spacing.PageWide, MonthCardHeight(RecentsView.MaxWeeks(calendar))),
                new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.None,
                    Selector = SelectorVisual.None,
                    Controller = _page._calendarController,
                    Grow = 1f,
                    Overscan = 1,
                    KeyOf = i => "recents-month:" + calendar.Months[i].Year + ":" + calendar.Months[i].Month,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = "recents-calendar:" + _shapeEpoch,
                        AutoEdgeFade = true,
                    },
                });
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
                // No dock reserve here either: the shell clips the content region above the docked player bar
                // (WaveeShell), so the calendar grid already ends at the bar's top edge like every other surface.
                Padding = default,
                OnPointerExit = _page.ResetCalendarDay,
                Children =
                [
                    // ONE band, not a three-line stack: the day + its readout are one reading (a label over its own
                    // sentence), and the legend is a KEY to the grid below — it belongs beside them, not under them.
                    // Two 16-DIP caption lines and the swatch row on one line costs the grid ~40 DIP less chrome.
                    new BoxEl
                    {
                        Direction = 0, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
                        Children =
                        [
                            new BoxEl
                            {
                                // Grow + MinWidth 0: the readout is the only line here that can be long (it carries a
                                // "top: <title>" clause), so it — never the legend — is what gives width back.
                                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                                Children =
                                [
                                    Caption(selected.ToString("dddd d MMMM", _page._culture)) with
                                    { Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                    Caption(_page.CalendarReadout(selected)) with
                                    { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                ],
                            },
                            Legend(_page),
                        ],
                    },
                    months,
                ],
            };
        }
    }

    sealed class CalendarMonthCard : Component
    {
        readonly RecentsPage _page;
        readonly int _monthIndex;
        public CalendarMonthCard(RecentsPage page, int monthIndex) { _page = page; _monthIndex = monthIndex; }

        public override Element Render()
        {
            var month = _page._shape.Calendar.Months[_monthIndex];
            DateOnly today = DateOnly.FromDateTime(_page._now.ToOffset(_page._now.Offset).DateTime);
            // The weekday band: 7 cells at the EXACT cell width, so an initial sits over its own column. Rotated by the
            // culture's own FirstDayOfWeek (a nl-NL grid starts on Monday) — the same rotation DayDensity computes
            // FirstDayOffset with, so the band and the offset can never disagree about which column is column 0.
            var weekNames = new Element[7];
            var firstDay = _page._culture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < weekNames.Length; i++)
            {
                int day = ((int)firstDay + i) % 7;
                weekNames[i] = new BoxEl
                {
                    Width = CalCellW, Height = CalHeaderH, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Caption(_page._culture.DateTimeFormat.AbbreviatedDayNames[day]) with
                    { Color = Tok.TextSecondary, MaxLines = 1 }],
                };
            }

            var rows = new List<Element>(1 + month.WeekCount)
            {
                new BoxEl { Direction = 0, Gap = CalGap, Children = weekNames },
            };
            DateOnly first = new(month.Year, month.Month, 1);
            DateOnly gridFirst = first.AddDays(-month.FirstDayOffset);
            // month.WeekCount, never a fixed 6: a February that starts on the culture's first weekday occupies FOUR
            // rows, and drawing six left a dead band under the card that read as clipping.
            for (int week = 0; week < month.WeekCount; week++)
            {
                var cells = new Element[7];
                for (int column = 0; column < 7; column++)
                {
                    DateOnly date = gridFirst.AddDays(week * 7 + column);
                    cells[column] = _page.CalendarCell(date, month, today, _monthIndex);
                }
                rows.Add(new BoxEl { Direction = 0, Gap = CalGap, Children = cells });
            }

            string monthTitle = first.ToString("MMMM", _page._culture);
            // ONE line, title + subdued fact — the three-line stat block (play count + "Busiest: …") is gone: the grid
            // IS the busiest-day statement, said in colour, and repeating it in prose under every card was the clutter.
            string meta = month.TotalPlays > 0
                ? Strings.Recents.PlayCount(month.TotalPlays)
                    + (month.IsCurrentMonth ? " · " + Loc.Get(Strings.Recents.SoFar) : "")
                : Loc.Get(Strings.Recents.NothingPlayed);
            return new BoxEl
            {
                Direction = 1, Width = CalGridW, Gap = Spacing.S,
                OnPointerExit = _page.ResetCalendarDay,
                Children =
                [
                    // Deliberately NOT WaveeType.ModuleHeader(title, meta) — that alias is a SpanTextEl, whose Color is
                    // a plain ColorF with no BrushTransitionMs, so it cannot carry the W2.9 accent BIND below. Two nodes
                    // on one row instead, centered rather than bottom-aligned (the engine has no FlexAlign.Baseline —
                    // see WaveeType.ModuleHeader(string,string)'s remarks on why bottom-aligning reads as a mistake).
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                        Children =
                        [
                            WaveeType.ModuleHeader(monthTitle) with
                            {
                                // W2.9: Prop-bound so a section crossing while the overview happens to be open glides
                                // this ink rather than requiring CalendarMonthCard itself to re-render.
                                Color = month.IsCurrentMonth ? Prop.Of(() => _page._accent.Value.Ink) : Tok.TextPrimary,
                                BrushTransitionMs = RecentsPage.AccentTransitionMs,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                            },
                            Caption(meta) with
                            {
                                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                Grow = 1f, Basis = 0f, MinWidth = 0f,
                            },
                        ],
                    },
                    new BoxEl { Direction = 1, Gap = CalGap, Children = rows.ToArray() },
                ],
            };
        }
    }

    RecentsCalendarDay? CalendarDay(DateOnly date)
    {
        var s = _shape;
        int monthIndex = MonthFor(date);
        if ((uint)monthIndex >= (uint)s.Calendar.Months.Length) return null;
        var month = s.Calendar.Months[monthIndex];
        int day = date.Day - 1;
        return (uint)day < (uint)month.Days.Length ? month.Days[day] : null;
    }

    string CalendarReadout(DateOnly date)
    {
        var s = _shape;
        var day = CalendarDay(date);
        int plays = day?.PlayCount ?? 0;
        // A quiet day says so in words. "0 plays" is a plural template doing arithmetic at the reader — the app
        // already owns the sentence for exactly this state, and the month card's meta line uses the same one.
        string readout = plays > 0 ? Strings.Recents.PlayCount(plays) : Loc.Get(Strings.Recents.NothingPlayed);
        if (day?.TopItem is { } top && (uint)top.OriginalRowIndex < (uint)s.Rows.Length
            && FactsFor(s.Rows[top.OriginalRowIndex]).Title is { Length: > 0 } title)
            readout += " · " + Strings.Recents.Mostly(title);
        // W2.7: the ONE place this readout is built — the calendar cell tooltip and the sticky live readout both call
        // through here, so a jumpable day (the flat list actually has a header for it) states the affordance in both
        // without a second call site to keep in sync. Not jumpable ⇒ no dead "click here" promise.
        if (HeaderFlatFor(date) >= 0)
            readout += " · " + Strings.Recents.JumpToDay(date.ToString("d MMM", _culture));
        return readout;
    }

    string CalendarTooltip(DateOnly date)
        => date.ToString("dddd d MMMM", _culture) + " · " + CalendarReadout(date);

    void ResetCalendarDay()
        => _calendarDay.Value = DateOnly.FromDateTime(_now.ToOffset(_now.Offset).DateTime);

    /// <summary>W2.9: an INSTANCE method now — the ramp's hue is this page's dynamic accent (<see cref="_accent"/>'s
    /// Ink, substituting for the old fixed <see cref="WaveeAccent.Decor"/>), the alpha formula unchanged. Prop-bound
    /// so the whole heatmap glides colour on a section crossing without any cell re-rendering.</summary>
    Prop<ColorF> DensityFill(int level)
    {
        if (level <= 0) return ColorF.Transparent;
        float t = Math.Clamp(level, 1, 5) / 5f;
        return Prop.Of(() =>
        {
            ColorF accent = _accent.Value.Ink;
            float alpha = Tok.AccentSubtle.A + (accent.A - Tok.AccentSubtle.A) * t;
            return accent with { A = alpha };
        });
    }

    /// <summary>W2.5: the "Quieter → Busier" key for the heatmap's 5-level ramp — five swatches at the exact
    /// <see cref="DensityFill"/> levels the calendar cells themselves paint, so the legend can never drift from what
    /// it explains.</summary>
    static Element Legend(RecentsPage page)
    {
        var swatches = new Element[5];
        for (int level = 1; level <= 5; level++)
            swatches[level - 1] = new BoxEl
            {
                Width = 12f, Height = 12f, Shrink = 0f, Corners = Radii.ControlAll, Fill = page.DensityFill(level),
                BrushTransitionMs = AccentTransitionMs,
            };
        var kids = new List<Element>(7)
        {
            Caption(Loc.Get(Strings.Recents.Legend.Quieter)) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f },
        };
        kids.AddRange(swatches);
        kids.Add(Caption(Loc.Get(Strings.Recents.Legend.Busier)) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
            Children = kids.ToArray(),
        };
    }

    Element CalendarCell(DateOnly date, RecentsCalendarMonth month, DateOnly today, int monthIndex)
    {
        // A lead/trail day belongs to the NEIGHBOURING month's card, which states it there with its own heat. Drawing
        // it here as a dimmed numeral put the same day on screen twice with two different densities, and gave it a
        // tooltip that read the other card's data. So: a blank spacer that only holds the column open.
        if (date.Year != month.Year || date.Month != month.Month)
            return new BoxEl { Width = CalCellW, Height = CalCellH, Shrink = 0f };

        var day = CalendarDay(date);
        bool isToday = date == today;
        bool hasRows = HeaderFlatFor(date) >= 0;
        // ONE today cue: the numeral goes Semibold in the page's dynamic accent ink. The 700 weight (outside the app's
        // 400/600 type policy), the accent dot, the accent border ring and the accent glow shadow are all deleted —
        // four simultaneous cues for one day, three of which spent the accent on GEOMETRY the accent-role rules
        // reserve for Decor (a wash, an ink), and the ring in particular competed with the density fill it sat on.
        var numeral = Body(date.Day.ToString(_culture)) with
        {
            Weight = (ushort)(isToday ? 600 : 400),
            Color = isToday ? Prop.Of(() => _accent.Value.Ink) : Tok.TextPrimary,
            BrushTransitionMs = AccentTransitionMs,
            MaxLines = 1,
        };
        var cell = new BoxEl
        {
            Width = CalCellW, Height = CalCellH, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            // W2.9: the heat itself — a Prop-bound Decor wash, so the whole heatmap glides colour on a section
            // crossing without one cell re-rendering.
            Fill = DensityFill(day?.DensityLevel ?? 0),
            BrushTransitionMs = AccentTransitionMs,
            Role = hasRows ? AutomationRole.Button : AutomationRole.None,
            Focusable = hasRows,
            TabStop = hasRows,
            Cursor = hasRows ? CursorId.Hand : CursorId.Arrow,
            OnHoverMove = _ => _calendarDay.SetIfChanged(date),
            OnClick = hasRows ? () =>
            {
                _calendarDay.Value = date;
                _zoomController.ZoomInTo(monthIndex);
            } : null,
            OnFocusChanged = hasRows ? focused =>
            {
                if (focused) _calendarDay.SetIfChanged(date); else ResetCalendarDay();
            } : null,
            Children = [numeral],
        };
        // Straight through: the cell carries its own fixed 38×32 now, so ToolTip.Wrap has no flex contract left to
        // drop and the extra Grow-carrying box the stretchy cell needed is dead weight.
        return ToolTip.Wrap(cell, CalendarTooltip(date));
    }

    sealed class RecentsListSurface : Component
    {
        readonly RecentsPage _page;
        readonly string? _token;
        readonly int _shapeEpoch;

        public RecentsListSurface(RecentsPage page, string? token, int shapeEpoch)
        {
            _page = page; _token = token; _shapeEpoch = shapeEpoch;
        }

        public override Element Render()
        {
            // W1.1: the layout lives ON the Shape now (built/reused in Recut) — no per-mount UseMemo needed, and no
            // separate assignment to keep it in sync with RecentsRail: both read the SAME _page._shape.Layout.
            var layout = _page._shape.Layout;
            var view = UseComputed(() => new RowsView(_page._epoch.Value, _page._vc.Version.Value, _page._vc));
            var items = UseMemo(() => BoundItems.Project(
                view,
                static s => s.Rows.CountOr0,
                static (s, i) => s.Rows[i],
                EmptyFlat), DepKey.Empty);

            Element list = ItemsView.CreateBound<RecentsFlatItem>(
                items,
                (BoundItemScope<RecentsFlatItem> scope) => Embed.Comp(() => new RecentsRowSlot(_page, scope)),
                RepeatLayout.Measured(layout),
                new ListOptions<RecentsFlatItem>
            {
                // The rows are cards with their own chrome; a list selector here would be a second, competing cue.
                SelectionMode = ItemsSelectionMode.None,
                Selector = SelectorVisual.None,
                IsItemInvokedEnabled = true,
                OnInvokedTyped = (_, item) => _page.InvokeFlat(item),
                ItemTextTyped = (_, item) => _page.TextFor(item),
                Controller = _page._listController,
                Overscan = OverscanRows,
                Grow = 1f,
                // One recycle pool per row KIND: a group card's slot must never rebind into the (defensive) track-grid
                // shape — a cross-shape reuse forces a full rebuild instead of a cheap rebind.
                ContentType = _page.ContentTypeOf,
                Scroll = new ScrollOptions
                {
                    ScrollKey = "recents:" + (_token ?? "all"),
                    AutoEdgeFade = true,
                    VerticalScrollController = _page._scrollController,
                    SuppressScrollBar = true,
                    OnScrollGeometryChanged = (_page.ProjectSticky, _page.UpdateSticky),
                    // The app's canonical pinned-chrome offset model (DetailTracks' vertical arm): the recyclable rows
                    // are band-clipped at exactly the inset the sticky overlay occupies, feathered by the same 24-DIP
                    // StickyFadeBand, so a row scrolls UNDER the band instead of showing through a translucent plate.
                    // Static values suffice here — unlike DetailTracks there is no collapsing hero, so the pinned band
                    // is always exactly one DateHeaderHeight tall.
                    ItemClipTopInset = DateHeaderHeight,
                    ItemClipTopFadeBand = Wavee.Features.Detail.DetailVerticalLayout.StickyFadeBand,
                },
                // The engine's own cold-realize stagger: bounded to the REALIZED window by construction, which is the
                // only kind of entrance a 1,708-row list may have.
                Entrance = new EntranceOptions { StaggerColdRealize = true },
                // The point of the page: the realized window moved → hydrate what it still misses.
                OnVisibleRange = _page.OnVisibleRange,
            }) with { Key = "recents-list:" + (_token ?? "all") + ":" + _shapeEpoch };

            Element overlay = Embed.Comp(() => new StickyDayHeader(_page)) with
            { Key = "recents-sticky:" + (_token ?? "all") + ":" + _shapeEpoch };
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                ZStack = true, ClipToBounds = true,
                Children = [list, overlay],
            };
        }
    }

    /// <summary>The recycle-pool id per row KIND — a group card's slot must never rebind into another kind's shape.</summary>
    int ContentTypeOf(int index)
    {
        var s = _shape;
        var sections = s.Sections;
        if ((uint)index >= (uint)sections.Items.Length) return 0;
        var flat = sections.Items[index];
        if (flat.Kind == RecentsFlatItemKind.DateHeader) return 0;
        int rowIndex = flat.OriginalRowIndex;
        return (uint)rowIndex < (uint)s.Rows.Length ? 1 + (int)s.Rows[rowIndex].Kind : 1;
    }

    // ── rows ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A recents row is a card ONE realized slot renders. Its own component so the slot re-renders on its own
    /// item subscription (an index rebind, or a hydration epoch) without the page re-rendering.</summary>
    void InvokeFlat(RecentsFlatItem item)
    {
        if (item.Kind != RecentsFlatItemKind.Row) return;
        var s = _shape;
        int rowIndex = item.OriginalRowIndex;
        if ((uint)rowIndex >= (uint)s.Rows.Length) return;
        var row = s.Rows[rowIndex];
        if (CanExpand(row)) ToggleExpanded(row, rowIndex);
        else Open(row);
    }

    string TextFor(RecentsFlatItem item)
    {
        var s = _shape;
        if (item.Kind == RecentsFlatItemKind.DateHeader)
            return (uint)item.DayIndex < (uint)s.Sections.HeaderLabels.Length ? s.Sections.HeaderLabels[item.DayIndex] : "";
        if (item.Kind != RecentsFlatItemKind.Row) return "";
        int rowIndex = item.OriginalRowIndex;
        return (uint)rowIndex < (uint)s.Rows.Length ? FactsFor(s.Rows[rowIndex]).Title ?? "" : "";
    }

    /// <summary>The scroll-geometry change key for <see cref="UpdateSticky"/>'s cheap gate — the action only fires
    /// when this projected value actually changes. W1.4c widens it to also carry the layout's measured-extent version,
    /// so a drawer-driven extent correction bumps this key on the SAME scroll-geometry callback that already fires on
    /// every meaningful scroll frame (observers run pre-publish every frame), instead of needing a second observer.
    /// <para>Bit layout (documented for review — nothing downstream ever decodes this value, only compares it for
    /// equality): bits 63..24 (40 bits) = sticky header's flat index + 1 (0 encodes "no sticky header"); bits 23..8
    /// (16 bits) = the low 16 bits of <see cref="GroupedListVirtualLayout.MeasuredVersion"/> (a monotone counter — the
    /// truncation only matters if 65,536 corrections land between two frames, and equality is all this key needs);
    /// bits 7..0 (8 bits) = the quantized push (<see cref="Spacing.XXS"/> units), offset by 128 so it packs as an
    /// unsigned byte — push is always ≤ 0 and bounded in magnitude by <see cref="DateHeaderHeight"/>.</para></summary>
    long ProjectSticky(ScrollGeometry geometry)
    {
        StickyMetrics(geometry, out int header, out float push);
        int measured = _shape.Layout.MeasuredVersion & 0xFFFF;
        int quantizedPush = (int)MathF.Round(push / Spacing.XXS);
        long headerPart = (long)(header + 1) << 24;
        long measuredPart = (long)(uint)measured << 8;
        long pushPart = (uint)(byte)Math.Clamp(quantizedPush + 128, 0, 255);
        return headerPart | measuredPart | pushPart;
    }

    void UpdateSticky(ScrollGeometry geometry)
    {
        StickyMetrics(geometry, out int header, out float push);
        float quantized = MathF.Round(push / Spacing.XXS) * Spacing.XXS;
        if (_stickyHeader.Peek() != header) _stickyHeader.Value = header;
        if (!_stickyPush.Peek().Equals(quantized)) _stickyPush.Value = quantized;
        _railMeasuredVersion.SetIfChanged(_shape.Layout.MeasuredVersion);

        // W2.9: the dynamic accent (and the wash WashCard now shares a source row with) is quantized to the DAY the
        // sticky header belongs to — never the more volatile flat header index itself, so a drawer-driven extent
        // correction that shifts `header` within the SAME day cannot reshuffle it. One coalesced post per turn, the
        // same arm-then-post idiom Pump uses below: the LATEST day is recorded into a plain field immediately, and
        // the posted continuation re-reads that field fresh (never a value captured at arm time) — so several day
        // changes reported before the post drains still resolve to exactly one commit: the final one.
        var sections = _shape.Sections;
        int day = (uint)header < (uint)sections.Items.Length ? sections.Items[header].DayIndex : -1;
        _pendingAccentDay = day;
        if (_accentDay.Peek() != day && !_accentArmed)
        {
            _accentArmed = true;
            _post(_resolveAccentDay);
        }
        ProbeStickyAlignment(geometry, header);
    }

    /// <summary>The coalesced continuation <see cref="UpdateSticky"/> arms — reads <see cref="_pendingAccentDay"/>
    /// FRESH (never the day captured when it armed) and commits it via <c>SetIfChanged</c>, so a scroll burst that
    /// crossed several days before this drained still moves the accent exactly once, to the day it actually settled
    /// on.</summary>
    void ResolveAccentDay()
    {
        _accentArmed = false;
        _accentDay.SetIfChanged(_pendingAccentDay);
    }

    /// <summary>Issue 2 DEBUG probe: Today-over-July is not a grouping bug if the same PlayedAtMs feeds both
    /// the sticky header and the realized rows. Fail when a row's calendar day disagrees with its section header,
    /// or when the stuck header's day disagrees with the first visible row.</summary>
    [Conditional("DEBUG")]
    void ProbeStickyAlignment(ScrollGeometry geometry, int stickyFlat)
    {
        var s = _shape;
        var layout = s.Layout;
        var sections = s.Sections;
        var rows = s.Rows;
        if (sections.Items.Length == 0) return;
        TimeSpan offset = _now.Offset;
        ProbeFlatDate(sections, rows, stickyFlat, offset, "sticky");
        int at = layout.IndexAt(geometry.OffsetY, 0f);
        for (int d = -2; d <= 2; d++)
            ProbeFlatDate(sections, rows, at + d, offset, "visible");

        if ((uint)stickyFlat >= (uint)sections.Items.Length) return;
        int stickyDay = sections.Items[stickyFlat].DayIndex;
        int visibleDay = -1;
        int last = Math.Min(sections.Items.Length, at + 6);
        for (int i = Math.Max(0, at); i < last; i++)
        {
            if (sections.Items[i].Kind != RecentsFlatItemKind.Row) continue;
            visibleDay = sections.Items[i].DayIndex;
            break;
        }
        if (visibleDay < 0 || stickyDay == visibleDay) return;
        Debug.Fail($"recents sticky day {stickyDay} != first visible row day {visibleDay} (flat sticky={stickyFlat} at={at})");
    }

    [Conditional("DEBUG")]
    static void ProbeFlatDate(RecentsSections sections, RecentsRow[] rows, int flat, TimeSpan offset, string where)
    {
        if ((uint)flat >= (uint)sections.Items.Length) return;
        var item = sections.Items[flat];
        if (item.Kind != RecentsFlatItemKind.Row) return;
        if ((uint)item.OriginalRowIndex >= (uint)rows.Length) return;
        if ((uint)item.DayIndex >= (uint)sections.HeaderDates.Length) return;
        long unixMs = rows[item.OriginalRowIndex].PlayedAtMs;
        DateOnly played = unixMs <= 0
            ? DateOnly.MinValue
            : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(offset).DateTime);
        DateOnly header = sections.HeaderDates[item.DayIndex];
        if (played == header) return;
        Debug.Fail($"recents {where} flat {flat}: row day {played} != header {header} (DayIndex={item.DayIndex})");
    }

    void StickyMetrics(ScrollGeometry geometry, out int header, out float push)
    {
        var s = _shape;
        var layout = s.Layout;
        var sections = s.Sections;
        if (sections.HeaderIndices.Length == 0)
        {
            header = -1; push = 0f; return;
        }
        header = layout.StickyHeaderIndexAt(geometry.OffsetY);
        if (header < 0 || (uint)header >= (uint)sections.Items.Length)
        {
            header = -1; push = 0f; return;
        }
        int day = sections.Items[header].DayIndex;
        if ((uint)(day + 1) >= (uint)sections.HeaderIndices.Length)
        {
            push = 0f; return;
        }
        int next = sections.HeaderIndices[day + 1];
        push = MathF.Min(0f,
            layout.OffsetOf(next, geometry.ViewportW) - geometry.OffsetY - DateHeaderHeight);
    }

    void InvokeDay(int dayIndex)
    {
        var s = _shape;
        if ((uint)dayIndex < (uint)s.Sections.HeaderDates.Length)
            DateHeaderInvoked?.Invoke(s.Sections.HeaderDates[dayIndex]);
    }

    Element DayHeader(int dayIndex, bool overlay) => DayHeader(_shape, dayIndex, overlay);

    Element DayHeader(Shape s, int dayIndex, bool overlay)
    {
        string label = (uint)dayIndex < (uint)s.Sections.HeaderLabels.Length ? s.Sections.HeaderLabels[dayIndex] : "";
        // The per-day count is a PURE rule (it has to exclude the trailing dock spacer from the last day) and lives in
        // RecentsView beside the projection that emits that spacer, read here off the SAME shape snapshot.
        int n = RecentsView.CountForDay(s.Sections, dayIndex);
        return new BoxEl
        {
            Direction = 0, Height = DateHeaderHeight, Grow = overlay ? 1f : 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Fill = overlay ? Tok.FillLayerDefault : ColorF.Transparent,
            // The PINNED copy is a duplicate of an inline header that is still in the tree — one day, two tab stops and
            // two announcements. The inline arm keeps the semantics; the overlay keeps only the click affordance (it is
            // the same target under the pointer), so a11y sees exactly one zoom-out control per day.
            Role = overlay ? AutomationRole.None : AutomationRole.Button,
            Focusable = !overlay, Cursor = CursorId.Hand,
            OnClick = () => InvokeDay(dayIndex),
            Children =
            [
                // W2.6/W2.9: the header IS the semantic zoom-out affordance — the hover-accent ink says so without a
                // second re-render for the HOVER transition itself (TextEl's own hover ramp is compositor-only). The
                // COLOUR it eases to is now this page's dynamic accent rather than the fixed WaveeAccent.Decor;
                // HoverColor is a plain (non-bindable) ColorF, so — unlike Fill/BorderColor above — this DOES read
                // (and subscribe to) _accent directly: the realized date-header slot re-renders on a section
                // crossing, same as it already does for _stickyHeader/_epoch, so the cost is bounded to the small
                // realized window rather than the whole list.
                WaveeType.ModuleHeader(label) with
                {
                    Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    HoverColor = _accent.Value.Ink, BrushTransitionMs = WaveeMotion.Faster,
                },
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, AlignSelf = FlexAlign.Center,
                    Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
                Caption(n > 0 ? Strings.Recents.ItemCount(n) : "") with
                {
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                },
            ],
        };
    }

    sealed class StickyDayHeader : Component
    {
        readonly RecentsPage _page;
        public StickyDayHeader(RecentsPage page) => _page = page;

        public override Element Render()
        {
            // W1.6: a midnight relabel bumps _epoch (not _shapeEpoch — the grouping itself didn't change), so the
            // pinned overlay must subscribe here too or it would keep showing yesterday's day word after rollover.
            _ = _page._epoch.Value;
            int flatIndex = _page._stickyHeader.Value;
            var sections = _page._shape.Sections;
            int day = (uint)flatIndex < (uint)sections.Items.Length
                ? sections.Items[flatIndex].DayIndex : -1;
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f,
                Height = DateHeaderHeight,
                HitTestVisible = day >= 0,
                HitTestPassThrough = true,
                Opacity = day >= 0 ? 1f : 0f,
                Transform = Prop.Of(() => Affine2D.Translation(0f, _page._stickyPush.Value)),
                OnPointerWheel = ForwardWheel,
                Children = [_page.DayHeader(day, overlay: true)],
            };
        }

        void ForwardWheel(WheelEventArgs e)
        {
            // WheelEventArgs.Delta is the same signed DIP the viewport path consumes (InputDispatcher.ScrollBy
            // does offset + delta; ItemsView.ControllerScrollBy does OffsetY + request.Delta). Do not negate.
            // Animated: rides the engine's WheelAnimating chase (with target accumulation) like wheel over the list
            // body — a hard snap here felt alien beside it and arrested any in-flight fling.
            _page._scrollController.ScrollBy(e.Delta, animate: true);
            e.Handled = true;
        }
    }

    sealed class RecentsRowSlot : Component
    {
        readonly RecentsPage _page;
        readonly BoundItemScope<RecentsFlatItem> _scope;

        public RecentsRowSlot(RecentsPage page, BoundItemScope<RecentsFlatItem> scope) { _page = page; _scope = scope; }

        public override Element Render()
        {
            var flat = _scope.Item.Value;
            _ = _scope.Index.Value;
            if (flat.Kind == RecentsFlatItemKind.DateHeader) return _page.DayHeader(flat.DayIndex, overlay: false);
            int rowIndex = flat.OriginalRowIndex;
            var s = _page._shape;
            if ((uint)rowIndex >= (uint)s.Rows.Length) return new BoxEl { Height = RowHeight };
            var row = s.Rows[rowIndex];
            // RE-PUSHED PROPS, not ctor args, and NO Key. A recycled slot rebinds by writing one index signal — the
            // instance is deliberately REUSED, so a factory closure would freeze the row it first mounted with and the
            // slot would render that row forever (the "Tue/Wed under a Yesterday header" bug). And a Key here is inert:
            // a component's single root child is paired by ElementTypeId alone (Reconciler.ReconcileSingleChild), which
            // is exactly what the ReuseGuard.KeyIgnoredInSingleChildSlot tripwire documents. Props are the mechanism the
            // component-props contract reserves for "same instance, changed data".
            return Embed.Comp(new HydratedRecentsRow.Props(_page, row, rowIndex), () => new HydratedRecentsRow());
        }
    }

    sealed class HydratedRecentsRow : Component
    {
        /// <summary>The live row this slot currently stands for. An immutable record so an unchanged re-push is
        /// equality-coalesced (no child re-render) while a REBIND — always a changed <see cref="RowIndex"/> — pushes
        /// through immediately.</summary>
        internal sealed record Props(RecentsPage Page, RecentsRow Row, int RowIndex);

        public override Element Render()
        {
            var p = UseProps<Props>();
            var page = p.Page;
            RecentsRow row = p.Row;
            var facts = UseLoadable<RowFacts>();
            int epoch = page._epoch.Value;
            bool expanded = string.Equals(page._expandedRow.Value, row.ItemId, StringComparison.Ordinal);
            var bridge = UseContext(PlaybackBridge.Slot);
            var lib = UseContext(LibraryBridge.Slot);
            var acts = UseContext(ActionServices.Slot);
            var overlay = UseContext(Overlay.Service);
            // Keyed on (epoch, RowIndex): a hydration landing re-resolves the same row, and a RECYCLE (a new index into
            // the same reused instance) re-resolves the new one — including SetPending, so a recycled slot never shows
            // the previous row's facts while the new one is still a pointer. A same-index item change can only come
            // from a snapshot replace, which remounts the whole list via its "recents-list:…:" + _shapeEpoch key.
            UseEffect(() =>
            {
                var resolved = page.FactsFor(row);
                if (resolved.Title is { Length: > 0 }) facts.SetReady(resolved);
                else facts.SetPending(default);
            }, DepKey.From(epoch, p.RowIndex));
            return Skel.Region(facts,
                content: resolved => page.RowContent(row, resolved, p.RowIndex, expanded, bridge, lib, acts, overlay),
                reveal: SkelReveal.FadeOnly,
                smoothResize: false);
        }
    }

    /// <summary>Resolve a row's display facts FROM THE STORE. Liked Songs is answered locally — the app ships that cover
    /// and that name, so the one entity kind the catalogue kinds cannot address costs no request at all.</summary>
    RowFacts FactsFor(RecentsRow row)
    {
        if (RecentsView.HydrationUri(row) is not { Length: > 0 } uri) return default;
        return FactsFor(uri);
    }

    RowFacts FactsFor(string uri)
    {
        var kind = RecentsList.EntityKindOf(uri);
        if (kind == RecentsEntityKind.Collection)
            return new RowFacts(Loc.Get(Strings.Detail.LikedSongs), null, null);
        if (_store is not { } store) return default;
        return kind switch
        {
            RecentsEntityKind.Playlist => store.GetPlaylist(uri) is { } p
                ? new RowFacts(NullIfEmpty(p.Name), OwnerSubtitleFor(p), p.Cover) : default,
            RecentsEntityKind.Album => store.GetAlbum(uri) is { } a
                ? new RowFacts(NullIfEmpty(a.Name), ArtistNames(a.Artists), a.Cover) : default,
            RecentsEntityKind.Artist => store.GetArtist(uri) is { } ar
                ? new RowFacts(NullIfEmpty(ar.Name), null, ar.Image) : default,
            RecentsEntityKind.Show => store.GetShow(uri) is { } sh
                ? new RowFacts(NullIfEmpty(sh.Name), NullIfEmpty(sh.Publisher), sh.Cover) : default,
            RecentsEntityKind.Episode => store.GetEpisode(uri) is { } ep
                ? new RowFacts(NullIfEmpty(ep.Title), NullIfEmpty(ep.ShowName), ep.Image) : default,
            RecentsEntityKind.Track => store.GetTrack(uri) is { } t
                ? new RowFacts(NullIfEmpty(t.Title), ArtistNames(t.Artists), t.Image) : default,
            _ => default,
        };
    }

    /// <summary>W2.8 (C1): never a raw base62 owner id. Resolves the SAME way <see cref="StoreLibrarySource.OverlayOwner"/>
    /// already does for the Library surface — <c>Owner.Id</c> first, the store's own <c>OwnerName</c> as the raw-id
    /// fallback — then hands the store's own name, the raw id, and whatever <c>IStore.GetOwner</c> has
    /// already resolved to <see cref="RecentsView.OwnerSubtitle"/>, which owns the actual decision (resolved name wins;
    /// the store name shows only when it is more than the id parroted back; otherwise null, never the bare id).</summary>
    string? OwnerSubtitleFor(Playlist p)
    {
        string? rawOwnerId = p.Owner?.Id is { Length: > 0 } id ? id : NullIfEmpty(p.OwnerName);
        string? resolvedName = rawOwnerId is { Length: > 0 } raw ? _store?.GetOwner(raw)?.Name : null;
        return RecentsView.OwnerSubtitle(NullIfEmpty(p.OwnerName), rawOwnerId, resolvedName);
    }

    static readonly LayoutTransition DrawerReveal = new(
        TransitionChannels.Size | TransitionChannels.Opacity | TransitionChannels.Position,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Dy: -Spacing.S, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
        ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics(),
        Size: SizeMode.Reflow,
        Anchor: SizeAnchor.Leading);

    Element RowContent(RecentsRow row, RowFacts facts, int displayIndex, bool expanded,
                       PlaybackBridge? bridge = null, LibraryBridge? lib = null,
                       ActionServices? acts = null, IOverlayService? overlay = null)
    {
        if (row.ItemId.Length == 0) return new BoxEl { Height = RowHeight };
        // Unhydrated: the REAL row geometry with neutral placeholder tiles. Never empty space, and never an invented
        // string — the wire genuinely does not know this row's name yet.
        string uri = RecentsView.HydrationUri(row) ?? row.Uri;
        var kind = RecentsList.EntityKindOf(uri);
        // Liked Songs: the app's own cover art keys off the canonical collection uri, and `spotify:user:{id}:collection`
        // is the SAME entity under the recents surface's spelling. Handing the canonical one to the card is what makes
        // the bundled cover (and now-playing matching) resolve; navigation still goes through the shared dispatcher.
        string artUri = kind == RecentsEntityKind.Collection ? LikedSongsArtwork.Uri : uri;
        string when = RecentsView.PlayedAt(row.PlayedAtMs, _now, _culture);
        // "Played N tracks": the group's authoritative child_count (group_metadata field 1). NEVER ChildUris.Count —
        // the server truncates that list (a child_count of 11 arrived with 3 uris). A real PLURAL key, not a "×N"
        // glyph: the generator emits the typed Strings.Recents.PlayedCount(count) from the ICU template, so a language
        // whose one/other split differs from English gets its own branch instead of an English-shaped multiplier.
        var metaDecision = RecentsView.MetaFor(row);
        string phrase = metaDecision.Kind switch
        {
            RecentsMetaKind.PlayedCount => Strings.Recents.PlayedCount(metaDecision.Count),
            RecentsMetaKind.SavedCount => Strings.Recents.SavedCount(metaDecision.Count),
            _ => "",
        };
        // Prototype row: title + subtitle (two lines), time as its own trailing column — never a third meta line
        // inside the 64-DIP band.
        string owner = facts.Subtitle ?? "";
        string sub = phrase.Length == 0 ? owner
            : owner.Length == 0 ? phrase
            : phrase + " · " + owner;
        Element? savedLine = row.Reason == RecentsReason.Saved ? SavedMeta(sub.Length > 0 ? sub : phrase) : null;

        if (row.Kind == RecentsRowKind.Single)
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = [TrackRowContent(row, facts, displayIndex, uri, bridge, lib, acts, overlay) with { Key = "row" }],
            };

        bool canExpand = CanExpand(row);
        var menu = CardMenu(artUri, facts.Title ?? "", facts.Cover, sub.Length > 0 ? sub : owner, kind == RecentsEntityKind.Artist, acts, overlay);
        Element chevron = canExpand
            ? TrackRow.ExpandChevron(expanded, () => ToggleExpanded(row, displayIndex)) with { Transition = MotionTok.DisclosureChevron }
            : new BoxEl { Width = Spacing.XXL, Height = Spacing.XXL, Shrink = 0f };
        var trailingKids = new List<Element>(3);
        if (when.Length > 0)
            trailingKids.Add(Caption(when) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 });
        if (menu is not null) trailingKids.Add(TrackRow.MoreButton(true));
        trailingKids.Add(chevron);
        Element trailing = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = trailingKids.ToArray(),
        };

        Element card = MediaCard.Row(
            facts.Cover, facts.Title!, savedLine is null ? sub : "", artUri,
            circular: kind == RecentsEntityKind.Artist,
            onClick: canExpand ? () => ToggleExpanded(row, displayIndex) : () => Open(row),
            onPlay: () => Play(uri),
            typeChip: KindLabel(kind),
            metaContent: savedLine,
            trailing: trailing,
            plated: false,
            menu: menu,
            drag: CardDrag(kind, artUri, facts.Title ?? "", facts.Cover, acts),
            leadingArtwork: row.Reason == RecentsReason.Saved ? context => SavedArtwork(row, context) : null,
            // Shared-element source. Tagged for the FIRST occurrence of this uri only — uris repeat down a recents list
            // (~1,388 repeats on a real account) and two live nodes under one MorphId is a duplicate-key bug: the
            // engine's registry is last-writer-wins, and SetTaggedVisible/SetTaggedOpacity hide EVERY node carrying the
            // flying key, so a second tagged row would blank itself mid-fly.
            //
            // Nothing flies today, and the missing half is NOT DetailShell's `MorphKey = null` — it is the forward
            // CAPTURE. SharedTransition.Begin has no callers left anywhere in the app (3b80bbcf8 removed them), and
            // ConnectedAnimation captures nowhere else, so no snapshot is ever taken. See the long note at
            // DetailShell.cs's `MorphKey = null` for the full finding. This stays the source half of the pair, minted
            // through the ONE shared convention (MorphKeys.For) so the two sides cannot drift while it is dormant.
            //
            // 2026-08-12 decision: keep it dormant here too — the Zune×Fluent pass explicitly dropped connected
            // animation rather than wiring a forward capture for it. Follow-up recorded separately: "re-enable
            // connected flies app-wide" (RecentsPage + DetailShell + host publication).
            morphKey: Morphable(displayIndex) ? MorphKeys.For(DetailKindOf(kind), uri) : null);
        // Prototype rows are flat (transparent rest, hover wash). Card lift physics belong to plated home/search cards.
        Element primary = new BoxEl { Key = "row", Direction = 1, Children = [card] };
        Element? drawer = expanded && canExpand ? Drawer(row) : null;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = drawer is null ? [primary] : [primary, drawer],
        };
    }

    /// <summary>The defensive single-play arm. Zero occurrences in real captured data (9,446 items → 1,708 headers,
    /// 7,738 collapsed members, 0 ungrouped singles), but the grouping transform can still emit one, so the path exists
    /// and reuses the shared track cell rather than inventing a second row vocabulary.</summary>
    static Element SavedMeta(string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Icon(Icons.Check, 12f, Tok.SystemFillSuccess) with { Shrink = 0f },
            Caption(text) with
            {
                Color = Tok.SystemFillSuccess, Weight = 600, Grow = 1f, Basis = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    static bool CanExpand(RecentsRow row)
    {
        if (row.ChildCount <= 0 || row.ChildUris is not { Count: > 0 } children) return false;
        for (int i = 0; i < children.Count; i++) if (children[i].Length > 0) return true;
        return false;
    }

    void ToggleExpanded(RecentsRow row, int originalRowIndex)
    {
        // Event callbacks can outlive the shape generation that authored them for one reconcile turn. Resolve the
        // index against the current atomic Shape and reject a stale identity instead of opening a different row that
        // happened to inherit that index after a snapshot replacement.
        var shape = _shape;
        if ((uint)originalRowIndex >= (uint)shape.Rows.Length) return;
        var liveRow = shape.Rows[originalRowIndex];
        if (!string.Equals(liveRow.ItemId, row.ItemId, StringComparison.Ordinal)) return;
        var rowToFlat = shape.Sections.RowToFlat;
        if ((uint)originalRowIndex >= (uint)rowToFlat.Length || rowToFlat[originalRowIndex] < 0) return;

        bool closing = string.Equals(_expandedRow.Peek(), liveRow.ItemId, StringComparison.Ordinal);
        // An off-screen drawer has no realized node to remeasure, so snap its cached extent to the collapsed row
        // before the signal write. A REALIZED old row must NOT snap: ArrangeVirtualMeasured follows the SizeMode.Reflow
        // exit (the DetailTracks ExpandableRowSlot contract) so the slot eases expanded → 64 instead of leaving a
        // content-less gap at the old height.
        if (!IsFlatRealized(shape, _expandedOriginalRow))
            ResetExpandedExtent(shape, _expandedOriginalRow);
        _expandedOriginalRow = closing ? -1 : originalRowIndex;
        _expandedRow.Value = closing ? "" : liveRow.ItemId;
        if (!closing) HydrateChildren(liveRow, RecentsView.BatchCap);
    }

    void CollapseExpanded(Shape shape)
    {
        ResetExpandedExtent(shape, _expandedOriginalRow);
        _expandedOriginalRow = -1;
        if (_expandedRow.Peek().Length > 0) _expandedRow.Value = "";
    }

    void ResetExpandedExtent(Shape shape, int originalRowIndex)
    {
        var rowToFlat = shape.Sections.RowToFlat;
        if ((uint)originalRowIndex >= (uint)rowToFlat.Length) return;
        int flat = rowToFlat[originalRowIndex];
        if ((uint)flat >= (uint)shape.Sections.Items.Length
            || shape.Sections.Items[flat].Kind != RecentsFlatItemKind.Row) return;
        // BuildShape primes every table before an interaction can open, but ContentExtent keeps this helper safe for a
        // future caller that owns a freshly-created Shape too. An off-screen drawer has no realized node to enter
        // ArrangeVirtualMeasured's normal correction path, so use the controller's atomic measured-extent seam: it
        // preserves the visible anchor AND rebases any live wheel/touch/programmatic intent into the new coordinates.
        shape.Layout.ContentExtent(shape.Sections.Items.Length, 0f);
        if (!_listController.CorrectMeasuredExtent(shape.Layout, flat, RowHeight))
            shape.Layout.SetMeasured(flat, RowHeight, 0f); // not mounted on this shape: no live viewport needs anchoring
    }

    bool IsFlatRealized(Shape shape, int originalRowIndex)
    {
        var rowToFlat = shape.Sections.RowToFlat;
        if ((uint)originalRowIndex >= (uint)rowToFlat.Length) return false;
        int flat = rowToFlat[originalRowIndex];
        return flat >= 0 && _listController.IsItemRealized(flat);
    }

    Element Drawer(RecentsRow row)
    {
        var children = row.ChildUris!;
        var rendered = new List<Element>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            string uri = children[i];
            if (uri.Length == 0) continue;
            rendered.Add(new BoxEl
            {
                Key = "child-wrap:" + row.ItemId + ":" + i,
                Direction = 1,
                Enter = new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
                Transition = MotionTok.DisclosureExpand,
                Children =
                [
                    Embed.Comp(() => new HydratedChildRow(this, uri, i)) with
                    { Key = "child:" + row.ItemId + ":" + i },
                ],
            });
        }
        // Spine: a 1px divider descending from the parent art, children hanging off it (TrackVersionsPanel
        // connector idiom; proto `.kid-wrap{margin-left:30px;padding-left:26px;border-left}`). W2.9: a SECTION SPINE
        // is the one structural-looking element the app's own accent budget explicitly allows to carry accent (see
        // WaveeAccent's AccentDecor role, "section spines"; the plan's adopted recommendation is literally "spine
        // may take accent") — unlike the chevron beside it (stays Tok.TextSecondary) or DayHeader's plain divider
        // rule above it (stays Tok.StrokeDividerDefault, genuine chrome). Prop-bound so it glides with everything
        // else on a section crossing.
        return new BoxEl
        {
            Key = "drawer:" + row.ItemId,
            Direction = 0, MinWidth = 0f, Shrink = 0f, ClipToBounds = true,
            AlignItems = FlexAlign.Stretch,
            Margin = new Edges4(Spacing.XXL + Spacing.S, Spacing.XXS, 0f, Spacing.S),
            Animate = DrawerReveal,
            Children =
            [
                new BoxEl
                {
                    Width = 1f, Shrink = 0f, Fill = Prop.Of(() => _accent.Value.Ink),
                    BrushTransitionMs = AccentTransitionMs, HitTestVisible = false,
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 0f,
                    Padding = new Edges4(Spacing.L + Spacing.S, 0f, Spacing.S, 0f),
                    Stagger = WaveeMotion.StaggerMs,
                    Children = rendered.ToArray(),
                },
            ],
        };
    }

    sealed class HydratedChildRow : Component
    {
        readonly RecentsPage _page;
        readonly string _initialUri;
        readonly int _index;
        public HydratedChildRow(RecentsPage page, string initialUri, int index)
        {
            _page = page; _initialUri = initialUri; _index = index;
        }

        public override Element Render()
        {
            var facts = UseLoadable<RowFacts>();
            int epoch = _page._epoch.Value;
            var bridge = UseContext(PlaybackBridge.Slot);
            var lib = UseContext(LibraryBridge.Slot);
            var acts = UseContext(ActionServices.Slot);
            var overlay = UseContext(Overlay.Service);
            UseEffect(() =>
            {
                var resolved = _page.FactsFor(_initialUri);
                if (resolved.Title is { Length: > 0 }) facts.SetReady(resolved);
                else facts.SetPending(default);
            }, DepKey.From(epoch));
            return Skel.Region(facts, resolved => _page.ChildRowContent(_initialUri, resolved, _index, bridge, lib, acts, overlay),
                reveal: SkelReveal.FadeOnly, smoothResize: false);
        }
    }

    static readonly ColumnSet ChildCols = new(Album: false, By: false, Date: false, Video: false, Plays: false,
        Heart: true, Thumb: true, Actions: true);
    static readonly ColumnSet ChildColsNoArt = ChildCols with { Thumb = false };
    static readonly TrackSize[] ChildTracks =
        [TrackSize.Px(30f), TrackSize.Px(TrackRow.HeartCol), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(1f), TrackSize.Px(52f), TrackSize.Px(40f)];
    static readonly TrackSize[] ChildTracksNoArt =
        [TrackSize.Px(30f), TrackSize.Px(TrackRow.HeartCol), TrackSize.Star(1f), TrackSize.Px(52f), TrackSize.Px(40f)];

    Element ChildRowContent(string uri, RowFacts facts, int index,
                            PlaybackBridge? bridge, LibraryBridge? lib, ActionServices? acts, IOverlayService? overlay)
    {
        bool showArtwork = !AppearancePrefs.TrackArtworkHidden(_svc?.Settings);
        return BindTrackRow(ResolveTrack(uri, facts), index,
            showArtwork ? ChildCols : ChildColsNoArt,
            showArtwork ? ChildTracks : ChildTracksNoArt,
            ChildRowHeight, showTrackArtist: true, bridge, lib, acts, overlay);
    }

    Element SavedArtwork(RecentsRow row, Element context)
    {
        var children = row.ChildUris;
        var layers = new List<Element>(3);
        if (children is not null)
        {
            int shown = 0;
            for (int i = 0; i < children.Count && shown < 2; i++)
            {
                string uri = children[i];
                if (uri.Length == 0) continue;
                var facts = FactsFor(uri);
                float x = shown == 0 ? Spacing.M : Spacing.L;
                layers.Add(new BoxEl
                {
                    Width = WaveeSize.Thumb40, Height = WaveeSize.Thumb40,
                    Transform = Affine2D.Translation(x, 0f),
                    Children = [Surfaces.Artwork(facts.Cover, uri.GetHashCode() & 0x7fffffff,
                        WaveeSize.Thumb40, WaveeSize.Thumb40, Radii.Control)],
                });
                shown++;
            }
        }
        layers.Add(new BoxEl
        {
            Width = WaveeSize.Thumb48, Height = WaveeSize.Thumb48,
            Transform = Affine2D.Translation(0f, Spacing.S),
            Children = [context],
        });
        return new BoxEl
        {
            Width = WaveeSize.Thumb48 + Spacing.M, Height = WaveeSize.Thumb48 + Spacing.S,
            Shrink = 0f, ZStack = true, Children = layers.ToArray(),
        };
    }

    Element TrackRowContent(RecentsRow row, RowFacts facts, int displayIndex, string uri,
                            PlaybackBridge? bridge, LibraryBridge? lib, ActionServices? acts, IOverlayService? overlay)
    {
        _ = row;    // the single arm has no group facts to state — its identity is entirely the track's
        bool showArtwork = !AppearancePrefs.TrackArtworkHidden(_svc?.Settings);
        return BindTrackRow(ResolveTrack(uri, facts), displayIndex,
            showArtwork ? SingleRowCols : SingleRowColsNoArt,
            showArtwork ? SingleRowTracks : SingleRowTracksNoArt, RowHeight,
            showTrackArtist: false, bridge, lib, acts, overlay);
    }

    /// <summary>The single arm's column widths: # · ♥ · thumb · title* · duration · "…". Static because the shape never
    /// varies — this surface has no width tiers.</summary>
    static readonly ColumnSet SingleRowCols = new(Album: false, By: false, Date: false, Video: false, Plays: false,
        Heart: true, Thumb: true, Actions: true);
    static readonly ColumnSet SingleRowColsNoArt = SingleRowCols with { Thumb = false };
    static readonly TrackSize[] SingleRowTracks =
        [TrackSize.Px(36f), TrackSize.Px(TrackRow.HeartCol), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(1f), TrackSize.Px(52f), TrackSize.Px(40f)];
    static readonly TrackSize[] SingleRowTracksNoArt =
        [TrackSize.Px(36f), TrackSize.Px(TrackRow.HeartCol), TrackSize.Star(1f), TrackSize.Px(52f), TrackSize.Px(40f)];

    Track ResolveTrack(string uri, RowFacts facts)
    {
        if (_store?.GetTrack(uri) is { } track) return track;
        var episode = _store?.GetEpisode(uri);
        return new Track(HomeCardNav.Id(uri), uri, facts.Title ?? episode?.Title ?? "", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", facts.Subtitle ?? episode?.ShowName ?? ""),
            episode?.DurationMs ?? 0L, false, facts.Cover ?? episode?.Image);
    }

    BoxEl BindTrackRow(Track track, int displayIndex, ColumnSet cols, TrackSize[] sizes, float rowH, bool showTrackArtist,
                         PlaybackBridge? bridge, LibraryBridge? lib, ActionServices? acts, IOverlayService? overlay)
    {
        var st = TrackRow.StateOf(bridge, lib, track);
        Element title = WaveeType.TrackTitle(track.Title) with
        {
            Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element grid = TrackRow.Grid(track, displayIndex, st, cols, sizes, rowH, title, showTrackArtist, _go,
            onPlay: () => TrackRow.Invoke(bridge, track, () => Play(track.Uri)),
            onLike: track.Uri.Length > 0 ? () => lib?.ToggleSaved(track.Uri, track.Title) : null,
            actionsCell: TrackRow.MoreButton(true));
        BoxEl row = new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnClick = () => TrackRow.Invoke(bridge, track, () => Play(track.Uri)),
            HoverFill = Tok.FillSubtleSecondary,
            Corners = Radii.ControlAll,
            Draggable = track.Uri.Length > 0
                ? Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForTrack(track))
                : null,
            Children = [grid],
        };
        return acts is { } a && overlay is { } ov
            ? row.WithContextMenu(ov, () => TrackContextMenu.BuildSingle(a, track))
            : row;
    }

    static MenuAttach? CardMenu(string uri, string name, Image? image, string? subtitle, bool circular,
                                ActionServices? acts, IOverlayService? overlay)
        => acts is null || overlay is null || uri.Length == 0
            ? null
            : Menus.CardAttach(acts, overlay, uri, name, image, subtitle, circular);

    static DragSource? CardDrag(RecentsEntityKind kind, string uri, string name, Image? cover, ActionServices? acts)
    {
        if (uri.Length == 0) return null;
        WaveeResourceKind resource = kind switch
        {
            RecentsEntityKind.Album => WaveeResourceKind.Album,
            RecentsEntityKind.Artist => WaveeResourceKind.Artist,
            RecentsEntityKind.Show => WaveeResourceKind.Show,
            RecentsEntityKind.Episode => WaveeResourceKind.Episode,
            RecentsEntityKind.Track => WaveeResourceKind.Track,
            RecentsEntityKind.Collection or RecentsEntityKind.Playlist => WaveeResourceKind.Playlist,
            _ => WaveeResourceKind.Route,
        };
        if (resource == WaveeResourceKind.Route) return null;
        return Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(resource, uri, name, cover, acts));
    }

    bool Morphable(int rowIndex)
    {
        var flags = _shape.Morphable;
        return (uint)rowIndex < (uint)flags.Length && flags[rowIndex];
    }

    static DetailKind DetailKindOf(RecentsEntityKind kind) => kind switch
    {
        RecentsEntityKind.Album => DetailKind.Album,
        _ => DetailKind.Playlist,
    };

    /// <summary>The trailing capsule names what the row IS — a recents list mixes every entity kind, and without it a
    /// playlist and an album read as the same card. Existing keys only; this page adds none of its own.</summary>
    static string? KindLabel(RecentsEntityKind kind) => kind switch
    {
        RecentsEntityKind.Album => Loc.Get(Strings.Home.Album),
        RecentsEntityKind.Artist => Loc.Get(Strings.Home.Artist),
        RecentsEntityKind.Show => Loc.Get(Strings.Podcast.Show),
        RecentsEntityKind.Episode => Loc.Get(Strings.Podcast.Episodes),
        RecentsEntityKind.Track => Loc.Get(Strings.Detail.Column.Song),
        RecentsEntityKind.Playlist => Loc.Get(Strings.Nav.Playlist),
        _ => null,   // Collection's title already says "Liked Songs"; Unknown names nothing it can vouch for
    };

    static string? ArtistNames(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return null;
        if (artists.Count == 1) return NullIfEmpty(artists[0].Name);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count; i++)
        {
            if (artists[i].Name.Length == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(artists[i].Name);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ── navigation ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Opening a row goes to its context. Routed through the SHARED card dispatcher, not a local switch — the
    /// two surfaces that already own one drifted apart over exactly this (the Liked branch).</summary>
    void Open(RecentsRow row)
    {
        if (RecentsView.HydrationUri(row) is not { Length: > 0 } uri) return;
        var facts = FactsFor(row);
        var card = new HomeCard(uri, facts.Title ?? "", facts.Subtitle, facts.Cover,
            CardKindOf(RecentsList.EntityKindOf(uri)),
            // OwnerName, not Subtitle: PlaylistSummary's third slot IS the owner, and a playlist's store row already
            // resolves LIST_METADATA_V2's `source` into OwnerName for exactly that role.
            Meta: facts.Subtitle is { Length: > 0 } owner ? new HomeCardMeta(OwnerName: owner) : null);
        HomeCardNav.Open(card, _preview, _go, playTrack: null);
    }

    void Play(string uri)
    {
        if (_svc is not { } svc) return;
        // A track/episode plays itself; everything else is a CONTEXT the player starts from the top of.
        if (RecentsList.EntityKindOf(uri) is RecentsEntityKind.Track or RecentsEntityKind.Episode)
            _ = svc.Player.PlayTrackAsync(uri);
        else _ = svc.Player.PlayAsync(uri, 0);
    }

    static HomeCardKind CardKindOf(RecentsEntityKind kind) => kind switch
    {
        RecentsEntityKind.Track => HomeCardKind.Track,
        RecentsEntityKind.Album => HomeCardKind.Album,
        RecentsEntityKind.Artist => HomeCardKind.Artist,
        RecentsEntityKind.Show => HomeCardKind.Podcast,
        RecentsEntityKind.Episode => HomeCardKind.Episode,
        RecentsEntityKind.Collection => HomeCardKind.Liked,
        _ => HomeCardKind.Playlist,
    };

    // ── snapshot lifecycle ────────────────────────────────────────────────────────────────────────────────────────────
    async Task<RecentsSnapshot> FetchResourceAsync(IRecentsSource source, CancellationToken ct)
    {
        if (!_hasSnapshot)
        {
            var initial = await source.FetchAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _post(() => Adopt(initial));
            return initial;
        }
        byte[]? revision = null;
        if (_revision is { Length: > 0 } hex)
        {
            try { revision = Convert.FromHexString(hex); } catch (FormatException) { revision = null; }
        }
        var rows = _shape.Rows;
        var fresh = await source.FetchDiffAsync(revision, rows, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (fresh is null) return new RecentsSnapshot(_revision, rows);
        _post(() => Adopt(fresh));
        return fresh;
    }

    /// <summary>Install a snapshot. Hydration SURVIVES for free: the display facts live in the store, keyed by entity
    /// uri, so a diff that reorders or extends the list re-renders instantly against what is already resident and asks
    /// the network only for the genuinely new pointers.</summary>
    void Adopt(RecentsSnapshot snapshot)
    {
        var rows = CopyRows(snapshot.Rows);
        var morphable = RecentsView.FirstOccurrence(rows);
        _revision = snapshot.Revision;
        _hasSnapshot = true;

        // W2.3: the pivot set is now FIXED (All/Music/Podcasts/Artists), so unlike the old wire-derived chip bar there
        // is no "this token no longer exists" case to guard — every selectable token is always one of the four, and
        // PivotTabs itself disables whichever ones this snapshot has zero rows for.
        _inflight.Clear();
        BuildShape(rows, morphable, _chip.Peek());
        _epoch.Value++;
    }

    /// <summary>Re-cut the display array for a chip token. The row array is untouched — filtering is a VIEW, so a chip
    /// switch (or a DST-driven full recut off <see cref="RolloverMidnight"/>) can never lose hydration or reach the
    /// network.</summary>
    void Recut(string? token)
    {
        var s = _shape;
        BuildShape(s.Rows, s.Morphable, token);
    }

    /// <summary>W1.1's single build-and-publish chokepoint — <see cref="Adopt"/> (rows changed) and <see cref="Recut"/>
    /// (rows unchanged, only the cut/grouping changed) both fund through here so there is exactly ONE place that
    /// constructs a <see cref="Shape"/>. Ordering matters and is deliberate: build the map/display/sections/calendar →
    /// reuse-or-build the layout → PRIME its extent table → construct the Shape → publish <see cref="_shape"/> →
    /// replace the virtual collection's snapshot → reset the per-shape interaction state → bump
    /// <see cref="_shapeEpoch"/> LAST, once every reader-visible piece of state already agrees.</summary>
    void BuildShape(RecentsRow[] rows, bool[] morphable, string? token)
    {
        var map = RecentsView.Filter(rows, token);
        RecentsRow[] display;
        if (token is null)
        {
            display = rows;   // the identity cut shares the array outright — no copy
        }
        else
        {
            display = new RecentsRow[map.Length];
            for (int i = 0; i < map.Length; i++) display[i] = rows[map[i]];
        }
        var sections = RecentsView.BuildSections(rows, map, _now, _culture);
        var calendar = RecentsView.DayDensity(rows, map, _now, _culture);

        // W1.2b: reuse the previous Shape's Layout instance — and therefore its measured extent table — only when this
        // is the SAME row snapshot and the entire flat projection is unchanged. Count+header positions are not enough:
        // a fresh snapshot can place different row identities at those indices, which would transfer drawer/row
        // measurements to unrelated content.
        // KNOWN COHERENCE SEAM (documented, not fixed here): when this gate misses (hydration Adopt, pivot re-cut),
        // the new layout starts as a pure-estimate table while the restored ABSOLUTE offset was measured against the
        // old, corrected one — every measured correction is lost and the offset briefly means a different place in
        // content until the realized window re-corrects. Carrying measured extents across snapshots (re-keying by
        // row identity) is its own task; the exact per-kind estimates (row 64 == real 64, header 48, spacer 88) keep
        // the practical drift small for Recents.
        // _shape always holds a real instance by this point (the constructor seeds it before anything can call
        // BuildShape), so the reuse check compares against a real previous generation, never a null placeholder.
        var previous = _shape;
        // The outgoing projection may hold a drawer height at an unrealized index. Normalize it before deciding whether
        // the table is safe to reuse; the disclosure itself is reset after the new shape is atomically published.
        ResetExpandedExtent(previous, _expandedOriginalRow);
        _expandedOriginalRow = -1;
        GroupedListVirtualLayout layout =
            ReferenceEquals(rows, previous.Rows)
            && sections.Items.AsSpan().SequenceEqual(previous.Sections.Items)
                ? previous.Layout
                : new GroupedListVirtualLayout(sections.HeaderIndices, DateHeaderHeight, RowHeight);
        layout.ContentExtent(sections.Items.Length, 0f);   // prime (no-op when the table already matches this count)

        // PUBLISH ORDER (verified, and the reason the untracked `_shape` field is safe to read beside the tracked `_vc`
        // snapshot): `_shape` lands FIRST and `_shapeEpoch` LAST, on the UI thread, with nothing between them that can
        // yield. So any reader woken by `_vc.Version` or `_shapeEpoch` already sees the matching Shape, and a reader
        // that samples `_shape` alone can at worst be one generation AHEAD — never behind, and never half-updated
        // (a single reference swap; see the Shape doc comment).
        _shape = new Shape(rows, display, morphable, sections, calendar, layout, _now.Offset);
        _vc.ReplaceSnapshot(sections.Items);
        _expandedRow.Value = "";
        // Re-resolve the sticky overlay from the live offset — never leave it at -1. ItemClipTopInset clips the
        // in-list first header, so a blank overlay is a missing "Today" until the user scrolls. A pivot Recut
        // mid-list keeps the day under the current offset, not a hardcoded Today.
        float offset = _scrollController.Offset.Peek();
        float viewport = _scrollController.ViewportLength.Peek();
        UpdateSticky(new ScrollGeometry(0f, offset, viewport, viewport, 0f, 0f, 0f, 0f, 0));
        _calendarDay.Value = DateOnly.FromDateTime(_now.LocalDateTime);
        _shapeEpoch.Value++;
    }

    /// <summary>W1.6 midnight rollover, armed by <see cref="Render"/> via <c>UseTimeout</c> keyed on the local day
    /// number. A day-word bucket ("Today"/"Yesterday"/a weekday name) is a pure function of TODAY, so every one of
    /// them goes stale the instant local midnight passes even though the underlying grouping — which rows belong to
    /// which calendar day — never moved. The common case therefore only needs to relabel in place (an <see cref="_epoch"/>
    /// bump, not <see cref="_shapeEpoch"/> — nothing keyed on the shape needs to remount). A full <see cref="Recut"/>
    /// is reserved for the one case relabeling cannot fix: the local UTC offset itself moved (a DST edge, a timezone
    /// change) since the shape was built, which can shift which calendar day a timestamp near midnight belongs to.</summary>
    void RolloverMidnight()
    {
        var now = DateTimeOffset.Now;
        _now = now;
        var shape = _shape;
        if (shape.BuiltOffset != now.Offset)
        {
            Recut(_chip.Peek());
            return;
        }
        var relabeled = RecentsView.Relabel(shape.Sections, now, _culture);
        _shape = new Shape(shape.Rows, shape.Display, shape.Morphable, relabeled, shape.Calendar, shape.Layout,
            shape.BuiltOffset);
        _epoch.Value++;
    }

    // ── viewport hydration ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The realized window moved. Called from the reconciler's realize path, so it does the cheapest possible
    /// thing: record the range and arm ONE pump. The pump then reads the LATEST range — which is how work for a range
    /// that has already scrolled away is dropped before it is ever started.</summary>
    void OnVisibleRange(int first, int end)
    {
        _rangeFirst = first;
        _rangeEnd = end;
        if (_pumpArmed) return;
        _pumpArmed = true;
        _post(Pump);
    }

    void Pump()
    {
        _pumpArmed = false;
        if (_storeDirty) { _storeDirty = false; _epoch.Value++; }
        var s = _shape;
        // W2.8: owner resolution is a different LADDER from the entity hydration below (User vs Track/Album/…), and it
        // reads its own residency (IStore.GetOwner), so it runs on every pump regardless of whether there is anything
        // left for the entity batch.
        CollectUnresolvedOwners(s.Sections.FlatToRow, s.Rows, _rangeFirst, _rangeEnd);
        if (_hydrator is not { } hydrator || _cts is not { } cts) return;
        _batch.Clear();
        RecentsView.CollectRange(s.Rows, s.Sections.FlatToRow, _rangeFirst, _rangeEnd, Pending, _batch);
        int hi = Math.Min(_rangeEnd, s.Sections.FlatToRow.Length);
        for (int i = Math.Max(0, _rangeFirst); i < hi && _batch.Count < RecentsView.BatchCap; i++)
        {
            int rowIndex = s.Sections.FlatToRow[i];
            if ((uint)rowIndex >= (uint)s.Rows.Length || s.Rows[rowIndex].Reason != RecentsReason.Saved) continue;
            RecentsView.CollectChildUris(s.Rows[rowIndex], Pending, _batch,
                Math.Min(2, RecentsView.BatchCap - _batch.Count));
        }
        if (_batch.Count == 0) return;
        var uris = _batch.ToArray();
        for (int i = 0; i < uris.Length; i++) _inflight.Add(uris[i]);
        _ = HydrateAsync(hydrator, uris, cts.Token);
    }

    /// <summary>W2.8: the realized window's playlist rows whose owner has no resident <c>Owner</c> row yet, asked
    /// through the SAME façade every other hydration goes through (the User ladder, background mode — a byline never
    /// blocks a pump). Mirrors <see cref="StoreLibrarySource.OverlayOwner"/>'s raw-id derivation (<c>Owner.Id</c>
    /// first, the store's own <c>OwnerName</c> as the fallback) and canonicalizes it, so the uri asked for is the exact
    /// one <see cref="OwnerSubtitleFor"/> will later look up with. Reuses <see cref="_ownerBatch"/> across pumps —
    /// bounded to the realized range, never the whole snapshot.</summary>
    void CollectUnresolvedOwners(int[] flatToRow, RecentsRow[] rows, int first, int end)
    {
        if (_store is not { } store || _hydrator is not { } hydrator || _cts is not { } cts) return;
        _ownerBatch.Clear();
        int hi = Math.Min(end, flatToRow.Length);
        for (int i = Math.Max(0, first); i < hi; i++)
        {
            int rowIndex = flatToRow[i];
            if ((uint)rowIndex >= (uint)rows.Length) continue;
            if (RecentsView.HydrationUri(rows[rowIndex]) is not { Length: > 0 } uri
                || RecentsList.EntityKindOf(uri) != RecentsEntityKind.Playlist) continue;
            if (store.GetPlaylist(uri) is not { } p) continue;
            string? raw = p.Owner?.Id is { Length: > 0 } id ? id : NullIfEmpty(p.OwnerName);
            if (raw is null || UserProfileIds.Normalize(raw) is not { } canonical) continue;
            if (store.GetOwner(canonical) is not null) continue;
            _ownerBatch.Add(canonical);
        }
        if (_ownerBatch.Count > 0)
            _ = hydrator.EnsureManyAsync(_ownerBatch.ToArray(), HydrationLevel.Identity,
                new HydrationOptions(HydrationMode.Background, Surface: TraitSurface.UserProfiles), cts.Token);
    }

    void HydrateChildren(RecentsRow row, int cap)
    {
        if (_hydrator is not { } hydrator || _cts is not { } cts || cap <= 0) return;
        var pending = new List<string>(Math.Min(cap, RecentsView.BatchCap));
        RecentsView.CollectChildUris(row, Pending, pending, Math.Min(cap, RecentsView.BatchCap));
        if (pending.Count == 0) return;
        var uris = pending.ToArray();
        for (int i = 0; i < uris.Length; i++) _inflight.Add(uris[i]);
        _ = HydrateAsync(hydrator, uris, cts.Token);
    }

    /// <summary>Which URIs this window still owes the chokepoint. Freshness/dedup/skip belong to the hydration ledger —
    /// this only avoids handing the same uri to two overlapping facade calls, and skips the kinds that resolve
    /// LOCALLY: Liked Songs ships with the app, and an uri whose kind the catalogue cannot address would be dropped by
    /// KindFor anyway.</summary>
    bool Pending(string uri)
    {
        if (_inflight.Contains(uri)) return false;
        return RecentsList.EntityKindOf(uri) is RecentsEntityKind.Track or RecentsEntityKind.Album
            or RecentsEntityKind.Artist or RecentsEntityKind.Show or RecentsEntityKind.Episode
            or RecentsEntityKind.Playlist;
    }

    async Task HydrateAsync(IEntityHydrator hydrator, string[] uris, CancellationToken ct)
    {
        try
        {
            // TWO asks, concurrently, because they are two different things (design §1.5):
            //   • IDENTITY for the entity pointers themselves — a recents window is pointers, not a tracklist, so this
            //     is the catalogue rung and nothing more (no ref-closure, no second transport).
            //   • the Recents TRAIT surface — 178/220 for wire fidelity plus 179, the tint that lets a card paint in
            //     its own colour before an image byte arrives. TraitSurfaces.ClientFeatureId maps this surface (and
            //     only this surface) to `mdata_esperanto`, which is the attribution the census tied that bundle to.
            await Task.WhenAll(
                hydrator.EnsureManyAsync(uris, HydrationLevel.Identity, new HydrationOptions(Surface: TraitSurface.Recents), ct),
                hydrator.EnsureTraitsAsync(uris, TraitSurface.Recents, ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort: rows keep their skeleton */ }
        if (ct.IsCancellationRequested) return;
        _post(() =>
        {
            for (int i = 0; i < uris.Length; i++) _inflight.Remove(uris[i]);
            // The store's own change signal usually beats us here; the bump is what guarantees the realized window
            // re-reads even when the projection wrote nothing new.
            _epoch.Value++;
        });
    }

    /// <summary>A store write landed. Coalesced onto the existing pump so a burst (a bulk projection, a playback
    /// heartbeat) costs ONE epoch bump and therefore one re-render of the realized window.</summary>
    void MarkStoreDirty()
    {
        _post(() =>
        {
            if (_shape.Rows.Length == 0) return;   // nothing realized to re-skin — a parked/empty page ignores the churn
            _storeDirty = true;
            if (_pumpArmed) return;
            _pumpArmed = true;
            _post(Pump);
        });
    }

    /// <summary>W2.9 fallback accent: identical to what every consumer painted before this page had a dynamic one
    /// (<see cref="WaveeAccent.Decor"/> ⇒ <c>Tok.AccentTextPrimary</c> for Ink, <c>Tok.AccentDefault</c> for Fill), so
    /// a page with no graded artwork yet — or with washes disabled — never regresses.</summary>
    static PageAccent FallbackAccent() => new(Tok.AccentTextPrimary, Tok.AccentDefault, "");

    /// <summary>W2.9: the wash's source card — now the SAME row selector the dynamic accent grades from
    /// (<see cref="RecentsView.AccentSourceRow"/>), so the Mica wash and the accent always agree on which row they
    /// are painting and shift together on a section crossing. Falls back to the original "first resolved cover in
    /// the top of the list" scan when no sticky header has resolved yet (e.g. before the first scroll) or that row's
    /// cover has not hydrated — a wash invented before any artwork landed would be a colour the page does not own.</summary>
    HomeCard? WashCard()
    {
        var s = _shape;
        int sourceRow = RecentsView.AccentSourceRow(s.Sections, s.Rows, _stickyHeader.Peek());
        if ((uint)sourceRow < (uint)s.Rows.Length)
        {
            var sourceFacts = FactsFor(s.Rows[sourceRow]);
            if (sourceFacts.Cover?.Url is { Length: > 0 })
            {
                string sourceUri = RecentsView.HydrationUri(s.Rows[sourceRow]) ?? s.Rows[sourceRow].Uri;
                return new HomeCard(sourceUri, sourceFacts.Title ?? "", sourceFacts.Subtitle, sourceFacts.Cover,
                    CardKindOf(RecentsList.EntityKindOf(sourceUri)));
            }
        }
        var rows = s.Display;
        int scan = Math.Min(rows.Length, 32);   // the wash is the TOP of the list, not a full-array search per render
        for (int i = 0; i < scan; i++)
        {
            var facts = FactsFor(rows[i]);
            if (facts.Cover?.Url is not { Length: > 0 }) continue;
            string uri = RecentsView.HydrationUri(rows[i]) ?? rows[i].Uri;
            return new HomeCard(uri, facts.Title ?? "", facts.Subtitle, facts.Cover,
                CardKindOf(RecentsList.EntityKindOf(uri)));
        }
        return null;
    }

    /// <summary>W2.9 resolution leaf, modeled on <see cref="CoverPaletteLeaves"/>'s <c>CoverPageTonePlane</c>/
    /// <c>CoverShellTintBinder</c>: the ONE node that owns the cover-grading <c>Watch</c> subscription, so a grading
    /// landing mid-scroll re-renders THIS zero-size node and nothing else — never the page, never a row. Reads the
    /// day-quantized <see cref="_accentDay"/> (the coalesced trigger <see cref="UpdateSticky"/>/<see cref="ResolveAccentDay"/>
    /// maintain) to know WHEN to re-derive; re-derives the source row itself via the SAME
    /// <see cref="RecentsView.AccentSourceRow"/> selector <see cref="WashCard"/> uses (keyed off the live
    /// <see cref="_stickyHeader"/>, not a value captured at the day change), so the two can never disagree about
    /// which row they are grading from. Publishes into the page-owned <see cref="_accent"/> signal; every consumer
    /// reads THAT as a Prop, never this leaf's own Watch.</summary>
    sealed class RecentsAccentBinder : Component
    {
        internal sealed record Props(RecentsPage Page);

        public override Element Render()
        {
            var page = UseProps<Props>().Page;
            _ = AppearancePrefs.Epoch.Value;   // the Settings toggle applies LIVE — same gate as the wash (Render, ~:266)
            bool disabled = page._svc?.Settings.Get(WaveeSettings.DisableColorWashes) ?? false;
            _ = page._accentDay.Value;         // subscribe: re-derive once per section crossing, never per row
            _ = page._epoch.Value;             // a hydration landing may be what makes FactsFor resolve a cover at all

            PageAccent resolved = FallbackAccent();
            if (!disabled)
            {
                var s = page._shape;
                int row = RecentsView.AccentSourceRow(s.Sections, s.Rows, page._stickyHeader.Peek());
                if ((uint)row < (uint)s.Rows.Length && page.FactsFor(s.Rows[row]).Cover?.Url is { Length: > 0 } url)
                {
                    _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;   // the leaf owns THIS subscription
                    if (Surfaces.ChromeSchemeFor(url) is { } scheme)
                        resolved = new PageAccent(WaveePalette.ChromeAccent(scheme), WaveePalette.Accent(scheme), url);
                }
            }
            page._accent.SetIfChanged(resolved);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }
}
