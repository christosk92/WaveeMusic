using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The right area: a FIXED chrome bar (toolbar + column header) above the track table. WIDTH-ADAPTIVE — as the area
// narrows it DROPS columns by breakpoint (Album → ♥ → art-thumb; #, Title and Duration always stay) so the title
// stays readable and the grid never overflows/overlaps. (The engine also shrink-to-fits an over-wide fixed grid —
// FlexLayout.ResolveColumns — as a backstop.) The active "tier" is derived from the measured width; a breakpoint cross
// REMOUNTS the list (keyed by tier) so every tier mounts one clean, consistent column set: within a tier the row arity
// is stable (recycle-safe), and across a tier the fresh mount sidesteps any recycle-shape churn. Selection survives the
// remount (external SelectionModel); the scroll offset resets on the (rare) breakpoint cross. The now-playing recolour
// re-skins the realized window IN PLACE via the epoch (displacementVersion) so a track change keeps the scroll offset.
// Rows are PLAIN/recyclable (no binds, no Components) → a steady scroll is zero-alloc.
sealed class TrackList : Component
{
    // Row geometry + the cell builders themselves now live in the shared TrackRow (Components/TrackRow.cs) so the detail
    // list, the library pane, artist "Popular" and search all render ONE identical cell. These re-export the shared
    // constants by name so the chrome/header code below is unchanged (the header must stay aligned to the row columns).
    const float RowHeight = TrackRow.RowHeight;     // density M
    const float HeaderHeight = TrackRow.HeaderHeight;
    const float ColGap = TrackRow.ColGap;           // shared by header + rows (alignment invariant)
    const float PadX = TrackRow.PadX;               // shared horizontal inset (header chrome padding == row grid padding)
    const float RowInset = TrackRow.RowInset;       // rounded row-highlight inset (rows pad PadX−RowInset so columns stay header-aligned)
    const float ActionsColWidth = TrackLane.Actions;   // trailing "..." overflow column (28px button + breathing room)
    // The FLEXIBLE lanes, as star weights: Title : Artist : Album = 1 : 0.75 : 0.75. Artist and Album are the weaker
    // facts, so neither ever gets more width than the song title, and the group splits the space left by the fixed
    // columns rather than one of them absorbing every squeeze. (Album is playlist/Liked only — album pages have no
    // Album column; Artist is Classic's dedicated lane.) Their readable FLOORS live beside the weights in TrackLane and
    // are what the relief ladder defends — see DetailTrackTableRules.ReliefFor.
    const float TitleStar = TrackLane.TitleStar;
    const float ArtistStar = TrackLane.ArtistStar;
    const float AlbumStar = TrackLane.AlbumStar;
    const int VerticalHeroIndex = 0;
    const int VerticalChromeIndex = 1;
    const int VerticalTrackStart = DetailVerticalLayout.PrefixCount;   // the slot map's owner is DetailVerticalLayout
    const int TrackOverscanItems = 8;

    // The detail model is a Loadable: the HEADER bits (HasVideo, columns) read reactively from its current value
    // (preview → full), and the TRACK ROWS stream in via Skel.Region — which derives the shimmer from the REAL Row
    // template (ONE source, no hand-written skeleton). _model / _tracks are refreshed at the top of Render.
    readonly Loadable<DetailModel> _full;
    DetailModel _model = DetailModel.Empty;
    IReadOnlyList<Track> _tracks = Array.Empty<Track>();
    readonly Signal<Route> _route;                             // read reactively → cfg re-derived so ONE list serves successive detail routes
    DetailConfig _cfg = DetailConfig.Album;                     // derived from route kind + loaded ReleaseKind at the top of Render
    DetailKind _kind = DetailKind.Album;                        // the ROUTE's kind, kept beside _cfg (which erases it)
    PlaylistRowsState? _lastListState;                          // the last `playlist.open.state` logged for this context (null = none yet)
    readonly PlaybackBridge? _bridge;
    readonly DetailHandlers _initialH;                         // first-frame fallback before DetailShell publishes live handlers
    DetailHandlers _h;                                         // refreshed from _liveHandlers at the top of every render
    readonly IReadSignal<DetailHandlers?>? _liveHandlers;       // signal parent→child path: accent/actions must not freeze
    readonly bool _showToolbar;                                // false in the vertical layout (the header owns the toolbar)
    // Chip derivation walks every track, so it is memoized on the track-list INSTANCE: the list is rebuilt (new
    // reference) whenever enrichment lands, and re-derives then and only then. Never per render, never per frame.
    readonly ChipCache _chipCache = new();
    // The server's curated chip set, fetched once per Liked mount. Empty until it lands (or forever, offline), which
    // is exactly when the derived fallback applies.
    readonly Signal<IReadOnlyList<ContentFilterChip>> _serverChips = new(Array.Empty<ContentFilterChip>());
    readonly bool _embedded;                                   // true when hosted inside a compact pane (Library master-detail): the
                                                               // SAME virtualized list + cell, but no album trailing (About/Fans/More-by)
                                                               // so the rows ARE the scroller — the tier system still drops columns to fit.
    readonly bool _verticalHeader;                              // narrow detail mode: hero + chrome are measured rows in this list's scroller
    readonly Signal<float>? _verticalHeroHeightOut;             // published UP to DetailShell: the tone plane's backdrop band
    readonly Signal<bool> _verticalCompactInteractive = new(false); // pin-edge only: enable compact Play hit target
    readonly Signal<bool> _verticalBodyClipEngaged = new(false);     // trailing page only: fade exactly while the sticky cut is active
    readonly Signal<float> _verticalHeaderHeight = new(0f);
    // Measured page width (vertical mode) → the hero's art size / flow. Seeded (ctor) from DetailShell's page-width
    // ESTIMATE (see DetailLayoutBreakpoints.EstimatePageWidthFromViewport) rather than 0, so the hero's own pre-measure
    // geometry derives from that estimate instead of DetailVerticalLayout.FallbackW (580, a constant unrelated to this
    // window). OnBoundsChanged below overwrites it with the real width the moment layout measures it either way.
    readonly Signal<float> _verticalHeroW;
    bool _verticalHeroRowFlow;                                  // artwork BESIDE the identity column (see DetailVerticalLayout.RowFlow)
    bool _verticalHeroFlowInitialized;
    bool _hasDate;                                             // any track carries an AddedAt → the Date-added column exists
    bool _hasBy;                                               // collaborative (≥2 contributors) → the Added-by column exists
    readonly Signal<int> _tier = new(0);                       // width tier (0 = widest/full), written by OnBoundsChanged
    int _initialTierSeed;                                      // viewport-derived pre-measure tier (first-frame safety)
    readonly Signal<bool> _tierMeasured = new(false);          // false until the FIRST real (>0) width measure: while false the
                                                               // seed governs, and the flip is what invalidates a seeded render
                                                               // even when the measured tier equals _tier (see ClampTier)
    // Identity-first width relief: how many trailing lanes the CURRENT width forces the table to give up before Title
    // (and Classic's Artist) fall below their readable floor. A second, FINER ladder under the tier — the tier decides
    // which lanes a width may admit at all, relief decides which of those the star tracks can actually afford. Written
    // by OnBoundsChanged exactly like _tier, and value-gated the same way, so a drag only re-renders on the frames
    // where a lane genuinely changes hands.
    readonly Signal<int> _relief = new(0);
    // The lane geometry the shape memo last measured, so the bounds callback can re-run the SAME ladder without
    // rebuilding a ColumnSet out of this component's mutable fields (the snapshot contract on SetFor).
    DetailTrackTableRules.TrackTableLanes _reliefLanes;
    float _reliefGap = TrackRow.ColGap, _reliefPad = TrackRow.PadX;
    readonly Signal<int> _visibleCount = new(0);
    readonly Signal<int> _verticalItemCount = new(DetailVerticalLayout.ItemCount(0, hasFacts: false));
    // Does the HERO system's page footer (the metadata-facts bento — this week's adds, the tempo curve, the top
    // artists) exist for this page? A SIGNAL, not a plain field like _recsLive, because the answer flips when
    // hydration lands — and the slot that renders the footer is a recycled one whose index does not necessarily
    // change on that flip. Reading it inside the slot is the subscription that repaints it. Written from the same
    // layout effect as the item count, never from Render.
    readonly Signal<bool> _verticalFacts = new(false);

    // ── progressive reveal (cold shimmer→content swap) ───────────────────────────────────────────────────────────────
    // Measured: navigating to a detail/playlist page, the whole visible track band swapped shimmer→real in ONE ~80ms UI
    // frame (694 spans re-recorded, record=72.4ms, 138 component re-renders, a gen2 GC inside it) — freshly mounted rows
    // have no cached span to reuse and the engine's realize budget exempts the visible band by design. Instead of that
    // one-shot swap, the cold reveal ramps the count of REAL rows up DetailRevealRamp.Chunk-at-a-time over a few frames;
    // rows past the ramp render a cheap ShimmerRow, so no single frame records the whole band. Each row's own crossing
    // CROSS-FADES (BoundRowContent.RampReveal): the placeholder and the real row are two distinct element types in one
    // slot, so the swap is a remount and the real row eases in — without that, 12 rows a frame hard-popped from grey bars
    // into text while the page itself was still fading in. Only rows that actually shimmered fade; see the latch there.
    // Steady state = _reveal at
    // DetailRevealRamp.Done (every row real, no per-row cost); an instant/cached load only skips the ramp on pages whose
    // list owns a real viewport — a HasTrailing page (album/single) is one unwindowed mandatory band, so it ramps warm
    // opens too (see the arming edge in Render). The progression math is the pure, unit-tested DetailRevealRamp.
    readonly Signal<int> _reveal = new(DetailRevealRamp.Done);   // rows with displayIndex < _reveal are REAL; the rest render ShimmerRow. Done ⇒ all real
    readonly Signal<bool> _rampActive = new(false);     // gates the per-frame reveal clock — mounted only while ramping so the frame loop quiesces after
    bool _sawPending;                                    // this content actually showed shimmer (cold load) ⇒ the ready edge ramps; a warm load ramps only on a HasTrailing page
    float _lastRightW;                                         // last measured right-area width (0 until the first positive bounds)
    readonly SelectionModel _selection = new();                // external → survives a tier remount
    // Keyed by the COLUMN SET, not the tier: the set already folds in every input the track sizes depend on (tier +
    // which optional columns the model/config actually offer), so a cached entry can never go stale behind a snapshot
    // change. That matters because _rowShape is a lazily-evaluated memo — it can recompute before this component's
    // render body would have had a chance to invalidate a tier-keyed cache.
    // Keyed on (ColumnSet, Art): Art is density-derived and NOT part of ColumnSet, so a Cozy→Comfortable density
    // change (same lane set, same tier) must miss this cache too, or the Thumb column would keep the previous
    // density's width forever — TrackList itself never remounts on a density change (only its Key'd list children do).
    readonly Dictionary<(ColumnSet Set, float Art), TrackSize[]> _tracksBySet = new();
    (DetailTrackSort Sort, string Query, TrackFilterState Filters) _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterState.Default);   // invalid sentinel
    IReadOnlyList<Track>? _viewTrackSet;                       // source-list identity paired with the sort/filter cache key
    IReadOnlySet<string>? _viewSavedSet;                       // only populated by Liked-only; reference change invalidates the cached map
    int[] _view = Array.Empty<int>();                          // filtered + sorted → original track-index map (rows read via this)
    Memo<TrackRowsSnapshot>? _rowsSnapshot;                    // atomic model/config/handlers/sort/filter value for persistent rows
    Memo<RowShape>? _rowShape;                                 // (column set + tracks) for the ACTIVE tier — the one thing a breakpoint cross changes
    Memo<ColorF>? _rowAccent;                                  // equality-gated scalar: row pills never observe the full snapshot
    BoundItemsSource<Track>? _rowItems;                        // snapshot + recycled slot index resolve together
    AsyncCommandSet<string>? _play;                            // per-track-id play command in flight → the row's #-cell buffering spinner
    string? _lastCtxUri;                                       // last loaded context uri → detect a reused-slot album swap (invalidate view/columns/selection)
    IReadOnlyList<Track>? _lastTrackSet;                       // last model.Tracks INSTANCE → an in-place refresh (same ContextUri, new list) invalidates the view cache

    // ── §4.6 realtime membership choreography (live add/remove/move/reset narration) ──────────────────────────────────
    // Pure item transitions, seeded IN the render that commits the new order (same frame — a late seed reads as a
    // jump-then-snap-back flash): a removed row simply vanishes while the rows below FLIP-glide up to reclaim the space;
    // an added row's neighbors part (FLIP down) and the row itself fades in at its slot. No overlays, no remounts.
    readonly ItemsViewController _listCtl = new();             // scroll anchoring (ScrollOffset/ScrollBy) + insertion handoff
    InsertionOptions? _insertion;                              // the declarative drop destination (framework-owned geometry)
    readonly Signal<int> _dispVer = new(0);                    // bump → the ItemsView displacement seed re-runs (FLIP/fade)
    readonly Signal<int> _videoDropRow = new(-1);              // slot index a compatible .mp4 file drag is hovering (-1 = none)
    int _resetEpoch;                                          // render-local identity epoch: curated re-cut → keyed remount + fresh scroll state
    int _lastDealtTier = -1;                                   // the tier the realized rows were last narrated at (-1 = never dealt)
    long _lastDealtAtMs;                                       // stamp of the last breakpoint re-deal → rapid-reversal detection
    bool _dealtThisFrame;                                      // a membership choreography already owns this frame's seeds
    const int ReDealReversalMs = 200;                          // a cross this soon after the last one is a toggle storm, not a gesture
    const int ReDealRows = 24;                                 // seed a viewport's worth; ItemFadeFrom is queried per REALIZED row anyway
    readonly Dictionary<int, (float dx, float dy)> _flip = new();        // new display index → FLIP start residual (dy DIP)
    readonly Dictionary<int, (float from, float delayMs)> _fade = new(); // added display index → opacity ease-in + stagger
    Track[]? _lastDisplayed;                                   // displayed (view-ordered) snapshot — the keyed-diff baseline
    LibraryBridge? _lib;                                       // Mutations bridge → per-row heart saved-state + toggle (null when no Mutations source)
    ActionServices? _acts;                                     // the signals-first action system (row context menus + batch bar) — cached in Render like _lib
    IOverlayService? _menuOverlay;                             // the overlay service the rows' attached context menus open through (cached in Render)

    // ── "Recommended songs" (playlist extender) — appended into the SAME bound list, after the track rows ──────────────
    // Owned/collaborative playlists only (gated in Render). The list carries: track rows · one "Recommended" header row ·
    // N recommendation rows. The header's mount is the lazy first-fetch trigger; Refresh + Add re-fetch with the ACCUMULATED
    // skip set so every batch is fresh. State is signals-first so a batch landing / an optimistic add re-skins in place.
    const int RecBatch = 20;                                   // one extender page (matches the HAR capture)
    static readonly ColumnSet RecColumns = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: false, Thumb: false);
    readonly Signal<IReadOnlyList<Track>> _recs = new(Array.Empty<Track>());
    readonly Signal<int> _recState = new(0);                   // 0 idle · 1 loading · 2 loaded
    readonly HashSet<string> _recShown = new(StringComparer.Ordinal);   // every id ever shown → the accumulated skip set (non-repeating batches)
    readonly System.Threading.CancellationTokenSource _recCts = new();
    readonly Signal<int> _listCount = new(0);                  // ItemsView TOTAL (track rows + rec rows); _visibleCount stays the track count for §4.6
    // The expanded row, by MEMBERSHIP ROW identity (MembershipDiff.RowKey — the playlist4 per-row uid where the read
    // model has one, else uri#@displayIndex). NOT by track uri: a playlist may legitimately hold the same song twice,
    // and a uri-keyed expansion opened EVERY row carrying that uri at once (and minted duplicate element keys for their
    // drawers). NOT by display index either — that is the fallback and never the primary, because a sort or filter
    // reorders the list and a purely index-keyed expansion would open a different song than the one clicked.
    // "" = nothing expanded. ONE row at a time — several open drawers push the list around unpredictably while scrolling.
    readonly Signal<string> _expandedRow = new("");
    bool _recsLive;                                            // the DATA half of the recs gate (see Render): refreshed each render, read by
                                                               // RowOrRecContent so an appended index can never render a header/rec row while the
                                                               // gate is off — the count signal is the primary gate, this is the belt-and-braces one
    Services? _svc;                                            // cached in Render → the header/add handlers reach the extender + the post seam
    Action<Action>? _post;
    readonly HashSet<string> _recAdding = new(StringComparer.Ordinal);
    Memo<int>? _selectedCount;                                  // valid selected track rows (display-space selection)
    Memo<bool>? _checksVisible;                                 // equality-gated: checkbox lane visible (toggle OR selection)
    Memo<bool>? _selectionCommandsVisible;                      // contextual command-bar mode (never for a plain single click)
    Func<bool> _checksVisibleRead = static () => false;        // stable thunk for bound row lanes (repointed each render)

    // The shared detail-track command bar measures its own pane and promotes labeled commands only when they genuinely
    // fit. Search disclosure is shared with the compact sticky projection so both surfaces tell the same story.
    readonly Signal<bool> _searchExpanded = new(false);
    readonly Signal<bool> _searchFocused = new(false);
    readonly Signal<int> _toolbarMetricsEpoch = new(0);
    readonly float[] _toolbarWidths = [120f, 92f, 96f, 156f, 144f, 82f]; // conservative first-frame localized budgets
    DetailTrackCommandBarFit? _toolbarFit;
    // The fit LATCHED at the moment search opened, with the pane width it was resolved against. While search is open the
    // toolbar must not re-fit: promoting/evicting a command mid-flight changes the measured widths, which bumps
    // _toolbarMetricsEpoch, which re-resolves and hands the width tween a NEW target — the box visibly jumps. A real
    // pane resize (a different `available`) drops the latch and re-resolves once.
    (float Available, DetailTrackCommandBarFit Fit)? _searchOpenFit;
    NodeHandle _searchButtonNode;
    bool _restoreSearchFocus;

    /// <summary>The ONE search-disclosure duration. The width tween, the icon↔field cross-fade, the chrome brush
    /// cross-fade and the compact pill's exit all run on it, so the box expanding and its styling resolving read as a
    /// single motion instead of four overlapping ones.</summary>
    internal const float SearchExpandMs = 260f;
    internal const float SearchCollapseMs = 180f;

    static readonly LayoutTransition ToolbarCommandMotion = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 8f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

    // Reflow, never Reveal: the field must PUSH its neighbours through real layout. Reveal snaps the model bounds on
    // frame 1 (neighbours jump) and overpaints them on the way back in.
    static readonly LayoutTransition SearchDisclosureMotion = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The icon↔field swap inside the growing box: a pure cross-fade on the disclosure's own curve, so the
    /// content resolves WITH the width rather than popping in at either end.</summary>
    static readonly LayoutTransition SearchSwapMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(SearchCollapseMs, Easing.FluentAccelerate));

    /// <summary>The focus underline. Mounted only while focused, but it FADES rather than appearing — it is part of the
    /// same chrome resolve as the fill and the border.</summary>
    static readonly LayoutTransition SearchUnderlineMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(SearchCollapseMs, Easing.FluentAccelerate));

    static readonly LayoutTransition ToolbarModeMotion = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(210f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 10f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: -8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

    public TrackList(Signal<Route> route, Loadable<DetailModel> full, PlaybackBridge? bridge, DetailHandlers h,
                     bool showToolbar = true, bool embedded = false, bool verticalHeader = false,
                     Signal<float>? verticalHeroHeight = null, float verticalHeroWSeed = 0f,
                     IReadSignal<DetailHandlers?>? liveHandlers = null)
    {
        _route = route; _full = full; _bridge = bridge; _initialH = _h = h; _liveHandlers = liveHandlers;
        _showToolbar = showToolbar; _embedded = embedded;
        _verticalHeader = verticalHeader && !embedded;
        _verticalHeroHeightOut = verticalHeroHeight;
        // A one-shot mount seed, not a reactive prop (component props freeze at mount by design here) — OnBoundsChanged
        // is the only thing that ever writes this signal again once the real width lands.
        _verticalHeroW = new Signal<float>(MathF.Max(0f, verticalHeroWSeed));
    }

    // No scroll timeline is published here any more. It existed for exactly one consumer — the page-tone plane's
    // blurred artwork band, a ZStack sibling of this scroller that had to ride its offset — and that band is deleted
    // (see CoverPageTonePlane). The engine seam it used (ScrollEl/VirtualListEl.ScrollTimeline + ScrollBindDsl.Timeline)
    // is general and stays; nothing in Wavee needs it today.

    int TrackStart => _verticalHeader && !_cfg.HasTrailing ? VerticalTrackStart : 0;

    // The placeholder row the engine derives the shimmer from — the REAL Row(...) with an empty track, so the skeleton
    // rows always match the real rows (the single-source-of-truth the skeleton kit is built on).
    static readonly Track EmptyTrack = new("", "", "", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null);

    readonly record struct TrackRowsSnapshot(
        DetailModel Model, DetailConfig Config, DetailHandlers Handlers,
        DetailTrackSort Sort, string Query, TrackFilterState Filters, IReadOnlySet<string>? Saved,
        bool MarqueeDisabled, bool TrackArtworkHidden, bool Classic, string? TopTrackId,
        // The app-wide BPM·Key column opt-in. Carried in the SNAPSHOT (not read in SetFor) per the contract above:
        // an appearance flag read outside the snapshot would recompute, compare equal, and never reach the rows.
        bool TempoColumn = false,
        // …and the app-wide Plays column opt-in, on exactly the same contract. Only consulted where the surface offers
        // it (DetailConfig.PlaysColumnOptIn — playlist / Liked); album profiles carry the lane through ShowPlays.
        bool PlaysColumn = false);

    // The row/header column geometry for the active tier. Equality-gated: ColumnSet is a value record and the
    // TrackSize[] is the per-tier cached instance, so a re-render that does not cross a breakpoint compares equal and
    // costs nothing. Rows read this instead of taking the shape as a frozen constructor arg — that is what lets a
    // breakpoint cross patch the realized rows IN PLACE instead of remounting the whole virtualized list.
    // Art carries alongside Set/Tracks (rather than being re-derived downstream) because it is keyed on DENSITY, which
    // ColumnSet does not encode — every consumer of the shape (header/rows/shimmer/drawer indent) must read the SAME
    // number the width tracks were built from, or the Thumb column and the actual Surfaces.Artwork call disagree.
    readonly record struct RowShape(ColumnSet Set, TrackSize[] Tracks, float Art);

    readonly record struct RowPresentation(
        Track Track, int DisplayIndex, TrackRow.State State,
        bool MarqueeDisabled, bool ShowTrackArtist, bool ShowListMetadata,
        Action<string, string?> Go, Owner? AddedBy);

    // The visible order (cached, keyed by sort + filter): view[displayPos] = original track index, for the tracks that
    // pass the filter (search query + hide-explicit), in the current sort order. Read live by the frozen row template /
    // keyOf / invoke via .Peek(), so a SORT change re-skins the realized window in place; a FILTER change (which alters
    // the count) instead remounts the list via the keyed wrapper.
    int[] View(TrackRowsSnapshot snapshot)
    {
        var s = snapshot.Sort;
        string q = snapshot.Query;
        var filters = snapshot.Filters;
        var key = (s, q, filters);
        var tracks = snapshot.Model.Tracks;
        if (!ReferenceEquals(_viewTrackSet, tracks) || !ReferenceEquals(_viewSavedSet, snapshot.Saved) || !key.Equals(_viewKey))
        {
            var list = new List<int>(tracks.Count);
            var now = DateTimeOffset.Now;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (!TrackFilterModel.Matches(t, q, in filters,
                    hasVideo: VideoPresence.HasVideo(t),               // override-only videos pass the filter too
                    isSaved: snapshot.Saved?.Contains(t.Uri) ?? false,
                    now)) continue;
                list.Add(i);
            }
            Comparison<int> baseCmp = s.Column switch
            {
                SortColumn.Title => (a, b) => string.Compare(tracks[a].Title, tracks[b].Title, StringComparison.OrdinalIgnoreCase),
                SortColumn.Album => (a, b) => string.Compare(tracks[a].Album.Name, tracks[b].Album.Name, StringComparison.OrdinalIgnoreCase),
                SortColumn.Duration => (a, b) => tracks[a].DurationMs.CompareTo(tracks[b].DurationMs),
                SortColumn.Artist => (a, b) => string.Compare(DetailFormat.ArtistNames(tracks[a].Artists), DetailFormat.ArtistNames(tracks[b].Artists), StringComparison.OrdinalIgnoreCase),
                SortColumn.DateAdded => (a, b) => Nullable.Compare(tracks[a].AddedAt, tracks[b].AddedAt),
                SortColumn.Plays => (a, b) => tracks[a].PlayCount.CompareTo(tracks[b].PlayCount),
                _ => (a, b) => a.CompareTo(b),   // Index = original order
            };
            // Stable: ties break by original index (ascending), so descending only flips the primary key.
            list.Sort((a, b) => { int c = baseCmp(a, b); if (s.Descending) c = -c; return c != 0 ? c : a.CompareTo(b); });
            _view = list.ToArray(); _viewKey = key; _viewTrackSet = tracks; _viewSavedSet = snapshot.Saved;
        }
        return _view;
    }

    int[] View() => _rowsSnapshot is { } rows ? View(rows.Peek()) : _view;

    Track TrackAt(TrackRowsSnapshot snapshot, int displayPos)
    {
        var view = View(snapshot);
        if ((uint)displayPos >= (uint)view.Length) return EmptyTrack;
        int original = view[displayPos];
        var tracks = snapshot.Model.Tracks;
        return (uint)original < (uint)tracks.Count ? tracks[original] : EmptyTrack;
    }

    // The most-played track's id (gets the star), or null when there's no play data — so the star stays album-only.
    static string? TopTrack(IReadOnlyList<Track> tracks)
    {
        int best = -1;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].PlayCount > 0 && (best < 0 || tracks[i].PlayCount > tracks[best].PlayCount)) best = i;
        return best >= 0 ? tracks[best].Id : null;
    }

    // Shown in place of the list when there's nothing to show — an empty playlist, or a filter that matched nothing.
    static Element FilterEmpty(bool noTracks) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.L, Spacing.XXL, Spacing.L, Spacing.XXL),
        Children = [new TextEl(noTracks ? Loc.Get(Strings.Detail.Empty.NoTracks) : Loc.Get(Strings.Detail.Empty.NoMatch)) { Size = 14f, Color = Tok.TextTertiary }],
    };

    /// <summary>What stands in for the rows when there are none to show (<see cref="PlaylistListState"/>): shimmer rows
    /// while the membership is still unknown, else the empty / no-match message. The Loading arm is a skeleton boundary
    /// keyed on the MODEL's flag, not the page loadable (the header is Ready — only the rows are not), deriving the
    /// same <see cref="RowsShimmer"/> the cold page shows, so the two loading looks are one look.</summary>
    Element ListPlaceholder(PlaylistRowsState state, ColumnSet set, TrackSize[] tracks, float rowH, float art) => state switch
    {
        PlaylistRowsState.Loading => new SkelRegionEl(
            Pending: () => { var m = _full.Value.Value; return PlaylistListState.IsLoading(m.MembershipLoaded, m.Tracks.Count); },
            Failed: static () => false,
            // Ready with rows: this element is replaced by the real list in the same flush that flipped the flag; the
            // spacer is what stands there for that flush. Ready with none: the membership landed empty.
            Content: () => _full.Value.Value.Tracks.Count == 0 ? FilterEmpty(noTracks: true) : new BoxEl(),
            ShimmerSource: () => RowsShimmer(set, tracks, rowH, art),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false)
            with { Key = "rows:loading" },
        PlaylistRowsState.Empty => FilterEmpty(noTracks: true),
        _ => FilterEmpty(noTracks: false),
    };

    /// <summary>The list state THIS render shows, logged once per transition (<c>playlist.open.state</c>) so a page
    /// stuck on the loading skeleton — a membership that never lands — is visible in the log with its inputs.</summary>
    PlaylistRowsState ListStateFor(DetailModel model, int visible)
    {
        var state = PlaylistListState.For(model.MembershipLoaded, _tracks.Count, visible);
        if (state != _lastListState && _kind is DetailKind.Playlist or DetailKind.Liked)
        {
            _lastListState = state;
            WaveeLog.Instance.Event(WaveeLogLevel.Info, "playlist", "playlist.open.state", "detail list state",
                fields:
                [
                    WaveeLogField.Of("uri", model.ContextUri ?? ""),
                    WaveeLogField.Of("state", PlaylistListState.Name(state)),
                    WaveeLogField.Of("tracks", _tracks.Count),
                    WaveeLogField.Of("known", model.MembershipLoaded),
                ]);
        }
        return state;
    }

    // (ColumnSet — which optional columns are present at a tier — is the shared TrackRow.ColumnSet, so the header here and
    // every shared cell agree on the build order: # · ♥ · (thumb) · Title · Album · AddedBy · DateAdded · Plays · Tempo · Duration · Video.)
    //
    // Drop order as the area narrows (most expendable first): Added-by (≥1) → Album (≥2) → Plays/Date (≥3) →
    // art-thumb (≥5) → ♥ (≥5) → the trailing Video/"…" lane (6). Plays exists on album surfaces + the playlist/Liked
    // opt-in column (DetailConfig.PlaysColumnOptIn × the persisted setting, carried in the snapshot); Video follows any hydrated
    // list that has one and is the LAST thing to go (28 DIP, and the row's only statement that a video exists).
    // Album/By/Date exist only on playlists. Compact playlist tiers retain Album in the title metadata subline.
    // Derived from the SNAPSHOT, never from the parent's mutable fields: a persistent row re-renders on its own
    // subscriptions (index rebind, sort, now-playing, and now the tier), so it must be able to derive its column set
    // without depending on this parent having published _cfg/_model first. Same contract as TrackRowsSnapshot itself.
    ColumnSet SetFor(in TrackRowsSnapshot s, int tier)
    {
        var identity = DetailTrackTableRules.IdentityColumns(
            s.Classic, s.Config.ShowArtThumb, s.TrackArtworkHidden, s.Config.ShowTrackArtist, tier);
        var trailing = DetailTrackTableRules.TrailingColumns(
            s.Classic, s.Model.HasVideo, s.Config.ShowVersions, tier);
        return _verticalHeader
        // Vertical (Apple Music) profile: a simplified # · ♥ · (thumb) · Song(title + artist subline) · (Album) · Time · [⋯]
        // table. The artist rides the title subline (Spotify-style, per config), never its own lane; Album appears at wide
        // tiers (playlists/Liked) on the SAME gate as the standard profile; album surfaces retain their Plays lane so
        // stacked and automatic layouts expose the same hydrated metadata.
        // The heart follows the SAME tier gate as the standard profile. It used to be hard-false here with "liking stays
        // via hover ⋯ / context menu" — but this profile is FORCED at every width by the "Hero" page-layout setting
        // (DetailShell), so an album/playlist page in that layout could not state whether a song was saved at all, at any
        // width (user report 2026-08-10: "on album/playlist pages it's not visible if a song is liked or not"). Saved-ness
        // is a FACT the row owes the reader; the outline heart stays painted on unsaved rows so the like is always
        // reachable and the left cluster does not collapse into a dead gutter. The menu/swipe verbs remain the a11y path.
        // By/Date follow the SAME tier gates as the standard profile: the vertical SYSTEM is forced at every width by
        // the "Hero" page-layout setting (DetailShell), so hard-false here silently dropped Date-added/Added-by on WIDE
        // hero pages (user report 2026-07-23). At genuinely narrow widths the tiers hide them exactly as before; the
        // no-heart interaction profile of the vertical table is unchanged.
        // Video follows the SAME gate as the standard profile (every tier that keeps a trailing lane): a hydrated video
        // is a property of the ROW, not of the layout the page happens to be in, and hard-false here meant the film
        // glyph vanished on the hero/vertical system at every width. Actions is its exact complement — the trailing lane
        // is reserved ONCE (More rides IN the Video lane when Video is on).
        ? new(Album: s.Config.ShowAlbumColumn && tier < 2, By: s.Model.HasAddedBy && tier < 1, Date: s.Model.HasDateAdded && tier < 3,
              Video: trailing.Video,
              Plays: (s.Config.ShowPlays || (s.Config.PlaysColumnOptIn && s.PlaysColumn)) && tier < 3, Heart: tier < 5,
              Thumb: identity.Thumb,
              Actions: trailing.Actions, Tier: tier,
              Tempo: s.Config.ShowTempo && s.TempoColumn, Expand: trailing.Expand,
              Artist: identity.Artist, Classic: s.Classic)
        : new(
            Album: s.Config.ShowAlbumColumn && tier < 2,
            By: s.Model.HasAddedBy && tier < 1,
            Date: s.Model.HasDateAdded && tier < 3,
            // Video rides the trailing lane at EVERY tier that keeps one (down to, but not including, ultra-compact 6):
            // it costs 28 DIP and it is the only place the row states "this song has a video". The old tier-2 gate made
            // the glyph a wide-window luxury and forced the fact into the artist subline below — one fact in two lanes.
            Video: trailing.Video,
            Plays: (s.Config.ShowPlays || (s.Config.PlaysColumnOptIn && s.PlaysColumn)) && tier < 3,
            // Survives to tier 4 (was < 4) — a 28-DIP lane that answers "is this in my library?" (and is the like
            // affordance) earns its place further down the ladder than the old 40-DIP hover-only gutter.
            Heart: tier < 5,
            Thumb: identity.Thumb,
            // Video lane hosts More (rest=Movie / hover=bare "…") — reserve the trailing Actions track only when
            // Video is off. EXACT complement of the Video expression above, so the trailing lane is reserved once and
            // never twice. Ultra-compact still drops both (More stays reachable via the row context menu).
            Actions: trailing.Actions,
            Tier: tier,
            // Tempo gates on the tier inside TrackRow.ShowTempo (<= 3), so the flag here is purely "does this surface
            // want the column at all" — one place decides presence, one place decides width pressure.
            Tempo: s.Config.ShowTempo && s.TempoColumn,
            // The drawer needs room to breathe, so the chevron follows the same width gate as the "…" lane.
            Expand: trailing.Expand,
            Artist: identity.Artist, Classic: s.Classic);
    }

    // Right-area-width breakpoints (sized off the widest column set), so the Star Title keeps a usable width at each
    // tier's minimum. Fewer-column contexts just cross the same widths with nothing to drop until a present column.
    static int TierFor(float w, int prev, bool initialized) => DetailLayoutBreakpoints.TierFor(w, prev, initialized);

    // Self-heal: never RENDER a tier wider than the last measured width supports. If the tier signal is somehow stale
    // (a lost measure) a too-wide column set meets a too-narrow pane and the grid's overflow guard crushes the tracks;
    // clamping here makes every render structurally safe regardless of how the signal got there. Narrower-than-needed
    // is fine — the next OnBoundsChanged widens it. Applied in ONE place (the shape memo) so the header, the rows and
    // the shimmer can never disagree about which tier they are drawing.
    int ClampTier(int tier)
    {
        if (_lastRightW <= 0f)
        {
            // Subscribe ONLY while the seed actually governs, and only to the one-shot flip: the pre-measure seed comes
            // from the WINDOW viewport, which is wider than this right column by the nav pane + the metadata rail, so it
            // can only err WIDE (a first composition that admits Added-by and starves the Star Title). The tier signal
            // alone cannot retire it, because a Signal coalesces an equal-valued write: a first measure that computes
            // the number the signal already holds notifies nobody and the seeded composition survives. _tierMeasured
            // flips exactly once, so the first real width always invalidates the renders that used the seed — and,
            // because the subscription is taken only on this branch, it costs no re-render in the measured steady state.
            _ = _tierMeasured.Value;
            return Math.Max(tier, _initialTierSeed);
        }
        int fit = TierFor(_lastRightW, tier, initialized: true);
        return fit > tier ? fit : tier;
    }

    // The tier's column tracks (cached): [#, Title*, Album*?, AddedBy?, DateAdded?, ♥?, Duration]. Dropped columns are
    // truly removed (the cells carry stable Keys, so the reconciler removes exactly the departing ones in place), so
    // there is no wasted gap.
    TrackSize[] TracksFor(in ColumnSet s, float art)
    {
        var key = (s, art);
        if (_tracksBySet.TryGetValue(key, out var cached)) return cached;
        var t = new List<TrackSize>(10) { TrackSize.Px(TrackLane.Num) };
        if (s.Heart) t.Add(TrackSize.Px(TrackRow.HeartCol));  // ♥ in the LEFT cluster — between # and the art thumb
        if (s.Thumb) t.Add(TrackSize.Px(art));   // dedicated art column, density-keyed: the Title header aligns over the title text, not the art
        t.Add(TrackSize.Star(TitleStar));
        if (s.Artist) t.Add(TrackSize.Star(ArtistStar));
        // Album is a SECOND star track at AlbumStar : TitleStar (0.75 : 1), not a fixed 180 DIP lane. Two consequences,
        // both wanted: the album name can never be wider than the song title (it is the weaker fact), and the two share
        // the squeeze proportionally instead of the fixed lane holding its 180 while the flexible Title collapses toward
        // zero — the "Title starved to two characters at the widest tier" shape, where # + ♥ + thumb + Album + Added-by
        // + Date + Plays + Tempo + Duration + the trailing lanes could consume the whole tier-0 minimum.
        if (s.Album) t.Add(TrackSize.Star(AlbumStar));
        if (s.By) t.Add(TrackSize.Px(TrackLane.By));
        if (s.Date) t.Add(TrackSize.Px(TrackLane.Date));
        if (s.Plays) t.Add(TrackSize.Px(TrackLane.Plays));
        // Tempo · key — swatch + BPM + one key token (Camelot preferred). Gated by the SAME ShowTempo the row uses,
        // so the width track and the cell can never disagree (a mismatch shifts every later column).
        if (TrackRow.ShowTempo(s)) t.Add(TrackSize.Px(TrackLane.Tempo));
        t.Add(TrackSize.Px(TrackLane.Duration));
        if (s.Video) t.Add(TrackSize.Px(TrackLane.Video));     // trailing film / hover "…" (after Duration, before Expand)
        if (s.Actions) t.Add(TrackSize.Px(ActionsColWidth));   // trailing "..." when Video is off
        if (s.Expand) t.Add(TrackSize.Px(TrackLane.Expand));   // the expand chevron, last — matches ExpandChevron hit target
        var arr = t.ToArray();
        _tracksBySet[key] = arr;
        return arr;
    }

    // ── identity-first width relief ──────────────────────────────────────────────────────────────────────────────────
    // The tier ladder answers "which lanes may this WIDTH admit"; it cannot answer "which of them can the identity
    // lanes AFFORD", because it never looks at what the star tracks have left once the fixed lanes have taken theirs.
    // That gap is why a 650-DIP list still kept Date added + Plays + BPM·Key and paid for them with a one-glyph Title.
    // The relief ladder (DetailTrackTableRules) closes it: it re-measures the tier's own column set against the
    // identity floors and hands back the lanes the table can actually pay for. Applied in ONE place — the shape memo,
    // beside ClampTier — so the header, the rows and the shimmer can never disagree about which lanes are up.

    /// <summary>The set's lane presence as the pure geometry the ladder reasons about. Tempo goes through
    /// <see cref="TrackRow.ShowTempo"/> so the metric sees the lane the grid will actually build.</summary>
    static DetailTrackTableRules.TrackTableLanes LanesOf(in ColumnSet s) => new(
        Heart: s.Heart, Thumb: s.Thumb, Artist: s.Artist, Album: s.Album, By: s.By, Date: s.Date,
        Plays: s.Plays, Tempo: TrackRow.ShowTempo(s), Video: s.Video, Actions: s.Actions, Expand: s.Expand);

    /// <summary>How many lanes the last measured width forces this set to give up. Also publishes the lane geometry the
    /// bounds callback re-runs the ladder from: that callback has a fresh width but no snapshot, and this has the
    /// snapshot but a width that only ever changes underneath it.</summary>
    int ReliefStepFor(in ColumnSet s)
    {
        var lanes = LanesOf(in s);
        // The grid's own horizontal inset, as the ROWS pay it: the row skin's RowInset margin plus the grid's
        // padX−RowInset padding is exactly padX, and the header's chrome padding is that same padX (the alignment
        // invariant). The multi-select check lane is deliberately NOT counted — it is a transient mode, and the floors
        // are a guarantee about the resting table.
        float gap = TrackRow.ColGapFor(s.Tier), pad = TrackRow.PadXFor(s.Tier);
        _reliefLanes = lanes; _reliefGap = gap; _reliefPad = pad;
        return DetailTrackTableRules.ReliefFor(in lanes, _lastRightW, gap, pad, _relief.Value, _tierMeasured.Value);
    }

    /// <summary>The set with <paramref name="step"/> lanes yielded. Relief only ever REMOVES a lane the tier already
    /// admitted, so every downstream consumer — header, rows, shimmer, width tracks, the Classic artist fold in
    /// <c>RowGrid</c> — follows for free. Tempo is cleared only where the lane was actually up: that flag also means
    /// "this surface wants BPM·Key at all", and clearing it where the tier had already hidden the lane would fork the
    /// ColumnSet cache for no visible difference.</summary>
    static ColumnSet ApplyRelief(in ColumnSet s, int step)
    {
        if (step <= 0) return s;
        var r = DetailTrackTableRules.Relieve(LanesOf(in s), step);
        return s with
        {
            Heart = r.Heart, Thumb = r.Thumb, Artist = r.Artist, Album = r.Album, By = r.By, Date = r.Date,
            Plays = r.Plays, Tempo = s.Tempo && (r.Tempo || !TrackRow.ShowTempo(s)),
        };
    }

    /// <summary>The x of the parent row's ARTWORK CENTRE, in DIP from the row skin's left edge — the leading fixed
    /// columns (# · ♥) plus their gaps, plus half the art column.
    ///
    /// The drawer's connector rail is placed here so the line visibly descends OUT OF the album art: the art is the
    /// row's visual anchor, and a rail hanging off it says "these belong to that record" far more directly than one
    /// starting under the title. It also reclaims the dead band to the left of the drawer that a title-aligned indent
    /// left empty.
    ///
    /// It has to be DERIVED, not constant: the leading cluster is <c>TrackLane.Num</c> alone, or that plus ♥, or that
    /// plus ♥ plus half the art column — three different widths depending on which columns the tier kept — so the
    /// original hard-coded <c>PadX + ThumbSize</c> (52) landed mid-♥-column at every wide tier. It is derived from the
    /// SAME lane table the width tracks are built from, so shrinking a lane moves the rail with it.</summary>
    static float ArtCentreIndent(in ColumnSet s, float art)
    {
        float gap = TrackRow.ColGapFor(s.Tier);
        float x = TrackRow.PadXFor(s.Tier) - (s.Classic ? 0f : TrackRow.RowInset);   // the grid's own left pad
        x += TrackLane.Num;                                       // the # column
        if (s.Heart) x += gap + TrackRow.HeartCol;
        // Land on the MIDDLE of the art so the rail drops from the centre of the cover, not its edge.
        return s.Thumb ? x + gap + art / 2f : x + gap;
    }

    public override Element Render()
    {
        _play = UseAsyncCommands<string>();      // keyed by track id; a row's #-cell shows the buffer spinner while its PlayAsync runs (same instance each render)
        _lib = UseContext(LibraryBridge.Slot);   // Mutations bridge for the per-row heart (saved-state + toggle)
        _acts = UseContext(ActionServices.Slot); // the action system behind the row context menus + the batch bar
        _menuOverlay = UseContext(Overlay.Service);   // the overlay service the rows' attached menus open through
        var svc = UseContext(Services.Slot);     // extender client (recommended songs) + gate on live edits
        var viewport = UseContextSignal(Viewport.Size);
        if (_lastRightW <= 0f)
        {
            float seedW = viewport.Peek().Width;
            _initialTierSeed = DetailLayoutBreakpoints.InitialTierForViewport(seedW);
            // The vertical arm's own column IS the page (no sibling rail), so its pre-measure flow seed uses the same
            // PAGE-width estimate DetailShell seeds its mode from, not the raw window viewport — see
            // DetailLayoutBreakpoints.EstimatePageWidthFromViewport.
            if (_verticalHeader && !_verticalHeroFlowInitialized)
                _verticalHeroRowFlow = DetailVerticalLayout.RowFlow(DetailLayoutBreakpoints.EstimatePageWidthFromViewport(seedW));
        }
        _svc = svc; _post = UsePost();           // cached so the rec fetch/add handlers reach the extender + marshal results back to the UI thread
        Context.UseSignalEffect(() => Reactive.OnCleanup(() => { try { _recCts.Cancel(); _recCts.Dispose(); } catch { } }));   // cancel in-flight rec fetches on unmount
        UseEffect(() =>
        {
            // Do not reset the measured hero height here. Passive effects drain after paint, so on first navigation
            // this runs AFTER OnBoundsChanged has published the real height; clearing it then re-bakes PresentedH with
            // the fallback while layout still reserves the natural height, clipping the hero until the next resize.
            _verticalCompactInteractive.Value = false;
            _verticalBodyClipEngaged.Value = false;
            _searchExpanded.Value = false;
            _searchFocused.Value = false;
            _restoreSearchFocus = false;
        }, _route.Value.Name);

        // Spotify's curated Liked chip set. Fetched once per Liked route (the service itself coalesces, caches and
        // revalidates by ETag), and only for Liked — no other surface has a content-filter bar to feed.
        // The dep carries svc-readiness as well as the route: the body bails when svc is null, so keying on the route
        // alone meant a Services.Slot that resolved after first render never re-ran this and the chips never arrived.
        UseEffect(() =>
        {
            if (svc is null || !LikedSongsArtwork.IsLikedUri(_full.Value.Value.ContextUri)) return (Action?)null;
            var cts = new CancellationTokenSource();
            _ = LoadContentFilterChipsAsync(svc, _post, cts.Token);
            return (Action?)(() => { try { cts.Cancel(); cts.Dispose(); } catch { } });
        }, _route.Value.Name + (svc is null ? ":nosvc" : ":svc"));
        // One stable reactive snapshot owns every non-slot input a persistent row can observe. The memo reads the real
        // sources directly, so child rows never depend on this parent publishing mutable fields first.
        var rowsSnapshot = UseComputed(() =>
        {
            var handlers = _liveHandlers?.Value ?? _initialH;
            var currentModel = _full.Value.Value;
            var config = DetailPage.ResolveConfig(DetailPage.ParseDetail(_route.Value).Kind, currentModel);
            if (_embedded) config = config with { HasTrailing = false };
            // Epoch = the RECOMPUTE TRIGGER only (Settings.Get is a plain read, not reactive). The memo is
            // equality-gated, so what actually propagates is the returned snapshot's VALUE: bumping the epoch for an
            // appearance flag this component does not consume correctly re-renders nothing. CONTRACT: every appearance
            // setting read by a row must be carried in TrackRowsSnapshot (as noMarquee is) — a flag read outside the
            // snapshot would recompute here, compare equal, and silently never reach the rows.
            _ = AppearancePrefs.Epoch.Value;
            bool noMarquee = !(svc?.Settings.Get(WaveeSettings.MarqueeEnabled) ?? true);
            bool hideTrackArtwork = svc?.Settings.Get(WaveeSettings.HideTrackArtwork) ?? false;
            bool classic = (svc?.Settings.Get(WaveeSettings.TrackRowStyle) ?? 0) == 1;
            var filters = handlers.Filters.Value;
            IReadOnlySet<string>? saved = filters.LikedOnly ? _lib?.Saved.Value : null;
            return new TrackRowsSnapshot(
                currentModel, config, handlers,
                handlers.Sort.Value, handlers.Query.Value.Trim(), filters, saved,
                noMarquee, hideTrackArtwork, classic, TopTrack(currentModel.Tracks),
                handlers.TempoColumn.Value, handlers.PlaysColumn?.Value ?? false);
        });
        var rowItems = UseMemo(() => BoundItems.Project(
            rowsSnapshot,
            snapshot => View(snapshot).Length,
            (snapshot, displayIndex) => TrackAt(snapshot, displayIndex),
            EmptyTrack), DepKey.Empty);
        var rowAccent = UseComputed(() => rowsSnapshot.Value.Handlers.Accent);
        // The active column geometry, as a signal the persistent rows can read for themselves. A breakpoint cross now
        // re-renders the realized rows through their OWN subscription and the grid patches in place (same path a sort
        // change already takes) — instead of changing the list Key and remounting the whole virtualized viewport, which
        // is what threw the scroll position away on every rail toggle.
        var rowShape = UseComputed(() =>
        {
            var snap = rowsSnapshot.Value;
            // Two ladders, in order: the tier admits the lanes this WIDTH allows, then relief takes back the ones the
            // identity lanes cannot pay for. Relief is strictly subtractive, so this can never widen the table.
            var admitted = SetFor(in snap, ClampTier(_tier.Value));
            var set = ApplyRelief(in admitted, ReliefStepFor(in admitted));
            // Reading Density HERE (not only in Render) is what makes this memo re-run on a density change: a
            // computed only recomputes from the signals it reads, and TracksFor's Thumb column — and every row's
            // Surfaces.Artwork call — must follow the SAME density-keyed number the row height ladder already does.
            float art = DetailTrackTableRules.ArtSizeFor(_h.Density.Value, set.Classic);
            return new RowShape(set, TracksFor(in set, art), art);
        });
        _rowsSnapshot = rowsSnapshot;
        _rowShape = rowShape;
        _rowAccent = rowAccent;
        _rowItems = rowItems;

        var rowState = rowsSnapshot.Value;
        var model = rowState.Model;
        _h = rowState.Handlers;
        _model = model; _tracks = model.Tracks; _hasDate = model.HasDateAdded; _hasBy = model.HasAddedBy;
        if (_h.PlayAllOverride is { Length: > 0 } playAllCell) playAllCell[0] = () => StartVisible(0);   // rail "Play" → visible (sorted) order from the top
        _cfg = rowState.Config;
        // DetailConfig deliberately erases the route kind (an album and a compilation share one config), but the drop
        // cue has to know whether this surface is a PLAYLIST at all before it may say "Can't edit this playlist".
        _kind = DetailPage.ParseDetail(_route.Value).Kind;
        // Embedded in a compact pane (Library): drop the album trailing so the rows are the scroller (the pane owns the
        // hero + actions above). Everything else — the cell, hover transport, now-playing, heart, tier columns — is identical.
        // Reused slot: a detail-route swap changes the track set under a stable (sort,query,flags) view key, so the cached
        // view map + per-tier column sets + selection would be STALE (wrong / out-of-range indices). Invalidate them on a
        // context change so the new page recomputes cleanly.
        // Did this content actually show shimmer (a Pending→Ready load)? Tracked every render so the ready edge below
        // (which lands on a Ready render) knows whether the swap was cold or instant/cached — one of its two arm gates.
        if (_full.State.Value == (byte)LoadState.Pending) _sawPending = true;
        if (model.ContextUri != _lastCtxUri)
        {
            _lastCtxUri = model.ContextUri;
            _lastListState = null;   // a new context logs its first list state again
            _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterState.Default);
            _viewSavedSet = null;
            _tracksBySet.Clear();   // bound the cache across navigations (correctness comes from the set-keying, not this)
            _selection.ClearSelection();
            // §4.6 — navigation is not an edit: never choreograph across a context swap.
            _lastDisplayed = null;
            _flip.Clear(); _fade.Clear();
            _resetEpoch++;                               // current render remounts the virtual list — never publish a signal from Render
            // Progressive reveal: a fresh content identity ⇒ start the ramp (real rows fill in over frames instead of
            // the whole band in one ~80ms frame). Keyed to the ContextUri edge, so it fires ONCE per content and never
            // on scroll / re-render / a same-context refresh.
            // Two arming conditions, because two different things cost the frame:
            //   · _sawPending — this content actually showed shimmer (a cold Pending→Ready load), any page kind.
            //   · HasTrailing (album/single) — the list is a natural-size, Grow=0 ItemsView inside the trailing page
            //     scroller, so the WHOLE list is a mandatory band and the engine's realize budget cannot spread it: a
            //     WARM KeepAlive re-open (preview already Ready ⇒ no shimmer ⇒ _sawPending false) still mounted every
            //     row in one frame. Those pages therefore ramp on EVERY context edge, cold or warm.
            if (model.ContextUri is { Length: > 0 } && (_sawPending || _cfg.HasTrailing))
            {
                _reveal.Value = DetailRevealRamp.Chunk;   // the swap frame shows the first chunk real, the rest shimmer
                _rampActive.Value = true;
                _sawPending = false;
            }
        }

        // (The old "columns changed → drop the cached sizes" guard is gone: the tracks cache is keyed by the ColumnSet
        // itself, which already folds in Date-added/Added-by/Video arriving with the full model, so a changed column set
        // simply misses the cache and builds its own entry. Header and rows read the same set, so they cannot disagree.)

        // In-place refresh guard (§4.2): the view map is keyed only on (sort,query,flags), but JoinMembership hands us a
        // FRESH list instance whenever the tracks change — and a same-ContextUri live edit (a phone-side add/remove/move)
        // keeps the ContextUri guard above from firing. Key the view cache on the track-set IDENTITY so a shrink can't
        // leave a stale over-range index map (the latent IndexOutOfRange) and a same-size change can't show wrong rows.
        // Selection/scroll are NOT touched here — a same-playlist refresh must preserve them (the ContextUri guard owns that).
        bool trackSetChanged = !ReferenceEquals(_lastTrackSet, model.Tracks);
        if (trackSetChanged)
        {
            _lastTrackSet = model.Tracks;
            _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterState.Default);
            _viewSavedSet = null;
        }
        // The optimistic-membership handoff edge for the framework-owned insertion gap: a NEW track-set instance means
        // the real list accepted the mutation, so the temporary gap closes into its FLIP with no blank frame between.
        UseLayoutEffect(() => _listCtl.ObserveInsertionMembership(model.Tracks), DepKey.FromRef(model.Tracks));

        int tier = ClampTier(_tier.Value);       // subscribe → re-render (new header/chrome) on a breakpoint cross
        var shape = rowShape.Value;              // the SAME value the persistent rows read — header and rows stay aligned
        var set = shape.Set;
        var tracks = shape.Tracks;
        var sort = _h.Sort.Value;                // subscribe → re-render (header carets) on sort change
        int density = _h.Density.Value;          // subscribe → remount with the new row height on density change
        string query = _h.Query.Value;           // subscribe → remount with the filtered set on query change
        var filters = _h.Filters.Value;          // subscribe → update on local advanced-filter changes
        float rowH = DetailTrackTableRules.RowHeightFor(density, set.Classic);
        // The row's art edge, on the SAME density read above — shape.Art already carries it (computed inside
        // rowShape, which is where the Thumb column width comes from), so the row/art breathing-room invariant
        // (row − art ≥ 16, DetailTrackTableRules.ArtSizeFor) holds by construction rather than by two call sites
        // agreeing to compute the same thing twice.
        float art = shape.Art;
        var verticalLayout = UseMemo(() => new MeasuredStackVirtualLayout(rowH), rowH);
        // The flat list's layout. Stateful — hoisted so it survives re-renders and keeps its extent table.
        var flatLayout = UseMemo(() => new MeasuredStackVirtualLayout(rowH), rowH);
        // Only the vertical arm mounts a hero root; the two-column arm never reads this (its rail is a sibling column).
        float verticalHeroH = _verticalHeader ? VerticalHeaderHeight(subscribe: true) : 0f;
        float verticalCollapse = DetailVerticalLayout.CollapseDistance(verticalHeroH);
        _selectedCount = UseComputed(() =>
        {
            _ = _selection.Version.Value;
            int n = 0;
            for (int i = 0; i < _selection.ItemCount; i++)
                if (_selection.IsSelected(i) && DisplayTrack(i, TrackStart) is not null)
                    n++;
            return n;
        });
        _checksVisible = UseComputed(() => _h.MultiSelect?.Value == true || _selectedCount.Value >= 2);
        _selectionCommandsVisible = UseComputed(() =>
        {
            int count = _selectedCount.Value;
            return count > 0 && (_h.MultiSelect?.Value == true || count >= 2);
        });
        _checksVisibleRead = () => _checksVisible.Value;
        bool checkInset = _checksVisible.Value;

        // §4.6 — a same-context in-place track-set swap (a live push / /diff / a background refresh landing via SetReady)
        // narrates itself. Everything happens HERE, in the render that commits the new order, so the anchor adjust and
        // the FLIP/fade seeds land with the SAME frame — a post-layout seed is one frame late and reads as a
        // jump-then-snap-back flash. The snapshot refreshes EVERY render (sort/filter also reorder the displayed
        // sequence), so the diff baseline is always the order the user was actually looking at.
        int resetEpochBefore = _resetEpoch;
        {
            var vNow = View();
            var displayedNow = new Track[vNow.Length];
            for (int i = 0; i < vNow.Length; i++) displayedNow[i] = _tracks[vNow[i]];
            if (trackSetChanged && _lastDisplayed is { Length: > 0 } prevDisplayed && model.ContextUri == _lastCtxUri)
            {
                Choreograph(prevDisplayed, displayedNow, rowH);
                _dealtThisFrame = true;   // a membership narration outranks the breakpoint re-deal; never seed both
            }
            _lastDisplayed = displayedNow;
        }
        // A breakpoint cross re-composes every visible row (columns appear/disappear, the title lane re-truncates). Narrate
        // it as ONE deliberate re-deal instead of a silent pop: each realized row eases in from a 6px rise with a short
        // per-row stagger, top-down. Seeded HERE, in the render that commits the new column shape, so the seeds and the
        // new geometry land on the SAME frame (a post-layout seed is one frame late and reads as a flash).
        ReDeal(tier, rowH);
        _dealtThisFrame = false;
        // Curated re-cut (reset epoch) remounts replay row mount-opacity; tier/density/filter remounts do not.
        bool narrateRemount = _resetEpoch != resetEpochBefore;
        // The bound slots are cheap and persistent. Partial cold materialization leaves the track window visibly
        // catching up during fast scroll, especially when this list is embedded in the trailing page scroller.
        bool staggerCold = false;
        // Now-playing / sort / column re-skin is per-row now: each bound row subscribes to the bridge + _h.Sort inside
        // its own binds (BoundRowContent / BoundTitle), so a track change recolours and a sort change reorders the
        // realized rows IN PLACE — no whole-list epoch, no list re-render. Tier/column changes alter the slot SET and
        // remount via the keyed wrapper below.
        // Labels only at the widest tiers (≥ 720px): the LABELED bar alone is ~400px, so with the fixed 220px search box
        // it needs ~630px — at the old tier-3 threshold (440px) the bar overflowed and the card clip cut the search box
        // mid-control. Icon-only + the tiered search width below always fit each tier's minimum.
        bool labeled = tier <= 1;
        Element? contentFilterBar = ContentFilterBar();
        Element? lensHeader = LensHeader();
        bool verticalHasContentFilter = _verticalHeader && contentFilterBar is not null;
        bool verticalHasLens = _verticalHeader && lensHeader is not null;
        // Both rows are part of the STICKY chrome, so both have to be in the inset the item band is clipped by — a
        // lens header that was not counted here would leave the rows sliding under it (LikedLens.HeaderExtent is a
        // constant for exactly this reason: the header must not change height with its content).
        float verticalStickyInset = DetailVerticalLayout.StickyClipInset(
            (verticalHasContentFilter ? ContentFilterChips.VerticalExtent : 0f)
            + (verticalHasLens ? LikedLens.HeaderExtent : 0f));
        // ItemsView options are frozen at mount, but the Liked filter rail can arrive after enrichment. Patch the live
        // viewport's shared suffix band in a layout effect so 93→141 DIP updates in place without remounting the list
        // (a remount would race ScrollMemory and can restore a stale offset).
        UseLayoutEffect(() => ApplyVerticalItemBand(verticalStickyInset),
            DepKey.From(HashCode.Combine(verticalStickyInset, verticalHasContentFilter, verticalHasLens,
                                         _route.Value.Name, _resetEpoch)));
        Element chrome = Chrome(set, tracks, sort, labeled, tier, checkInset,
            padX: TrackRow.PadXFor(tier), contentFilterBar: contentFilterBar, lensHeader: lensHeader);
        int visible = View().Length;
        var listState = ListStateFor(model, visible);
        // "Recommended songs": owned/collaborative playlists only, non-embedded, non-vertical, live edits available. When
        // ON, the header (+ rec rows) are appended AFTER the track rows in the SAME bound list — the list TOTAL is a
        // SEPARATE signal (_listCount) so _visibleCount stays the track count the §4.6 choreography seeds are keyed to.
        // The gate is deliberately split in two halves that live on different clocks.
        // CAPABLE is the MOUNT-STABLE half — page config only (a playlist layout, standalone, with a rail). It is the
        // only half the list Key and the bound TEMPLATE may depend on, because an ItemsView's slot template freezes at
        // slot creation. LIVE adds everything that arrives WITH THE DATA (extender client, live edits, CanEditItems, a
        // context uri) and gates the COUNT only. Folding the live half into the Key (as this used to) meant the moment
        // the full model replaced the nav preview mid-navigation, CanEditItems flipped, the Key changed, and every
        // bound slot in the viewport was destroyed and recreated — the second row-rebuild wave in a nav. Now the flip
        // only grows _listCount, the CountSignal realizes the header + rec rows on top, and nothing remounts.
        bool recsCapable = _cfg.Recommendations && !_embedded && !_verticalHeader;
        bool recsLive =
            recsCapable
            && svc?.RealExtender is not null
            && PlaylistInlineEdit.SpotifyEditsLive(svc)
            && PlaylistInlineEdit.Editable(model)
            && model.ContextUri is { Length: > 0 };
        _recsLive = recsLive;                            // published to the bound slots (plain field: never write a signal from Render)
        int recCount = recsLive ? _recs.Value.Count : 0;   // subscribe (only when LIVE) → the list re-sizes when a batch lands / an add removes one
        int listTotal = recsLive ? visible + 1 + recCount : visible;   // +1 = the always-present "Recommended" header row
        // The HERO system's page FOOTER: the metadata-facts bento. Same panel the two-column arm puts in its rail, and
        // the same honesty gate (LikedFacts.Has — a list with nothing true to say mounts no cards, so no footer); the
        // only thing that changed is WHERE the hero system puts it. Not in the hero's identity column any more: that
        // column is the page's opening, and three stacked analytics cards there push the first track below the fold.
        // Has() is an allocation-free early-exit scan (it already runs on every rail render), so it is cheap here.
        bool verticalFacts = _verticalHeader && !_cfg.HasTrailing && LikedFacts.Has(model, _cfg.Badges);
        UseLayoutEffect(() =>
        {
            _visibleCount.Value = visible;
            _listCount.Value = listTotal;
            _verticalFacts.Value = verticalFacts;
            _verticalItemCount.Value = DetailVerticalLayout.ItemCount(visible, verticalFacts);
        }, DepKey.From(HashCode.Combine(visible, listTotal, verticalFacts)));

        Element RealList()
        {
            // Playlist/liked vertical keeps hero + chrome as persistent virtual-prefix items. The list therefore owns
            // a real viewport and realizes only its bounded row window, while the recorder applies one shared item-band
            // clip below the sticky chrome (never one reactive clip binding per realized row).
            if (_verticalHeader && !_cfg.HasTrailing)
                return VerticalList(visible, set, tracks, labeled, tier, rowH, narrateRemount, staggerCold,
                    verticalLayout, verticalStickyInset, verticalFacts);
            if (recsCapable)
            {
                // A search/filter that matched nothing still shows the no-match message (recs browse the whole list, not
                // the filtered slice); an EMPTY owned playlist still renders — the header at index 0 fetches immediately.
                // While the gate is capable but not yet LIVE the total is just the track count, so an empty list has no
                // header to render either — fall back to the empty-playlist message exactly like a non-recs page does.
                // A membership that has not landed is not an empty playlist: shimmer, never a header over nothing.
                if (listState == PlaylistRowsState.Loading || (visible == 0 && (_tracks.Count > 0 || !recsLive)))
                    return ListPlaceholder(listState, set, tracks, rowH, art);
                // The bound slots branch on the recycled index (RowOrRecContent): track rows keep the selection skin;
                // the "Recommended" header + rec rows render their OWN content, so they never join the track multi-select
                // (exactly like the vertical hero/chrome rows). The out-of-range guards (PlayRow / DisplayTrack / the
                // SelectionBar null-track skip) already reject the appended indices.
                return ItemsView.CreateBound(
                    listTotal,
                    scope => Embed.Comp(() => new RowOrRecContent(this, scope, rowH, narrateRemount)),
                    // Measured, like the plain branch: a track row in THIS list expands too, and a uniform stride can
                    // physically not let one row grow — the drawer mounted and was clipped to rowH, so the chevron
                    // flipped and nothing appeared. The rec header/rows still measure rowH, so this costs nothing when
                    // no drawer is open.
                    RepeatLayout.Measured(flatLayout),
                    new ListOptions
                    {
                        SelectionMode = _cfg.Selection,
                        Selection = _selection,
                        IsItemInvokedEnabled = true,
                        OnInvoked = i => { if (rowItems.TryPeek(i, out _)) PlayRow(i); },
                        IsItemEnabled = i => rowItems.TryPeek(i, out _),   // only track rows are roving-focus / selection targets
                        Overscan = TrackOverscanItems,
                        Grow = _cfg.HasTrailing ? 0f : 1f,
                        Controller = _listCtl,
                        CountSignal = _listCount,
                        Scroll = new ScrollOptions { ScrollKey = _route.Value.Name + ":r" + _resetEpoch, AutoEdgeFade = !_cfg.HasTrailing, OnScrollGeometryChanged = SwipeCloseObserver() },
                        Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                        Insertion = Insertion(),
                        Entrance = new EntranceOptions { StaggerColdRealize = staggerCold, ItemFlipFrom = SeedFlip, ItemFadeFrom = SeedFade },
                    });
            }
            return listState != PlaylistRowsState.Rows
            ? ListPlaceholder(listState, set, tracks, rowH, art)   // loading, empty playlist, or a filter that matched nothing
            // Bound rows (signals-first): each slot mounts ONCE and recycles by an index-signal write. Selection flips a
            // bound pill opacity — no list re-render, no remount, no Enter replay (the flash fix); now-playing/sort
            // re-skin each row's content in place via its own subscriptions. The row maps its display position → track
            // through View() inside its binds (sort-subscribed), so a sort change reorders in place; filter/density/tier
            // change the slot SET and remount via the keyed wrapper below.
            : ItemsView.CreateBound(
                rowItems,
                scope => ExpandableSlot(scope.Row, scope.Item, rowH, narrateRemount),
                // Measured, not a uniform stack: an open drawer makes exactly one row taller, and the measured layout
                // corrects that row's extent on arrange AND re-anchors the scroll so nothing jumps. With no drawer
                // open every row measures rowH, so this is identical to the old fixed-extent behaviour.
                RepeatLayout.Measured(flatLayout),
                new ListOptions<Track>
                {
                    SelectionMode = _cfg.Selection,
                    Selection = _selection,                // external → selection survives the tier remount
                    IsItemInvokedEnabled = true,
                    OnInvokedTyped = (i, _) => PlayRow(i),   // DoubleTap / Enter → same as a row click (visible-order play + now-playing toggle)
                    Overscan = TrackOverscanItems,
                    Grow = _cfg.HasTrailing ? 0f : 1f,
                    // §4.6 choreography: the controller reads/adjusts the scroll for anchoring; the displacement seed
                    // (target always rest) starts each row from its FLIP residual and eases added rows' opacity in.
                    Controller = _listCtl,
                    Scroll = new ScrollOptions
                    {
                        // Alpha-mask edge fade: the page floats over a gradient wash (no opaque plate), so the surface-colour
                        // EdgeCues fade self-skips — this feathers the rows' own alpha at the overflowing top/bottom instead.
                        // Nested under vertical sticky / album trailing scroll: the OUTER ScrollView owns scrolling.
                        AutoEdgeFade = !_cfg.HasTrailing,
                        // Scroll-position restoration keyed by the detail content (route): navigate away from a 10k-track
                        // playlist and back and the viewport seeds the saved row BEFORE its first realize — no scroll-to-top
                        // flash, no jump (the engine scopes this per tab via the KeepAlive slot). A different album starts at
                        // top. The reset epoch folds in so a curated re-cut starts a FRESH scroll state (top) instead of
                        // restoring the pre-reset offset into all-new content.
                        ScrollKey = _route.Value.Name + ":r" + _resetEpoch,
                        OnScrollGeometryChanged = SwipeCloseObserver(),
                    },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions
                    {
                        // Realize the full oversized row window immediately. Bound slots are persistent; exposing partial
                        // materialization during scroll reads as cut-off rows under the fixed chrome.
                        StaggerColdRealize = staggerCold,
                        ItemFlipFrom = SeedFlip,
                        ItemFadeFrom = SeedFade,
                    },
                });
        }

        // The tracks stream in via the engine's skeleton boundary: while the model is Pending it shows shimmer rows the
        // engine DERIVES from the real Row(EmptyTrack) template (ONE definition — no hand-written shimmer, no drift); on
        // Ready it reveals the real virtualized list.
        // FadeOnly (not StaggerRows): ContentHost already slides the page in. A per-row blur-rise on Ready was a
        // second entrance after that slide — first open cascaded, KeepAlive back did not. Newly realized overscan/
        // scroll rows never replayed the navigation reveal either way.
        // The insertion destination is declared ON the list (see Insertion()) — no wrapper lane, no Configure, and no
        // app-side geometry at all: the ItemsView owns viewport, scroll offset, MEASURED extents, prefix and slot. The
        // page-level gates (editable? sorted? filtered?) are answered LIVE by CanAccept, so a refused drop is a refusal
        // the engine can cue rather than a destination that silently never mounted.
        // D49 — WHAT THE SHIMMER HAS TO RESERVE. In the two-column arm the rail is a sibling COLUMN and the chrome is a
        // sibling ROW of this boundary, so both are outside it and were always held open; only the rows shimmer. The
        // vertical/hero arm is the opposite: hero and chrome are persistent PREFIX ITEMS of the virtualized list
        // (VerticalList items 0 and 1), so they live INSIDE this boundary and simply did not exist while Pending — the
        // page opened as rows at y=0 and the whole list was shoved down by several hundred DIP when content landed.
        // The shimmer therefore leads with the hero band (same pure resolver, same parts, same sizes) and the REAL
        // chrome element, then the rows. Reveal behaviour is untouched.
        Element list = Skel.Region(_full,
            () => _verticalHeader && !_cfg.HasTrailing
                ? VerticalShimmer(set, tracks, sort, labeled, tier, checkInset, contentFilterBar, rowH, art)
                : RowsShimmer(set, tracks, rowH, art),
            _ => RealList(), reveal: SkelReveal.FadeOnly, smoothResize: false);

        // Key the list by density + filter → either REMOUNTS it (a clean slot template with the right row height /
        // filtered window). Sort is NOT in the key — each bound row re-skins itself to the new order via its
        // sort-subscribed binds (scroll preserved).
        // TIER IS DELIBERATELY *NOT* IN THE KEY. It used to be, because the cell arity was frozen per slot at mount —
        // so a breakpoint cross rebuilt every slot, and a rebuild meant a NEW viewport node whose scroll offset was
        // seeded from ScrollMemory *before* the outgoing viewport had written its live offset there (Reconciler mounts
        // new keyed children before removing old ones). The visible bug: opening the right rail threw the list back to
        // the top, and toggling it ping-ponged between two stale offsets. Now the column shape is a memo the rows READ
        // (_rowShape), so a cross re-renders the realized rows and the grid patches in place — same path a sort change
        // already took — and the viewport, with its scroll position, is never torn down at all.
        float listGrow = _cfg.HasTrailing ? 0f : 1f;
        // The reset epoch folds into the key: a curated re-cut REMOUNTS the list — the slots' mount-opacity entrance
        // replays as the §4.6 "playlist was refreshed" crossfade (the truthful narration for an editorial re-cut).
        // recsCAPABLE — not recsLive — folds into the key: the ItemsView template + count-signal freeze at mount, so the
        // template choice must ride a value that CANNOT change while this list is mounted (page config only). The live
        // half deliberately stays out: it flips when the full model lands mid-navigation, and folding it in remounted
        // every bound slot in the viewport for nothing. A capable page mounts the recommendations template up front and
        // simply carries a total of `visible` until the gate goes live.
        string filterKey = _verticalHeader ? "" : ":q" + query + ":f" + filters.GetHashCode();
        Element listKeyed = new BoxEl { Key = "list:" + _route.Value.Name + ":" + (_verticalHeader ? "vh:" : "") + "d" + density + (set.Classic ? ":classic" : ":modern") + filterKey + ":r" + _resetEpoch + (recsCapable ? ":rec" : ""), Grow = listGrow, Shrink = 1f, MinHeight = 0f, Direction = 1, Children = [list] };

        Element rightBody = _cfg.HasTrailing
            ? TrailingBody(listKeyed,
                _verticalHeader ? VerticalHeroRoot(verticalHeroH, verticalCollapse) : null,
                _verticalHeader ? VerticalChromeRoot(chrome) : null,
                verticalStickyInset)
            : listKeyed;

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            // Measure the right-area width → the active tier. Value-gated, so a re-render happens only on a breakpoint
            // cross (not every resize frame); the new tier itself never changes this box's width → no feedback loop.
            OnBoundsChanged = r =>
            {
                if (r.W <= 0f) return;
                _lastRightW = r.W;
                if (_verticalHeader)
                {
                    bool rowFlow = DetailVerticalLayout.RowFlow(
                        r.W, _verticalHeroRowFlow, _verticalHeroFlowInitialized);
                    // A stacked ↔ row restructure changes the natural hero height a lot (a 280 cover ABOVE the copy vs
                    // a 200-240 cover BESIDE it). Clear the cached measure so PresentedH's OutStart cannot stay stuck
                    // on the previous flow's height and clip the new composition (missing actions / empty lower hero).
                    if (_verticalHeroFlowInitialized && rowFlow != _verticalHeroRowFlow)
                        _verticalHeaderHeight.Value = 0f;
                    _verticalHeroRowFlow = rowFlow;
                    _verticalHeroFlowInitialized = true;
                    if (MathF.Abs(_verticalHeroW.Peek() - r.W) > 4f) _verticalHeroW.Value = r.W;
                }
                // The FIRST real width is authoritative: it takes the NOMINAL tier with no hysteresis (initialized:
                // false), because there is nothing yet to be hysteretic about — the signal still holds its construction
                // default and the composition so far came from the viewport seed. Every later measure crosses the dip
                // band normally. Then flip _tierMeasured, which is what actually retires the seed (ClampTier).
                bool measured = _tierMeasured.Peek();
                int t = TierFor(r.W, _tier.Peek(), measured);
                if (t != _tier.Peek()) _tier.Value = t;
                // …then the finer relief ladder, over the lane geometry the shape memo published for the set the tier
                // admitted. Same value-gate, same first-measure rule: a width that changes no lane costs no re-render.
                int relief = DetailTrackTableRules.ReliefFor(
                    in _reliefLanes, r.W, _reliefGap, _reliefPad, _relief.Peek(), measured);
                if (relief != _relief.Peek()) _relief.Value = relief;
                if (!measured) _tierMeasured.Value = true;
            },
            Children = _verticalHeader ? [rightBody] : [chrome, rightBody],
        };
        // The per-frame reveal clock: mounted ONLY while a cold ramp is in flight (Flow.Show gated on _rampActive), so it
        // advances _reveal once per frame and then unmounts — the frame loop quiesces (no forever-loop). Copies the
        // FrameClock.Tick idiom (TickerClock / CountTicker). Hidden 0×0 node → no layout/hit-test footprint.
        // Wrapped in a zero-size hit-invisible Box: a bare ZStack sibling above the column would capture HitAny (topmost sibling)
        // and kill wheel scrolling — the list's scroller lives inside `column`, not up the ancestor chain from this node.
        Element revealClock = new BoxEl
        {
            HitTestVisible = false,
            Width = 0f,
            Height = 0f,
            Children = [Flow.Show(() => _rampActive.Value,
                Embed.Comp(() => new TickerClock { OnFrame = _ => AdvanceReveal() }))],
        };
        return ZStack(column, revealClock) with { Grow = 1f, Shrink = 1f, MinHeight = 0f };
    }

    // Resolve a display row index (what the SelectionModel stores) → the track, through the current filtered+sorted view.
    Track? DisplayTrack(int itemIndex, int trackStart)
    {
        return _rowItems is { } items && items.TryPeek(itemIndex, out var track, trackStart) ? track : null;
    }

    // The FLIP/fade seeds belong to exactly ONE _dispVer bump — the Choreograph / ReDeal write that filled them, which
    // are now this signal's ONLY writers (the drop lane that used to share the bus is gone; the framework-owned
    // insertion runs on its own version signal). The app-side epoch guard that de-duplicated the shared bus is deleted
    // with it: ItemsView edge-gates the seeds itself now (see its lastEntranceVer check), so a re-run of the seed
    // effect can no longer replay a spent choreography as a phantom half-fade on rows that never moved.
    (float dx, float dy)? SeedFlip(int display)
        => _flip.TryGetValue(display, out var f) ? f : null;

    (float from, float delayMs)? SeedFade(int display)
        => _fade.TryGetValue(display, out var f) ? f : null;

    // ONE definition of "which membership position does this display slot name" — the page and the keyed-anchor gate
    // must agree about it or the gate would clear a slot the commit then resolves differently.
    int OriginalInsertionIndex(int displaySlot)
        => PlaylistReorderRules.OriginalInsertionIndex(View(), _tracks.Count, displaySlot);

    // ── the declarative drop destination (framework-owned geometry; this page declares only INTENT) ───────────────────
    // Created ONCE and reused by all three list branches (flat · recommendations · vertical hero). Every delegate reads
    // LIVE page state — the record is frozen at mount like every other ItemsView option, so a captured snapshot would
    // answer for a playlist the user left three navigations ago.
    InsertionOptions Insertion() => _insertion ??= new InsertionOptions
    {
        AcceptKinds = [WaveeDragKinds.Resource],
        CanAccept = CanDropResource,
        // Sit the gesture out entirely on a surface that is not a playlist and never will be — an ALBUM page's track
        // table, the embedded library album pane. CanAccept would also turn the drop away there, but a refusal owes a
        // reason and the only one the table has is "Can't edit this playlist", which is about a thing the user is not
        // looking at: the accusation wears a not-allowed glyph across the whole pane for a drag that was only ever
        // passing through. Transparent lets discovery continue instead (B2's latent cousin). A read-only PLAYLIST is
        // deliberately NOT included — "Can't edit this playlist" is the literal truth there, and it is useful.
        Transparent = _ => _kind is DetailKind.Album or DetailKind.Show && !DropEditable,
        IsSameList = IsSameListDrop,
        // Scrim policy (A14): a SAME-LIST reorder must never dim the app. The user is aiming inside the very list they
        // are looking at — darkening everything but that list adds a full-window veil to a gesture whose destination is
        // already under the pointer, and the line + gap already say where the block lands. A CROSS-list deposit is the
        // opposite case (the destination is somewhere else entirely), so it keeps the scrim.
        SpotlightWhen = s => !IsSameListDrop(s.Payload),
        SourceIndices = DropSourceDisplayRows,
        DraggedCount = DropTrackCount,
        // The insertable sub-range: the TRACK rows only. The vertical-hero layout leads with hero + chrome as
        // persistent prefix items, and a recommendations-capable list appends a header + N cards — neither may ride
        // the gap down, and the framework derives the LEADING extent from the prefix items' MEASURED heights (the
        // hard-coded 420/200 + ChromeExtent estimate this replaces was one of the four "cannot drop" causes).
        Range = () => (TrackStart, View().Length),
        OnDeposit = DepositAtAsync,
        Caption = InsertionCaption,
        RefusalCaption = DropRefusalCaption,
        GapPreview = (payload, _) => WaveeResourceDrag.Unwrap(payload) is { } resource
            ? PlaylistInsertionPreview.Cards(resource,
                DetailTrackTableRules.RowHeightFor(_h.Density.Peek(), _rowsSnapshot?.Peek().Classic ?? false),
                showArtwork: !AppearancePrefs.TrackArtworkHidden(_svc?.Settings))
            : new BoxEl { Height = 0f },
        PreviewCap = PlaylistInsertionPreview.Cap,
    };

    /// <summary>The page-level write gate, answered LIVE (a nav preview that becomes editable must not need a remount).</summary>
    bool DropEditable => _model.ContextUri is { Length: > 0 } && PlaylistInlineEdit.Editable(_model)
                         && _acts is not null && _lib is not null;

    bool IsSameListDrop(object? payload)
        => WaveeResourceDrag.Unwrap(payload) is { SourceRows.Count: > 0 } resource
           && string.Equals(resource.SourcePlaylistUri, _model.ContextUri, StringComparison.Ordinal);

    /// <summary>Score this drop against the live page state ONCE — the accept test and the refusal cue both read the
    /// same verdict, so a refusal can never be explained with a reason the gate did not actually use.
    /// <para>A same-list MOVE addresses original membership rows through the DISPLAYED order, so it is unambiguous only
    /// while the display IS the membership order (PlaylistReorderRules); a foreign COPY has no such constraint.</para></summary>
    PlaylistDropRefusal DropVerdict(object? payload)
    {
        var sort = _h.Sort.Peek();
        var rows = WaveeResourceDrag.Unwrap(payload)?.SourceRows;
        return PlaylistDropRefusalRules.Evaluate(
            editable: DropEditable,
            // A still-shimmering page has no membership to insert into: Wave 4 made an EMPTY list accept at slot 0, and
            // without this a PENDING one looks identical to it and swallows the drop.
            loading: _full.State.Peek() == (byte)LoadState.Pending,
            payloadHasTracks: WaveeResourceDrag.Unwrap(payload) is { CanCopyTracks: true },
            sameList: IsSameListDrop(payload),
            naturalOrder: sort.Column == SortColumn.Index && !sort.Descending,
            filtered: !PlaylistReorderRules.AllowsSameListMove(true, _h.Query.Peek(), _h.Filters.Peek()),
            // The keyed-reorder gate: a row whose membership item_id has not arrived yet cannot be named by the wire's
            // item-keyed MOV, and there is no positional fallback to fall back TO. Payload-level only, because the
            // accept test is answered without a slot; the ANCHOR half of the same rule is checked at the commit
            // (DepositAtAsync), which is the first moment a slot exists.
            rowsKeyed: rows is not { Count: > 0 } || PlaylistReorderRules.RowsAreKeyed(rows));
    }

    bool CanDropResource(object? payload) => DropVerdict(payload) == PlaylistDropRefusal.None;

    /// <summary>The refusal CUE. Wave 4 made every one of these gates answer live, which turned four silent
    /// "nothing happens" failures into four honest refusals — but a refusing target is transparent, so without a
    /// caption the user still just sees the drag pass over the list. This is the sentence the chip shows next to its
    /// not-allowed glyph.</summary>
    string? DropRefusalCaption(object? payload) => DropVerdict(payload) switch
    {
        PlaylistDropRefusal.NotEditable => Loc.Get(Strings.Drag.CantEditPlaylist),
        PlaylistDropRefusal.Loading => Loc.Get(Strings.Drag.StillLoading),
        // FUTURE WORK (locked product decision): an artist has no single obvious track set, so we refuse rather than
        // guess. The intended answer is to let the USER choose what to deposit — a picker offering the artist's top
        // tracks or a release from their discography — not to silently pick one for them.
        PlaylistDropRefusal.NoTracks => WaveeResourceDrag.Unwrap(payload)?.Kind == WaveeResourceKind.Artist
            ? Loc.Get(Strings.Drag.CantAddArtist)
            : Loc.Get(Strings.Drag.NothingToAdd),
        PlaylistDropRefusal.Sorted => Loc.Get(Strings.Drag.ClearSortingToReorder),
        PlaylistDropRefusal.Filtered => Loc.Get(Strings.Drag.ClearFiltersToReorder),
        PlaylistDropRefusal.Syncing => Loc.Get(Strings.Drag.StillSyncing),
        _ => null,
    };

    /// <summary>What this drop will DO, said in the chip. The verb is the whole point — the line and the gap show WHERE
    /// the block lands, but nothing else distinguishes a same-playlist MOVE (the rows leave their old slots) from a
    /// COPY out of another list, and those are different edits to the user's library.
    /// <para>A container payload (an album/playlist whose tracks are still behind a cold resolver) deliberately gets no
    /// number: the count is genuinely unknown until the drop resolves it, and a placeholder "1" would be a lie.</para></summary>
    string? InsertionCaption(object? payload, int _)
    {
        if (WaveeResourceDrag.Unwrap(payload) is not { } resource) return null;
        int rows = resource.SourceRows?.Count ?? 0;
        int tracks = resource.Tracks?.Count ?? 0;
        return PlaylistReorderRules.VerbFor(IsSameListDrop(payload), rows, tracks) switch
        {
            PlaylistDropVerb.MoveRows => Strings.Drag.MoveTracks(rows),
            PlaylistDropVerb.AddTracks => Strings.Drag.AddTracks(tracks),
            PlaylistDropVerb.AddContainer => Strings.Drag.AddTo(_model.Title),
            _ => null,
        };
    }

    int DropTrackCount(object? payload)
        => WaveeResourceDrag.Unwrap(payload) is { Tracks.Count: > 0 } resource ? resource.Tracks!.Count : 1;

    /// <summary>The dragged rows as DISPLAY positions (what the view's virtual-removal math needs). The payload carries
    /// ORIGINAL membership indices; a same-list move is only legal in natural order, so the map is normally the
    /// identity — the scan is the defensive fallback, and it runs once per gesture, never per move.</summary>
    IReadOnlyList<int>? DropSourceDisplayRows(object? payload)
    {
        if (WaveeResourceDrag.Unwrap(payload) is not { SourceRows.Count: > 0 } resource) return null;
        var rows = resource.SourceRows!;
        var view = View();
        var display = new List<int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = PlaylistReorderRules.DisplayRowOf(rows[i].Index, view);
            if (at >= 0) display.Add(at);
        }
        return display;
    }

    /// <summary>The commit. <paramref name="displaySlot"/> is the RAW slot the user aimed at: the backend's move
    /// convention already discounts the rows removed above it (pinned by MoveRowsConventionTests), so correcting here
    /// would move the block twice.</summary>
    Task<bool> DepositAtAsync(object? payload, int displaySlot)
    {
        if (_acts is not { } acts || _model.ContextUri is not { Length: > 0 } uri) return Task.FromResult(false);
        int at = OriginalInsertionIndex(displaySlot);
        // The ANCHOR half of the keyed-reorder gate (the rows half is already in DropVerdict): the wire names the
        // landing position by the predecessor row's item_id, so a predecessor whose id has not arrived cannot be a
        // landing position at all. Refused HERE — the first moment a slot exists — rather than sent and rolled back.
        if (IsSameListDrop(payload) && WaveeResourceDrag.Unwrap(payload) is { SourceRows: { Count: > 0 } rows }
            && !PlaylistReorderRules.AnchorRowIsKeyedAt(_tracks, rows, at))
        {
            PlaylistEditErrors.Toast(PlaylistMutationFailure.Pending, PlaylistEditVerb.Reorder);
            return Task.FromResult(false);
        }
        return WaveeResourceDrop.DepositTracksAsync(acts, uri, _model.Title, payload, at);
    }

    /// <summary>Alt+Up / Alt+Down: move the selected rows one position, through the SAME mutation seam and the same
    /// pre-move index convention the drag commits with (<c>MovePlaylistRowsAsync</c>). The rules themselves live in the
    /// engine-free <see cref="PlaylistReorderRules"/> so they are pinned by tests rather than by this call site.
    /// <para>The selection is re-pointed at the landed rows immediately: display order IS membership order here (the
    /// gate guarantees it), so the block's new indices are known without waiting for the snapshot — which is what keeps
    /// a held Alt+Down walking the same block down the list instead of dragging a different one each press.</para></summary>
    bool TryBlockMove(int delta)
    {
        if (_lib is not { } lib || _model.ContextUri is not { Length: > 0 } uri) return false;
        var sort = _h.Sort.Peek();
        if (!PlaylistReorderRules.AllowsBlockMove(PlaylistInlineEdit.Editable(_model),
                sort.Column == SortColumn.Index && !sort.Descending, _h.Query.Peek(), _h.Filters.Peek()))
            return false;
        if (HostInfo() is not { } host || host.Rows.Count == 0) return false;

        var rows = host.Rows;
        Span<int> indices = rows.Count <= 64 ? stackalloc int[rows.Count] : new int[rows.Count];
        for (int i = 0; i < rows.Count; i++) indices[i] = rows[i].Index;
        int to = PlaylistReorderRules.BlockMoveTarget(indices, _tracks.Count, delta);
        if (to < 0) return false;
        // The keyed-reorder gate, keystroke edition. The drag gets its refusal as a chip caption; a keyboard command
        // has no chip, so the same verdict is spoken (assertively — the block did NOT move) and toasted through the
        // one mapped-copy chokepoint, which says the same sentence.
        if (!PlaylistReorderRules.RowsAreKeyed(rows) || !PlaylistReorderRules.AnchorRowIsKeyedAt(_tracks, rows, to))
        {
            Announcer.Say(Loc.Get(Strings.Drag.StillSyncing), assertive: true);
            PlaylistEditErrors.Toast(PlaylistMutationFailure.Pending, PlaylistEditVerb.Reorder);
            return false;
        }

        int first = indices[0];
        for (int i = 1; i < indices.Length; i++) if (indices[i] < first) first = indices[i];
        var post = _post;
        _ = Commit();

        int start = TrackStart + first + delta;
        _selection.ClearSelection();
        _selection.SelectRange(start, start + rows.Count - 1);
        _selection.AnchorIndex = start;
        return true;

        // AWAITED, mapped, and — on a refusal — the selection is put back. This used to discard the move task outright:
        // a rejected reorder rolled the membership back (the rows visibly snap home) while the selection stayed pointing
        // at the slots the block would have landed in, so the next Alt+Down walked a DIFFERENT block down the list and
        // nothing ever said the first move had been refused.
        async System.Threading.Tasks.Task Commit()
        {
            try { await lib.MovePlaylistRowsTrackedAsync(uri, rows, to).ConfigureAwait(false); }
            catch (Exception ex)
            {
                ContainerActions.Post(post, () =>
                {
                    int back = TrackStart + first;
                    _selection.ClearSelection();
                    _selection.SelectRange(back, back + rows.Count - 1);
                    _selection.AnchorIndex = back;
                    PlaylistEditErrors.Toast(ex, PlaylistEditVerb.Reorder);
                });
            }
        }
    }

    WaveeResourceDragPayload? TrackDragPayload(int itemIndex, int trackStart)
    {
        if (DisplayTrack(itemIndex, trackStart) is not { Uri.Length: > 0 } dragged
            || _rowsSnapshot is not { } sourceSnapshot) return null;
        bool carrySelection = _selection.IsSelected(itemIndex);
        var snapshot = sourceSnapshot.Peek();
        var view = View(snapshot);
        var tracks = snapshot.Model.Tracks;
        var selectedTracks = new List<Track>();
        var sourceRows = new List<PlaylistRowRef>();

        void Add(int listIndex)
        {
            int display = listIndex - trackStart;
            if ((uint)display >= (uint)view.Length) return;
            int original = view[display];
            if ((uint)original >= (uint)tracks.Count) return;
            var track = tracks[original];
            selectedTracks.Add(track);
            sourceRows.Add(new PlaylistRowRef(original, track.Uri, track.ContextUid ?? string.Empty));
        }

        if (carrySelection)
        {
            for (int i = 0; i < _selection.ItemCount; i++)
                if (_selection.IsSelected(i)) Add(i);
        }
        else Add(itemIndex);

        if (selectedTracks.Count == 0) return null;
        // A page under a notice (deleted / access revoked) is not a MOVE source: dragging its rows could only produce
        // an edit the server will refuse. The rows still drag as a COPY — that is what `source == null` means.
        string? source = snapshot.Model.ContextUri is { Length: > 0 } uri && PlaylistInlineEdit.Editable(snapshot.Model)
            ? uri : null;
        string name = selectedTracks.Count == 1 ? dragged.Title : Strings.Sidebar.SongCount(selectedTracks.Count);
        // The chip glyph/caption comes from the payload KIND, so a single episode row drags as an episode rather than
        // as a song. A multi-row drag keeps the Track kind: a mixed selection has no one kind to state, and every
        // destination reads the rows themselves anyway.
        var kind = selectedTracks.Count == 1
            ? WaveeResourceDragPayload.PlayableKind(dragged.Uri)
            : WaveeResourceKind.Track;
        return new WaveeResourceDragPayload(kind, dragged.Uri, dragged.Uri, name,
            selectedTracks, source, source is null ? null : sourceRows);
    }

    // The hosting-playlist descriptor for the context menu / batch bar: only when this context is an editable playlist.
    // Maps the CURRENTLY SELECTED item indices through the live view to PlaylistRowRef(originalIndex, uri, itemId) —
    // called AFTER TrackContextMenu settles the selection, so the rows cover exactly the menu's target set (display
    // order; Index = the ORIGINAL playlist position the remove op needs).
    PlaylistHost? HostInfo()
    {
        if (_rowsSnapshot is not { } source) return null;
        var snapshot = source.Peek();
        var model = snapshot.Model;
        if (model.ContextUri is not { Length: > 0 } uri || !PlaylistInlineEdit.Editable(model)) return null;
        int trackStart = TrackStart;
        var v = View(snapshot);
        var tracks = model.Tracks;
        var rows = new List<PlaylistRowRef>();
        for (int i = 0; i < _selection.ItemCount; i++)
        {
            if (!_selection.IsSelected(i)) continue;
            int d = i - trackStart;
            if ((uint)d >= (uint)v.Length) continue;
            int orig = v[d];
            if ((uint)orig >= (uint)tracks.Count) continue;
            var t = tracks[orig];
            rows.Add(new PlaylistRowRef(orig, t.Uri, t.ContextUid ?? string.Empty));
        }
        return rows.Count == 0 ? null : new PlaylistHost(uri, model.Capabilities, rows);
    }

    // Playlist/liked vertical owns a HARD viewport. Hero + chrome are persistent leading items; all track rows remain
    // positional-recycled beneath them. ItemClipTopInset is one recorder/input band for the recyclable suffix, avoiding
    // both the unbounded outer ScrollView regression and the former O(realized rows) ClipTopAtViewport bindings.
    Element VerticalList(int visible, ColumnSet set, TrackSize[] tracks, bool labeled, int tier,
                         float rowH, bool narrateRemount, bool staggerCold, MeasuredStackVirtualLayout layout,
                         float stickyInset, bool facts)
    {
        // The MOUNT seed only — CountSignal (_verticalItemCount) owns it from the first layout effect on. `facts` is
        // passed rather than peeked off _verticalFacts so the seed and that signal cannot disagree for a frame.
        int itemCount = DetailVerticalLayout.ItemCount(visible, facts);
        int DisplayOf(int itemIndex) => itemIndex - VerticalTrackStart;
        // ROW slots only. §4.6's seeds are keyed by DISPLAY index, and the non-row slots below the prefix — the
        // empty-list placeholder and the facts footer — sit at display indices ≥ the live row count, where a stale
        // seed from a just-committed reorder would otherwise make the footer rise/fade like a track that moved.
        bool IsRowSlot(int itemIndex)
        {
            int display = DisplayOf(itemIndex);
            return display >= 0 && display < _rowItems!.Count.Peek();
        }
        (float dx, float dy)? FlipFrom(int itemIndex) => IsRowSlot(itemIndex) ? SeedFlip(DisplayOf(itemIndex)) : null;
        (float from, float delayMs)? FadeFrom(int itemIndex) => IsRowSlot(itemIndex) ? SeedFade(DisplayOf(itemIndex)) : null;

        return ItemsView.CreateBound(
            itemCount,
            scope =>
            {
                Element content = Embed.Comp(() =>
                    new VerticalItemContent(this, scope, rowH, narrateRemount));
                int initial = scope.Index.Peek();
                Element row = new BoxEl { Direction = 1, Children = [content] };
                return initial == VerticalHeroIndex
                    ? row.Collapse(VerticalHeaderHeight(), DetailVerticalLayout.CompactIdentityHeight,
                        DetailVerticalLayout.CollapseDistance(VerticalHeaderHeight()))
                    : initial == VerticalChromeIndex
                        ? row.Sticky(DetailVerticalLayout.CompactIdentityHeight, pinned => _verticalCompactInteractive.Value = pinned)
                        : row;
            },
            RepeatLayout.Measured(layout),
            new ListOptions
            {
                SelectionMode = visible > 0 ? _cfg.Selection : ItemsSelectionMode.None,
                Selection = _selection,
                IsItemInvokedEnabled = true,
                OnInvoked = i =>
                {
                    if (_rowItems!.TryPeek(i, out _, VerticalTrackStart))
                        PlayRow(DisplayOf(i));
                },
                ItemText = i => _rowItems!.TryPeek(i, out var item, VerticalTrackStart) ? item.Title : "",
                IsItemEnabled = i => _rowItems!.TryPeek(i, out _, VerticalTrackStart),
                Overscan = TrackOverscanItems,
                PersistentPrefixCount = VerticalTrackStart,
                Grow = 1f,
                Controller = _listCtl,
                CountSignal = _verticalItemCount,
                Scroll = new ScrollOptions
                {
                    ScrollKey = _route.Value.Name + ":r" + _resetEpoch,
                    AutoEdgeFade = false,
                    // NO surface-colour scroll-edge cue. It paints an OPAQUE gradient band at the top of the viewport
                    // once the list is scrolled — i.e. straight over the unpainted context band — and the colour it
                    // fades toward is resolved by an ANCESTOR walk (SceneRecorder.TryResolveCueSurface). This page's
                    // ground is the art-derived tone PLANE, a ZStack SIBLING, so the walk sails past it to the shell's
                    // neutral ground and lands a one-rung-off slab over the band. Exactly the reason ArtistPage's own
                    // ScrollView already opts out. The band's clip + its feather is the "more content" cue here.
                    EdgeCues = ScrollEdgeCues.None,
                    ItemClipTopInset = stickyInset,
                    ItemClipTopFadeBand = DetailVerticalLayout.StickyFadeBand,
                    OnScrollGeometryChanged = SwipeCloseObserver(),
                },
                Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                Insertion = Insertion(),
                Entrance = new EntranceOptions
                {
                    StaggerColdRealize = staggerCold,
                    ItemFlipFrom = FlipFrom,
                    ItemFadeFrom = FadeFrom,
                },
            });
    }

    // ── §4.6 — the choreography pass. Runs INSIDE the render that commits the new order (the ItemsView child renders
    // after this and reads the bumped displacement version in the same pass), so the anchor adjust + the FLIP/fade seeds
    // land with the SAME frame — never a jump-then-animate flash. Pure item transitions: a removed row's slot rebinds to
    // the next track and every row below FLIP-glides up to reclaim the space; an added row's neighbors part downward and
    // the row fades in at its slot. Everything is bounded: the engine's displacement seed walks only the REALIZED window.
    void Choreograph(Track[] old, Track[] next, float rowH)
    {
        var delta = MembershipDiff.Diff(old, next);
        if (delta.IsEmpty) return;

        if (delta.IsReset)
        {
            // Curated re-cut (Discover-Weekly style): ONE deliberate crossfade — the keyed remount replays the slots'
            // mount entrance and the fresh scroll state starts at top — never a 40-row animation storm.
            _flip.Clear(); _fade.Clear();
            _resetEpoch++;   // plain render-local identity write — the remount lands in this pass without scheduling it again
            return;
        }

        float offset = _listCtl.ScrollOffset;
        int firstVis = Math.Max(0, (int)(offset / rowH));

        // (1) Anchor: the first visible SURVIVING row keeps its screen Y — adjust the offset by its index shift so an
        // add/remove ABOVE the viewport never yanks the content (the single most jarring live-list failure mode).
        var oldKeys = MembershipDiff.Keys(old);
        var newKeys = MembershipDiff.Keys(next);
        var newIdxByKey = new Dictionary<string, int>(newKeys.Length, StringComparer.Ordinal);
        for (int i = 0; i < newKeys.Length; i++) newIdxByKey[newKeys[i]] = i;
        int shift = 0;
        for (int i = Math.Clamp(firstVis, 0, Math.Max(0, oldKeys.Length - 1)); i < oldKeys.Length; i++)
            if (newIdxByKey.TryGetValue(oldKeys[i], out int ni)) { shift = ni - i; break; }
        if (shift != 0) _listCtl.ScrollBy(shift * rowH);

        // (2) FLIP residuals for EVERY survivor — an unmoved row is still screen-displaced when the anchor shifted
        // ((o−n+shift)·rowH; the anchor row itself resolves to 0) — and fade/slide-in for adds (staggered, capped at 8;
        // the delay HOLDS the from-value, so a staggered add is invisible until its turn — no pop).
        _flip.Clear(); _fade.Clear();
        var oldIdxByKey = new Dictionary<string, int>(oldKeys.Length, StringComparer.Ordinal);
        for (int i = 0; i < oldKeys.Length; i++) oldIdxByKey[oldKeys[i]] = i;
        for (int n = 0; n < newKeys.Length; n++)
            if (oldIdxByKey.TryGetValue(newKeys[n], out int o))
            {
                float resid = (o - n + shift) * rowH;
                if (MathF.Abs(resid) > 0.5f) _flip[n] = (0f, resid);
            }
        int addOrd = 0;
        foreach (var a in delta.Adds)
        {
            int n = a.NewIndex!.Value;
            _flip[n] = (0f, -6f);                                      // ease in from a slight rise…
            _fade[n] = (0f, Math.Min(addOrd++, 8) * 20f);              // …with a 20ms/row stagger, capped (no cascades)
        }

        _dispVer.Value = _dispVer.Peek() + 1;   // the ItemsView (a child, renders after this) seeds in the SAME frame
    }

    // ── Breakpoint re-deal ────────────────────────────────────────────────────────────────────────────────────────────
    // Rides the SAME seed channel the membership choreography uses (ItemsView.ItemFlipFrom / ItemFadeFrom consumed by a
    // _dispVer bump): per-row Opacity from→1 and TranslateY from→0, applied to the ALREADY-REALIZED slots. No remount, no
    // orphans, no per-cell exit tracks — purely compositor channels, so the animation ticks cost no layout and no record.
    //
    // The delay HOLDS the from-value, so a staggered row is invisible until its turn: the list reads as dealt top-down
    // rather than as 25 rows blinking together. Capped so a tall viewport never trails a long cascade behind the rail's
    // own 300ms slide, and skipped entirely on a rapid reversal (during a toggle storm there is no coherent gesture to
    // narrate, and stacking a fresh set of delayed tracks per toggle is pure waste).
    void ReDeal(int tier, float rowH)
    {
        if (_lastDealtTier == tier) return;
        int prev = _lastDealtTier;
        _lastDealtTier = tier;
        long now = Environment.TickCount64;
        bool reversal = now - _lastDealtAtMs < ReDealReversalMs;
        _lastDealtAtMs = now;
        if (_dealtThisFrame) return;   // a membership choreography already populated the seeds for this frame — leave them
        if (prev < 0 || reversal || Motion.ReducedMotion || rowH <= 0f)
        {
            // Not narrating this cross. Drop any seeds a PREVIOUS deal left behind so they cannot be replayed later by
            // an unrelated _dispVer bump (a live add/remove) as a phantom fade on rows that never changed.
            _flip.Clear(); _fade.Clear();
            return;
        }

        bool narrowing = tier > prev;   // higher tier = fewer columns
        int cap = narrowing ? 6 : 4;    // exits stay quicker than enters (the app's 0.5-0.7x vocabulary)
        float step = narrowing ? 24f : 16f;

        _flip.Clear(); _fade.Clear();
        int visible = _visibleCount.Peek();
        int firstVis = Math.Max(0, (int)(_listCtl.ScrollOffset / rowH));
        int last = Math.Min(visible, firstVis + ReDealRows);
        for (int i = firstVis; i < last; i++)
        {
            int ord = i - firstVis;
            _flip[i] = (0f, 6f);                                  // rise into place…
            _fade[i] = (0f, Math.Min(ord, cap) * step);           // …behind a capped top-down stagger
        }
        _dispVer.Value = _dispVer.Peek() + 1;   // the ItemsView (a child, renders after this) seeds in the SAME frame
    }

    float VerticalHeaderHeight(bool subscribe = false)
    {
        float h = subscribe ? _verticalHeaderHeight.Value : _verticalHeaderHeight.Peek();
        if (h > 1f) return h;
        // Pre-measure fallback — the SAME pure sum the loading skeleton reserves (DetailSkeleton.VerticalHeroBand), so
        // the band the shimmer holds open and the band this hero's collapse binds assume are one number. It replaced a
        // pair of hand-picked constants (420 stacked / 320 row flow) that were a function of nothing: at 400 DIP the
        // artwork alone is 280 and the padding another 32, which left ~100 DIP for the entire identity column plus the
        // toolbar row. See DetailVerticalLayout.HeroBandHeight.
        return DetailVerticalLayout.HeroBandHeight(_verticalHeroW.Peek(), _verticalHeroRowFlow,
            HeroHasEyebrow(), HeroHasAttribution(), HeroHasMeta(), HeroHasDescription(), HeroHasPulse());
    }

    // ── the hero's own emit predicates, shared by the hero and its skeleton ───────────────────────────────────────
    // Read exactly what DetailVerticalHero.Build branches on, so the reserved band contains the blocks the hero will
    // actually compose for THIS model (an album's eyebrow, a playlist's owner row, a release blurb) and no others.
    bool HeroHasEyebrow() => DetailRail.EyebrowText(_model, _cfg).Length > 0;

    bool HeroHasAttribution()
        => DetailRail.ShowCollaborators(_model) || _model.OwnerName is { Length: > 0 } || _model.Artists.Count > 0;

    bool HeroHasMeta() => _model.MetaLine is { Length: > 0 };

    bool HeroHasDescription()
        => HeroEditable() || _model.Description is { Length: > 0 };

    bool HeroHasPulse() => _model.ExpiresAtMs > 0;   // the daylist rollover countdown row

    bool HeroEditable() => PlaylistInlineEdit.EditableMetadata(_model) && _model.ContextUri is { Length: > 0 };

    (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) SwipeCloseObserver()
        => (ProjectScrollState, ApplyScrollState);

    long ProjectScrollState(ScrollGeometry g)
    {
        uint swipe = _swipeGroup.AnyOpen ? unchecked((uint)BitConverter.SingleToInt32Bits(g.OffsetY)) : 0u;
        return swipe;
    }

    void ApplyScrollState(ScrollGeometry g)
    {
        if (_swipeGroup.AnyOpen) _swipeGroup.Close();
    }

    void ApplyVerticalItemBand(float stickyInset)
    {
        if (!_verticalHeader || _cfg.HasTrailing || Context.Scene is not { } scene) return;
        var viewport = _listCtl.Viewport;
        if (viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return;
        ref ScrollState sc = ref scene.ScrollRef(viewport);
        if (MathF.Abs(sc.ItemClipTopInset - stickyInset) <= 0.01f
            && MathF.Abs(sc.ItemClipTopFadeBand - DetailVerticalLayout.StickyFadeBand) <= 0.01f)
            return;
        sc.ItemClipTopInset = stickyInset;
        sc.ItemClipTopFadeBand = DetailVerticalLayout.StickyFadeBand;
        scene.Mark(viewport, NodeFlags.PaintDirty);
        if (!sc.ContentNode.IsNull && scene.IsLive(sc.ContentNode))
            scene.Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    void MeasureVerticalHeader(RectF r)
    {
        if (r.H <= 1f) return;
        if (MathF.Abs(_verticalHeaderHeight.Peek() - r.H) <= 1f) return;
        _verticalHeaderHeight.Value = r.H;
        // …and up to the page, whose art-derived tone plane sizes its blurred background extension (and, in hero-only
        // mode, its fade back to neutral) to exactly this band. Value-gated by the 1-DIP test above, so a settling
        // hero writes it once rather than once per layout pass.
        if (_verticalHeroHeightOut is not null) _verticalHeroHeightOut.Value = r.H;
    }

    Element VerticalHero()
    {
        // This method runs from VerticalHeroRoot (TrackList.Render). Palette hydration + width subscribe here so the
        // hero updates when the wash lands / the right pane resizes. Its measured height now lives outside the
        // ItemsView identity, so a settle cannot remount every realized track row.
        var h = _liveHandlers?.Value ?? _h;
        float availW = _verticalHeroW.Value;
        bool rowFlow = _verticalHeroRowFlow;
        float expandedHeight = VerticalHeaderHeight(subscribe: true);
        float collapseDistance = DetailVerticalLayout.CollapseDistance(expandedHeight);
        int tier = ClampTier(_tier.Value);
        float compactLeft = TrackRow.PadXFor(tier);
        bool toolbarLabeled = tier <= 1;
        Element toolbar = Toolbar(toolbarLabeled, tier);
        // ONE capability scan per hero render, shared by the field and the band's Filter action — the scan walks every
        // track in the model, and a 10k playlist cannot afford to walk it twice for two views of the same facts.
        var filterCaps = FilterCapabilities(_full.Value.Value);
        Element compactSearch = CompactSearch(availW, compactLeft, filterCaps);
        Element compactActions = CompactBandActions(filterCaps);
        Element compactSelection = CompactSelectionToolbar();
        Element header = new BoxEl
        {
            Key = "vhero:header",
            Direction = 1,
            OnBoundsChanged = MeasureVerticalHeader,
            Children = [DetailVerticalHero.Build(_model, _cfg, h, _full, rowFlow, availW, _verticalHeroFlowInitialized,
                compactLeft, collapseDistance, _verticalCompactInteractive,
                _searchExpanded, _selectionCommandsVisible!,
                toolbar, compactSearch, compactActions, compactSelection, _acts)],
        };
        return new BoxEl
        {
            Key = "vitem:hero", Direction = 1,
            Children = [header],
        };
    }

    // ── the page FOOTER (hero system only) ─────────────────────────────────────────────────────────────────────────
    // The metadata-facts bento — "This week" + the 12-week strip, the tempo curve, the top-artists ranking — as the
    // LAST slot of the virtual viewport, i.e. under the last track. Identical panel, identical cards, identical
    // honesty gate: the ONLY thing this method changes about them is the address. It is here because the hero's
    // identity column is the page's opening and three stacked analytics cards there push the tracks below the fold;
    // the two-column arm keeps them in the rail (DetailRail `rail:likedfacts`), where they cost the tracks nothing.
    //
    // WHY A LIST SLOT and not a box wrapped around the list: this viewport is the page's only scroller (Grow=1), so
    // anything composed as a SIBLING of it would dock to the window bottom instead of following the rows. As an item
    // it scrolls with them, realizes only when it is scrolled to, and stays outside the insertion Range (which is
    // (TrackStart, View().Length)) so a drag can never aim a drop at it.
    Element VerticalFactsFooter(int tier)
    {
        // Subscribe through the SAME snapshot the rows read: hydration hands the page a new track list without
        // necessarily changing the row COUNT, and this slot's index would not move for it, so without this read the
        // footer would keep the props it mounted with. The snapshot is a record struct — equality-gated, so an
        // unchanged model costs no re-render. (LikedFactsPanel coalesces an equal list on top of that.)
        var snap = _rowsSnapshot!.Value;
        // Aligned to the TRACK ROWS, not to the hero: the footer's neighbour above is the last row, and the eye reads
        // the shared left edge. Same padX the pinned chrome/column header uses (TrackList.Chrome).
        float padX = TrackRow.PadXFor(tier);
        return new BoxEl
        {
            Key = "vitem:facts",
            Direction = 1,
            // The page's own rhythm (Spacing.XXL == the hero's HeroPad grid): a full block of air off the last row so
            // the cards read as a separate closing section, and the same again below so the list does not end flush
            // against the docked player bar. No PlayerDock.Reserve here — the shell's content region is a flex
            // SIBLING of the bar and is clipped to its top edge (WaveeShell), so the dock never overlaps this page and
            // a 72-DIP reserve would only be dead space at the end of every playlist.
            Padding = new Edges4(padX, Spacing.XXL, padX, Spacing.XXL),
            Children = [LikedFacts.Panel(snap.Model, snap.Handlers, outerPadding: false)],
        };
    }

    Element VerticalHeroRoot(float expandedHeight, float collapseDistance) => new BoxEl
    {
        Key = "vertical:hero-root", Direction = 1, ClipToBounds = true,
        Children = [VerticalHero()],
    }.Collapse(expandedHeight, DetailVerticalLayout.CompactIdentityHeight, collapseDistance);

    Element VerticalChromeRoot(Element chrome) => new BoxEl
    {
        Key = "vertical:chrome-root", Direction = 1,
        Children = [chrome],
    }.Sticky(DetailVerticalLayout.CompactIdentityHeight, pinned => _verticalCompactInteractive.Value = pinned);

    // Album/single AND playlist/liked vertical: hero + chrome are direct children of the OUTER scroll content, so
    // their binds resolve this scroller. Everything after chrome shares ONE sticky clip owner (never per-row clips —
    // those were O(realized window) PaintDirty/frame under the stuck bar).
    Element TrailingBody(Element listKeyed, Element? verticalHero, Element? verticalChrome, float stickyInset,
                         bool includeAlbumTrailing = true)
    {
        var bodyChildren = new List<Element>(3) { listKeyed };
        if (includeAlbumTrailing)
        {
            Element trailing = Embed.Comp(() => new AlbumTrailing(_full, _route, _h));
            if (_verticalHeader && DetailRail.PreReleaseCard(_model, _h) is { } countdown)
                bodyChildren.Add(countdown);
            if (_verticalHeader && _cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(_model))
                bodyChildren.Add(AlbumTrailing.ReleasePanel(_model, _h));
            bodyChildren.Add(trailing);
        }

        Element body = new BoxEl
        {
            Direction = 1,
            EdgeFade = _verticalHeader && _verticalBodyClipEngaged.Value
                ? new EdgeFadeSpec(EdgeMask.Top, DetailVerticalLayout.StickyFadeBand)
                : null,
            ScrollBinds = _verticalHeader
                ? [new() { ClipTopAtViewport = stickyInset,
                    OnFlag = engaged => _verticalBodyClipEngaged.Value = engaged }]
                : [],
            Children = bodyChildren.ToArray(),
        };
        Element[] children = _verticalHeader && verticalHero is not null && verticalChrome is not null
            ? [verticalHero, verticalChrome, body]
            : [body];

        return ScrollView(new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children = children,
        }) with
        {
            Grow = 1f,
            // Same opt-out, same reason as the virtual path's (see VerticalList): the surface-colour top cue would
            // paint an opaque, ancestor-resolved slab over the unpainted context band. Only the vertical/hero system
            // has a band, so the two-column trailing scroller keeps the stock cue.
            EdgeCues = _verticalHeader ? ScrollEdgeCues.None : ScrollEdgeCues.Auto,
            OnScrollGeometryChanged = SwipeCloseObserver(),
        };
    }

    // ── chrome (fixed) ───────────────────────────────────────────────────────────────────────────────────
    // The track-list command bar: ONE left-aligned WinUI CommandBar above the column header, grouped by a vertical
    // separator — [Play · Shuffle] | [Sort · Row size] — with a "Find" search field docked alone on the RIGHT. The
    // search field carries the filter FUNNEL as its trailing affix (advanced filter folded into search — hide explicit /
    // videos only, a light checkable menu), so there's no separate Filter button. Every command shows icon + label at
    // wide tiers and collapses to icon-only at a narrow list (tier ≥ 4, < 440px) — the DefaultLabelPosition=Right +
    // dynamic-overflow behavior, in Wavee's own 32px pill styling. The rail owns Add-to-queue / Copy-to-playlist, so the
    // bar doesn't carry them. Keyed by the labeled state so a tier cross rebuilds cleanly. (Composed from ToolFx, not the
    // CommandBar control, which only does the classic labels-on-open mode.)
    Element Chrome(ColumnSet set, TrackSize[] tracks, DetailTrackSort sort, bool labeled, int tier, bool checkInset,
                   float padX = PadX, float? padRight = null, Element? contentFilterBar = null,
                   Element? lensHeader = null)
    {
        Element header = Header(set, tracks, sort, checkInset);
        Element[] chromeChildren;
        if (_verticalHeader)
        {
            // The vertical hero owns the toolbar, but the chip bar still belongs to the LIST — it changes what the
            // rows below contain. Without this the Liked content-filter bar was unreachable in the vertical/hero
            // layout (and with DetailPageLayout=Hero, unreachable at every width) while its fetch still ran.
            var verticalStack = new List<Element>(3);
            if (contentFilterBar is { } verticalChips) verticalStack.Add(verticalChips);
            if (lensHeader is { } verticalLens) verticalStack.Add(verticalLens);
            verticalStack.Add(header);
            chromeChildren = verticalStack.ToArray();
        }
        else if (_showToolbar)
        {
            // One surface owns both action commands and the sortable column projection (same row-grid tracks as
            // TrackRow). Chromeless — no fill/border — so the bar floats on the page backdrop.
            // The content-filter chips sit BETWEEN the command toolbar and the column header: they change what the
            // header's rows contain, so they must read as belonging to the list rather than to the page chrome.
            // The lens header sits BELOW the chips and directly above the column header, because it does not offer a
            // choice — it states what the rows already are, which is the column header's own register.
            var stack = new List<Element>(4) { Toolbar(labeled, tier) };
            if (contentFilterBar is { } chipBar) stack.Add(chipBar);
            if (lensHeader is { } lens) stack.Add(lens);
            stack.Add(header);
            chromeChildren =
            [
                new BoxEl
                {
                    Direction = 1,
                    MinWidth = 0f,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
                    Children = stack.ToArray(),
                },
            ];
        }
        else
        {
            chromeChildren = lensHeader is { } bareLens ? [bareLens, header] : [header];
        }

        Element content = new BoxEl
        {
            Key = "chrome", Direction = 1,
            Padding = new Edges4(padX, _verticalHeader ? 0f : Spacing.S, padRight ?? padX, 0f),
            Children = chromeChildren,
        };

        // THE MERGED BAND'S LOWER STRATUM. This node pins at exactly the identity row's height, so once both are stuck
        // the page shows ONE band: identity row (56) + this column row, with this row's own bottom divider (see
        // Header) as the band's single hairline. No drop shadow and — now — no FILL either: the band is an unpainted
        // omission and the rows are clipped at its lower edge instead of sliding under it (the OFFSET model, see
        // ContextBand). The clip is DetailVerticalLayout.StickyClipInset, which is exactly 56 + this row + the
        // hairline, so this stratum and the cut are the same line by construction.
        //
        // What went with the fill: a bound `pinned ? ContextBand.Fill : Transparent` brush. It was a compositor write
        // rather than a re-render, which was the right shape for the wrong idea — the colour it wrote was an opaque
        // approximation of a live-Mica surface, and on a dark wallpaper it read as a black slab.
        return content;
    }

    Element Toolbar(bool labeled, int tier) =>
        Responsive.Of(BuildToolbar, fallback: _lastRightW > 0f ? _lastRightW : 760f)
            with { Key = "detail-track-commandbar" };

    Element BuildToolbar(float available)
    {
        _ = _toolbarMetricsEpoch.Value; // measured labeled widths refine the conservative first-frame budgets
        var h = _liveHandlers?.Value ?? _initialH;
        var model = _full.Value.Value;
        var cfg = DetailPage.ResolveConfig(DetailPage.ParseDetail(_route.Value).Kind, model);
        bool selectionMode = _selectionCommandsVisible?.Value == true;
        int selectionTrackStart = TrackStart;
        if (selectionMode)
        {
            Element selection = Embed.Comp(() => new SelectionCommandBar(
                _selection, i => DisplayTrack(i, selectionTrackStart), ExitSelection, host: HostInfo));
            return CommandBarSurface("selection", selection);
        }

        var tuningSource = _svc?.PlaylistTuning;
        bool showTune = PlaylistTuneMenuModel.IsEligible(model.Tuning, tuningSource?.Value is not null);
        bool hasSelect = h.MultiSelect is not null && h.SetMultiSelect is not null;
        bool explicitSearch = _searchExpanded.Value;

        var widths = new DetailTrackCommandWidths(
            _toolbarWidths[0], _toolbarWidths[1], _toolbarWidths[2],
            _toolbarWidths[3], _toolbarWidths[4], _toolbarWidths[5]);
        float pane = MathF.Max(0f, available - 12f);
        var fit = DetailTrackCommandBarLayout.Resolve(
            pane, in widths, _verticalHeader, showTune, hasSelect, explicitSearch, _toolbarFit);
        // FREEZE the fit for as long as search is open. Hysteresis alone is not enough: measuring an evicted command
        // bumps _toolbarMetricsEpoch, which re-renders and re-resolves, which hands SizeMode.Reflow a moving target.
        // One latch, held until the pane genuinely resizes or search closes, gives the width tween a single destination.
        if (!explicitSearch) _searchOpenFit = null;
        else if (_searchOpenFit is { } latched && MathF.Abs(latched.Available - pane) <= 0.5f) fit = latched.Fit;
        else _searchOpenFit = (pane, fit);
        _toolbarFit = fit;

        var kids = new List<Element>(9);
        if (!_verticalHeader)
        {
            IReadOnlyList<MenuFlyoutItem> playItems =
            [
                new(Loc.Get(Strings.Detail.AddToQueue),
                    new IconRef { Glyph = WaveeIcons.PlayAfter, Font = WaveeIcons.Font },
                    Invoke: h.AddToQueue),
            ];
            Element playNextContent = new BoxEl
            {
                Direction = 0,
                Gap = 6f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(WaveeIcons.PlayNext)
                    {
                        Size = 14f,
                        FontFamily = WaveeIcons.Font,
                        // SplitButton owns this content after mount, so a literal token would freeze across a live
                        // theme flip. Keep the ink on a binding just like the theme-live page surface behind it.
                        Color = Prop.Of(static () => Tok.TextSecondary),
                    },
                    new TextEl(Loc.Get(Strings.Detail.PlayNext))
                    {
                        Size = 12f,
                        Weight = 600,
                        Color = Prop.Of(static () => Tok.TextSecondary),
                    },
                ],
            };
            kids.Add(MeasuredCommand(0, "cmd:play-next:" + (model.ContextUri ?? ""),
                SplitButton.Create(playNextContent, h.PlayNext, playItems,
                    parts: ToolFx.CommandBarSplitParts)));
        }
        if (fit.Has(DetailTrackInlineCommand.Shuffle))
            kids.Add(MeasuredCommand(2, "cmd:shuffle",
                ToolFx.LabeledButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), false, h.Shuffle, NoAnchor)));
        if (showTune)
            kids.Add(MeasuredCommand(1, "cmd:tune",
                Embed.Comp(() => new PlaylistTuneButton(_full, tuningSource!, labeled: true))));

        bool hasViewInline = fit.Has(DetailTrackInlineCommand.Sort)
            || fit.Has(DetailTrackInlineCommand.Density)
            || fit.Has(DetailTrackInlineCommand.Select);
        if (hasViewInline && kids.Count > 0) kids.Add(ToolFx.Separator() with { Key = "cmd:separator" });

        if (fit.Has(DetailTrackInlineCommand.Sort))
            kids.Add(MeasuredCommand(3, "cmd:sort",
                Embed.Comp(() => new SortMenuButton(h.Sort, h.SetSort, cfg.ShowAlbumColumn, model.HasDateAdded, labeled: true,
                    hasPlays: () => cfg.ShowPlays || (cfg.PlaysColumnOptIn && (h.PlaysColumn?.Peek() ?? false))))));
        if (fit.Has(DetailTrackInlineCommand.Density))
            kids.Add(MeasuredCommand(4, "cmd:density",
                Embed.Comp(() => new ListButton(h.Density, h.SetDensity, labeled: true))));
        if (fit.Has(DetailTrackInlineCommand.Select) && h.MultiSelect is not null && h.SetMultiSelect is not null)
            kids.Add(MeasuredCommand(5, "cmd:select",
                Embed.Comp(() => new MultiSelectButton(h.MultiSelect, h.SetMultiSelect, _selection, labeled: true))));

        PlaylistInlineForOverflow(fit, hasSelect, out var overflow);
        kids.Add(Embed.Comp(() => new DetailTrackMoreButton(
            _full, h, cfg, overflow, _selection, _verticalHeader))
            with { Key = "cmd:more:" + (int)overflow + ":" + (model.ContextUri ?? "") });

        var caps = FilterCapabilities(model);
        Element search = SearchHost(h, caps, fit.SearchExpanded, fit.SearchWidth, compact: false);
        Element normal = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = DetailTrackCommandBarLayout.Gap,
            Grow = 1f, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = DetailTrackCommandBarLayout.Gap,
                    Shrink = 0f, Children = kids.ToArray(),
                },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                search,
            ],
        };
        return CommandBarSurface("normal", normal);
    }

    Element CommandBarSurface(string mode, Element content)
    {
        // Chromeless lane (browsing + selection): no fill/border — floats on the page backdrop. Mode swap still
        // animates via the keyed child.
        return new BoxEl
        {
            Direction = 1,
            Height = DetailVerticalLayout.ToolbarRowHeight,
            MinWidth = 0f,
            Padding = new Edges4(DetailVerticalLayout.ToolbarSurfacePadX, DetailVerticalLayout.ToolbarSurfacePadY,
                DetailVerticalLayout.ToolbarSurfacePadX, DetailVerticalLayout.ToolbarSurfacePadY),
            ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = "commandbar-mode:" + mode,
                    Direction = 1,
                    Grow = 1f,
                    MinWidth = 0f,
                    Animate = ToolbarModeMotion,
                    Children = [content],
                },
            ],
        };
    }

    void ExitSelection()
    {
        _selection.ClearSelection();
        var h = _liveHandlers?.Peek() ?? _initialH;
        if (h.MultiSelect?.Peek() == true) h.SetMultiSelect?.Invoke(false);
    }

    static void PlaylistInlineForOverflow(DetailTrackCommandBarFit fit, bool hasSelect,
                                          out DetailTrackInlineCommand overflow)
    {
        overflow = DetailTrackInlineCommand.Shuffle | DetailTrackInlineCommand.Sort | DetailTrackInlineCommand.Density;
        if (hasSelect) overflow |= DetailTrackInlineCommand.Select;
        overflow &= ~fit.Inline;
    }

    Element MeasuredCommand(int slot, string key, Element command) => new BoxEl
    {
        Key = key, Direction = 1, Shrink = 0f, Animate = ToolbarCommandMotion,
        OnBoundsChanged = r => MeasureToolbarCommand(slot, r.W),
        Children = [command],
    };

    void MeasureToolbarCommand(int slot, float width)
    {
        if ((uint)slot >= (uint)_toolbarWidths.Length || width <= 1f) return;
        if (MathF.Abs(_toolbarWidths[slot] - width) <= 0.5f) return;
        _toolbarWidths[slot] = width;
        _toolbarMetricsEpoch.Value = _toolbarMetricsEpoch.Peek() + 1;
    }

    Element CompactSearch(float availW, float compactLeft, TrackFilterCapabilities caps)
    {
        var h = _liveHandlers?.Value ?? _initialH;
        bool expanded = _searchExpanded.Value;
        return SearchHost(h, caps, expanded,
            expanded ? CompactSearchWidth(availW, compactLeft) : DetailTrackCommandBarLayout.SearchIconWidth,
            compact: true);
    }

    /// <summary>The context band's RIGHT cluster: Find · Filter · Play, as plateless text actions (see
    /// <see cref="WaveeCta.TextAction"/> and its fence). These are the SAME three affordances the band's three
    /// deleted floating objects carried, on the same handlers — the search glyph became a word, the filter funnel
    /// (which only existed as an affix INSIDE the expanded field, so it was unreachable from the collapsed bar at
    /// all) became a word beside it, and the accent circle FAB became the band's one accent word.
    ///
    /// <para>Find keeps its node capture: collapsing the field restores focus to whatever opened it, and dropping the
    /// icon button would otherwise have dropped that focus target on the floor.</para></summary>
    Element CompactBandActions(TrackFilterCapabilities caps)
    {
        var h = _liveHandlers?.Value ?? _initialH;
        void ToggleFind()
        {
            if (_searchExpanded.Peek()) CollapseSearch(restoreFocus: false);
            else _searchExpanded.Value = true;
        }
        return new BoxEl
        {
            Direction = 0, Gap = ContextBandLayout.ActionGap, Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                WaveeCta.TextAction(Loc.Get(Strings.Detail.Filter.Find), ToggleFind) with
                {
                    Key = "band:find",
                    OnRealized = CaptureSearchButton,
                },
                Embed.Comp(new FilterButtonProps(caps, TextAction: true),
                    () => new FilterButton(h.Filters, h.SetFilters)) with { Key = "band:filter" },
                WaveeCta.TextAction(Loc.Get(Strings.Detail.Play), h.PlayAll, primary: true) with { Key = "band:play" },
            ],
        };
    }

    void CaptureSearchButton(NodeHandle node)
    {
        _searchButtonNode = node;
        if (!_restoreSearchFocus) return;
        _restoreSearchFocus = false;
        _post?.Invoke(() => InputHooks.Current.Default.FocusNode?.Invoke(node, true));
    }

    /// <summary>The expanded field's width in the context band. Derived, not the old hardcoded 196: the band is
    /// <c>availW</c> wide with <c>compactLeft</c> padding either side, and the right cluster is the three text
    /// actions plus one cluster gap in front of them. The identity block is unmounted while search is open, so all of
    /// that room is genuinely the field's.</summary>
    float CompactSearchWidth(float availW, float compactLeft)
    {
        Span<float> actions =
        [
            ContextBandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.Filter.Find), ContextBandLayout.ActionPadX),
            ContextBandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.Filter.Short), ContextBandLayout.ActionPadX),
            ContextBandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.Play), ContextBandLayout.ActionPadX),
        ];
        float room = availW - compactLeft * 2f - ContextBandLayout.ActionsWidth(actions)
                     - ContextBandLayout.ClusterGap;
        return MathF.Max(DetailTrackCommandBarLayout.SearchIconWidth,
                         MathF.Min(room, DetailTrackCommandBarLayout.SearchMax));
    }

    async Task LoadContentFilterChipsAsync(Services svc, Action<Action> post, CancellationToken ct)
    {
        IReadOnlyList<ContentFilterChip> chips;
        // The service is documented never to throw and to return an empty list when the endpoint is unusable; this
        // catch is the belt to that braces, so a chip bar can never take down a mounted track list.
        try { chips = await svc.ContentFilters.GetLikedChipsAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { return; }
        if (ct.IsCancellationRequested) return;
        // An empty result is published, not swallowed. The service already falls back to its own cache before it
        // returns empty, so empty means "the curated set is genuinely unusable now" — keeping the previous set on
        // screen would leave a stale bar for the rest of the session, whereas publishing it hands the bar to the
        // descriptor-derived fallback, which is what that fallback is for.
        post(() => _serverChips.Value = chips);
    }

    /// <summary>Liked Songs' descriptor chips. Null everywhere else, and null on Liked until enough tracks carry
    /// kind-6 tags — the bar appears as enrichment lands rather than reserving an empty strip.</summary>
    Element? ContentFilterBar()
    {
        var model = _full.Value.Value;
        if (!LikedSongsArtwork.IsLikedUri(model.ContextUri)) return null;

        // Spotify's curated set is authoritative when it is available; descriptor-derived chips are the documented
        // fallback for offline / a 404 account / a shape change, never the primary. Both paths carry an evidence
        // split, so a chip that would match zero rows renders unavailable rather than filtering the list to empty.
        var chips = _chipCache.For(model.Tracks, _serverChips.Value);
        if (chips.Count == 0) return null;

        var filters = _h.Filters.Value;   // subscribe: the selected chip is filter state, so the bar re-renders with it
        // AllChip, not AllTracks: this is a chip sitting next to "Mellow" and "K-Pop", where the shorter word reads as
        // one of the set. AllTracks stays reserved for the filter flyout, which uses it as a status SENTENCE.
        return ContentFilterChips.Build(chips, filters.Tag,
            tag => _h.SetFilters(_h.Filters.Peek() with { Tag = tag }),
            Loc.Get(Strings.Detail.Filter.AllChip),
            scrollKey: "contentfilter:" + _route.Value.Name);
    }

    /// <summary>The Liked Songs LENS header — the row that says which rail fact is currently filtering the list, with a
    /// per-facet clear. Null everywhere else and null whenever no lens is on, exactly like <see cref="ContentFilterBar"/>
    /// above; the wording and the elements are <c>LikedLens.Header</c>'s (one file owns both halves of the feature).
    ///
    /// <para>The count is <c>View().Length</c> — the visible rows, read through the cached view rather than through
    /// <c>_visibleCount</c>. The signal is written in a LAYOUT EFFECT, i.e. after this render, so reading it here would
    /// print the previous filter's count for one frame; <c>View()</c> peeks its snapshot and is already exact.</para></summary>
    Element? LensHeader()
    {
        var model = _full.Value.Value;
        if (!LikedSongsArtwork.IsLikedUri(model.ContextUri) && _cfg.Badges != BadgeStyle.OwnerRow) return null;
        var filters = _h.Filters.Value;   // subscribe: the header IS the filter state, so it re-renders with it
        return LikedLens.Header(in filters, View().Length, _h, CultureInfo.CurrentCulture,
            LikedSongsArtwork.IsLikedUri(model.ContextUri));
    }

    Element CompactSelectionToolbar()
    {
        int trackStart = TrackStart;
        return Responsive.Of(_ =>
        {
            Element selection = Embed.Comp(() => new SelectionCommandBar(
                _selection, i => DisplayTrack(i, trackStart), ExitSelection, host: HostInfo));
            return CommandBarSurface("compact-selection", selection);
        }, fallback: DetailVerticalLayout.FallbackW);
    }

    Element SearchHost(DetailHandlers h, TrackFilterCapabilities caps, bool expanded, float width, bool compact)
    {
        bool queryActive = h.Query.Value.Length > 0;
        bool focused = expanded && _searchFocused.Value;
        // Both states are keyed LAYERS of the query region's ZStack, each with the same cross-fade spec: the outgoing
        // one stays mounted and fades while the incoming one fades up, so neither ever pops. Distinct keys are what
        // makes the reconciler run the Enter/Exit legs rather than morphing one into the other.
        Element query;
        if (expanded)
        {
            query = new BoxEl
            {
                Key = "search:field",
                Direction = 1,
                Height = 32f,
                Animate = SearchSwapMotion,
                Children =
                [
                    Embed.Comp(() => new DetailTrackSearchField(
                        h.Query, _searchFocused, focusOnMount: true, canCollapse: true, CollapseSearch)),
                ],
            };
        }
        else
        {
            void Open() => _searchExpanded.Value = true;
            void Capture(NodeHandle node)
            {
                _searchButtonNode = node;
                if (!_restoreSearchFocus) return;
                _restoreSearchFocus = false;
                _post?.Invoke(() => InputHooks.Current.Default.FocusNode?.Invoke(node, true));
            }
            query = new BoxEl
            {
                Key = "search:icon",
                Direction = 1,
                Width = 32f,
                Height = 32f,
                JustifySelf = FlexAlign.Start,
                Animate = SearchSwapMotion,
                Children =
                [
                    ToolTip.Wrap(
                        ToolFx.Button(Icons.Search, queryActive, Open, Capture),
                        Loc.Get(Strings.Detail.Filter.SearchThisList)),
                ],
            };
        }

        // The row carries NO explicit width: it fills the host, whose width is what the reflow tween is animating. That
        // is what keeps the funnel pinned to the right edge for the whole flight — an explicitly-final-width row would
        // hang past the narrower host and the button would be clipped, then snap into place at the end.
        float gap = expanded ? 0f : DetailTrackCommandBarLayout.Gap;
        var row = new BoxEl
        {
            Direction = 0,
            Gap = gap,
            Height = 32f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                // Grow, not a computed width: the query region tracks the host's INTERPOLATED width every tick, so the
                // field is always laid out at the width it is actually being shown at. (Sizing it to the final width
                // inside a narrower clip is what produced the half-drawn placeholder mid-expand.)
                new BoxEl
                {
                    Key = "search-query-region",
                    ZStack = true,
                    Grow = 1f,
                    MinWidth = 0f,
                    Height = 32f,
                    ClipToBounds = true,
                    Children = [query],
                },
                // STABLE key + live props: capabilities are re-pushed, not keyed. Keying on them remounted the button
                // whenever enrichment landed, which dropped its anchor/overlay refs and orphaned an open flyout.
                Embed.Comp(new FilterButtonProps(caps), () => new FilterButton(h.Filters, h.SetFilters))
                    with { Key = "search-filter" },
            ],
        };
        Element[] layers = focused
            ?
            [
                row,
                new BoxEl
                {
                    Key = "search-underline",
                    Height = 2f,
                    AlignSelf = FlexAlign.End,
                    Fill = Tok.AccentDefault,
                    HitTestVisible = false,
                    Animate = SearchUnderlineMotion,
                },
            ]
            : [row];

        return new BoxEl
        {
            Key = compact ? "compact-search-host" : "search-host",
            ZStack = true,
            Width = width,
            Height = 32f,
            Shrink = 0f,
            // The CHROME travels with the width instead of snapping at the ends. Corners and the border are mounted at
            // ALL times — invisible while the fill and the border colour are transparent — because neither the corner
            // radius nor the border WIDTH is an animatable channel, so toggling them pops. Only the colours change, and
            // BrushTransitionMs cross-fades Fill and BorderColor over the SAME duration as the width tween.
            // (BorderBrush, the ControlElevationBorder gradient, is deliberately not used here: the brush channel has no
            // cross-fade, so it would snap. A flat stroke at 32 DIP tall is visually indistinguishable.)
            Corners = Radii.ControlAll,
            Fill = expanded
                ? (focused ? Tok.FillControlInputActive : Tok.FillControlDefault)
                : ColorF.Transparent,
            BorderWidth = 1f,
            BorderColor = expanded ? Tok.StrokeControlDefault : ColorF.Transparent,
            BrushTransitionMs = SearchExpandMs,
            Animate = SearchDisclosureMotion,
            ClipToBounds = true,
            Children = layers,
        };
    }

    void CollapseSearch(bool restoreFocus)
    {
        _restoreSearchFocus |= restoreFocus;
        _searchFocused.Value = false;
        _searchExpanded.Value = false;
    }

    TrackFilterCapabilities FilterCapabilities(DetailModel model)
    {
        bool local = false, streamed = false, unavailable = false, tempo = false;
        for (int i = 0; i < model.Tracks.Count; i++)
        {
            var track = model.Tracks[i];
            if (track.Origin == TrackOrigin.Local) local = true; else streamed = true;
            // Deliberately BROADER than the filter it gates (TrackFilterModel: `IsNotYetOut()`): Availability is
            // nullable, and `!= Playable` counts a track with no verdict at all. Kept as-is rather than aligned — the
            // narrower test would stop offering the "Playable only" chip on every surface whose rows never carry a
            // verdict, which is a visible affordance change, not a tidy-up. Over-offering is inert: the chip appears and
            // filters nothing.
            if (track.Availability != Availability.Playable) unavailable = true;
            if (track.TempoBpm is > 0d) tempo = true;
            if (local && streamed && unavailable && tempo) break;
        }
        return new TrackFilterCapabilities(
            HasVideo: model.HasVideo,
            HasDateAdded: model.HasDateAdded,
            HasMixedOrigin: local && streamed,
            HasUnavailable: unavailable,
            HasLibrary: _lib is not null,
            HasTempo: tempo);
    }

    // A no-op OnRealized for the plain action buttons (Play / Shuffle) — they open no flyout, so they need no anchor.
    static readonly Action<NodeHandle> NoAnchor = static _ => { };

    Element Header(ColumnSet set, TrackSize[] tracks, DetailTrackSort sort, bool checkInset)
    {
        // Keyed exactly like the row cells (TrackRow.CellKey) — same reason: a breakpoint cross drops a MIDDLE column,
        // and an unkeyed positional diff would reconcile the surviving header cells against the wrong ones.
        var cells = new List<Element>(tracks.Length);
        void Add(string key, Element cell) => cells.Add(cell with { Key = key });

        Add(TrackRow.CellKey.Num, set.Classic ? new BoxEl() : IndexSortCell(sort));
        if (set.Heart) Add(TrackRow.CellKey.Heart, new BoxEl());
        if (set.Thumb) Add(TrackRow.CellKey.Art, new BoxEl());
        // Title owns Artist only while the responsive grammar has folded Artist into its metadata subline. At wider
        // Classic tiers Artist is a peer header and each cell owns an independent three-state sort cycle.
        Element titleHeader = Embed.Comp(() => new SortLabel(
            _h.Sort, song: _verticalHeader, artistColumn: set.Artist, classic: set.Classic))
            with { Key = set.Classic ? "sort-label:classic" : "sort-label:modern" };
        Add(TrackRow.CellKey.Title, SortCell(titleHeader, SortColumn.Title, sort, FlexJustify.Start, set.Artist));
        if (set.Artist)
            Add(TrackRow.CellKey.Artist, SortCell(
                HLabel(Loc.Get(Strings.Detail.Column.Artist), SortColumn.Artist, sort, set.Classic),
                SortColumn.Artist, sort, FlexJustify.Start, artistColumn: true));
        if (set.Album) Add(TrackRow.CellKey.Album, SortCell(HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort, set.Classic), SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) Add(TrackRow.CellKey.By, PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy), classic: set.Classic));
        if (set.Date) Add(TrackRow.CellKey.Date, SortCell(HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort, set.Classic), SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Plays) Add(TrackRow.CellKey.Plays, SortCell(HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort, set.Classic), SortColumn.Plays, sort, FlexJustify.End));
        // Tempo · key. Not sortable: tempo lands ASYNCHRONOUSLY per row (kind 222), so a sort would reorder the list
        // under the user's cursor as adornments arrive. End-aligned to match the EndCell value lane.
        if (TrackRow.ShowTempo(set)) Add(TrackRow.CellKey.Tempo, PlainHeader(Loc.Get(Strings.Detail.Column.Tempo), FlexJustify.End, set.Classic));
        // Duration header: a "Time" text label in the vertical (Apple Music) profile, the clock icon everywhere else.
        Add(TrackRow.CellKey.Duration, SortCell(_verticalHeader
                ? HLabel(Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort, set.Classic)
                : Icon(Icons.Clock, 14f, sort.Column == SortColumn.Duration ? Tok.TextSecondary : Tok.TextTertiary),
                           SortColumn.Duration, sort, FlexJustify.End));
        if (set.Video) Add(TrackRow.CellKey.Video, new BoxEl());   // trailing film / "…" lane: no header label
        if (set.Actions) Add(TrackRow.CellKey.More, new BoxEl());   // trailing "..." overflow lane: no header label (keeps rows aligned)
        if (set.Expand) Add(TrackRow.CellKey.Expand, new BoxEl());  // chevron lane: no header label

        var grid = new GridEl
        {
            Columns = tracks, ColGap = TrackRow.ColGapFor(set.Tier),
            RowHeight = DetailTrackTableRules.HeaderHeightFor(set.Classic),
            Children = cells.ToArray(),
        };
        var headerGrid = new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Padding = new Edges4(checkInset ? 28f : 0f, 0f, 0f, 0f),
            Animate = new LayoutTransition(TransitionChannels.Position,
                TransitionDynamics.Tween(MotionTok.DisclosureExpand.DurationMs, Easing.FluentDecelerate)),
            Children =
            [
                grid,
                new BoxEl
                {
                    Height = 1f, Fill = Tok.StrokeDividerDefault,
                    // Chrome already owns the same PadX inset that Classic rows apply locally. A second inset here
                    // shortened only the header rule, so its endpoints missed every row hairline by one full PadX.
                    Margin = Edges4.All(0f),
                },
            ],
        };
        return headerGrid;
    }

    // The sort-direction caret (Segoe Fluent CaretSolid — chosen over the chevrons).
    internal static readonly string CaretGlyph = Icons.CaretSolidUp;   // track-list sort-direction caret (SortCaret rotates it 180° for descending)

    // Does this header own the active sort? The Title header also owns Artist (the title subline has no column of its
    // own), so it stays lit — and reads "Artist" — while the list is sorted by artist.
    static bool HeaderActive(SortColumn header, SortColumn active, bool artistColumn = false) =>
        DetailTrackTableRules.HeaderActive(header, active, artistColumn);

    // The owning header brightens — EXCEPT Index (#), the default "original order", which carries no indicator.
    static TextEl HLabel(string s, SortColumn col, DetailTrackSort sort, bool classic = false) =>
        new(ClassicHeaderText(s, classic))
        {
            Size = classic ? 11f : 12f, Weight = 600,
            CharSpacing = classic ? WaveeType.EyebrowTracking : 0f,
            Color = HeaderActive(col, sort.Column) ? Tok.TextSecondary : Tok.TextTertiary,
            MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

    static string ClassicHeaderText(string label, bool classic) =>
        classic ? label.ToUpper(CultureInfo.CurrentUICulture) : label;

    // A non-sortable column header — static label, no click/caret. Default Start (Added by); Tempo passes End so it
    // shares the value lane's right edge.
    static Element PlainHeader(string label, FlexJustify justify = FlexJustify.Start, bool classic = false) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = justify,
        MinWidth = 0f, ClipToBounds = true,
        Children = [new TextEl(ClassicHeaderText(label, classic)) { Size = classic ? 11f : 12f, Weight = 600,
            CharSpacing = classic ? WaveeType.EyebrowTracking : 0f,
            Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    // The row number lives at the exact centre of its TrackLane.Num lane. Reserve the SAME TrackLane.NumCaretSlot on
    // both sides and put the descending caret only in the right slot, so enabling the indicator never nudges # away
    // from the row numbers. Both numbers come from the lane table — the header's reservation and the width track the
    // grid builds from must be one arithmetic, or the "#" drifts off the digits under it.
    Element IndexSortCell(DetailTrackSort sort)
    {
        bool showCaret = sort.Column == SortColumn.Index && sort.Descending;
        Element side(bool trailing) => new BoxEl
        {
            Width = TrackLane.NumCaretSlot, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = trailing && showCaret ? [Embed.Comp(() => new SortCaret(_h.Sort))] : [],
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Control), HoverFill = Tok.FillSubtleSecondary,
            OnClick = () => _h.SetSort(NextSort(sort, SortColumn.Index)),
            Children =
            [
                side(false),
                new BoxEl
                {
                    Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [HLabel(Loc.Get(Strings.Detail.Column.Number), SortColumn.Index, sort)],
                },
                side(true),
            ],
        };
    }

    // A clickable column header: click to sort by this column (toggles asc/desc on repeat), with a caret on the active
    // column (before the content for the right-aligned duration, after it otherwise). The default Index/# column shows
    // NO caret and resets to the original order on click.
    Element SortCell(Element content, SortColumn col, DetailTrackSort sort, FlexJustify justify, bool artistColumn = false)
    {
        bool showCaret = HeaderActive(col, sort.Column, artistColumn)
            && (col != SortColumn.Index || sort.Descending);   // default/original order stays visually quiet
        // The caret is a self-animating component: it pops in when its column becomes the sort and springs its rotation
        // 0°↔180° on every direction flip (so the Title cell's ↑→↓→↑→↓ run reads as one continuous spin).
        Element Caret() => Embed.Comp(() => new SortCaret(_h.Sort));
        Element[] kids = !showCaret ? [content]
            : justify == FlexJustify.End ? [Caret(), content]
            : [content, Caret()];
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, Gap = Spacing.XS,
            // Same squeeze contract as the row cells (TrackRow.LeftCell/CenterCell/EndCell): shrinkable + clipped, so a
            // header label can never paint over the next column while the grid is narrower than its fixed tracks.
            MinWidth = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Control), HoverFill = Tok.FillSubtleSecondary,
            OnClick = () => _h.SetSort(NextSort(sort, col, artistColumn)),
            Children = kids,
        };
    }

    // The header-click cycle: each column steps ascending → descending → (back to the default original order), so a
    // run of clicks always lands back on the default. The Title header carries TWO fields — Title then Artist — so it
    // cycles Title↑ → Title↓ → Artist↑ → Artist↓ → default (the only header route to an artist sort). The # / Index
    // header always resets to the default.
    static DetailTrackSort NextSort(DetailTrackSort cur, SortColumn clicked, bool artistColumn = false) =>
        DetailTrackTableRules.NextSort(cur, clicked, artistColumn);

    /// <summary>The vertical/hero arm's shimmer: the reserved hero band, the REAL chrome (built exactly as
    /// <c>VerticalItemContent</c> builds list item 1 — same overload, same default inset — so the derived shimmer sits
    /// on the same column origin the loaded header will), then the row shimmer. Those three ARE the vertical list's
    /// item sequence, so a loaded page replaces each of them in place instead of appearing above them (D49).</summary>
    Element VerticalShimmer(ColumnSet set, TrackSize[] tracks, DetailTrackSort sort, bool labeled, int tier,
                            bool checkInset, Element? contentFilterBar, float rowH, float art) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            DetailSkeleton.VerticalHeroBand(
                _verticalHeroW.Peek(), _verticalHeroRowFlow, TrackRow.PadXFor(tier),
                HeroHasEyebrow(), HeroHasAttribution(), HeroHasMeta(), HeroHasDescription(), HeroHasPulse(),
                previewArt: ImageSource.IsUsable(_model.Cover) ? PreviewHeroArt : null),
            Chrome(set, tracks, sort, labeled, tier, checkInset, contentFilterBar: contentFilterBar),
            RowsShimmer(set, tracks, rowH, art),
        ],
    };

    /// <summary>The real hero cover for the loading skeleton's hero slot (see <c>DetailSkeleton.VerticalHeroBand</c>'s
    /// <c>previewArt</c>). The grid-click nav path always hands the page a usable preview cover (<c>DetailPage</c>'s
    /// preview→full latch, <see cref="ImageSource.PreferVisible"/>), so <c>_model.Cover</c> already carries it while
    /// <c>_full</c> is Pending — the same 256-bucket <c>Surfaces.Artwork</c> block the unmeasured real hero mounts
    /// (<see cref="DetailRail.HeroArtwork"/>'s own default), so the swap to the real hero is a synchronous cache hit.
    /// <c>.Skel(art)</c> (a self-override) is what makes this exempt from <c>SkeletonDeriver</c>'s generic recursion —
    /// without it the deriver would strip the real <c>ImageEl</c>'s Source and repaint it as a plain gray placeholder,
    /// which is the bug this fixes.</summary>
    Element PreviewHeroArt(float artSize)
    {
        Element art = DetailRail.HeroArtwork(_model, artSize, Radii.Card, connected: false, saturation: 1.18f);
        return art.Skel(art);
    }

    // The shimmer source for the track list: N copies of the REAL Row built with an empty track. The engine derives the
    // grey shimmer bars from this (one source of truth — the row shape can never drift from the real rows).
    Element RowsShimmer(ColumnSet set, TrackSize[] tracks, float rowH, float art)
    {
        var rows = new Element[12];
        // Static title (no bound slot index here) — the skeleton deriver only needs the row SHAPE. Plain TextEl (matches
        // the non-now-playing real rows now), so the skeleton mount carries no per-row marquee cost either.
        for (int i = 0; i < rows.Length; i++)
            rows[i] = RowGrid(EmptyTrack, i, isNow: false, isPlaying: false, isBuffering: false, isTop: false,
                              new TextEl(EmptyTrack.Title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                              set, tracks, rowH, art, more: false);
        return new BoxEl { Direction = 1, Children = rows };
    }

    // ── progressive reveal ───────────────────────────────────────────────────────────────────────────────────────────
    // Advance the cold ramp one chunk. Called once per frame by the reveal clock (Flow.Show-gated on _rampActive). When
    // the chunk crosses the realized band, snap _reveal to MaxValue (all rows real, incl. any scrolled in later) and drop
    // _rampActive so the clock unmounts. All Peek/arithmetic/signal-write — no per-frame allocation.
    void AdvanceReveal()
    {
        int next = DetailRevealRamp.Next(_reveal.Peek(), _visibleCount.Peek());
        _reveal.Value = next;
        if (next == DetailRevealRamp.Done) _rampActive.Value = false;   // ramp finished → the clock unmounts, the frame loop quiesces
    }

    // Is the row at this display position a REAL row yet, or still a shimmer placeholder? Reading _reveal.Value subscribes
    // the caller (an equality-gated per-row bool memo), so as the ramp advances only the newly-crossed chunk re-renders
    // shimmer→real. Done (MaxValue) in steady state ⇒ always true, so a revealed list pays nothing here.
    internal bool RowRevealed(int displayIndex) => DetailRevealRamp.Revealed(displayIndex, _reveal.Value);

    // The placeholder for a slot beyond the reveal ramp: an EMPTY row of the real row's exact extent — the same GridEl
    // (same column tracks, same RowHeight) with one empty cell per lane and nothing painted. Deliberately blank, not grey
    // bars: the crossing into the real row is a remount that fades the row in (BoundRowContent.RampReveal), and a bar
    // that vanished on the swap frame under a row still at opacity 0 read as bars → blank → text — a per-row flicker, the
    // very thing the ramp's cross-fade exists to remove. From blank the cascade is one gesture: rows fade in top-down,
    // chunk by chunk, under the page's own entrance. (On a cold load the region's derived shimmer — RowsShimmer, 12 rows =
    // one Chunk — is what dissolves under rows 0..11; slots past it were blank during Pending too.) It carries no art,
    // text shaping, hover transport, marquee or context menu — the record cost the ramp spreads across frames — and it
    // is static, so it keeps the loop asleep. It stays a GridEl on purpose: the real row mounts as a BoxEl wrapper, and
    // the TYPE change is what makes the crossing a remount in a single-child slot (see BoundRowContent).
    const string ShimmerRowKey = "row:shim";

    Element ShimmerRow(TrackSize[] tracks, float rowH)
    {
        var cells = new Element[tracks.Length];
        for (int i = 0; i < cells.Length; i++) cells[i] = new BoxEl();
        return new GridEl
        {
            // Paired with BoundRowContent.RealRowKey: the two roots of the same slot are DISTINCT identities, so the
            // placeholder→real crossing is a remount (and gets the entrance fade), never an in-place patch.
            Key = ShimmerRowKey,
            Columns = tracks, RowHeight = rowH, Grow = 1f,
            Children = cells,
        };
    }

    // ── bound row ────────────────────────────────────────────────────────────────────────────────────────
    // A bound row: ONE self-subscribing content component (re-renders on recycle/sort/now-playing, patching cells in
    // place — never a remount, so no flash) wrapped in the shape-stable bound selection skin.
    Element BoundRow(RowScope scope, IReadSignal<Track> item, float rowH, int trackStart, IReadSignal<bool>? hoverPaused = null)
        => Embed.Comp(() => new BoundRowContent(this, scope, item, _rowsSnapshot!, rowH, trackStart, hoverPaused));

    // ── Phase-D touch swipe-to-action for the VIRTUALIZED track rows (OFF by default) ────────────────────────────────
    // FLAGGED OFF: shipping the swipe layer on the eager queue/preview lists first. Before flipping this on, three things
    // need on-device verification (they don't affect the eager lists): (1) the ActionContext built here freezes at the
    // wrap — a recycled slot rebinds by index but the swipe actions capture the mount-time track, so the actions must be
    // made to read scope.Index (a lazy ctx) or the row re-wrapped per recycle; (2) close-on-scroll (the flat list does
    // not pass onScrollGeometryChanged — recycle+ResetKey covers scroll-OUT, but a small in-place scroll leaves an open
    // row open, unlike Spotify); (3) the ItemsView roving-focus toggles the slot ROOT's focusability imperatively — with
    // a SwipeControl now the root, that must still land on the focusable content. ResetKey (scope.Index) already
    // snap-closes on recycle. See app/Wavee/Components/RowSwipe.cs and the touch-design doc §4.2 / risks.
    static readonly bool RowSwipeOnVirtualizedRows = true;
    readonly SwipeGroup _swipeGroup = new();   // one single-open group per list instance (remounts with the keyed list)

    Element WrapRowSwipe(RowScope scope, Element row, int trackStart, IReadSignal<Track> item)
    {
        if (!RowSwipeOnVirtualizedRows || _acts is not { } acts) return row;
        ActionContext? Current()
        {
            var t = item.Peek();
            if (ReferenceEquals(t, EmptyTrack) || t.Id.Length == 0) return null;
            return new ActionContext(ActionTarget.ForTracks(new[] { t }), acts);
        }
        return RowSwipe.WrapBound(row, Current, group: _swipeGroup,
            leading: TrackActions.ToggleLike, trailing: TrackActions.AddToQueue, resetKey: scope.Index);
    }

    // The track shown at a display position, through the current (sort-subscribed) view. Reading _h.Sort.Value subscribes
    // the calling bind/render so a SORT change re-skins in place; the caller also reads the slot index signal so a
    // RECYCLE rebinds. Out-of-range (overscan past the view) → EmptyTrack.
    internal Track BoundTrackAt(int displayPos)
    {
        _ = _h.Sort.Value;
        var v = View();
        if ((uint)displayPos >= (uint)v.Length) return EmptyTrack;
        // Defense in depth: even if a stale view survives one frame after a shrink, the mapped ORIGINAL index must stay
        // within the CURRENT _tracks — never index past it (the R2 IndexOutOfRange). The identity guard in Render clears
        // the view on a track-set swap, so this is belt-and-suspenders for the one-frame window.
        int orig = v[displayPos];
        return (uint)orig < (uint)_tracks.Count ? _tracks[orig] : EmptyTrack;
    }

    // The title text stays bound to the recycled item signal. Playback colour is resolved by the row's equality-gated
    // presentation memo, so every realized title no longer owns a CurrentTrack subscription.
    Element BoundTitle(IReadSignal<Track> item) => Marquee.Of(
        Prop.Of(() => item.Value.Title),
        new Marquee.Style
        {
            FontSize = 14f, Weight = 600,
            Foreground = Tok.AccentTextPrimary,
        });

    // PERF: the marquee is 2 nested components + a measure→re-render cycle + a perpetual TranslateX track PER ROW — on a
    // 12-row cold mount that was ~24 of ~60 components and the dominant slice of the flush spike (and every one re-rendered
    // by RethemeAll on a theme flip). A non-now-playing title never needs to scroll (Spotify only scrolls the now-playing
    // row), so render it as a plain, bound, ellipsis TextEl — ONE node, no extra component, no measure cycle, no animation.
    // Recycle-safe: a recycled row is non-playing → stays plain (no type swap); only the single now-playing row uses the
    // marquee, and BoundRowContent re-renders (swapping plain↔marquee for just that row) when now-playing changes.
    Element BoundTitlePlain(IReadSignal<Track> item, bool nowPlaying) => new TextEl(Prop.Of(() => item.Value.Title))
    {
        Size = 14f, Weight = 600,
        Color = nowPlaying ? Tok.AccentTextPrimary : Tok.TextPrimary,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // The live content of a bound row: re-renders on its OWN subscriptions (recycle index, sort, now-playing, COLUMN
    // SHAPE) and patches the GRID in place via diff — no remount, no flash. Child COMPONENTS (the title marquee) are
    // reused across these re-renders, so the title is built with index-signal binds (BoundTitle) to update despite
    // frozen args. The column shape is deliberately NOT a constructor arg: those freeze at mount (the component-props
    // contract), and a frozen shape is what forced a breakpoint cross to remount the whole list.
    sealed class BoundRowContent : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly IReadSignal<Track> _item;
        readonly IReadSignal<TrackRowsSnapshot> _state;
        readonly float _rowH;
        readonly int _trackStart;
        readonly IReadSignal<bool>? _hoverPaused;
        public BoundRowContent(TrackList o, RowScope scope, IReadSignal<Track> item, IReadSignal<TrackRowsSnapshot> state,
                               float rowH, int trackStart, IReadSignal<bool>? hoverPaused = null)
        {
            _o = o; _scope = scope; _item = item; _state = state; _rowH = rowH; _trackStart = trackStart;
            _hoverPaused = hoverPaused;
        }

        public override Element Render()
        {
            var likePrev = UseRef(((string?)null, false));               // hook FIRST (stable order) — per-slot like-edge memory
            // The ramp-crossing latch: 0 = this slot has not rendered yet, 1 = its FIRST render was a shimmer placeholder
            // (so the crossing into the real row is a visible swap and earns the fade below), 2 = it mounted ALREADY
            // revealed. Only state 1 ever animates: a row that mounted real — steady state (_reveal at Done), a row
            // scrolled in later, a warm page that never ramped — must play nothing, because an entrance replayed on a
            // recycled slot reads as flicker (see the "where it may be used" note on WaveeEntrance in Design/WaveeMotion.cs).
            var revealLatch = UseRef((byte)0);
            var shape = _o._rowShape!.Value;                             // subscribe → a breakpoint cross re-renders THIS row in place
            // Progressive reveal: while the cold list ramps in, a row past the ramp renders a cheap shimmer placeholder.
            // The bool is equality-gated (per-slot), so this row re-renders shimmer→real only on the single frame its own
            // reveal edge crosses — not on every ramp tick. MaxValue in steady state ⇒ always true (no per-row cost).
            var revealed = UseComputed(() => _o.RowRevealed(_scope.Index.Value - _trackStart));   // hook order stays stable: all hooks run before the branch below
            // Full-detail/playback invalidations may recompute this record, but Memo's equality gate schedules a render
            // only when this particular row's visual state actually changed.
            var presentation = UseComputed(() =>
            {
                int i = _scope.Index.Value;
                int displayIndex = i - _trackStart;
                var rowState = _state.Value;
                var t = _item.Value;
                bool isTop = rowState.Config.ShowPlays
                    && rowState.TopTrackId is not null
                    && t.Id == rowState.TopTrackId;
                var st = TrackRow.StateOf(
                    _o._bridge, _o._lib, t, isTop,
                    _o._play?.IsRunning(t.Id) ?? false);
                return new RowPresentation(
                    t, displayIndex, st,
                    rowState.MarqueeDisabled,
                    rowState.Config.ShowTrackArtist,
                    rowState.Config.ShowAlbumColumn,
                    rowState.Handlers.Go,
                    AddedByProfile(rowState.Model, t));
            });
            // Latch this slot's FIRST reveal state before the branch below — it is what decides whether the crossing fades.
            bool revealedNow = revealed.Value;
            if (revealLatch.Value == 0) revealLatch.Value = revealedNow ? (byte)2 : (byte)1;
            // Not yet revealed (cold ramp): a cheap shimmer placeholder. All hooks above ran, so this early return keeps
            // hook order stable; presentation stays unread (lazy) so no real-row work happens until this row reveals.
            if (!revealedNow) return _o.ShimmerRow(shape.Tracks, _rowH);
            var row = presentation.Value;
            var t = row.Track;
            var st = row.State;
            // Buffering = this track's PlayAsync command is in flight (the Task-driven start spinner), OR the now-playing
            // track is mid-playback re-buffering (the bridge signal). Reading _play.IsRunning subscribes this row so the
            // spinner appears/clears as the command starts/finishes.
            bool likePop = TrackRow.LikeEdge(likePrev, t.Uri, st.Saved);   // pop only on the SAME-uri unsaved→saved edge

            // Marquee only for the now-playing row; every other row is a cheap plain ellipsis title (see BoundTitlePlain).
            Element title = st.IsNow && !row.MarqueeDisabled
                ? _o.BoundTitle(_item)
                : _o.BoundTitlePlain(_item, st.IsNow);
            Element grid = _o.RowGrid(t, row.DisplayIndex, st.IsNow, st.IsPlaying, st.IsBuffering, st.IsTop, title, shape.Set, shape.Tracks, _rowH, shape.Art,
                              onPlay: () => _o.PlayRow(row.DisplayIndex),
                              saved: st.Saved, onLike: t.Uri.Length > 0 ? (Action)(() =>
                              {
                                  var current = _item.Peek();
                                  if (current.Uri.Length > 0) _o._lib?.ToggleSaved(current.Uri, current.Title);
                              }) : null,
                              likePop: likePop, presentation: row, hoverPaused: _hoverPaused);
            // Rows that never shimmered return the grid bare — steady state, every recycled row, every warm page: not one
            // extra node, not one transition slot. Only a slot whose first render WAS a placeholder (latch 1) is wrapped.
            if (revealLatch.Value != 1) return grid;
            // The ramp crossing. The placeholder and the real row are the SAME component slot, so the reconciler would
            // patch one into the other in place — and a changed Key cannot ask for a remount here, because a component's
            // root goes through ReconcileSingleChild, which reuses a same-type child and only *reports* the ignored key
            // (Reconciler.ReportKeyIgnoredInSingleChildSlot). What actually forces Remove+Mount is the root's ELEMENT
            // TYPE changing, GridEl → BoxEl — and the mount is what seeds Enter (Element.Enter/Animate.Enter is baked on
            // BoxEl only, so the real row needs this box regardless). The keys are the intent, the type is the mechanism.
            // Layout-neutral: the wrapper carries the grid's own flex config (Grow 1, Shrink 0, Basis auto, row direction).
            return new BoxEl { Key = RealRowKey, Direction = 0, Grow = 1f, Animate = RampReveal, Children = [grid] };
        }

        const string RealRowKey = "row:real";
        // The crossing's softener: OPACITY ONLY — the row is already sitting in its final slot and its extent is the
        // measured rowH, so a rise or a size channel would fight the geometry the placeholder just reserved. Same 280ms
        // FluentDecelerate as the skin's mount entrance (BoundRowSkin `entrance`), so a ramp reveal and a re-cut remount
        // read as one gesture. Reduced motion needs no branch here: the engine's ReducedSnap still cross-fades opacity.
        // Static readonly ⇒ one shared spec for every row, so a crossing allocates the wrapper box and nothing else.
        static readonly LayoutTransition RampReveal = new(
            TransitionChannels.Opacity,
            TransitionDynamics.Tween(280f, Easing.FluentDecelerate),
            Enter: new EnterExit(Opacity: 0f, Active: true));
    }

    // Heterogeneous persistent-prefix content for the vertical playlist viewport. Prefix slots never recycle; track
    // slots bind their item through the same positional source as the flat list. Constructor values are shape-only.
    sealed class VerticalItemContent : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly IReadSignal<Track> _item;
        readonly float _rowH;
        readonly bool _entrance;

        public VerticalItemContent(TrackList o, RowScope scope, float rowH, bool entrance)
        {
            _o = o;
            _scope = scope;
            _item = o._rowItems!.BindItem(scope.Index, VerticalTrackStart);
            _rowH = rowH;
            _entrance = entrance;
        }

        public override Element Render()
        {
            int i = _scope.Index.Value;
            var shape = _o._rowShape!.Value;   // subscribe → breakpoint crosses patch the sticky chrome + rows in place
            int tier = shape.Set.Tier;
            bool labeled = tier <= 1;
            Element child;
            int visible = _o._rowItems!.Count.Value;
            switch (DetailVerticalLayout.ItemRole(i, visible, _o._verticalFacts.Value))
            {
                case DetailVerticalItemRole.Hero:
                    child = _o.VerticalHero();
                    break;
                case DetailVerticalItemRole.Chrome:
                    Element? filterBar = _o.ContentFilterBar();
                    child = _o.Chrome(shape.Set, shape.Tracks, _o._h.Sort.Value, labeled, tier,
                        _o._checksVisible?.Value ?? false, contentFilterBar: filterBar,
                        lensHeader: _o.LensHeader()) with { Key = "vitem:chrome" };
                    break;
                case DetailVerticalItemRole.Footer:
                    // The metadata cards, once, at the very bottom of the page. Hero system only — the rail arm never
                    // reaches this switch (it is not a vertical viewport at all).
                    child = _o.VerticalFactsFooter(tier);
                    break;
                case DetailVerticalItemRole.ExpandableTrack:
                    // The vertical viewport has two persistent prefix slots, but its track suffix must use the SAME
                    // expandable slot as the flat/recommendations lists. Building BoundRow directly made the chevron
                    // toggle _expandedRow while no drawer host existed — the glyph flipped and the row stayed one line.
                    child = _o.ExpandableSlot(_scope, _item, _rowH, _entrance, VerticalTrackStart)
                        with { Key = "vitem:row" };
                    break;
                default:
                    // The empty/loading PLACEHOLDER owns the single row slot an empty list keeps — and only that slot.
                    // Any other unaddressed index is transient: the footer's slot on the frame before _verticalFacts
                    // publishes sits here, and a shimmer band flashing under the last row of a short playlist would be
                    // the whole cost of the move. Render nothing there instead.
                    child = i == DetailVerticalLayout.PrefixCount
                        ? new BoxEl
                        {
                            Key = "vitem:empty",
                            MinHeight = 160f,
                            Direction = 1,
                            Children =
                            [
                                _o.ListPlaceholder(PlaylistListState.For(_o._model.MembershipLoaded, _o._tracks.Count, visible),
                                                   shape.Set, shape.Tracks, _rowH, shape.Art),
                            ],
                        }
                        : new BoxEl { Key = "vitem:blank" };
                    break;
            }

            return new BoxEl { Direction = 1, Children = [child] };
        }
    }

    // ── "Recommended songs" — the appended header + rec rows (playlist extender) ──────────────────────────────────────
    // The bound-slot content when recommendations are ON: ONE bound list carries the track rows, then the "Recommended"
    // header, then the recommendation rows — branching on the recycled slot index. Track rows use the normal bound
    // selection skin (multi-select); the header + rec rows render their OWN content, so they never join the track selection.
    sealed class RowOrRecContent : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly IReadSignal<Track> _item;
        readonly float _rowH;
        readonly bool _entrance;
        public RowOrRecContent(TrackList o, RowScope scope, float rowH, bool entrance)
        { _o = o; _scope = scope; _item = o._rowItems!.BindItem(scope.Index); _rowH = rowH; _entrance = entrance; }

        public override Element Render()
        {
            int i = _scope.Index.Value;            // recycle → re-render
            int visible = _o._rowItems!.Count.Value;
            Element child;
            if (i < visible)
                // The SAME expandable slot the plain list uses. This branch used to build the row inline and never host
                // a drawer, so on any playlist with recommendations live the expand chevron toggled its signal, flipped
                // its glyph, and nothing opened — the one list that looked broken was the one with an extra feature.
                child = _o.ExpandableSlot(_scope, _item, _rowH, _entrance)
                    with { Key = "rec:track" };
            // The DATA half of the gate (see Render): this template is mounted for every capable playlist, including the
            // window before the full model lands (and non-owned playlists, which never go live). _listCount then equals
            // the track count, so an appended index cannot be realized — except transiently, if a count write lands a
            // frame apart from the row projection. Render nothing rather than a stray "Recommended" header.
            else if (!_o._recsLive)
                child = new BoxEl { Key = "rec:empty" };
            else if (i == visible)
                child = Embed.Comp(() => new RecHeader(_o, _rowH)) with { Key = "rec:header" };
            else
            {
                int k = i - visible - 1;
                var recs = _o._recs.Value;         // subscribe → rec rows re-render when the batch changes
                child = k >= 0 && k < recs.Count
                    ? _o.RecRow(recs[k], _rowH)    // keyed by track id inside RecRow → a recycled slot remounts for the new track
                    : new BoxEl { Key = "rec:empty" };
            }
            return new BoxEl { Direction = 1, Children = [child] };
        }
    }

    // The always-present "Recommended songs" header row (the first appended slot). Its MOUNT is the lazy first-fetch
    // trigger — it realizes only once the user scrolls to the bottom — and it hosts the Refresh control plus the
    // loading spinner / empty note. Self-subscribing so those track the fetch state without re-rendering the whole list.
    sealed class RecHeader : Component
    {
        readonly TrackList _o;
        readonly float _rowH;
        public RecHeader(TrackList o, float rowH) { _o = o; _rowH = rowH; }

        public override Element Render()
        {
            var svc = UseContext(Services.Slot);
            var post = UsePost();
            int state = _o._recState.Value;        // subscribe → spinner ↔ refresh, empty note
            int count = _o._recs.Value.Count;      // subscribe → "no suggestions" only once loaded-empty

            // Lazy first fetch when THIS header realizes (scrolled to bottom). Constant dep ⇒ runs once per mount;
            // FetchRecs(force:false) no-ops unless idle, so a recycle remount never re-fetches.
            UseEffect(() =>
            {
                if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri)
                    _o.FetchRecs(svc, post, uri, force: false);
            }, "rec-header-once");

            void Refresh()
            {
                if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri)
                    _o.FetchRecs(svc, post, uri, force: true);
            }

            var trailing = new List<Element>(2);
            if (state == 2 && count == 0)
                trailing.Add(new TextEl("No suggestions right now") { Size = 12f, Color = Tok.TextTertiary });
            trailing.Add(state == 1
                ? new BoxEl { Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [TrackRow.Spinner()] }
                : RefreshButton(Refresh));

            return new BoxEl
            {
                Key = "rec:header-row",
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = _rowH,
                Padding = new Edges4(TrackRow.PadX, 0f, TrackRow.PadX, 0f),
                Children =
                [
                    // BodyStrong (14/20/600) — a list-section label, the same rung as the track titles under it. Was 15/700.
                    Ui.BodyStrong("Recommended songs") with
                    {
                        Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    .. trailing,
                ],
            };
        }

        static Element RefreshButton(Action onClick) => new BoxEl
        {
            Width = 32f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f),
            HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press, Cursor = CursorId.Hand,
            Role = AutomationRole.Button, OnClick = onClick,
            Children = [Icon(Icons.Refresh, 14f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }

    // One recommendation row: the shared art-forward ArtCard with a "+" Add button. Keyed by track id so a recycled slot
    // remounts cleanly for the new track (the NowPlayingOverlay inside ArtCard freezes its uri at mount). Renders its OWN
    // content (no bound selection skin) → it never joins the track multi-select. Right-click / long-press / the "…"
    // cell open the SINGLE-track menu (rec rows carry the full Track — the eager TrackAttach shape, search-"Songs" precedent).
    Element RecRow(Track t, float rowH) => new BoxEl
    {
        Key = "rec:" + t.Id,
        // Pin to the uniform stride (RepeatLayout.Stack): the ArtCard's own min-height (~52) would otherwise overflow the
        // 48px default row and overlap its neighbour — cap + clip keeps the 40px art (centred) fully visible.
        Direction = 0, AlignItems = FlexAlign.Center, MinHeight = rowH, MaxHeight = rowH, ClipToBounds = true,
        Padding = new Edges4(TrackRow.PadX - TrackRow.RowInset, 0f, TrackRow.PadX - TrackRow.RowInset, 0f),
        // A rec row carries its full Track and joins NO selection, so its payload is always exactly this one track —
        // and always a COPY: a recommendation is not yet a member of anything.
        Draggable = Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForTrack(t)),
        Children =
        [
            TrackRow.ArtCard(
                t, TrackRow.StateOf(_bridge, _lib, t), RecColumns, _h.Go,
                onPlay: () => { _ = _bridge?.Player.PlayAsync(t.Uri, 0); },
                art: 40f, showArtists: true, explicitBadge: true, showDuration: true,
                onAdd: () => AddRec(t),
                showMore: _acts is not null && _menuOverlay is not null,
                showArtwork: !_rowsSnapshot!.Peek().TrackArtworkHidden),
        ],
    }.WithMenu(_menuOverlay is { } ov ? Menus.TrackAttach(_acts, ov, t) : null);

    // Fetch a fresh, non-repeating batch. force:false = the lazy trigger (fires only from idle); force:true = Refresh /
    // auto-refill. The skip set carries every id ever shown, so the server never repeats. Marshalled back to the UI thread.
    void FetchRecs(Services svc, Action<Action> post, string uri, bool force)
    {
        if (svc.RealExtender is not { } extender) return;
        if (_recState.Peek() == 1) return;                     // already loading
        if (!force && _recState.Peek() != 0) return;           // the lazy trigger fires only once (idle → loading)
        _recState.Value = 1;
        string[] skip = _recShown.Count == 0 ? Array.Empty<string>() : new string[_recShown.Count];
        if (skip.Length > 0) _recShown.CopyTo(skip);
        var ct = _recCts.Token;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            IReadOnlyList<Track> batch;
            try { batch = await extender.ExtendAsync(uri, skip, RecBatch, ct).ConfigureAwait(false); }
            catch { batch = Array.Empty<Track>(); }
            post(() =>
            {
                for (int i = 0; i < batch.Count; i++) { var id = batch[i].Id; if (id.Length > 0) _recShown.Add(id); }
                _recs.Value = batch;
                _recState.Value = 2;
            });
        }
    }

    // Add a recommendation to THIS playlist (reuses LibraryBridge.AddTracksAsync → PlaylistOpKind.Add). Keep the card
    // until the serialized /changes write is confirmed: the old fire-and-forget path removed it and showed success before
    // the sync loop had even attempted the server write.
    void AddRec(Track t)
    {
        if (_lib is not { } lib || _post is not { } post || _model.ContextUri is not { Length: > 0 } uri) return;
        string key = t.Uri.Length > 0 ? t.Uri : t.Id;
        if (!_recAdding.Add(key)) return;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            try { await lib.AddTracksAsync(uri, new[] { t }, _recCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                post(() => { _recAdding.Remove(key); PlaylistEditErrors.Toast(ex); });
                return;
            }

            post(() =>
            {
                _recAdding.Remove(key);
                if (t.Id.Length > 0) _recShown.Add(t.Id);
                var cur = _recs.Peek();
                var next = new List<Track>(Math.Max(0, cur.Count - 1));
                for (int i = 0; i < cur.Count; i++) { var o = cur[i]; if (!ReferenceEquals(o, t) && o.Id != t.Id) next.Add(o); }
                _recs.Value = next;
                Toast.Show(Strings.Detail.AddedToPlaylist(_model.Title), new ToastOptions { Severity = InfoBarSeverity.Success });
                if (next.Count == 0 && _svc is not null && _post is not null)
                    FetchRecs(_svc, _post, uri, force: true);
            });
        }
    }

    // The # cell's play affordance: single-click PLAYS this track, or PAUSES/RESUMES it when it is already the
    // now-playing one (the PlayerBar.PrimaryClick toggle: optimistic IsPlaying write, then the player command). Called
    // from a click handler (not a reactive context), so the .Peek() reads here never subscribe.
    void PlayRow(int displayPos)
    {
        if (_rowsSnapshot is not { } source) return;
        var snapshot = source.Peek();
        var v = View(snapshot);
        if ((uint)displayPos >= (uint)v.Length) return;
        int orig = v[displayPos];
        var track = snapshot.Model.Tracks[orig];
        // The row is drawn dim, its # cell withholds the hover-play button (Components/TrackRow.cs) and its duration
        // lane shows a date instead of a time — but a click, a double-tap and Enter all still reached the player and
        // started nothing, silently. This is the one funnel every activation path goes through, so one guard closes
        // all of them.
        if (track.IsNotYetOut()) return;
        TrackRow.Invoke(_bridge, track, () => StartVisible(displayPos, snapshot));
    }

    // Start THIS context at the given display row, sending the VISIBLE (sorted/filtered) order so a remote player mirrors
    // the screen (PlayOrderedAsync). Collections stay URI-only on the wire (sort rides on context.url, server-side) — but
    // still identity-first: the clicked track's uri+uid is carried, the index is only a diagnostic fallback (§7.3), so a
    // divergent server order can never land on an unrelated row (the collection "always plays the first song" regression).
    // The keyed async command drives the row's #-cell buffer spinner. Empty view (still loading / filtered out) → context top.
    void StartVisible(int displayPos)
    {
        if (_rowsSnapshot is { } source) StartVisible(displayPos, source.Peek());
    }

    void StartVisible(int displayPos, TrackRowsSnapshot snapshot)
    {
        var v = View(snapshot);
        var model = snapshot.Model;
        var handlers = snapshot.Handlers;
        var tracks = model.Tracks;
        if (v.Length == 0) { handlers.Play(0); return; }
        if ((uint)displayPos >= (uint)v.Length) displayPos = 0;
        int orig = v[displayPos];
        var track = tracks[orig];
        if (_bridge is not null && _play is not null && model.ContextUri is { } uri)
        {
            // Collection OR natural order → identity-first skip (uri+uid; index diagnostic only), URI-only on the wire (no
            // embedded pages). Only a SORTED/FILTERED non-collection view embeds the visible order (the correct shape there).
            if (Wavee.Backend.ContextResolve.IsCollection(uri) || IsNaturalContextOrder(snapshot, v))
                _play.Run(track.Id, ct => _bridge.Player.PlayContextTrackAsync(
                    uri, new PlaybackContextTrack(track.Uri, track.ContextUid ?? string.Empty), orig, ct));
            else
            {
                var ordered = VisibleOrder(tracks, v);
                _play.Run(track.Id, ct => _bridge.Player.PlayOrderedAsync(uri, ordered, displayPos, ct));
            }
        }
        else
            handlers.Play(orig);
    }

    // The visible order as (uri, contextUid) pairs — the embedded page the outbound play command carries. ContextUid is
    // the per-row playlist-membership uid (skip_to-by-uid); "" outside a user playlist.
    static PlaybackContextTrack[] VisibleOrder(IReadOnlyList<Track> tracks, int[] v)
    {
        var ordered = new PlaybackContextTrack[v.Length];
        for (int k = 0; k < v.Length; k++) { var t = tracks[v[k]]; ordered[k] = new PlaybackContextTrack(t.Uri, t.ContextUid ?? string.Empty); }
        return ordered;
    }

    static bool IsNaturalContextOrder(TrackRowsSnapshot snapshot, int[] v)
    {
        if (snapshot.Sort != DetailTrackSort.Default) return false;
        if (snapshot.Query.Length != 0 || !snapshot.Filters.IsDefault) return false;
        if (v.Length != snapshot.Model.Tracks.Count) return false;
        for (int i = 0; i < v.Length; i++)
            if (v[i] != i) return false;
        return true;
    }

    /// <summary>One list slot: the row, and — when THIS row is the expanded one — its versions drawer beneath.
    ///
    /// The drawer is a child of the slot rather than a list item of its own, so selection, reorder, roving focus and
    /// the swipe layer keep their one-slot-per-track model. Only the slot's height changes.</summary>
    Element ExpandableSlot(RowScope row, IReadSignal<Track> item, float rowH, bool narrateRemount, int trackStart = 0)
        => Embed.Comp(() => new ExpandableRowSlot(this, row, item, rowH, narrateRemount, trackStart));

    /// <summary>Open this row's drawer, closing any other. One at a time, because two open drawers make the list jump
    /// unpredictably under the cursor while scrolling — and the measured layout re-anchors on every extent change.</summary>
    void ToggleExpanded(string rowKey)
        => _expandedRow.Value = _expandedRow.Peek() == rowKey ? "" : rowKey;

    /// <summary>Everything the expanded row's facts strip needs that a <see cref="Track"/> does not carry.
    ///
    /// <para>The two PENDING gates are the surface's own column gating minus the tier — the same expressions
    /// <see cref="SetFor"/> uses to decide whether the table reserves a Plays / BPM·key lane. That is deliberate: the
    /// drawer's honest "asked, not answered yet" dash must appear exactly where the table would have shown a lane for
    /// the same fact, and never on a surface that does not ask for the enrichment at all (search, artist Popular).
    /// The tier is excluded because a lane the WIDTH dropped is precisely what the drawer exists to restore.</para>
    ///
    /// <para>Culture + zone are read HERE, once per open, and injected — the rules file itself reads no ambient clock
    /// or locale, which is what makes the exact-date format unit-testable.</para></summary>
    TrackFactsOptions FactsOptionsFor(Track track)
    {
        var snap = _rowsSnapshot!.Peek();
        bool asksTempo = snap.Config.ShowTempo && snap.TempoColumn;
        bool asksPlays = snap.Config.ShowPlays || (snap.Config.PlaysColumnOptIn && snap.PlaysColumn);
        var by = AddedByProfile(snap.Model, track);
        return new TrackFactsOptions(
            TempoPending: asksTempo && track.TempoBpm is null,
            PlaysPending: asksPlays && track.PlayCount <= 0,
            // "Has a music video" is a property of the CATALOGUE ENTRY (the kind-99 association plane) and never a
            // field on Track — the SAME probe the trailing film lane uses, so a row and its drawer agree.
            HasVideo: VideoPresence.HasVideo(track),
            AddedByName: by?.Name is { Length: > 0 } name ? name : track.AddedBy,
            Culture: CultureInfo.CurrentCulture,
            Zone: TimeZoneInfo.Local,
            MajorWord: Loc.Get(Strings.Detail.TrackFacts.Major),
            MinorWord: Loc.Get(Strings.Detail.TrackFacts.Minor));
    }

    /// <summary>The expandable list slot. A Component (not a plain element) so it re-renders on ITS OWN
    /// subscriptions: the expanded-uri signal and the slot's bound item. That keeps expansion off the parent's render
    /// path — opening a drawer must not re-render the whole list.</summary>
    sealed class ExpandableRowSlot : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly IReadSignal<Track> _item;
        readonly float _rowH;
        readonly bool _narrate;
        readonly int _trackStart;

        public ExpandableRowSlot(TrackList o, RowScope scope, IReadSignal<Track> item, float rowH, bool narrate,
                                 int trackStart = 0)
        {
            _o = o; _scope = scope; _item = item; _rowH = rowH; _narrate = narrate;
            _trackStart = trackStart;
        }

        // Mount/unmount reflow, not an auto-height toggle on an already-mounted empty clip. Inside a measured virtual
        // row, changing Children=[] + Height=0 to Children=[body] + Height=auto in one commit gave FLIP no solved
        // destination to seed from: the GIF showed the whole drawer appearing in one 40ms frame. A newly-mounted,
        // keyed body takes the engine's proven PendingEnterReflow path (the same path stock Expander uses), while its
        // exit orphan preserves the painted drawer until the measured row has eased closed.
        //
        // TWO specs on TWO nodes — the split is load-bearing, and it is the same one the artist page's album drawer
        // already uses (DiscoGrid.DrawerResize / DrawerPresence):
        //
        //   OUTER (DrawerReveal, the CLIPPING box) — SIZE ONLY. A Position/Opacity terminal here moved
        //   the clip WINDOW itself, so the drawer's entrance painted Spacing.S ABOVE its own box, over the row it
        //   hangs from. The window must never move; only its height eases. Enter/Exit stay Active with NO Dx/Dy/
        //   Opacity because Enter.Active && Size == Reflow is what enrolls the node in PendingEnterReflow — the flag
        //   IS the mount-reflow opt-in, not a request for a translate. SuppressDescendantTransitions stops the
        //   drawer's own late-mounting content (version rows that arrive after the expansion fetch) from starting a
        //   SECOND wave of geometry motion inside a surface that is already projecting: descendants still get their
        //   final layout, their structural tracks are simply snapped onto it.
        //
        //   INNER (DrawerPresence) — the fade + drop-in, and nothing structural. It lives INSIDE the
        //   clip, so the translate slides the CONTENT under a stationary window instead of dragging the window with
        //   it. No Size channel: the outer owns height.
        static readonly LayoutTransition DrawerReveal = new(
            TransitionChannels.Size,
            MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Active: true),
            Exit: new EnterExit(Active: true),
            ExitDynamics: MotionTok.ControlFast.ToDynamics(),
            Size: SizeMode.Reflow,
            Anchor: SizeAnchor.Leading,
            SuppressDescendantTransitions: true);

        static readonly LayoutTransition DrawerPresence = new(
            TransitionChannels.Opacity | TransitionChannels.Position,
            MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Dy: -Spacing.S, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
            ExitDynamics: MotionTok.ControlFast.ToDynamics());

        public override Element Render()
        {
            // Hooks first, unconditionally. UseContext used to sit AFTER the collapsed early-return, so the hook cursor
            // saw a different sequence depending on whether the row was open — a rules-of-hooks violation that made the
            // expanded render read another slot's hook state.
            var svc = UseContext(Services.Slot);
            // Row hover → EQ pause (same interactive ancestor that drives #-cell HoverOpacity). Stable UseSignal.
            var rowHovered = UseSignal(false);

            var track = _item.Value;                       // subscribe → recycle rebinds this slot
            string expanded = _o._expandedRow.Value;       // subscribe → open/close re-renders only the two slots involved
            // Subscribe to the slot's own index too: it is half the row identity whenever the read model carries no
            // per-row uid, so a recycle must be able to move the drawer off this slot.
            int displayIndex = _scope.Index.Value - _trackStart;
            var shape = _o._rowShape!.Value;                 // style/columns are live for both the row skin and drawer
            var row = _o.WrapRowSwipe(_scope,
                _o.BoundRowSkin(_scope, _o.BoundRow(_scope, _item, _rowH, _trackStart, rowHovered),
                    _rowH, _narrate, _trackStart, shape.Set, rowHovered),
                _trackStart, _item);

            bool hasTrack = track is { Uri.Length: > 0 };
            bool open = hasTrack && MembershipDiff.RowKeyMatches(expanded, track!, displayIndex);
            // The drawer's element keys carry the ROW identity for the same reason the state does: two rows holding the
            // same song used to mint two IDENTICAL keys under the list, which is a keyed-reconcile collision.
            string rowKey = open ? MembershipDiff.RowKey(track!, displayIndex) : "";

            Element? drawer = null;
            if (open)
            {
                // Subscribe to the shape: a breakpoint cross changes which leading columns exist, so an open drawer
                // must re-indent in place rather than keep the indent it mounted with.
                var model = new TrackVersionsPanel.Model(
                    track!,
                    // A version's KIND is the user's requested form — clicking the music video means "watch it", not
                    // "play the song". The video plays through the PARENT song's uri, not the version's own: kind 99
                    // keys the video association on the SONG, so the linked video-track uri has no association of its
                    // own and resolves to plain audio (`available=None has=False` in the log — the "plays but no
                    // video" bug). Song-as-video is the proven pipeline: the resolve links the DRM video and plays it
                    // with its own soundtrack, exactly what "play, then switch to video" does. The intent is a
                    // ONE-PLAY scope (PlayAs → PrimeVideoIntentFor), so it dies with this track instead of leaving
                    // the standing video toggle on for the rest of the queue.
                    OnPlay: v =>
                    {
                        if (v.Kind == TrackVersionKind.Video)
                            VideoActions.PlayAs(svc?.Player, _o._bridge, track!.Uri, MediaForm.Video);
                        else
                            VideoActions.PlayAs(svc?.Player, _o._bridge, v.Uri, MediaForm.Default);
                    },
                    OnOpen: (route, arg) => _o._rowsSnapshot?.Peek().Handlers.Go(route, arg),
                    // Minus the rail's own offset inside the gutter, so the RAIL — not the gutter's left edge — is what
                    // lands on the artwork centre.
                    Indent: Math.Max(0f, ArtCentreIndent(shape.Set, shape.Art) - TrackVersionsPanel.RailOffset),
                    Facts: _o.FactsOptionsFor(track!));
                drawer = new BoxEl
                {
                    Key = "drawer:" + rowKey,
                    Direction = 1,
                    MinWidth = 0f,
                    ClipToBounds = true,
                    Animate = DrawerReveal,
                    // The drawer continues the row's plate: same zebra parity, same inset, bottom corners only.
                    // BOUND, not a value — the slot recycles by an index-signal write, so a plain fill would keep
                    // whichever parity it was first built with.
                    Fill = shape.Set.Classic
                        ? ColorF.Transparent
                        : Prop.Of(() => _scope.Index.Value % 2 != 0 ? WaveeColors.RowZebra : ColorF.Transparent),
                    Margin = shape.Set.Classic
                        ? Edges4.All(0f)
                        : new Edges4(TrackRow.RowInset, 0f, TrackRow.RowInset, 0f),
                    Corners = shape.Set.Classic
                        ? CornerRadius4.All(Radii.None)
                        : new CornerRadius4(0f, 0f, 6f, 6f),
                    Children =
                    [
                        // The presence layer, INSIDE the clip (see DrawerReveal/DrawerPresence above). Stable key so
                        // it is one node for the drawer's whole life — it enters and exits with the drawer, and never
                        // re-mounts when the panel's own content grows underneath it.
                        new BoxEl
                        {
                            Key = "drawer-presence:" + rowKey,
                            Direction = 1,
                            MinWidth = 0f,
                            Animate = DrawerPresence,
                            Children =
                            [
                                Ctx.Provide(TrackVersionsPanel.Props, model,
                                    Embed.Comp(() => new TrackVersionsPanel()) with { Key = "drawer-body:" + rowKey }),
                            ],
                        },
                    ],
                };
            }

            // ONE root shape in every state: the row never shifts tree depth, so its bound selection/zebra/hover skin
            // remains wired. Only the keyed drawer child enters/exits; the reconciler keeps an exiting Reflow orphan
            // under this same root until its close motion settles.
            Element[] children = drawer is null ? [row with { Key = "row" }] : [row with { Key = "row" }, drawer];
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = children,
            };
        }
    }

    /// <summary>Reference-keyed memo for the derived chip set. IReadOnlyList identity is the right key here: the store
    /// hands out a NEW list when adornments land and the SAME one on an unrelated re-render.</summary>
    sealed class ChipCache
    {
        IReadOnlyList<Track>? _key;
        ContentFilterChipSet _value = ContentFilterChipSet.Empty;

        IReadOnlyList<ContentFilterChip>? _serverKey;

        public ContentFilterChipSet For(IReadOnlyList<Track> tracks, IReadOnlyList<ContentFilterChip> serverChips)
        {
            if (ReferenceEquals(_key, tracks) && ReferenceEquals(_serverKey, serverChips)) return _value;
            _key = tracks;
            _serverKey = serverChips;
            // The server's set is computed FOR THIS LIBRARY — Spotify already decided these concepts describe these
            // Liked Songs — so it stands on its own and is NOT gated on local descriptor evidence. Requiring a
            // matching kind-6 tag hid the bar entirely whenever descriptor enrichment was sparse or still in flight,
            // which is the common case on a cold list. Evidence therefore decides ORDER and AVAILABILITY, never
            // membership: concepts the tracks in view demonstrably carry come first and are tappable, the rest keep
            // the server's own order behind them and render unavailable until enrichment lands.
            _value = serverChips.Count > 0
                ? ContentFilterTags.OrderByEvidence(serverChips, tracks)
                : ContentFilterChips.Derive(tracks);
            return _value;
        }
    }

    // ── row grid ─────────────────────────────────────────────────────────────────────────────────────────
    // The row cell is the shared TrackRow.Grid (Components/TrackRow.cs) — ONE definition rendered identically by the
    // detail list, the library pane, artist "Popular" and search. This threads the detail list's per-row state + the
    // column set + the navigation handler through; the bound title element (plain vs marquee) is decided by the caller
    // (BoundRowContent), and the skeleton passes a static title. Plain/diffable → a BoundRowContent re-render patches in place.
    Element RowGrid(Track t, int displayIndex, bool isNow, bool isPlaying, bool isBuffering, bool isTop, Element title,
                    ColumnSet set, TrackSize[] tracks, float rowH, float art, Action? onPlay = null, bool saved = false, Action? onLike = null,
                    bool likePop = false, bool more = true, RowPresentation? presentation = null,
                    IReadSignal<bool>? hoverPaused = null)
    {
        var snapshot = presentation is null ? _rowsSnapshot!.Peek() : default;
        bool configuredTrackArtist = presentation is { } row ? row.ShowTrackArtist : snapshot.Config.ShowTrackArtist;
        bool showTrackArtist = configuredTrackArtist && !set.Artist;
        bool showListMetadata = presentation is { } rowMeta ? rowMeta.ShowListMetadata : snapshot.Config.ShowAlbumColumn;
        var go = presentation is { } rowGo ? rowGo.Go : snapshot.Handlers.Go;
        Owner? addedBy = presentation is { } rowOwner ? rowOwner.AddedBy : AddedByProfile(snapshot.Model, t);
        return TrackRow.Grid(t, displayIndex, new TrackRow.State(isNow, isPlaying, isBuffering, isTop, saved),
                         set, tracks, rowH, title, showTrackArtist, go, art,
                         onPlay, onLike, addedBy, likePop,
                         // The trailing "…" — ClickRequestsContext opens the row's own context menu anchored at the
                         // button (input-a11y §6.5.1). Disabled for the shimmer rows: a skeleton keeps the identical
                         // reserved lane but stays non-interactive and hidden.
                         // When Video is on, More lives in the Video lane (set.Actions false) → no trailing button.
                         // The ultra-compact tier also drops the "…" lane → no button built.
                         actionsCell: set.Actions ? TrackRow.MoreButton(more, classic: set.Classic) : null,
                         // Keyed by the ROW, not the track: two rows holding the same song are two independent drawers.
                         // TRACKS only: the drawer's whole content is alternate versions + per-item audio format, and
                         // SpotifyTrackExpansionService is track-only by decision — an EPISODE row would open an empty
                         // drawer, which is worse than no chevron at all (the lane is simply absent for it).
                         expandCell: set.Expand && t.Uri.Length > 0 && EntityUri.KindOf(t.Uri) == EntityKind.Track
                             ? TrackRow.ExpandChevron(
                                 MembershipDiff.RowKeyMatches(_expandedRow.Value, t, displayIndex),
                                 () => ToggleExpanded(MembershipDiff.RowKey(t, displayIndex)))
                             : null,
                         showAlbumInMeta: !set.Classic && showListMetadata && !set.Album,
                         showListBadges: showListMetadata,
                         moreEnabled: more,
                         hoverPaused: hoverPaused);
    }

    static Owner? AddedByProfile(DetailModel model, Track t)
    {
        if (t.AddedBy is not { Length: > 0 } raw || model.UserProfilesById is not { Count: > 0 } profiles) return null;
        if (profiles.TryGetValue(raw, out var owner)) return owner;
        var canonical = UserProfileIds.Normalize(raw);
        return canonical is not null && profiles.TryGetValue(canonical, out owner) ? owner : null;
    }

    // The custom row container, BOUND + shape-stable so it recycles without remounting: the zebra REST fill AND the
    // hover/press fills are bound to the slot index (recycle-correct + theme-reactive via RethemeAll re-firing binds),
    // and the left accent pill is an ALWAYS-PRESENT child revealed by a BOUND opacity on scope.IsSelected — so a
    // selection change is a compositor-only re-skin (no list re-render, no remount, no Enter replay → no flash).
    // Border stays uniform; the bound hover/press restores the zebra-vs-flush hover-depth nuance.
    BoxEl BoundRowSkin(RowScope scope, Element content, float rowH, bool entrance, int trackStart, ColumnSet set,
                       Signal<bool>? rowHovered = null)
    {
        var index = scope.Index;
        var isSel = scope.IsSelected;
        var onInteraction = scope.OnInteraction;
        var onFocusChanged = scope.OnFocusChanged;
        int DisplayIndex() => Math.Max(0, index.Value - trackStart);
        // .mp4-onto-a-row attach. Scoped to the DETAIL-PAGE row wrapper (not the shared TrackRow cell) precisely because
        // that is where a per-slot spec is affordable: this method already builds the slot's bound Prop closures + the
        // lazy context-menu thunk once per bind, and the spec rides the SAME live index signal the menu does, so it stays
        // correct across recycling. Built ONLY when the curation service exists — no service, no allocation, no target.
        // The hover cue reuses the existing Fill closure (below) rather than adding an overlay element per row.
        bool DropCue() => _videoDropRow.Value == index.Value;
        DropTargetSpec? drop = _acts?.VideoOverrides is { } curation
            ? new DropTargetSpec(
                [DropKinds.Files],
                OnEnter: _ => _videoDropRow.Value = index.Peek(),
                OnLeave: _ => { if (_videoDropRow.Peek() == index.Peek()) _videoDropRow.Value = -1; },
                OnDrop: s =>
                {
                    _videoDropRow.Value = -1;
                    if (s.Payload is not FileDropData { Count: > 0 } files) return;
                    if (VideoOverrideUx.FirstMp4(files.Paths) is not { } mp4)
                    {
                        // No video in the drop, so this was never an attach gesture. This row target sits BETWEEN the
                        // pointer and the shell's play-this-file target (the engine picks the deepest accepting node),
                        // so swallowing it would make the whole tracklist a dead zone for an audio drop. Hand it to the
                        // very same shell path instead — which also raises the "can't play that" cue when it is neither.
                        LocalFileActions.PlayDropped(_acts, files.Paths);
                        return;
                    }
                    if (DisplayTrack(index.Peek(), trackStart) is not { Uri.Length: > 0 } t) return;
                    VideoActions.Apply(_acts!, curation, t.Uri, mp4, replace: curation.Has(t.Uri));
                })
            : null;
        // The hero-system page's STACKED flow uses plain rows (no per-row pill/border) — the page is one column there
        // and an inset pill reads as a second, narrower page. Row flow (a wide hero-system page) keeps the WinUI
        // zebra-pill treatment below, as every two-column page does.
        bool classic = set.Classic;
        bool plainRows = !classic && _verticalHeader && !_verticalHeroRowFlow;
        var skin = new BoxEl
        {
            ZStack = true, MinHeight = rowH, ClipToBounds = true,    // ZStack → the left accent bar overlays the content
            Margin = classic || plainRows ? Edges4.All(0f) : new Edges4(RowInset, 0f, RowInset, 0f), // inset → rounded pill (#32)
            // Bottom corners square while THIS row's drawer is open, so the row and the drawer below it read as one
            // taller pill instead of a rounded row with a second plate stuck to it. Bound, so opening a drawer re-skins
            // the row on the compositor without re-rendering the list.
            Corners = classic || plainRows
                ? Prop.Of(() => CornerRadius4.All(0f))
                : Prop.Of(() => DisplayTrack(index.Value, trackStart) is { Uri.Length: > 0 } dt
                                && MembershipDiff.RowKeyMatches(_expandedRow.Value, dt, index.Value - trackStart)
                    ? new CornerRadius4(6f, 6f, 0f, 0f)
                    : CornerRadius4.All(6f)),
            // Reveal on slot MOUNT for navigation cold load + curated re-cut (reset epoch). Tier/density/filter remounts
            // skip the entrance — recycling reuses the slot (no mount), so this never replays on scroll or selection.
            Animate = entrance ? new LayoutTransition(TransitionChannels.Opacity,
                TransitionDynamics.Tween(280f, Easing.FluentDecelerate),
                Enter: new EnterExit(Opacity: 0f, Active: true)) : null,
            // Zebra REST fill bound to the slot index (recycle-correct), reading WaveeColors so RethemeAll recolours
            // it on a theme/palette switch (RowZebra chooses the theme-appropriate neutral overlay).
            // Selection does NOT change the fill — the left accent bar (below) is the ONLY selection cue.
            // The .mp4 drop cue rides the fill closure that already exists (an extra overlay child would cost a node per
            // row); DropCue() is a constant false when no drop target was built.
            DropTarget = drop,
            Fill = classic ? Prop.Of(() => DropCue() || isSel() ? WaveeColors.RowHover : ColorF.Transparent)
                : plainRows ? Prop.Of(() => DropCue() ? WaveeColors.RowHover : ColorF.Transparent)
                : Prop.Of(() => DropCue() ? WaveeColors.RowHover
                    : DisplayIndex() % 2 != 0 ? WaveeColors.RowZebra : ColorF.Transparent),
            HoverFill = classic || plainRows ? WaveeColors.RowHover
                : Prop.Of(() => DisplayIndex() % 2 != 0 ? WaveeColors.RowHoverZebra : WaveeColors.RowHover),
            PressedFill = classic || plainRows ? WaveeColors.RowPressed
                : Prop.Of(() => DisplayIndex() % 2 != 0 ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed),
            PressScale = WaveeMotion.ScaleSubtle.Press,
            // Stationary lift: the row stays in its slot at 0.4 (Atlassian's "it's in the chip" dim) while the chip
            // follows the pointer — the full-width lifted row snapshot was the S1 ghost failure.
            Draggable = Drag.Source(WaveeDragKinds.Resource, () => TrackDragPayload(index.Peek(), trackStart)),

            BorderWidth = classic || plainRows ? 0f : 1f,
            // WinUI even rows: CardStroke at rest. BorderColor is Prop<ColorF> — bind to the zebra index.
            BorderColor = classic || plainRows ? ColorF.Transparent
                : Prop.Of(() => DisplayIndex() % 2 != 0 ? Tok.StrokeCardDefault : ColorF.Transparent),
            HoverBorderColor = classic || plainRows ? ColorF.Transparent : Tok.StrokeCardDefault,
            FocusVisualMargin = Edges4.All(1f),
            Focusable = false,                       // the ItemsView roving effect owns the single tab stop
            Role = AutomationRole.Button,
            // Double-click invokes (plays), single click selects. DoubleTap is a POINTER trigger, so the ItemsView lands
            // focus + scrolls the row into view on it (EnterKey would skip that). While the check lane is visible a
            // plain tap/space TOGGLES the row into the selection (synthesized Ctrl) — WinUI multi-select semantics.
            OnPointerReleased = args =>
            {
                if (args.ClickCount >= 2) onInteraction(ItemContainerTrigger.DoubleTap, args.Mods);
                else onInteraction(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(_checksVisibleRead(), args.Mods));
            },
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(_checksVisibleRead(), args.Mods)); args.Handled = true; }
                // Alt+Up / Alt+Down: shift the SELECTED block one row — the keyboard equivalent of the drag reorder
                // (Alt because bare arrows are the list's own roving navigation). Under the same gates the drag has:
                // an editable playlist in natural order with no query or filter, else it silently does nothing rather
                // than move a row the display order cannot name.
                else if (args.Alt && (args.KeyCode == Keys.Up || args.KeyCode == Keys.Down)
                         && TryBlockMove(args.KeyCode == Keys.Up ? -1 : +1))
                    args.Handled = true;
            },
            OnFocusChanged = onFocusChanged,
            // Enter/exit: PointerBit so HoverOpacity descendants inherit row hover, AND write rowHovered so the EQ
            // stops ticking while the #-cell is faded out. Without rowHovered, keep the historic no-op exit.
            OnHoverMove = rowHovered is { } h
                ? _ => { if (!h.Peek()) h.Value = true; }
                : null,
            OnPointerExit = rowHovered is { } h2
                ? () => { if (h2.Peek()) h2.Value = false; }
                : static () => { },
            // Content lane (Grow fills the ZStack) + the WinUI ListView-style left accent selection bar. The pill is
            // ALWAYS present (shape-stable) and revealed by a BOUND opacity (no mount-Enter spring — the slot never
            // remounts, so selection is a compositor-only re-skin); press still shrinks it (10/16).
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
                    Animate = new LayoutTransition(TransitionChannels.Position,
                        TransitionDynamics.Tween(MotionTok.DisclosureExpand.DurationMs, Easing.FluentDecelerate)),
                    Children =
                    [
                        SelectorVisualsBound.BoundCheckLane(_checksVisibleRead, isSel, onInteraction, leftMargin: 4f),
                        content,
                    ],
                },
                new BoxEl
                {
                    Key = "row-pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                    Corners = classic ? CornerRadius4.All(Radii.None) : CornerRadius4.All(1.5f),
                    Fill = Prop.Of(() => _rowAccent!.Value), AlignSelf = FlexAlign.Center,
                    HitTestVisible = false, PressScale = 10f / 16f,
                    Opacity = Prop.Of(() => !classic && isSel() && !_checksVisibleRead() ? 1f : 0f),
                },
                new BoxEl
                {
                    Key = "row-divider", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch,
                    Height = 1f, Fill = classic ? Tok.StrokeDividerDefault : ColorF.Transparent,
                    Margin = classic
                        ? new Edges4(TrackRow.PadXFor(set.Tier), 0f, TrackRow.PadXFor(set.Tier), 0f)
                        : Edges4.All(0f),
                    HitTestVisible = false,
                },
            ],
        };
        // Win11 context menu (right-click / Menu key / touch long-press): attached ONCE per recycled slot (chains, never
        // clobbers, the skin's press/key handlers); the factory runs lazily AT OPEN, reading the slot's live index + the
        // current selection (Explorer semantics in TrackContextMenu). Non-track slots (recommendation header/rows,
        // vertical hero/chrome, overscan) resolve no track → no menu. Covers the flat, recommendations and vertical
        // layouts at once — all three go through this skin.
        if (_acts is { } acts && _menuOverlay is { } menuSvc)
        {
            // The drawer's FALLBACK verb, in either skin. The chevron is the affordance; this exists only for the
            // ultra-compact tier, where the table has no room for the chevron lane at all and the drawer would
            // otherwise be unreachable. Whenever the lane IS up, ShowVersionsMenuItem returns false and the menu
            // carries nothing — one action must not have two visible controls.
            IReadOnlyList<MenuFlyoutItem>? TrackDetailsExtras(Track track)
            {
                bool musicTrack = track.Uri.Length > 0 && EntityUri.KindOf(track.Uri) == EntityKind.Track;
                bool showVersions = _rowsSnapshot?.Peek().Config.ShowVersions ?? _cfg.ShowVersions;
                if (!DetailTrackTableRules.ShowVersionsMenuItem(showVersions, set.Expand, musicTrack)) return null;

                int displayIndex = index.Peek() - trackStart;
                string rowKey = MembershipDiff.RowKey(track, displayIndex);
                return
                [
                    new MenuFlyoutItem(
                        Loc.Get(Strings.Detail.TrackFacts.ShowDetails),
                        Icons.List,
                        Invoke: () => ToggleExpanded(rowKey)),
                ];
            }

            Func<Track, IReadOnlyList<MenuFlyoutItem>?>? extras = TrackDetailsExtras;
            return skin.WithContextMenu(menuSvc, () => TrackContextMenu.Build(
                acts, _selection, i => DisplayTrack(i, trackStart), index.Peek(), HostInfo,
                showGoToAlbum: _rowsSnapshot?.Peek().Config.ShowAlbumColumn ?? _cfg.ShowAlbumColumn,
                singleTrackExtras: extras));
        }
        return skin;
    }

    // (The # cell's number↔play/pause transport, the now-playing equalizer, the buffer spinner, the per-row heart, the
    // artist/album links and the cell wrappers all live in the shared TrackRow now — see Components/TrackRow.cs.)
}

// The multi-select toggle: shows/hides row checkboxes; turning OFF also clears the current selection.
sealed class MultiSelectButton : Component
{
    readonly IReadSignal<bool> _mode;
    readonly Action<bool> _setMode;
    readonly SelectionModel _selection;
    readonly bool _labeled;
    public MultiSelectButton(IReadSignal<bool> mode, Action<bool> setMode, SelectionModel selection, bool labeled)
    { _mode = mode; _setMode = setMode; _selection = selection; _labeled = labeled; }

    public override Element Render()
    {
        bool on = _mode.Value;
        void Toggle()
        {
            bool next = !on;
            if (!next) _selection.ClearSelection();
            _setMode(next);
        }
        string label = Loc.Get(Strings.Detail.Select);
        return _labeled
            ? ToolFx.LabeledButton(Icons.MultiSelect, label, on, Toggle, static _ => { })
            : ToolTip.Wrap(ToolFx.Button(Icons.MultiSelect, on, Toggle, static _ => { }), label);
    }
}

// The sort-direction caret: pops in (scale + fade) when its column becomes the active sort, and springs its rotation
// 0°↔180° (up↔down) on every direction flip — so the Title header's Title↑→Title↓→Artist↑→Artist↓ run reads as one
// continuous rotation rather than four glyph swaps. Self-contained, so it survives the header's re-render on each sort
// (its column persists → the spring is velocity-continuous; a column change remounts it → it pops in afresh).
sealed class SortCaret : Component
{
    readonly IReadSignal<DetailTrackSort> _sort;
    public SortCaret(IReadSignal<DetailTrackSort> sort) { _sort = sort; }

    public override Element Render()
    {
        bool desc = _sort.Value.Descending;   // subscribe → re-seed the rotation spring on each flip
        UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.EaseInOut, "in");
        UseTransition(AnimChannel.ScaleX, 0.3f, 1f, Expressive.Fast, Easing.Overshoot, "in");
        UseTransition(AnimChannel.ScaleY, 0.3f, 1f, Expressive.Fast, Easing.Overshoot, "in");
        UseSpring(AnimChannel.Rotation, desc ? 180f : 0f, SpringParams.FromResponse(0.30f, 0.7f), desc);
        return new BoxEl
        {
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(TrackList.CaretGlyph, 9f, Tok.TextSecondary)],
        };
    }
}

// The Title header label: "Title" (or the vertical profile's "Song"), or "Artist" while the list is sorted by artist
// (the Title header owns the artist sort). A small rise + fade plays when the word flips (keyed on the text), so the
// Title↔Artist swap reads as a transition rather than a snap.
sealed class SortLabel : Component
{
    readonly IReadSignal<DetailTrackSort> _sort;
    readonly bool _song;   // vertical (Apple Music) profile: the base label reads "Song" instead of "Title"
    readonly bool _artistColumn;
    readonly bool _classic;
    public SortLabel(IReadSignal<DetailTrackSort> sort, bool song = false, bool artistColumn = false, bool classic = false)
    { _sort = sort; _song = song; _artistColumn = artistColumn; _classic = classic; }

    public override Element Render()
    {
        var col = _sort.Value.Column;
        string text = !_artistColumn && col == SortColumn.Artist ? Loc.Get(Strings.Detail.Column.Artist)
            : Loc.Get(_song ? Strings.Detail.Column.Song : Strings.Detail.Column.Title);
        if (_classic) text = text.ToUpper(CultureInfo.CurrentUICulture);
        bool active = DetailTrackTableRules.HeaderActive(SortColumn.Title, col, _artistColumn);
        UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
        UseTransition(AnimChannel.TranslateY, 4f, 0f, Expressive.Fast, Easing.SmoothOut, text);
        return new TextEl(text)
        {
            Size = _classic ? 11f : 12f, Weight = 600,
            CharSpacing = _classic ? WaveeType.EyebrowTracking : 0f,
            Color = active ? Tok.TextSecondary : Tok.TextTertiary,
        };
    }
}

// The "sort by" flyout button (Icons.Sort). Opens a MenuFlyout via the overlay service — the same DropDownButton path as
// ShellToolbar — listing the sort FIELDS as a radio group (Custom order / Title / Artist / Album / Date added /
// Duration, each gated by what the context actually has) plus an Ascending/Descending pair. This is the only way to sort
// by Artist (no column header of its own). Used in both the chrome toolbar and the vertical-header toolbar.
sealed class PlaylistTuneButton : Component
{
    readonly Loadable<DetailModel> _full;
    readonly IReadSignal<IPlaylistTuningSource?> _source;
    readonly bool _labeled;

    public PlaylistTuneButton(
        Loadable<DetailModel> full,
        IReadSignal<IPlaylistTuningSource?> source,
        bool labeled)
    {
        _full = full;
        _source = source;
        _labeled = labeled;
    }

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var command = UseAsyncCommand();
        var post = Context.UsePost();
        // The first-run teaching tip's settings seam comes from CONTEXT, never a frozen ctor prop (component props freeze
        // at mount, and this button is embedded through a factory that runs once).
        var services = UseContext(Services.Slot);
        var model = _full.Value.Value;
        var source = _source.Value;
        // The command's own visibility gate — and therefore the teaching tip's page gate too. Tune is playlist-only
        // (a tuning source + at least one named choice + a context uri), so an album/artist page can never arm the tip;
        // there is deliberately no second page-kind test to drift from this one.
        bool eligible = PlaylistTuneMenuModel.IsEligible(model.Tuning, source is not null)
            && model.ContextUri is { Length: > 0 };

        // First-run teaching tip, via the app-wide service: WaveeTips owns the acknowledged-id set, the once-per-launch
        // latch, one-tip-at-a-time and the after-first-paint scheduling, and it renders through the engine's WinUI-parity
        // TeachingTip control — this call site owns only the anchor and the copy. EVERY hook above and this effect run on
        // every render; the eligibility early-return sits BELOW them, so the hook order can never change.
        UseEffect(() =>
        {
            if (eligible)
                WaveeTips.TryShow(overlay, services?.Settings, post, WaveeTipIds.DetailTuning,
                    () => anchor.Value, () => Context.Scene,
                    "detail.tuning.tipTitle", "detail.tuning.tipBody");
            // Navigation away / eligibility loss takes the tip DOWN without acknowledging it: the user never answered, so
            // it earns one more chance next launch (the service's per-launch latch stops it re-opening before then).
            return (Action?)(() => WaveeTips.Close(WaveeTipIds.DetailTuning));
        }, eligible);

        if (!eligible) return new BoxEl();

        bool busy = command.IsRunning;
        bool active = model.Tuning!.SelectedIdentifier is not null;

        void Apply(string identifier)
        {
            var currentSource = _source.Peek();
            var current = _full.Value.Peek();
            if (currentSource is null || current.ContextUri is not { Length: > 0 } uri) return;
            command.Run(
                async ct =>
                {
                    await currentSource.ApplyAsync(uri, identifier, ct).ConfigureAwait(false);
                    post(() => Toast.Show(Loc.Get(Strings.Detail.Tuning.Applied),
                        new ToastOptions { Severity = InfoBarSeverity.Success }));
                },
                _ => Toast.Show(Loc.Get(Strings.Detail.Tuning.ApplyFailed),
                    new ToastOptions { Severity = InfoBarSeverity.Error }));
        }

        IReadOnlyList<MenuFlyoutItem> Items()
        {
            var tuning = _full.Value.Peek().Tuning;
            if (tuning is null) return Array.Empty<MenuFlyoutItem>();
            var choices = PlaylistTuneMenuModel.VisibleChoices(tuning);
            var items = new List<MenuFlyoutItem>(choices.Count + 2);
            for (int i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                bool selected = string.Equals(tuning.SelectedIdentifier, choice.Identifier, StringComparison.Ordinal);
                items.Add(MenuFlyoutItem.RadioItem(
                    choice.DisplayName!,
                    selected,
                    () => Apply(choice.Identifier),
                    enabled: !selected) with
                {
                    AcceleratorText = selected ? Loc.Get(Strings.Detail.Tuning.Current) : null,
                });
            }
            if (PlaylistTuneMenuModel.ResetOption(tuning) is { } reset)
            {
                items.Add(MenuFlyoutItem.Separator);
                items.Add(new MenuFlyoutItem(
                    Loc.Get(Strings.Detail.Tuning.Reset),
                    Invoke: () => Apply(reset.Identifier)));
            }
            return items;
        }

        void Toggle()
        {
            if (busy || overlay is null) return;
            // Using the command IS the acknowledgement the teaching tip was asking for — burn the id and take the tip
            // down before the menu opens over it. No-op when no tip is up.
            WaveeTips.Acknowledge(services?.Settings, WaveeTipIds.DetailTuning);
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => TuneFlyout(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                ToolFx.MenuPopup);
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        Element TuneFlyout(IReadOnlyList<MenuFlyoutItem> items, Action close)
        {
            ColorF iconColor = Tok.AccentTextPrimary;
            return new BoxEl
            {
                Direction = 1,
                MinWidth = 336f,
                MaxWidth = 336f,
                Padding = new Edges4(0f, 8f, 0f, 6f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Gap = 12f,
                        AlignItems = FlexAlign.Center,
                        Padding = new Edges4(14f, 9f, 14f, 11f),
                        Children =
                        [
                            new BoxEl
                            {
                                Width = 36f,
                                Height = 36f,
                                AlignItems = FlexAlign.Center,
                                Justify = FlexJustify.Center,
                                Corners = CornerRadius4.All(10f),
                                Fill = iconColor with { A = 0.13f },
                                Children =
                                [
                                    Icon(Icons.RefineSparkle, 18f, iconColor),
                                ],
                            },
                            new BoxEl
                            {
                                Direction = 1,
                                Gap = 2f,
                                Grow = 1f,
                                Basis = 0f,
                                Children =
                                [
                                    // BodyStrong / Caption — the flyout's title+subtitle pair on the ramp (was 14/650).
                                    Ui.BodyStrong(Loc.Get(Strings.Detail.Tuning.FlyoutTitle)),
                                    Ui.Caption(Loc.Get(Strings.Detail.Tuning.FlyoutSubtitle)) with { Wrap = TextWrap.Wrap },
                                ],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Height = 1f,
                        Margin = new Edges4(8f, 3f, 8f, 4f),
                        Fill = Tok.StrokeDividerDefault,
                    },
                    MenuFlyout.Create(items, close, 336f),
                ],
            };
        }

        ColorF accent = Tok.AccentTextPrimary;
        Element leading = busy
            ? ProgressRing.Indeterminate(16f, true, accent)
            : Icon(Icons.RefineSparkle, 16f, active ? accent : Tok.TextSecondary);
        Element button = new BoxEl
        {
            Direction = 0,
            Width = _labeled ? float.NaN : 32f,
            Height = 32f,
            Padding = _labeled ? new Edges4(9f, 0f, 10f, 0f) : default,
            Gap = 6f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            Fill = active ? accent with { A = 0.11f } : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.17f } : Tok.FillSubtleSecondary,
            PressedFill = active ? accent with { A = 0.08f } : Tok.FillSubtleTertiary,
            HoverDurationMs = Motion.ControlFaster,
            PressDurationMs = Motion.ControlFaster,
            IsEnabled = !busy,
            Role = AutomationRole.Button,
            Focusable = true,
            Cursor = busy ? CursorId.Arrow : CursorId.Hand,
            OnClick = Toggle,
            OnRealized = h => anchor.Value = h,
            Children = _labeled
                ?
                [
                    leading,
                    new TextEl(Loc.Get(Strings.Detail.Tuning.Tune))
                    { Size = 12f, Weight = 600, Color = active ? accent : Tok.TextSecondary },
                    Icon(Icons.ChevronDown, 9f, active ? accent : Tok.TextTertiary),
                ]
                : [leading],
        };
        return _labeled ? button : ToolTip.Wrap(button, Loc.Get(Strings.Detail.Tuning.Tooltip));
    }

}

sealed class SortMenuButton : Component
{
    readonly IReadSignal<DetailTrackSort> _sort;
    readonly Action<DetailTrackSort> _setSort;
    readonly bool _hasAlbum, _hasDate, _labeled;
    // A PREDICATE, not a bool, unlike hasAlbum/hasDate: those are facts about the SURFACE (frozen with the rest of this
    // component's props at mount, per component-props-contract), while the Plays lane is a live user toggle that has to
    // reach the menu the next time it OPENS — not the next time this button remounts.
    readonly Func<bool>? _hasPlays;
    public SortMenuButton(IReadSignal<DetailTrackSort> sort, Action<DetailTrackSort> setSort, bool hasAlbum, bool hasDate,
                          bool labeled = false, Func<bool>? hasPlays = null)
    { _sort = sort; _setSort = setSort; _hasAlbum = hasAlbum; _hasDate = hasDate; _labeled = labeled; _hasPlays = hasPlays; }

    internal static string Label(SortColumn c) => c switch
    {
        SortColumn.Index => Loc.Get(Strings.Detail.Sort.CustomOrder),
        SortColumn.Title => Loc.Get(Strings.Detail.Sort.Title),
        SortColumn.Artist => Loc.Get(Strings.Detail.Sort.Artist),
        SortColumn.Album => Loc.Get(Strings.Detail.Sort.Album),
        SortColumn.DateAdded => Loc.Get(Strings.Detail.Sort.DateAdded),
        SortColumn.Duration => Loc.Get(Strings.Detail.Sort.Duration),
        SortColumn.Plays => Loc.Get(Strings.Detail.Column.Plays),
        _ => "",
    };

    internal static IReadOnlyList<MenuFlyoutItem> ItemsFor(
        IReadSignal<DetailTrackSort> sort, Action<DetailTrackSort> setSort, bool hasAlbum, bool hasDate, bool hasPlays = false)
    {
        var cur = sort.Peek();
        var fields = new List<SortColumn>(7) { SortColumn.Index, SortColumn.Title, SortColumn.Artist };
        if (hasAlbum) fields.Add(SortColumn.Album);
        if (hasDate) fields.Add(SortColumn.DateAdded);
        // Offered only while the Plays lane is actually VISIBLE (album profiles always, playlist/Liked behind the
        // opt-in): sorting by a column the reader cannot see reorders the list for no stated reason, and on a playlist
        // the counts are not even filled until the column is on.
        if (hasPlays) fields.Add(SortColumn.Plays);
        fields.Add(SortColumn.Duration);

        var items = new List<MenuFlyoutItem>(fields.Count + 3);
        foreach (var col in fields)
            items.Add(MenuFlyoutItem.RadioItem(Label(col), cur.Column == col,
                () => setSort(col == SortColumn.Index ? DetailTrackSort.Default : new DetailTrackSort(col, cur.Descending))));

        // Direction applies to original/custom order too: descending is the explicit "invert this list" operation.
        items.Add(MenuFlyoutItem.Separator);
        items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Sort.Ascending), !cur.Descending,
            () => setSort(sort.Peek() with { Descending = false })));
        items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Sort.Descending), cur.Descending,
            () => setSort(sort.Peek() with { Descending = true })));
        return items;
    }

    IReadOnlyList<MenuFlyoutItem> Items() => ItemsFor(_sort, _setSort, _hasAlbum, _hasDate, _hasPlays?.Invoke() ?? false);

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var current = _sort.Value;   // subscribe: active field + direction survive into the pinned/shy projection
        bool active = current.Column != SortColumn.Index;

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        Element trailing = new BoxEl
        {
            Direction = 0, Gap = 3f, AlignItems = FlexAlign.Center,
            Children = active
                ? [Embed.Comp(() => new SortCaret(_sort)), Icon(Icons.ChevronDown, 8f, Tok.TextTertiary)]
                : [Icon(Icons.ChevronDown, 8f, Tok.TextTertiary)],
        };
        return _labeled
            ? ToolFx.LabeledButton(Icons.Sort,
                Label(current.Column),
                active, Toggle, h => anchor.Value = h,
                trailing)
            : ToolFx.Button(Icons.Sort, active, Toggle, h => anchor.Value = h);
    }
}

// Shared chrome for the track-list toolbar buttons: the 32px icon button (with the accent "on" pill when active) and the
// flyout panel surface. Keeps Filter/Sort/More/List visually identical.
static class ToolFx
{
    // Real SplitButton behavior with AppBar/CommandBar visuals: two hit targets and keyboard chords remain intact, but
    // the joined root is ghosted at rest instead of painting the boxed form-control surface.
    public static readonly TemplateParts CommandBarSplitParts = new()
    {
        [SplitButton.PartRoot] = static e => e with
        {
            AlignSelf = FlexAlign.Center,
            MinHeight = 32f,
            Fill = ColorF.Transparent,
            BorderWidth = 0f,
            BorderBrush = null,
            Corners = Radii.ControlAll,
        },
        [SplitButton.PartPrimaryButton] = static e => e with
        {
            Grow = 0f,
            MinWidth = 0f,
            Height = 32f,
            Padding = new Edges4(9f, 0f, 8f, 0f),
        },
        [SplitButton.PartSecondaryButton] = static e => e with
        {
            Width = 24f,
            Height = 32f,
            Padding = default,
            Justify = FlexJustify.Center,
        },
        [SplitButton.PartDivider] = static e => e with
        {
            Width = 1f,
            Height = 20f,
            AlignSelf = FlexAlign.Center,
            Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
        },
    };

    // The toolbar icon button. Active → a subtle accent pill + accent glyph (the WinUI ToggleButton "on" look); idle → ghost.
    public static Element Button(string glyph, bool active, Action onClick, Action<NodeHandle> onRealized)
    {
        ColorF accent = Tok.AccentTextPrimary;
        Prop<ColorF> ink = active
            ? Prop.Of(static () => Tok.AccentTextPrimary)
            : Prop.Of(static () => Tok.TextSecondary);
        return new BoxEl
        {
            Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            Fill = active
                ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f })
                : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.17f } : Tok.FillSubtleSecondary,
            PressedFill = active ? accent with { A = 0.08f } : Tok.FillSubtleTertiary,
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            OnClick = onClick, OnRealized = onRealized,
            Children = [Ui.Icon(glyph, 14f) with { Color = ink }],
        };
    }

    // The labeled (icon + text) command-bar form of Button — the wide-layout variant; opens the same flyout on click and
    // carries the same accent "on" ramp so an active filter/sort reads identically to the icon-only form.
    public static Element LabeledButton(string glyph, string label, bool active, Action onClick,
                                        Action<NodeHandle> onRealized, Element? trailing = null)
    {
        ColorF accent = Tok.AccentTextPrimary;
        Prop<ColorF> ink = active
            ? Prop.Of(static () => Tok.AccentTextPrimary)
            : Prop.Of(static () => Tok.TextSecondary);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Height = 32f,
            Padding = new Edges4(9f, 0f, 10f, 0f),
            Corners = CornerRadius4.All(Radii.Control),
            Fill = active
                ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f })
                : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.17f } : Tok.FillSubtleSecondary,
            PressedFill = active ? accent with { A = 0.08f } : Tok.FillSubtleTertiary,
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            OnClick = onClick, OnRealized = onRealized,
            Children = trailing is null
                ?
                [
                    Ui.Icon(glyph, 14f) with { Color = ink },
                    new TextEl(label) { Size = 12f, Weight = 600, Color = ink },
                ]
                :
                [
                    Ui.Icon(glyph, 14f) with { Color = ink },
                    new TextEl(label) { Size = 12f, Weight = 600, Color = ink },
                    trailing,
                ],
        };
    }

    // A vertical group separator (the WinUI AppBarSeparator) — a 1px divider between command groups in the bar.
    public static Element Separator() => new BoxEl
    {
        Width = 1f, Height = 20f, AlignSelf = FlexAlign.Center,
        Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
        Margin = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
    };

    public static PopupOptions MenuPopup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss)
    { ConstrainToRootBounds = false };
    public static PopupOptions RichPopup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
    { ConstrainToRootBounds = false };
}

// Search and Filter share one outer field surface. The editor is chromeless and the stable FilterButton occupies the
// trailing 32-DIP lane, so expanding/collapsing never remounts or visually ejects the funnel from the control.
readonly record struct TrackFilterCapabilities(
    bool HasVideo, bool HasDateAdded, bool HasMixedOrigin, bool HasUnavailable, bool HasLibrary,
    // At least one track carries a kind-222 tempo. The BPM/Key facets are offered only then — on a list with no
    // enrichment they would silently match nothing, which reads as a broken filter rather than an empty result.
    bool HasTempo = false);

sealed class DetailTrackSearchField : Component
{
    readonly Signal<string> _query;
    readonly Signal<bool> _focused;
    readonly bool _focusOnMount;
    readonly bool _canCollapse;
    readonly Action<bool> _collapse;

    public DetailTrackSearchField(Signal<string> query, Signal<bool> focused,
        bool focusOnMount, bool canCollapse, Action<bool> collapse)
    {
        _query = query; _focused = focused; _focusOnMount = focusOnMount;
        _canCollapse = canCollapse; _collapse = collapse;
    }

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        bool hasQuery = _query.Value.Length > 0;
        var hadQuery = UseRef(hasQuery);
        var parts = new TemplateParts();
        if (_focusOnMount)
            parts[EditableText.PartRoot] = b => b with
            {
                OnRealized = h => post(() => hooks.FocusNode?.Invoke(h, false)),
            };

        UseEffect(() =>
        {
            bool hadText = hadQuery.Value;
            hadQuery.Value = hasQuery;
            if (hadText && !hasQuery && _canCollapse)
                post(() => _collapse(true));
        }, hasQuery);

        Element AffixButton(string glyph, string tip, Action click) => ToolTip.Wrap(new BoxEl
        {
            Width = 26f, Height = 26f, AlignSelf = FlexAlign.Center,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            OnClick = click, Focusable = true, Role = AutomationRole.Button,
            Children = [Icon(glyph, 12f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle), tip);

        var affix = new List<Element>(1);
        if (hasQuery)
            affix.Add(AffixButton(Icons.ClearText, Loc.Get(Strings.Detail.Filter.Clear), () => _query.Value = ""));

        return Embed.Comp(() => new EditableText
        {
            Text = _query,
            Width = float.NaN,
            Height = 32f,
            Chromeless = true,
            Placeholder = Loc.Get(Strings.Detail.Filter.SearchThisList),
            LeftAffix = new BoxEl
            {
                Width = 28f, Height = 32f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Children = [Icon(Icons.Search, 13f, Tok.TextTertiary)],
            },
            RightAffix = new BoxEl
            {
                Direction = 0, Gap = 1f, Height = 32f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(0f, 0f, 3f, 0f), Children = affix.ToArray(),
            },
            OnCancel = _canCollapse ? () => _collapse(true) : null,
            OnFocusChanged = focused =>
            {
                _focused.Value = focused;
                if (!focused && _canCollapse && _query.Peek().Length == 0)
                    post(() => _collapse(false));
            },
            Parts = parts,
        });
    }
}

/// <summary>Live props for <see cref="FilterButton"/>. Capabilities change MID-SESSION as enrichment lands (a kind-222
/// tempo arriving adds the Tempo facet), so they must reach the component through the props channel — never as a frozen
/// ctor field, and never baked into the <c>Key</c>: a remount would drop the anchor/overlay refs and orphan an OPEN
/// flyout. See docs/design/subsystems/component-props-contract.md.</summary>
/// <summary><paramref name="TextAction"/> renders the button as the context band's plateless text action instead of
/// the 32-DIP funnel square — same flyout, same state, a word instead of a glyph, because the band carries no plates.</summary>
sealed record FilterButtonProps(TrackFilterCapabilities Caps, bool TextAction = false);

sealed class FilterButton : Component
{
    readonly IReadSignal<TrackFilterState> _filters;
    readonly Action<TrackFilterState> _setFilters;
    public FilterButton(IReadSignal<TrackFilterState> filters, Action<TrackFilterState> setFilters)
    { _filters = filters; _setFilters = setFilters; }

    /// <summary>Park the count pill in the button's top-right corner. Declarative on both axes now that a ZStack
    /// aligns horizontally too — and the pill is content-sized (a two-digit count widens it), which is exactly the
    /// auto-sized-and-aligned case the stack resolves to the desired width rather than stretching.</summary>
    static readonly TemplateParts BadgeCorner = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            AlignSelf = FlexAlign.Start,        // top …
            JustifySelf = FlexAlign.End,        // … right
            BorderWidth = 1.5f,
            BorderColor = Tok.FillSolidBase,    // surface ring: keeps the accent pill off the funnel's ink
        },
    };

    // Checkable menu items (WinUI AppBarToggleButton-in-menu): the ✓ shows the live flag; clicking toggles it.
    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var props = UsePropsOrDefault<FilterButtonProps>();
        var caps = props?.Caps ?? default;
        bool asText = props?.TextAction == true;
        var current = _filters.Value;
        int activeCount = current.ActiveCount;
        // ActiveCount increments for EVERY non-default facet, so "has a count" and "is not the default state" are the
        // same predicate — one flag, and the badge can never disagree with the accent plate.
        bool active = activeCount > 0;
        ColorF accent = Tok.AccentTextPrimary;

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new TrackFilterFlyout(_filters, _setFilters, caps)),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // The context band's arm: a word, with ACCENT ink standing in for the accent plate + count badge. "Some
        // filters are on" is the only state the collapsed affordance ever needed to carry — the exact count is one
        // click away inside the flyout, and a numeral floating beside a bold word in a typographic bar reads as a
        // defect rather than as a badge.
        if (asText)
            return ToolTip.Wrap(
                WaveeCta.TextAction(Loc.Get(Strings.Detail.Filter.Short), Toggle, toggledOn: active) with
                {
                    OnRealized = h => anchor.Value = h,
                },
                Loc.Get(Strings.Detail.Filter.Title));

        // Mirror the WinUI TextControlButton affix (EditableText.InnerButton): Width 30 + the inner-button margin 0,4,4,4,
        // and NO explicit height / AlignSelf — the field's affix row (AlignItems=Stretch) fills it to full height, while
        // AlignItems/Justify=Center centers the glyph. Accent plate + count badge when a filter is active.
        Element glyph = new BoxEl
        {
            Width = 14f,
            Height = 14f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            // Segoe Fluent's funnel has more ink above its em-box midpoint than below it; the one-DIP optical offset
            // aligns the visible funnel with the adjacent Search glyph, whose outline is vertically symmetrical.
            // With a badge in the top-right corner the glyph steps down-left so the two never share ink. These are the
            // DECOMPOSED offsets, which the reconciler composes into LocalTransform.
            OffsetX = active ? -4f : 0f,
            OffsetY = active ? 4f : 1f,
            // Accent is the PLATE's and the BADGE's colour, never the glyph's: an accent funnel touching an accent
            // pill reads as one blob rather than an icon with a count on it.
            Children = [Icon(Icons.Filter, 14f, active ? Tok.TextPrimary : Tok.TextSecondary)],
        };
        Element[] visual = active
            ? [glyph, InfoBadge.Count(activeCount, parts: BadgeCorner)]
            : [glyph];

        return ToolTip.Wrap(new BoxEl
        {
            ZStack = true,
            Width = 32f, Height = 32f, AlignSelf = FlexAlign.Center,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleSecondary,
            PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
            OnClick = Toggle, OnRealized = h => anchor.Value = h,
            Children = visual,
        }, Loc.Get(Strings.Detail.Filter.Title));
    }

}

// The List button: opens a flyout with a stepped slider (Compact · Default · Cozy · Comfortable) controlling row height.
sealed class DetailTrackMoreButton : Component
{
    readonly Loadable<DetailModel> _full;
    readonly DetailHandlers _h;
    readonly DetailConfig _cfg;
    readonly DetailTrackInlineCommand _overflow;
    readonly SelectionModel _selection;

    public DetailTrackMoreButton(Loadable<DetailModel> full, DetailHandlers h, DetailConfig cfg,
        DetailTrackInlineCommand overflow, SelectionModel selection, bool vertical)
    {
        _full = full; _h = h; _cfg = cfg; _overflow = overflow; _selection = selection;
    }

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var pickerHandle = UseRef<OverlayHandle?>(null);

        IReadOnlyList<MenuFlyoutItem> Items()
        {
            var model = _full.Value.Peek();
            var items = new List<MenuFlyoutItem>(10);
            if ((_overflow & DetailTrackInlineCommand.Shuffle) != 0)
                items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Shuffle), Icons.Shuffle, Invoke: _h.Shuffle));
            if ((_overflow & DetailTrackInlineCommand.Sort) != 0)
                items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Detail.Sort.Title),
                    SortMenuButton.ItemsFor(_h.Sort, _h.SetSort, _cfg.ShowAlbumColumn, model.HasDateAdded,
                        _cfg.ShowPlays || (_cfg.PlaysColumnOptIn && (_h.PlaysColumn?.Peek() ?? false))),
                    new IconRef { Glyph = Icons.Sort }));
            if ((_overflow & DetailTrackInlineCommand.Density) != 0)
                items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Detail.Density.RowSize),
                    ListButton.ItemsFor(_h.Density, _h.SetDensity), new IconRef { Glyph = Icons.List }));
            // Offered only where the surface can actually host the column; the data itself is always in the expander.
            if (_cfg.ShowTempo)
            {
                bool tempoOn = _h.TempoColumn.Peek();
                items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.TempoColumn), tempoOn,
                    () => _h.SetTempoColumn(!tempoOn)));
            }
            // Same shape for the Plays lane, offered only where the profile does not already carry one (playlist /
            // Liked). Turning it on is also what authorises the kind-185 fill for this list — see DetailShell.
            if (_cfg.PlaysColumnOptIn && _h.PlaysColumn is not null && _h.SetPlaysColumn is not null)
            {
                bool playsOn = _h.PlaysColumn.Peek();
                items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.PlaysColumn), playsOn,
                    () => _h.SetPlaysColumn(!playsOn)));
            }
            if ((_overflow & DetailTrackInlineCommand.Select) != 0
                && _h.MultiSelect is not null && _h.SetMultiSelect is not null)
            {
                bool selected = _h.MultiSelect.Peek();
                items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Select), selected, () =>
                {
                    if (selected) _selection.ClearSelection();
                    _h.SetMultiSelect(!selected);
                }));
            }
            if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);

            bool copy = _cfg.Heart == HeartMode.Follow || LikedSongsArtwork.IsLikedUri(model.ContextUri);
            items.Add(new MenuFlyoutItem(Loc.Get(copy ? Strings.Detail.CopyToPlaylist : Strings.Detail.AddToPlaylist),
                new IconRef { Glyph = Icons.Add },
                Invoke: () =>
                {
                    if (overlay is not null)
                        PlaylistPickerLauncher.OpenFlyout(
                            overlay, () => anchor.Value, () => _full.Value.Peek().Tracks, pickerHandle);
                }));
            return items;
        }

        void Toggle()
        {
            if (overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight, ToolFx.MenuPopup);
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(ToolFx.Button(Icons.More, false, Toggle, h => anchor.Value = h),
            Loc.Get(Strings.Common.More));
    }
}

sealed class ListButton : Component
{
    readonly IReadSignal<int> _density;
    readonly Action<int> _setDensity;
    readonly bool _labeled;
    public ListButton(IReadSignal<int> density, Action<int> setDensity, bool labeled = false) { _density = density; _setDensity = setDensity; _labeled = labeled; }

    internal static string Label(int d) => d switch { 0 => Loc.Get(Strings.Detail.Density.Compact), 2 => Loc.Get(Strings.Detail.Density.Cozy), 3 => Loc.Get(Strings.Detail.Density.Comfortable), _ => Loc.Get(Strings.Detail.Density.Default) };

    internal static IReadOnlyList<MenuFlyoutItem> ItemsFor(IReadSignal<int> density, Action<int> setDensity)
    {
        int current = density.Peek();
        var items = new List<MenuFlyoutItem>(4);
        for (int i = 0; i < 4; i++)
        {
            int value = i;
            items.Add(MenuFlyoutItem.RadioItem(Label(value), current == value, () => setDensity(value)));
        }
        return items;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        int current = _density.Value;
        // The label this button showed when its flyout OPENED. The flyout is right-aligned to this button, and the
        // toolbar flows left→right, so relabelling "Comfortable" → "Cozy" mid-drag narrows the button, moves its right
        // edge, re-anchors the flyout sideways under the stationary pointer, and the thumb then reads a different
        // fraction — the value bounced back and forth between levels. While the flyout is open the button keeps the
        // opening label (the flyout's own caption is the live readout); it catches up the moment the flyout closes.
        var frozenLabel = UseRef<string?>(null);
        var closedEpoch = UseSignal(0);
        _ = closedEpoch.Value;   // subscribe: the close must re-render this button so the frozen label is released

        Element Content() => Embed.Comp(() => new DensityPanel(_density, _setDensity));

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            frozenLabel.Value = Label(_density.Peek());
            handle.Value = svc.Open(() => anchor.Value, Content, FlyoutPlacement.BottomEdgeAlignedRight, ToolFx.RichPopup);
            handle.Value.ClosedAction = () => { handle.Value = null; frozenLabel.Value = null; closedEpoch.Value++; };
        }
        string label = handle.Value is { IsOpen: true } && frozenLabel.Value is { } frozen ? frozen : Label(current);
        // Never accent — density is a view preference, not an active filter/sort (matches the reasoning in the design).
        return _labeled
            ? ToolFx.LabeledButton(Icons.RowSize, label, false, Toggle, h => anchor.Value = h,
                Icon(Icons.ChevronDown, 8f, Tok.TextTertiary))
            : ToolFx.Button(Icons.RowSize, false, Toggle, h => anchor.Value = h);
    }
}

// The density flyout's body — its own Component so the slider + label re-render as the value changes during a drag.
sealed class DensityPanel : Component
{
    readonly IReadSignal<int> _density;
    readonly Action<int> _setDensity;
    public DensityPanel(IReadSignal<int> density, Action<int> setDensity) { _density = density; _setDensity = setDensity; }

    public override Element Render()
    {
        int d = _density.Value;   // subscribe → the slider thumb + label track the value
        // The unified Slider.Create takes a FloatSignal; mirror the external int density into one (synced via an effect
        // keyed on d) so the thumb rides the compositor bind and still follows external density changes.
        var dv = UseFloatSignal(d);
        UseEffect(() => { dv.Value = d; return null; }, d);
        return Layer(Edges4.All(Spacing.M),
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 240f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            // BodyStrong label + Caption value — the ramp's control-label pair (was 13/700).
                            Ui.BodyStrong(Loc.Get(Strings.Detail.Density.RowSize)) with { Grow = 1f },
                            Ui.Caption(ListButton.Label(d)),
                        ],
                    },
                    Slider.Create(dv, v => _setDensity(Math.Clamp((int)MathF.Round(v), 0, 3)),
                        // Step=1 → snaps to each level. The thumb tip names the level (WinUI ThumbToolTipValueConverter)
                        // instead of showing the raw 0–3 index the slider would print by default.
                        new Slider.SliderOptions
                        {
                            Min = 0f, Max = 3f, Step = 1f, TickFrequency = 1f,
                            ThumbToolTipValueConverter = v => ListButton.Label(Math.Clamp((int)MathF.Round(v), 0, 3)),
                        },
                        length: 216f),
                ],
            });
    }
}
