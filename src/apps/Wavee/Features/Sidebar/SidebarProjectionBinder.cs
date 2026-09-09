using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// THE ENTRY-PROJECTION DRIVER — Wave 1 built the whole pure pipeline and the <c>SidebarPreferences.Entries</c> cell, and
/// left nothing driving them. This is that driver, and (M1) the resolver that turns every
/// <c>SidebarSectionKind.Extension</c> section into planner-ready row slices.
///
/// <para><b>What it owns.</b> One unified projection over <c>LibraryStore</c>'s warm cells + <c>HistoryStore</c> recency +
/// the play log + the pin store, rebuilt whenever any of them moves; the V3-shaped published entry list; the first-seen
/// commit; the contribution slices; and the <see cref="CurrentInput"/> a Curated pane hands to
/// <c>SidebarRowPlanner.Build</c>.</para>
///
/// <para><b>Impure by design.</b> Every DECISION lives in the engine-free half (<c>SidebarBinderPipeline</c>,
/// <c>SidebarSourceMap</c>, <c>SidebarProjection</c>, <c>SidebarSort</c>) so the tests drive the real rules; this class is
/// the subscription/store/signal shell around them — the same split as <c>SidebarPreferences</c> over
/// <c>SidebarPaneState</c>.</para>
///
/// <para><b>THREADING: UI thread only</b>, unsynchronized. Every signal write happens either inside
/// <see cref="Sync"/> (called from the pump's <c>UseEffect</c> — after render, never during it) or inside a callback
/// marshalled through the <c>post</c> handed to <see cref="Start"/>. A source that completes a fetch on a pool thread MUST
/// come back through that <c>post</c>; <see cref="WaveeBuiltInDataSources.Attach"/> hands it to every source that needs
/// one. Re-entrancy is fenced: a source that raises Changed while a rebuild is running only marks the binder dirty.</para>
///
/// <para><b>Why a mounted pump.</b> A <c>ReactiveRuntime</c> is not reachable from a plain service (the note in
/// <c>SidebarPreferences.SwitchDesign</c>), so a service cannot own an <c>Effect</c> and cannot observe a
/// <c>Signal&lt;T&gt;</c> — <c>Signal</c> has no imperative Subscribe. <see cref="MountPoint"/> therefore returns a
/// zero-size always-mounted component (the <c>SidebarOnboardingChrome</c> / <c>ActionServicesOverlayBinder</c> precedent)
/// that READS every trigger signal in its render (subscription only — no work) and calls <see cref="Sync"/> from a
/// <c>UseEffect</c> keyed on their fold. Mount it ONCE at the app root, not inside the sidebar: the docked pane and the
/// narrow drawer come and go, the projection may not.</para>
/// </summary>
public sealed class SidebarProjectionBinder : ISidebarProjectionSnapshot
{
    /// <summary>How many navigation/playback rows the recency feeds keep. Far more than any "top 3–8" section needs, and
    /// it bounds the per-rebuild work regardless of how long the logs are.</summary>
    public const int RecencyCap = 40;

    readonly SidebarPreferences _prefs;
    readonly IQueryService _queries;
    CatalogScope _scope;
    QuerySignalBinding<LibraryQuerySnapshot>? _libraryQuery;
    static readonly LibraryQuerySnapshot EmptyLibrary = new([], [], new LibraryStats(0, 0, 0, 0),
        new Dictionary<string, long>());
    readonly Dictionary<string, PinQuery> _pinQueries = new(StringComparer.Ordinal);
    readonly Dictionary<object, HashSet<string>> _pinDemand = new();
    sealed record PinQuery(string Uri, IQuerySignalBinding Binding, Func<SidebarLibraryEntry?> Read);
    readonly PlayLogStore? _playLog;
    readonly PlaybackBridge? _playback;

    // Rebuild buffers — allocated once, reused forever (the F.7.5 allocation contract).
    readonly List<SidebarLibraryEntry> _all = new(256);       // the full projection, source order (planner Library)
    readonly List<SidebarLibraryEntry> _tree = new(128);      // the flattened rootlist tree (planner PlaylistTree)
    readonly List<SidebarLibraryEntry> _pinRows = new(16);    // resolved pins, in pin order
    readonly List<SidebarLibraryEntry> _unlistedPinRows = new(8);   // the subset the library projection does not contain
    readonly List<SidebarLibraryEntry> _visited = new(16);
    readonly List<SidebarLibraryEntry> _played = new(16);
    readonly List<SidebarLibraryEntry> _newReleases = new(8);
    readonly List<SidebarLibraryEntry> _concerts = new(8);
    readonly List<SidebarLibraryEntry> _extEntries = new(64); // every extension section's rows, back to back
    readonly List<SidebarLibraryEntry> _scratch = new(256);   // PinsFirst' partition buffer
    readonly List<SidebarVisit> _visits = new(64);            // HistoryStore → the engine-free visit shape
    readonly List<SidebarPlayedContext> _playedContexts = new(RecencyCap);
    readonly HashSet<string> _pinnedIds = new(StringComparer.Ordinal);
    readonly List<string> _liveIds = new(256);
    readonly SidebarSourceIndex _index = new();
    readonly SidebarExtensionSlices _slices = new();
    readonly SidebarContributionCache _cache = new();
    readonly Dictionary<string, SidebarSourceState> _observedSourceStates = new(StringComparer.Ordinal);
    readonly HashSet<string> _staleSourceIds = new(StringComparer.Ordinal);
    readonly Signal<int> _sourceEpoch = new(0);
    readonly Func<string, bool> _isFolderExpanded;
    readonly Action _syncAction;
    readonly Action _onSourceChanged;
    Action<Action>? _post;
#if DEBUG || FLUENTGPU_DIAG
    readonly Action<string> _onSourceChangedWithId;
#endif

    SidebarFirstSeen? _firstSeen;
    ISidebarContributionHost? _host;
    SidebarDataSourceTable? _table;
    HistoryStore? _history;
    // The pump's debounced V3Search (SidebarBinderPump.Render → UseDebouncedValue), attached once from its attach
    // effect (never from render — AttachSearch itself does no signal work, it just swaps which signal Read/Rebuild
    // peek). Null until the pump's first effect runs; Read/Rebuild fall back to prefs.V3Search directly until then.
    IReadSignal<string>? _effectiveSearch;
    Action? _detachSources;
    readonly List<ISidebarDataSourceDemandLifecycle> _demandSources = [];
    SidebarProjectionInput _input;
    SidebarBinderTriggers _lastTriggers;
    SidebarSourceState _libraryState = SidebarSourceState.Pending;
    SidebarSourceState _treeState = SidebarSourceState.Pending;
    int _playedRevision = -1;
    int _revision;
    bool _started;
    bool _active = true;
    bool _rebuilding;
    bool _dirty = true;

#if DEBUG || FLUENTGPU_DIAG
    // The pump is the only computation subscribed to the projection inputs. Comparing its folded lanes therefore
    // identifies the producer that keeps the entire sidebar alive without a global Signal<T> subscription ledger.
    static readonly bool s_binderDiag = Diag.EnvFlag("WAVEE_SIDEBAR_BINDER_DIAG");
    SidebarBinderTriggers _diagLastTriggers;
    string _diagLastChange = "initial";
    long _diagLastReportMs;
    int _diagPumpRenders;
    int _diagTriggerChanges;
    int _diagRebuilds;
    int _diagSourceNotifications;
    Dictionary<string, int>? _diagSourceCounts;
    bool _diagHaveTriggers;
#endif

    public SidebarProjectionBinder(SidebarPreferences prefs, IQueryService queries, CatalogScope scope,
                                  PlayLogStore? playLog = null, PlaybackBridge? playback = null)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _scope = scope;
        _playLog = playLog;
        _playback = playback;
        // Cached delegates: a rebuild must not allocate a closure per pass.
        _isFolderExpanded = prefs.IsFolderExpanded;
        _syncAction = () => { SyncPinQueries(); Sync(); };
        _onSourceChanged = OnSourceChanged;
#if DEBUG || FLUENTGPU_DIAG
        _onSourceChangedWithId = OnSourceChanged;
#endif
    }

    // ─────────────────────────────────── wiring ───────────────────────────────────

    /// <summary>The contribution host every Extension section resolves through — <c>WaveeBuiltInDataSources.ContributionHost</c>
    /// in M1, M3's sandboxed host later. Nothing else in the app may look a contribution up.</summary>
    /// <param name="sources">The first-party table behind <paramref name="host"/>, for the built-in feed slices and
    /// <see cref="StateOf"/>. Omit it when the host IS the table.</param>
    public void UseHost(ISidebarContributionHost? host, SidebarDataSourceTable? sources = null)
    {
        _host = host;
        _table = sources ?? host as SidebarDataSourceTable;
        _demandSources.Clear();
        if (_table is not null)
            foreach (var source in _table.All)
                if (source is ISidebarDataSourceDemandLifecycle lifetime) _demandSources.Add(lifetime);
        Invalidate();
    }

    /// <summary>Attach the navigation log. It is created by <c>WaveeShell</c> (not <c>Services</c>), so the binder is
    /// constructed without one and becomes recency-aware the moment the shell mounts. Until then the visited feed is
    /// simply empty — never pending, never an error.</summary>
    public void AttachHistory(HistoryStore? history)
    {
        if (ReferenceEquals(_history, history)) return;
        _history = history;
        Invalidate();
        if (_started) Sync();
    }

    /// <summary>Wire the pump's debounced <c>V3Search</c> (90 ms — <c>SidebarBinderPump.SearchDebounceMs</c>) as the
    /// text <see cref="Read(bool,HistoryStore?,IReadSignal{string}?)"/>'s <c>SearchHash</c> term and <see cref="Rebuild"/>
    /// both read, so a rebuild fired by an unrelated trigger mid-typing sees the SAME (debounced) text the trigger
    /// fold saw — never the raw keystroke-by-keystroke preference. Idempotent: called every time the pump's attach
    /// effect runs (same instance each time unless the pump itself remounts).</summary>
    internal void AttachSearch(IReadSignal<string> search) => _effectiveSearch = search;

    /// <summary>Single-scope call site (kept for the current caller shape — see the two-scope overload below for the
    /// account-gated rebind this now runs). Uses the binder's OWN previous scope as <c>previous</c>, which is exactly
    /// what a caller that has not yet been updated to hand in both scopes would mean anyway.</summary>
    public void RebindScope(CatalogScope scope) => RebindScope(_scope, scope);

    /// <summary>Idempotent start: capture the UI-thread marshaller, warm the cheap library cells, hand the marshaller to
    /// every source that owns async work, subscribe their Changed, and do the first rebuild.
    ///
    /// <para>Required-change C: re-acquiring the library query and every pin query is now GATED on
    /// <see cref="ScopeRebindRules.RequiresReacquire"/> — a same-account session merely confirming itself (a
    /// <c>ContextKnown</c> flip alone) keeps the existing handles instead of disposing and re-fetching them.</para></summary>
    public void RebindScope(CatalogScope previous, CatalogScope next)
    {
        if (_scope == next) return;
        bool reacquire = ScopeRebindRules.RequiresReacquire(previous, next);
        _scope = next;
        if (reacquire)
        {
            _libraryQuery?.Dispose(); _libraryQuery = null;
            foreach (var query in _pinQueries.Values) query.Binding.Dispose();
            _pinQueries.Clear();
            if (_started && _post is { } post)
            {
                _libraryQuery = new(_queries.Acquire(new SidebarLibraryQuery(next)), post, _ => OnSourceChanged());
                _libraryQuery.SetDemand(QueryDemand.Initial);
                _libraryQuery.SetActive(_active);
            }
        }
        Invalidate(); Sync(); SyncPinQueries();
    }

    public void Start(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
        if (_started) { Invalidate(); Sync(); return; }
        _started = true;
        _active = true;

        _libraryQuery = new QuerySignalBinding<LibraryQuerySnapshot>(
            _queries.Acquire(new SidebarLibraryQuery(_scope)), post, _ => OnSourceChanged());
        _libraryQuery.SetDemand(QueryDemand.Initial);
        _libraryQuery.SetActive(true);
        SyncPinQueries();
        if (_table is not null)
        {
#if DEBUG || FLUENTGPU_DIAG
            _detachSources = s_binderDiag
                ? WaveeBuiltInDataSources.Attach(_table, post, _onSourceChangedWithId)
                : WaveeBuiltInDataSources.Attach(_table, post, _onSourceChanged);
#else
            _detachSources = WaveeBuiltInDataSources.Attach(_table, post, _onSourceChanged);
#endif
        }

        Invalidate();
        Sync();
    }

    /// <summary>Detach every source subscription. The binder itself stays usable (a later <see cref="Start"/> re-attaches).</summary>
    public void Stop()
    {
        _detachSources?.Invoke();
        _detachSources = null;
        _started = false;
        _libraryQuery?.Dispose();
        _libraryQuery = null;
        foreach (var pin in _pinQueries.Values) pin.Binding.Dispose();
        _pinQueries.Clear();
        _pinDemand.Clear();
    }

    void SetActive(bool active)
    {
        _active = active;
        _libraryQuery?.SetActive(active);
        foreach (var pin in _pinQueries.Values) pin.Binding.SetActive(active);
        foreach (var source in _demandSources) source.SetActive(active);
    }

    /// <summary>Mount ONCE at the app root: a zero-size component that subscribes every rebuild trigger and calls
    /// <see cref="Sync"/> after render. Never mount it inside the sidebar — the pane unmounts, the projection must not.</summary>
    public Element MountPoint() => Embed.Comp(() => new SidebarBinderPump(this));

    // ─────────────────────────────────── reads ───────────────────────────────────

    /// <summary>The planner input for the CURRENT projection — what a Curated pane hands to
    /// <c>SidebarRowPlanner.Build</c>/<c>BuildRail</c>. Its lists ALIAS the binder's buffers, so it is valid until the next
    /// rebuild (exactly the <c>UseMemo</c> lifetime the planner is built for). Key the memo on <see cref="Revision"/>.</summary>
    public SidebarProjectionInput CurrentInput => _input;

    /// <summary>Bumped once per completed rebuild — the planner's <c>DepKey</c> lane and the input's echoed revision.</summary>
    public int Revision => _revision;

    /// <summary>A source's live health (Error when nothing is registered under that id).</summary>
    public SidebarSourceState StateOf(string sourceId) => _table?.StateOf(sourceId) ?? SidebarSourceState.Error;

    /// <summary>Why a section renders a "Manage extension" placeholder (or that it does not).</summary>
    public SidebarContributionAvailability AvailabilityOf(string sectionId) => _slices.AvailabilityOf(sectionId);

    /// <summary>The last-good snapshot registry — M3's stale-badge seam. First-party sources are always live, so in M1 this
    /// only holds rows for a contributed source that started failing after serving some.</summary>
    public SidebarContributionCache ContributionCache => _cache;

    // ISidebarProjectionSnapshot — what the first-party sources read.
    IReadOnlyList<SidebarLibraryEntry> ISidebarProjectionSnapshot.All => _all;
    IReadOnlyList<SidebarLibraryEntry> ISidebarProjectionSnapshot.Tree => _tree;
    SidebarSourceIndex ISidebarProjectionSnapshot.Index => _index;
    SidebarSourceState ISidebarProjectionSnapshot.LibraryState => _libraryState;
    SidebarSourceState ISidebarProjectionSnapshot.TreeState => _treeState;
    IReadOnlyList<SidebarVisit> ISidebarProjectionSnapshot.Visits => _visits;
    IReadOnlyList<SidebarPlayedContext> ISidebarProjectionSnapshot.Played => _playedContexts;

    // ─────────────────────────────────── the rebuild gate ───────────────────────────────────

    /// <summary>Force the next <see cref="Sync"/> to rebuild even if no trigger moved (a new host, a new history store).</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>Rebuild iff a trigger moved (or <see cref="Invalidate"/> was called). Returns whether it rebuilt. Cheap
    /// enough to call unconditionally: the gate is one struct compare over peeked versions + reference epochs.</summary>
    public bool Sync()
    {
        if (_rebuilding) { _dirty = true; return false; }
        bool forced = _dirty;   // Invalidate() was called: something outside the trigger fold moved (new host, new
                                 // history store) — Rebuild must not trust the lane diff below to decide what to skip.
        var triggers = Read(subscribe: false);
        if (!forced && triggers == _lastTriggers) return false;
        var previous = _lastTriggers;
        _lastTriggers = triggers;
        _dirty = false;

        _rebuilding = true;
        try
        {
#if DEBUG || FLUENTGPU_DIAG
            if (s_binderDiag) _diagRebuilds++;
#endif
            Rebuild(in previous, in triggers, forced);
        }
        finally { _rebuilding = false; }

        // A source that fired Changed mid-rebuild (an inline fetch completion) only marked us dirty; settle now.
        // Always a FORCED settle: whatever changed mid-rebuild is not necessarily reflected in the trigger lanes
        // alone (the same reason Invalidate() forces above), so this pass takes the full path unconditionally.
        if (_dirty)
        {
            var settledPrevious = _lastTriggers;
            _dirty = false;
            _lastTriggers = Read(subscribe: false);
            _rebuilding = true;
            try
            {
#if DEBUG || FLUENTGPU_DIAG
                if (s_binderDiag) _diagRebuilds++;
#endif
                Rebuild(in settledPrevious, in _lastTriggers, forceFull: true);
            }
            finally { _rebuilding = false; }
        }
        return true;
    }

    // A source said its rows or health moved. DEFERRED on purpose: bumping the epoch marks the pump stale (it subscribed
    // to this signal), the host schedules a frame, and the rebuild happens in the pump's effect like every other one. That
    // keeps EVERY rebuild on one path — never inside an arbitrary source callback that might be mid-flush — and turns a
    // hypothetical "rebuild ⇒ notify ⇒ rebuild" cycle into at worst one frame-paced pass instead of a stack overflow.
    // (The sources' own SetHealth dedupe means a steady state notifies nothing at all.)
    void OnSourceChanged()
    {
        _dirty = true;
        if (_rebuilding) return;
        _sourceEpoch.Value = _sourceEpoch.Peek() + 1;
    }

#if DEBUG || FLUENTGPU_DIAG
    void OnSourceChanged(string sourceId)
    {
        if (s_binderDiag)
        {
            _diagSourceNotifications++;
            var counts = _diagSourceCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            counts.TryGetValue(sourceId, out int count);
            counts[sourceId] = count + 1;
        }
        OnSourceChanged();
    }
#endif

    // ─────────────────────────────────── the rebuild ───────────────────────────────────

    // Whether STAGE ONE (the full library re-projection: _all/_tree/_index + the V3 buffer's own Build pass) ran at
    // least once. Fields below cache its last output so a rebuild that skips it (see <see cref="Rebuild"/>) still has
    // something correct to publish.
    bool _hasRebuiltOnce;
    SidebarEntriesShape _lastShape;
    bool _lastQualifiers;

    /// <summary><paramref name="forceFull"/>: the caller (<see cref="Sync"/>) already knows this pass must not trust
    /// the lane diff — <see cref="Invalidate"/> was called (a new host/history store — nothing a trigger lane would
    /// ever move) or this is the mid-rebuild settle pass (a source changed while THIS rebuild was still running, so
    /// what exactly moved is not safe to infer from the two trigger snapshots alone).
    ///
    /// <para>Otherwise this stages the work behind which lane(s) actually moved: LIBRARY-relevant lanes (content,
    /// history/play-log recency, the V3 filter/sort/search/culture/folder state) rebuild <c>_all</c>/<c>_tree</c>/
    /// <c>_index</c> and the V3 buffer from scratch, exactly as before. A PINS-ONLY change (the common "pin/unpin
    /// one row" case) reuses that untouched projection and only re-resolves pins + re-shapes (filter/sort are
    /// idempotent re-runs over an already-filtered-and-sorted buffer; only the pins-first partition actually needs
    /// to move). A change in neither set (feed/source health, Curated layout, playback) reuses the last shape
    /// outright — the feeds/extensions/publish steps below already run unconditionally, which is what makes those
    /// rebuilds show up at all.</para></summary>
    void Rebuild(in SidebarBinderTriggers previous, in SidebarBinderTriggers current, bool forceFull)
    {
        var prefs = _prefs;

        bool libraryRelevant = forceFull || !_hasRebuiltOnce
            || current.LibraryEpoch != previous.LibraryEpoch
            || current.HistoryVersion != previous.HistoryVersion
            || current.PlayLogRevision != previous.PlayLogRevision
            || current.CultureEpoch != previous.CultureEpoch
            || current.FolderVersion != previous.FolderVersion
            || current.V3State != previous.V3State
            || current.SearchHash != previous.SearchHash
            || current.OrderVersion != previous.OrderVersion;
        bool pinsChanged = current.PinsVersion != previous.PinsVersion;

        // The raw inputs, PEEKED (a service is not a computation; reading Value here would subscribe nothing and
        // reading it during someone else's render would be a phantom dependency). Status can move independently of
        // LibraryEpoch only if the epoch's own fold missed it, which it does not (LibraryEpochOf folds Status too).
        var snapshot = _libraryQuery?.Snapshot.Peek();
        var library = snapshot?.Value ?? EmptyLibrary;
        _treeState = _libraryState = snapshot?.Status.HasPrimaryData == true
            ? SidebarSourceState.Ready : SidebarSourceState.Pending;

        bool v3 = prefs.Design.Peek() == SidebarDesign.LibraryV3;
        var filter = v3 ? (SidebarV3Filter)prefs.V3Filter.Peek() : SidebarV3Filter.All;
        var qualifier = v3 ? (SidebarV3Qualifier)prefs.V3Qualifier.Peek() : SidebarV3Qualifier.Any;
        var sort = (SidebarV3Sort)prefs.V3Sort.Peek();
        bool desc = prefs.V3Desc.Peek();
        // Rebuild has no render context, so this PEEKS the debounced signal the pump attached (AttachSearch) instead
        // of prefs.V3Search directly — the same text the trigger fold in Read(...) subscribed to, so a rebuild fired
        // by another trigger mid-typing can never run ahead of the debounced tick.
        string search = v3 ? SidebarSearch.Normalize((_effectiveSearch ?? (IReadSignal<string>)prefs.V3Search).Peek()) : "";

        SidebarEntriesShape shape;
        bool qualifiers;
        int newStamps = 0;

        if (libraryRelevant)
        {
            var tree = library.Tree;
            var albums = library.Albums;
            var artists = library.Artists;
            var shows = library.Shows;
            var addedAt = library.AddedAt;

            // navigation recency. F.7.6's exact join: the route key IS the entry id, so this is an identity lookup.
            _visits.Clear();
            var history = _history;
            if (history is not null)
            {
                var log = history.Entries;
                for (int i = 0; i < log.Count; i++)
                    _visits.Add(new SidebarVisit(log[i].Route.Name, log[i].VisitedAt.ToUniversalTime().Ticks));
            }
            var recency = history is null
                ? SidebarRecency.Empty
                : SidebarRecency.Build(history.Entries, static e => e.Route.Name,
                                       static e => e.VisitedAt.ToUniversalTime().Ticks);

            // the first-observation map, seeded ONCE from the persisted document.
            var firstSeen = _firstSeen ??= LoadFirstSeen(prefs.FirstSeen);

            // THE projection. Fully flattened (folders AND all their children) because this list is the planner's
            // `Library` slice, the feeds' join index and the pin resolver: hiding a collapsed folder's playlists here
            // would hide them from an EntityList section too. Folder COLLAPSE is a V3-list concern, handled below.
            var lastPlayed = _playLog?.Recency;
            var full = SidebarProjection.Build(_all, SidebarEntryKindMask.All, tree, albums, artists, shows, addedAt,
                                               recency, firstSeen, includeFolderChildren: true, lastPlayed: lastPlayed);

            // the tree slice the planner's PlaylistTree section walks: depth-stamped, folders carried as Folder rows.
            SidebarProjection.Build(_tree, SidebarEntryKindMask.PlaylistTree, tree, Array.Empty<Album>(),
                                    Array.Empty<Artist>(), Array.Empty<Show>(), addedAt, recency, firstSeen,
                                    includeFolderChildren: true, lastPlayed: lastPlayed);

            _index.Rebuild(_all);

            // the PUBLISHED entry list (V3 / Classic read it). Built with its own kind mask and folder-collapse rule,
            // then filtered → sorted → pins-first by the pure pipeline.
            bool searching = search.Length > 0;
            qualifiers = SidebarProjection.QualifiersAvailable(full.FlavorMask);

            var buffer = prefs.Entries.Buffer;
            var v3Result = SidebarProjection.Build(buffer, SidebarEntryKinds.From(filter), tree, albums, artists, shows,
                                                   addedAt, recency, firstSeen,
                                                   // Searching FLATTENS the tree; otherwise a folder is opaque until expanded.
                                                   includeFolderChildren: searching,
                                                   isFolderExpanded: searching ? null : _isFolderExpanded,
                                                   lastPlayed: lastPlayed);

            ResolvePins(prefs);
            // A pin the library does not contain (the Liked Songs route pin, an editorial playlist never saved, a pinned
            // page) is absent from `buffer`, and Shape's PinsFirst cannot move what is absent — so V3 dropped exactly the
            // pins Classic's band still drew. Append the ones this lens admits before shaping; they join the band like
            // any other pin and answer the search like any other row.
            SidebarBinderPipeline.AppendUnlistedPins(buffer, _unlistedPinRows, SidebarEntryKinds.From(filter));
            var query = new SidebarV3Query(filter, qualifier, sort, desc, search, qualifiers);
            shape = SidebarBinderPipeline.Shape(buffer, _scratch, in query, prefs.Pins.Items,
                                                    prefs.CanReorderV3 ? prefs.V3CustomOrder : null);

            newStamps = full.NewFirstSeenStamps + v3Result.NewFirstSeenStamps;
        }
        else if (pinsChanged)
        {
            // The library projection, the tree, the index and the V3 buffer's FILTERED+SORTED content are all
            // untouched (none of the library-relevant lanes moved) — only re-resolve pins against the existing
            // `_index` and re-shape the existing buffer. Shape's filter/sort passes are idempotent re-runs over an
            // already-filtered-and-sorted list; only the pins-first partition actually needed this pass.
            qualifiers = _lastQualifiers;
            var buffer = prefs.Entries.Buffer;
            ResolvePins(prefs);
            SidebarBinderPipeline.AppendUnlistedPins(buffer, _unlistedPinRows, SidebarEntryKinds.From(filter));
            var query = new SidebarV3Query(filter, qualifier, sort, desc, search, qualifiers);
            shape = SidebarBinderPipeline.Shape(buffer, _scratch, in query, prefs.Pins.Items,
                                                    prefs.CanReorderV3 ? prefs.V3CustomOrder : null);
        }
        else
        {
            // Neither the library nor pins moved (a feed/source-health, layout or playback-only trigger) — reuse the
            // last published shape outright; the feed/extension/publish steps below still run unconditionally, which
            // is what makes this kind of rebuild visible at all.
            shape = _lastShape;
            qualifiers = _lastQualifiers;
        }
        _lastShape = shape;
        _lastQualifiers = qualifiers;
        _hasRebuiltOnce = true;

        // 7 — the recency + playback feeds the planner's JumpBackIn / feed sections consume.
        _visited.Clear();
        SidebarSourceMap.Visited(_visits, static v => v.RouteKey, static v => v.TicksUtc, _index, _visited, RecencyCap);
        RefreshPlayedContexts();
        _played.Clear();
        SidebarSourceMap.Played(_playedContexts, _index, _played, RecencyCap);

        // 8a — the two BUILT-IN feed kinds (NewReleases / Concerts) are served by the very same registered sources as
        //      their Extension-section form, so there is exactly one fetch, one cache and one health verdict per feed —
        //      never a second parallel input that can disagree with the contribution path.
        foreach (var source in _demandSources) source.BeginDemandPass();
        try
        {
            FillFeed(SidebarContributions.NewReleases, _newReleases, 8);
            FillFeed(SidebarContributions.Concerts, _concerts, 8);

            // 8b — contributions. Resolution happens AFTER the projection, so a source reading the snapshot sees this pass.
            SidebarBinderPipeline.ResolveExtensions(prefs.Layout, _host, _extEntries, _slices, _cache, search);
            ObserveExtensionSources(prefs.Layout);
        }
        finally { foreach (var source in _demandSources) source.EndDemandPass(); }

        // 9 — publish the cell. ONE version bump per rebuild, never per entry. `_all` is also passed as the FULL
        //     projection gate: it is the planner's `Library` slice and, content-wise, a superset of `_tree` (both
        //     project the same `tree` with `includeFolderChildren: true`), so comparing it alone covers both of the
        //     planner's inputs — a change invisible to the published (collapsed-folder-filtered) rows still trips it.
        bool anyPending = AnyContributingKindPending(filter);
        var (state, error) = PublishState(filter, shape.Count, anyPending);
        prefs.Entries.Publish(state, error, anyPending, qualifiers, shape.PinCount, _all);

        // 10 — commit point #9: persist the document only when this pass actually observed something new. `newStamps`
        //      is 0 whenever the library-relevant stage above did not run — nothing new could have been observed.
        if (newStamps > 0) CommitFirstSeen(prefs, _firstSeen!);

        // 11 — the planner input. Revision is the caller's composite epoch, echoed into every plan.
        _revision++;
        _input = new SidebarProjectionInput(
            Library: _all,
            PlaylistTree: _tree,
            Pins: _pinRows,
            Visited: _visited,
            Played: _played,
            NewReleases: _newReleases,
            Concerts: _concerts,
            ByUri: _index.AsLookup(),
            PinnedIds: _pinnedIds,
            ExpandedFolders: prefs.ExpandedFolders,
            Search: search,
            LibraryState: _libraryState,
            TreeState: _treeState,
            RecentsState: SidebarSourceState.Ready,
            NewReleasesState: StateOf(SidebarContributions.NewReleases),
            ConcertsState: StateOf(SidebarContributions.Concerts),
            ConcertsLocationUnset: NeedsPrompt(SidebarContributions.Concerts),
            Revision: _revision,
            ExtensionEntries: _extEntries,
            ExtensionSlices: _slices);
    }

    // Pins resolve against the projection; an UNRESOLVED pin still renders from its own display cache (F.5.4's
    // offline-first contract) instead of disappearing, and a resolved one refreshes that cache (commit point #2 —
    // TouchPin never commits on its own).
    void ResolvePins(SidebarPreferences prefs)
    {
        _pinRows.Clear();
        _unlistedPinRows.Clear();
        _pinnedIds.Clear();
        var pins = prefs.Pins.Items;
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.Id.Length == 0) continue;
            _pinnedIds.Add(pin.Id);
            if (_index.TryGet(pin.Id, out var entry))
            {
                // entry.Name can still be empty here — the library's mosaic cover/child-count resolve off the
                // playlist's REPLICA (unconditional), independent of the PlaylistHeader facet that carries the name
                // (see SidebarBinderPipeline.ResolveListedPin) — so fall back to the pin's own display cache instead
                // of rendering a name-less row while the header is in flight.
                _pinRows.Add(SidebarBinderPipeline.ResolveListedPin(pin, i, entry));
                if (entry.Name.Length > 0) prefs.TouchPin(pin.Id, entry.Name);
                continue;
            }

            var resolved = _pinQueries.TryGetValue(pin.Id, out var query) ? query.Read() : null;
            var unlisted = SidebarBinderPipeline.ResolveUnlistedPin(pin, i, resolved);
            _pinRows.Add(unlisted);
            _unlistedPinRows.Add(unlisted);
            if (resolved is { Name.Length: > 0 } latest) prefs.TouchPin(pin.Id, latest.Name);
        }
    }

    // Demand is updated from the pump effect, separately from the pure projection. An unresolved pin keeps an
    // observable header query, including after failures; canonical freshness/backoff owns retry eligibility.
    void SyncPinQueries()
    {
        if (!_started || _post is null) return;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in _prefs.Pins.Items)
        {
            if (pin.Uri.Length == 0 || _index.TryGet(pin.Id, out _)) continue;
            live.Add(pin.Id);
            if (_pinQueries.TryGetValue(pin.Id, out var prior))
            {
                if (prior.Uri == pin.Uri) continue;
                prior.Binding.Dispose();
                _pinQueries.Remove(pin.Id);
            }
            PinQuery? query = pin.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Album or SidebarEntryKind.Show or SidebarEntryKind.Artist
                ? Watch(new EntityCardQuery(_scope, pin.Uri), pin.Uri,
                    card => SidebarBinderPipeline.PinEntry(pin.Kind, card.Title, card.Image, card.ChildCount ?? 0, card.Subtitle ?? "")) : null;
            if (query is not null) _pinQueries.Add(pin.Id, query);
        }
        var removed = new List<string>();
        foreach (var pair in _pinQueries) if (!live.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (var id in removed) { _pinQueries[id].Binding.Dispose(); _pinQueries.Remove(id); }
        ApplyPinDemand();
    }

    /// <summary>Kept for the pane's realized-window effect (<c>SidebarPane.ApplyPinDemand</c>-side bookkeeping), but no
    /// longer decides pin demand (see <see cref="ApplyPinDemand"/>) — a pin's header must resolve whether or not its
    /// row happens to be scrolled into view (problem 2: an unlisted pinned entity below the fold sat at
    /// <c>QueryDemand.None</c> forever, because "realized" for it was never true).</summary>
    internal void SetPinDemand(object owner, IReadOnlySet<string>? subjects)
    {
        if (subjects is null || subjects.Count == 0)
        {
            if (!_pinDemand.Remove(owner)) return;
        }
        else
        {
            if (_pinDemand.TryGetValue(owner, out var prior) && prior.SetEquals(subjects)) return;
            _pinDemand[owner] = new(subjects, StringComparer.Ordinal);
        }
        ApplyPinDemand();
    }

    /// <summary>W8/Required-B — pins are a HANDFUL of rows (unlike the library/rootlist projection, which is a
    /// virtualized window over potentially thousands), so gating a pin's header query on the realized scroll window
    /// is not the cost/benefit trade it is for the big lists: it bought nothing (the fetch is cheap and bounded by
    /// the pin count) and cost a pinned-but-unlisted row's title a permanent skeleton whenever the row was not the
    /// one currently on screen. Every pin binding therefore demands <c>Visible</c> unconditionally the moment it
    /// exists (see <see cref="Watch{T}"/>); this method now only re-asserts that invariant after add/remove.</summary>
    void ApplyPinDemand()
    {
        foreach (var pin in _pinQueries.Values)
            pin.Binding.SetDemand(QueryDemand.Initial);
    }

    PinQuery Watch<T>(QuerySpec<T> specification, string uri, Func<T, SidebarLibraryEntry?> map)
    {
        var binding = new QuerySignalBinding<T>(_queries.Acquire(specification), _post!, _ => OnSourceChanged());
        // Visible from the start (Required-B) — never QueryDemand.None waiting for a window effect to reach it.
        binding.SetDemand(QueryDemand.Initial);
        binding.SetActive(_active);
        return new(uri, binding, () => map(binding.Snapshot.Peek().Value));
    }

    // RecentContexts allocates (a list + a dedupe set), so it is recomputed only when the log actually moved. This is also
    // the ONE place PlayLogStore's vocabulary is translated into the engine-free row shape the mappers work on.
    void RefreshPlayedContexts()
    {
        var log = _playLog;
        if (log is null) { _playedContexts.Clear(); return; }
        int rev = log.Revision;
        if (rev == _playedRevision && _playedContexts.Count > 0) return;
        _playedRevision = rev;
        _playedContexts.Clear();
        var rows = log.RecentContexts(RecencyCap);
        for (int i = 0; i < rows.Count; i++)
            _playedContexts.Add(new SidebarPlayedContext(rows[i].Uri, KindOfContext(rows[i].Kind), rows[i].PlayedAtMs));
    }

    static SidebarEntryKind KindOfContext(PlayContextKind kind) => kind switch
    {
        PlayContextKind.Album => SidebarEntryKind.Album,
        PlayContextKind.Playlist => SidebarEntryKind.Playlist,
        PlayContextKind.Artist => SidebarEntryKind.Artist,
        PlayContextKind.Show => SidebarEntryKind.Show,
        // A bare track play (no context) renders as a track row; spotify:collection:tracks becomes the "liked" ROUTE id,
        // which SidebarPinId.FromUri already knows how to produce.
        PlayContextKind.None => SidebarEntryKind.Track,
        _ => SidebarEntryKind.AppRoute,
    };

    bool NeedsPrompt(string sourceId)
    {
        if (_host is null) return false;
        var source = _host.Resolve(sourceId, out _);
        return source?.NeedsPrompt ?? false;
    }

    /// <summary>Refresh a built-in feed's slice from its registered source. Never throws: one bad feed may not take the
    /// sidebar down, so a throwing source contributes zero rows and keeps its own Error health.</summary>
    void FillFeed(string sourceId, List<SidebarLibraryEntry> into, int max)
    {
        into.Clear();
        var source = _host?.Resolve(sourceId, out _);
        if (source is null) return;
        var request = new SidebarSourceRequest(SidebarSourceConfig.Empty, max);
        try
        {
            source.EnsureFresh(request);
            source.Fill(into, request);
        }
        catch (Exception ex)
        {
            into.Clear();
            ObserveSourceState(sourceId, SidebarSourceState.Error, staleReplay: false, error: ex);
            return;
        }
        ObserveSourceState(sourceId, source.State, staleReplay: false);
    }

    void ObserveExtensionSources(SidebarCustomLayout layout)
    {
        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            ObserveExtensionSection(sections[i]);
            var children = sections[i].ChildList;
            for (int j = 0; j < children.Count; j++) ObserveExtensionSection(children[j]);
        }
    }

    void ObserveExtensionSection(SidebarSectionSpec section)
    {
        if (section.Kind != SidebarSectionKind.Extension || section.Extension is not { } xref) return;
        string sourceId = SidebarContributions.SourceId(xref.ExtensionId, xref.ContributionId);
        if (sourceId.Length == 0 || !_slices.TryGet(section.Id, out var slice)) return;
        bool stale = slice.Availability == SidebarContributionAvailability.Cached;
        ObserveSourceState(sourceId, stale ? SidebarSourceState.Error : slice.State, stale);
    }

    // Edge-triggered diagnostics: a failed source is noisy only once, recovery is explicit, and serving a last-good
    // snapshot is observable without logging row contents, search text, config, or any other user data.
    void ObserveSourceState(string sourceId, SidebarSourceState next, bool staleReplay, Exception? error = null)
    {
        bool hadPrevious = _observedSourceStates.TryGetValue(sourceId, out var previous);
        if (!hadPrevious || previous != next)
        {
            _observedSourceStates[sourceId] = next;
            if (next == SidebarSourceState.Error)
            {
                WaveeLog.Instance.Event(WaveeLogLevel.Warning, "sidebar", "sidebar.source.failed",
                    "A sidebar data source failed.", ex: error,
                    fields: [WaveeLogField.Of("source_id", sourceId)]);
            }
            else if (hadPrevious && previous == SidebarSourceState.Error)
            {
                WaveeLog.Instance.Info("sidebar", "sidebar.source.recovered",
                    "A sidebar data source recovered.",
                    WaveeLogField.Of("source_id", sourceId),
                    WaveeLogField.Of("state", next.ToString()));
            }
        }

        if (staleReplay)
        {
            if (_staleSourceIds.Add(sourceId))
                WaveeLog.Instance.Warn("sidebar", "sidebar.source.stale_replayed",
                    "A last-good sidebar source snapshot was replayed.",
                    WaveeLogField.Of("source_id", sourceId));
        }
        else
        {
            _staleSourceIds.Remove(sourceId);
        }
    }

    bool AnyContributingKindPending(SidebarV3Filter filter)
        => _libraryQuery?.Snapshot.Peek().Status.HasPrimaryData != true;

    (LoadState State, Exception? Error) PublishState(SidebarV3Filter filter, int count, bool anyPending)
    {
        if (count > 0 || !anyPending) return (LoadState.Ready, null);
        var snapshot = _libraryQuery?.Snapshot.Peek();
        if (snapshot is { Status.IsRefreshing: false, Problems.Count: > 0 })
            return (LoadState.Failed, new InvalidOperationException(snapshot.Problems[0].Error?.Message
                ?? "The library is not available."));
        return (LoadState.Pending, null);
    }

    static SidebarFirstSeen LoadFirstSeen(SidebarFirstSeenDto[]? stored)
    {
        var seen = new SidebarFirstSeen();
        if (stored is null || stored.Length == 0) return seen;
        var pairs = new List<KeyValuePair<string, long>>(stored.Length);
        for (int i = 0; i < stored.Length; i++) pairs.Add(new KeyValuePair<string, long>(stored[i].Id, stored[i].Ms));
        seen.Load(pairs);
        return seen;
    }

    void CommitFirstSeen(SidebarPreferences prefs, SidebarFirstSeen seen)
    {
        // Pruning is O(stamps × live). Only worth paying once the map is genuinely large — an unpruned entry costs a few
        // bytes and its own eviction is already bounded by SidebarFirstSeen.Cap.
        if (seen.Count > SidebarFirstSeen.Cap / 2)
        {
            _liveIds.Clear();
            SidebarProjection.CollectIds(_all, _liveIds);
            seen.PruneTo(_liveIds);
        }

        var pairs = new List<KeyValuePair<string, long>>(seen.Count);
        seen.CopyTo(pairs);
        var dtos = new SidebarFirstSeenDto[pairs.Count];
        for (int i = 0; i < pairs.Count; i++) dtos[i] = new SidebarFirstSeenDto(pairs[i].Key, pairs[i].Value);
        seen.ResetNewCount();
        prefs.PublishFirstSeen(dtos);
    }

    // ─────────────────────────────────── triggers ───────────────────────────────────

    /// <summary>Read every trigger AND SUBSCRIBE the calling computation — the pump's render calls this, nothing else.
    /// Returns the fold the pump keys its effect on.
    ///
    /// <para><paramref name="history"/> is the pump's CONTEXT history store, which it has one render EARLIER than the
    /// binder does (the attach happens in the pump's effect). Subscribing through it means the very first render already
    /// depends on <c>HistoryStore.Version</c> — otherwise a navigation would never re-render the pump and the recents feed
    /// would sit stale until some other trigger moved.</para>
    ///
    /// <para><paramref name="search"/> is the pump's debounced <c>V3Search</c> signal, one render EARLIER than
    /// <see cref="_effectiveSearch"/> (the attach also happens in the pump's effect) — exactly the <paramref
    /// name="history"/> pattern, so the very first render's fold already reads the debounced text instead of every
    /// keystroke.</para></summary>
    internal long SubscribeAndFold(HistoryStore? history = null, IReadSignal<string>? search = null)
    {
        var triggers = Read(subscribe: true, history ?? _history, search);
#if DEBUG || FLUENTGPU_DIAG
        if (s_binderDiag) ObservePump(triggers);
#endif
        return triggers.Fold();
    }

    SidebarBinderTriggers Read(bool subscribe) => Read(subscribe, _history, null);

    SidebarBinderTriggers Read(bool subscribe, HistoryStore? history, IReadSignal<string>? search = null)
    {
        var prefs = _prefs;
        int libraryEpoch = Epoch(subscribe);
        long playback = PlaybackEpoch(subscribe);
        // The debounced signal the pump attached (or was just handed for this very call, pre-attach) — never the raw
        // prefs.V3Search directly once a debounced signal exists, so this trigger's SearchHash and Rebuild's own peek
        // of the same field can never disagree about what text a rebuild is for.
        var effSearch = search ?? _effectiveSearch;
        string? searchText = effSearch is not null
            ? (subscribe ? effSearch.Value : effSearch.Peek())
            : (subscribe ? prefs.V3Search.Value : prefs.V3Search.Peek());

        return new SidebarBinderTriggers(
            LibraryEpoch: libraryEpoch,
            PinsVersion: Ver(prefs.PinsVersion, subscribe),
            HistoryVersion: history is null ? 0 : Ver(history.Version, subscribe),
            // Also re-SORTS: PlayLogStore.Version bumps on Append AND MergeRecency, and SidebarProjection.Build now
            // stamps LastPlayedMs from PlayLogStore.Recency, so a play here or synced from another device both
            // rebuilds AND re-sorts "Recents" — a navigation (HistoryVersion, above) still rebuilds the VISITED feed
            // but no longer moves the played-based sort.
            PlayLogRevision: _playLog is null ? 0 : Ver(_playLog.Version, subscribe),
            LayoutVersion: Ver(prefs.LayoutVersion, subscribe),
            FolderVersion: Ver(prefs.FolderVersion, subscribe),
            OrderVersion: Ver(prefs.V3OrderVersion, subscribe),
            CultureEpoch: Ver(Localization.CultureEpoch, subscribe),
            V3State: SidebarBinderTriggers.PackV3(
                (int)(subscribe ? prefs.Design.Value : prefs.Design.Peek()),
                Ver(prefs.V3Filter, subscribe), Ver(prefs.V3Qualifier, subscribe), Ver(prefs.V3Sort, subscribe),
                subscribe ? prefs.V3Desc.Value : prefs.V3Desc.Peek()),
            SearchHash: searchText?.GetHashCode(StringComparison.Ordinal) ?? 0,
            SourceEpoch: Ver(_sourceEpoch, subscribe),
            PlaybackEpoch: playback);
    }

    // The decision (why VALUE identity, not Revision, is the right fold) lives in the pure, unit-tested
    // SidebarBinderTriggers.LibraryEpochOf — LibraryDefinition.Read (CatalogQueryDefinitions.cs) always allocates a
    // fresh LibraryQuerySnapshot when it actually re-projects, so object identity is a safe "changed" signal there.
    int Epoch(bool subscribe)
    {
        var snapshot = _libraryQuery is not { } query ? null
            : subscribe ? query.Snapshot.Value : query.Snapshot.Peek();
        return SidebarBinderTriggers.LibraryEpochOf(snapshot);
    }

    long PlaybackEpoch(bool subscribe)
    {
        if (_playback is null) return 0;
        long queue = subscribe ? _playback.QueueRevision.Value : _playback.QueueRevision.Peek();
        var track = subscribe ? _playback.CurrentTrack.Value : _playback.CurrentTrack.Peek();
        int uri = track?.Uri.GetHashCode(StringComparison.Ordinal) ?? 0;
        return unchecked((queue << 20) ^ uri);
    }

    static int Ver(IReadSignal<int> signal, bool subscribe) => subscribe ? signal.Value : signal.Peek();

#if DEBUG || FLUENTGPU_DIAG
    void ObservePump(in SidebarBinderTriggers triggers)
    {
        _diagPumpRenders++;
        if (!_diagHaveTriggers)
        {
            _diagHaveTriggers = true;
            _diagLastTriggers = triggers;
        }
        else if (triggers != _diagLastTriggers)
        {
            _diagLastChange = DescribeTriggerDiff(_diagLastTriggers, triggers);
            _diagLastTriggers = triggers;
            _diagTriggerChanges++;
        }

        long now = Environment.TickCount64;
        if (_diagLastReportMs != 0 && now - _diagLastReportMs < 1000) return;
        _diagLastReportMs = now;
        WaveeLog.Instance.Event(WaveeLogLevel.Debug, "sidebar", "sidebar.binder.diag",
            "[sidebarbinder] pump=" + _diagPumpRenders
            + " triggerChanges=" + _diagTriggerChanges
            + " rebuilds=" + _diagRebuilds
            + " sourceNotifications=" + _diagSourceNotifications
            + " sources=" + DescribeSourceNotifications()
            + " last=" + _diagLastChange);
        _diagPumpRenders = 0;
        _diagTriggerChanges = 0;
        _diagRebuilds = 0;
        _diagSourceNotifications = 0;
        _diagSourceCounts?.Clear();
    }

    string DescribeSourceNotifications()
    {
        var counts = _diagSourceCounts;
        if (counts is null || counts.Count == 0) return "none";
        var sb = new StringBuilder();
        foreach (var pair in counts)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(pair.Key).Append('=').Append(pair.Value);
        }
        return sb.ToString();
    }

    static string DescribeTriggerDiff(in SidebarBinderTriggers before, in SidebarBinderTriggers after)
    {
        var sb = new StringBuilder();
        AppendDiff(sb, "library", before.LibraryEpoch, after.LibraryEpoch);
        AppendDiff(sb, "pins", before.PinsVersion, after.PinsVersion);
        AppendDiff(sb, "history", before.HistoryVersion, after.HistoryVersion);
        AppendDiff(sb, "playLog", before.PlayLogRevision, after.PlayLogRevision);
        AppendDiff(sb, "layout", before.LayoutVersion, after.LayoutVersion);
        AppendDiff(sb, "folders", before.FolderVersion, after.FolderVersion);
        AppendDiff(sb, "order", before.OrderVersion, after.OrderVersion);
        AppendDiff(sb, "culture", before.CultureEpoch, after.CultureEpoch);
        AppendDiff(sb, "v3", before.V3State, after.V3State);
        AppendDiff(sb, "search", before.SearchHash, after.SearchHash);
        AppendDiff(sb, "source", before.SourceEpoch, after.SourceEpoch);
        AppendDiff(sb, "playback", before.PlaybackEpoch, after.PlaybackEpoch);
        return sb.Length == 0 ? "same" : sb.ToString();
    }

    static void AppendDiff(StringBuilder sb, string name, long before, long after)
    {
        if (before == after) return;
        if (sb.Length > 0) sb.Append(',');
        sb.Append(name).Append('=').Append(before).Append("->").Append(after);
    }
#endif

    // ─────────────────────────────────── the pump ───────────────────────────────────

    /// <summary>Zero-size, always-mounted. Its ONLY job is to be the computation that subscribes to the binder's triggers
    /// (a plain service cannot) and to run <see cref="Sync"/> AFTER the frame — in a <c>UseEffect</c>, never in render, so
    /// a rebuild can never write a signal mid-render.</summary>
    sealed class SidebarBinderPump : Component
    {
        // WHY 90 ms: a keystroke used to run the whole 11-step Rebuild synchronously — every character typed into V3
        // search re-walked the entire library. 90 ms is under the perceptual threshold (it does not read as lag) and
        // lets a burst of typing coalesce into one rebuild instead of one per keystroke.
        const float SearchDebounceMs = 90f;

        readonly SidebarProjectionBinder _binder;
        public SidebarBinderPump(SidebarProjectionBinder binder) => _binder = binder;

        public override Element Render()
        {
            // The navigation log is created by WaveeShell and provided as context, so the pump — not Services — is where
            // the binder gets it. Keyed on the INSTANCE: mount this below the shell's provide and the visited feed lights
            // up the moment the provide resolves; mount it above and everything else still works, visited stays empty.
            var history = UseContext(HistoryStore.Slot);
            // The debounced query text. Threaded into SubscribeAndFold (below) so the very FIRST render's trigger fold
            // already reads this signal instead of prefs.V3Search directly — mirrors how `history` is handed in one
            // render before AttachHistory runs.
            var search = UseDebouncedValue(_binder._prefs.V3Search, SearchDebounceMs);
            long fold = _binder.SubscribeAndFold(history, search);   // reads = subscriptions; no work, no writes
            var post = UsePost();
            UseEffect(() =>
            {
                _binder.AttachHistory(history);
                _binder.AttachSearch(search);
                _binder.Start(post);
            }, DepKey.FromRef(history));
            UseEffect(_binder._syncAction, DepKey.From(fold));
            UseActivation(onActivated: () => _binder.SetActive(true), onDeactivated: () => _binder.SetActive(false));
            UseEffect(() => (Action?)_binder.Stop, DepKey.Empty);
            return new BoxEl { HitTestVisible = false, Shrink = 0f };
        }
    }
}
