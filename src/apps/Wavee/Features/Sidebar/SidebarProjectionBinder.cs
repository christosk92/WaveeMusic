using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
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
    readonly LibraryStore _library;
    readonly PlayLogStore? _playLog;
    readonly PlaybackBridge? _playback;
    readonly IMusicLibrary? _catalog;

    // Rebuild buffers — allocated once, reused forever (the F.7.5 allocation contract).
    readonly List<SidebarLibraryEntry> _all = new(256);       // the full projection, source order (planner Library)
    readonly List<SidebarLibraryEntry> _tree = new(128);      // the flattened rootlist tree (planner PlaylistTree)
    readonly List<SidebarLibraryEntry> _pinRows = new(16);    // resolved pins, in pin order
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
    // A pin the live projection does not know (an editorial/Spotify-owned playlist, or any other entity never saved to
    // the user's own library/rootlist) is hydrated through the SAME façade the detail page uses (Services.Library),
    // rather than rendering a permanent blank cover + "0 songs". Keyed by pin id; populated on the UI thread once a
    // fetch lands (see RequestPinHydration/HydratePinAsync), so ResolvePins can overlay it onto the pin's own offline
    // display cache. _pinHydrationInFlight is "asked at least once" for the whole session: a HIT clears the pin's id
    // (nothing left to dedupe once it is cached), but a MISS leaves the id in place — ResolvePins runs on every
    // rebuild, so retrying a permanently-gone/unreachable pin there would hit the network once per rebuild forever.
    readonly Dictionary<string, SidebarLibraryEntry> _pinHydration = new(StringComparer.Ordinal);
    readonly HashSet<string> _pinHydrationInFlight = new(StringComparer.Ordinal);
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
    SidebarProjectionInput _input;
    SidebarBinderTriggers _lastTriggers;
    SidebarSourceState _libraryState = SidebarSourceState.Pending;
    SidebarSourceState _treeState = SidebarSourceState.Pending;
    int _playedRevision = -1;
    int _revision;
    bool _started;
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

    public SidebarProjectionBinder(SidebarPreferences prefs, LibraryStore library,
                                  PlayLogStore? playLog = null, PlaybackBridge? playback = null,
                                  IMusicLibrary? catalog = null)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _playLog = playLog;
        _playback = playback;
        _catalog = catalog;
        // Cached delegates: a rebuild must not allocate a closure per pass.
        _isFolderExpanded = prefs.IsFolderExpanded;
        _syncAction = () => Sync();
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

    /// <summary>Idempotent start: capture the UI-thread marshaller, warm the cheap library cells, hand the marshaller to
    /// every source that owns async work, subscribe their Changed, and do the first rebuild.</summary>
    public void Start(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
        if (_started) { Invalidate(); Sync(); return; }
        _started = true;

        // The tree + added-at cells are cheap local reads, so a V3/Curated sidebar paints from warm cells on its first
        // frame exactly as Classic does (LibraryStore.WarmCheap's own contract).
        _library.WarmCheap();
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
        var triggers = Read(subscribe: false);
        if (!_dirty && triggers == _lastTriggers) return false;
        _lastTriggers = triggers;
        _dirty = false;

        _rebuilding = true;
        try
        {
#if DEBUG || FLUENTGPU_DIAG
            if (s_binderDiag) _diagRebuilds++;
#endif
            Rebuild();
        }
        finally { _rebuilding = false; }

        // A source that fired Changed mid-rebuild (an inline fetch completion) only marked us dirty; settle now.
        if (_dirty)
        {
            _dirty = false;
            _lastTriggers = Read(subscribe: false);
            _rebuilding = true;
            try
            {
#if DEBUG || FLUENTGPU_DIAG
                if (s_binderDiag) _diagRebuilds++;
#endif
                Rebuild();
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

    void Rebuild()
    {
        var prefs = _prefs;

        // 1 — the raw inputs, PEEKED (a service is not a computation; reading Value here would subscribe nothing and
        //     reading it during someone else's render would be a phantom dependency).
        var tree = _library.PlaylistTree.Value.Peek();
        var albums = _library.Albums.Value.Peek();
        var artists = _library.Artists.Value.Peek();
        var shows = _library.Shows.Value.Peek();
        var addedAt = _library.AddedAt.Value.Peek();
        _treeState = CellState(_library.PlaylistTree);
        _libraryState = Worst(CellState(_library.Albums), Worst(CellState(_library.Artists), CellState(_library.Shows)));

        // 2 — navigation recency. F.7.6's exact join: the route key IS the entry id, so this is an identity lookup.
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

        // 3 — the first-observation map, seeded ONCE from the persisted document.
        var firstSeen = _firstSeen ??= LoadFirstSeen(prefs.FirstSeen);

        // 4 — THE projection. Fully flattened (folders AND all their children) because this list is the planner's
        //     `Library` slice, the feeds' join index and the pin resolver: hiding a collapsed folder's playlists here
        //     would hide them from an EntityList section too. Folder COLLAPSE is a V3-list concern, handled in step 6.
        var lastPlayed = _playLog?.Recency;
        var full = SidebarProjection.Build(_all, SidebarEntryKindMask.All, tree, albums, artists, shows, addedAt,
                                           recency, firstSeen, includeFolderChildren: true, lastPlayed: lastPlayed);

        // 5 — the tree slice the planner's PlaylistTree section walks: depth-stamped, folders carried as Folder rows.
        SidebarProjection.Build(_tree, SidebarEntryKindMask.PlaylistTree, tree, Array.Empty<Album>(),
                                Array.Empty<Artist>(), Array.Empty<Show>(), addedAt, recency, firstSeen,
                                includeFolderChildren: true, lastPlayed: lastPlayed);

        _index.Rebuild(_all);

        // 6 — the PUBLISHED entry list (V3 / Classic read it). Built with its own kind mask and folder-collapse rule, then
        //     filtered → sorted → pins-first by the pure pipeline.
        bool v3 = prefs.Design.Peek() == SidebarDesign.LibraryV3;
        var filter = v3 ? (SidebarV3Filter)prefs.V3Filter.Peek() : SidebarV3Filter.All;
        var qualifier = v3 ? (SidebarV3Qualifier)prefs.V3Qualifier.Peek() : SidebarV3Qualifier.Any;
        var sort = (SidebarV3Sort)prefs.V3Sort.Peek();
        bool desc = prefs.V3Desc.Peek();
        // Rebuild has no render context, so this PEEKS the debounced signal the pump attached (AttachSearch) instead
        // of prefs.V3Search directly — the same text the trigger fold in Read(...) subscribed to, so a rebuild fired
        // by another trigger mid-typing can never run ahead of the debounced tick.
        string search = v3 ? SidebarSearch.Normalize((_effectiveSearch ?? (IReadSignal<string>)prefs.V3Search).Peek()) : "";
        bool searching = search.Length > 0;
        bool qualifiers = SidebarProjection.QualifiersAvailable(full.FlavorMask);

        var buffer = prefs.Entries.Buffer;
        var v3Result = SidebarProjection.Build(buffer, SidebarEntryKinds.From(filter), tree, albums, artists, shows,
                                               addedAt, recency, firstSeen,
                                               // Searching FLATTENS the tree; otherwise a folder is opaque until expanded.
                                               includeFolderChildren: searching,
                                               isFolderExpanded: searching ? null : _isFolderExpanded,
                                               lastPlayed: lastPlayed);

        ResolvePins(prefs);
        var query = new SidebarV3Query(filter, qualifier, sort, desc, search, qualifiers);
        var shape = SidebarBinderPipeline.Shape(buffer, _scratch, in query, prefs.Pins.Items,
                                                prefs.CanReorderV3 ? prefs.V3CustomOrder : null);

        // 7 — the recency + playback feeds the planner's JumpBackIn / feed sections consume.
        _visited.Clear();
        SidebarSourceMap.Visited(_visits, static v => v.RouteKey, static v => v.TicksUtc, _index, _visited, RecencyCap);
        RefreshPlayedContexts();
        _played.Clear();
        SidebarSourceMap.Played(_playedContexts, _index, _played, RecencyCap);

        // 8a — the two BUILT-IN feed kinds (NewReleases / Concerts) are served by the very same registered sources as
        //      their Extension-section form, so there is exactly one fetch, one cache and one health verdict per feed —
        //      never a second parallel input that can disagree with the contribution path.
        FillFeed(SidebarContributions.NewReleases, _newReleases, 8);
        FillFeed(SidebarContributions.Concerts, _concerts, 8);

        // 8b — contributions. Resolution happens AFTER the projection, so a source reading the snapshot sees this pass.
        SidebarBinderPipeline.ResolveExtensions(prefs.Layout, _host, _extEntries, _slices, _cache, search);
        ObserveExtensionSources(prefs.Layout);

        // 9 — publish the cell. ONE version bump per rebuild, never per entry. `_all` is also passed as the FULL
        //     projection gate: it is the planner's `Library` slice and, content-wise, a superset of `_tree` (both
        //     project the same `tree` with `includeFolderChildren: true`), so comparing it alone covers both of the
        //     planner's inputs — a change invisible to the published (collapsed-folder-filtered) rows still trips it.
        bool anyPending = AnyContributingKindPending(filter);
        var (state, error) = PublishState(filter, shape.Count, anyPending);
        prefs.Entries.Publish(state, error, anyPending, qualifiers, shape.PinCount, _all);

        // 10 — commit point #9: persist the document only when this pass actually observed something new.
        int newStamps = full.NewFirstSeenStamps + v3Result.NewFirstSeenStamps;
        if (newStamps > 0) CommitFirstSeen(prefs, firstSeen);

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
        _pinnedIds.Clear();
        var pins = prefs.Pins.Items;
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.Id.Length == 0) continue;
            _pinnedIds.Add(pin.Id);
            if (_index.TryGet(pin.Id, out var entry))
            {
                _pinRows.Add(entry with { IsPinned = true, SourceOrder = i });
                prefs.TouchPin(pin.Id, entry.Name);
                continue;
            }

            // Not in the user's own library/rootlist projection — an editorial/Spotify-owned playlist (or any other
            // entity never saved to the library) never appears in `_index` (it is built from LibraryStore's rootlist
            // tree + Albums/Artists/Shows only). Render the pin's own offline display cache immediately, overlaid with
            // whatever RequestPinHydration has already resolved through the SAME catalog façade the detail page uses —
            // and kick a hydration request the first time we see it unresolved.
            SidebarLibraryEntry? hydrated = _pinHydration.TryGetValue(pin.Id, out var h) ? h : null;
            _pinRows.Add(SidebarBinderPipeline.ResolveUnlistedPin(pin, i, hydrated));
            if (hydrated is null) RequestPinHydration(pin);
        }
    }

    // Fire-and-forget, deduped per pin id: ask Services.Library (the same façade DetailPage.LoadPlaylistAsync /
    // LoadAlbumAsync use) for cover + track count, then post the result back onto the UI thread and nudge the binder
    // dirty through the existing source-epoch signal — the same mechanism a contributed data source uses to say
    // "I have more now" (OnSourceChanged). No catalog wired (a headless/offline build) ⇒ the pin stays on its display
    // cache forever, which is the previous (correct) behaviour for those builds.
    void RequestPinHydration(SidebarPin pin)
    {
        if (_catalog is null || pin.Uri.Length == 0) return;
        if (!_pinHydrationInFlight.Add(pin.Id)) return;
        _ = HydratePinAsync(pin.Id, pin.Uri, pin.Kind);
    }

    async Task HydratePinAsync(string id, string uri, SidebarEntryKind kind)
    {
        SidebarLibraryEntry? resolved = null;
        try
        {
            resolved = kind switch
            {
                SidebarEntryKind.Playlist => await HydratePlaylistAsync(uri).ConfigureAwait(false),
                SidebarEntryKind.Album => await HydrateAlbumAsync(uri).ConfigureAwait(false),
                SidebarEntryKind.Show => await HydrateShowAsync(uri).ConfigureAwait(false),
                SidebarEntryKind.Artist => await HydrateArtistAsync(uri).ConfigureAwait(false),
                _ => null,   // AppRoute / Folder / Track — no catalog fetch backs any of these
            };
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Event(WaveeLogLevel.Warning, "sidebar", "sidebar.pin.hydrate_failed",
                "Hydrating an unlisted pin's art/count failed.", ex: ex, fields: [WaveeLogField.Of("pin_id", id)]);
        }

        _post?.Invoke(() =>
        {
            if (resolved is { } r)
            {
                // Success: drop the in-flight marker (nothing left to dedupe) and let the row pick up the cache entry
                // on the next ResolvePins pass.
                _pinHydrationInFlight.Remove(id);
                _pinHydration[id] = r;
                OnSourceChanged();   // marks dirty + bumps _sourceEpoch, exactly like a contributed source's Changed.
            }
            // A miss (not found / no cover / a transport failure) DELIBERATELY stays in _pinHydrationInFlight rather
            // than being retried next rebuild: ResolvePins runs on EVERY rebuild (a keystroke in search, a sort change,
            // an unrelated pin mutation), and a pin pointing at a permanently-gone/unreachable entity would otherwise
            // hit the network once per rebuild for the rest of the session. One attempt per pin per session is enough
            // — the row still renders from its own offline display cache either way, and a restart (or the pin
            // resolving into the library some other way) clears this set.
        });
    }

    async Task<SidebarLibraryEntry?> HydratePlaylistAsync(string uri)
    {
        var p = await _catalog!.GetPlaylistAsync(uri, HydrationLevel.Open).ConfigureAwait(false);
        if (p is null || (p.Cover is null && p.TrackCount == 0)) return null;
        return Hydrated(SidebarEntryKind.Playlist, p.Cover, p.TrackCount, p.OwnerName);
    }

    async Task<SidebarLibraryEntry?> HydrateAlbumAsync(string uri)
    {
        var a = await _catalog!.GetAlbumAsync(uri, HydrationLevel.Open).ConfigureAwait(false);
        if (a is null || (a.Cover is null && a.TrackCount == 0)) return null;
        return Hydrated(SidebarEntryKind.Album, a.Cover, a.TrackCount, JoinArtistNames(a.Artists));
    }

    async Task<SidebarLibraryEntry?> HydrateShowAsync(string uri)
    {
        var s = await _catalog!.GetShowAsync(uri, HydrationLevel.Open).ConfigureAwait(false);
        if (s is null || s.Cover is null) return null;
        return Hydrated(SidebarEntryKind.Show, s.Cover, 0, s.Publisher);
    }

    async Task<SidebarLibraryEntry?> HydrateArtistAsync(string uri)
    {
        var a = await _catalog!.GetArtistAsync(uri, HydrationLevel.Open).ConfigureAwait(false);
        if (a is null || a.Image is null) return null;
        return Hydrated(SidebarEntryKind.Artist, a.Image, 0, "");
    }

    // A minimal carrier for the fields ResolveUnlistedPin overlays (Cover/ChildCount/Creator) — everything else on this
    // instance is unused by the merge and never reaches a row.
    static SidebarLibraryEntry Hydrated(SidebarEntryKind kind, Image? cover, int childCount, string creator) =>
        new("", kind, "", "", creator, cover, null, ChildCount: childCount, AddedAtMs: 0, SortStamp: 0,
            LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static string JoinArtistNames(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return "";
        if (artists.Count == 1) return artists[0].Name;
        var sb = new StringBuilder(64);
        int n = Math.Min(artists.Count, 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(artists[i].Name);
        }
        if (artists.Count > n) sb.Append('…');
        return sb.ToString();
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

    // The skeleton gate is per CONTRIBUTING kind (a pending Shows load must not skeleton the Playlists filter).
    bool AnyContributingKindPending(SidebarV3Filter filter)
    {
        var kinds = SidebarEntryKinds.From(filter);
        if ((kinds & SidebarEntryKindMask.PlaylistTree) != 0
            && CellState(_library.PlaylistTree) == SidebarSourceState.Pending) return true;
        if ((kinds & SidebarEntryKindMask.Album) != 0
            && CellState(_library.Albums) == SidebarSourceState.Pending) return true;
        if ((kinds & SidebarEntryKindMask.Artist) != 0
            && CellState(_library.Artists) == SidebarSourceState.Pending) return true;
        if ((kinds & SidebarEntryKindMask.Show) != 0
            && CellState(_library.Shows) == SidebarSourceState.Pending) return true;
        return false;
    }

    (LoadState State, Exception? Error) PublishState(SidebarV3Filter filter, int count, bool anyPending)
    {
        var kinds = SidebarEntryKinds.From(filter);
        // A FAILED contributing cell is the only thing that makes the whole list failed; everything else degrades to a
        // (possibly empty) real list — the honest reading of "the library is what we could load".
        if ((kinds & SidebarEntryKindMask.PlaylistTree) != 0 && _library.PlaylistTree.IsFailed)
            return (LoadState.Failed, _library.PlaylistTree.Error);
        if ((kinds & SidebarEntryKindMask.Album) != 0 && _library.Albums.IsFailed)
            return (LoadState.Failed, _library.Albums.Error);
        if ((kinds & SidebarEntryKindMask.Artist) != 0 && _library.Artists.IsFailed)
            return (LoadState.Failed, _library.Artists.Error);
        if ((kinds & SidebarEntryKindMask.Show) != 0 && _library.Shows.IsFailed)
            return (LoadState.Failed, _library.Shows.Error);
        // Pending ONLY while there is genuinely nothing to show: a warm list that is refreshing must not flash a skeleton.
        return count == 0 && anyPending ? (LoadState.Pending, null) : (LoadState.Ready, null);
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

    static SidebarSourceState CellState<T>(Loadable<T> cell) => (LoadState)cell.State.Peek() switch
    {
        LoadState.Pending => SidebarSourceState.Pending,
        LoadState.Failed => SidebarSourceState.Error,
        _ => SidebarSourceState.Ready,
    };

    static SidebarSourceState Worst(SidebarSourceState a, SidebarSourceState b) => a > b ? a : b;

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

    // A LibraryStore cell publishes a NEW list instance on every fill/refresh, so instance identity is an exact content
    // epoch — a rename inside a same-length list moves it, which a Count-based epoch would miss. Reading the cell's Value
    // signal is what SUBSCRIBES the pump to it.
    int Epoch(bool subscribe)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Ref(_library.PlaylistTree, subscribe);
            h = h * 31 + Ref(_library.Albums, subscribe);
            h = h * 31 + Ref(_library.Artists, subscribe);
            h = h * 31 + Ref(_library.Shows, subscribe);
            h = h * 31 + Ref(_library.AddedAt, subscribe);
            h = h * 31 + (subscribe ? _library.PlaylistTree.State.Value : _library.PlaylistTree.State.Peek());
            h = h * 31 + (subscribe ? _library.Albums.State.Value : _library.Albums.State.Peek());
            h = h * 31 + (subscribe ? _library.Artists.State.Value : _library.Artists.State.Peek());
            h = h * 31 + (subscribe ? _library.Shows.State.Value : _library.Shows.State.Peek());
            return h;
        }
    }

    static int Ref<T>(Loadable<T> cell, bool subscribe)
    {
        var value = subscribe ? cell.Value.Value : cell.Value.Peek();
        return value is null ? 0 : RuntimeHelpers.GetHashCode(value);
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
            return new BoxEl { HitTestVisible = false, Shrink = 0f };
        }
    }
}
